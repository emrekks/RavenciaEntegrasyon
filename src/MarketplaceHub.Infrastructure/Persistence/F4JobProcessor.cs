using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

public sealed class F4JobProcessor(AppDbContext db, IInvoiceProviderPort provider, IInvoiceMarketplacePort marketplace, IPrivateFileStorage files, TimeProvider timeProvider) : IF4JobProcessor
{
    public async Task<JobExecutionResult> ProcessAsync(Guid tenantId, Guid? connectionId, string jobType, string payloadJson, string correlationId, CancellationToken cancellationToken)
    {
        if (jobType != F4JobTypes.InvoiceDueScan && connectionId is null)
            return JobExecutionResult.Blocked("CONNECTION_REQUIRED", "Job requires a provider connection.");
        try
        {
            var succeeded = jobType switch
            {
                F4JobTypes.ConnectionTest => await TestConnection(tenantId, connectionId!.Value, correlationId, cancellationToken),
                F4JobTypes.InvoiceSubmit => await Submit(tenantId, connectionId!.Value, payloadJson, correlationId, cancellationToken),
                F4JobTypes.InvoiceReconcile => await Reconcile(tenantId, connectionId!.Value, payloadJson, correlationId, cancellationToken),
                F4JobTypes.InvoiceDocumentFetch => await FetchDocument(tenantId, connectionId!.Value, payloadJson, correlationId, cancellationToken),
                F4JobTypes.MarketplaceDelivery => await Deliver(tenantId, payloadJson, correlationId, cancellationToken),
                F4JobTypes.InvoiceCancellation => await Cancel(tenantId, connectionId!.Value, payloadJson, correlationId, cancellationToken),
                F4JobTypes.InvoiceDueScan => await ScanDue(tenantId, cancellationToken),
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
            var capability = await db.PlatformCapabilities.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.Code == F4Capabilities.ConnectionTest, cancellationToken);
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
        connection.Version++;
        await db.SaveChangesAsync(cancellationToken);
        if (!result.IsSuccess) throw JobProcessingException.FromAdapter(result.Error!);
        return true;
    }

    private async Task<bool> Submit(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, CancellationToken cancellationToken)
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
        var result = await provider.SubmitAsync(Context(tenantId, connectionId, correlationId, invoice.IdempotencyKey), new(invoice.Id, invoice.Id.ToString("N"), invoice.InvoiceType, invoice.Currency, canonical, hash), cancellationToken);
        attempt.CompletedAt = timeProvider.GetUtcNow();
        if (result.IsSuccess)
        {
            attempt.Outcome = "SUCCEEDED"; attempt.ExternalReference = result.Value!.ExternalReference; attempt.RemoteRequestId = result.Value.RemoteRequestId;
            invoice.ExternalReference = result.Value.ExternalReference; invoice.InvoiceNumber = result.Value.InvoiceNumber; invoice.EttnUuid = result.Value.EttnUuid; invoice.IssuedAt ??= started; invoice.Status = InvoiceStatus.Submitted; invoice.LastErrorCode = null;
            var documentPayload = JsonSerializer.Serialize(new { invoiceId = invoice.Id });
            var documentDedup = $"{F4JobTypes.InvoiceDocumentFetch}:{invoice.Id}:automatic";
            if (await db.PlatformCapabilities.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.Code == F4Capabilities.InvoiceDocumentRead && x.SupportLevel == CapabilitySupportLevel.Supported, cancellationToken)
                && !await db.IntegrationJobs.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.JobType == F4JobTypes.InvoiceDocumentFetch && x.JobDedupKey == documentDedup, cancellationToken))
                db.IntegrationJobs.Add(new IntegrationJob { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = connectionId, JobType = F4JobTypes.InvoiceDocumentFetch, PayloadJson = documentPayload, PayloadVersion = 1, PayloadHash = Hash(documentPayload), JobDedupKey = documentDedup, EffectIdempotencyKey = documentDedup, AvailableAt = timeProvider.GetUtcNow(), CorrelationId = correlationId, Version = 1 });
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
        var normalized = NormalizeRemoteStatus(remote.RawStatus);
        if (AcceptedStatuses.Contains(normalized))
        {
            invoice.Status = InvoiceStatus.Accepted;
            invoice.LastErrorCode = null;
        }
        else if (RejectedStatuses.Contains(normalized))
        {
            invoice.Status = InvoiceStatus.Rejected;
            invoice.LastErrorCode = $"REMOTE_{normalized}";
        }
        else if (!remote.IsTerminal && PendingStatuses.Contains(normalized))
        {
            invoice.Status = InvoiceStatus.Submitted;
            invoice.LastErrorCode = null;
            invoice.UpdatedAt = timeProvider.GetUtcNow();
            invoice.Version++;
            await db.SaveChangesAsync(cancellationToken);
            throw new JobProcessingException(JobExecutionResult.Retry("INVOICE_REMOTE_PENDING", "Fatura sağlayıcıda işlenmeye devam ediyor.", TimeSpan.FromMinutes(2)));
        }
        else
        {
            invoice.Status = InvoiceStatus.ManualReview;
            invoice.LastErrorCode = "REMOTE_STATUS_MAPPING_REQUIRED";
            invoice.UpdatedAt = timeProvider.GetUtcNow();
            invoice.Version++;
            await db.SaveChangesAsync(cancellationToken);
            throw new JobProcessingException(JobExecutionResult.ManualReview("REMOTE_STATUS_MAPPING_REQUIRED", $"Eşlenmemiş uzak fatura durumu: {normalized}."));
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
        var permanentUrl = await db.InvoiceDocuments.AsNoTracking().Where(x => x.TenantId == tenantId && x.InvoiceId == invoice.Id && x.PermanentUrl != null).OrderByDescending(x => x.CreatedAt).Select(x => x.PermanentUrl).FirstOrDefaultAsync(cancellationToken);
        if (package is null || string.IsNullOrWhiteSpace(permanentUrl) || string.IsNullOrWhiteSpace(invoice.InvoiceNumber) || invoice.IssuedAt is null || !Uri.TryCreate(permanentUrl, UriKind.Absolute, out var link) || link.Scheme != Uri.UriSchemeHttps) return false;

        var submitted = await db.MarketplaceDeliveries
            .Where(x => x.TenantId == tenantId && x.InvoiceId == invoice.Id && x.Status == "SUBMITTED" && x.ExternalReference != null)
            .OrderByDescending(x => x.AttemptNumber)
            .FirstOrDefaultAsync(cancellationToken);
        if (submitted is not null)
            return await ConfirmDelivery(tenantId, invoice, package, submitted, correlationId, cancellationToken);

        var payload = JsonSerializer.Serialize(new { shipmentPackageId = package.ExternalPackageId, invoiceLink = link.AbsoluteUri, invoiceDateTime = invoice.IssuedAt.Value.ToUnixTimeMilliseconds(), invoiceNumber = invoice.InvoiceNumber });
        var requestHash = Hash(payload);
        var delivery = await db.MarketplaceDeliveries
            .Where(x => x.TenantId == tenantId && x.InvoiceId == invoice.Id && x.Status == "RETRYABLE_FAILURE")
            .OrderByDescending(x => x.AttemptNumber)
            .FirstOrDefaultAsync(cancellationToken);
        if (delivery is not null && !string.Equals(delivery.RequestHash, requestHash, StringComparison.Ordinal))
            throw new JobProcessingException(JobExecutionResult.ManualReview("DELIVERY_RETRY_PAYLOAD_CHANGED", "Önceki belirsiz teslim denemesinden sonra fatura bağlantısı payloadı değişti."));
        if (delivery is null)
        {
            var attemptNumber = await db.MarketplaceDeliveries.CountAsync(x => x.TenantId == tenantId && x.InvoiceId == invoice.Id, cancellationToken) + 1;
            delivery = new MarketplaceDelivery { Id = Guid.CreateVersion7(), TenantId = tenantId, InvoiceId = invoice.Id, ConnectionId = package.ConnectionId, PackageId = package.Id, AttemptNumber = attemptNumber, IdempotencyKey = $"delivery:{invoice.Id:N}:{attemptNumber}", RequestHash = requestHash, DeliveryType = "LINK", Status = "STARTED", CreatedAt = timeProvider.GetUtcNow() };
            db.MarketplaceDeliveries.Add(delivery);
        }
        else
        {
            delivery.Status = "STARTED";
            delivery.CompletedAt = null;
            delivery.ErrorCode = null;
        }
        var result = await marketplace.DeliverAsync(Context(tenantId, package.ConnectionId, correlationId, delivery.IdempotencyKey), new(package.ExternalPackageId, delivery.DeliveryType, payload, delivery.RequestHash), cancellationToken);
        delivery.ExternalReference = result.Value?.ExternalReference;
        delivery.ErrorCode = result.Error?.Code;
        if (!result.IsSuccess)
        {
            delivery.Status = result.Error!.Class is AdapterErrorClass.TransientNetwork or AdapterErrorClass.RateLimit or AdapterErrorClass.Remote5xx ? "RETRYABLE_FAILURE" : "FAILED";
            delivery.CompletedAt = timeProvider.GetUtcNow();
            invoice.Status = result.Error.Class is AdapterErrorClass.TransientNetwork or AdapterErrorClass.RateLimit or AdapterErrorClass.Remote5xx ? InvoiceStatus.MarketplacePending : InvoiceStatus.MarketplaceFailed;
            invoice.LastErrorCode = result.Error.Code;
            invoice.UpdatedAt = timeProvider.GetUtcNow();
            invoice.Version++;
            await db.SaveChangesAsync(cancellationToken);
            throw JobProcessingException.FromAdapter(result.Error!);
        }

        delivery.Status = "SUBMITTED";
        invoice.Status = InvoiceStatus.MarketplacePending;
        invoice.LastErrorCode = null;
        invoice.UpdatedAt = timeProvider.GetUtcNow();
        invoice.Version++;
        await db.SaveChangesAsync(cancellationToken);
        return await ConfirmDelivery(tenantId, invoice, package, delivery, correlationId, cancellationToken);
    }

