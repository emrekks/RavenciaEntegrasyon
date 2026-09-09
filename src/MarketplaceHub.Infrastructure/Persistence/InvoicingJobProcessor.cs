using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

public sealed class InvoicingJobProcessor(AppDbContext db, IInvoiceProviderPort provider, IInvoiceMarketplacePort marketplace, IPrivateFileStorage files, TimeProvider timeProvider) : IInvoicingJobProcessor
{
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
                InvoicingJobTypes.MarketplaceDelivery => await Deliver(tenantId, payloadJson, correlationId, cancellationToken),
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
        if (invoice is null || invoice.ProviderConnectionId != connectionId || invoice.Status != InvoiceStatus.Submitting) return false;
        var lines = await db.InvoiceLines.AsNoTracking().Where(x => x.TenantId == tenantId && x.InvoiceId == invoice.Id).OrderBy(x => x.LineSequence).ToListAsync(cancellationToken);
        var order = await db.Orders.AsNoTracking().SingleAsync(x => x.TenantId == tenantId && x.Id == invoice.OrderId, cancellationToken);
        var package = invoice.PackageId is null ? null : await db.ShipmentPackages.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == invoice.PackageId, cancellationToken);
        var canonical = JsonSerializer.Serialize(new
        {
            invoice.Id,
            invoice.InvoiceType,
            invoice.Currency,
            invoice.PayableTotal,
            invoice.Note,
            IssuedAt = invoice.IssuedAt ?? invoice.UpdatedAt,
            Order = new { order.OrderNumber, order.OrderedAt, order.CustomerSnapshotJson, order.InvoiceAddressSnapshotJson, order.ShipmentAddressSnapshotJson },
            Package = package is null ? null : new { package.ExternalPackageId, package.CargoProviderExternalId, package.StatusOccurredAt },
            Lines = lines.Select(x => new { x.LineSequence, x.DescriptionSnapshot, x.SkuSnapshot, x.UnitSnapshot, x.Quantity, x.UnitPrice, x.DiscountAmount, x.VatRate, x.VatAmount, x.LineTotal })
        });
        var hash = Hash(canonical); var started = timeProvider.GetUtcNow();
        var attempt = new InvoiceSubmissionAttempt { Id = Guid.CreateVersion7(), TenantId = tenantId, InvoiceId = invoice.Id, AttemptNumber = await NextAttempt(tenantId, invoice.Id, cancellationToken), RequestHash = hash, Outcome = "STARTED", StartedAt = started };
        var context = Context(tenantId, connectionId, correlationId, invoice.IdempotencyKey) with { IsStageCapabilityProbe = isStageCapabilityProbe };
        var result = await provider.SubmitAsync(context, new(invoice.Id, invoice.Id.ToString("N"), invoice.InvoiceType, invoice.Currency, canonical, hash), cancellationToken);
        attempt.CompletedAt = timeProvider.GetUtcNow();
        if (result.IsSuccess)
        {
            attempt.Outcome = "SUCCEEDED"; attempt.ExternalReference = result.Value!.ExternalReference; attempt.RemoteRequestId = result.Value.RemoteRequestId;
            invoice.ExternalReference = result.Value.ExternalReference; invoice.InvoiceNumber = result.Value.InvoiceNumber; invoice.EttnUuid = result.Value.EttnUuid; invoice.IssuedAt ??= started; invoice.Status = InvoiceStatus.Submitted; invoice.LastErrorCode = null;
            await EnqueueAutomaticJob(tenantId, connectionId, invoice.Id, InvoicingJobTypes.InvoiceReconcile, "after-submit", correlationId, cancellationToken);
        }
        else
        {
            attempt.Outcome = "FAILED";
            attempt.ErrorClass = result.Error!.Class.ToString();
            attempt.ErrorCode = result.Error.Code;
            attempt.RemoteRequestId = result.Error.RemoteRequestId;
            invoice.Status = result.Error.Class switch
            {
                AdapterErrorClass.Validation or AdapterErrorClass.BusinessConflict => InvoiceStatus.Rejected,
                AdapterErrorClass.ContractViolation or AdapterErrorClass.InternalBug => InvoiceStatus.ManualReview,
                _ => InvoiceStatus.Submitting
            };
            invoice.LastErrorCode = result.Error.Code;
        }
        db.InvoiceSubmissionAttempts.Add(attempt); invoice.UpdatedAt = timeProvider.GetUtcNow(); invoice.Version++; await db.SaveChangesAsync(cancellationToken);
        if (!result.IsSuccess) throw JobProcessingException.FromAdapter(result.Error!);
        return true;
    }

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

    private async Task<bool> Deliver(Guid tenantId, string payloadJson, string correlationId, CancellationToken cancellationToken)
    {
        var invoice = await FindInvoice(tenantId, payloadJson, cancellationToken);
        if (invoice?.PackageId is null) return false;
        var package = await db.ShipmentPackages.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == invoice.PackageId, cancellationToken);
        var orderSnapshot = package is null
            ? null
            : await db.Orders.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == package.OrderId).Select(x => x.CustomerSnapshotJson).SingleOrDefaultAsync(cancellationToken);
        if (package is null) return false;

        var state = await LoadDeliveryState(tenantId, invoice.Id, cancellationToken);
        if (state?.Status == "CONFIRMED") return true;
        if (invoice.Status == InvoiceStatus.Completed && state is null) return true;
        if (invoice.Status == InvoiceStatus.Completed
            && state is not null
            && state.Status is not "CONFIRMED" and not "FAILED" and not "UNKNOWN")
        {
            // The order/package invoice observation is the definitive source
            // for this marketplace delivery. Do not let a queued retry
            // downgrade a completed invoice back to pending.
            AppendDeliveryHistory(state, "CONFIRMED", state.ExternalReference, null, timeProvider.GetUtcNow());
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        if (state?.Status == "SUBMITTED")
        {
            if (string.IsNullOrWhiteSpace(state.ExternalReference))
                throw new JobProcessingException(JobExecutionResult.ManualReview("DELIVERY_REFERENCE_MISSING", "Gönderilmiş fatura bağlantısı için uzak referans bulunamadı; yeniden gönderim yapılmadı."));
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
        if (state?.Status == "FAILED")
            throw new JobProcessingException(JobExecutionResult.Blocked("DELIVERY_ALREADY_FAILED", "Önceki fatura bağlantısı teslimi kalıcı olarak reddedildi; yeni dış işlem başlatılmadı."));
        if (state?.Status == "UNKNOWN")
            throw new JobProcessingException(JobExecutionResult.ManualReview("DELIVERY_RESULT_UNKNOWN", "Fatura bağlantısı sonucu kesinleşmedi; kurtarma/uzlaştırma tamamlanmadan yeniden gönderim yapılmadı."));

        var permanentUrl = await db.InvoiceDocuments.AsNoTracking().Where(x => x.TenantId == tenantId && x.InvoiceId == invoice.Id && x.PermanentUrl != null).OrderByDescending(x => x.CreatedAt).Select(x => x.PermanentUrl).FirstOrDefaultAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(permanentUrl) || !Uri.TryCreate(permanentUrl, UriKind.Absolute, out var link) || link.Scheme != Uri.UriSchemeHttps) return false;

        var payload = JsonSerializer.Serialize(new
        {
            shipmentPackageId = package.ExternalPackageId,
            invoiceLink = link.AbsoluteUri,
            invoiceDateTime = invoice.IssuedAt?.ToUnixTimeMilliseconds(),
            invoiceNumber = invoice.InvoiceNumber,
            micro = IsMicroExport(orderSnapshot)
        });
        var requestHash = Hash(payload);
        if (state is not null && !string.Equals(state.RequestHash, requestHash, StringComparison.Ordinal))
            throw new JobProcessingException(JobExecutionResult.ManualReview("DELIVERY_RETRY_PAYLOAD_CHANGED", "Önceki belirsiz teslim denemesinden sonra fatura bağlantısı payloadı değişti."));

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
            result = await marketplace.DeliverAsync(Context(tenantId, package.ConnectionId, correlationId, state.ExternalIdempotencyKey), new(package.ExternalPackageId, state.DeliveryType, payload, state.RequestHash), cancellationToken);
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
                throw new JobProcessingException(JobExecutionResult.ManualReview("DELIVERY_RESULT_UNKNOWN", "Fatura bağlantısı sonucu kesinleşmedi; aynı dış işlem körlemesine tekrarlanmadı.", result.Error.RemoteRequestId ?? state.ExternalIdempotencyKey));
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
        return await ConfirmDelivery(tenantId, invoice, package, state, correlationId, cancellationToken);
    }

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

    private async Task<bool> ConfirmDelivery(Guid tenantId, Invoice invoice, ShipmentPackage package, MarketplaceDeliveryState state, string correlationId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(state.ExternalReference))
            throw new JobProcessingException(JobExecutionResult.ManualReview("DELIVERY_REFERENCE_MISSING", "Gönderilmiş fatura bağlantısı için uzak referans bulunamadı."));

        var confirmation = await marketplace.QueryDeliveryAsync(Context(tenantId, package.ConnectionId, correlationId, $"delivery-confirm:{state.Id:N}"), new(state.ExternalReference), cancellationToken);
        if (!confirmation.IsSuccess)
        {
            if (confirmation.Error!.Class == AdapterErrorClass.NotSupported)
            {
                // Trendyol exposes the definitive state through the order
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
        if (!confirmation.Value.IsTerminal)
        {
            AppendDeliveryHistory(state, "CONFIRMATION_RETRYABLE", state.ExternalReference, null, timeProvider.GetUtcNow());
            invoice.LastErrorCode = null;
            invoice.UpdatedAt = timeProvider.GetUtcNow();
            invoice.Version++;
            await db.SaveChangesAsync(cancellationToken);
            throw new JobProcessingException(JobExecutionResult.Retry("DELIVERY_REMOTE_PENDING", "Fatura linki Trendyol tarafında işlenmeye devam ediyor.", TimeSpan.FromMinutes(2), state.ExternalReference));
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
            throw new JobProcessingException(JobExecutionResult.Blocked(errorCode, "Trendyol fatura bağlantısı teslimini reddetti.", state.ExternalReference));
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
        await db.SaveChangesAsync(cancellationToken); return true;
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
        "DELIVERED", "CONFIRMED", "ACCEPTED", "SUCCESS", "SUCCEEDED", "COMPLETED"
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
    private AdapterContext Context(Guid tenantId, Guid connectionId, string correlationId, string idempotencyKey) => new(tenantId, connectionId, correlationId, idempotencyKey, timeProvider.GetUtcNow().AddMinutes(2));
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
