using System.Security.Cryptography;
using System.Globalization;
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
            if (connection.Status == "DRAFT") connection.Status = "VERIFIED";
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
            var canonical = JsonSerializer.Serialize(new
            {
                invoice.Id,
                invoice.InvoiceType,
                invoice.Currency,
                invoice.PayableTotal,
                invoice.Note,
                IssuedAt = invoice.IssuedAt ?? invoice.UpdatedAt,
                MarketplacePlatformCode = orderConnection?.PlatformCode ?? "TRENDYOL",
                MarketplaceDisplayName = orderConnection?.DisplayName ?? "Trendyol",
                MarketplaceEnvironment = orderConnection?.Environment ?? "UNKNOWN",
                Order = new { OrderNumber = orderNumber, OrderedAt = orderedAt, CustomerSnapshotJson = customerSnapshotJson, InvoiceAddressSnapshotJson = invoiceAddressSnapshotJson, ShipmentAddressSnapshotJson = shipmentAddressSnapshotJson },
                Package = package is null ? null : new { package.ExternalPackageId, package.CargoProviderExternalId, package.StatusOccurredAt },
                Lines = lines.Select(x => new { x.LineSequence, x.DescriptionSnapshot, x.SkuSnapshot, x.UnitSnapshot, x.Quantity, x.UnitPrice, x.DiscountAmount, x.VatRate, x.VatAmount, x.LineTotal })
            });
            var hash = Hash(canonical); var started = timeProvider.GetUtcNow();
            var attempt = new InvoiceSubmissionAttempt { Id = Guid.CreateVersion7(), TenantId = tenantId, InvoiceId = invoice.Id, AttemptNumber = await NextAttempt(tenantId, invoice.Id, cancellationToken), RequestHash = hash, Outcome = "STARTED", StartedAt = started };
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
                invoice.ExternalReference = result.Value.ExternalReference; invoice.InvoiceNumber = result.Value.InvoiceNumber; invoice.EttnUuid = result.Value.EttnUuid; invoice.IssuedAt ??= started; invoice.Status = InvoiceStatus.Submitted; invoice.LastErrorCode = null;
                await EnqueueAutomaticJob(tenantId, connectionId, invoice.Id, InvoicingJobTypes.InvoiceReconcile, "after-submit", correlationId, cancellationToken);
            }
            else
            {
                var error = result.Error!;
                if (error.Class is AdapterErrorClass.TransientNetwork or AdapterErrorClass.Remote5xx or AdapterErrorClass.BusinessConflict)
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
                break;
            case "PENDING" when !remote.IsTerminal:
                invoice.Status = cancellationPending ? InvoiceStatus.CancellationPending : InvoiceStatus.Submitted;
                invoice.LastErrorCode = null;
                invoice.UpdatedAt = timeProvider.GetUtcNow();
                invoice.Version++;
                await db.SaveChangesAsync(cancellationToken);
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
        if (invoice?.PackageId is null) return false;
        using var deliveryJobPayload = JsonDocument.Parse(payloadJson);
        var manualDeliveryRetry = deliveryJobPayload.RootElement.TryGetProperty("manualDeliveryRetry", out var retryValue)
            && retryValue.ValueKind == JsonValueKind.True
            && invoice.LastErrorCode == InvoiceMarketplaceRetryPolicy.RepeatedRemoteFailure;
        var package = await db.ShipmentPackages.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == invoice.PackageId, cancellationToken);
        var order = package is null
            ? null
            : await db.Orders.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == package.OrderId).Select(x => new { x.OrderNumber, x.ExternalOrderId, x.CustomerSnapshotJson }).SingleOrDefaultAsync(cancellationToken);
        if (package is null) return false;
        var platformCode = await db.PlatformConnections.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == package.ConnectionId).Select(x => x.PlatformCode).SingleOrDefaultAsync(cancellationToken);
        var permanentDocument = await LoadPermanentInvoiceDocumentAsync(tenantId, invoice.Id, cancellationToken);
        var permanentUrl = permanentDocument?.PermanentUrl;

        var state = await LoadDeliveryState(tenantId, invoice.Id, cancellationToken);
        if (oneTimeAuthorization is not null
            && !await IsValidOneTimeInvoiceDeliveryAsync(tenantId, jobConnectionId, invoice, package, order?.OrderNumber, oneTimeAuthorization, state, cancellationToken))
            return false;
        if (oneTimeAuthorization is not null && OneTimeInvoiceDeliveryPolicy.IsAuthorizedStageDocument(order?.OrderNumber, invoice.Id)
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
                    Id = Guid.CreateVersion7(), TenantId = tenantId, InvoiceId = invoice.Id,
                    DocumentType = "MARKETPLACE_DELIVERY", FileAssetId = document.FileAssetId,
                    Sha256 = document.Sha256, CreatedAt = timeProvider.GetUtcNow()
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
                    new(state.ExternalReference ?? package.ExternalPackageId, order?.OrderNumber, orderGid, invoice.InvoiceNumber, unknownPermanentUrl),
                    cancellationToken);
                if (!liveReadback.IsSuccess) throw JobProcessingException.FromAdapter(liveReadback.Error!);
                if (HasExactDeliveryProof(liveReadback.Value!, invoice.InvoiceNumber, unknownPermanentUrl))
                {
                    AppendDeliveryHistory(state, "CONFIRMED", liveReadback.Value!.ExternalReference, null, timeProvider.GetUtcNow());
                    invoice.Status = InvoiceStatus.Completed;
                    invoice.LastErrorCode = null;
                    invoice.UpdatedAt = timeProvider.GetUtcNow();
                    invoice.Version++;
                    await db.SaveChangesAsync(cancellationToken);
                    return true;
                }
                invoice.Status = InvoiceStatus.ManualReview;
                invoice.LastErrorCode = liveReadback.Value!.RawStatus == "NOT_INVOICED" ? "DELIVERY_RESULT_UNKNOWN" : "REMOTE_INVOICE_NUMBER_MISMATCH";
                invoice.UpdatedAt = timeProvider.GetUtcNow();
                invoice.Version++;
                AppendDeliveryHistory(state, "UNKNOWN", state.ExternalReference, invoice.LastErrorCode, timeProvider.GetUtcNow());
                await db.SaveChangesAsync(cancellationToken);
                throw new JobProcessingException(JobExecutionResult.ManualReview(invoice.LastErrorCode, "Shopify'da bu mali faturayla eşleşen iki metafield doğrulanamadı. Belirsiz yazma otomatik tekrarlanmadı."));
            }
            if (string.Equals(platformCode, "HEPSIBURADA", StringComparison.OrdinalIgnoreCase))
            {
                var liveReadback = await marketplace.QueryDeliveryAsync(
                    Context(tenantId, package.ConnectionId, correlationId, $"delivery-recovery-read:{invoice.Id:N}"),
                    new(package.ExternalPackageId, order?.OrderNumber),
                    cancellationToken);
                if (!liveReadback.IsSuccess)
                    throw JobProcessingException.FromAdapter(liveReadback.Error!);

                // Hepsiburada exposes hasInvoice on order detail. Refresh the
                // decision from this live read instead of waiting for an order
                // stream that may not update MarketplaceInvoiceObservedAt.
                package.MarketplaceInvoiceStatus = MarketplaceInvoiceStatePolicy.FromRemote(liveReadback.Value!.RawStatus);
                package.MarketplaceInvoiceRawStatus = liveReadback.Value.RawStatus;
                package.MarketplaceInvoiceNumber = null;
                package.MarketplaceInvoiceObservedAt = timeProvider.GetUtcNow();
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
                    invoice.Status = InvoiceStatus.Completed;
                    invoice.LastErrorCode = null;
                    invoice.UpdatedAt = timeProvider.GetUtcNow();
                    invoice.Version++;
                    await db.SaveChangesAsync(cancellationToken);
                    return true;
                case InvoiceDeliveryRecoveryAction.RetryDelivery when oneTimeAuthorization is null:
                    if (!manualDeliveryRetry && InvoiceDeliveryRecoveryPolicy.ShouldStopAfterRemoteFailures(
                        platformCode,
                        await db.MarketplaceDeliveries.AsNoTracking().CountAsync(
                            attempt => attempt.TenantId == tenantId
                                && attempt.InvoiceId == invoice.Id
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
        var payload = JsonSerializer.Serialize(deliveryPayload);
        var requestHash = Hash(payload);
        if (state?.Status == "FAILED" && oneTimeAuthorization is not null
            && OneTimeInvoiceDeliveryPolicy.IsAuthorizedStageDocument(order?.OrderNumber, invoice.Id))
            state.RequestHash = requestHash;
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
                ExternalIdempotencyKey = $"delivery:{invoice.Id:N}",
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
            result = await marketplace.DeliverAsync(context, new(package.ExternalPackageId, state.DeliveryType, payload, state.RequestHash), cancellationToken);
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
        if (!OneTimeInvoiceDeliveryPolicy.IsAuthorizedTarget(authorization.OrderNumber)
            || !string.Equals(authorization.OrderNumber, orderNumber, StringComparison.Ordinal)
            || !InvoiceMarketplaceRetryPolicy.CanRetryDelivery(invoice.Status, invoice.LastErrorCode)
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
            .AnyAsync(x => x.TenantId == tenantId && x.InvoiceId == invoice.Id, cancellationToken);
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

    private async Task<MarketplaceDeliveryState?> LoadDeliveryState(Guid tenantId, Guid invoiceId, CancellationToken cancellationToken)
    {
        var state = await db.MarketplaceDeliveryStates.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.InvoiceId == invoiceId, cancellationToken);
        if (state is not null) return state;

        // Bootstrap existing append-only attempts without rewriting them. New
        // processing uses the separate mutable state row from this point on.
        var latest = await db.MarketplaceDeliveries.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.InvoiceId == invoiceId)
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

    private async Task<PermanentInvoiceDocument?> LoadPermanentInvoiceDocumentAsync(Guid tenantId, Guid invoiceId, CancellationToken cancellationToken) =>
        await (from document in db.InvoiceDocuments.AsNoTracking()
               join asset in db.FileAssets.AsNoTracking() on new { document.TenantId, Id = document.FileAssetId } equals new { asset.TenantId, asset.Id }
               where document.TenantId == tenantId && document.InvoiceId == invoiceId && document.PermanentUrl != null
               orderby document.CreatedAt descending
               select new PermanentInvoiceDocument(document.PermanentUrl, asset.MimeType)).FirstOrDefaultAsync(cancellationToken);

    private async Task<string?> LoadPermanentInvoiceUrlAsync(Guid tenantId, Guid invoiceId, CancellationToken cancellationToken) =>
        (await LoadPermanentInvoiceDocumentAsync(tenantId, invoiceId, cancellationToken))?.PermanentUrl;

    private static string? ShopifyOrderGid(string? externalOrderId)
    {
        if (string.IsNullOrWhiteSpace(externalOrderId)) return null;
        if (externalOrderId.StartsWith("gid://shopify/Order/", StringComparison.Ordinal)) return externalOrderId;
        return long.TryParse(externalOrderId, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _)
            ? $"gid://shopify/Order/{externalOrderId}"
            : null;
    }

    private static bool HasExactDeliveryProof(InvoiceDeliveryStatus status, string? invoiceNumber, string? invoiceLink) =>
        (!string.IsNullOrWhiteSpace(invoiceNumber)
         && string.Equals(status.VerifiedInvoiceNumber, invoiceNumber, StringComparison.Ordinal))
        || (!string.IsNullOrWhiteSpace(invoiceLink)
            && string.Equals(status.VerifiedInvoiceLink, invoiceLink, StringComparison.Ordinal));

    private sealed record PermanentInvoiceDocument(string? PermanentUrl, string? MimeType);

    private MarketplaceDelivery AppendDeliveryHistory(MarketplaceDeliveryState state, string status, string? externalReference, string? errorCode, DateTimeOffset now)
    {
        var sequence = state.AttemptNumber + 1;
        var entry = new MarketplaceDelivery
        {
            Id = Guid.CreateVersion7(),
            TenantId = state.TenantId,
            InvoiceId = state.InvoiceId,
            ConnectionId = state.ConnectionId,
            PackageId = state.PackageId,
            AttemptNumber = sequence,
            IdempotencyKey = $"delivery-event:{state.Id:N}:{sequence}",
            ExternalIdempotencyKey = state.ExternalIdempotencyKey,
            RequestHash = state.RequestHash,
            DeliveryType = state.DeliveryType,
            Status = status,
            ExternalReference = externalReference ?? state.ExternalReference,
            ErrorCode = errorCode,
            CreatedAt = now,
            CompletedAt = status == "STARTED" ? null : now
        };
        db.MarketplaceDeliveries.Add(entry);
        state.AttemptNumber = sequence;
        state.Status = status;
        state.ExternalReference = entry.ExternalReference;
        state.ErrorCode = errorCode;
        state.CompletedAt = status is "FAILED" or "CONFIRMED" ? now : null;
        state.UpdatedAt = now;
        state.Version++;
        return entry;
    }

    private async Task<bool> ConfirmDelivery(Guid tenantId, Invoice invoice, ShipmentPackage package, MarketplaceDeliveryState state, string correlationId, CancellationToken cancellationToken, InvoiceDeliveryResult? submittedResult = null)
    {
        if (string.IsNullOrWhiteSpace(state.ExternalReference))
            throw new JobProcessingException(JobExecutionResult.ManualReview("DELIVERY_REFERENCE_MISSING", "Gönderilmiş fatura bağlantısı için uzak referans bulunamadı."));

        var order = await db.Orders.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == package.OrderId).Select(x => new { x.OrderNumber, x.ExternalOrderId }).SingleOrDefaultAsync(cancellationToken);
        var permanentUrl = await LoadPermanentInvoiceUrlAsync(tenantId, invoice.Id, cancellationToken);
        if (submittedResult?.SameAttemptVerified == true
            && string.Equals(submittedResult.VerifiedInvoiceNumber, invoice.InvoiceNumber, StringComparison.Ordinal)
            && string.Equals(submittedResult.VerifiedInvoiceLink, permanentUrl, StringComparison.Ordinal))
        {
            AppendDeliveryHistory(state, "CONFIRMED", submittedResult.ExternalReference, null, timeProvider.GetUtcNow());
            invoice.Status = InvoiceStatus.Completed;
            invoice.LastErrorCode = null;
            invoice.UpdatedAt = timeProvider.GetUtcNow();
            invoice.Version++;
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }

        var confirmation = await marketplace.QueryDeliveryAsync(
            Context(tenantId, package.ConnectionId, correlationId, $"delivery-confirm:{state.Id:N}"),
            new(state.ExternalReference, order?.OrderNumber, ShopifyOrderGid(order?.ExternalOrderId), invoice.InvoiceNumber, permanentUrl),
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
        if (!confirmation.Value.IsTerminal || deliveryStatus == "NOT_INVOICED")
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

        AppendDeliveryHistory(state, "CONFIRMED", state.ExternalReference, null, timeProvider.GetUtcNow());
        invoice.Status = InvoiceStatus.Completed;
        invoice.LastErrorCode = null;
        invoice.UpdatedAt = timeProvider.GetUtcNow();
        invoice.Version++;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static bool IsMicroExport(string? customerSnapshotJson)
    {
        if (string.IsNullOrWhiteSpace(customerSnapshotJson)) return false;
        try
        {
            using var document = JsonDocument.Parse(customerSnapshotJson);
            var root = document.RootElement;
            if (root.TryGetProperty("micro", out var micro) && micro.ValueKind == JsonValueKind.True) return true;
            if (root.TryGetProperty("microExport", out var microExport) && microExport.ValueKind == JsonValueKind.True) return true;
            if (root.TryGetProperty("3pByTrendyol", out var thirdParty) && thirdParty.ValueKind == JsonValueKind.True) return true;
            foreach (var name in new[] { "shipmentPackageType", "orderType" })
                if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && (value.GetString()?.Contains("MICRO", StringComparison.OrdinalIgnoreCase) == true || value.GetString()?.Contains("İHRAC", StringComparison.OrdinalIgnoreCase) == true)) return true;
        }
        catch (JsonException) { }
        return false;
    }

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
        if (invoice.Status != InvoiceStatus.Accepted || invoice.PackageId is null || string.IsNullOrWhiteSpace(permanentUrl)) return;
        var marketplaceConnectionId = await db.ShipmentPackages.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.Id == invoice.PackageId)
            .Select(x => (Guid?)x.ConnectionId)
            .SingleOrDefaultAsync(cancellationToken);
        if (marketplaceConnectionId is null) return;
        var environments = await db.PlatformConnections.AsNoTracking()
            .Where(connection => connection.TenantId == tenantId && (connection.Id == invoice.ProviderConnectionId || connection.Id == marketplaceConnectionId.Value))
            .Select(connection => new { connection.Id, connection.Environment })
            .ToDictionaryAsync(connection => connection.Id, connection => connection.Environment, cancellationToken);
        environments.TryGetValue(invoice.ProviderConnectionId, out var providerEnvironment);
        environments.TryGetValue(marketplaceConnectionId.Value, out var marketplaceEnvironment);
        if (!InvoiceDeliveryEnvironmentPolicy.IsCompatible(providerEnvironment, marketplaceEnvironment))
        {
            invoice.Status = InvoiceStatus.ManualReview;
            invoice.LastErrorCode = InvoiceDeliveryEnvironmentPolicy.MismatchErrorCode;
            invoice.UpdatedAt = timeProvider.GetUtcNow();
            invoice.Version++;
            return;
        }
        await EnqueueAutomaticJob(tenantId, marketplaceConnectionId.Value, invoice.Id, InvoicingJobTypes.MarketplaceDelivery, "after-document", correlationId, cancellationToken);
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
        var now = timeProvider.GetUtcNow(); var due = await db.Invoices.AsNoTracking().Where(x => x.TenantId == tenantId && x.DueAt != null && x.DueAt < now && x.Status != InvoiceStatus.Cancelled && x.Status != InvoiceStatus.CancelledLocal && x.Status != InvoiceStatus.Rejected).Select(x => x.Id).ToListAsync(cancellationToken);
        foreach (var invoiceId in due)
        {
            var key = $"invoice-due:{invoiceId:N}"; var issue = await db.OperationalIssues.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.DedupeKey == key, cancellationToken);
            if (issue is null) db.OperationalIssues.Add(new OperationalIssue { Id = Guid.CreateVersion7(), TenantId = tenantId, DedupeKey = key, Code = "INVOICE_DUE_REVIEW", Summary = "Vadesi geçen fatura manuel inceleme bekliyor.", Status = IssueStatus.Open, FirstSeenAt = now, LastSeenAt = now, OccurrenceCount = 1 });
            else { issue.LastSeenAt = now; issue.OccurrenceCount++; }
        }
        await QueuePendingMarketplaceDeliveryRecovery(tenantId, now, cancellationToken);
        await db.SaveChangesAsync(cancellationToken); return true;
    }

    private async Task QueuePendingMarketplaceDeliveryRecovery(Guid tenantId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!configuration.GetValue<bool>("FeatureFlags:InvoiceMarketplaceDeliveryWrites")
            && !configuration.GetValue<bool>("FeatureFlags:ExternalWrites")) return;

        var candidates = await (from invoice in db.Invoices.AsNoTracking()
                                join package in db.ShipmentPackages.AsNoTracking()
                                    on new { invoice.TenantId, Id = invoice.PackageId!.Value } equals new { package.TenantId, package.Id }
                                join connection in db.PlatformConnections.AsNoTracking()
                                    on new { package.TenantId, Id = package.ConnectionId } equals new { connection.TenantId, connection.Id }
                                join state in db.MarketplaceDeliveryStates.AsNoTracking()
                                    on new { invoice.TenantId, InvoiceId = invoice.Id } equals new { state.TenantId, state.InvoiceId }
                                where invoice.TenantId == tenantId
                                    && invoice.Status == InvoiceStatus.MarketplacePending
                                    && (state.Status == "UNKNOWN" || state.Status == "CONFIRMATION_RETRYABLE")
                                    && state.UpdatedAt <= now.AddMinutes(-2)
                                    && connection.Status == "ACTIVE"
                                    && connection.Environment == "PRODUCTION"
                                    && (connection.PlatformCode == "TRENDYOL" || connection.PlatformCode == "HEPSIBURADA")
                                    && db.InvoiceDocuments.Any(document => document.TenantId == invoice.TenantId && document.InvoiceId == invoice.Id && document.PermanentUrl != null)
                                select new { invoice.Id, ConnectionId = package.ConnectionId, StateVersion = state.Version }).ToListAsync(cancellationToken);

        var bucket = now.ToUnixTimeSeconds() / 300;
        foreach (var candidate in candidates)
        {
            var payload = JsonSerializer.Serialize(new { invoiceId = candidate.Id });
            var activeJobExists = await db.IntegrationJobs.AsNoTracking().AnyAsync(job =>
                job.TenantId == tenantId
                && job.JobType == InvoicingJobTypes.MarketplaceDelivery
                && job.PayloadJson == payload
                && (job.Status == JobStatus.Pending || job.Status == JobStatus.Leased || job.Status == JobStatus.RetryScheduled), cancellationToken);
            if (activeJobExists) continue;

            var dedup = $"{InvoicingJobTypes.MarketplaceDelivery}:{candidate.Id}:recovery:{candidate.StateVersion}:{bucket}";
            if (await db.IntegrationJobs.AsNoTracking().AnyAsync(job => job.TenantId == tenantId && job.JobType == InvoicingJobTypes.MarketplaceDelivery && job.JobDedupKey == dedup, cancellationToken)) continue;

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
                Priority = InvoicingBillingService.InvoiceJobPriority(InvoicingJobTypes.MarketplaceDelivery),
                AvailableAt = now,
                CorrelationId = $"invoice-delivery-recovery:{candidate.Id:N}",
                Version = 1
            });
        }
    }

    private async Task<bool> Cancel(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, CancellationToken cancellationToken)
    {
        var invoice = await FindInvoice(tenantId, payloadJson, cancellationToken);
        if (invoice is null || invoice.ProviderConnectionId != connectionId || invoice.InvoiceType != "EARSIVFATURA" || string.IsNullOrWhiteSpace(invoice.ExternalReference)) return false;
        var result = await provider.CancelAsync(Context(tenantId, connectionId, correlationId, $"cancel:{invoice.Id:N}"), new(invoice.ExternalReference, invoice.EttnUuid, "OPERATOR_CONFIRMED"), cancellationToken);
        if (result.IsSuccess)
        {
            invoice.Status = result.Value!.CanonicalStatus == "CANCELLED" ? InvoiceStatus.Cancelled : InvoiceStatus.CancellationPending;
            invoice.LastErrorCode = null;
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
        db.IntegrationJobs.Add(new IntegrationJob { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = connectionId, JobType = jobType, PayloadJson = payload, PayloadVersion = 1, PayloadHash = Hash(payload), JobDedupKey = dedup, EffectIdempotencyKey = dedup, AvailableAt = timeProvider.GetUtcNow(), CorrelationId = correlationId, Version = 1 });
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