    private async Task<bool> ConfirmDelivery(Guid tenantId, Invoice invoice, ShipmentPackage package, MarketplaceDelivery delivery, string correlationId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(delivery.ExternalReference))
            throw new JobProcessingException(JobExecutionResult.ManualReview("DELIVERY_REFERENCE_MISSING", "Gönderilmiş fatura bağlantısı için uzak referans bulunamadı."));

        var confirmation = await marketplace.QueryDeliveryAsync(Context(tenantId, package.ConnectionId, correlationId, $"delivery-confirm:{delivery.Id:N}"), new(delivery.ExternalReference), cancellationToken);
        if (!confirmation.IsSuccess)
        {
            delivery.ErrorCode = confirmation.Error!.Code;
            invoice.LastErrorCode = confirmation.Error.Code;
            invoice.UpdatedAt = timeProvider.GetUtcNow();
            invoice.Version++;
            await db.SaveChangesAsync(cancellationToken);
            if (confirmation.Error.Class == AdapterErrorClass.NotSupported)
                throw new JobProcessingException(JobExecutionResult.ManualReview("DELIVERY_CONFIRMATION_REQUIRED", "Fatura linki gönderildi; Trendyol tarafındaki kesin kabul henüz otomatik doğrulanamıyor.", delivery.ExternalReference));
            throw JobProcessingException.FromAdapter(confirmation.Error);
        }

