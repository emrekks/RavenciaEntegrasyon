using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace MarketplaceHub.Infrastructure.Persistence;

public sealed class InvoicingJobProcessor(AppDbContext db, IInvoiceProviderPort provider, IInvoiceMarketplacePort marketplace, IPrivateFileStorage files, TimeProvider timeProvider, IConfiguration configuration, IDataProtectionProvider dataProtection) : IInvoicingJobProcessor
{
    private readonly IDataProtector partyProtector = dataProtection.CreateProtector("MarketplaceHub.InvoicePartySnapshot.v1");

    public async Task<JobExecutionResult> ProcessAsync(Guid tenantId, Guid? connectionId, string jobType, string payloadJson, string correlationId, CancellationToken cancellationToken)
    {
        if (jobType != InvoicingJobTypes.InvoiceDueScan && connectionId is null)
            return JobExecutionResult.Blocked("CONNECTION_REQUIRED", "Job requires a provider connection.");
        if (connectionId is Guid connection
            && jobType is not InvoicingJobTypes.ConnectionTest and not InvoicingJobTypes.StageCapabilityProbe
            && !await db.PlatformConnections.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.Id == connection && (x.Status == "ACTIVE" || x.Status == "VERIFIED"), cancellationToken))
            return JobExecutionResult.Blocked("CONNECTION_INACTIVE", "Bağlantı pasif veya gizli olduğu için işlem çalıştırılmadı.");
        try
        {
            var succeeded = jobType switch
            {
                InvoicingJobTypes.ConnectionTest => await TestConnection(tenantId, connectionId!.Value, correlationId, cancellationToken),
                InvoicingJobTypes.InvoiceSubmit => await Submit(tenantId, connectionId!.Value, payloadJson, correlationId, cancellationToken),
                InvoicingJobTypes.InvoiceReconcile => await Reconcile(tenantId, connectionId!.Value, payloadJson, correlationId, cancellationToken),
                InvoicingJobTypes.InvoiceDocumentFetch => await FetchDocument(tenantId, connectionId!.Value, payloadJson, correlationId, cancellationToken),
                InvoicingJobTypes.MarketplaceDelivery => await Deliver(tenantId, connectionId!.Value, payloadJson, correlationId, cancellationToken),
                InvoicingJobTypes.ManualOrderInvoiceDelivery => await DeliverManualOrderInvoiceDocument(tenantId, connectionId!.Value, payloadJson, correlationId, cancellationToken),
                InvoicingJobTypes.InvoiceCancellation => await Cancel(tenantId, connectionId!.Value, payloadJson, correlationId, cancellationToken),
                InvoicingJobTypes.InvoiceDueScan => await ScanDue(tenantId, cancellationToken),
                InvoicingJobTypes.StageCapabilityProbe => await StageCapabilityProbe(tenantId, connectionId!.Value, payloadJson, correlationId, cancellationToken),
                _ => false
            };
            return succeeded
                ? JobExecutionResult.Success()
                : JobExecutionResult.Blocked("F4_JOB_REJECTED", "Job payload, capability or invoice state did not permit the operation.");
        }
        catch (JobProcessingException exception)
        {
            return exception.Result;
        }
    }

    internal static string? PazaramaOrderId(string? customerSnapshotJson)
    {
        if (string.IsNullOrWhiteSpace(customerSnapshotJson)) return null;
        try
        {
            using var document = JsonDocument.Parse(customerSnapshotJson);
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("pazaramaOrderId", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() : null;
        }
        catch (JsonException) { return null; }
    }

    private async Task<bool> TestConnection(Guid tenantId, Guid connectionId, string correlationId, CancellationToken cancellationToken)
    {
        var connection = await db.PlatformConnections.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == connectionId && x.PlatformCode == "TRENDYOL_EFATURAM", cancellationToken);
        if (connection is null) return false;
        var now = timeProvider.GetUtcNow();
        connection.LastTestedAt = now;
        var result = await provider.TestConnectionAsync(Context(tenantId, connectionId, correlationId, "connection-test"), cancellationToken);
        connection.LastErrorCode = result.Error?.Code;
        if (result.IsSuccess)
        {
            connection.LastSuccessAt = now;
            // A passing test makes the provider ready to use. Creating an invoice stays a separate, confirmed action gated by the write switch.
            if (connection.Status is "DRAFT" or "VERIFIED") connection.Status = "ACTIVE";
            var capability = await db.PlatformCapabilities.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.Code == InvoicingCapabilities.ConnectionTest, cancellationToken);
            if (capability is not null)
            {
                capability.SupportLevel = CapabilitySupportLevel.Supported;
                capability.SourceUrl = "https://developers.trendyolefaturam.com/OpenApi/Auth/sign-in";
                capability.SourceVersion = "1.0.0";
                capability.EvidenceNote = "Resmî sign-in sözleşmesi ve yapılandırılmış test hesabıyla x-access-token kanıtı.";
                capability.VerifiedAt = now;
                capability.Version++;
            }
        }
        else
        {
            connection.Status = "DRAFT";
            connection.LastSuccessAt = null;
        }
        connection.Version++;
        await db.SaveChangesAsync(cancellationToken);
        if (!result.IsSuccess) throw JobProcessingException.FromAdapter(result.Error!);
        return true;
    }

    private async Task<bool> Submit(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, CancellationToken cancellationToken, bool isStageCapabilityProbe = false)
    {
        var invoice = await FindInvoice(tenantId, payloadJson, cancellationToken);
        if (invoice is null)
            throw new JobProcessingException(JobExecutionResult.Blocked("INVOICE_SUBMIT_INVOICE_NOT_FOUND", "Fatura gönderim isteği geçerli bir fatura kaydıyla eşleşmedi. Job payloadındaki fatura kimliğini kontrol edin."));
        if (invoice.ProviderConnectionId != connectionId)
            throw new JobProcessingException(JobExecutionResult.Blocked("INVOICE_SUBMIT_CONNECTION_MISMATCH", "Faturanın sağlayıcı bağlantısı job bağlantısıyla eşleşmiyor. Faturayı doğru e-Fatura bağlantısından yeniden kuyruğa alın."));
        if (invoice.Status != InvoiceStatus.Submitting)
            throw new JobProcessingException(JobExecutionResult.Blocked("INVOICE_SUBMIT_STATE_MISMATCH", $"Fatura gönderilemedi; beklenen durum Submitting, mevcut durum {invoice.Status}. Fatura durumunu kontrol edip güvenli yeniden deneme işlemini kullanın."));
        var previousAttempt = await db.InvoiceSubmissionAttempts.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.InvoiceId == invoice.Id)
            .OrderByDescending(x => x.AttemptNumber)
            .FirstOrDefaultAsync(cancellationToken);
        if (previousAttempt?.Outcome == "STARTED")
        {
            var uncertainNow = timeProvider.GetUtcNow();
            var staleAttempt = await db.InvoiceSubmissionAttempts.SingleAsync(x => x.TenantId == tenantId && x.Id == previousAttempt.Id, cancellationToken);
            staleAttempt.Outcome = "UNKNOWN";
            staleAttempt.ErrorCode = "INVOICE_SUBMISSION_OUTCOME_UNKNOWN";
            staleAttempt.CompletedAt = uncertainNow;
            invoice.Status = InvoiceStatus.UnknownResult;
            invoice.LastErrorCode = staleAttempt.ErrorCode;
            invoice.UpdatedAt = uncertainNow;
            invoice.Version++;
            await db.SaveChangesAsync(cancellationToken);
            throw new JobProcessingException(JobExecutionResult.ManualReview("INVOICE_SUBMISSION_OUTCOME_UNKNOWN", "Önceki mali gönderim yanıtı alınmadan kesildi. Sağlayıcıdan kesin yokluk doğrulanmadan yeni mali gönderim yapılmadı."));
        }
        var order = await db.Orders.AsNoTracking().SingleAsync(x => x.TenantId == tenantId && x.Id == invoice.OrderId, cancellationToken);
        var orderConnection = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == order.ConnectionId, cancellationToken);
        if (orderConnection is not null
            && ActiveIntegrationScope.IsMarketplace(orderConnection.PlatformCode)
            && !MarketplaceInvoiceCreationPolicy.IsEnabled(orderConnection.PlatformCode, orderConnection.SettingsJson))
        {
            invoice.LastErrorCode = MarketplaceInvoiceCreationPolicy.DisabledErrorCode;
            invoice.UpdatedAt = timeProvider.GetUtcNow();
            invoice.Version++;
            await db.SaveChangesAsync(cancellationToken);
            throw new JobProcessingException(JobExecutionResult.Blocked(MarketplaceInvoiceCreationPolicy.DisabledErrorCode, "Bu pazaryeri için fatura oluşturma izni kapalı. Bağlantı ayarlarında izin verilmeden fatura gönderilmedi."));
        }
        var lines = await db.InvoiceLines.AsNoTracking().Where(x => x.TenantId == tenantId && x.InvoiceId == invoice.Id).OrderBy(x => x.LineSequence).ToListAsync(cancellationToken);
        var package = invoice.PackageId is null ? null : await db.ShipmentPackages.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == invoice.PackageId, cancellationToken);
        var receiverSnapshot = await db.InvoicePartySnapshots.AsNoTracking()
            .SingleOrDefaultAsync(x => x.TenantId == tenantId && x.InvoiceId == invoice.Id && x.Role == "RECEIVER", cancellationToken);
        JsonDocument? frozenReceiver = null;
        try
        {
            if (receiverSnapshot is not null)
                frozenReceiver = JsonDocument.Parse(partyProtector.Unprotect(receiverSnapshot.ProtectedContent));
        }
        catch (Exception exception) when (exception is System.Security.Cryptography.CryptographicException or JsonException)
        {
            throw new JobProcessingException(JobExecutionResult.ManualReview("INVOICE_PARTY_SNAPSHOT_INVALID", "Fatura alıcı snapshot'ı okunamadı. Güncel sipariş bilgileriyle otomatik gönderim yapılmadı."));
        }
        using (frozenReceiver)
        {
            var snapshotRoot = frozenReceiver?.RootElement ?? default;
            var orderNumber = SnapshotText(snapshotRoot, "OrderNumber", order.OrderNumber);
            var orderedAt = SnapshotDate(snapshotRoot, "OrderedAt", order.OrderedAt);
            var customerSnapshotJson = SnapshotText(snapshotRoot, "CustomerSnapshotJson", order.CustomerSnapshotJson);
            var invoiceAddressSnapshotJson = SnapshotText(snapshotRoot, "InvoiceAddressSnapshotJson", order.InvoiceAddressSnapshotJson);
            var shipmentAddressSnapshotJson = SnapshotText(snapshotRoot, "ShipmentAddressSnapshotJson", order.ShipmentAddressSnapshotJson);
            var fiscalDeliveryPackage = InvoicingBillingService.ReadFiscalDeliveryPackage(snapshotRoot)
                ?? (package is null ? null : new InvoiceFiscalDeliveryPackageSnapshot(package.ExternalPackageId, package.CargoProviderExternalId, package.StatusOccurredAt));
            if (invoice.InvoiceType == "EARSIVFATURA"
                && !InvoicingBillingService.IsValidFiscalDeliveryPackage(fiscalDeliveryPackage))
            {
                await BlockFiscalSubmission(invoice, "INVOICE_DELIVERY_PACKAGE_SNAPSHOT_INVALID",
                    "E-Arşiv için doğrulanmış kargo firması ve gönderim tarihi bulunamadı. Fatura sağlayıcıya gönderilmedi.", cancellationToken);
            }
            if (orderConnection is null)
                await BlockFiscalSubmission(invoice, "MARKETPLACE_INVOICE_PRECHECK_FAILED", "Siparişin pazaryeri bağlantısı okunamadı; mali kesim başlatılmadı.", cancellationToken);

            if (ActiveIntegrationScope.IsMarketplace(orderConnection!.PlatformCode))
            {
                // Tracked: the precheck records the marketplace invoice state on these rows.
                var packages = await MarketplaceSalesService.CurrentShipmentPackages(db, db.ShipmentPackages).Where(item => item.TenantId == tenantId
                    && item.OrderId == order.Id && item.Status != ShipmentPackageStatus.Cancelled)
                    .OrderBy(item => item.Id).ToListAsync(cancellationToken);
                if (packages.Count == 0)
                    await BlockFiscalSubmission(invoice, "MARKETPLACE_INVOICE_PRECHECK_FAILED", "Sipariş için kontrol edilebilir etkin paket bulunamadı; mali kesim başlatılmadı.", cancellationToken);

                foreach (var targetPackage in packages)
                {
                    var platformRead = await marketplace.QueryDeliveryAsync(
                        Context(tenantId, targetPackage.ConnectionId, correlationId, $"invoice-precheck:{invoice.Id:N}:{targetPackage.Id:N}", IntegrationOperation.Automatic),
                        WithLookup(new(targetPackage.ExternalPackageId, order.OrderNumber, ShopifyOrderGid(order.ExternalOrderId)), order, targetPackage),
                        cancellationToken);
                    if (!platformRead.IsSuccess)
                        await BlockFiscalSubmission(invoice, "MARKETPLACE_INVOICE_PRECHECK_FAILED",
                            $"{orderConnection.PlatformCode} siparişindeki mevcut fatura güvenle okunamadı ({platformRead.Error!.Code}); mali kesim başlatılmadı.", cancellationToken);

                    var observation = platformRead.Value!;
                    if (MarketplaceInvoiceStatePolicy.IsUnverified(observation.RawStatus))
                    {
                        // n11 / PTT AVM: no API invoice says nothing about the seller panel, so the package keeps its state.
                        if (MarketplaceInvoiceStatePolicy.UnverifiedReadShowsInvoice(targetPackage.MarketplaceInvoiceStatus, targetPackage.ManualInvoiceStatus))
                            await BlockFiscalSubmission(invoice, "PLATFORM_INVOICE_ALREADY_PRESENT",
                                "Siparişte pazaryeri faturası var veya fatura yüklendi olarak işaretlenmiş. Yeni mali kesim yapılmadı.", cancellationToken);
                        continue;
                    }
                    var observedAt = timeProvider.GetUtcNow();
                    targetPackage.MarketplaceInvoiceRawStatus = observation.RawStatus;
                    targetPackage.MarketplaceInvoiceStatus = MarketplaceInvoiceStatePolicy.FromRemote(
                        observation.RawStatus, invoiceNumber: observation.VerifiedInvoiceNumber, invoiceUrl: observation.VerifiedInvoiceLink);
                    var explicitlyNotInvoiced = MarketplaceInvoiceStatePolicy.FromRemote(observation.RawStatus,
                        invoiceNumber: observation.VerifiedInvoiceNumber, invoiceUrl: observation.VerifiedInvoiceLink) == MarketplaceInvoiceStatus.NotInvoiced;
                    var references = explicitlyNotInvoiced
                        ? (InvoiceNumber: (string?)null, InvoiceUrl: (string?)null)
                        : MarketplaceInvoiceStatePolicy.MergeReferences(
                            targetPackage.MarketplaceInvoiceNumber,
                            targetPackage.MarketplaceInvoiceUrl,
                            observation.VerifiedInvoiceNumber,
                            observation.VerifiedInvoiceLink,
                            observation.ReferencesAreAuthoritative);
                    MarketplaceInvoiceReferenceHistoryRecorder.RecordPreviousReferences(
                        db, targetPackage, references.InvoiceNumber, references.InvoiceUrl, observedAt, "FISCAL_PRECHECK");
                    targetPackage.MarketplaceInvoiceNumber = references.InvoiceNumber;
                    targetPackage.MarketplaceInvoiceUrl = references.InvoiceUrl;
                    targetPackage.MarketplaceInvoiceObservedAt = observedAt;
                    targetPackage.UpdatedAt = observedAt;
                    targetPackage.Version++;

                    if (!explicitlyNotInvoiced)
                    {
                        await db.SaveChangesAsync(cancellationToken);
                        var present = targetPackage.MarketplaceInvoiceStatus is MarketplaceInvoiceStatus.Invoiced or MarketplaceInvoiceStatus.Received;
                        await BlockFiscalSubmission(invoice, present ? "PLATFORM_INVOICE_ALREADY_PRESENT" : "PLATFORM_INVOICE_STATUS_NOT_CLEAR",
                            present
                                ? "Pazaryerinde fatura bulundu veya platform faturanın var olduğunu bildiriyor. Yeni mali kesim yapılmadı."
                                : "Pazaryeri açık ve güvenilir bir 'fatura yok' durumu döndürmedi. Yeni mali kesim yapılmadı.", cancellationToken);
                    }
                }
                await db.SaveChangesAsync(cancellationToken);
            }
            // Stamped after the marketplace reads, right before the request: the provider prints this as the invoice date and time.
            StampIssueTime(invoice, timeProvider.GetUtcNow());
            var canonical = JsonSerializer.Serialize(new
            {
                invoice.Id,
                invoice.InvoiceType,
                invoice.Currency,
                invoice.PayableTotal,
                invoice.Note,
                invoice.IncludeInternetSalesInfo,
                IssuedAt = invoice.IssuedAt ?? invoice.UpdatedAt,
                MarketplacePlatformCode = orderConnection.PlatformCode,
                MarketplaceDisplayName = orderConnection.DisplayName,
                MarketplaceExternalStoreId = orderConnection.ExternalStoreId,
                MarketplaceEnvironment = orderConnection.Environment,
                Order = new { OrderNumber = orderNumber, OrderedAt = orderedAt, CustomerSnapshotJson = customerSnapshotJson, InvoiceAddressSnapshotJson = invoiceAddressSnapshotJson, ShipmentAddressSnapshotJson = shipmentAddressSnapshotJson },
                Package = fiscalDeliveryPackage,
                Lines = lines.Select(x => new { x.LineSequence, x.DescriptionSnapshot, x.SkuSnapshot, x.UnitSnapshot, x.Quantity, x.UnitPrice, x.DiscountAmount, x.VatRate, x.VatAmount, x.LineTotal })
            });
            var hash = Hash(canonical); var started = timeProvider.GetUtcNow();
            var attempt = new InvoiceSubmissionAttempt { Id = Guid.CreateVersion7(), TenantId = tenantId, InvoiceId = invoice.Id, AttemptNumber = await NextAttempt(tenantId, invoice.Id, cancellationToken), RequestHash = hash, Outcome = "STARTED", StartedAt = started };
            // A Submitting invoice with an error code means "refused, nothing in flight" (retryable in the
            // workspace); an automatic retry that reaches this point is in flight again, so the code goes.
            invoice.LastErrorCode = null;
            db.InvoiceSubmissionAttempts.Add(attempt);
            await db.SaveChangesAsync(cancellationToken);
            var context = Context(tenantId, connectionId, correlationId, invoice.IdempotencyKey) with { IsStageCapabilityProbe = isStageCapabilityProbe };

            AdapterResult<InvoiceSubmissionResult> result;
            try
            {
                result = await provider.SubmitAsync(context, new(invoice.Id, invoice.Id.ToString("N"), invoice.InvoiceType, invoice.Currency, canonical, hash), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                MarkSubmissionUnknown(invoice, attempt, "INVOICE_SUBMISSION_CANCELLED");
                await db.SaveChangesAsync(CancellationToken.None);
                throw new JobProcessingException(JobExecutionResult.ManualReview("INVOICE_SUBMISSION_OUTCOME_UNKNOWN", "Mali fatura isteği sırasında bağlantı kesildi. Dış sonuç doğrulanmadan yeniden gönderilmedi."));
            }
            catch (Exception)
            {
                MarkSubmissionUnknown(invoice, attempt, "INVOICE_SUBMISSION_TRANSPORT_UNKNOWN");
                await db.SaveChangesAsync(CancellationToken.None);
                throw new JobProcessingException(JobExecutionResult.ManualReview("INVOICE_SUBMISSION_OUTCOME_UNKNOWN", "Mali sağlayıcı isteğinin sonucu kesinleşmedi. Dış sonuç doğrulanmadan yeniden gönderilmedi."));
            }

            attempt.CompletedAt = timeProvider.GetUtcNow();
            if (result.IsSuccess)
            {
                attempt.Outcome = "SUCCEEDED"; attempt.ExternalReference = result.Value!.ExternalReference; attempt.RemoteRequestId = result.Value.RemoteRequestId;
                ApplySubmissionSuccess(invoice, result.Value, started);
                // From here on the provider holds the invoice: nothing may be cancelled before its reference is saved.
                await EnqueueAutomaticJob(tenantId, connectionId, invoice.Id, InvoicingJobTypes.InvoiceReconcile, "after-submit", correlationId, CancellationToken.None);
                if (!await SaveSubmissionSuccess(tenantId, connectionId, invoice, result.Value, started, correlationId))
                    throw new JobProcessingException(JobExecutionResult.ManualReview("INVOICE_SUBMISSION_STATE_CHANGED", "Sağlayıcı faturayı oluşturdu ancak fatura kaydı bu sırada başka bir işlemle değişti. Sağlayıcı referansı gönderim denemesinde saklandı; fatura incelenmeli.", result.Value.RemoteRequestId));
                return true;
            }
            else
            {
                var error = result.Error!;
                if (InvoiceSubmissionOutcomePolicy.IsOutcomeUnknown(error))
                {
                    MarkSubmissionUnknown(invoice, attempt, error.Code);
                    attempt.RemoteRequestId = error.RemoteRequestId;
                    await db.SaveChangesAsync(CancellationToken.None);
                    throw new JobProcessingException(JobExecutionResult.ManualReview("INVOICE_SUBMISSION_OUTCOME_UNKNOWN", "Sağlayıcı yanıtı mali faturanın oluşup oluşmadığını kesinleştirmedi. Aynı fatura yeniden gönderilmedi.", error.RemoteRequestId));
                }
                attempt.Outcome = "FAILED";
                attempt.ErrorClass = error.Class.ToString();
                attempt.ErrorCode = error.Code;
                attempt.RemoteRequestId = error.RemoteRequestId;
                invoice.Status = error.Class switch
                {
                    AdapterErrorClass.Validation => InvoiceStatus.Rejected,
                    AdapterErrorClass.ContractViolation or AdapterErrorClass.InternalBug => InvoiceStatus.ManualReview,
                    _ => InvoiceStatus.Submitting
                };
                invoice.LastErrorCode = error.Code;
            }
            invoice.UpdatedAt = timeProvider.GetUtcNow(); invoice.Version++;
            await db.SaveChangesAsync(cancellationToken);
            if (!result.IsSuccess) throw JobProcessingException.FromAdapter(result.Error!);
            return true;
        }
    }

    // The invoice date is the moment it is actually sent: nothing has reached the provider yet, so a date
    // left from the draft or an earlier failed try is replaced. Once the provider holds it, it never changes.
    internal static void StampIssueTime(Invoice invoice, DateTimeOffset now)
    {
        if (!string.IsNullOrWhiteSpace(invoice.ExternalReference)) return;
        invoice.IssuedAt = now;
        invoice.UpdatedAt = now;
    }

    private void ApplySubmissionSuccess(Invoice invoice, InvoiceSubmissionResult submitted, DateTimeOffset started)
    {
        invoice.ExternalReference = submitted.ExternalReference; invoice.InvoiceNumber = submitted.InvoiceNumber; invoice.EttnUuid = submitted.EttnUuid; invoice.IssuedAt ??= started; invoice.Status = InvoiceStatus.Submitted; invoice.LastErrorCode = null;
        invoice.UpdatedAt = timeProvider.GetUtcNow(); invoice.Version++;
    }

    private static readonly TimeSpan[] SubmissionSuccessSaveDelays = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8)];

    // The provider has issued the invoice. If its reference, number and ETTN are not saved, the next run finds the
    // attempt STARTED and marks the invoice UnknownResult: the reference is lost and a resubmission could issue a
    // second fiscal invoice. So the save ignores cancellation and is retried on a database fault or a concurrent
    // change; each retry reloads the invoice and applies the result again only while the invoice still waits for it
    // (Submitting, or UnknownResult set meanwhile), never over another reference or a later state. False: the invoice
    // moved on, and only the attempt (which keeps the provider's reference) was saved.
    private async Task<bool> SaveSubmissionSuccess(Guid tenantId, Guid connectionId, Invoice invoice, InvoiceSubmissionResult submitted, DateTimeOffset started, string correlationId)
    {
        for (var retry = 0; ; retry++)
        {
            try
            {
                if (retry > 0)
                {
                    // A failed save keeps its changes, but the commit may still have happened: start again from the
                    // stored invoice and let the reconcile job's deduplication see whether it was already queued.
                    foreach (var queued in db.ChangeTracker.Entries<IntegrationJob>().Where(x => x.State == EntityState.Added).ToList())
                        queued.State = EntityState.Detached;
                    var entry = db.Entry(invoice);
                    await entry.ReloadAsync(CancellationToken.None);
                    if (entry.State == EntityState.Detached) return false;
                    var stored = string.Equals(invoice.ExternalReference, submitted.ExternalReference, StringComparison.Ordinal);
                    if (!stored && invoice.Status is (InvoiceStatus.Submitting or InvoiceStatus.UnknownResult) && string.IsNullOrWhiteSpace(invoice.ExternalReference))
                    {
                        ApplySubmissionSuccess(invoice, submitted, started);
                        stored = true;
                    }
                    if (stored)
                        await EnqueueAutomaticJob(tenantId, connectionId, invoice.Id, InvoicingJobTypes.InvoiceReconcile, "after-submit", correlationId, CancellationToken.None);
                }
                await db.SaveChangesAsync(CancellationToken.None);
                return string.Equals(invoice.ExternalReference, submitted.ExternalReference, StringComparison.Ordinal);
            }
            catch (Exception exception) when (retry < SubmissionSuccessSaveDelays.Length
                && exception is DbUpdateException or System.Data.Common.DbException or TimeoutException or InvalidOperationException)
            {
                await Task.Delay(SubmissionSuccessSaveDelays[retry], CancellationToken.None);
            }
        }
    }

    private void MarkSubmissionUnknown(Invoice invoice, InvoiceSubmissionAttempt attempt, string errorCode)
    {
        var now = timeProvider.GetUtcNow();
        attempt.Outcome = "UNKNOWN";
        attempt.ErrorCode = errorCode;
        attempt.CompletedAt = now;
        invoice.Status = InvoiceStatus.UnknownResult;
        invoice.LastErrorCode = errorCode;
        invoice.UpdatedAt = now;
        invoice.Version++;
    }

    private async Task BlockFiscalSubmission(Invoice invoice, string errorCode, string message, CancellationToken cancellationToken)
    {
        invoice.Status = InvoiceStatus.ManualReview;
        invoice.LastErrorCode = errorCode;
        invoice.UpdatedAt = timeProvider.GetUtcNow();
        invoice.Version++;
        await db.SaveChangesAsync(cancellationToken);
        throw new JobProcessingException(JobExecutionResult.Blocked(errorCode, message));
    }

    private static string SnapshotText(JsonElement root, string property, string fallback) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;

    private static DateTimeOffset SnapshotDate(JsonElement root, string property, DateTimeOffset fallback) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed
                : fallback;

    private async Task<bool> Reconcile(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, CancellationToken cancellationToken)
    {
        var invoice = await FindInvoice(tenantId, payloadJson, cancellationToken);
        if (invoice is null || invoice.ProviderConnectionId != connectionId || string.IsNullOrWhiteSpace(invoice.ExternalReference)) return false;
        var cancellationPending = invoice.Status == InvoiceStatus.CancellationPending;
        var result = await provider.QueryStatusAsync(Context(tenantId, connectionId, correlationId, $"reconcile:{invoice.Id:N}"), new(invoice.ExternalReference, invoice.EttnUuid, invoice.InvoiceType), cancellationToken);
        if (!result.IsSuccess)
        {
            invoice.LastErrorCode = result.Error!.Code;
            invoice.UpdatedAt = timeProvider.GetUtcNow();
            invoice.Version++;
            await db.SaveChangesAsync(cancellationToken);
            throw JobProcessingException.FromAdapter(result.Error!);
        }

        var remote = result.Value!;
        invoice.InvoiceNumber ??= remote.InvoiceNumber;
        invoice.EttnUuid ??= remote.EttnUuid;
        switch (remote.CanonicalStatus)
        {
            case "ACCEPTED" when cancellationPending:
                invoice.LastErrorCode = null;
                invoice.UpdatedAt = timeProvider.GetUtcNow();
                invoice.Version++;
                await db.SaveChangesAsync(cancellationToken);
                throw new JobProcessingException(JobExecutionResult.Retry("INVOICE_CANCELLATION_REMOTE_PENDING", "E-Arşiv iptal isteği henüz nihai duruma ulaşmadı.", TimeSpan.FromMinutes(2)));
            case "ACCEPTED":
                invoice.Status = InvoiceStatus.Accepted;
                invoice.LastErrorCode = null;
                await EnqueueAutomaticJob(tenantId, connectionId, invoice.Id, InvoicingJobTypes.InvoiceDocumentFetch, "after-acceptance", correlationId, cancellationToken);
                break;
            case "REJECTED":
                invoice.Status = cancellationPending ? InvoiceStatus.CancellationRejected : InvoiceStatus.Rejected;
                invoice.LastErrorCode = $"REMOTE_STATUS_{remote.RawStatus}";
                break;
            case "CANCELLED":
                invoice.Status = InvoiceStatus.Cancelled;
                invoice.LastErrorCode = null;
                // The cancelled invoice's link stays on the marketplace until it is removed there.
                await InvoiceAdjustmentTracking.MarkLinkRevocationRequiredAsync(db, invoice, timeProvider.GetUtcNow(), cancellationToken);
                break;
            case "PENDING" when !remote.IsTerminal:
                invoice.Status = cancellationPending ? InvoiceStatus.CancellationPending : InvoiceStatus.Submitted;
                invoice.LastErrorCode = null;
                invoice.UpdatedAt = timeProvider.GetUtcNow();
                invoice.Version++;
                await db.SaveChangesAsync(cancellationToken);
                // A just-submitted invoice is checked again after 10, 30 and 60 seconds without using
                // attempts; after five minutes the normal two-minute retry takes over.
                var pendingFor = timeProvider.GetUtcNow() - (invoice.IssuedAt ?? invoice.UpdatedAt);
                if (!cancellationPending && InvoicePendingPolicy.QuickRecheck(pendingFor) is { } quick)
                    throw new JobProcessingException(JobExecutionResult.Deferred("INVOICE_REMOTE_PENDING", "Fatura sağlayıcıda işleniyor; birkaç saniye içinde yeniden kontrol edilecek.", quick));
                throw new JobProcessingException(JobExecutionResult.Retry(cancellationPending ? "INVOICE_CANCELLATION_REMOTE_PENDING" : "INVOICE_REMOTE_PENDING", "Fatura sağlayıcıda işlenmeye devam ediyor.", TimeSpan.FromMinutes(2)));
            default:
                invoice.Status = InvoiceStatus.ManualReview;
                invoice.LastErrorCode = "REMOTE_STATUS_MAPPING_REQUIRED";
                invoice.UpdatedAt = timeProvider.GetUtcNow();
                invoice.Version++;
                await db.SaveChangesAsync(cancellationToken);
                throw new JobProcessingException(JobExecutionResult.ManualReview("REMOTE_STATUS_MAPPING_REQUIRED", $"Eşlenmemiş uzak fatura durumu: {remote.RawStatus}."));
        }

        invoice.UpdatedAt = timeProvider.GetUtcNow();
        invoice.Version++;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private async Task<bool> Deliver(Guid tenantId, Guid jobConnectionId, string payloadJson, string correlationId, CancellationToken cancellationToken)
    {
        if (!TryReadOneTimeInvoiceDeliveryAuthorization(payloadJson, out var oneTimeAuthorization)) return false;
        var invoice = await FindInvoice(tenantId, payloadJson, cancellationToken);
        if (invoice is null) return false;
        var requestedPackageId = ReadPackageId(payloadJson);
        var packageIds = invoice.PackageId is { } legacyPackageId
            ? new[] { legacyPackageId }
            : requestedPackageId is { } selectedPackageId
                ? new[] { selectedPackageId }
                : await db.ShipmentPackages.AsNoTracking()
                    .Where(item => item.TenantId == tenantId && item.OrderId == invoice.OrderId && item.Status != ShipmentPackageStatus.Cancelled)
                    .OrderBy(item => item.Id).Select(item => item.Id).ToArrayAsync(cancellationToken);
        if (packageIds.Length == 0 || oneTimeAuthorization is not null && packageIds.Length != 1) return false;

        foreach (var packageId in packageIds)
        {
            var succeeded = await DeliverSinglePackage(tenantId, jobConnectionId, packageId, payloadJson, correlationId, cancellationToken);
            if (!succeeded) return false;
        }
        return true;
    }

    private async Task<bool> DeliverManualOrderInvoiceDocument(Guid tenantId, Guid jobConnectionId, string payloadJson, string correlationId, CancellationToken cancellationToken)
    {
        Guid documentId;
        Guid packageId;
        var inspectionOnly = false;
        try
        {
            using var payloadDocument = JsonDocument.Parse(payloadJson);
            var root = payloadDocument.RootElement;
            if (!root.TryGetProperty("manualOrderInvoiceDocumentId", out var documentValue)
                || documentValue.ValueKind != JsonValueKind.String
                || !Guid.TryParse(documentValue.GetString(), out documentId)
                || !root.TryGetProperty("packageId", out var packageValue)
                || packageValue.ValueKind != JsonValueKind.String
                || !Guid.TryParse(packageValue.GetString(), out packageId)) return false;
            inspectionOnly = root.TryGetProperty("inspectionOnly", out var inspectionValue)
                && inspectionValue.ValueKind == JsonValueKind.True;
        }
        catch (JsonException) { return false; }

        var document = await db.ManualOrderInvoiceDocuments.SingleOrDefaultAsync(
            item => item.TenantId == tenantId && item.Id == documentId,
            cancellationToken);
        var package = await db.ShipmentPackages.AsNoTracking().SingleOrDefaultAsync(
            item => item.TenantId == tenantId && item.Id == packageId && item.Status != ShipmentPackageStatus.Cancelled,
            cancellationToken);
        var order = package is null ? null : await db.Orders.AsNoTracking().SingleOrDefaultAsync(
            item => item.TenantId == tenantId && item.Id == package.OrderId,
            cancellationToken);
        if (document is null || document.Status != "ACTIVE" || package is null || order is null
            || document.OrderId != order.Id || package.OrderId != order.Id
            || document.ConnectionId != package.ConnectionId || package.ConnectionId != jobConnectionId
            || string.IsNullOrWhiteSpace(document.DocumentUrl)
            || !ProductPublicationComposer.IsPublicHttpsUrl(document.DocumentUrl)) return false;

        var state = await db.ManualOrderInvoiceDeliveryStates.SingleOrDefaultAsync(
            item => item.TenantId == tenantId && item.DocumentId == document.Id && item.PackageId == package.Id,
            cancellationToken);
        inspectionOnly |= ManualInvoiceDeliveryPolicy.RequiresReadOnlyInspection(state?.Status);
        var documentHash = Hash($"{document.DocumentUrl}\n{document.InvoiceNumber}\n{document.IssuedAt:O}");
        if (state is not null && !string.Equals(state.RequestHash, documentHash, StringComparison.Ordinal))
        {
            SetManualInvoiceDeliveryState(state, "MANUAL_REVIEW", "MANUAL_INVOICE_DELIVERY_DOCUMENT_CHANGED", null, timeProvider.GetUtcNow());
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }

        if (document.IssuedAt is null)
        {
            if (state is not null) SetManualInvoiceDeliveryState(state, "FAILED", "MANUAL_INVOICE_ISSUED_AT_REQUIRED", null, timeProvider.GetUtcNow());
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }

        var platformCode = await db.PlatformConnections.AsNoTracking()
            .Where(connection => connection.TenantId == tenantId && connection.Id == package.ConnectionId)
            .Select(connection => connection.PlatformCode)
            .SingleOrDefaultAsync(cancellationToken);
        if (platformCode is not ("TRENDYOL" or "HEPSIBURADA" or "SHOPIFY")
            || platformCode == "SHOPIFY" && string.IsNullOrWhiteSpace(document.InvoiceNumber))
        {
            if (state is not null) SetManualInvoiceDeliveryState(state, "FAILED", "MANUAL_INVOICE_PLATFORM_FIELDS_REQUIRED", null, timeProvider.GetUtcNow());
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }

        var deliveryContext = Context(tenantId, package.ConnectionId, correlationId,
            state?.IdempotencyKey ?? $"manual-invoice-delivery:{document.Id:N}:{package.Id:N}");
        var reference = new ExternalInvoiceDeliveryReference(
            package.ExternalPackageId,
            order.OrderNumber,
            platformCode == "SHOPIFY" ? ShopifyOrderGid(order.ExternalOrderId) : null,
            document.InvoiceNumber,
            document.DocumentUrl,
            LookupFor(order.OrderedAt, order.LastRemoteModifiedAt, order.StoreFrontCode, package));
        var before = await marketplace.QueryDeliveryAsync(deliveryContext, reference, cancellationToken);
        if (!before.IsSuccess)
        {
            if (state is not null) SetManualInvoiceDeliveryState(state, "MANUAL_REVIEW", before.Error!.Code, null, timeProvider.GetUtcNow());
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }

        var beforeStatus = MarketplaceInvoiceStatePolicy.FromRemote(before.Value!.RawStatus,
            invoiceNumber: before.Value.VerifiedInvoiceNumber, invoiceUrl: before.Value.VerifiedInvoiceLink);
        if (beforeStatus == MarketplaceInvoiceStatus.Invoiced)
        {
            if (ManualInvoiceIdentityMatches(document, before.Value.VerifiedInvoiceNumber, before.Value.VerifiedInvoiceLink))
                await ConfirmManualOrderInvoiceDelivery(tenantId, document, package, state, before.Value.ExternalReference, before.Value.VerifiedInvoiceNumber, before.Value.VerifiedInvoiceLink, cancellationToken);
            else
            {
                if (state is not null) SetManualInvoiceDeliveryState(state, "MANUAL_REVIEW", "MARKETPLACE_HAS_DIFFERENT_OR_UNVERIFIABLE_INVOICE", before.Value.ExternalReference, timeProvider.GetUtcNow());
                await db.SaveChangesAsync(cancellationToken);
            }
            return true;
        }
        if (beforeStatus != MarketplaceInvoiceStatus.NotInvoiced)
        {
            if (state is not null) SetManualInvoiceDeliveryState(state, "MANUAL_REVIEW", "MARKETPLACE_INVOICE_PRECHECK_NOT_CLEAR", before.Value.ExternalReference, timeProvider.GetUtcNow());
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        if (inspectionOnly || !ManualInvoiceDeliveryPolicy.CanSubmitAfterPrecheck(state?.Status, beforeStatus))
        {
            if (state is not null) SetManualInvoiceDeliveryState(state, "MANUAL_REVIEW", "MANUAL_INVOICE_DELIVERY_ABSENCE_REQUIRES_REVIEW", before.Value.ExternalReference, timeProvider.GetUtcNow());
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }

        var deliveryPayload = new Dictionary<string, object?>
        {
            ["shipmentPackageId"] = package.ExternalPackageId,
            ["invoiceLink"] = document.DocumentUrl,
            ["invoiceDateTime"] = document.IssuedAt.Value.ToUnixTimeMilliseconds(),
            ["invoiceNumber"] = document.InvoiceNumber,
            ["micro"] = IsMicroExport(order.CustomerSnapshotJson)
        };
        if (platformCode == "HEPSIBURADA")
        {
            deliveryPayload["arrangementDate"] = document.IssuedAt.Value.ToString("O", CultureInfo.InvariantCulture);
            deliveryPayload["contentType"] = "application/pdf";
            deliveryPayload["orderNumber"] = order.OrderNumber;
        }
        if (platformCode == "SHOPIFY") deliveryPayload["shopifyOrderId"] = ShopifyOrderGid(order.ExternalOrderId);
        var payloadJsonValue = JsonSerializer.Serialize(deliveryPayload);
        var deliveryRequestHash = Hash(payloadJsonValue);
        if (state is null)
        {
            state = new ManualOrderInvoiceDeliveryState
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                DocumentId = document.Id,
                ConnectionId = package.ConnectionId,
                PackageId = package.Id,
                IdempotencyKey = $"manual-invoice-delivery:{document.Id:N}:{package.Id:N}",
                RequestHash = documentHash,
                Status = "STARTED",
                AttemptCount = 1,
                CreatedAt = timeProvider.GetUtcNow(),
                UpdatedAt = timeProvider.GetUtcNow(),
                Version = 1
            };
            db.ManualOrderInvoiceDeliveryStates.Add(state);
        }
        else
        {
            if (state.Status == "CONFIRMED") return true;
            state.Status = "STARTED";
            state.ErrorCode = null;
            state.AttemptCount++;
            state.UpdatedAt = timeProvider.GetUtcNow();
            state.CompletedAt = null;
            state.Version++;
        }
        await db.SaveChangesAsync(cancellationToken);

        AdapterResult<InvoiceDeliveryResult> submitted;
        try
        {
            var explicitContext = Context(tenantId, package.ConnectionId, correlationId, state.IdempotencyKey, IntegrationOperation.Manual) with
            {
                IsExplicitManualInvoiceDelivery = true
            };
            submitted = await marketplace.DeliverAsync(explicitContext,
                new(package.ExternalPackageId, "LINK", payloadJsonValue, deliveryRequestHash, order.StoreFrontCode), cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            SetManualInvoiceDeliveryState(state, "UNKNOWN", "MANUAL_INVOICE_DELIVERY_TIMEOUT", null, timeProvider.GetUtcNow());
            await db.SaveChangesAsync(CancellationToken.None);
            return true;
        }
        catch (Exception)
        {
            SetManualInvoiceDeliveryState(state, "UNKNOWN", "MANUAL_INVOICE_DELIVERY_RESULT_UNKNOWN", null, timeProvider.GetUtcNow());
            await db.SaveChangesAsync(CancellationToken.None);
            return true;
        }

        if (!submitted.IsSuccess)
        {
            var outcome = InvoiceDeliveryFailurePolicy.Classify(submitted.Error!.Class);
            var newState = outcome switch
            {
                InvoiceDeliveryFailureDisposition.Retry => "RETRYABLE_FAILURE",
                InvoiceDeliveryFailureDisposition.Unknown => "UNKNOWN",
                _ => "FAILED"
            };
            SetManualInvoiceDeliveryState(state, newState, submitted.Error.Code, submitted.Value?.ExternalReference, timeProvider.GetUtcNow());
            await db.SaveChangesAsync(cancellationToken);
            if (outcome == InvoiceDeliveryFailureDisposition.Retry)
                throw new JobProcessingException(JobExecutionResult.Retry(submitted.Error.Code,
                    "Pazaryeri faturayı kesin olarak kabul etmedi; güvenli geçici hata yeniden denenecek.",
                    submitted.Error.RetryAfter ?? TimeSpan.FromSeconds(30), state.IdempotencyKey));
            return true;
        }

        if (submitted.Value!.SameAttemptVerified)
        {
            await ConfirmManualOrderInvoiceDelivery(tenantId, document, package, state,
                submitted.Value.ExternalReference,
                submitted.Value.VerifiedInvoiceNumber,
                submitted.Value.VerifiedInvoiceLink,
                cancellationToken);
            return true;
        }

        var after = await marketplace.QueryDeliveryAsync(deliveryContext, reference, cancellationToken);
        if (after.IsSuccess && MarketplaceInvoiceStatePolicy.FromRemote(after.Value!.RawStatus,
                invoiceNumber: after.Value.VerifiedInvoiceNumber, invoiceUrl: after.Value.VerifiedInvoiceLink) == MarketplaceInvoiceStatus.Invoiced
            && ManualInvoiceIdentityMatches(document, after.Value.VerifiedInvoiceNumber, after.Value.VerifiedInvoiceLink))
        {
            await ConfirmManualOrderInvoiceDelivery(tenantId, document, package, state, after.Value.ExternalReference,
                after.Value.VerifiedInvoiceNumber, after.Value.VerifiedInvoiceLink, cancellationToken);
            return true;
        }

        var readbackCode = !after.IsSuccess ? after.Error!.Code : "MANUAL_INVOICE_DELIVERY_READBACK_UNVERIFIED";
        SetManualInvoiceDeliveryState(state, "UNKNOWN", readbackCode, submitted.Value.ExternalReference, timeProvider.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private async Task ConfirmManualOrderInvoiceDelivery(Guid tenantId, ManualOrderInvoiceDocument document, ShipmentPackage package,
        ManualOrderInvoiceDeliveryState? state, string? externalReference, string? verifiedInvoiceNumber, string? verifiedInvoiceLink,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        if (state is not null) SetManualInvoiceDeliveryState(state, "CONFIRMED", null, externalReference, now);
        var trackedPackage = await db.ShipmentPackages.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == package.Id, cancellationToken);
        if (trackedPackage is not null)
        {
            MarketplaceInvoiceReferenceHistoryRecorder.RecordPreviousReferences(
                db, trackedPackage, verifiedInvoiceNumber, verifiedInvoiceLink, now, "MANUAL_DELIVERY_READBACK");
            trackedPackage.MarketplaceInvoiceStatus = MarketplaceInvoiceStatus.Invoiced;
            trackedPackage.MarketplaceInvoiceNumber = verifiedInvoiceNumber;
            trackedPackage.MarketplaceInvoiceUrl = verifiedInvoiceLink;
            trackedPackage.MarketplaceInvoiceObservedAt = now;
            trackedPackage.UpdatedAt = now;
            trackedPackage.Version++;
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    private static void SetManualInvoiceDeliveryState(ManualOrderInvoiceDeliveryState state, string status, string? errorCode, string? externalReference, DateTimeOffset now)
    {
        state.Status = status;
        state.ErrorCode = errorCode;
        state.ExternalReference = externalReference;
        state.UpdatedAt = now;
        state.CompletedAt = status is "CONFIRMED" or "FAILED" or "MANUAL_REVIEW" ? now : null;
        state.Version++;
    }

    private static bool ManualInvoiceIdentityMatches(ManualOrderInvoiceDocument document, string? remoteNumber, string? remoteUrl)
    {
        if (!string.IsNullOrWhiteSpace(document.InvoiceNumber) && !string.IsNullOrWhiteSpace(remoteNumber)
            && !string.Equals(document.InvoiceNumber.Trim(), remoteNumber.Trim(), StringComparison.Ordinal)) return false;
        if (!string.IsNullOrWhiteSpace(remoteUrl)
            && !string.Equals(document.DocumentUrl.TrimEnd('/'), remoteUrl.Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase)) return false;
        var numberMatches = !string.IsNullOrWhiteSpace(document.InvoiceNumber)
            && string.Equals(document.InvoiceNumber.Trim(), remoteNumber?.Trim(), StringComparison.Ordinal);
        var urlMatches = !string.IsNullOrWhiteSpace(remoteUrl)
            && string.Equals(document.DocumentUrl.TrimEnd('/'), remoteUrl.Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
        return numberMatches || urlMatches;
    }

    private async Task<bool> DeliverSinglePackage(Guid tenantId, Guid jobConnectionId, Guid targetPackageId, string payloadJson, string correlationId, CancellationToken cancellationToken)
    {
        if (!TryReadOneTimeInvoiceDeliveryAuthorization(payloadJson, out var oneTimeAuthorization)) return false;
        var invoice = await FindInvoice(tenantId, payloadJson, cancellationToken);
        if (invoice is null) return false;
        if (InvoiceDeliveryGuardPolicy.InvoiceBlocksDelivery(invoice.Status))
            throw new JobProcessingException(JobExecutionResult.Blocked(InvoiceDeliveryGuardPolicy.InvoiceNotDeliverableCode,
                "Fatura iptal sürecinde, iptal edilmiş, reddedilmiş veya düzeltme bekliyor; pazaryerine fatura linki gönderilmedi."));
        using var deliveryJobPayload = JsonDocument.Parse(payloadJson);
        var manualDeliveryRetry = deliveryJobPayload.RootElement.TryGetProperty("manualDeliveryRetry", out var retryValue)
            && retryValue.ValueKind == JsonValueKind.True
            && invoice.LastErrorCode == InvoiceMarketplaceRetryPolicy.RepeatedRemoteFailure;
        // Tracked: the delivery reads write the marketplace's invoice state onto the package. Read without tracking,
        // those writes were dropped, so the stored references never caught up and every attempt added the same
        // reference-history row again.
        var package = await db.ShipmentPackages.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == targetPackageId && x.OrderId == invoice.OrderId, cancellationToken);
        var order = package is null
            ? null
            : await db.Orders.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == package.OrderId).Select(x => new { x.OrderNumber, x.ExternalOrderId, x.CustomerSnapshotJson, x.OrderedAt, x.LastRemoteModifiedAt, x.StoreFrontCode }).SingleOrDefaultAsync(cancellationToken);
        if (package is null || package.ConnectionId != jobConnectionId) return false;
        var platformCode = await db.PlatformConnections.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == package.ConnectionId).Select(x => x.PlatformCode).SingleOrDefaultAsync(cancellationToken);
        var permanentDocument = await LoadPermanentInvoiceDocumentAsync(tenantId, invoice.Id, cancellationToken);
        var permanentUrl = permanentDocument?.PermanentUrl;

        var state = await LoadDeliveryState(tenantId, invoice.Id, package.Id, cancellationToken);
        // A link in removal (or already removed) belongs to a cancelled invoice and is never sent again.
        if (InvoiceDeliveryGuardPolicy.StateBlocksDelivery(state?.Status))
            throw new JobProcessingException(JobExecutionResult.Blocked(InvoiceDeliveryGuardPolicy.LinkRevocationOpenCode,
                "Bu paketteki fatura linki kaldırma sürecinde veya kaldırıldı; pazaryerine yeniden gönderilmedi."));
        if (oneTimeAuthorization is not null
            && !await IsValidOneTimeInvoiceDeliveryAsync(tenantId, jobConnectionId, invoice, package, order?.OrderNumber, oneTimeAuthorization, state, cancellationToken))
            return false;
        // n11, Pazarama and PTT AVM take only links that end in a document extension, so their deliveries always use the
        // document-bound link, which the API also serves as .../invoice.pdf.
        if ((oneTimeAuthorization is not null && OneTimeInvoiceDeliveryPolicy.IsAuthorizedStageDocument(order?.OrderNumber, invoice.Id)
                || string.Equals(platformCode, "N11", StringComparison.OrdinalIgnoreCase)
                || string.Equals(platformCode, "PAZARAMA", StringComparison.OrdinalIgnoreCase)
                || string.Equals(platformCode, "PTTAVM", StringComparison.OrdinalIgnoreCase))
            && state?.Status is not ("STARTED" or "UNKNOWN" or "SUBMITTED" or "CONFIRMATION_RETRYABLE" or "CONFIRMED"))
        {
            // Provider URLs may require authentication. Publish only the already
            // stored PDF, through a document-bound bearer link, for this explicit delivery.
            var deliveryLinkDocument = await db.InvoiceDocuments.SingleOrDefaultAsync(x => x.TenantId == tenantId
                && x.InvoiceId == invoice.Id && x.DocumentType == "MARKETPLACE_DELIVERY", cancellationToken);
            if (deliveryLinkDocument is null)
            {
                var document = await db.InvoiceDocuments.Where(x => x.TenantId == tenantId && x.InvoiceId == invoice.Id && x.DocumentType == "PDF")
                    .OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(cancellationToken);
                if (document is null) return false;
                deliveryLinkDocument = new InvoiceDocument
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = tenantId,
                    InvoiceId = invoice.Id,
                    DocumentType = "MARKETPLACE_DELIVERY",
                    FileAssetId = document.FileAssetId,
                    Sha256 = document.Sha256,
                    CreatedAt = timeProvider.GetUtcNow()
                };
                deliveryLinkDocument.PermanentUrl = InvoiceDocumentLink.Create(dataProtection, configuration["Marketplace:PublicBaseUrl"] ?? "", tenantId, invoice.Id, deliveryLinkDocument.Id);
                db.InvoiceDocuments.Add(deliveryLinkDocument);
                await db.SaveChangesAsync(cancellationToken);
            }
            if (!Uri.TryCreate(deliveryLinkDocument.PermanentUrl, UriKind.Absolute, out var publicDocumentUri)
                || !publicDocumentUri.AbsolutePath.StartsWith("/api/v1/public/invoice-documents/", StringComparison.Ordinal)) return false;
            permanentUrl = deliveryLinkDocument.PermanentUrl;
        }
        if (state?.Status == "CONFIRMED") return true;
        if (oneTimeAuthorization is not null && state?.Status == "STARTED")
            throw new JobProcessingException(JobExecutionResult.ManualReview("ONE_TIME_INVOICE_DELIVERY_ALREADY_STARTED", "Tek seferlik fatura isteği daha önce başlatıldı; dış sonuç doğrulanmadan tekrar gönderilmedi."));
        if (invoice.Status == InvoiceStatus.Completed && state is null)
            throw new JobProcessingException(JobExecutionResult.ManualReview("INVOICE_DELIVERY_EVIDENCE_MISSING", "Mali faturanın tamamlanma kaydı var ancak aynı faturanın pazaryerine iletildiğine dair teslim kanıtı bulunamadı; yeniden gönderim yapılmadı."));
        if (invoice.Status == InvoiceStatus.Completed && state?.Status is not "CONFIRMED")
            throw new JobProcessingException(JobExecutionResult.ManualReview("INVOICE_DELIVERY_EVIDENCE_MISSING", "Mali fatura tamamlandı görünse de pazaryeri teslimi doğrulanmamış; kayıt incelemede bırakıldı."));
        if (state?.Status == "SUBMITTED")
        {
            if (string.IsNullOrWhiteSpace(state.ExternalReference))
                throw new JobProcessingException(JobExecutionResult.ManualReview("DELIVERY_REFERENCE_MISSING", "Gönderilmiş fatura bağlantısı için uzak referans bulunamadı; yeniden gönderim yapılmadı."));
            if (string.Equals(platformCode, "SHOPIFY", StringComparison.OrdinalIgnoreCase))
                return await ConfirmDelivery(tenantId, invoice, package, state, correlationId, cancellationToken);
            // A previous 201 only proves that Trendyol accepted the link
            // submission. Keep it pending until order stream/webhook data
            // reports invoiceStatus=Invoiced or Rejected.
            invoice.Status = InvoiceStatus.MarketplacePending;
            invoice.LastErrorCode = null;
            invoice.UpdatedAt = timeProvider.GetUtcNow();
            invoice.Version++;
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        if (state?.Status == "CONFIRMATION_RETRYABLE")
        {
            invoice.Status = InvoiceStatus.MarketplacePending;
            await db.SaveChangesAsync(cancellationToken);
            return await ConfirmDelivery(tenantId, invoice, package, state, correlationId, cancellationToken);
        }
        if (state?.Status == "FAILED" && oneTimeAuthorization is null)
            throw new JobProcessingException(JobExecutionResult.Blocked("DELIVERY_ALREADY_FAILED", "Önceki fatura bağlantısı teslimi kalıcı olarak reddedildi; yeni dış işlem başlatılmadı."));
        if (state?.Status == "FAILED" && oneTimeAuthorization is not null
            && !OneTimeInvoiceDeliveryPolicy.IsSafePriorFailure(state.Status, state.ErrorCode, state.ExternalReference))
            throw new JobProcessingException(JobExecutionResult.Blocked("ONE_TIME_INVOICE_PRIOR_ATTEMPT_UNSAFE", "Önceki teslim sonucu tek seferlik yeniden gönderim için güvenli değil."));
        if (state?.Status == "STARTED" && oneTimeAuthorization is null)
        {
            // A crash after persisting STARTED may have happened either before
            // or after the remote request. Treat it as uncertain and reconcile
            // by read only; never send a second write from this state.
            AppendDeliveryHistory(state, "UNKNOWN", state.ExternalReference, "DELIVERY_ATTEMPT_INTERRUPTED", timeProvider.GetUtcNow());
            await db.SaveChangesAsync(cancellationToken);
        }
        if (state?.Status is "UNKNOWN" or "STARTED")
        {
            if (string.Equals(platformCode, "SHOPIFY", StringComparison.OrdinalIgnoreCase))
            {
                var unknownPermanentUrl = await LoadPermanentInvoiceUrlAsync(tenantId, invoice.Id, cancellationToken);
                var orderGid = ShopifyOrderGid(order?.ExternalOrderId);
                var liveReadback = await marketplace.QueryDeliveryAsync(
                    Context(tenantId, package.ConnectionId, correlationId, $"delivery-recovery-read:{invoice.Id:N}"),
                    new(state.ExternalReference ?? package.ExternalPackageId, order?.OrderNumber, orderGid, invoice.InvoiceNumber, unknownPermanentUrl, LookupFor(order?.OrderedAt, order?.LastRemoteModifiedAt, order?.StoreFrontCode, package)),
                    cancellationToken);
                if (!liveReadback.IsSuccess) throw JobProcessingException.FromAdapter(liveReadback.Error!);
                if (HasExactDeliveryProof(liveReadback.Value!, invoice.InvoiceNumber, unknownPermanentUrl))
                {
                    AppendDeliveryHistory(state, "CONFIRMED", liveReadback.Value!.ExternalReference, null, timeProvider.GetUtcNow());
                    await SetInvoiceDeliveryOutcome(tenantId, invoice, cancellationToken);
                    await db.SaveChangesAsync(cancellationToken);
                    return true;
                }
                if (MarketplaceInvoiceStatePolicy.FromRemote(liveReadback.Value!.RawStatus) == MarketplaceInvoiceStatus.NotInvoiced)
                {
                    AppendDeliveryHistory(state, "RETRYABLE_FAILURE", state.ExternalReference, "REMOTE_NOT_INVOICED", timeProvider.GetUtcNow());
                    await db.SaveChangesAsync(cancellationToken);
                }
                else
                {
                    invoice.Status = InvoiceStatus.ManualReview;
                    invoice.LastErrorCode = "REMOTE_INVOICE_NUMBER_MISMATCH";
                    invoice.UpdatedAt = timeProvider.GetUtcNow();
                    invoice.Version++;
                    AppendDeliveryHistory(state, "UNKNOWN", state.ExternalReference, invoice.LastErrorCode, timeProvider.GetUtcNow());
                    await db.SaveChangesAsync(cancellationToken);
                    throw new JobProcessingException(JobExecutionResult.ManualReview(invoice.LastErrorCode, "Shopify'da bu mali faturayla eşleşen iki metafield doğrulanamadı. Belirsiz yazma otomatik tekrarlanmadı."));
                }
            }
            if (string.Equals(platformCode, "HEPSIBURADA", StringComparison.OrdinalIgnoreCase))
            {
                var liveReadback = await marketplace.QueryDeliveryAsync(
                    Context(tenantId, package.ConnectionId, correlationId, $"delivery-recovery-read:{invoice.Id:N}"),
                    new(package.ExternalPackageId, order?.OrderNumber, Lookup: LookupFor(order?.OrderedAt, order?.LastRemoteModifiedAt, order?.StoreFrontCode, package)),
                    cancellationToken);
                if (!liveReadback.IsSuccess)
                    throw JobProcessingException.FromAdapter(liveReadback.Error!);

                // Hepsiburada exposes hasInvoice on order detail. Refresh the
                // decision from this live read instead of waiting for an order
                // stream that may not update MarketplaceInvoiceObservedAt.
                package.MarketplaceInvoiceStatus = MarketplaceInvoiceStatePolicy.FromRemote(liveReadback.Value!.RawStatus);
                package.MarketplaceInvoiceRawStatus = liveReadback.Value.RawStatus;
                var readAt = timeProvider.GetUtcNow();
                MarketplaceInvoiceReferenceHistoryRecorder.RecordPreviousReferences(
                    db, package, liveReadback.Value.VerifiedInvoiceNumber, liveReadback.Value.VerifiedInvoiceLink,
                    readAt, "DELIVERY_RECOVERY_READBACK");
                package.MarketplaceInvoiceNumber = liveReadback.Value.VerifiedInvoiceNumber;
                package.MarketplaceInvoiceUrl = liveReadback.Value.VerifiedInvoiceLink;
                package.MarketplaceInvoiceObservedAt = readAt;
                package.UpdatedAt = readAt;
                package.Version++;

                // The package was read empty right before the link was sent and Hepsiburada accepted the request, so an
                // invoice it shows afterwards is this one; its read shows only that an invoice exists, never its number,
                // which made every such delivery a "different invoice number" for manual review.
                if (InvoiceDeliveryRecoveryPolicy.ConfirmsAcceptedHepsiburadaAttemptReadback(
                        state.Status,
                        state.ErrorCode,
                        package.MarketplaceInvoiceStatus,
                        package.MarketplaceInvoiceObservedAt,
                        state.UpdatedAt))
                {
                    var confirmedAt = timeProvider.GetUtcNow();
                    AppendDeliveryHistory(state, "CONFIRMED", liveReadback.Value.ExternalReference ?? package.ExternalPackageId, null, confirmedAt);
                    await SetInvoiceDeliveryOutcome(tenantId, invoice, cancellationToken, confirmedAt);
                    await db.SaveChangesAsync(cancellationToken);
                    return true;
                }
            }

            var recovery = InvoiceDeliveryRecoveryPolicy.Decide(
                package.MarketplaceInvoiceStatus,
                package.MarketplaceInvoiceNumber,
                invoice.InvoiceNumber,
                package.MarketplaceInvoiceObservedAt,
                state.UpdatedAt);
            switch (recovery)
            {
                case InvoiceDeliveryRecoveryAction.ConfirmDelivery:
                    AppendDeliveryHistory(state, "CONFIRMED", state.ExternalReference ?? package.ExternalPackageId, null, timeProvider.GetUtcNow());
                    await SetInvoiceDeliveryOutcome(tenantId, invoice, cancellationToken);
                    await db.SaveChangesAsync(cancellationToken);
                    return true;
                case InvoiceDeliveryRecoveryAction.RetryDelivery when oneTimeAuthorization is null:
                    if (!manualDeliveryRetry && InvoiceDeliveryRecoveryPolicy.ShouldStopAfterRemoteFailures(
                        platformCode,
                        await db.MarketplaceDeliveries.AsNoTracking().CountAsync(
                            attempt => attempt.TenantId == tenantId
                                && attempt.InvoiceId == invoice.Id
                                && attempt.PackageId == package.Id
                                && attempt.ErrorCode == "HEPSIBURADA_REMOTE_ERROR",
                            cancellationToken)))
                    {
                        const string errorCode = "HEPSIBURADA_INVOICE_DELIVERY_REPEATED_500";
                        AppendDeliveryHistory(state, "FAILED", state.ExternalReference, errorCode, timeProvider.GetUtcNow());
                        invoice.Status = InvoiceStatus.ManualReview;
                        invoice.LastErrorCode = errorCode;
                        invoice.UpdatedAt = timeProvider.GetUtcNow();
                        invoice.Version++;
                        await db.SaveChangesAsync(cancellationToken);
                        throw new JobProcessingException(JobExecutionResult.ManualReview(
                            errorCode,
                            "Hepsiburada faturayı üç veya daha fazla kez HTTP 500 ile reddetti. Siparişte fatura görünmediği doğrudan doğrulandı; yeni otomatik gönderim durduruldu."));
                    }
                    AppendDeliveryHistory(state, "RETRYABLE_FAILURE", state.ExternalReference, "REMOTE_NOT_INVOICED", timeProvider.GetUtcNow());
                    invoice.Status = InvoiceStatus.MarketplacePending;
                    invoice.LastErrorCode = null;
                    invoice.UpdatedAt = timeProvider.GetUtcNow();
                    invoice.Version++;
                    await db.SaveChangesAsync(cancellationToken);
                    break;
                case InvoiceDeliveryRecoveryAction.RetryDelivery:
                    throw new JobProcessingException(JobExecutionResult.ManualReview("ONE_TIME_INVOICE_DELIVERY_ALREADY_ATTEMPTED", "Tek seferlik gönderim belirsiz sonuçlandı; uzlaştırma yeni dış gönderim izni olmadan ikinci kez çalıştırılmadı."));
                case InvoiceDeliveryRecoveryAction.StopRejected:
                    AppendDeliveryHistory(state, "FAILED", state.ExternalReference, "REMOTE_INVOICE_REJECTED", timeProvider.GetUtcNow());
                    invoice.Status = InvoiceStatus.MarketplaceFailed;
                    invoice.LastErrorCode = "REMOTE_INVOICE_REJECTED";
                    invoice.UpdatedAt = timeProvider.GetUtcNow();
                    invoice.Version++;
                    await db.SaveChangesAsync(cancellationToken);
                    throw new JobProcessingException(JobExecutionResult.Blocked("REMOTE_INVOICE_REJECTED", "Pazaryeri bu fatura bağlantısını reddetti; yeniden gönderim yapılmadı."));
                case InvoiceDeliveryRecoveryAction.ManualReview:
                    invoice.LastErrorCode = "REMOTE_INVOICE_NUMBER_MISMATCH";
                    invoice.UpdatedAt = timeProvider.GetUtcNow();
                    invoice.Version++;
                    await db.SaveChangesAsync(cancellationToken);
                    throw new JobProcessingException(JobExecutionResult.ManualReview("REMOTE_INVOICE_NUMBER_MISMATCH", "Pazaryerinde başka bir fatura numarası görünüyor; kayıt tamamlanmadan önce incelenmeli."));
                default:
                    throw new JobProcessingException(JobExecutionResult.Retry("DELIVERY_RESULT_UNKNOWN", "Belirsiz fatura gönderiminden sonra siparişin güncel fatura durumu bekleniyor; yeni dış istek yapılmadı.", TimeSpan.FromMinutes(2), state.ExternalIdempotencyKey));
            }
        }

        // Each delivery is gated by a fresh read immediately before the write.
        // A previous precheck (including the one before fiscal submission) is
        // not sufficient because the marketplace may have changed meanwhile.
        var beforeDelivery = await marketplace.QueryDeliveryAsync(
            Context(tenantId, package.ConnectionId, correlationId, $"delivery-precheck:{invoice.Id:N}:{package.Id:N}", IntegrationOperation.Automatic),
            new(package.ExternalPackageId, order?.OrderNumber, ShopifyOrderGid(order?.ExternalOrderId), Lookup: LookupFor(order?.OrderedAt, order?.LastRemoteModifiedAt, order?.StoreFrontCode, package)),
            cancellationToken);
        if (!beforeDelivery.IsSuccess)
        {
            invoice.Status = InvoiceStatus.MarketplacePending;
            invoice.LastErrorCode = "MARKETPLACE_INVOICE_PRECHECK_FAILED";
            invoice.UpdatedAt = timeProvider.GetUtcNow();
            invoice.Version++;
            await db.SaveChangesAsync(cancellationToken);
            throw JobProcessingException.FromAdapter(beforeDelivery.Error!);
        }

        var latestMarketplaceInvoice = beforeDelivery.Value!;
        var observedAt = timeProvider.GetUtcNow();
        var unverified = MarketplaceInvoiceStatePolicy.IsUnverified(latestMarketplaceInvoice.RawStatus);
        if (unverified)
        {
            // n11 / PTT AVM cannot show a seller-panel invoice: the package keeps its state, and a link is sent only
            // while nothing local (an observed invoice or a hand-set "uploaded") says the order already holds one.
            if (MarketplaceInvoiceStatePolicy.UnverifiedReadShowsInvoice(package.MarketplaceInvoiceStatus, package.ManualInvoiceStatus))
            {
                invoice.Status = InvoiceStatus.ManualReview;
                invoice.LastErrorCode = "PLATFORM_INVOICE_ALREADY_PRESENT";
                invoice.UpdatedAt = observedAt;
                invoice.Version++;
                await db.SaveChangesAsync(cancellationToken);
                throw new JobProcessingException(JobExecutionResult.ManualReview(invoice.LastErrorCode,
                    "Siparişte pazaryeri faturası var veya fatura yüklendi olarak işaretlenmiş. Üzerine yazılmadı ve yeni belge gönderilmedi."));
            }
        }
        else
        {
            var explicitlyNotInvoiced = MarketplaceInvoiceStatePolicy.FromRemote(
                latestMarketplaceInvoice.RawStatus,
                invoiceNumber: latestMarketplaceInvoice.VerifiedInvoiceNumber,
                invoiceUrl: latestMarketplaceInvoice.VerifiedInvoiceLink) == MarketplaceInvoiceStatus.NotInvoiced;
            package.MarketplaceInvoiceRawStatus = latestMarketplaceInvoice.RawStatus;
            package.MarketplaceInvoiceStatus = MarketplaceInvoiceStatePolicy.FromRemote(
                latestMarketplaceInvoice.RawStatus,
                invoiceNumber: latestMarketplaceInvoice.VerifiedInvoiceNumber,
                invoiceUrl: latestMarketplaceInvoice.VerifiedInvoiceLink);
            var latestReferences = explicitlyNotInvoiced
                ? (InvoiceNumber: (string?)null, InvoiceUrl: (string?)null)
                : MarketplaceInvoiceStatePolicy.MergeReferences(
                    package.MarketplaceInvoiceNumber,
                    package.MarketplaceInvoiceUrl,
                    latestMarketplaceInvoice.VerifiedInvoiceNumber,
                    latestMarketplaceInvoice.VerifiedInvoiceLink,
                    latestMarketplaceInvoice.ReferencesAreAuthoritative);
            MarketplaceInvoiceReferenceHistoryRecorder.RecordPreviousReferences(
                db, package, latestReferences.InvoiceNumber, latestReferences.InvoiceUrl, observedAt, "DELIVERY_PRECHECK");
            package.MarketplaceInvoiceNumber = latestReferences.InvoiceNumber;
            package.MarketplaceInvoiceUrl = latestReferences.InvoiceUrl;
            package.MarketplaceInvoiceObservedAt = observedAt;
            package.UpdatedAt = observedAt;
            package.Version++;

            if (!explicitlyNotInvoiced)
            {
                if (HasExactDeliveryProof(latestMarketplaceInvoice, invoice.InvoiceNumber, permanentUrl))
                {
                    state ??= new MarketplaceDeliveryState
                    {
                        Id = Guid.CreateVersion7(),
                        TenantId = tenantId,
                        InvoiceId = invoice.Id,
                        ConnectionId = package.ConnectionId,
                        PackageId = package.Id,
                        AttemptNumber = 0,
                        ExternalIdempotencyKey = $"delivery:{invoice.Id:N}:{package.Id:N}",
                        RequestHash = Hash($"remote-proof:{invoice.Id:N}:{package.Id:N}:{invoice.InvoiceNumber}:{permanentUrl}"),
                        DeliveryType = "LINK",
                        Status = "UNKNOWN",
                        ExternalReference = package.ExternalPackageId,
                        CreatedAt = observedAt,
                        UpdatedAt = observedAt,
                        Version = 1
                    };
                    if (db.Entry(state).State == EntityState.Detached) db.MarketplaceDeliveryStates.Add(state);
                    AppendDeliveryHistory(state, "CONFIRMED", latestMarketplaceInvoice.ExternalReference, null, observedAt);
                    await SetInvoiceDeliveryOutcome(tenantId, invoice, cancellationToken, observedAt);
                    await db.SaveChangesAsync(cancellationToken);
                    return true;
                }

                var presentButDifferent = package.MarketplaceInvoiceStatus is MarketplaceInvoiceStatus.Invoiced or MarketplaceInvoiceStatus.Received;
                invoice.Status = InvoiceStatus.ManualReview;
                invoice.LastErrorCode = presentButDifferent ? "PLATFORM_INVOICE_ALREADY_PRESENT" : "PLATFORM_INVOICE_STATUS_NOT_CLEAR";
                invoice.UpdatedAt = observedAt;
                invoice.Version++;
                await db.SaveChangesAsync(cancellationToken);
                throw new JobProcessingException(JobExecutionResult.ManualReview(invoice.LastErrorCode,
                    presentButDifferent
                        ? "Pazaryerinde mevcut fatura veya farklı bir belge bulundu. Üzerine yazılmadı ve yeni belge gönderilmedi."
                        : "Pazaryeri açık bir 'fatura yok' cevabı vermedi. Güvenli iletim için işlem durduruldu."));
            }
        }

        await db.SaveChangesAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(permanentUrl) || !Uri.TryCreate(permanentUrl, UriKind.Absolute, out var link) || link.Scheme != Uri.UriSchemeHttps) return false;

        var deliveryPayload = new Dictionary<string, object?>
        {
            ["shipmentPackageId"] = package.ExternalPackageId,
            ["invoiceLink"] = link.AbsoluteUri,
            ["invoiceDateTime"] = invoice.IssuedAt?.ToUnixTimeMilliseconds(),
            ["invoiceNumber"] = invoice.InvoiceNumber,
            ["micro"] = IsMicroExport(order?.CustomerSnapshotJson)
        };
        if (string.Equals(platformCode, "HEPSIBURADA", StringComparison.OrdinalIgnoreCase))
        {
            deliveryPayload["arrangementDate"] = invoice.IssuedAt?.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
            deliveryPayload["contentType"] = permanentDocument?.MimeType;
            deliveryPayload["orderNumber"] = order?.OrderNumber;
        }
        if (string.Equals(platformCode, "SHOPIFY", StringComparison.OrdinalIgnoreCase))
            deliveryPayload["shopifyOrderId"] = ShopifyOrderGid(order?.ExternalOrderId);
        if (string.Equals(platformCode, "N11", StringComparison.OrdinalIgnoreCase))
            deliveryPayload["orderNumber"] = order?.OrderNumber;
        // Pazarama attaches the link to the order by its GUID, which the order snapshot keeps.
        if (string.Equals(platformCode, "PAZARAMA", StringComparison.OrdinalIgnoreCase))
        {
            deliveryPayload["orderNumber"] = order?.OrderNumber;
            deliveryPayload["pazaramaOrderId"] = PazaramaOrderId(order?.CustomerSnapshotJson);
        }
        // PTT AVM attaches the invoice to order lines: the LineItemId of every line not cancelled.
        if (string.Equals(platformCode, "PTTAVM", StringComparison.OrdinalIgnoreCase))
        {
            deliveryPayload["orderNumber"] = order?.OrderNumber;
            deliveryPayload["lineItemIds"] = await db.OrderLines.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.OrderId == package.OrderId && x.OrderedQuantity > x.CancelledQuantity)
                .OrderBy(x => x.ExternalLineId)
                .Select(x => x.ExternalLineId)
                .ToListAsync(cancellationToken);
        }
        var payload = JsonSerializer.Serialize(deliveryPayload);
        var requestHash = Hash(payload);
        if (state?.Status == "FAILED" && oneTimeAuthorization is not null
            && OneTimeInvoiceDeliveryPolicy.IsAuthorizedStageDocument(order?.OrderNumber, invoice.Id))
            state.RequestHash = requestHash;
        // REVOKED means a read-back proved the platform does not hold the invoice, so sending again cannot duplicate it and the payload may be rebuilt.
        if (state?.Status == "REVOKED" && !string.Equals(state.RequestHash, requestHash, StringComparison.Ordinal))
        {
            state.RequestHash = requestHash;
            state.UpdatedAt = timeProvider.GetUtcNow();
            await db.SaveChangesAsync(cancellationToken);
        }
        if (state is not null && !string.Equals(state.RequestHash, requestHash, StringComparison.Ordinal))
            throw new JobProcessingException(JobExecutionResult.ManualReview("DELIVERY_RETRY_PAYLOAD_CHANGED", "Önceki belirsiz teslim denemesinden sonra fatura bağlantısı payloadı değişti."));

        var environments = await db.PlatformConnections.AsNoTracking()
            .Where(connection => connection.TenantId == tenantId && (connection.Id == invoice.ProviderConnectionId || connection.Id == package.ConnectionId))
            .Select(connection => new { connection.Id, connection.Environment })
            .ToDictionaryAsync(connection => connection.Id, connection => connection.Environment, cancellationToken);
        environments.TryGetValue(invoice.ProviderConnectionId, out var providerEnvironment);
        environments.TryGetValue(package.ConnectionId, out var marketplaceEnvironment);
        if (!InvoiceDeliveryEnvironmentPolicy.IsCompatible(providerEnvironment, marketplaceEnvironment)
            && !(oneTimeAuthorization is not null
                && providerEnvironment == "STAGE" && marketplaceEnvironment == "PRODUCTION"
                && OneTimeInvoiceDeliveryPolicy.IsAuthorizedStageDocument(order?.OrderNumber, invoice.Id)))
        {
            invoice.Status = InvoiceStatus.ManualReview;
            invoice.LastErrorCode = InvoiceDeliveryEnvironmentPolicy.MismatchErrorCode;
            invoice.UpdatedAt = timeProvider.GetUtcNow();
            invoice.Version++;
            await db.SaveChangesAsync(cancellationToken);
            throw new JobProcessingException(JobExecutionResult.Blocked(
                InvoiceDeliveryEnvironmentPolicy.MismatchErrorCode,
                InvoiceDeliveryEnvironmentPolicy.DescribeMismatch(providerEnvironment, marketplaceEnvironment)));
        }

        var now = timeProvider.GetUtcNow();
        if (state is null)
        {
            state = new MarketplaceDeliveryState
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                InvoiceId = invoice.Id,
                ConnectionId = package.ConnectionId,
                PackageId = package.Id,
                ExternalIdempotencyKey = $"delivery:{invoice.Id:N}:{package.Id:N}",
                RequestHash = requestHash,
                DeliveryType = "LINK",
                Status = "STARTED",
                CreatedAt = now,
                UpdatedAt = now
            };
            db.MarketplaceDeliveryStates.Add(state);
        }

        AppendDeliveryHistory(state, "STARTED", null, null, now);
        await db.SaveChangesAsync(cancellationToken);

        AdapterResult<InvoiceDeliveryResult> result;
        try
        {
            var context = Context(tenantId, package.ConnectionId, correlationId, state.ExternalIdempotencyKey,
                oneTimeAuthorization is null ? IntegrationOperation.Automatic : IntegrationOperation.Manual) with
            {
                IsAutomaticInvoiceMarketplaceDelivery = oneTimeAuthorization is null
            };
            if (oneTimeAuthorization is not null)
                context = context with
                {
                    IsOneTimeInvoiceDeliveryAuthorized = true,
                    OneTimeInvoiceDeliveryOrderNumber = oneTimeAuthorization.OrderNumber
                };
            result = await marketplace.DeliverAsync(context, new(package.ExternalPackageId, state.DeliveryType, payload, state.RequestHash, order?.StoreFrontCode), cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            AppendDeliveryHistory(state, "UNKNOWN", null, "DELIVERY_TIMEOUT", timeProvider.GetUtcNow());
            await db.SaveChangesAsync(CancellationToken.None);
            throw new JobProcessingException(JobExecutionResult.ManualReview("DELIVERY_TIMEOUT", "Fatura bağlantısı isteği zaman aşımına uğradı; aynı dış işlem anahtarıyla uzlaştırma yapılmadan yeniden gönderilmedi.", state.ExternalIdempotencyKey));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            AppendDeliveryHistory(state, "UNKNOWN", null, "DELIVERY_CANCELLED", timeProvider.GetUtcNow());
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
        catch (Exception)
        {
            AppendDeliveryHistory(state, "UNKNOWN", null, "DELIVERY_RESULT_UNKNOWN", timeProvider.GetUtcNow());
            await db.SaveChangesAsync(CancellationToken.None);
            throw new JobProcessingException(JobExecutionResult.ManualReview("DELIVERY_RESULT_UNKNOWN", "Fatura bağlantısı sonucu kesinleşmedi; aynı dış işlem körlemesine tekrarlanmadı."));
        }

        if (!result.IsSuccess)
        {
            var disposition = InvoiceDeliveryFailurePolicy.Classify(result.Error!.Class);
            if (disposition == InvoiceDeliveryFailureDisposition.Unknown)
            {
                AppendDeliveryHistory(state, "UNKNOWN", result.Value?.ExternalReference, result.Error.Code, timeProvider.GetUtcNow());
                invoice.Status = InvoiceStatus.MarketplacePending;
                invoice.LastErrorCode = result.Error.Code;
                invoice.UpdatedAt = timeProvider.GetUtcNow();
                invoice.Version++;
                await db.SaveChangesAsync(CancellationToken.None);
                if (oneTimeAuthorization is not null)
                    throw new JobProcessingException(JobExecutionResult.ManualReview("DELIVERY_RESULT_UNKNOWN", "Tek seferlik fatura gönderiminin sonucu kesinleşmedi; aynı dış işlem körlemesine tekrarlanmadı.", result.Error.RemoteRequestId ?? state.ExternalIdempotencyKey));
                throw new JobProcessingException(JobExecutionResult.Retry("DELIVERY_RESULT_UNKNOWN", "Pazaryerindeki fatura durumu okunana kadar yeniden denenecek; her denemeden önce sipariş durumu doğrulanır.", TimeSpan.FromMinutes(2), result.Error.RemoteRequestId ?? state.ExternalIdempotencyKey));
            }

            if (oneTimeAuthorization is not null && disposition == InvoiceDeliveryFailureDisposition.Retry)
            {
                AppendDeliveryHistory(state, "FAILED", result.Value?.ExternalReference, result.Error.Code, timeProvider.GetUtcNow());
                invoice.Status = InvoiceStatus.MarketplaceFailed;
                invoice.LastErrorCode = result.Error.Code;
                invoice.UpdatedAt = timeProvider.GetUtcNow();
                invoice.Version++;
                await db.SaveChangesAsync(CancellationToken.None);
                throw new JobProcessingException(JobExecutionResult.Blocked("ONE_TIME_INVOICE_DELIVERY_FAILED", "Tek seferlik fatura iletimi reddedildi; ikinci bir dış istek otomatik olarak gönderilmedi."));
            }

            var status = disposition == InvoiceDeliveryFailureDisposition.Retry ? "RETRYABLE_FAILURE" : "FAILED";
            AppendDeliveryHistory(state, status, result.Value?.ExternalReference, result.Error.Code, timeProvider.GetUtcNow());
            invoice.Status = disposition == InvoiceDeliveryFailureDisposition.Retry ? InvoiceStatus.MarketplacePending : InvoiceStatus.MarketplaceFailed;
            invoice.LastErrorCode = result.Error.Code;
            invoice.UpdatedAt = timeProvider.GetUtcNow();
            invoice.Version++;
            await db.SaveChangesAsync(cancellationToken);
            throw JobProcessingException.FromAdapter(result.Error!);
        }

        AppendDeliveryHistory(state, "SUBMITTED", result.Value?.ExternalReference, null, timeProvider.GetUtcNow());
        invoice.Status = InvoiceStatus.MarketplacePending;
        invoice.LastErrorCode = null;
        invoice.UpdatedAt = timeProvider.GetUtcNow();
        invoice.Version++;
        await db.SaveChangesAsync(cancellationToken);
        return await ConfirmDelivery(tenantId, invoice, package, state, correlationId, cancellationToken, result.Value);
    }

    private async Task<bool> IsValidOneTimeInvoiceDeliveryAsync(
        Guid tenantId,
        Guid jobConnectionId,
        Invoice invoice,
        ShipmentPackage package,
        string? orderNumber,
        OneTimeInvoiceDeliveryAuthorization authorization,
        MarketplaceDeliveryState? state,
        CancellationToken cancellationToken)
    {
        var reconcileAcceptedButDelayedReadback = OneTimeInvoiceDeliveryPolicy.IsAuthorizedStageDocument(orderNumber, invoice.Id)
            && invoice.Status is InvoiceStatus.MarketplacePending or InvoiceStatus.ManualReview
            && state?.Status == "UNKNOWN"
            && state.ErrorCode == InvoiceDeliveryRecoveryPolicy.HepsiburadaAcceptedButReadbackMissingErrorCode;
        if (!OneTimeInvoiceDeliveryPolicy.IsAuthorizedTarget(authorization.OrderNumber)
            || !string.Equals(authorization.OrderNumber, orderNumber, StringComparison.Ordinal)
            || !InvoiceMarketplaceRetryPolicy.CanRetryDelivery(invoice.Status, invoice.LastErrorCode) && !reconcileAcceptedButDelayedReadback
            || string.IsNullOrWhiteSpace(invoice.InvoiceNumber)
            || string.IsNullOrWhiteSpace(package.ExternalPackageId)
            || package.ConnectionId != jobConnectionId)
            return false;

        var destination = await db.PlatformConnections.AsNoTracking()
            .SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == package.ConnectionId, cancellationToken);
        if (destination is null
            || !string.Equals(destination.PlatformCode, "HEPSIBURADA", StringComparison.OrdinalIgnoreCase)
            || !IntegrationRuntimePolicy.IsProduction(destination)
            || !IntegrationRuntimePolicy.IsActive(destination))
            return false;

        var sourceJob = await db.IntegrationJobs.AsNoTracking()
            .SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == authorization.SourceJobId, cancellationToken);
        var hasPriorNoWriteAttempt = sourceJob is not null
            && (sourceJob.LastErrorCode == OneTimeInvoiceDeliveryPolicy.PriorNoWriteFailureCode
                || await db.JobAttempts.AsNoTracking().AnyAsync(x =>
                    x.TenantId == tenantId
                    && x.JobId == sourceJob.Id
                    && !x.Succeeded
                    && x.ErrorCode == OneTimeInvoiceDeliveryPolicy.PriorNoWriteFailureCode,
                    cancellationToken));
        if (sourceJob is null
            || sourceJob.JobType != InvoicingJobTypes.MarketplaceDelivery
            || sourceJob.ConnectionId != package.ConnectionId
            || sourceJob.Status is not (JobStatus.Blocked or JobStatus.ManualReview or JobStatus.Dead)
            || !OneTimeInvoiceDeliveryPolicy.IsEligibleSourceFailure(sourceJob.LastErrorCode, hasPriorNoWriteAttempt)
            || FindInvoiceId(sourceJob.PayloadJson) != invoice.Id)
            return false;

        if (state is not null)
            return OneTimeInvoiceDeliveryPolicy.IsSafePriorFailure(state.Status, state.ErrorCode, state.ExternalReference)
                || state.Status is "STARTED" or "UNKNOWN" or "SUBMITTED" or "CONFIRMATION_RETRYABLE" or "CONFIRMED";

        return !await db.MarketplaceDeliveries.AsNoTracking()
            .AnyAsync(x => x.TenantId == tenantId && x.InvoiceId == invoice.Id && x.PackageId == package.Id, cancellationToken);
    }

    private static bool TryReadOneTimeInvoiceDeliveryAuthorization(string payloadJson, out OneTimeInvoiceDeliveryAuthorization? authorization)
    {
        authorization = null;
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            if (!document.RootElement.TryGetProperty("oneTimeInvoiceDelivery", out var value)) return true;
            if (value.ValueKind != JsonValueKind.Object
                || !value.TryGetProperty("orderNumber", out var orderNumber)
                || orderNumber.ValueKind != JsonValueKind.String
                || !value.TryGetProperty("sourceJobId", out var sourceJobId)
                || sourceJobId.ValueKind != JsonValueKind.String
                || !Guid.TryParse(sourceJobId.GetString(), out var parsedSourceJobId))
                return false;
            authorization = new OneTimeInvoiceDeliveryAuthorization(orderNumber.GetString()!, parsedSourceJobId);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static Guid? FindInvoiceId(string payloadJson)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            return document.RootElement.TryGetProperty("invoiceId", out var value)
                && value.ValueKind == JsonValueKind.String
                && Guid.TryParse(value.GetString(), out var invoiceId)
                    ? invoiceId
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record OneTimeInvoiceDeliveryAuthorization(string OrderNumber, Guid SourceJobId);

    private async Task<MarketplaceDeliveryState?> LoadDeliveryState(Guid tenantId, Guid invoiceId, Guid packageId, CancellationToken cancellationToken)
    {
        var state = await db.MarketplaceDeliveryStates.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.InvoiceId == invoiceId && x.PackageId == packageId, cancellationToken);
        if (state is not null) return state;

        // Bootstrap existing append-only attempts without rewriting them. New
        // processing uses the separate mutable state row from this point on.
        var latest = await db.MarketplaceDeliveries.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.InvoiceId == invoiceId && x.PackageId == packageId)
            .OrderByDescending(x => x.AttemptNumber)
            .FirstOrDefaultAsync(cancellationToken);
        if (latest is null) return null;

        state = new MarketplaceDeliveryState
        {
            Id = Guid.CreateVersion7(),
            TenantId = latest.TenantId,
            InvoiceId = latest.InvoiceId,
            ConnectionId = latest.ConnectionId,
            PackageId = latest.PackageId,
            AttemptNumber = latest.AttemptNumber,
            ExternalIdempotencyKey = latest.ExternalIdempotencyKey ?? latest.IdempotencyKey,
            RequestHash = latest.RequestHash,
            DeliveryType = latest.DeliveryType,
            Status = latest.Status,
            ExternalReference = latest.ExternalReference,
            ErrorCode = latest.ErrorCode,
            CreatedAt = latest.CreatedAt,
            UpdatedAt = latest.CreatedAt,
            CompletedAt = latest.CompletedAt
        };
        db.MarketplaceDeliveryStates.Add(state);
        return state;
    }

    private static Guid? ReadPackageId(string payloadJson)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            return document.RootElement.TryGetProperty("packageId", out var value)
                && value.ValueKind == JsonValueKind.String
                && Guid.TryParse(value.GetString(), out var packageId) ? packageId : null;
        }
        catch (JsonException) { return null; }
    }

    private async Task<PermanentInvoiceDocument?> LoadPermanentInvoiceDocumentAsync(Guid tenantId, Guid invoiceId, CancellationToken cancellationToken) =>
        await (from document in db.InvoiceDocuments.AsNoTracking()
               join asset in db.FileAssets.AsNoTracking() on new { document.TenantId, Id = document.FileAssetId } equals new { asset.TenantId, asset.Id }
               where document.TenantId == tenantId && document.InvoiceId == invoiceId && document.PermanentUrl != null
               orderby document.CreatedAt descending
               select new PermanentInvoiceDocument(document.PermanentUrl, asset.MimeType)).FirstOrDefaultAsync(cancellationToken);

    private async Task<string?> LoadPermanentInvoiceUrlAsync(Guid tenantId, Guid invoiceId, CancellationToken cancellationToken) =>
        (await LoadPermanentInvoiceDocumentAsync(tenantId, invoiceId, cancellationToken))?.PermanentUrl;

    // The order's date, last change and storefront and the package's last status time, for the marketplace read.
    private static PackageLookup? LookupFor(DateTimeOffset? orderedAt, DateTimeOffset? lastModifiedAt, string? storeFrontCode, ShipmentPackage? package) =>
        orderedAt is null ? null : new PackageLookup(package?.StatusOccurredAt, new OrderLookupHint(orderedAt, lastModifiedAt, storeFrontCode), InvoiceOnly: true);

    private static ExternalInvoiceDeliveryReference WithLookup(ExternalInvoiceDeliveryReference reference, Order? order, ShipmentPackage? package) =>
        reference with { Lookup = LookupFor(order?.OrderedAt, order?.LastRemoteModifiedAt, order?.StoreFrontCode, package) };

    private static string? ShopifyOrderGid(string? externalOrderId)
    {
        if (string.IsNullOrWhiteSpace(externalOrderId)) return null;
        if (externalOrderId.StartsWith("gid://shopify/Order/", StringComparison.Ordinal)) return externalOrderId;
        return long.TryParse(externalOrderId, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _)
            ? $"gid://shopify/Order/{externalOrderId}"
            : null;
    }

    private static bool HasExactDeliveryProof(InvoiceDeliveryStatus status, string? invoiceNumber, string? invoiceLink) =>
        InvoiceDeliveryProofPolicy.HasExactProof(status, invoiceNumber, invoiceLink);

    private sealed record PermanentInvoiceDocument(string? PermanentUrl, string? MimeType);

    private MarketplaceDelivery AppendDeliveryHistory(MarketplaceDeliveryState state, string status, string? externalReference, string? errorCode, DateTimeOffset now) =>
        InvoiceAdjustmentTracking.AppendDeliveryHistory(db, state, status, externalReference, errorCode, now);

    private async Task<bool> ConfirmDelivery(Guid tenantId, Invoice invoice, ShipmentPackage package, MarketplaceDeliveryState state, string correlationId, CancellationToken cancellationToken, InvoiceDeliveryResult? submittedResult = null)
    {
        if (string.IsNullOrWhiteSpace(state.ExternalReference))
            throw new JobProcessingException(JobExecutionResult.ManualReview("DELIVERY_REFERENCE_MISSING", "Gönderilmiş fatura bağlantısı için uzak referans bulunamadı."));

        var order = await db.Orders.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == package.OrderId).Select(x => new { x.OrderNumber, x.ExternalOrderId, x.OrderedAt, x.LastRemoteModifiedAt, x.StoreFrontCode }).SingleOrDefaultAsync(cancellationToken);
        var permanentUrl = await LoadPermanentInvoiceUrlAsync(tenantId, invoice.Id, cancellationToken);
        if (submittedResult?.SameAttemptVerified == true
            && string.Equals(submittedResult.VerifiedInvoiceNumber, invoice.InvoiceNumber, StringComparison.Ordinal)
            && string.Equals(submittedResult.VerifiedInvoiceLink, permanentUrl, StringComparison.Ordinal))
        {
            RecordConfirmedDelivery(package, invoice.InvoiceNumber, permanentUrl, timeProvider.GetUtcNow());
            AppendDeliveryHistory(state, "CONFIRMED", submittedResult.ExternalReference, null, timeProvider.GetUtcNow());
            await SetInvoiceDeliveryOutcome(tenantId, invoice, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }

        var confirmation = await marketplace.QueryDeliveryAsync(
            Context(tenantId, package.ConnectionId, correlationId, $"delivery-confirm:{state.Id:N}"),
            new(state.ExternalReference, order?.OrderNumber, ShopifyOrderGid(order?.ExternalOrderId), invoice.InvoiceNumber, permanentUrl, LookupFor(order?.OrderedAt, order?.LastRemoteModifiedAt, order?.StoreFrontCode, package)),
            cancellationToken);
        if (!confirmation.IsSuccess)
        {
            if (confirmation.Error!.Class == AdapterErrorClass.NotSupported)
            {
                // Some marketplaces expose definitive invoice state through order
                // package invoiceStatus/webhook flow, not a delivery query.
                // Do not fail the job or mark the invoice complete here.
                AppendDeliveryHistory(state, "SUBMITTED", state.ExternalReference, null, timeProvider.GetUtcNow());
                invoice.Status = InvoiceStatus.MarketplacePending;
                invoice.LastErrorCode = null;
                invoice.UpdatedAt = timeProvider.GetUtcNow();
                invoice.Version++;
                await db.SaveChangesAsync(cancellationToken);
                return true;
            }
            AppendDeliveryHistory(state, "CONFIRMATION_RETRYABLE", state.ExternalReference, confirmation.Error.Code, timeProvider.GetUtcNow());
            invoice.LastErrorCode = confirmation.Error.Code;
            invoice.UpdatedAt = timeProvider.GetUtcNow();
            invoice.Version++;
            await db.SaveChangesAsync(cancellationToken);
            throw JobProcessingException.FromAdapter(confirmation.Error);
        }

        var deliveryStatus = NormalizeRemoteStatus(confirmation.Value!.RawStatus);
        if (deliveryStatus == "INVOICE_MISMATCH")
        {
            invoice.Status = InvoiceStatus.ManualReview;
            invoice.LastErrorCode = "REMOTE_INVOICE_NUMBER_MISMATCH";
            invoice.UpdatedAt = timeProvider.GetUtcNow();
            invoice.Version++;
            AppendDeliveryHistory(state, "UNKNOWN", state.ExternalReference, invoice.LastErrorCode, timeProvider.GetUtcNow());
            await db.SaveChangesAsync(cancellationToken);
            throw new JobProcessingException(JobExecutionResult.ManualReview(invoice.LastErrorCode, "Pazaryerindeki fatura metafield'ları başka bir fatura gösteriyor; mevcut değerlerin üzerine yazılmadı."));
        }
        if (!confirmation.Value.IsTerminal || deliveryStatus is "NOT_INVOICED" or MarketplaceInvoiceStatePolicy.Unverified)
        {
            AppendDeliveryHistory(state, "CONFIRMATION_RETRYABLE", state.ExternalReference, null, timeProvider.GetUtcNow());
            invoice.LastErrorCode = null;
            invoice.UpdatedAt = timeProvider.GetUtcNow();
            invoice.Version++;
            await db.SaveChangesAsync(cancellationToken);
            throw new JobProcessingException(JobExecutionResult.Retry("DELIVERY_REMOTE_PENDING", "Fatura bağlantısı pazaryerinde işlenmeye devam ediyor.", TimeSpan.FromMinutes(2), state.ExternalReference));
        }
        if (!AcceptedDeliveryStatuses.Contains(deliveryStatus))
        {
            var errorCode = $"REMOTE_{deliveryStatus}";
            AppendDeliveryHistory(state, "FAILED", state.ExternalReference, errorCode, timeProvider.GetUtcNow());
            invoice.Status = InvoiceStatus.MarketplaceFailed;
            invoice.LastErrorCode = errorCode;
            invoice.UpdatedAt = timeProvider.GetUtcNow();
            invoice.Version++;
            await db.SaveChangesAsync(cancellationToken);
            throw new JobProcessingException(JobExecutionResult.Blocked(errorCode, "Pazaryeri fatura bağlantısı teslimini reddetti.", state.ExternalReference));
        }

        if (!HasExactDeliveryProof(confirmation.Value, invoice.InvoiceNumber, permanentUrl))
        {
            invoice.Status = InvoiceStatus.ManualReview;
            invoice.LastErrorCode = "DELIVERY_PROOF_MISSING";
            invoice.UpdatedAt = timeProvider.GetUtcNow();
            invoice.Version++;
            AppendDeliveryHistory(state, "UNKNOWN", state.ExternalReference, invoice.LastErrorCode, timeProvider.GetUtcNow());
            await db.SaveChangesAsync(cancellationToken);
            throw new JobProcessingException(JobExecutionResult.ManualReview(invoice.LastErrorCode, "Pazaryeri yalnızca genel bir 'faturalı' durumu döndürdü; bu mali faturayla eşleşen numara veya bağlantı doğrulanamadı."));
        }

        RecordConfirmedDelivery(package, invoice.InvoiceNumber, permanentUrl, timeProvider.GetUtcNow());
        AppendDeliveryHistory(state, "CONFIRMED", state.ExternalReference, null, timeProvider.GetUtcNow());
        await SetInvoiceDeliveryOutcome(tenantId, invoice, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    // A delivery proven for this exact invoice is itself the marketplace invoice state: the package shows it at once
    // instead of waiting for the next order read (n11 and PTT AVM read back only invoices sent through the API).
    private void RecordConfirmedDelivery(ShipmentPackage package, string? invoiceNumber, string? invoiceLink, DateTimeOffset at)
    {
        if (package.MarketplaceInvoiceStatus == MarketplaceInvoiceStatus.Invoiced) return;
        MarketplaceInvoiceReferenceHistoryRecorder.RecordPreviousReferences(db, package, invoiceNumber, invoiceLink, at, "DELIVERY_CONFIRMED");
        package.MarketplaceInvoiceStatus = MarketplaceInvoiceStatus.Invoiced;
        package.MarketplaceInvoiceRawStatus = "INVOICED";
        package.MarketplaceInvoiceNumber = invoiceNumber;
        package.MarketplaceInvoiceUrl = invoiceLink;
        package.MarketplaceInvoiceObservedAt = at;
        package.UpdatedAt = at;
        package.Version++;
    }

    private async Task SetInvoiceDeliveryOutcome(Guid tenantId, Invoice invoice, CancellationToken cancellationToken, DateTimeOffset? at = null)
    {
        var packageIds = invoice.PackageId is { } packageId
            ? new[] { packageId }
            : await db.ShipmentPackages.AsNoTracking()
                .Where(item => item.TenantId == tenantId && item.OrderId == invoice.OrderId && item.Status != ShipmentPackageStatus.Cancelled)
                .Select(item => item.Id).ToArrayAsync(cancellationToken);
        var persistedStates = await db.MarketplaceDeliveryStates
            .Where(state => state.TenantId == tenantId && state.InvoiceId == invoice.Id && state.PackageId != null && packageIds.Contains(state.PackageId.Value))
            .ToListAsync(cancellationToken);
        var deliveryStates = persistedStates.Concat(db.ChangeTracker.Entries<MarketplaceDeliveryState>()
            .Where(entry => (entry.State == EntityState.Added || entry.State == EntityState.Modified) && entry.Entity.TenantId == tenantId
                && entry.Entity.InvoiceId == invoice.Id && entry.Entity.PackageId.HasValue && packageIds.Contains(entry.Entity.PackageId.Value))
            .Select(entry => entry.Entity)).DistinctBy(state => state.Id).ToArray();
        var confirmedPackageIds = deliveryStates.Where(state => state.Status == "CONFIRMED" && state.PackageId.HasValue)
            .Select(state => state.PackageId!.Value).ToHashSet();
        var completed = MarketplaceInvoiceDeliveryEvidencePolicy.AllTargetsConfirmed(packageIds, confirmedPackageIds);
        var next = InvoiceDeliveryGuardPolicy.OutcomeStatus(invoice.Status, completed, !string.IsNullOrWhiteSpace(invoice.ExternalReference));
        if (next is null)
        {
            // The invoice left delivery while this job ran (cancelled, being cancelled, refused or to be adjusted): its
            // status stays. A link just confirmed for a cancelled invoice is queued for removal like the earlier ones.
            if (invoice.Status == InvoiceStatus.Cancelled)
                await InvoiceAdjustmentTracking.MarkLinkRevocationRequiredAsync(db, invoice, at ?? timeProvider.GetUtcNow(), cancellationToken);
            return;
        }
        invoice.Status = next.Value;
        invoice.LastErrorCode = null;
        invoice.UpdatedAt = at ?? timeProvider.GetUtcNow();
        invoice.Version++;
    }

    private static bool IsMicroExport(string? customerSnapshotJson) => InvoiceIssuePolicy.IsMicroExport(customerSnapshotJson);

    private async Task<bool> FetchDocument(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, CancellationToken cancellationToken)
    {
        var invoice = await FindInvoice(tenantId, payloadJson, cancellationToken);
        if (invoice is null || invoice.ProviderConnectionId != connectionId || string.IsNullOrWhiteSpace(invoice.ExternalReference)) return false;
        var result = await provider.GetDocumentAsync(Context(tenantId, connectionId, correlationId, $"document:{invoice.Id:N}"), new(invoice.ExternalReference, invoice.EttnUuid, invoice.InvoiceType), "PDF", cancellationToken);
        if (!result.IsSuccess) { invoice.LastErrorCode = result.Error!.Code; invoice.UpdatedAt = timeProvider.GetUtcNow(); invoice.Version++; await db.SaveChangesAsync(cancellationToken); throw JobProcessingException.FromAdapter(result.Error!); }
        var document = result.Value!; var hash = Convert.ToHexString(SHA256.HashData(document.Content));
        var existing = await db.InvoiceDocuments.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.InvoiceId == invoice.Id && x.DocumentType == document.DocumentKind && x.Sha256 == hash, cancellationToken);
        if (existing is not null)
        {
            existing.PermanentUrl ??= document.PermanentUrl; invoice.LastErrorCode = null; invoice.UpdatedAt = timeProvider.GetUtcNow(); invoice.Version++;
            await QueueMarketplaceDeliveryAfterDocument(tenantId, invoice, existing.PermanentUrl, correlationId, cancellationToken);
            await db.SaveChangesAsync(cancellationToken); return true;
        }
        await using var content = new MemoryStream(document.Content, writable: false); var assetId = Guid.CreateVersion7(); var stored = await files.SaveAsync(tenantId, $"{assetId:N}-{Path.GetFileName(document.FileName)}", document.MimeType, content, document.Content.LongLength, cancellationToken);
        db.FileAssets.Add(new FileAsset { Id = assetId, TenantId = tenantId, Classification = "INVOICE_DOCUMENT", RelativePath = stored, OriginalNameSafe = Path.GetFileName(document.FileName), MimeType = document.MimeType, SizeBytes = document.Content.LongLength, Sha256 = hash, Status = "ACTIVE", CreatedAt = timeProvider.GetUtcNow() });
        db.InvoiceDocuments.Add(new InvoiceDocument { Id = Guid.CreateVersion7(), TenantId = tenantId, InvoiceId = invoice.Id, DocumentType = document.DocumentKind, FileAssetId = assetId, Sha256 = hash, ExternalDocumentId = document.ExternalDocumentId, PermanentUrl = document.PermanentUrl, CreatedAt = timeProvider.GetUtcNow() });
        invoice.LastErrorCode = null; invoice.UpdatedAt = timeProvider.GetUtcNow(); invoice.Version++;
        await QueueMarketplaceDeliveryAfterDocument(tenantId, invoice, document.PermanentUrl, correlationId, cancellationToken);
        await db.SaveChangesAsync(cancellationToken); return true;
    }

    private async Task QueueMarketplaceDeliveryAfterDocument(Guid tenantId, Invoice invoice, string? permanentUrl, string correlationId, CancellationToken cancellationToken)
    {
        if (invoice.Status != InvoiceStatus.Accepted || string.IsNullOrWhiteSpace(permanentUrl)) return;
        var packages = await db.ShipmentPackages.AsNoTracking()
            .Where(item => item.TenantId == tenantId && (invoice.PackageId == null
                ? item.OrderId == invoice.OrderId && item.Status != ShipmentPackageStatus.Cancelled
                : item.Id == invoice.PackageId.Value))
            .OrderBy(item => item.Id).ToListAsync(cancellationToken);
        var marketplaceConnectionIds = packages.Select(item => item.ConnectionId).Distinct().ToArray();
        if (marketplaceConnectionIds.Length == 0) return;
        if (marketplaceConnectionIds.Length > 1)
        {
            invoice.Status = InvoiceStatus.ManualReview;
            invoice.LastErrorCode = "INVOICE_PACKAGE_CONNECTION_MISMATCH";
            invoice.UpdatedAt = timeProvider.GetUtcNow();
            invoice.Version++;
            return;
        }
        var marketplaceConnectionId = marketplaceConnectionIds[0];
        var environments = await db.PlatformConnections.AsNoTracking()
            .Where(connection => connection.TenantId == tenantId && (connection.Id == invoice.ProviderConnectionId || connection.Id == marketplaceConnectionId))
            .Select(connection => new { connection.Id, connection.Environment })
            .ToDictionaryAsync(connection => connection.Id, connection => connection.Environment, cancellationToken);
        environments.TryGetValue(invoice.ProviderConnectionId, out var providerEnvironment);
        environments.TryGetValue(marketplaceConnectionId, out var marketplaceEnvironment);
        if (!InvoiceDeliveryEnvironmentPolicy.IsCompatible(providerEnvironment, marketplaceEnvironment))
        {
            invoice.Status = InvoiceStatus.ManualReview;
            invoice.LastErrorCode = InvoiceDeliveryEnvironmentPolicy.MismatchErrorCode;
            invoice.UpdatedAt = timeProvider.GetUtcNow();
            invoice.Version++;
            return;
        }
        if (!IntegrationRuntimePolicy.AllowsAutomaticInvoiceMarketplaceDeliveryQueue(
                configuration.GetValue<bool>("FeatureFlags:ExternalWrites"),
                configuration.GetValue<bool>("FeatureFlags:InvoiceMarketplaceDeliveryWrites"))) return;
        var marketplacePlatform = await db.PlatformConnections.AsNoTracking().Where(connection => connection.TenantId == tenantId && connection.Id == marketplaceConnectionId)
            .Select(connection => connection.PlatformCode).SingleOrDefaultAsync(cancellationToken);
        // Hepsiburada's adapter reads the package before and after the write, so it does not depend on a hand-entered capability record.
        if (!string.Equals(marketplacePlatform, "HEPSIBURADA", StringComparison.OrdinalIgnoreCase)
            && !await db.PlatformCapabilities.AsNoTracking().AnyAsync(capability => capability.TenantId == tenantId
                && capability.ConnectionId == marketplaceConnectionId && capability.Code == InvoicingCapabilities.InvoiceDeliver
                && capability.SupportLevel == CapabilitySupportLevel.Supported && capability.VerifiedAt != null, cancellationToken)) return;
        await EnqueueAutomaticJob(tenantId, marketplaceConnectionId, invoice.Id, InvoicingJobTypes.MarketplaceDelivery, "after-document", correlationId, cancellationToken);
    }

    private async Task<bool> StageCapabilityProbe(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, CancellationToken cancellationToken)
    {
        var invoice = await FindInvoice(tenantId, payloadJson, cancellationToken);
        var connection = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == connectionId && x.PlatformCode == "TRENDYOL_EFATURAM", cancellationToken);
        if (invoice is null || connection is null || !string.Equals(connection.Environment, "STAGE", StringComparison.OrdinalIgnoreCase) || invoice.ProviderConnectionId != connectionId || invoice.InvoiceType != "EARSIVFATURA") return false;
        if (!string.Equals(connection.ExternalStoreId, "Ravencia - Ravencia", StringComparison.Ordinal))
            throw new JobProcessingException(JobExecutionResult.Blocked("STAGE_INVOICE_FIXTURE_REQUIRED", "Canary yalnız sabitlenmiş E-Faturam Stage test hesabında çalışır."));
        if (invoice.Status == InvoiceStatus.Submitting && string.IsNullOrWhiteSpace(invoice.ExternalReference)) await Submit(tenantId, connectionId, payloadJson, correlationId, cancellationToken, true);
        invoice = await FindInvoice(tenantId, payloadJson, cancellationToken);
        if (invoice is null) return false;
        if (invoice.Status == InvoiceStatus.Submitted) await Reconcile(tenantId, connectionId, payloadJson, correlationId, cancellationToken);
        invoice = await FindInvoice(tenantId, payloadJson, cancellationToken);
        if (invoice is null || invoice.Status != InvoiceStatus.Accepted) return false;
        if (!await db.InvoiceDocuments.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.InvoiceId == invoice.Id && x.DocumentType == "PDF", cancellationToken)) await FetchDocument(tenantId, connectionId, payloadJson, correlationId, cancellationToken);
        var checksum = await db.InvoiceDocuments.AsNoTracking().Where(x => x.TenantId == tenantId && x.InvoiceId == invoice.Id && x.DocumentType == "PDF").OrderByDescending(x => x.CreatedAt).Select(x => x.Sha256).FirstOrDefaultAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(checksum)) return false;
        var now = timeProvider.GetUtcNow();
        foreach (var code in new[] { InvoicingCapabilities.InvoiceSubmit, InvoicingCapabilities.InvoiceStatusRead, InvoicingCapabilities.InvoiceDocumentRead })
        {
            var capability = await db.PlatformCapabilities.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.Code == code, cancellationToken);
            if (capability is null) continue;
            capability.SupportLevel = CapabilitySupportLevel.Supported;
            capability.SourceUrl = code == InvoicingCapabilities.InvoiceSubmit ? "https://developers.trendyolefaturam.com/OpenApi/eAr%C5%9Fiv/create-e-archive" : code == InvoicingCapabilities.InvoiceStatusRead ? "https://developers.trendyolefaturam.com/OpenApi/eAr%C5%9Fiv/get-e-archive-status" : "https://developers.trendyolefaturam.com/OpenApi/Di%C4%9Fer/get-permanent-document-download-url";
            capability.SourceVersion = "1.0.0"; capability.Environment = connection.Environment; capability.StoreScope = connection.ExternalStoreId;
            capability.EvidenceNote = $"Auditli Stage Test Order E-Arşiv canary submit/status/PDF zinciri başarıyla doğrulandı; private fixture SHA-256 kaydedildi.";
            capability.FixtureChecksum = checksum; capability.VerifiedAt = now; capability.Version++;
            db.AuditLogs.Add(new AuditLog { TenantId = tenantId, Action = "EFATURAM_STAGE_CAPABILITY_PROBE_SUCCEEDED", TargetType = "PlatformCapability", TargetId = capability.Id.ToString("D"), Reason = $"{code}:invoice:{invoice.Id:D}", CorrelationId = correlationId, CreatedAt = now });
        }
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private async Task<bool> ScanDue(Guid tenantId, CancellationToken cancellationToken)
    {
        // DueAt is the issuing deadline (delivery + InvoiceDueDays), so only sale invoices the provider does not hold yet are overdue.
        var now = timeProvider.GetUtcNow();
        await FillMissingIssueDeadlines(tenantId, cancellationToken);
        var due = await db.Invoices.AsNoTracking().Where(x => x.TenantId == tenantId && x.SequencePurpose == "SALE" && x.ExternalReference == null && x.DueAt != null && x.DueAt < now && x.Status != InvoiceStatus.Cancelled && x.Status != InvoiceStatus.CancelledLocal && x.Status != InvoiceStatus.Rejected).Select(x => x.Id).ToListAsync(cancellationToken);
        foreach (var invoiceId in due)
        {
            var key = $"invoice-due:{invoiceId:N}"; var issue = await db.OperationalIssues.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.DedupeKey == key, cancellationToken);
            if (issue is null) db.OperationalIssues.Add(new OperationalIssue { Id = Guid.CreateVersion7(), TenantId = tenantId, DedupeKey = key, Code = "INVOICE_DUE_REVIEW", Summary = "Vadesi geçen fatura manuel inceleme bekliyor.", Status = IssueStatus.Open, FirstSeenAt = now, LastSeenAt = now, OccurrenceCount = 1 });
            else { issue.LastSeenAt = now; issue.OccurrenceCount++; }
        }
        await InvoiceAdjustmentTracking.FlagAdjustmentsAsync(db, tenantId, now, cancellationToken);
        await QueuePendingMarketplaceDeliveryRecovery(tenantId, now, cancellationToken);
        await CloseDeliveryJobsSettledElsewhere(tenantId, now, cancellationToken);
        await db.SaveChangesAsync(cancellationToken); return true;
    }

    public const string DeliverySettledElsewhereCode = "INVOICE_DELIVERY_CONFIRMED_BY_ANOTHER_JOB";

    // A delivery job that stopped for review (or gave up) while another job of the same invoice confirmed the
    // delivery has nothing left to do: it is closed here instead of waiting for an operator. Only targets whose
    // delivery state is CONFIRMED count (the job's package, or every package of an invoice-level job). The stopped
    // jobs are read newest first, a page at a time, so a backlog of older unsettled ones cannot keep new ones open;
    // each closed job raises its "jobs" event so the job screens show it closed.
    private async Task CloseDeliveryJobsSettledElsewhere(Guid tenantId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        const int pageSize = 200;
        const int maximumPages = 10;
        var since = now.AddDays(-30);
        (DateTimeOffset CreatedAt, Guid Id)? before = null;
        for (var page = 0; page < maximumPages; page++)
        {
            var query = db.IntegrationJobs
                .Where(job => job.TenantId == tenantId && job.JobType == InvoicingJobTypes.MarketplaceDelivery
                    && (job.Status == JobStatus.ManualReview || job.Status == JobStatus.Blocked || job.Status == JobStatus.Dead)
                    && job.CreatedAt >= since);
            if (before is { } position)
                query = query.Where(job => job.CreatedAt < position.CreatedAt || job.CreatedAt == position.CreatedAt && job.Id.CompareTo(position.Id) < 0);
            var stopped = await query.OrderByDescending(job => job.CreatedAt).ThenByDescending(job => job.Id).Take(pageSize).ToListAsync(cancellationToken);
            if (stopped.Count == 0) return;
            await CloseSettledDeliveryJobs(tenantId, stopped, now, cancellationToken);
            if (stopped.Count < pageSize) return;
            before = (stopped[^1].CreatedAt, stopped[^1].Id);
        }
    }

    private async Task CloseSettledDeliveryJobs(Guid tenantId, List<IntegrationJob> stopped, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var targets = stopped.Select(job => (Job: job, Target: DeliveryJobTarget(job.PayloadJson))).Where(x => x.Target is not null).ToList();
        var invoiceIds = targets.Select(x => x.Target!.Value.InvoiceId).Distinct().ToArray();
        var states = (await db.MarketplaceDeliveryStates.AsNoTracking()
                .Where(state => state.TenantId == tenantId && invoiceIds.Contains(state.InvoiceId))
                .Select(state => new { state.InvoiceId, state.PackageId, state.Status }).ToListAsync(cancellationToken))
            .ToLookup(state => state.InvoiceId);
        foreach (var (job, target) in targets)
        {
            var invoiceStates = states[target!.Value.InvoiceId].ToList();
            var settled = target.Value.PackageId is { } packageId
                ? invoiceStates.Any(state => state.PackageId == packageId && state.Status == "CONFIRMED")
                : invoiceStates.Count > 0 && invoiceStates.All(state => state.Status == "CONFIRMED");
            if (!settled) continue;
            job.Status = JobStatus.Cancelled;
            job.CompletedAt = now;
            job.LastErrorCode = DeliverySettledElsewhereCode;
            job.LastErrorSummary = "Fatura pazaryerine başka bir işle iletildi ve doğrulandı; bu iş otomatik kapatıldı.";
            job.Version++;
            db.IntegrationOutboxEvents.Add(JobLeaseService.NewJobEvent(job, now));
        }
    }

    internal static bool DeliveryJobCoversPackage(string payloadJson, Guid packageId) =>
        DeliveryJobTarget(payloadJson) is not { } target || target.PackageId is null || target.PackageId == packageId;

    private static (Guid InvoiceId, Guid? PackageId)? DeliveryJobTarget(string payloadJson)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            var root = document.RootElement;
            if (!root.TryGetProperty("invoiceId", out var invoice) || !invoice.TryGetGuid(out var invoiceId)) return null;
            Guid? packageId = root.TryGetProperty("packageId", out var package) && package.ValueKind == JsonValueKind.String && package.TryGetGuid(out var parsed) ? parsed : null;
            return (invoiceId, packageId);
        }
        catch (JsonException) { return null; }
    }

    // A draft made before its packages were delivered has no issuing deadline; once a package is delivered the deadline
    // is set here (only DueAt: it is derived, so the invoice's version and the operator's If-Match stay as they are).
    private async Task FillMissingIssueDeadlines(Guid tenantId, CancellationToken cancellationToken)
    {
        var open = await db.Invoices.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.SequencePurpose == "SALE" && x.ExternalReference == null && x.DueAt == null
                && x.Status != InvoiceStatus.Cancelled && x.Status != InvoiceStatus.CancelledLocal && x.Status != InvoiceStatus.Rejected)
            .Select(x => new { x.Id, x.OrderId, x.PackageId }).ToListAsync(cancellationToken);
        if (open.Count == 0) return;
        var orderIds = open.Select(x => x.OrderId).Distinct().ToArray();
        var packages = (await db.ShipmentPackages.AsNoTracking()
                .Where(x => x.TenantId == tenantId && orderIds.Contains(x.OrderId) && x.Status != ShipmentPackageStatus.Cancelled)
                .Select(x => new { x.Id, x.OrderId, x.Status, x.StatusOccurredAt }).ToListAsync(cancellationToken))
            .ToLookup(x => x.OrderId);
        foreach (var invoice in open)
        {
            var dueAt = InvoicingBillingService.InvoiceIssueDueAt(packages[invoice.OrderId]
                .Where(x => invoice.PackageId == null || x.Id == invoice.PackageId)
                .Select(x => (x.Status, x.StatusOccurredAt)));
            if (dueAt is null) continue;
            await db.Invoices.Where(x => x.TenantId == tenantId && x.Id == invoice.Id && x.DueAt == null)
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.DueAt, dueAt), cancellationToken);
        }
    }

    private async Task QueuePendingMarketplaceDeliveryRecovery(Guid tenantId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!IntegrationRuntimePolicy.AllowsAutomaticInvoiceMarketplaceDeliveryQueue(
                configuration.GetValue<bool>("FeatureFlags:ExternalWrites"),
                configuration.GetValue<bool>("FeatureFlags:InvoiceMarketplaceDeliveryWrites"))) return;

        var candidates = await (from state in db.MarketplaceDeliveryStates.AsNoTracking()
                                join invoice in db.Invoices.AsNoTracking()
                                    on new { state.TenantId, Id = state.InvoiceId } equals new { invoice.TenantId, invoice.Id }
                                join package in db.ShipmentPackages.AsNoTracking()
                                    on new { state.TenantId, Id = state.PackageId!.Value } equals new { package.TenantId, package.Id }
                                join connection in db.PlatformConnections.AsNoTracking()
                                    on new { package.TenantId, Id = package.ConnectionId } equals new { connection.TenantId, connection.Id }
                                where invoice.TenantId == tenantId
                                    && invoice.SequencePurpose == "SALE"
                                    && (invoice.Status == InvoiceStatus.MarketplacePending || invoice.Status == InvoiceStatus.MarketplaceFailed)
                                    && (state.Status == "REVOKED" || state.Status == "UNKNOWN" || state.Status == "CONFIRMATION_RETRYABLE" || state.Status == "RETRYABLE_FAILURE")
                                    && state.UpdatedAt <= now.AddMinutes(-2)
                                    && connection.Status == "ACTIVE"
                                    && connection.Environment == "PRODUCTION"
                                    && (connection.PlatformCode == "TRENDYOL" || connection.PlatformCode == "HEPSIBURADA" || connection.PlatformCode == "SHOPIFY" || connection.PlatformCode == "N11" || connection.PlatformCode == "PAZARAMA" || connection.PlatformCode == "PTTAVM")
                                    && db.InvoiceDocuments.Any(document => document.TenantId == invoice.TenantId && document.InvoiceId == invoice.Id && document.PermanentUrl != null)
                                select new { invoice.Id, PackageId = package.Id, ConnectionId = package.ConnectionId, StateVersion = state.Version }).ToListAsync(cancellationToken);

        var bucket = now.ToUnixTimeSeconds() / 300;
        // Sale invoices are order level (PackageId null): their targets are the order's non-cancelled packages,
        // resolved the same way EnqueueDeliveryAsync does, and they must all sit on one marketplace connection.
        var acceptedNeverDelivered = await db.Invoices.AsNoTracking()
            .Where(invoice => invoice.TenantId == tenantId
                && invoice.SequencePurpose == "SALE"
                && invoice.Status == InvoiceStatus.Accepted
                && invoice.UpdatedAt <= now.AddMinutes(-2)
                && !db.MarketplaceDeliveryStates.Any(state => state.TenantId == invoice.TenantId && state.InvoiceId == invoice.Id)
                && db.InvoiceDocuments.Any(document => document.TenantId == invoice.TenantId && document.InvoiceId == invoice.Id && document.PermanentUrl != null))
            .Select(invoice => new { invoice.Id, invoice.OrderId, invoice.PackageId })
            .ToListAsync(cancellationToken);
        // The targets, the deliverable connections and the active and earlier delivery jobs are read in a few set
        // queries instead of several queries per invoice.
        var neverOrderIds = acceptedNeverDelivered.Select(invoice => invoice.OrderId).Distinct().ToArray();
        var neverPackageIds = acceptedNeverDelivered.Where(invoice => invoice.PackageId != null).Select(invoice => invoice.PackageId!.Value).Distinct().ToArray();
        var targetPackages = acceptedNeverDelivered.Count == 0 ? [] : await db.ShipmentPackages.AsNoTracking()
            .Where(package => package.TenantId == tenantId && (neverOrderIds.Contains(package.OrderId) || neverPackageIds.Contains(package.Id)))
            .Select(package => new RecoveryTargetPackage(package.Id, package.OrderId, package.ConnectionId, package.Status)).ToListAsync(cancellationToken);
        var deliverableConnections = (await db.PlatformConnections.AsNoTracking()
            .Where(connection => connection.TenantId == tenantId
                && connection.Status == "ACTIVE"
                && connection.Environment == "PRODUCTION"
                && (connection.PlatformCode == "TRENDYOL" || connection.PlatformCode == "HEPSIBURADA" || connection.PlatformCode == "SHOPIFY" || connection.PlatformCode == "N11" || connection.PlatformCode == "PAZARAMA" || connection.PlatformCode == "PTTAVM"))
            .Select(connection => connection.Id).ToListAsync(cancellationToken)).ToHashSet();
        var activeDeliveryJobs = await db.IntegrationJobs.AsNoTracking()
            .Where(job => job.TenantId == tenantId && job.JobType == InvoicingJobTypes.MarketplaceDelivery
                && (job.Status == JobStatus.Pending || job.Status == JobStatus.Leased || job.Status == JobStatus.RetryScheduled))
            .Select(job => new { job.JobDedupKey, job.PayloadJson }).ToListAsync(cancellationToken);
        var neverQueued = new List<(Guid InvoiceId, Guid ConnectionId)>();
        foreach (var invoice in acceptedNeverDelivered)
        {
            var targetConnections = targetPackages
                .Where(package => invoice.PackageId == null
                    ? package.OrderId == invoice.OrderId && package.Status != ShipmentPackageStatus.Cancelled
                    : package.Id == invoice.PackageId.Value)
                .Select(package => package.ConnectionId).Distinct().ToList();
            if (targetConnections.Count == 1 && deliverableConnections.Contains(targetConnections[0]))
                neverQueued.Add((invoice.Id, targetConnections[0]));
        }
        foreach (var undelivered in neverQueued)
        {
            // Every delivery job is keyed "{type}:{invoiceId}:...", so the key prefix finds it.
            var keyPrefix = $"{InvoicingJobTypes.MarketplaceDelivery}:{undelivered.InvoiceId}:";
            if (activeDeliveryJobs.Any(job => job.JobDedupKey.StartsWith(keyPrefix, StringComparison.Ordinal))) continue;
            await EnqueueAutomaticJob(tenantId, undelivered.ConnectionId, undelivered.InvoiceId, InvoicingJobTypes.MarketplaceDelivery, $"never-queued:{bucket}", "scheduler-delivery-recovery", cancellationToken);
        }

        var recoveryKeys = candidates.Select(candidate => RecoveryDedupKey(candidate.Id, candidate.PackageId, candidate.StateVersion, bucket)).ToArray();
        var queuedRecoveryKeys = recoveryKeys.Length == 0 ? [] : (await db.IntegrationJobs.AsNoTracking()
            .Where(job => job.TenantId == tenantId && job.JobType == InvoicingJobTypes.MarketplaceDelivery && recoveryKeys.Contains(job.JobDedupKey))
            .Select(job => job.JobDedupKey).ToListAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            var payload = JsonSerializer.Serialize(new { invoiceId = candidate.Id, packageId = candidate.PackageId });
            // A running or waiting delivery job that covers this package settles it itself: an invoice-level job
            // ({"invoiceId"} only, every package) or a job of this very package. A job of another package of the
            // same invoice does not, so it no longer holds this package's recovery back. A job whose payload
            // cannot be read is taken as covering the whole invoice.
            var keyPrefix = $"{InvoicingJobTypes.MarketplaceDelivery}:{candidate.Id}:";
            if (activeDeliveryJobs.Any(job => job.JobDedupKey.StartsWith(keyPrefix, StringComparison.Ordinal)
                && DeliveryJobCoversPackage(job.PayloadJson, candidate.PackageId))) continue;

            var dedup = RecoveryDedupKey(candidate.Id, candidate.PackageId, candidate.StateVersion, bucket);
            if (!queuedRecoveryKeys.Add(dedup)) continue;

            db.IntegrationJobs.Add(new IntegrationJob
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                ConnectionId = candidate.ConnectionId,
                JobType = InvoicingJobTypes.MarketplaceDelivery,
                PayloadJson = payload,
                PayloadVersion = 1,
                PayloadHash = Hash(payload),
                JobDedupKey = dedup,
                EffectIdempotencyKey = dedup,
                // A recovery nobody clicked: it yields to single user actions in the user lane.
                Priority = InvoicingBillingService.InvoiceJobPriority(InvoicingJobTypes.MarketplaceDelivery, bulk: true),
                AvailableAt = now,
                CorrelationId = $"invoice-delivery-recovery:{candidate.Id:N}:{candidate.PackageId:N}",
                Version = 1
            });
        }
    }

    private sealed record RecoveryTargetPackage(Guid Id, Guid OrderId, Guid ConnectionId, ShipmentPackageStatus Status);

    private static string RecoveryDedupKey(Guid invoiceId, Guid packageId, long stateVersion, long bucket) =>
        $"{InvoicingJobTypes.MarketplaceDelivery}:{invoiceId}:{packageId}:recovery:{stateVersion}:{bucket}";

    private async Task<bool> Cancel(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, CancellationToken cancellationToken)
    {
        var invoice = await FindInvoice(tenantId, payloadJson, cancellationToken);
        if (invoice is null || invoice.ProviderConnectionId != connectionId || invoice.InvoiceType != "EARSIVFATURA" || string.IsNullOrWhiteSpace(invoice.ExternalReference)) return false;
        var result = await provider.CancelAsync(Context(tenantId, connectionId, correlationId, $"cancel:{invoice.Id:N}"), new(invoice.ExternalReference, invoice.EttnUuid, "OPERATOR_CONFIRMED"), cancellationToken);
        if (result.IsSuccess)
        {
            invoice.Status = result.Value!.CanonicalStatus == "CANCELLED" ? InvoiceStatus.Cancelled : InvoiceStatus.CancellationPending;
            invoice.LastErrorCode = null;
            // Cancelled at once: the links sent for it must come off the marketplace, as after a reconciled cancellation.
            if (invoice.Status == InvoiceStatus.Cancelled)
                await InvoiceAdjustmentTracking.MarkLinkRevocationRequiredAsync(db, invoice, timeProvider.GetUtcNow(), cancellationToken);
            if (invoice.Status == InvoiceStatus.CancellationPending)
                await EnqueueAutomaticJob(tenantId, connectionId, invoice.Id, InvoicingJobTypes.InvoiceReconcile, "after-cancellation", correlationId, cancellationToken);
        }
        else
        {
            invoice.Status = result.Error!.Class switch
            {
                AdapterErrorClass.TransientNetwork or AdapterErrorClass.RateLimit or AdapterErrorClass.Remote5xx => InvoiceStatus.CancellationPending,
                AdapterErrorClass.ContractViolation or AdapterErrorClass.InternalBug => InvoiceStatus.ManualReview,
                _ => InvoiceStatus.CancellationRejected
            };
            invoice.LastErrorCode = result.Error.Code;
        }
        invoice.UpdatedAt = timeProvider.GetUtcNow();
        invoice.Version++;
        await db.SaveChangesAsync(cancellationToken);
        if (!result.IsSuccess) throw JobProcessingException.FromAdapter(result.Error!);
        return true;
    }

    private async Task EnqueueAutomaticJob(Guid tenantId, Guid connectionId, Guid invoiceId, string jobType, string suffix, string correlationId, CancellationToken cancellationToken)
    {
        var dedup = $"{jobType}:{invoiceId}:{suffix}";
        if (await db.IntegrationJobs.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.JobType == jobType && x.JobDedupKey == dedup, cancellationToken)) return;
        var payload = JsonSerializer.Serialize(new { invoiceId });
        // Status check, document and marketplace delivery follow a submission the user waits for, so they run in
        // the user lane, at the pace of that submission: a single click's chain stays ahead of a bulk confirmation,
        // and a bulk (or scheduler-started) chain yields to single user actions.
        var needle = invoiceId.ToString();
        var submitPriority = await db.IntegrationJobs.AsNoTracking()
            .Where(job => job.TenantId == tenantId && job.JobType == InvoicingJobTypes.InvoiceSubmit && job.PayloadJson.Contains(needle))
            .OrderByDescending(job => job.CreatedAt)
            .Select(job => (int?)job.Priority)
            .FirstOrDefaultAsync(cancellationToken);
        db.IntegrationJobs.Add(new IntegrationJob { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = connectionId, JobType = jobType, PayloadJson = payload, PayloadVersion = 1, PayloadHash = Hash(payload), JobDedupKey = dedup, EffectIdempotencyKey = dedup, Priority = JobPriorityPolicy.ForFollowUp(submitPriority), TriggerType = "user", AvailableAt = timeProvider.GetUtcNow(), CorrelationId = correlationId, Version = 1 });
    }

    private static readonly HashSet<string> AcceptedDeliveryStatuses = new(StringComparer.Ordinal)
    {
        "DELIVERED", "CONFIRMED", "ACCEPTED", "SUCCESS", "SUCCEEDED", "COMPLETED", "INVOICED"
    };
    private static string NormalizeRemoteStatus(string? value) => string.IsNullOrWhiteSpace(value)
        ? "EMPTY"
        : value.Trim().Replace('-', '_').Replace(' ', '_').ToUpperInvariant();

    private async Task<Invoice?> FindInvoice(Guid tenantId, string payloadJson, CancellationToken cancellationToken)
    {
        try { using var document = JsonDocument.Parse(payloadJson); var id = document.RootElement.GetProperty("invoiceId").GetGuid(); return await db.Invoices.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, cancellationToken); }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException) { return null; }
    }
    private async Task<int> NextAttempt(Guid tenantId, Guid invoiceId, CancellationToken cancellationToken) => await db.InvoiceSubmissionAttempts.CountAsync(x => x.TenantId == tenantId && x.InvoiceId == invoiceId, cancellationToken) + 1;
    private AdapterContext Context(Guid tenantId, Guid connectionId, string correlationId, string idempotencyKey, IntegrationOperation operation = IntegrationOperation.Manual) => new(tenantId, connectionId, correlationId, idempotencyKey, timeProvider.GetUtcNow().AddMinutes(2), Operation: operation);
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