        var deliveryStatus = NormalizeRemoteStatus(confirmation.Value!.RawStatus);
        if (!confirmation.Value.IsTerminal)
            throw new JobProcessingException(JobExecutionResult.Retry("DELIVERY_REMOTE_PENDING", "Fatura linki Trendyol tarafında işlenmeye devam ediyor.", TimeSpan.FromMinutes(2), delivery.ExternalReference));
        if (!AcceptedDeliveryStatuses.Contains(deliveryStatus))
        {
            delivery.Status = "FAILED";
            delivery.ErrorCode = $"REMOTE_{deliveryStatus}";
            delivery.CompletedAt = timeProvider.GetUtcNow();
            invoice.Status = InvoiceStatus.MarketplaceFailed;
            invoice.LastErrorCode = delivery.ErrorCode;
            invoice.UpdatedAt = timeProvider.GetUtcNow();
            invoice.Version++;
            await db.SaveChangesAsync(cancellationToken);
            throw new JobProcessingException(JobExecutionResult.Blocked(delivery.ErrorCode, "Trendyol fatura bağlantısı teslimini reddetti.", delivery.ExternalReference));
        }

        delivery.Status = "CONFIRMED";
        delivery.ErrorCode = null;
        delivery.CompletedAt = timeProvider.GetUtcNow();
        invoice.Status = InvoiceStatus.Completed;
        invoice.LastErrorCode = null;
        invoice.UpdatedAt = timeProvider.GetUtcNow();
        invoice.Version++;
        await db.SaveChangesAsync(cancellationToken);
        return true;
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
            existing.PermanentUrl ??= document.PermanentUrl; invoice.Status = invoice.Status == InvoiceStatus.Submitted ? InvoiceStatus.Accepted : invoice.Status; invoice.LastErrorCode = null; invoice.UpdatedAt = timeProvider.GetUtcNow(); invoice.Version++;
            await db.SaveChangesAsync(cancellationToken); return true;
        }
        await using var content = new MemoryStream(document.Content, writable: false); var assetId = Guid.CreateVersion7(); var stored = await files.SaveAsync(tenantId, $"{assetId:N}-{Path.GetFileName(document.FileName)}", document.MimeType, content, document.Content.LongLength, cancellationToken);
        db.FileAssets.Add(new FileAsset { Id = assetId, TenantId = tenantId, Classification = "INVOICE_DOCUMENT", RelativePath = stored, OriginalNameSafe = Path.GetFileName(document.FileName), MimeType = document.MimeType, SizeBytes = document.Content.LongLength, Sha256 = hash, Status = "ACTIVE", CreatedAt = timeProvider.GetUtcNow() });
        db.InvoiceDocuments.Add(new InvoiceDocument { Id = Guid.CreateVersion7(), TenantId = tenantId, InvoiceId = invoice.Id, DocumentType = document.DocumentKind, FileAssetId = assetId, Sha256 = hash, ExternalDocumentId = document.ExternalDocumentId, PermanentUrl = document.PermanentUrl, CreatedAt = timeProvider.GetUtcNow() });
        invoice.Status = invoice.Status == InvoiceStatus.Submitted ? InvoiceStatus.Accepted : invoice.Status; invoice.LastErrorCode = null; invoice.UpdatedAt = timeProvider.GetUtcNow(); invoice.Version++; await db.SaveChangesAsync(cancellationToken); return true;
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
        if (invoice is null || invoice.ProviderConnectionId != connectionId || string.IsNullOrWhiteSpace(invoice.ExternalReference)) return false;
        var result = await provider.CancelAsync(Context(tenantId, connectionId, correlationId, $"cancel:{invoice.Id:N}"), new(invoice.ExternalReference, invoice.EttnUuid, "OPERATOR_CONFIRMED"), cancellationToken);
        if (result.IsSuccess) invoice.Status = InvoiceStatus.Cancelled;
        else
        {
            invoice.Status = result.Error!.Class switch
            {
                AdapterErrorClass.TransientNetwork or AdapterErrorClass.RateLimit or AdapterErrorClass.Remote5xx => InvoiceStatus.CancellationPending,
                AdapterErrorClass.ContractViolation or AdapterErrorClass.InternalBug => InvoiceStatus.ManualReview,
                _ => InvoiceStatus.CancellationRejected
            };
        }
        invoice.LastErrorCode = result.Error?.Code;
        invoice.UpdatedAt = timeProvider.GetUtcNow();
        invoice.Version++;
        await db.SaveChangesAsync(cancellationToken);
        if (!result.IsSuccess) throw JobProcessingException.FromAdapter(result.Error!);
        return true;
    }

    private static readonly HashSet<string> AcceptedStatuses = new(StringComparer.Ordinal)
    {
        "ACCEPTED", "APPROVED", "COMPLETED", "SUCCESS", "SUCCEEDED", "ISSUED", "SENT"
    };
    private static readonly HashSet<string> RejectedStatuses = new(StringComparer.Ordinal)
    {
        "REJECTED", "FAILED", "ERROR", "INVALID", "CANCELLED", "CANCELED"
    };
    private static readonly HashSet<string> PendingStatuses = new(StringComparer.Ordinal)
    {
        "PENDING", "PROCESSING", "CREATED", "SUBMITTED", "WAITING", "QUEUED", "IN_PROGRESS"
    };
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
