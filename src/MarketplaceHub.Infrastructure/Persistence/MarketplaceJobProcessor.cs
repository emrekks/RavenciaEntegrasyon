using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.Hepsiburada;
using MarketplaceHub.Infrastructure.Adapters.Trendyol;
using MarketplaceHub.Infrastructure.Adapters.Trendyol.Mapping;
using MarketplaceHub.Infrastructure.Imports;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace MarketplaceHub.Infrastructure.Persistence;

public sealed class MarketplaceJobProcessor(AppDbContext db, IConnectionPort connections, IReferenceDataPort references, IProductPort products, IInventoryPricePort inventoryPrice, IOrderPort orders, IOrderPackageReadPort orderPackages, IReturnPort returns, IPrivateFileStorage files, IConfiguration configuration, TimeProvider timeProvider) : IMarketplaceJobProcessor
{
    // The payload deadline is the authoritative approval bound. The worker currently
    // applies exponential backoff, but this ceiling also keeps retry accounting from
    // becoming the earlier bound if the polling schedule is made more frequent later.
    private const int ProductApprovalReconcileMaxAttempts = (7 * 24 * 12) + 1;
    private const int MaxReferenceItems = 500_000;
    private const int MaxBrandReferenceItems = 2_000_000;
    private int telemetryRequestCount;
    private int telemetryReceivedCount;
    private int telemetryChangedCount;
    private int telemetryInsertedCount;
    private int telemetryUpdatedCount;
    private int telemetrySkippedCount;
    private int telemetryFailedCount;
    private int telemetryRetryCount;
    private int telemetryRateLimitCount;
    private int telemetryImportProcessedCount;
    private int telemetryImportSkippedCount;
    private int telemetryImportFailedCount;

    public async Task<JobExecutionResult> ProcessAsync(Guid tenantId, Guid? connectionId, string jobType, string payloadJson, string correlationId, CancellationToken cancellationToken, Guid? jobId = null)
    {
        if (connectionId is null) return JobExecutionResult.Blocked("CONNECTION_REQUIRED", "Job requires a platform connection.");
        var connectionState = await db.PlatformConnections.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == connectionId.Value).Select(x => new { x.PlatformCode, x.Status }).SingleOrDefaultAsync(cancellationToken);
        var platform = connectionState?.PlatformCode;
        if (!ActiveIntegrationScope.Contains(platform)) return JobExecutionResult.Blocked("CONNECTION_OUT_OF_SCOPE", "Connection is not active in the current integration scope.");
        var currentJobIsBootstrap = jobId is { } currentJobId
            && await db.IntegrationJobs.AsNoTracking().Where(x => x.Id == currentJobId).Select(x => x.JobDedupKey).AnyAsync(x => x.StartsWith(MarketplaceJobTypes.ActivationBootstrapPrefix), cancellationToken);
        if (!currentJobIsBootstrap && IsBootstrapManagedJob(jobType)
            && await db.IntegrationJobs.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId
                && x.JobDedupKey.StartsWith(MarketplaceJobTypes.ActivationBootstrapPrefix)
                && (x.Status == JobStatus.Pending || x.Status == JobStatus.Leased || x.Status == JobStatus.RetryScheduled), cancellationToken))
            return JobExecutionResult.Retry("INITIAL_SYNC_PENDING", "İlk kapsamlı veri aktarımı tamamlanmadan artımlı senkronizasyon başlatılmayacak.", TimeSpan.FromSeconds(30));
        // A disabled connection may still be tested so it can be reactivated, but
        // no data sync or marketplace operation may execute while it is passive.
        if (jobType is not (MarketplaceJobTypes.ConnectionTest or MarketplaceJobTypes.ShopifyConnectionTest or MarketplaceJobTypes.HepsiburadaConnectionTest) && connectionState?.Status is not ("ACTIVE" or "VERIFIED"))
            return JobExecutionResult.Blocked("CONNECTION_INACTIVE", "Bağlantı pasif olduğu için işlem çalıştırılmadı.");
        var syncLock = await MarketplaceSyncExecutionLock.TryAcquireAsync(db, connectionId.Value, jobType, cancellationToken);
        if (syncLock is null)
        {
            // Scheduled duplicates may coalesce, but another job cannot satisfy
            // a request for a specific order/package. Preserve that work for retry.
            return MarketplaceSyncExecutionLock.ContentionResult(jobType, payloadJson);
        }
        await using (syncLock)
        {
            var telemetryResource = TelemetryResource(jobType);
            var stopwatch = Stopwatch.StartNew();
            ResetTelemetry();
            if (telemetryResource is not null) await RecordSyncAttempt(tenantId, connectionId.Value, telemetryResource, cancellationToken);
            try
            {
                JobExecutionResult? directResult = null;
                if (jobType == MarketplaceJobTypes.ProductCreate) directResult = await CreateProduct(tenantId, connectionId.Value, payloadJson, correlationId, cancellationToken);
                else if (jobType == MarketplaceJobTypes.ProductApprovalReconcile) directResult = await ReconcileProductApproval(tenantId, connectionId.Value, payloadJson, correlationId, cancellationToken);
                else if (jobType == MarketplaceJobTypes.ProductUpdate) directResult = await UpdateProduct(tenantId, connectionId.Value, payloadJson, correlationId, cancellationToken);
                else if (jobType == MarketplaceJobTypes.ProductArchive) directResult = await ArchiveProduct(tenantId, connectionId.Value, payloadJson, correlationId, cancellationToken);
                else if (jobType == MarketplaceJobTypes.PriceInventorySync) directResult = await SyncPriceInventory(tenantId, connectionId.Value, payloadJson, correlationId, cancellationToken);
                else if (jobType == MarketplaceJobTypes.StockProjectionDispatch) directResult = await DispatchStockProjection(tenantId, connectionId.Value, payloadJson, correlationId, cancellationToken);
                else if (jobType == MarketplaceJobTypes.CommonLabel) directResult = await CommonLabel(tenantId, connectionId.Value, payloadJson, correlationId, cancellationToken);
                else if (jobType == MarketplaceJobTypes.CapabilityProbe) directResult = await LabelCapabilityProbe(tenantId, connectionId.Value, payloadJson, correlationId, cancellationToken);
                else if (jobType == MarketplaceJobTypes.StageTestOrder) directResult = await CreateStageTestOrder(tenantId, connectionId.Value, payloadJson, correlationId, cancellationToken);
                if (directResult is not null)
                {
                    if (!directResult.Succeeded)
                    {
                        telemetryFailedCount++;
                        if (directResult.Kind == JobCompletionKind.Retry) telemetryRetryCount++;
                    }
                    if (telemetryResource is not null) await RecordSyncCompletion(tenantId, connectionId.Value, telemetryResource, stopwatch.Elapsed, directResult.Succeeded, directResult.ErrorCode, cancellationToken);
                    return directResult;
                }
                var succeeded = jobType switch
                {
                    MarketplaceJobTypes.ConnectionTest or MarketplaceJobTypes.ShopifyConnectionTest or MarketplaceJobTypes.HepsiburadaConnectionTest => await TestConnection(tenantId, connectionId.Value, correlationId, cancellationToken),
                    MarketplaceJobTypes.ReferenceSync => await SyncReferences(tenantId, connectionId.Value, payloadJson, correlationId, cancellationToken),
                    MarketplaceJobTypes.OrderSync or MarketplaceJobTypes.ShopifyOrderSync or MarketplaceJobTypes.HepsiburadaOrderSync => await SyncOrders(tenantId, connectionId.Value, payloadJson, correlationId, "ORDERS_HOT", allowBaseline: false, cancellationToken),
                    MarketplaceJobTypes.OrderRecoverySync or MarketplaceJobTypes.ShopifyOrderRecoverySync or MarketplaceJobTypes.HepsiburadaOrderRecoverySync => await SyncOrders(tenantId, connectionId.Value, payloadJson, correlationId, "ORDERS_RECOVERY", allowBaseline: true, cancellationToken),
                    MarketplaceJobTypes.OrderStatusSync or MarketplaceJobTypes.ShopifyOrderStatusSync or MarketplaceJobTypes.HepsiburadaOrderStatusSync => await SyncOpenOrders(tenantId, connectionId.Value, correlationId, cancellationToken),
                    MarketplaceJobTypes.OrderReconciliation or MarketplaceJobTypes.ShopifyOrderReconciliation => await ReconcileOrders(tenantId, connectionId.Value, payloadJson, correlationId, cancellationToken),
                    MarketplaceJobTypes.OrderInvoiceReconciliation or MarketplaceJobTypes.ShopifyOrderInvoiceReconciliation or MarketplaceJobTypes.HepsiburadaOrderInvoiceReconciliation => await ReconcileOrderInvoices(tenantId, connectionId.Value, payloadJson, correlationId, cancellationToken),
                    MarketplaceJobTypes.ProductSync or MarketplaceJobTypes.ShopifyProductSync or MarketplaceJobTypes.HepsiburadaProductSync => await SyncProducts(tenantId, connectionId.Value, payloadJson, correlationId, jobId, cancellationToken),
                    MarketplaceJobTypes.ReturnSync or MarketplaceJobTypes.HepsiburadaReturnSync => await SyncReturns(tenantId, connectionId.Value, payloadJson, correlationId, cancellationToken),
                    MarketplaceJobTypes.ReturnStatusSync => await SyncOpenReturns(tenantId, connectionId.Value, correlationId, cancellationToken),
                    MarketplaceJobTypes.ReturnReconciliation => await ReconcileReturns(tenantId, connectionId.Value, payloadJson, correlationId, cancellationToken),
                    MarketplaceJobTypes.StockReconciliation => await ReconcileStock(tenantId, connectionId.Value, payloadJson, correlationId, cancellationToken),
                    MarketplaceJobTypes.WebhookIngest or MarketplaceJobTypes.ShopifyWebhookIngest or MarketplaceJobTypes.HepsiburadaWebhookIngest => await IngestWebhook(tenantId, connectionId.Value, payloadJson, correlationId, cancellationToken),
                    MarketplaceJobTypes.ShipmentAction => await ShipmentAction(tenantId, connectionId.Value, payloadJson, correlationId, cancellationToken),
                    MarketplaceJobTypes.ReturnAction => await ReturnAction(tenantId, connectionId.Value, payloadJson, correlationId, cancellationToken),
                    _ => false
                };
                if (telemetryResource is not null) await RecordSyncCompletion(tenantId, connectionId.Value, telemetryResource, stopwatch.Elapsed, succeeded, succeeded ? null : "F3_JOB_REJECTED", cancellationToken);
                return succeeded ? JobExecutionResult.Success() : JobExecutionResult.Blocked("F3_JOB_REJECTED", "Job payload, capability or current entity state did not permit the operation.");
            }
            catch (JobProcessingException exception)
            {
                if (exception.Result.Kind == JobCompletionKind.Retry) telemetryRetryCount++;
                else telemetryFailedCount++;
                if (telemetryResource is not null) await RecordSyncCompletion(tenantId, connectionId.Value, telemetryResource, stopwatch.Elapsed, false, exception.Result.ErrorCode, cancellationToken);
                return exception.Result;
            }
            catch (Exception exception)
            {
                telemetryFailedCount++;
                telemetryRetryCount++;
                if (telemetryResource is not null) await RecordSyncCompletion(tenantId, connectionId.Value, telemetryResource, stopwatch.Elapsed, false, exception.GetType().Name, cancellationToken);
                throw;
            }
        }
    }

    private static string? TelemetryResource(string jobType) => jobType switch
    {
        MarketplaceJobTypes.ConnectionTest or MarketplaceJobTypes.ShopifyConnectionTest or MarketplaceJobTypes.HepsiburadaConnectionTest => "CONNECTION_TEST",
        MarketplaceJobTypes.ReferenceSync => "REFERENCE_DATA",
        MarketplaceJobTypes.OrderSync or MarketplaceJobTypes.ShopifyOrderSync or MarketplaceJobTypes.HepsiburadaOrderSync => "ORDERS_HOT",
        MarketplaceJobTypes.OrderRecoverySync or MarketplaceJobTypes.ShopifyOrderRecoverySync or MarketplaceJobTypes.HepsiburadaOrderRecoverySync => "ORDERS_RECOVERY",
        MarketplaceJobTypes.OrderStatusSync or MarketplaceJobTypes.ShopifyOrderStatusSync or MarketplaceJobTypes.HepsiburadaOrderStatusSync => "ORDER_LIFECYCLE",
        MarketplaceJobTypes.OrderReconciliation or MarketplaceJobTypes.ShopifyOrderReconciliation => "ORDER_RECONCILIATION",
        MarketplaceJobTypes.OrderInvoiceReconciliation or MarketplaceJobTypes.ShopifyOrderInvoiceReconciliation or MarketplaceJobTypes.HepsiburadaOrderInvoiceReconciliation => "ORDER_INVOICE_RECONCILIATION",
        MarketplaceJobTypes.ReturnSync or MarketplaceJobTypes.HepsiburadaReturnSync => "RETURNS",
        MarketplaceJobTypes.ReturnStatusSync => "RETURN_LIFECYCLE",
        MarketplaceJobTypes.ReturnReconciliation => "RETURN_RECONCILIATION",
        MarketplaceJobTypes.ProductSync or MarketplaceJobTypes.ShopifyProductSync or MarketplaceJobTypes.HepsiburadaProductSync => "PRODUCTS",
        MarketplaceJobTypes.ProductCreate => "PRODUCT_CREATE",
        MarketplaceJobTypes.ProductApprovalReconcile => "PRODUCT_APPROVAL",
        MarketplaceJobTypes.ProductUpdate => "PRODUCT_UPDATE",
        MarketplaceJobTypes.ProductArchive => "PRODUCT_ARCHIVE",
        MarketplaceJobTypes.PriceInventorySync => "PRICE_INVENTORY",
        MarketplaceJobTypes.StockProjectionDispatch => "STOCK_PROJECTION",
        MarketplaceJobTypes.StockReconciliation => "STOCK_RECONCILIATION",
        MarketplaceJobTypes.WebhookIngest or MarketplaceJobTypes.ShopifyWebhookIngest or MarketplaceJobTypes.HepsiburadaWebhookIngest => "WEBHOOK_INGEST",
        MarketplaceJobTypes.ShipmentAction => "SHIPMENT_ACTION",
        MarketplaceJobTypes.ReturnAction => "RETURN_ACTION",
        MarketplaceJobTypes.CommonLabel => "COMMON_LABEL",
        MarketplaceJobTypes.CapabilityProbe => "CAPABILITY_PROBE",
        MarketplaceJobTypes.StageTestOrder => "STAGE_TEST_ORDER",
        _ => null
    };

    private static bool IsBootstrapManagedJob(string jobType) => jobType is
        MarketplaceJobTypes.ReferenceSync
        or MarketplaceJobTypes.OrderSync
        or MarketplaceJobTypes.ShopifyOrderSync
        or MarketplaceJobTypes.HepsiburadaOrderSync
        or MarketplaceJobTypes.OrderRecoverySync
        or MarketplaceJobTypes.ShopifyOrderRecoverySync
        or MarketplaceJobTypes.HepsiburadaOrderRecoverySync
        or MarketplaceJobTypes.OrderStatusSync
        or MarketplaceJobTypes.ShopifyOrderStatusSync
        or MarketplaceJobTypes.HepsiburadaOrderStatusSync
        or MarketplaceJobTypes.OrderReconciliation
        or MarketplaceJobTypes.ShopifyOrderReconciliation
        or MarketplaceJobTypes.OrderInvoiceReconciliation
        or MarketplaceJobTypes.ShopifyOrderInvoiceReconciliation
        or MarketplaceJobTypes.HepsiburadaOrderInvoiceReconciliation
        or MarketplaceJobTypes.ProductSync
        or MarketplaceJobTypes.ShopifyProductSync
        or MarketplaceJobTypes.HepsiburadaProductSync
        or MarketplaceJobTypes.ReturnSync
        or MarketplaceJobTypes.HepsiburadaReturnSync
        or MarketplaceJobTypes.ReturnStatusSync
        or MarketplaceJobTypes.ReturnReconciliation
        or MarketplaceJobTypes.StockReconciliation;

    private async Task RecordSyncAttempt(Guid tenantId, Guid connectionId, string resourceType, CancellationToken cancellationToken)
    {
        var cursor = await Cursor(tenantId, connectionId, resourceType, cancellationToken);
        cursor.LastAttemptAt = timeProvider.GetUtcNow();
        cursor.Version++;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task RecordSyncCompletion(Guid tenantId, Guid connectionId, string resourceType, TimeSpan duration, bool succeeded, string? error, CancellationToken cancellationToken)
    {
        if (!succeeded) db.ChangeTracker.Clear();
        var cursor = await Cursor(tenantId, connectionId, resourceType, cancellationToken);
        cursor.LastDurationMs = Math.Max(0, (long)duration.TotalMilliseconds);
        if (succeeded)
        {
            cursor.LastSuccessAt = timeProvider.GetUtcNow();
            cursor.LastError = null;
            cursor.LastErrorAt = null;
            cursor.ConsecutiveFailureCount = 0;
            cursor.LastFailedCount = telemetryFailedCount;
        }
        else
        {
            cursor.LastError = Short(error, 1024);
            cursor.LastErrorAt = timeProvider.GetUtcNow();
            cursor.ConsecutiveFailureCount++;
            cursor.LastFailedCount = Math.Max(1, telemetryFailedCount);
        }
        cursor.LastRequestCount = telemetryRequestCount;
        cursor.LastReceivedCount = telemetryReceivedCount;
        cursor.LastChangedCount = telemetryChangedCount;
        cursor.LastInsertedCount = telemetryInsertedCount;
        cursor.LastUpdatedCount = telemetryUpdatedCount;
        cursor.LastSkippedCount = telemetrySkippedCount;
        cursor.LastRetryCount = telemetryRetryCount + (succeeded ? 0 : 1);
        cursor.LastRateLimitCount = telemetryRateLimitCount;
        cursor.Version++;
        await db.SaveChangesAsync(cancellationToken);
    }

    private void ResetTelemetry()
    {
        telemetryRequestCount = 0;
        telemetryReceivedCount = 0;
        telemetryChangedCount = 0;
        telemetryInsertedCount = 0;
        telemetryUpdatedCount = 0;
        telemetrySkippedCount = 0;
        telemetryFailedCount = 0;
        telemetryRetryCount = 0;
        telemetryRateLimitCount = 0;
        telemetryImportProcessedCount = 0;
        telemetryImportSkippedCount = 0;
        telemetryImportFailedCount = 0;
    }

    private void TrackRequest() => telemetryRequestCount++;
    private void TrackReceived(bool changed = true)
    {
        telemetryReceivedCount++;
        if (changed) telemetryChangedCount++;
        else telemetrySkippedCount++;
    }
    private void TrackSkipped() => telemetrySkippedCount++;
    private void TrackResultFailure(AdapterError? error)
    {
        telemetryFailedCount++;
        if (error?.Class == AdapterErrorClass.RateLimit) telemetryRateLimitCount++;
        if (error?.Class is AdapterErrorClass.TransientNetwork or AdapterErrorClass.RateLimit or AdapterErrorClass.Remote5xx) telemetryRetryCount++;
    }

    private async Task<JobExecutionResult> CreateProduct(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, CancellationToken cancellationToken)
    {
        ProductPublicationJobPayload? payload;
        try { payload = JsonSerializer.Deserialize<ProductPublicationJobPayload>(payloadJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
        catch (JsonException) { return JobExecutionResult.Blocked("PRODUCT_PUBLICATION_PAYLOAD_INVALID", "Ürün yayınlama işi payload sözleşmesini sağlamıyor."); }
        if (payload is null || payload.JobId == Guid.Empty || payload.ProductId == Guid.Empty || payload.ProfileId == Guid.Empty || string.IsNullOrWhiteSpace(payload.PayloadHash) || string.IsNullOrWhiteSpace(payload.PayloadJson)) return JobExecutionResult.Blocked("PRODUCT_PUBLICATION_PAYLOAD_INVALID", "Ürün yayınlama işi zorunlu alanları eksik.");

        var job = await db.IntegrationJobs.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == payload.JobId && x.ConnectionId == connectionId && x.JobType == MarketplaceJobTypes.ProductCreate, cancellationToken);
        var profile = await db.ChannelListingProfiles.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == payload.ProfileId && x.ProductId == payload.ProductId && x.ConnectionId == connectionId, cancellationToken);
        if (job is null || profile is null) return JobExecutionResult.Blocked("PRODUCT_PUBLICATION_STATE_MISSING", "Yayın işi veya listing profile bulunamadı.");
        var platformCode = await db.PlatformConnections.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == connectionId).Select(x => x.PlatformCode).SingleOrDefaultAsync(cancellationToken);

        if (string.Equals(payload.Phase, "SUBMIT", StringComparison.OrdinalIgnoreCase))
        {
            if (!await ExternalWriteMasterAllowedAsync(tenantId, connectionId, cancellationToken)) return await MarkPublicationResult(tenantId, connectionId, profile, "BLOCKED", "EXTERNAL_WRITES_DISABLED", JobExecutionResult.Blocked("EXTERNAL_WRITES_DISABLED", "Dış yazma anahtarı kapalı; pazar yeri isteği gönderilmedi."), cancellationToken);
            if (string.Equals(platformCode, "HEPSIBURADA", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.Equals(configuration["Hepsiburada:AuthenticationMode"], "BASIC", StringComparison.OrdinalIgnoreCase)) return await MarkPublicationResult(tenantId, connectionId, profile, "BLOCKED", "HEPSIBURADA_AUTHENTICATION_UNVERIFIED", JobExecutionResult.Blocked("HEPSIBURADA_AUTHENTICATION_UNVERIFIED", "Hepsiburada auth biçimi doğrulanmadı; ürün aktarımı gönderilmedi."), cancellationToken);
                if (!await HasHepsiburadaPublicationEvidenceAsync(tenantId, connectionId, cancellationToken)) return await MarkPublicationResult(tenantId, connectionId, profile, "BLOCKED", "HEPSIBURADA_WRITE_CAPABILITY_EVIDENCE_REQUIRED", JobExecutionResult.Blocked("HEPSIBURADA_WRITE_CAPABILITY_EVIDENCE_REQUIRED", "PRODUCT_WRITE, PRICE_WRITE ve INVENTORY_WRITE SIT kanıtı bulunamadı; ürün aktarımı gönderilmedi."), cancellationToken);
            }
            var existingEffect = await db.ExternalEffectRecords.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.EffectType == MarketplaceJobTypes.ProductCreate && x.IdempotencyKey == job.EffectIdempotencyKey, cancellationToken);
            if (existingEffect is not null) return await MarkPublicationResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "EXTERNAL_EFFECT_AMBIGUOUS", JobExecutionResult.ManualReview("EXTERNAL_EFFECT_AMBIGUOUS", "Önceki dış yazmanın sonucu kesinleştirilemedi; tekrar gönderim engellendi."), cancellationToken);

            var effect = new ExternalEffectRecord { Id = Guid.CreateVersion7(), TenantId = tenantId, EffectType = MarketplaceJobTypes.ProductCreate, IdempotencyKey = job.EffectIdempotencyKey, CreatedAt = timeProvider.GetUtcNow() };
            db.ExternalEffectRecords.Add(effect);
            await db.SaveChangesAsync(cancellationToken);

            TrackRequest();
            var submit = await products.CreateAsync(Context(tenantId, connectionId, correlationId, job.EffectIdempotencyKey), new ProductPublication(payload.ProductId, payload.PayloadHash, payload.PayloadJson), cancellationToken);
            if (!submit.IsSuccess)
            {
                TrackResultFailure(submit.Error);
                var error = submit.Error!;
                if (error.Class is AdapterErrorClass.TransientNetwork or AdapterErrorClass.Remote5xx or AdapterErrorClass.ContractViolation or AdapterErrorClass.InternalBug)
                    return await MarkPublicationResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "EXTERNAL_EFFECT_AMBIGUOUS", JobExecutionResult.ManualReview("EXTERNAL_EFFECT_AMBIGUOUS", "Create çağrısının uzak tarafta uygulanıp uygulanmadığı kesinleştirilemedi.", error.RemoteRequestId), cancellationToken);
                db.ExternalEffectRecords.Remove(effect);
                await db.SaveChangesAsync(cancellationToken);
                var adapterResult = JobExecutionResult.FromAdapterError(error);
                var status = adapterResult.Kind == JobCompletionKind.Retry ? "RETRY_SCHEDULED" : adapterResult.Kind == JobCompletionKind.ManualReview ? "MANUAL_REVIEW" : "BLOCKED";
                return await MarkPublicationResult(tenantId, connectionId, profile, status, error.Code, adapterResult, cancellationToken);
            }

            var operation = submit.Value!;
            effect.CompletedAt = timeProvider.GetUtcNow();
            var next = payload with { Phase = "POLL", ExternalOperationId = operation.ExternalOperationId, SubmittedAt = operation.SubmittedAt };
            job.PayloadJson = JsonSerializer.Serialize(next);
            job.PayloadHash = Hash(job.PayloadJson);
            profile.ActualStatus = string.Equals(platformCode, "HEPSIBURADA", StringComparison.OrdinalIgnoreCase) ? "IMPORT_SUBMITTED" : "BATCH_SUBMITTED";
            profile.LastRejectionCode = null;
            profile.Version++;
            await SetListingStatus(tenantId, connectionId, profile.Id, profile.ActualStatus, null, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            return JobExecutionResult.Retry("PRODUCT_BATCH_PENDING", string.Equals(platformCode, "HEPSIBURADA", StringComparison.OrdinalIgnoreCase) ? "Hepsiburada ürün import sonucu bekleniyor." : "Trendyol create batch sonucu bekleniyor.", TimeSpan.FromSeconds(15), operation.ExternalOperationId);
        }

        if (!string.Equals(payload.Phase, "POLL", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(payload.ExternalOperationId) || payload.SubmittedAt is null) return JobExecutionResult.Blocked("PRODUCT_PUBLICATION_PHASE_INVALID", "Yayın işi bilinmeyen bir fazda.");
        if (timeProvider.GetUtcNow() - payload.SubmittedAt.Value > TimeSpan.FromHours(4)) return await MarkPublicationResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "PRODUCT_BATCH_RESULT_EXPIRED", JobExecutionResult.ManualReview("PRODUCT_BATCH_RESULT_EXPIRED", "Batch sonucu dört saatlik sorgulama penceresinde tamamlanamadı.", payload.ExternalOperationId), cancellationToken);

        TrackRequest();
        var operationResult = await products.GetOperationAsync(Context(tenantId, connectionId, correlationId, $"{job.EffectIdempotencyKey}:poll"), payload.ExternalOperationId, cancellationToken);
        if (!operationResult.IsSuccess)
        {
            TrackResultFailure(operationResult.Error);
            var adapterResult = JobExecutionResult.FromAdapterError(operationResult.Error!);
            if (adapterResult.Kind == JobCompletionKind.Retry) return adapterResult;
            var status = adapterResult.Kind == JobCompletionKind.ManualReview ? "MANUAL_REVIEW" : "BLOCKED";
            return await MarkPublicationResult(tenantId, connectionId, profile, status, operationResult.Error!.Code, adapterResult, cancellationToken);
        }

        var operationStatus = operationResult.Value!;
        if (string.Equals(operationStatus.Status, "IN_PROGRESS", StringComparison.OrdinalIgnoreCase))
        {
            profile.ActualStatus = string.Equals(platformCode, "HEPSIBURADA", StringComparison.OrdinalIgnoreCase) ? "IMPORT_IN_PROGRESS" : "BATCH_IN_PROGRESS";
            profile.Version++;
            await SetListingStatus(tenantId, connectionId, profile.Id, profile.ActualStatus, null, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            return JobExecutionResult.Retry("PRODUCT_BATCH_PENDING", string.Equals(platformCode, "HEPSIBURADA", StringComparison.OrdinalIgnoreCase) ? "Hepsiburada ürün import işlemi sürüyor." : "Trendyol create batch işlemi sürüyor.", TimeSpan.FromSeconds(15), payload.ExternalOperationId);
        }
        if (!string.Equals(operationStatus.Status, "COMPLETED", StringComparison.OrdinalIgnoreCase)) return await MarkPublicationResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "PRODUCT_BATCH_STATUS_UNKNOWN", JobExecutionResult.ManualReview("PRODUCT_BATCH_STATUS_UNKNOWN", "Batch servisi tanınmayan bir durum döndürdü.", payload.ExternalOperationId), cancellationToken);

        var listings = await db.ChannelListingVariants.Where(x => x.TenantId == tenantId && x.ProfileId == profile.Id).ToListAsync(cancellationToken);
        var listingVariantIds = listings.Select(x => x.VariantId).ToArray();
        var states = await db.MarketplaceListingStates.Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && listingVariantIds.Contains(x.VariantId)).ToDictionaryAsync(x => x.VariantId, cancellationToken);
        var isHepsiburada = string.Equals(platformCode, "HEPSIBURADA", StringComparison.OrdinalIgnoreCase);
        var lines = operationStatus.Lines.Where(x => !string.IsNullOrWhiteSpace(x.ExternalKey)).ToList();
        if (lines.Count == 0 || lines.GroupBy(x => x.ExternalKey, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1)) return await MarkPublicationResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "PRODUCT_BATCH_CONTRACT_INVALID", JobExecutionResult.ManualReview("PRODUCT_BATCH_CONTRACT_INVALID", "Tamamlanan batch sonucu eksik veya yinelenen merchantSku/barkod satırları içeriyor.", payload.ExternalOperationId), cancellationToken);
        var resultKeys = listings.Select(x => isHepsiburada ? x.ExternalSku ?? "" : x.ExternalBarcode ?? "").ToList();
        if (resultKeys.Any(string.IsNullOrWhiteSpace) || resultKeys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != listings.Count) return await MarkPublicationResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "PRODUCT_BATCH_CONTRACT_INVALID", JobExecutionResult.ManualReview("PRODUCT_BATCH_CONTRACT_INVALID", "Uzak sonuç eşlemesi için listing merchantSku/barkodları eksik veya yinelenmiş.", payload.ExternalOperationId), cancellationToken);
        var resultKeyByListing = listings.ToDictionary(x => isHepsiburada ? x.ExternalSku! : x.ExternalBarcode!, x => x, StringComparer.OrdinalIgnoreCase);
        var resultByKey = lines.ToDictionary(x => x.ExternalKey, StringComparer.OrdinalIgnoreCase);
        if (resultKeyByListing.Keys.Any(key => !resultByKey.ContainsKey(key))) return await MarkPublicationResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "PRODUCT_BATCH_CONTRACT_INVALID", JobExecutionResult.ManualReview("PRODUCT_BATCH_CONTRACT_INVALID", "Batch sonucu gönderilen tüm merchantSku/barkodları içermiyor.", payload.ExternalOperationId), cancellationToken);
        if (resultByKey.Keys.Any(key => !resultKeyByListing.ContainsKey(key))) return await MarkPublicationResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "PRODUCT_BATCH_CONTRACT_INVALID", JobExecutionResult.ManualReview("PRODUCT_BATCH_CONTRACT_INVALID", "Batch sonucu bilinmeyen merchantSku/barkod içeriyor.", payload.ExternalOperationId), cancellationToken);

        var succeeded = 0;
        foreach (var listing in listings)
        {
            var key = isHepsiburada ? listing.ExternalSku! : listing.ExternalBarcode!;
            var line = resultByKey[key];
            var rejection = line.Succeeded ? null : SafeCode(line.ErrorCode ?? "REMOTE_VALIDATION_FAILED");
            listing.ActualStatus = line.Succeeded ? "CREATE_ACCEPTED" : "CREATE_REJECTED";
            listing.RejectionCode = rejection;
            if (line.Succeeded) succeeded++;
            if (states.TryGetValue(listing.VariantId, out var state))
            {
                state.ActualStatus = listing.ActualStatus;
                state.LastRejectionCode = rejection;
                state.Version++;
            }
        }
        profile.ActualStatus = succeeded == listings.Count ? (isHepsiburada ? "IMPORT_ACCEPTED" : "APPROVAL_PENDING") : succeeded == 0 ? "CREATE_REJECTED" : "PARTIAL_FAILURE";
        profile.LastRejectionCode = listings.Select(x => x.RejectionCode).FirstOrDefault(x => x is not null);
        profile.Version++;
        if (succeeded > 0)
            await EnsureApprovalReconciliationJob(tenantId, connectionId, payload.ProductId, profile.Id, payload.PayloadHash, correlationId, cancellationToken, payload.ExternalOperationId);
        await db.SaveChangesAsync(cancellationToken);
        if (succeeded == listings.Count) return JobExecutionResult.Success();
        return succeeded == 0
            ? JobExecutionResult.Blocked("PRODUCT_BATCH_REJECTED", isHepsiburada ? "Hepsiburada ürün import içindeki tüm varyantlar reddedildi." : "Trendyol create batch içindeki tüm varyantlar reddedildi.", payload.ExternalOperationId)
            : JobExecutionResult.Blocked("PRODUCT_BATCH_PARTIAL_FAILURE", isHepsiburada ? "Hepsiburada ürün import kısmi başarısızlıkla tamamlandı." : "Trendyol create batch kısmi başarısızlıkla tamamlandı.", payload.ExternalOperationId);
    }

    private async Task<JobExecutionResult> ReconcileProductApproval(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, CancellationToken cancellationToken)
    {
        ProductApprovalReconciliationJobPayload? payload;
        try { payload = JsonSerializer.Deserialize<ProductApprovalReconciliationJobPayload>(payloadJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
        catch (JsonException) { return JobExecutionResult.Blocked("PRODUCT_APPROVAL_PAYLOAD_INVALID", "Ürün onay uzlaştırma işi payload sözleşmesini sağlamıyor."); }
        if (payload is null || payload.JobId == Guid.Empty || payload.ProductId == Guid.Empty || payload.ProfileId == Guid.Empty || string.IsNullOrWhiteSpace(payload.PayloadHash) || payload.DeadlineAt <= payload.StartedAt)
            return JobExecutionResult.Blocked("PRODUCT_APPROVAL_PAYLOAD_INVALID", "Ürün onay uzlaştırma işi zorunlu alanları eksik.");

        var job = await db.IntegrationJobs.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == payload.JobId && x.ConnectionId == connectionId && x.JobType == MarketplaceJobTypes.ProductApprovalReconcile, cancellationToken);
        var profile = await db.ChannelListingProfiles.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == payload.ProfileId && x.ProductId == payload.ProductId && x.ConnectionId == connectionId, cancellationToken);
        if (job is null || profile is null) return JobExecutionResult.Blocked("PRODUCT_APPROVAL_STATE_MISSING", "Onay uzlaştırma işi veya listing profile bulunamadı.");

        var listings = await db.ChannelListingVariants.Where(x => x.TenantId == tenantId && x.ProfileId == profile.Id).OrderBy(x => x.Id).ToListAsync(cancellationToken);
        var allVariantIds = listings.Select(x => x.VariantId).ToArray();
        var states = allVariantIds.Length == 0
            ? new Dictionary<Guid, MarketplaceListingState>()
            : await db.MarketplaceListingStates.Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && allVariantIds.Contains(x.VariantId)).ToDictionaryAsync(x => x.VariantId, cancellationToken);
        if (states.Values.Any(x => !string.Equals(x.PayloadHash, payload.PayloadHash, StringComparison.Ordinal)))
            return JobExecutionResult.Blocked("PRODUCT_APPROVAL_SUPERSEDED", "Daha yeni bir ürün yayınlama payload'ı bulundu; eski onay işi güncel listing durumunu değiştirmedi.");
        if (timeProvider.GetUtcNow() > payload.DeadlineAt)
            return await MarkApprovalResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "PRODUCT_APPROVAL_DEADLINE_EXPIRED", JobExecutionResult.ManualReview("PRODUCT_APPROVAL_DEADLINE_EXPIRED", "Trendyol ürün onayı belirlenen uzlaştırma penceresinde terminal duruma ulaşmadı."), cancellationToken);
        if (listings.Count == 0 || states.Count != listings.Count)
            return await MarkApprovalResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "PRODUCT_APPROVAL_STATE_INCOMPLETE", JobExecutionResult.ManualReview("PRODUCT_APPROVAL_STATE_INCOMPLETE", "Onay uzlaştırması için listing state kayıtları eksik."), cancellationToken);

        var candidates = listings.Where(x => !string.Equals(x.ActualStatus, "CREATE_REJECTED", StringComparison.Ordinal) && !string.Equals(x.ActualStatus, "UPDATE_REJECTED", StringComparison.Ordinal)).ToList();
        if (candidates.Count == 0 || candidates.Any(x => string.IsNullOrWhiteSpace(x.ExternalBarcode)) || candidates.Select(x => x.ExternalBarcode).Distinct(StringComparer.OrdinalIgnoreCase).Count() != candidates.Count)
            return await MarkApprovalResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "PRODUCT_APPROVAL_BARCODES_INVALID", JobExecutionResult.ManualReview("PRODUCT_APPROVAL_BARCODES_INVALID", "Onay uzlaştırması için kabul edilmiş ve benzersiz barkod listesi bulunamadı."), cancellationToken);

        var platformCode = await db.PlatformConnections.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == connectionId).Select(x => x.PlatformCode).SingleOrDefaultAsync(cancellationToken);
        if (string.Equals(platformCode, "HEPSIBURADA", StringComparison.OrdinalIgnoreCase))
            return await ReconcileHepsiburadaProductApproval(tenantId, connectionId, payload, profile, listings, states, correlationId, cancellationToken);

        var remoteByBarcode = new Dictionary<string, RemotePublicationStatus>(StringComparer.OrdinalIgnoreCase);
        foreach (var listing in candidates)
        {
            var barcode = listing.ExternalBarcode!;
            TrackRequest();
            var result = await products.GetPublicationStatusAsync(Context(tenantId, connectionId, correlationId, $"product-approval:{profile.Id:N}:{barcode}"), barcode, cancellationToken);
            if (!result.IsSuccess)
            {
                TrackResultFailure(result.Error);
                var adapterResult = JobExecutionResult.FromAdapterError(result.Error!);
                if (adapterResult.Kind == JobCompletionKind.Retry) return adapterResult;
                return await MarkApprovalResult(tenantId, connectionId, profile, "MANUAL_REVIEW", result.Error!.Code, JobExecutionResult.ManualReview(result.Error.Code, result.Error.SafeMessage, result.Error.RemoteRequestId), cancellationToken);
            }
            var remoteStatus = result.Value!;
            if (!string.Equals(remoteStatus.Barcode, barcode, StringComparison.OrdinalIgnoreCase))
                return await MarkApprovalResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "PRODUCT_APPROVAL_CONTRACT_INVALID", JobExecutionResult.ManualReview("PRODUCT_APPROVAL_CONTRACT_INVALID", "Trendyol ürün durum yanıtı istenen barkodla eşleşmedi."), cancellationToken);
            remoteByBarcode.Add(barcode, remoteStatus);
        }

        var localVariantIds = candidates.Select(x => x.VariantId).ToArray();
        if (candidates.Count == listings.Count
            && ProductApprovalReconciliationPolicy.ShouldResetMissingPublication(listings.Count, remoteByBarcode.Values.ToArray()))
            return await ResetMissingPublication(tenantId, connectionId, profile, listings, states, payload.PayloadHash, payload.JobId, cancellationToken);

        var approvedStatuses = remoteByBarcode.Values.Where(x => x.Status == "APPROVED").ToList();
        if (approvedStatuses.Any(x => string.IsNullOrWhiteSpace(x.ExternalProductId) || string.IsNullOrWhiteSpace(x.ExternalVariantId)))
            return await MarkApprovalResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "PRODUCT_APPROVAL_IDENTIFIERS_MISSING", JobExecutionResult.ManualReview("PRODUCT_APPROVAL_IDENTIFIERS_MISSING", "Onaylanan ürün yanıtında contentId veya variantId bulunamadı."), cancellationToken);
        var approvedContentIds = approvedStatuses.Select(x => x.ExternalProductId!).Distinct(StringComparer.Ordinal).ToList();
        var hasSplitApprovedContents = ProductApprovalReconciliationPolicy.HasSplitApprovedContents(remoteByBarcode.Values);
        var approvedVariantIds = approvedStatuses.Select(x => x.ExternalVariantId!).ToList();
        if (approvedVariantIds.Distinct(StringComparer.Ordinal).Count() != approvedVariantIds.Count)
            return await MarkApprovalResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "PRODUCT_APPROVAL_VARIANT_ID_DUPLICATE", JobExecutionResult.ManualReview("PRODUCT_APPROVAL_VARIANT_ID_DUPLICATE", "Trendyol onay yanıtı birden fazla barkod için aynı variantId değerini döndürdü."), cancellationToken);
        if (approvedContentIds.Count > 0)
        {
            if (await db.MarketplaceProductLinks.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && approvedContentIds.Contains(x.ExternalId) && x.ProductId != payload.ProductId, cancellationToken))
                return await MarkApprovalResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "PRODUCT_APPROVAL_IDENTITY_CONFLICT", JobExecutionResult.ManualReview("PRODUCT_APPROVAL_IDENTITY_CONFLICT", "Trendyol content kimliği başka bir yerel ürünle eşleşiyor."), cancellationToken);
            var localProductLink = await db.MarketplaceProductLinks.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ProductId == payload.ProductId, cancellationToken);
            if (localProductLink is not null && !approvedContentIds.Contains(localProductLink.ExternalId, StringComparer.Ordinal))
                return await MarkApprovalResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "PRODUCT_APPROVAL_IDENTITY_CONFLICT", JobExecutionResult.ManualReview("PRODUCT_APPROVAL_IDENTITY_CONFLICT", "Yerel ürün daha önce farklı bir Trendyol content kimliğiyle eşleştirilmiş."), cancellationToken);
        }
        if (approvedVariantIds.Count > 0 && await db.MarketplaceVariantLinks.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && approvedVariantIds.Contains(x.ExternalId) && !localVariantIds.Contains(x.VariantId), cancellationToken))
            return await MarkApprovalResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "PRODUCT_APPROVAL_IDENTITY_CONFLICT", JobExecutionResult.ManualReview("PRODUCT_APPROVAL_IDENTITY_CONFLICT", "Trendyol variant kimliği başka bir yerel varyantla eşleşiyor."), cancellationToken);
        var approvedVariantByLocalId = candidates
            .Where(listing => remoteByBarcode[listing.ExternalBarcode!].Status == "APPROVED")
            .ToDictionary(listing => listing.VariantId, listing => remoteByBarcode[listing.ExternalBarcode!].ExternalVariantId!, EqualityComparer<Guid>.Default);
        var localVariantLinks = await db.MarketplaceVariantLinks.AsNoTracking().Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && localVariantIds.Contains(x.VariantId)).ToListAsync(cancellationToken);
        if (localVariantLinks.Any(link => approvedVariantByLocalId.TryGetValue(link.VariantId, out var expectedExternalId) && !string.Equals(link.ExternalId, expectedExternalId, StringComparison.Ordinal)))
            return await MarkApprovalResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "PRODUCT_APPROVAL_IDENTITY_CONFLICT", JobExecutionResult.ManualReview("PRODUCT_APPROVAL_IDENTITY_CONFLICT", "Yerel varyant daha önce farklı bir Trendyol variant kimliğiyle eşleştirilmiş."), cancellationToken);

        var live = 0;
        var rejected = listings.Count - candidates.Count;
        var pending = 0;
        var exceptional = 0;
        string? firstCode = listings.Where(x => string.Equals(x.ActualStatus, "CREATE_REJECTED", StringComparison.Ordinal) || string.Equals(x.ActualStatus, "UPDATE_REJECTED", StringComparison.Ordinal)).Select(x => x.RejectionCode).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (approvedContentIds.Count == 1)
            await UpsertMarketplaceProductLink(tenantId, connectionId, payload.ProductId, approvedContentIds[0], cancellationToken);
        foreach (var listing in candidates)
        {
            var remote = remoteByBarcode[listing.ExternalBarcode!];
            var localStatus = remote.Status switch
            {
                "APPROVED" => "LIVE",
                "PENDING_APPROVAL" or "NOT_FOUND" => "APPROVAL_PENDING",
                "REJECTED" => "REJECTED",
                "ARCHIVED" => "ARCHIVED",
                "LOCKED" => "LOCKED",
                "BLACKLISTED" => "BLACKLISTED",
                _ => "MANUAL_REVIEW"
            };
            var code = localStatus switch
            {
                "REJECTED" => SafeCode(remote.RejectionCode ?? "PRODUCT_APPROVAL_REJECTED"),
                "ARCHIVED" => "REMOTE_PRODUCT_ARCHIVED",
                "LOCKED" => "REMOTE_PRODUCT_LOCKED",
                "BLACKLISTED" => "REMOTE_PRODUCT_BLACKLISTED",
                "MANUAL_REVIEW" => "PRODUCT_APPROVAL_STATUS_UNKNOWN",
                _ => null
            };
            listing.ActualStatus = localStatus;
            listing.DesiredStatus = "LIVE";
            listing.RejectionCode = code;
            if (states.TryGetValue(listing.VariantId, out var state))
            {
                state.DesiredStatus = "LIVE";
                state.ActualStatus = localStatus;
                state.LastRejectionCode = code;
                state.Version++;
            }

            switch (localStatus)
            {
                case "LIVE":
                    await UpsertMarketplaceVariantLink(tenantId, connectionId, listing.VariantId, remote.ExternalVariantId!, cancellationToken);
                    live++;
                    break;
                case "REJECTED": rejected++; firstCode ??= code; break;
                case "APPROVAL_PENDING": pending++; break;
                default: exceptional++; firstCode ??= code; break;
            }
        }

        profile.DesiredStatus = "LIVE";
        if (exceptional > 0)
        {
            profile.ActualStatus = "MANUAL_REVIEW";
            profile.LastRejectionCode = hasSplitApprovedContents ? ProductApprovalReconciliationPolicy.SplitApprovedContentsCode : firstCode;
        }
        else if (pending > 0)
        {
            profile.ActualStatus = live + rejected > 0 ? "APPROVAL_PARTIAL_PENDING" : "APPROVAL_PENDING";
            profile.LastRejectionCode = hasSplitApprovedContents ? ProductApprovalReconciliationPolicy.SplitApprovedContentsCode : firstCode;
        }
        else if (live == listings.Count)
        {
            profile.ActualStatus = "LIVE";
            profile.LastRejectionCode = hasSplitApprovedContents ? ProductApprovalReconciliationPolicy.SplitApprovedContentsCode : null;
            if (!hasSplitApprovedContents)
                await MarkProductLinkPublished(tenantId, connectionId, payload.ProductId, cancellationToken);
        }
        else if (hasSplitApprovedContents && live > 0)
        {
            profile.ActualStatus = "PARTIAL_LIVE";
            profile.LastRejectionCode = ProductApprovalReconciliationPolicy.SplitApprovedContentsCode;
        }
        else if (rejected == listings.Count)
        {
            profile.ActualStatus = "REJECTED";
            profile.LastRejectionCode = firstCode;
        }
        else
        {
            profile.ActualStatus = "PARTIAL_REJECTED";
            profile.LastRejectionCode = firstCode;
        }
        profile.Version++;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        if (exceptional > 0) return JobExecutionResult.ManualReview(firstCode ?? "PRODUCT_APPROVAL_REVIEW_REQUIRED", "Trendyol ürün onayında operatör incelemesi gerektiren terminal durum oluştu.");
        if (pending > 0) return JobExecutionResult.Retry("PRODUCT_APPROVAL_PENDING", "Trendyol ürün onayı henüz tamamlanmadı.", TimeSpan.FromMinutes(5));
        if (live == listings.Count) return JobExecutionResult.Success();
        return rejected == listings.Count
            ? JobExecutionResult.Blocked("PRODUCT_APPROVAL_REJECTED", "Trendyol ürün onayı tüm varyantlar için reddedildi.")
            : JobExecutionResult.Blocked("PRODUCT_APPROVAL_PARTIAL_REJECTION", "Trendyol ürün onayı bazı varyantlar için reddedildi.");
    }

    private async Task<JobExecutionResult> ReconcileHepsiburadaProductApproval(Guid tenantId, Guid connectionId, ProductApprovalReconciliationJobPayload payload, ChannelListingProfile profile, IReadOnlyList<ChannelListingVariant> listings, IReadOnlyDictionary<Guid, MarketplaceListingState> states, string correlationId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(payload.ExternalOperationId))
            return await MarkApprovalResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "HEPSIBURADA_TRACKING_ID_REQUIRED", JobExecutionResult.ManualReview("HEPSIBURADA_TRACKING_ID_REQUIRED", "Hepsiburada ürün durumunu uzlaştırmak için import trackingId bulunamadı."), cancellationToken);

        TrackRequest();
        var operation = await products.GetOperationAsync(Context(tenantId, connectionId, correlationId, $"hepsiburada-product-approval:{profile.Id:N}:{payload.ExternalOperationId}"), payload.ExternalOperationId, cancellationToken);
        if (!operation.IsSuccess)
        {
            TrackResultFailure(operation.Error);
            var mapped = JobExecutionResult.FromAdapterError(operation.Error!);
            return mapped.Kind == JobCompletionKind.Retry
                ? mapped
                : await MarkApprovalResult(tenantId, connectionId, profile, "MANUAL_REVIEW", operation.Error!.Code, JobExecutionResult.ManualReview(operation.Error.Code, operation.Error.SafeMessage, operation.Error.RemoteRequestId), cancellationToken);
        }
        if (string.Equals(operation.Value!.Status, "IN_PROGRESS", StringComparison.OrdinalIgnoreCase))
            return JobExecutionResult.Retry("HEPSIBURADA_PRODUCT_IMPORT_PENDING", "Hepsiburada ürün dosyası hâlâ işleniyor.", TimeSpan.FromMinutes(1), payload.ExternalOperationId);
        if (!string.Equals(operation.Value.Status, "COMPLETED", StringComparison.OrdinalIgnoreCase))
            return await MarkApprovalResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "HEPSIBURADA_PRODUCT_IMPORT_STATUS_UNKNOWN", JobExecutionResult.ManualReview("HEPSIBURADA_PRODUCT_IMPORT_STATUS_UNKNOWN", "Hepsiburada ürün dosyası tanınmayan terminal durum döndürdü.", payload.ExternalOperationId), cancellationToken);

        var expectedSkus = listings.Select(x => x.ExternalSku ?? "").ToArray();
        var lines = operation.Value.Lines.Where(x => !string.IsNullOrWhiteSpace(x.ExternalKey)).ToList();
        if (expectedSkus.Any(string.IsNullOrWhiteSpace)
            || expectedSkus.Distinct(StringComparer.OrdinalIgnoreCase).Count() != expectedSkus.Length
            || lines.Count != listings.Count
            || lines.GroupBy(x => x.ExternalKey, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
            return await MarkApprovalResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "HEPSIBURADA_PRODUCT_STATUS_CONTRACT_INVALID", JobExecutionResult.ManualReview("HEPSIBURADA_PRODUCT_STATUS_CONTRACT_INVALID", "Hepsiburada trackingId satırları merchantSku üzerinden listing'lerle bire bir eşleşmiyor.", payload.ExternalOperationId), cancellationToken);
        var byMerchantSku = lines.ToDictionary(x => x.ExternalKey, StringComparer.OrdinalIgnoreCase);
        if (expectedSkus.Any(sku => !byMerchantSku.ContainsKey(sku)) || byMerchantSku.Keys.Any(sku => !expectedSkus.Contains(sku, StringComparer.OrdinalIgnoreCase)))
            return await MarkApprovalResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "HEPSIBURADA_PRODUCT_STATUS_CONTRACT_INVALID", JobExecutionResult.ManualReview("HEPSIBURADA_PRODUCT_STATUS_CONTRACT_INVALID", "Hepsiburada trackingId sonucu eksik veya gönderilmemiş merchantSku içeriyor.", payload.ExternalOperationId), cancellationToken);

        var variantIds = listings.Select(x => x.VariantId).ToArray();
        var existingLinks = await db.MarketplaceVariantLinks.Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && variantIds.Contains(x.VariantId)).ToDictionaryAsync(x => x.VariantId, cancellationToken);
        var live = 0;
        var rejected = 0;
        var pending = 0;
        var manualReview = 0;
        string? firstCode = null;
        foreach (var listing in listings)
        {
            var line = byMerchantSku[listing.ExternalSku!];
            var remoteStatus = line.Status?.ToUpperInvariant() ?? "";
            var localStatus = !line.Succeeded || remoteStatus is "MISSING_INFO" or "URUN_BILGILERI_EKSIK" or "REJECTED"
                ? "REJECTED"
                : remoteStatus is "MATCHED" or "SATISA_HAZIR" or "SALE_READY"
                    ? "LIVE"
                    : remoteStatus is "WAITING" or "INCELENECEK" or "GOREV_ACILMIS" or "ESLESEN" or "PRE_MATCHED" or "MATCHED_WITH_STAGED" or "ON_KATALOG_ESLESEN" or "CREATED" or "KATALOG_SURECINDE" or "APPROVAL_PENDING" or "WAITING_APPROVAL"
                        ? "APPROVAL_PENDING"
                        : "MANUAL_REVIEW";
            var rejection = localStatus switch
            {
                "REJECTED" => SafeCode(line.ErrorCode ?? (remoteStatus == "MISSING_INFO" ? "PRODUCT_MISSING_INFO" : "PRODUCT_IMPORT_REJECTED")),
                "MANUAL_REVIEW" => "HEPSIBURADA_PRODUCT_STATUS_UNKNOWN",
                _ => null
            };
            if (localStatus == "LIVE")
            {
                if (string.IsNullOrWhiteSpace(line.ExternalId)
                    || await db.MarketplaceVariantLinks.AnyAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ExternalId == line.ExternalId && x.VariantId != listing.VariantId, cancellationToken)
                    || (existingLinks.TryGetValue(listing.VariantId, out var priorLink) && !string.Equals(priorLink.ExternalId, line.ExternalId, StringComparison.Ordinal)))
                {
                    localStatus = "MANUAL_REVIEW";
                    rejection = "HEPSIBURADA_PRODUCT_IDENTITY_CONFLICT";
                }
                else
                    await UpsertMarketplaceVariantLink(tenantId, connectionId, listing.VariantId, line.ExternalId, cancellationToken);
            }
            listing.ActualStatus = localStatus;
            listing.DesiredStatus = "LIVE";
            listing.RejectionCode = rejection;
            if (states.TryGetValue(listing.VariantId, out var state))
            {
                state.ActualStatus = localStatus;
                state.DesiredStatus = "LIVE";
                state.LastRejectionCode = rejection;
                state.Version++;
            }
            switch (localStatus)
            {
                case "LIVE": live++; break;
                case "REJECTED": rejected++; firstCode ??= rejection; break;
                case "APPROVAL_PENDING": pending++; break;
                default: manualReview++; firstCode ??= rejection; break;
            }
        }

        profile.DesiredStatus = "LIVE";
        profile.LastRejectionCode = firstCode;
        profile.ActualStatus = manualReview > 0 ? "MANUAL_REVIEW"
            : pending > 0 ? (live + rejected > 0 ? "APPROVAL_PARTIAL_PENDING" : "APPROVAL_PENDING")
            : live == listings.Count ? "LIVE"
            : rejected == listings.Count ? "REJECTED"
            : "PARTIAL_REJECTED";
        profile.Version++;
        await db.SaveChangesAsync(cancellationToken);
        if (manualReview > 0) return JobExecutionResult.ManualReview(firstCode ?? "HEPSIBURADA_PRODUCT_STATUS_UNKNOWN", "Hepsiburada ürün durum eşlemesi veya HB SKU kimliği belirsiz; manuel inceleme gerekir.", payload.ExternalOperationId);
        if (pending > 0) return JobExecutionResult.Retry("HEPSIBURADA_PRODUCT_APPROVAL_PENDING", "Hepsiburada ürün inceleme/eşleşme süreci henüz tamamlanmadı.", TimeSpan.FromMinutes(5), payload.ExternalOperationId);
        if (rejected > 0) return JobExecutionResult.Blocked(rejected == listings.Count ? "HEPSIBURADA_PRODUCT_REJECTED" : "HEPSIBURADA_PRODUCT_PARTIAL_REJECTION", "Hepsiburada ürün import sonucu eksik bilgi veya ret içeriyor.", payload.ExternalOperationId);
        return JobExecutionResult.Success();
    }

    private async Task<JobExecutionResult> MarkApprovalResult(Guid tenantId, Guid connectionId, ChannelListingProfile profile, string status, string? rejectionCode, JobExecutionResult result, CancellationToken cancellationToken)
    {
        profile.ActualStatus = status;
        profile.LastRejectionCode = SafeCode(rejectionCode);
        profile.Version++;
        var listings = await db.ChannelListingVariants.Where(x => x.TenantId == tenantId && x.ProfileId == profile.Id).ToListAsync(cancellationToken);
        var candidates = listings.Where(x => !string.Equals(x.ActualStatus, "CREATE_REJECTED", StringComparison.Ordinal) && !string.Equals(x.ActualStatus, "UPDATE_REJECTED", StringComparison.Ordinal)).ToList();
        var variantIds = candidates.Select(x => x.VariantId).ToArray();
        var states = variantIds.Length == 0
            ? new Dictionary<Guid, MarketplaceListingState>()
            : await db.MarketplaceListingStates.Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && variantIds.Contains(x.VariantId)).ToDictionaryAsync(x => x.VariantId, cancellationToken);
        foreach (var listing in candidates)
        {
            listing.ActualStatus = status;
            listing.RejectionCode = SafeCode(rejectionCode);
            if (states.TryGetValue(listing.VariantId, out var state))
            {
                state.ActualStatus = status;
                state.LastRejectionCode = SafeCode(rejectionCode);
                state.Version++;
            }
        }
        await db.SaveChangesAsync(cancellationToken);
        return result;
    }

    private async Task<JobExecutionResult> ResetMissingPublication(
        Guid tenantId,
        Guid connectionId,
        ChannelListingProfile profile,
        IReadOnlyCollection<ChannelListingVariant> listings,
        IReadOnlyDictionary<Guid, MarketplaceListingState> states,
        string payloadHash,
        Guid currentJobId,
        CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var productCreatePrefix = $"product-create:{connectionId:N}:{profile.ProductId:N}:";
        var productUpdatePrefix = $"product-update:{connectionId:N}:{profile.ProductId:N}:";
        var productArchivePrefix = $"product-archive:{connectionId:N}:{profile.ProductId:N}:";
        var approvalDedup = $"product-approval:{connectionId:N}:{profile.Id:N}:{payloadHash}";
        var approvalManualPrefix = $"{approvalDedup}:manual-v";
        var productCreateDedup = $"{productCreatePrefix}{payloadHash}";
        var relatedJobs = await db.IntegrationJobs.FromSqlInterpolated($"""
            SELECT * FROM integration.jobs
            WHERE "TenantId" = {tenantId} AND "ConnectionId" = {connectionId}
              AND (("JobType" = {MarketplaceJobTypes.ProductCreate} AND "JobDedupKey" LIKE {productCreatePrefix + "%"})
                OR ("JobType" = {MarketplaceJobTypes.ProductUpdate} AND "JobDedupKey" LIKE {productUpdatePrefix + "%"})
                OR ("JobType" = {MarketplaceJobTypes.ProductArchive} AND "JobDedupKey" LIKE {productArchivePrefix + "%"})
                OR ("JobType" = {MarketplaceJobTypes.ProductApprovalReconcile}
                    AND ("JobDedupKey" = {approvalDedup} OR "JobDedupKey" LIKE {approvalManualPrefix + "%"})))
            FOR UPDATE
            """).ToListAsync(cancellationToken);
        if (relatedJobs.Any(job => job.JobType != MarketplaceJobTypes.ProductApprovalReconcile
            && job.Status is JobStatus.Pending or JobStatus.Leased or JobStatus.RetryScheduled))
            return JobExecutionResult.Retry("PRODUCT_WRITE_IN_PROGRESS", "Ürün için başka bir yayın işlemi sürüyor; durum kontrolü yeniden denenecek.", TimeSpan.FromMinutes(1));
        if (relatedJobs.Any(job => job.JobType == MarketplaceJobTypes.ProductApprovalReconcile
            && job.Id != currentJobId && job.Status == JobStatus.Leased))
            return JobExecutionResult.Retry("PRODUCT_APPROVAL_CHECK_IN_PROGRESS", "Başka bir durum kontrolü sürüyor; ürünün bulunmadığı doğrulaması yeniden denenecek.", TimeSpan.FromMinutes(1));

        var now = timeProvider.GetUtcNow();
        foreach (var oldCheck in relatedJobs.Where(job => job.JobType == MarketplaceJobTypes.ProductApprovalReconcile && job.Id != currentJobId
            && job.Status is JobStatus.Pending or JobStatus.RetryScheduled))
        {
            oldCheck.Status = JobStatus.Cancelled;
            oldCheck.CompletedAt = now;
            oldCheck.LastErrorCode = "PRODUCT_MISSING_RESET";
            oldCheck.LastErrorSummary = "Trendyol ilanının bulunmadığı doğrulandığı için eski durum kontrolü kapatıldı.";
            oldCheck.Version++;
        }
        var previousCreate = relatedJobs.SingleOrDefault(job => job.JobType == MarketplaceJobTypes.ProductCreate && job.JobDedupKey == productCreateDedup);
        if (previousCreate is not null)
        {
            previousCreate.JobDedupKey = $"{previousCreate.JobDedupKey}:not-found:{previousCreate.Id:N}";
            previousCreate.Version++;
        }
        var previousApproval = relatedJobs.SingleOrDefault(job => job.JobType == MarketplaceJobTypes.ProductApprovalReconcile && job.JobDedupKey == approvalDedup);
        if (previousApproval is not null)
        {
            previousApproval.JobDedupKey = $"{previousApproval.JobDedupKey}:not-found:{previousApproval.Id:N}";
            previousApproval.Version++;
        }

        profile.DesiredStatus = "DRAFT";
        profile.ActualStatus = "UNKNOWN";
        profile.LastRejectionCode = null;
        profile.Version++;
        foreach (var listing in listings)
        {
            listing.DesiredStatus = "DRAFT";
            listing.ActualStatus = "UNKNOWN";
            listing.RejectionCode = null;
            if (states.TryGetValue(listing.VariantId, out var state))
            {
                state.DesiredStatus = "DRAFT";
                state.ActualStatus = "UNKNOWN";
                state.LastRejectionCode = null;
                state.PayloadHash = null;
                state.Version++;
            }
        }

        var productLink = await db.MarketplaceProductLinks.SingleOrDefaultAsync(
            x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ProductId == profile.ProductId,
            cancellationToken);
        if (productLink is not null) db.MarketplaceProductLinks.Remove(productLink);
        var variantIds = listings.Select(x => x.VariantId).ToArray();
        var variantLinks = await db.MarketplaceVariantLinks.Where(
            x => x.TenantId == tenantId && x.ConnectionId == connectionId && variantIds.Contains(x.VariantId))
            .ToListAsync(cancellationToken);
        db.MarketplaceVariantLinks.RemoveRange(variantLinks);

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return JobExecutionResult.Success();
    }

    private async Task EnsureApprovalReconciliationJob(Guid tenantId, Guid connectionId, Guid productId, Guid profileId, string payloadHash, string correlationId, CancellationToken cancellationToken, string? externalOperationId = null)
    {
        var dedup = $"product-approval:{connectionId:N}:{profileId:N}:{payloadHash}";
        if (await db.IntegrationJobs.AnyAsync(x => x.TenantId == tenantId && x.JobType == MarketplaceJobTypes.ProductApprovalReconcile && x.JobDedupKey == dedup, cancellationToken)) return;
        var now = timeProvider.GetUtcNow();
        var jobId = Guid.CreateVersion7();
        var payload = JsonSerializer.Serialize(new ProductApprovalReconciliationJobPayload(jobId, productId, profileId, payloadHash, now, now.AddDays(7), externalOperationId));
        db.IntegrationJobs.Add(new IntegrationJob
        {
            Id = jobId,
            TenantId = tenantId,
            ConnectionId = connectionId,
            JobType = MarketplaceJobTypes.ProductApprovalReconcile,
            PayloadJson = payload,
            PayloadVersion = 1,
            PayloadHash = Hash(payload),
            JobDedupKey = dedup,
            EffectIdempotencyKey = dedup,
            Priority = 4,
            Status = JobStatus.Pending,
            AvailableAt = now,
            MaxAttempts = ProductApprovalReconcileMaxAttempts,
            CorrelationId = correlationId,
            CreatedAt = now,
            Version = 1
        });
    }

    private async Task UpsertMarketplaceProductLink(Guid tenantId, Guid connectionId, Guid productId, string externalProductId, CancellationToken cancellationToken)
    {
        var productLink = await db.MarketplaceProductLinks.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ProductId == productId, cancellationToken);
        if (productLink is null) db.MarketplaceProductLinks.Add(new MarketplaceProductLink { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = connectionId, ProductId = productId, ExternalId = externalProductId, Version = 1 });
    }

    private async Task MarkProductLinkPublished(Guid tenantId, Guid connectionId, Guid productId, CancellationToken cancellationToken)
    {
        var link = db.MarketplaceProductLinks.Local.SingleOrDefault(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ProductId == productId)
            ?? await db.MarketplaceProductLinks.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ProductId == productId, cancellationToken);
        if (link is null) return;
        var productVersion = await db.Products.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == productId).Select(x => x.Version).SingleAsync(cancellationToken);
        link.LastPublishedProductVersion = productVersion;
        link.LastPublishedAt = timeProvider.GetUtcNow();
        link.SyncStatus = "SYNCED";
        link.DirtyFieldsJson = null;
        link.LastError = null;
        link.Version++;
    }

    private async Task UpsertMarketplaceVariantLink(Guid tenantId, Guid connectionId, Guid variantId, string externalVariantId, CancellationToken cancellationToken)
    {
        var variantLink = await db.MarketplaceVariantLinks.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.VariantId == variantId, cancellationToken);
        if (variantLink is null) db.MarketplaceVariantLinks.Add(new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = connectionId, VariantId = variantId, ExternalId = externalVariantId, Version = 1 });
    }

    private async Task<JobExecutionResult> MarkPublicationResult(Guid tenantId, Guid connectionId, ChannelListingProfile profile, string status, string? rejectionCode, JobExecutionResult result, CancellationToken cancellationToken)
    {
        profile.ActualStatus = status;
        profile.LastRejectionCode = SafeCode(rejectionCode);
        profile.Version++;
        await SetListingStatus(tenantId, connectionId, profile.Id, status, rejectionCode, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return result;
    }

    private async Task SetListingStatus(Guid tenantId, Guid connectionId, Guid profileId, string status, string? rejectionCode, CancellationToken cancellationToken)
    {
        var listings = await db.ChannelListingVariants.Where(x => x.TenantId == tenantId && x.ProfileId == profileId).ToListAsync(cancellationToken);
        var variantIds = listings.Select(x => x.VariantId).ToArray();
        var states = await db.MarketplaceListingStates.Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && variantIds.Contains(x.VariantId)).ToDictionaryAsync(x => x.VariantId, cancellationToken);
        foreach (var listing in listings)
        {
            listing.ActualStatus = status;
            listing.RejectionCode = SafeCode(rejectionCode);
            if (states.TryGetValue(listing.VariantId, out var state)) { state.ActualStatus = status; state.LastRejectionCode = SafeCode(rejectionCode); state.Version++; }
        }
    }

    private static string? SafeCode(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(256, value.Trim().Length)];

    private async Task<JobExecutionResult> UpdateProduct(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, CancellationToken cancellationToken)
    {
        ProductUpdateJobPayload? payload;
        try { payload = JsonSerializer.Deserialize<ProductUpdateJobPayload>(payloadJson, JsonOptions); }
        catch (JsonException) { return JobExecutionResult.Blocked("PRODUCT_UPDATE_PAYLOAD_INVALID", "Ürün güncelleme işi payload sözleşmesini sağlamıyor."); }
        if (payload is null || payload.JobId == Guid.Empty || payload.ProductId == Guid.Empty || payload.ProfileId == Guid.Empty || string.IsNullOrWhiteSpace(payload.PayloadHash))
            return JobExecutionResult.Blocked("PRODUCT_UPDATE_PAYLOAD_INVALID", "Ürün güncelleme işi zorunlu alanları eksik.");

        var job = await db.IntegrationJobs.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == payload.JobId && x.ConnectionId == connectionId && x.JobType == MarketplaceJobTypes.ProductUpdate, cancellationToken);
        var profile = await db.ChannelListingProfiles.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == payload.ProfileId && x.ProductId == payload.ProductId && x.ConnectionId == connectionId, cancellationToken);
        if (job is null || profile is null) return JobExecutionResult.Blocked("PRODUCT_UPDATE_STATE_MISSING", "Ürün güncelleme işi veya listing profile bulunamadı.");
        var platformCode = await db.PlatformConnections.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == connectionId).Select(x => x.PlatformCode).SingleOrDefaultAsync(cancellationToken);
        var isHepsiburada = string.Equals(platformCode, "HEPSIBURADA", StringComparison.OrdinalIgnoreCase);

        var listings = await db.ChannelListingVariants.Where(x => x.TenantId == tenantId && x.ProfileId == profile.Id).OrderBy(x => x.Id).ToListAsync(cancellationToken);
        var variantIds = listings.Select(x => x.VariantId).ToArray();
        var states = variantIds.Length == 0 ? new Dictionary<Guid, MarketplaceListingState>() : await db.MarketplaceListingStates.Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && variantIds.Contains(x.VariantId)).ToDictionaryAsync(x => x.VariantId, cancellationToken);
        if (listings.Count == 0 || states.Count != listings.Count) return await MarkPublicationResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "PRODUCT_UPDATE_STATE_INCOMPLETE", JobExecutionResult.ManualReview("PRODUCT_UPDATE_STATE_INCOMPLETE", "Ürün güncelleme listing state kayıtları eksik."), cancellationToken);
        if (states.Values.Any(x => !string.Equals(x.PayloadHash, payload.PayloadHash, StringComparison.Ordinal))) return JobExecutionResult.Blocked("PRODUCT_UPDATE_SUPERSEDED", "Daha yeni ürün payload'ı bulundu; eski güncelleme işi uzak çağrı yapmadan durduruldu.");

        var phase = payload.Phase.Trim().ToUpperInvariant();
        if (phase.StartsWith("SUBMIT_", StringComparison.Ordinal))
        {
            if (!await ExternalWriteMasterAllowedAsync(tenantId, connectionId, cancellationToken)) return await MarkPublicationResult(tenantId, connectionId, profile, "UPDATE_BLOCKED", "EXTERNAL_WRITES_DISABLED", JobExecutionResult.Blocked("EXTERNAL_WRITES_DISABLED", "Dış yazma anahtarı kapalı; pazar yeri isteği gönderilmedi."), cancellationToken);
            if (isHepsiburada)
            {
                if (!string.Equals(configuration["Hepsiburada:AuthenticationMode"], "BASIC", StringComparison.OrdinalIgnoreCase)) return await MarkPublicationResult(tenantId, connectionId, profile, "UPDATE_BLOCKED", "HEPSIBURADA_AUTHENTICATION_UNVERIFIED", JobExecutionResult.Blocked("HEPSIBURADA_AUTHENTICATION_UNVERIFIED", "Hepsiburada auth biçimi doğrulanmadı; ürün güncellemesi gönderilmedi."), cancellationToken);
                if (!await HasHepsiburadaWriteEvidenceAsync(tenantId, connectionId, cancellationToken, MarketplaceCapabilities.ProductWrite)) return await MarkPublicationResult(tenantId, connectionId, profile, "UPDATE_BLOCKED", "HEPSIBURADA_WRITE_CAPABILITY_EVIDENCE_REQUIRED", JobExecutionResult.Blocked("HEPSIBURADA_WRITE_CAPABILITY_EVIDENCE_REQUIRED", "Ürün güncellemesi için mağaza/ortam kapsamlı PRODUCT_WRITE SIT kanıtı bulunamadı; gönderim yapılmadı."), cancellationToken);
            }
            var phasePayload = UpdatePayload(payload, phase);
            if (!HasItems(phasePayload))
            {
                var skipped = AdvanceUpdate(payload, phase);
                if (skipped is null) return await CompleteProductUpdate(tenantId, connectionId, payload, profile, listings, states, correlationId, cancellationToken);
                job.PayloadJson = JsonSerializer.Serialize(skipped); job.PayloadHash = Hash(job.PayloadJson);
                await db.SaveChangesAsync(cancellationToken);
                return JobExecutionResult.Retry("PRODUCT_UPDATE_NEXT_PHASE", "Boş ürün güncelleme fazı atlandı.", TimeSpan.FromSeconds(1));
            }

            var effectType = $"{MarketplaceJobTypes.ProductUpdate}:{phase}";
            var effectKey = $"{job.EffectIdempotencyKey}:{phase}";
            if (await db.ExternalEffectRecords.AnyAsync(x => x.TenantId == tenantId && x.EffectType == effectType && x.IdempotencyKey == effectKey, cancellationToken))
                return await MarkPublicationResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "EXTERNAL_EFFECT_AMBIGUOUS", JobExecutionResult.ManualReview("EXTERNAL_EFFECT_AMBIGUOUS", "Önceki ürün güncelleme çağrısının sonucu kesinleştirilemedi; tekrar gönderim engellendi."), cancellationToken);

            var effect = new ExternalEffectRecord { Id = Guid.CreateVersion7(), TenantId = tenantId, EffectType = effectType, IdempotencyKey = effectKey, CreatedAt = timeProvider.GetUtcNow() };
            db.ExternalEffectRecords.Add(effect); await db.SaveChangesAsync(cancellationToken);
            var publication = new ProductUpdatePublication(payload.ProductId, payload.Mode, payload.PayloadHash, payload.UnapprovedPayloadJson, payload.ApprovedContentPayloadJson, payload.ApprovedVariantPayloadJson, payload.ApprovedDeliveryPayloadJson);
            var context = Context(tenantId, connectionId, correlationId, effectKey);
            TrackRequest();
            var submit = phase switch
            {
                "SUBMIT_UNAPPROVED" => await products.UpdateUnapprovedAsync(context, publication, cancellationToken),
                "SUBMIT_CONTENT" => await products.UpdateApprovedContentAsync(context, publication, cancellationToken),
                "SUBMIT_VARIANTS" => await products.UpdateApprovedVariantsAsync(context, publication, cancellationToken),
                "SUBMIT_DELIVERY" => await products.UpdateApprovedDeliveryAsync(context, publication, cancellationToken),
                _ => AdapterResult<RemoteOperationRef>.Failure(new(AdapterErrorClass.ContractViolation, "PRODUCT_UPDATE_PHASE_INVALID", "Ürün güncelleme fazı tanınmıyor.", null, null, null))
            };
            if (!submit.IsSuccess)
            {
                TrackResultFailure(submit.Error);
                var error = submit.Error!;
                if (IsAmbiguous(error)) return await MarkPublicationResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "EXTERNAL_EFFECT_AMBIGUOUS", JobExecutionResult.ManualReview("EXTERNAL_EFFECT_AMBIGUOUS", "Ürün güncelleme çağrısının uygulanıp uygulanmadığı kesinleştirilemedi.", error.RemoteRequestId), cancellationToken);
                db.ExternalEffectRecords.Remove(effect); await db.SaveChangesAsync(cancellationToken);
                return await MarkPublicationResult(tenantId, connectionId, profile, "UPDATE_BLOCKED", error.Code, JobExecutionResult.FromAdapterError(error), cancellationToken);
            }
            effect.CompletedAt = timeProvider.GetUtcNow();
            var operation = submit.Value!;
            var poll = payload with { Phase = phase.Replace("SUBMIT_", "POLL_", StringComparison.Ordinal), ExternalOperationId = operation.ExternalOperationId, SubmittedAt = operation.SubmittedAt };
            job.PayloadJson = JsonSerializer.Serialize(poll); job.PayloadHash = Hash(job.PayloadJson);
            profile.ActualStatus = phase.Replace("SUBMIT_", "UPDATE_", StringComparison.Ordinal) + "_SUBMITTED"; profile.Version++;
            await db.SaveChangesAsync(cancellationToken);
            return JobExecutionResult.Retry("PRODUCT_UPDATE_BATCH_PENDING", isHepsiburada ? "Hepsiburada ürün import güncelleme sonucu bekleniyor." : "Trendyol ürün güncelleme batch sonucu bekleniyor.", ProductUpdatePollDelay(operation.SubmittedAt), operation.ExternalOperationId);
        }

        if (!phase.StartsWith("POLL_", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(payload.ExternalOperationId) || payload.SubmittedAt is null)
            return JobExecutionResult.Blocked("PRODUCT_UPDATE_PHASE_INVALID", "Ürün güncelleme işi bilinmeyen bir fazda.");
        if (timeProvider.GetUtcNow() - payload.SubmittedAt.Value > TimeSpan.FromHours(4))
            return await MarkPublicationResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "PRODUCT_UPDATE_BATCH_EXPIRED", JobExecutionResult.ManualReview("PRODUCT_UPDATE_BATCH_EXPIRED", "Ürün güncelleme batch sonucu dört saatlik pencerede alınamadı.", payload.ExternalOperationId), cancellationToken);

        TrackRequest();
        var operationResult = await products.GetOperationAsync(Context(tenantId, connectionId, correlationId, $"{job.EffectIdempotencyKey}:{phase}:poll"), payload.ExternalOperationId, cancellationToken);
        if (!operationResult.IsSuccess)
        {
            TrackResultFailure(operationResult.Error);
            var result = JobExecutionResult.FromAdapterError(operationResult.Error!);
            return result.Kind == JobCompletionKind.Retry ? result : await MarkPublicationResult(tenantId, connectionId, profile, "MANUAL_REVIEW", operationResult.Error!.Code, result, cancellationToken);
        }
        var status = operationResult.Value!;
        if (string.Equals(status.Status, "IN_PROGRESS", StringComparison.OrdinalIgnoreCase)) return JobExecutionResult.Retry("PRODUCT_UPDATE_BATCH_PENDING", isHepsiburada ? "Hepsiburada ürün import güncelleme sonucu bekleniyor." : "Trendyol ürün güncelleme batch sonucu bekleniyor.", ProductUpdatePollDelay(payload.SubmittedAt.Value), payload.ExternalOperationId);
        if (!string.Equals(status.Status, "COMPLETED", StringComparison.OrdinalIgnoreCase)) return await MarkPublicationResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "PRODUCT_UPDATE_BATCH_STATUS_UNKNOWN", JobExecutionResult.ManualReview("PRODUCT_UPDATE_BATCH_STATUS_UNKNOWN", "Ürün güncelleme batch servisi tanınmayan durum döndürdü.", payload.ExternalOperationId), cancellationToken);

        var failed = status.Lines.Where(x => !x.Succeeded).ToList();
        if (phase == "POLL_CONTENT" && failed.Count > 0)
            return await MarkPublicationResult(tenantId, connectionId, profile, "UPDATE_REJECTED", SafeCode(failed[0].ErrorCode) ?? "PRODUCT_UPDATE_CONTENT_REJECTED", JobExecutionResult.Blocked("PRODUCT_UPDATE_CONTENT_REJECTED", "Trendyol content güncellemesi reddedildi.", payload.ExternalOperationId), cancellationToken);
        if (phase is "POLL_UNAPPROVED" or "POLL_VARIANTS" or "POLL_DELIVERY")
        {
            if (status.Lines.Count == 0) return await MarkPublicationResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "PRODUCT_UPDATE_BATCH_CONTRACT_INVALID", JobExecutionResult.ManualReview("PRODUCT_UPDATE_BATCH_CONTRACT_INVALID", "Ürün güncelleme batch sonucu satır içermiyor.", payload.ExternalOperationId), cancellationToken);
            var updateKeys = status.Lines.Where(x => !string.IsNullOrWhiteSpace(x.ExternalKey)).GroupBy(x => x.ExternalKey, StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => x.Last(), StringComparer.OrdinalIgnoreCase);
            if (isHepsiburada)
            {
                var expectedKeys = listings.Select(x => x.ExternalSku).ToArray();
                if (expectedKeys.Any(string.IsNullOrWhiteSpace) || expectedKeys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != expectedKeys.Length
                    || expectedKeys.Any(key => !updateKeys.ContainsKey(key!)) || updateKeys.Keys.Any(key => !expectedKeys.Contains(key, StringComparer.OrdinalIgnoreCase)))
                    return await MarkPublicationResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "PRODUCT_UPDATE_BATCH_CONTRACT_INVALID", JobExecutionResult.ManualReview("PRODUCT_UPDATE_BATCH_CONTRACT_INVALID", "Hepsiburada ürün import sonucu merchantSku satırları beklenen listing'lerle eşleşmiyor.", payload.ExternalOperationId), cancellationToken);
            }
            foreach (var listing in listings)
            {
                var key = isHepsiburada ? listing.ExternalSku : listing.ExternalBarcode;
                if (string.IsNullOrWhiteSpace(key) || !updateKeys.TryGetValue(key, out var line)) continue;
                if (line.Succeeded) continue;
                var code = SafeCode(line.ErrorCode) ?? "PRODUCT_UPDATE_REJECTED";
                listing.ActualStatus = "UPDATE_REJECTED"; listing.RejectionCode = code;
                if (states.TryGetValue(listing.VariantId, out var state)) { state.ActualStatus = "UPDATE_REJECTED"; state.LastRejectionCode = code; state.Version++; }
            }
        }

        var next = AdvanceUpdate(payload, phase);
        if (next is null) return await CompleteProductUpdate(tenantId, connectionId, payload, profile, listings, states, correlationId, cancellationToken);
        job.PayloadJson = JsonSerializer.Serialize(next); job.PayloadHash = Hash(job.PayloadJson);
        profile.ActualStatus = "UPDATE_IN_PROGRESS"; profile.Version++;
        await db.SaveChangesAsync(cancellationToken);
        return JobExecutionResult.Retry("PRODUCT_UPDATE_NEXT_PHASE", "Trendyol ürün güncellemesinin sonraki batch fazı hazırlanıyor.", TimeSpan.FromSeconds(1));
    }

    private async Task<JobExecutionResult> CompleteProductUpdate(Guid tenantId, Guid connectionId, ProductUpdateJobPayload payload, ChannelListingProfile profile, IReadOnlyList<ChannelListingVariant> listings, IReadOnlyDictionary<Guid, MarketplaceListingState> states, string correlationId, CancellationToken cancellationToken)
    {
        var platformCode = await db.PlatformConnections.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == connectionId).Select(x => x.PlatformCode).SingleOrDefaultAsync(cancellationToken);
        var isHepsiburada = string.Equals(platformCode, "HEPSIBURADA", StringComparison.OrdinalIgnoreCase);
        var rejected = listings.Count(x => string.Equals(x.ActualStatus, "UPDATE_REJECTED", StringComparison.Ordinal));
        foreach (var listing in listings.Where(x => !string.Equals(x.ActualStatus, "UPDATE_REJECTED", StringComparison.Ordinal)))
        {
            listing.ActualStatus = isHepsiburada ? "IMPORT_ACCEPTED" : "APPROVAL_PENDING"; listing.RejectionCode = null;
            if (states.TryGetValue(listing.VariantId, out var state)) { state.ActualStatus = listing.ActualStatus; state.LastRejectionCode = null; state.Version++; }
        }
        profile.ActualStatus = rejected == 0 ? (isHepsiburada ? "IMPORT_ACCEPTED" : "APPROVAL_PENDING") : rejected == listings.Count ? "UPDATE_REJECTED" : "UPDATE_PARTIAL_FAILURE";
        profile.LastRejectionCode = listings.Select(x => x.RejectionCode).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)); profile.Version++;
        if (rejected < listings.Count) await EnsureApprovalReconciliationJob(tenantId, connectionId, payload.ProductId, profile.Id, payload.PayloadHash, correlationId, cancellationToken, payload.ExternalOperationId);
        await db.SaveChangesAsync(cancellationToken);
        return rejected == 0 ? JobExecutionResult.Success() : rejected == listings.Count ? JobExecutionResult.Blocked("PRODUCT_UPDATE_REJECTED", "Trendyol ürün güncellemesindeki tüm varyantlar reddedildi.") : JobExecutionResult.Blocked("PRODUCT_UPDATE_PARTIAL_FAILURE", "Trendyol ürün güncellemesi bazı varyantlar için reddedildi.");
    }

    private async Task<JobExecutionResult> ArchiveProduct(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, CancellationToken cancellationToken)
    {
        ProductArchiveJobPayload? payload;
        try { payload = JsonSerializer.Deserialize<ProductArchiveJobPayload>(payloadJson, JsonOptions); }
        catch (JsonException) { return JobExecutionResult.Blocked("PRODUCT_ARCHIVE_PAYLOAD_INVALID", "Ürün arşiv işi payload sözleşmesini sağlamıyor."); }
        if (payload is null || payload.JobId == Guid.Empty || payload.ProductId == Guid.Empty || payload.ProfileId == Guid.Empty || string.IsNullOrWhiteSpace(payload.PayloadHash) || payload.DeadlineAt <= payload.StartedAt)
            return JobExecutionResult.Blocked("PRODUCT_ARCHIVE_PAYLOAD_INVALID", "Ürün arşiv işi zorunlu alanları eksik.");
        var job = await db.IntegrationJobs.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == payload.JobId && x.ConnectionId == connectionId && x.JobType == MarketplaceJobTypes.ProductArchive, cancellationToken);
        var profile = await db.ChannelListingProfiles.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == payload.ProfileId && x.ProductId == payload.ProductId && x.ConnectionId == connectionId, cancellationToken);
        if (job is null || profile is null) return JobExecutionResult.Blocked("PRODUCT_ARCHIVE_STATE_MISSING", "Ürün arşiv işi veya listing profile bulunamadı.");
        var listings = await db.ChannelListingVariants.Where(x => x.TenantId == tenantId && x.ProfileId == profile.Id).OrderBy(x => x.Id).ToListAsync(cancellationToken);
        var ids = listings.Select(x => x.VariantId).ToArray();
        var states = ids.Length == 0 ? new Dictionary<Guid, MarketplaceListingState>() : await db.MarketplaceListingStates.Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && ids.Contains(x.VariantId)).ToDictionaryAsync(x => x.VariantId, cancellationToken);
        if (listings.Count == 0 || states.Count != listings.Count) return await MarkPublicationResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "PRODUCT_ARCHIVE_STATE_INCOMPLETE", JobExecutionResult.ManualReview("PRODUCT_ARCHIVE_STATE_INCOMPLETE", "Arşiv uzlaştırması için listing state kayıtları eksik."), cancellationToken);
        if (states.Values.Any(x => !string.Equals(x.PayloadHash, payload.PayloadHash, StringComparison.Ordinal))) return JobExecutionResult.Blocked("PRODUCT_ARCHIVE_SUPERSEDED", "Daha yeni listing işlemi bulundu; eski arşiv işi uzak çağrı yapmadan durduruldu.");
        if (timeProvider.GetUtcNow() > payload.DeadlineAt) return await MarkPublicationResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "PRODUCT_ARCHIVE_DEADLINE_EXPIRED", JobExecutionResult.ManualReview("PRODUCT_ARCHIVE_DEADLINE_EXPIRED", "Ürün arşiv durumu belirlenen pencerede kesinleşmedi."), cancellationToken);

        var phase = payload.Phase.Trim().ToUpperInvariant();
        if (phase == "SUBMIT")
        {
            if (!await ExternalWriteMasterAllowedAsync(tenantId, connectionId, cancellationToken)) return await MarkPublicationResult(tenantId, connectionId, profile, "ARCHIVE_BLOCKED", "EXTERNAL_WRITES_DISABLED", JobExecutionResult.Blocked("EXTERNAL_WRITES_DISABLED", "Dış yazma anahtarı kapalı; pazar yeri isteği gönderilmedi."), cancellationToken);
            var effectKey = job.EffectIdempotencyKey;
            if (await db.ExternalEffectRecords.AnyAsync(x => x.TenantId == tenantId && x.EffectType == MarketplaceJobTypes.ProductArchive && x.IdempotencyKey == effectKey, cancellationToken)) return await MarkPublicationResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "EXTERNAL_EFFECT_AMBIGUOUS", JobExecutionResult.ManualReview("EXTERNAL_EFFECT_AMBIGUOUS", "Önceki arşiv çağrısının sonucu kesinleştirilemedi."), cancellationToken);
            var effect = new ExternalEffectRecord { Id = Guid.CreateVersion7(), TenantId = tenantId, EffectType = MarketplaceJobTypes.ProductArchive, IdempotencyKey = effectKey, CreatedAt = timeProvider.GetUtcNow() };
            db.ExternalEffectRecords.Add(effect); await db.SaveChangesAsync(cancellationToken);
            TrackRequest();
            var submit = await products.ArchiveAsync(Context(tenantId, connectionId, correlationId, effectKey), payload.PayloadJson, cancellationToken);
            if (!submit.IsSuccess)
            {
                TrackResultFailure(submit.Error);
                if (IsAmbiguous(submit.Error!)) return await MarkPublicationResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "EXTERNAL_EFFECT_AMBIGUOUS", JobExecutionResult.ManualReview("EXTERNAL_EFFECT_AMBIGUOUS", "Arşiv çağrısının uzak tarafta uygulanıp uygulanmadığı kesinleştirilemedi.", submit.Error!.RemoteRequestId), cancellationToken);
                db.ExternalEffectRecords.Remove(effect); await db.SaveChangesAsync(cancellationToken);
                return await MarkPublicationResult(tenantId, connectionId, profile, "ARCHIVE_BLOCKED", submit.Error!.Code, JobExecutionResult.FromAdapterError(submit.Error), cancellationToken);
            }
            effect.CompletedAt = timeProvider.GetUtcNow(); var op = submit.Value!;
            var next = payload with { Phase = "POLL", ExternalOperationId = op.ExternalOperationId };
            job.PayloadJson = JsonSerializer.Serialize(next); job.PayloadHash = Hash(job.PayloadJson); profile.ActualStatus = "ARCHIVE_BATCH_SUBMITTED"; profile.Version++;
            await db.SaveChangesAsync(cancellationToken);
            return JobExecutionResult.Retry("PRODUCT_ARCHIVE_BATCH_PENDING", "Trendyol arşiv batch sonucu bekleniyor.", TimeSpan.FromSeconds(15), op.ExternalOperationId);
        }
        if (phase == "POLL")
        {
            if (string.IsNullOrWhiteSpace(payload.ExternalOperationId)) return JobExecutionResult.Blocked("PRODUCT_ARCHIVE_PHASE_INVALID", "Arşiv poll fazında batch kimliği eksik.");
            TrackRequest();
            var result = await products.GetOperationAsync(Context(tenantId, connectionId, correlationId, $"{job.EffectIdempotencyKey}:poll"), payload.ExternalOperationId, cancellationToken);
            if (!result.IsSuccess) { TrackResultFailure(result.Error); return JobExecutionResult.FromAdapterError(result.Error!); }
            TrackReceived();
            var remote = result.Value!;
            if (remote.Status.Equals("IN_PROGRESS", StringComparison.OrdinalIgnoreCase)) return JobExecutionResult.Retry("PRODUCT_ARCHIVE_BATCH_PENDING", "Trendyol arşiv batch sonucu bekleniyor.", TimeSpan.FromSeconds(20), payload.ExternalOperationId);
            if (!remote.Status.Equals("COMPLETED", StringComparison.OrdinalIgnoreCase)) return await MarkPublicationResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "PRODUCT_ARCHIVE_BATCH_STATUS_UNKNOWN", JobExecutionResult.ManualReview("PRODUCT_ARCHIVE_BATCH_STATUS_UNKNOWN", "Arşiv batch servisi tanınmayan durum döndürdü.", payload.ExternalOperationId), cancellationToken);
            if (remote.Lines.Count == 0) return await MarkPublicationResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "PRODUCT_ARCHIVE_BATCH_CONTRACT_INVALID", JobExecutionResult.ManualReview("PRODUCT_ARCHIVE_BATCH_CONTRACT_INVALID", "Arşiv batch sonucu satır içermiyor.", payload.ExternalOperationId), cancellationToken);
            var byBarcode = remote.Lines.Where(x => !string.IsNullOrWhiteSpace(x.ExternalKey)).GroupBy(x => x.ExternalKey, StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => x.Last(), StringComparer.OrdinalIgnoreCase);
            foreach (var listing in listings)
            {
                if (string.IsNullOrWhiteSpace(listing.ExternalBarcode) || !byBarcode.TryGetValue(listing.ExternalBarcode, out var line)) return await MarkPublicationResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "PRODUCT_ARCHIVE_BATCH_CONTRACT_INVALID", JobExecutionResult.ManualReview("PRODUCT_ARCHIVE_BATCH_CONTRACT_INVALID", "Arşiv batch sonucu tüm barkodları içermiyor.", payload.ExternalOperationId), cancellationToken);
                var status = line.Succeeded ? (payload.Archived ? "ARCHIVE_ACCEPTED" : "UNARCHIVE_ACCEPTED") : "ARCHIVE_REJECTED";
                var code = line.Succeeded ? null : SafeCode(line.ErrorCode) ?? "PRODUCT_ARCHIVE_REJECTED";
                listing.ActualStatus = status; listing.RejectionCode = code;
                if (states.TryGetValue(listing.VariantId, out var state)) { state.ActualStatus = status; state.LastRejectionCode = code; state.Version++; }
            }
            var reconcile = payload with { Phase = "RECONCILE" };
            job.PayloadJson = JsonSerializer.Serialize(reconcile); job.PayloadHash = Hash(job.PayloadJson); profile.ActualStatus = "ARCHIVE_RECONCILING"; profile.Version++;
            await db.SaveChangesAsync(cancellationToken);
            return JobExecutionResult.Retry("PRODUCT_ARCHIVE_RECONCILE_PENDING", "Trendyol arşiv durumu read-back ile doğrulanacak.", TimeSpan.FromSeconds(30));
        }
        if (phase != "RECONCILE") return JobExecutionResult.Blocked("PRODUCT_ARCHIVE_PHASE_INVALID", "Ürün arşiv işi bilinmeyen bir fazda.");

        var pending = 0; var succeeded = 0; var rejected = listings.Count(x => x.ActualStatus == "ARCHIVE_REJECTED"); string? firstCode = listings.Select(x => x.RejectionCode).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
        foreach (var listing in listings.Where(x => x.ActualStatus != "ARCHIVE_REJECTED"))
        {
            if (string.IsNullOrWhiteSpace(listing.ExternalBarcode)) return await MarkPublicationResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "REMOTE_BARCODE_REQUIRED", JobExecutionResult.ManualReview("REMOTE_BARCODE_REQUIRED", "Arşiv read-back için barkod eksik."), cancellationToken);
            TrackRequest();
            var result = await products.GetPublicationStatusAsync(Context(tenantId, connectionId, correlationId, $"archive-readback:{profile.Id:N}:{listing.ExternalBarcode}"), listing.ExternalBarcode, cancellationToken);
            if (!result.IsSuccess) { TrackResultFailure(result.Error); var mapped = JobExecutionResult.FromAdapterError(result.Error!); if (mapped.Kind == JobCompletionKind.Retry) return mapped; return await MarkPublicationResult(tenantId, connectionId, profile, "MANUAL_REVIEW", result.Error!.Code, JobExecutionResult.ManualReview(result.Error.Code, result.Error.SafeMessage, result.Error.RemoteRequestId), cancellationToken); }
            TrackReceived();
            var desiredReached = payload.Archived ? result.Value!.Status == "ARCHIVED" : result.Value!.Status == "APPROVED";
            if (desiredReached)
            {
                var status = payload.Archived ? "ARCHIVED" : "LIVE"; listing.ActualStatus = status; listing.RejectionCode = null;
                if (states.TryGetValue(listing.VariantId, out var state)) { state.ActualStatus = status; state.LastRejectionCode = null; state.Version++; }
                succeeded++;
            }
            else if (result.Value!.Status is "NOT_FOUND" or "PENDING_APPROVAL" || (payload.Archived && result.Value.Status == "APPROVED") || (!payload.Archived && result.Value.Status == "ARCHIVED")) pending++;
            else { listing.ActualStatus = "MANUAL_REVIEW"; listing.RejectionCode = "PRODUCT_ARCHIVE_READBACK_CONFLICT"; if (states.TryGetValue(listing.VariantId, out var state)) { state.ActualStatus = "MANUAL_REVIEW"; state.LastRejectionCode = listing.RejectionCode; state.Version++; } firstCode ??= listing.RejectionCode; rejected++; }
        }
        profile.DesiredStatus = payload.Archived ? "ARCHIVED" : "LIVE";
        profile.ActualStatus = pending > 0 ? (succeeded > 0 ? "ARCHIVE_PARTIAL_PENDING" : "ARCHIVE_RECONCILING") : rejected == 0 ? (payload.Archived ? "ARCHIVED" : "LIVE") : succeeded == 0 ? "ARCHIVE_REJECTED" : "ARCHIVE_PARTIAL_FAILURE";
        profile.LastRejectionCode = firstCode; profile.Version++; await db.SaveChangesAsync(cancellationToken);
        if (pending > 0) return JobExecutionResult.Retry("PRODUCT_ARCHIVE_RECONCILE_PENDING", "Trendyol arşiv durumu henüz kesinleşmedi.", TimeSpan.FromMinutes(2));
        if (rejected == 0) return JobExecutionResult.Success();
        return succeeded == 0 ? JobExecutionResult.Blocked("PRODUCT_ARCHIVE_REJECTED", "Trendyol arşiv işlemi tüm varyantlar için başarısız oldu.") : JobExecutionResult.Blocked("PRODUCT_ARCHIVE_PARTIAL_FAILURE", "Trendyol arşiv işlemi bazı varyantlar için başarısız oldu.");
    }

    private async Task<JobExecutionResult> SyncPriceInventory(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, CancellationToken cancellationToken)
    {
        PriceInventoryJobPayload? payload;
        try { payload = JsonSerializer.Deserialize<PriceInventoryJobPayload>(payloadJson, JsonOptions); }
        catch (JsonException) { return JobExecutionResult.Blocked("PRICE_INVENTORY_PAYLOAD_INVALID", "Fiyat-stok işi payload sözleşmesini sağlamıyor."); }
        if (payload is null || payload.JobId == Guid.Empty || payload.ConnectionId != connectionId || string.IsNullOrWhiteSpace(payload.PayloadHash) || string.IsNullOrWhiteSpace(payload.PayloadJson)) return JobExecutionResult.Blocked("PRICE_INVENTORY_PAYLOAD_INVALID", "Fiyat-stok işi zorunlu alanları eksik.");
        var job = await db.IntegrationJobs.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == payload.JobId && x.ConnectionId == connectionId && x.JobType == MarketplaceJobTypes.PriceInventorySync, cancellationToken);
        if (job is null) return JobExecutionResult.Blocked("PRICE_INVENTORY_STATE_MISSING", "Fiyat-stok işi bulunamadı.");
        var phase = payload.Phase.Trim().ToUpperInvariant();
        if (phase == "SUBMIT")
        {
            var connection = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == connectionId, cancellationToken);
            if (connection is null) return JobExecutionResult.Blocked("CONNECTION_NOT_FOUND", "Bağlantı bulunamadı.");
            if (!WritesEnabled(connection.SettingsJson))
                return JobExecutionResult.Blocked("EXTERNAL_WRITES_DISABLED", "Dış yazma kapalı olduğu için fiyat-stok gönderimi çalıştırılmadı.");
            var isHepsiburadaConnection = string.Equals(connection.PlatformCode, "HEPSIBURADA", StringComparison.OrdinalIgnoreCase);
            var requiredWritePolicies = isHepsiburadaConnection
                ? new[] { MarketplaceExternalWritePolicies.Price, MarketplaceExternalWritePolicies.Stock }
                : new[] { payload.VariantId.HasValue ? MarketplaceExternalWritePolicies.Stock : MarketplaceExternalWritePolicies.Price };
            foreach (var writePolicy in requiredWritePolicies)
                if (!await ExternalWritePolicyEnabledAsync(tenantId, connectionId, writePolicy, cancellationToken))
                    return JobExecutionResult.Blocked("EXTERNAL_WRITE_POLICY_DISABLED", writePolicy == MarketplaceExternalWritePolicies.Stock ? "Stok dış yazma akışı kapalı; pazar yeri isteği gönderilmedi." : "Fiyat dış yazma akışı kapalı; pazar yeri isteği gönderilmedi.");
            var current = await new PriceInventoryComposer(db).BuildAsync(tenantId, connectionId, cancellationToken, payload.VariantId, payload.ProductId);
            if (!current.Succeeded)
            {
                if (current.Error!.Code == "NO_EXTERNAL_CHANGES") return JobExecutionResult.Success();
                return JobExecutionResult.Blocked(current.Error.Code, current.Error.Message);
            }
            if (!string.Equals(current.Value!.PayloadHash, payload.PayloadHash, StringComparison.Ordinal)) return JobExecutionResult.Blocked("PRICE_INVENTORY_SUPERSEDED", "Fiyat veya stok yeni bir sürüme geçti; eski payload uzak sisteme gönderilmedi.");
            if (await db.ExternalEffectRecords.AnyAsync(x => x.TenantId == tenantId && x.EffectType == MarketplaceJobTypes.PriceInventorySync && x.IdempotencyKey == job.EffectIdempotencyKey, cancellationToken)) return JobExecutionResult.ManualReview("EXTERNAL_EFFECT_AMBIGUOUS", "Önceki fiyat-stok çağrısının sonucu kesinleştirilemedi; tekrar gönderim engellendi.");
            var effect = new ExternalEffectRecord { Id = Guid.CreateVersion7(), TenantId = tenantId, EffectType = MarketplaceJobTypes.PriceInventorySync, IdempotencyKey = job.EffectIdempotencyKey, CreatedAt = timeProvider.GetUtcNow() };
            db.ExternalEffectRecords.Add(effect); await db.SaveChangesAsync(cancellationToken);
            TrackRequest();
            var submit = await inventoryPrice.PushPriceAndInventoryAsync(Context(tenantId, connectionId, correlationId, job.EffectIdempotencyKey) with { Operation = IntegrationOperation.Automatic }, payload.PayloadJson, cancellationToken);
            if (!submit.IsSuccess)
            {
                TrackResultFailure(submit.Error);
                if (IsAmbiguous(submit.Error!)) return JobExecutionResult.ManualReview("EXTERNAL_EFFECT_AMBIGUOUS", "Fiyat-stok çağrısının uygulanıp uygulanmadığı kesinleştirilemedi.", submit.Error!.RemoteRequestId);
                db.ExternalEffectRecords.Remove(effect); await db.SaveChangesAsync(cancellationToken); return JobExecutionResult.FromAdapterError(submit.Error!);
            }
            effect.CompletedAt = timeProvider.GetUtcNow(); var op = submit.Value!;
            var next = payload with { Phase = "POLL", ExternalOperationId = op.ExternalOperationId, SubmittedAt = op.SubmittedAt };
            job.PayloadJson = JsonSerializer.Serialize(next); job.PayloadHash = Hash(job.PayloadJson); await db.SaveChangesAsync(cancellationToken);
            return JobExecutionResult.Retry("PRICE_INVENTORY_BATCH_PENDING", "Trendyol fiyat-stok batch sonucu bekleniyor.", TimeSpan.FromSeconds(15), op.ExternalOperationId);
        }
        if (phase != "POLL" || string.IsNullOrWhiteSpace(payload.ExternalOperationId) || payload.SubmittedAt is null) return JobExecutionResult.Blocked("PRICE_INVENTORY_PHASE_INVALID", "Fiyat-stok işi bilinmeyen bir fazda.");
        if (timeProvider.GetUtcNow() - payload.SubmittedAt.Value > TimeSpan.FromHours(4)) return JobExecutionResult.ManualReview("PRICE_INVENTORY_BATCH_EXPIRED", "Fiyat-stok batch sonucu dört saatlik pencerede alınamadı.", payload.ExternalOperationId);
        TrackRequest();
        var operation = await products.GetOperationAsync(Context(tenantId, connectionId, correlationId, $"{job.EffectIdempotencyKey}:poll"), payload.ExternalOperationId, cancellationToken);
        if (!operation.IsSuccess) { TrackResultFailure(operation.Error); return JobExecutionResult.FromAdapterError(operation.Error!); }
        TrackReceived();
        if (operation.Value!.Status.Equals("IN_PROGRESS", StringComparison.OrdinalIgnoreCase)) return JobExecutionResult.Retry("PRICE_INVENTORY_BATCH_PENDING", "Trendyol fiyat-stok batch sonucu bekleniyor.", TimeSpan.FromSeconds(20), payload.ExternalOperationId);
        if (!operation.Value.Status.Equals("COMPLETED", StringComparison.OrdinalIgnoreCase)) return JobExecutionResult.ManualReview("PRICE_INVENTORY_BATCH_STATUS_UNKNOWN", "Fiyat-stok batch servisi tanınmayan durum döndürdü.", payload.ExternalOperationId);
        var platformCode = await db.PlatformConnections.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == connectionId).Select(x => x.PlatformCode).SingleOrDefaultAsync(cancellationToken);
        var isHepsiburada = string.Equals(platformCode, "HEPSIBURADA", StringComparison.OrdinalIgnoreCase);
        var maximumLineCount = isHepsiburada ? 4000 : 1000;
        var duplicateKeys = isHepsiburada
            ? payload.Lines.Select(x => x.MerchantSku).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).Count()
            : payload.Lines.Select(x => x.Barcode).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        if (payload.Lines.Count == 0 || payload.Lines.Count > maximumLineCount || duplicateKeys != payload.Lines.Count)
            return JobExecutionResult.ManualReview("PRICE_INVENTORY_PAYLOAD_INVALID", "Fiyat-stok job satırları eksik, yinelenen veya limit dışı.");
        var offerIds = payload.Lines.Select(x => x.OfferId).ToArray();
        var offers = await db.ChannelOffers.Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && offerIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, cancellationToken);
        if (offers.Count != payload.Lines.Count) return JobExecutionResult.ManualReview("PRICE_INVENTORY_STATE_INCOMPLETE", "Fiyat-stok uzlaştırmasında teklif kayıtları eksik.");
        var variantIds = payload.Lines.Select(x => x.VariantId).Distinct().ToArray();
        var inventory = await db.InventoryItems.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.LocationCode == "MAIN" && variantIds.Contains(x.VariantId))
            .ToDictionaryAsync(x => x.VariantId, cancellationToken);
        if (inventory.Count != variantIds.Length) return JobExecutionResult.ManualReview("PRICE_INVENTORY_STATE_INCOMPLETE", "Fiyat-stok uzlaştırmasında MAIN stok projection kayıtları eksik.");
        foreach (var line in payload.Lines)
        {
            if (!PriceInventoryOutboxPolicy.IsCurrent(line, inventory[line.VariantId].ProjectionVersion, offers[line.OfferId].PriceVersion))
                return JobExecutionResult.Blocked("PRICE_INVENTORY_SUPERSEDED", "Batch sonucu alınırken fiyat veya stok daha yeni bir sürüme geçti; eski sonuç güncel kayda uygulanmadı.");
        }
        var lineByBarcode = operation.Value.Lines.Where(x => !string.IsNullOrWhiteSpace(x.ExternalKey)).GroupBy(x => x.ExternalKey, StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => x.Last(), StringComparer.OrdinalIgnoreCase);
        if (isHepsiburada && operation.Value.Lines.Any(remoteLine => !payload.Lines.Any(localLine =>
                string.Equals(localLine.MerchantSku, remoteLine.ExternalKey, StringComparison.OrdinalIgnoreCase)
                || string.Equals(localLine.HepsiburadaSku, remoteLine.ExternalKey, StringComparison.OrdinalIgnoreCase))))
            return JobExecutionResult.ManualReview("PRICE_INVENTORY_BATCH_CONTRACT_INVALID", "Hepsiburada fiyat-stok sonucu gönderilmeyen bir merchantSku/hbSku içeriyor.", payload.ExternalOperationId);
        var success = 0; var failed = 0;
        foreach (var line in payload.Lines)
        {
            var found = isHepsiburada
                ? line.MerchantSku is not null && lineByBarcode.TryGetValue(line.MerchantSku, out var remoteLineByMerchantSku)
                    ? (Found: true, Remote: remoteLineByMerchantSku)
                    : line.HepsiburadaSku is not null && lineByBarcode.TryGetValue(line.HepsiburadaSku, out var remoteLineByHbSku)
                        ? (Found: true, Remote: remoteLineByHbSku)
                        : (Found: false, Remote: (RemoteOperationLine?)null)
                : lineByBarcode.TryGetValue(line.Barcode, out var remoteLineByBarcode)
                    ? (Found: true, Remote: remoteLineByBarcode)
                    : (Found: false, Remote: (RemoteOperationLine?)null);
            if (!found.Found && !isHepsiburada) return JobExecutionResult.ManualReview("PRICE_INVENTORY_BATCH_CONTRACT_INVALID", "Fiyat-stok batch sonucu tüm barkodları içermiyor.", payload.ExternalOperationId);
            var lineSucceeded = !found.Found || found.Remote!.Succeeded;
            var offer = offers[line.OfferId];
            if (lineSucceeded) { offer.LastPriceHash = line.PriceHash; offer.LastStockProjectionVersion = line.ProjectionVersion; offer.Version++; success++; }
            else { failed++; await RecordIssue(tenantId, $"price-inventory:{connectionId}:{line.Barcode}:{SafeCode(found.Remote!.ErrorCode)}", SafeCode(found.Remote.ErrorCode) ?? "PRICE_INVENTORY_LINE_REJECTED", $"Pazar yeri fiyat-stok satırı reddedildi: {line.Barcode}.", cancellationToken); }
        }
        await db.SaveChangesAsync(cancellationToken);
        if (failed == 0) return JobExecutionResult.Success();
        return success == 0 ? JobExecutionResult.Blocked("PRICE_INVENTORY_REJECTED", "Pazar yeri fiyat-stok batch içindeki tüm satırları reddetti.", payload.ExternalOperationId) : JobExecutionResult.ManualReview("PRICE_INVENTORY_PARTIAL_FAILURE", "Pazar yeri fiyat-stok batch kısmi başarısızlıkla tamamlandı.", payload.ExternalOperationId);
    }

    private async Task<JobExecutionResult> LabelCapabilityProbe(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, CancellationToken cancellationToken)
    {
        CapabilityProbeJobPayload? payload;
        try { payload = JsonSerializer.Deserialize<CapabilityProbeJobPayload>(payloadJson, JsonOptions); }
        catch (JsonException) { return JobExecutionResult.Blocked("CAPABILITY_PROBE_PAYLOAD_INVALID", "Stage capability canary payloadı geçersiz."); }
        if (payload is null || payload.JobId == Guid.Empty || payload.PackageId == Guid.Empty || payload.ActorUserId == Guid.Empty || payload.CapabilityCode is not (MarketplaceCapabilities.LabelRead or MarketplaceCapabilities.LabelWrite)) return JobExecutionResult.Blocked("CAPABILITY_PROBE_PAYLOAD_INVALID", "Stage capability canary zorunlu alanları geçersiz.");
        var connection = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == connectionId && x.PlatformCode == "TRENDYOL", cancellationToken);
        if (connection is null || !string.Equals(connection.Environment, "STAGE", StringComparison.OrdinalIgnoreCase) || !string.Equals(connection.ExternalStoreId, "2738", StringComparison.Ordinal)) return JobExecutionResult.Blocked("STAGE_CONNECTION_REQUIRED", "Capability canary yalnız Trendyol STAGE seller 2738 bağlantısında çalışır.");
        var package = await db.ShipmentPackages.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == payload.PackageId && x.ConnectionId == connectionId, cancellationToken);
        if (package is null || string.IsNullOrWhiteSpace(package.CargoTrackingNumber)) return JobExecutionResult.Blocked("CAPABILITY_PROBE_TARGET_INVALID", "Canary için takip numaralı Stage paketi bulunamadı.");
        var context = Context(tenantId, connectionId, correlationId, $"stage-capability-probe:{payload.JobId:N}") with { IsStageCapabilityProbe = true };
        if (payload.CapabilityCode == MarketplaceCapabilities.LabelWrite)
        {
            if (!CommonLabelCarrierPolicy.Supports(package.CargoProviderExternalId)) return JobExecutionResult.Blocked("COMMON_LABEL_CARRIER_UNSUPPORTED", "LABEL_WRITE canary yalnız Trendyol öder Aras Kargo veya TEX Stage paketi üzerinde çalışır.");
            var order = await db.Orders.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == package.OrderId, cancellationToken);
            var latestFixture = await db.AuditLogs.AsNoTracking().Where(x => x.TenantId == tenantId && x.Action == "STAGE_TEST_ORDER_CREATED" && x.TargetType == "StageTestOrder").OrderByDescending(x => x.CreatedAt).Select(x => x.TargetId).FirstOrDefaultAsync(cancellationToken);
            if (order is null || !string.Equals(order.OrderNumber, latestFixture, StringComparison.Ordinal)) return JobExecutionResult.Blocked("STAGE_LABEL_FRESH_FIXTURE_REQUIRED", "LABEL_WRITE canary yalnız en son oluşturulan auditli Stage Test Order paketi üzerinde çalışır.");
            var lines = await db.OrderLines.AsNoTracking().Where(x => x.TenantId == tenantId && x.OrderId == package.OrderId).Select(x => new { x.ExternalLineId, x.OrderedQuantity }).ToListAsync(cancellationToken);
            if (lines.Count != 1 || !long.TryParse(lines[0].ExternalLineId, out var lineId) || lines[0].OrderedQuantity <= 0 || lines[0].OrderedQuantity != decimal.Truncate(lines[0].OrderedQuantity) || lines[0].OrderedQuantity > int.MaxValue) return JobExecutionResult.Blocked("STAGE_LABEL_PICKING_PAYLOAD_INVALID", "Taze Stage fixture için tek ve geçerli satır kimliği/miktarı gerekir.");
            TrackRequest();
            var picking = await orders.ExecutePackageActionAsync(context, new PackageActionCommand(package.ExternalPackageId, "PICKING", JsonSerializer.Serialize(new { lines = new[] { new { lineId, quantity = (int)lines[0].OrderedQuantity } }, @params = new { }, status = "Picking" })), cancellationToken);
            if (!picking.IsSuccess) { TrackResultFailure(picking.Error); throw JobProcessingException.FromAdapter(picking.Error!); }
            TrackRequest();
            var created = await orders.CreateCommonLabelAsync(context, new CommonLabelRequest(package.CargoTrackingNumber, payload.BoxQuantity, payload.VolumetricHeight), cancellationToken);
            if (!created.IsSuccess) { TrackResultFailure(created.Error); throw JobProcessingException.FromAdapter(created.Error!); }
        }
        TrackRequest();
        var document = await orders.GetCommonLabelAsync(context, package.CargoTrackingNumber, cancellationToken);
        if (!document.IsSuccess) { TrackResultFailure(document.Error); throw JobProcessingException.FromAdapter(document.Error!); }
        TrackReceived();
        var hash = Convert.ToHexString(SHA256.HashData(document.Value!.Content));
        var codes = payload.CapabilityCode == MarketplaceCapabilities.LabelWrite ? new[] { MarketplaceCapabilities.LabelRead, MarketplaceCapabilities.LabelWrite, MarketplaceCapabilities.ShipmentWrite } : new[] { MarketplaceCapabilities.LabelRead };
        var now = timeProvider.GetUtcNow();
        foreach (var code in codes)
        {
            var capability = await db.PlatformCapabilities.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.Code == code, cancellationToken);
            if (capability is null) continue;
            capability.SupportLevel = CapabilitySupportLevel.Supported;
            capability.SourceUrl = code == MarketplaceCapabilities.LabelRead ? "https://developers.trendyol.com/v2.0/docs/common-label-barcode-get-integration" : "https://developers.trendyol.com/v2.0/docs/common-label-barcode-request-createcommonlabel";
            capability.SourceVersion = "V2";
            capability.Environment = connection.Environment;
            capability.StoreScope = connection.ExternalStoreId;
            capability.ConstraintsJson = code == MarketplaceCapabilities.ShipmentWrite ? JsonSerializer.Serialize(new { allowedActions = new[] { "PICKING" } }) : JsonSerializer.Serialize(new { formats = new[] { document.Value.Format } });
            capability.EvidenceNote = code == MarketplaceCapabilities.ShipmentWrite
                ? "SHIPMENT_WRITE Stage canary, en son auditli test fixture üzerinde resmî PICKING isteğini ve ardından ortak etiket create/read-back zincirini başarıyla doğruladı."
                : $"{code} Stage canary gerçek paket/etiket read-back ile başarılı; private fixture SHA-256 kaydedildi.";
            capability.FixtureChecksum = hash;
            capability.VerifiedAt = now;
            capability.Version++;
            db.AuditLogs.Add(new AuditLog { TenantId = tenantId, ActorUserId = payload.ActorUserId, Action = "CAPABILITY_STAGE_PROBE_SUCCEEDED", TargetType = "PlatformCapability", TargetId = capability.Id.ToString("D"), Reason = $"{code}:package:{package.Id:D}", CorrelationId = correlationId, CreatedAt = now });
        }
        await db.SaveChangesAsync(cancellationToken);
        return JobExecutionResult.Success();
    }

    private async Task<JobExecutionResult> CreateStageTestOrder(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, CancellationToken cancellationToken)
    {
        StageTestOrderJobPayload? payload;
        try { payload = JsonSerializer.Deserialize<StageTestOrderJobPayload>(payloadJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
        catch (JsonException) { return JobExecutionResult.Blocked("STAGE_TEST_ORDER_PAYLOAD_INVALID", "Stage test siparişi payloadı geçersiz."); }
        if (payload is null || payload.JobId == Guid.Empty || payload.ActorUserId == Guid.Empty || string.IsNullOrWhiteSpace(payload.Barcode)) return JobExecutionResult.Blocked("STAGE_TEST_ORDER_PAYLOAD_INVALID", "Stage test siparişi zorunlu alanları eksik.");
        var connection = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == connectionId && (x.PlatformCode == "TRENDYOL" || x.PlatformCode == "HEPSIBURADA"), cancellationToken);
        if (connection is null || !string.Equals(connection.Environment, "STAGE", StringComparison.OrdinalIgnoreCase)
            || connection.PlatformCode == "TRENDYOL" && !string.Equals(connection.ExternalStoreId, "2738", StringComparison.Ordinal))
            return JobExecutionResult.Blocked("STAGE_TEST_ORDER_SCOPE_REQUIRED", "Stage test siparişi yalnız desteklenen Stage bağlantılarında kullanılabilir.");
        var sku = payload.Barcode;
        if (connection.PlatformCode == "HEPSIBURADA")
        {
            var remoteSkus = await (from listing in db.ChannelListingVariants.AsNoTracking()
                                    join profile in db.ChannelListingProfiles.AsNoTracking() on new { listing.TenantId, ProfileId = listing.ProfileId } equals new { profile.TenantId, ProfileId = profile.Id }
                                    join state in db.MarketplaceListingStates.AsNoTracking() on new { listing.TenantId, profile.ConnectionId, listing.VariantId } equals new { state.TenantId, state.ConnectionId, state.VariantId }
                                    join link in db.MarketplaceVariantLinks.AsNoTracking() on new { listing.TenantId, profile.ConnectionId, listing.VariantId } equals new { link.TenantId, link.ConnectionId, link.VariantId }
                                    where listing.TenantId == tenantId && profile.ConnectionId == connectionId && profile.Enabled && state.ActualStatus == "LIVE"
                                          && listing.ExternalBarcode == payload.Barcode && !string.IsNullOrWhiteSpace(link.ExternalId)
                                    select link.ExternalId).Distinct().ToListAsync(cancellationToken);
            if (remoteSkus.Count != 1) return JobExecutionResult.Blocked("HEPSIBURADA_STAGE_LISTING_AMBIGUOUS", "Test sipariş barkodu için tek bir Hepsiburada hbSku eşleşmesi bulunamadı.");
            sku = remoteSkus[0];
        }
        TrackRequest();
        var result = await orders.CreateStageTestOrderAsync(Context(tenantId, connectionId, correlationId, $"stage-test-order:{payload.JobId:N}") with { IsStageCapabilityProbe = true }, sku, cancellationToken);
        if (!result.IsSuccess) { TrackResultFailure(result.Error); throw JobProcessingException.FromAdapter(result.Error!); }
        TrackReceived();
        db.AuditLogs.Add(new AuditLog { TenantId = tenantId, ActorUserId = payload.ActorUserId, Action = "STAGE_TEST_ORDER_CREATED", TargetType = "StageTestOrder", TargetId = result.Value!.OrderNumber, Reason = $"fresh-label-write-fixture:{connection.PlatformCode}", CorrelationId = correlationId, CreatedAt = timeProvider.GetUtcNow() });
        await db.SaveChangesAsync(cancellationToken);
        return JobExecutionResult.Success();
    }

    private async Task<JobExecutionResult> CommonLabel(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, CancellationToken cancellationToken)
    {
        CommonLabelJobPayload? payload;
        try { payload = JsonSerializer.Deserialize<CommonLabelJobPayload>(payloadJson, JsonOptions); }
        catch (JsonException) { return JobExecutionResult.Blocked("COMMON_LABEL_PAYLOAD_INVALID", "Ortak etiket işi payload sözleşmesini sağlamıyor."); }
        if (payload is null || payload.JobId == Guid.Empty || payload.PackageId == Guid.Empty || payload.BoxQuantity is < 1 or > 50 || payload.VolumetricHeight is < 0 or > 10000 || payload.DeadlineAt <= payload.StartedAt || string.IsNullOrWhiteSpace(payload.Phase)) return JobExecutionResult.Blocked("COMMON_LABEL_PAYLOAD_INVALID", "Ortak etiket işi zorunlu alanları eksik veya sınır dışında.");
        var job = await db.IntegrationJobs.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == payload.JobId && x.ConnectionId == connectionId && x.JobType == MarketplaceJobTypes.CommonLabel, cancellationToken);
        var package = await db.ShipmentPackages.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == payload.PackageId && x.ConnectionId == connectionId, cancellationToken);
        var attempt = await db.ShipmentDocumentAttempts.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.IdempotencyKey == (job == null ? "" : job.EffectIdempotencyKey), cancellationToken);
        if (job is null || package is null || attempt is null) return JobExecutionResult.Blocked("COMMON_LABEL_STATE_MISSING", "Ortak etiket işi, paket veya deneme kaydı bulunamadı.");
        var platform = await db.PlatformConnections.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == connectionId).Select(x => x.PlatformCode).SingleOrDefaultAsync(cancellationToken);
        var hepsiburada = platform == "HEPSIBURADA";
        var requestedFormat = (payload.Format ?? "ZPL").Trim().ToUpperInvariant();
        var validFormat = hepsiburada
            ? requestedFormat is "ZPL" or "BASE64ZPL" or "PDF" or "PNG" or "JPG"
            : requestedFormat == "ZPL";
        if (!validFormat) return JobExecutionResult.Blocked("COMMON_LABEL_FORMAT_UNSUPPORTED", "Ortak etiket işi desteklenmeyen biçim istiyor.");
        var labelLookupKey = hepsiburada ? package.ExternalPackageId : package.CargoTrackingNumber;
        if (string.IsNullOrWhiteSpace(labelLookupKey)) return JobExecutionResult.Blocked("CARGO_TRACKING_REQUIRED", "Etiket için paket veya kargo takip numarası gerekir.");
        if (hepsiburada ? !CommonLabelCarrierPolicy.SupportsHepsiburada(package.CargoProviderExternalId) : !CommonLabelCarrierPolicy.Supports(package.CargoProviderExternalId)) return JobExecutionResult.Blocked("COMMON_LABEL_CARRIER_UNSUPPORTED", hepsiburada ? "Hepsiburada ortak etiket yalnız HepsiJet ve Aras paketlerinde desteklenir." : "Ortak etiket yalnız Trendyol öder Aras Kargo veya TEX gönderilerinde kullanılabilir.");
        if (timeProvider.GetUtcNow() > payload.DeadlineAt) { attempt.Status = "MANUAL_REVIEW"; attempt.ErrorCode = "COMMON_LABEL_DEADLINE_EXPIRED"; attempt.CompletedAt = timeProvider.GetUtcNow(); await db.SaveChangesAsync(cancellationToken); return JobExecutionResult.ManualReview("COMMON_LABEL_DEADLINE_EXPIRED", "Ortak etiket belirlenen pencerede hazır olmadı."); }
        var phase = payload.Phase.Trim().ToUpperInvariant();
        if (phase == "SUBMIT")
        {
            if (hepsiburada)
            {
                var nextRead = payload with { Phase = "POLL" };
                job.PayloadJson = JsonSerializer.Serialize(nextRead);
                job.PayloadHash = Hash(job.PayloadJson);
                job.AvailableAt = timeProvider.GetUtcNow();
                attempt.Status = "POLLING";
                await db.SaveChangesAsync(cancellationToken);
                return JobExecutionResult.Retry("COMMON_LABEL_READ_PENDING", "Hepsiburada paket etiketi salt okunur API’den alınıyor.", TimeSpan.FromSeconds(1));
            }
            if (await db.ExternalEffectRecords.AnyAsync(x => x.TenantId == tenantId && x.EffectType == MarketplaceJobTypes.CommonLabel && x.IdempotencyKey == job.EffectIdempotencyKey, cancellationToken)) return JobExecutionResult.ManualReview("EXTERNAL_EFFECT_AMBIGUOUS", "Önceki ortak etiket oluşturma çağrısının sonucu kesinleştirilemedi.");
            var effect = new ExternalEffectRecord { Id = Guid.CreateVersion7(), TenantId = tenantId, EffectType = MarketplaceJobTypes.CommonLabel, IdempotencyKey = job.EffectIdempotencyKey, CreatedAt = timeProvider.GetUtcNow() };
            db.ExternalEffectRecords.Add(effect); await db.SaveChangesAsync(cancellationToken);
            TrackRequest();
            var create = await orders.CreateCommonLabelAsync(Context(tenantId, connectionId, correlationId, job.EffectIdempotencyKey), new(package.CargoTrackingNumber!, payload.BoxQuantity, payload.VolumetricHeight), cancellationToken);
            if (!create.IsSuccess)
            {
                TrackResultFailure(create.Error);
                if (IsAmbiguous(create.Error!)) { attempt.Status = "MANUAL_REVIEW"; attempt.ErrorCode = "EXTERNAL_EFFECT_AMBIGUOUS"; attempt.CompletedAt = timeProvider.GetUtcNow(); await db.SaveChangesAsync(cancellationToken); return JobExecutionResult.ManualReview("EXTERNAL_EFFECT_AMBIGUOUS", "Ortak etiket oluşturma çağrısının sonucu kesinleştirilemedi.", create.Error!.RemoteRequestId); }
                db.ExternalEffectRecords.Remove(effect); attempt.Status = "FAILED"; attempt.ErrorCode = create.Error!.Code; attempt.CompletedAt = timeProvider.GetUtcNow(); await db.SaveChangesAsync(cancellationToken); return JobExecutionResult.FromAdapterError(create.Error);
            }
            effect.CompletedAt = timeProvider.GetUtcNow(); attempt.Status = "POLLING";
            var next = payload with { Phase = "POLL" }; job.PayloadJson = JsonSerializer.Serialize(next); job.PayloadHash = Hash(job.PayloadJson); await db.SaveChangesAsync(cancellationToken);
            return JobExecutionResult.Retry("COMMON_LABEL_PENDING", "Trendyol ortak etiket hazırlanıyor.", TimeSpan.FromSeconds(10));
        }
        if (phase != "POLL") return JobExecutionResult.Blocked("COMMON_LABEL_PHASE_INVALID", "Ortak etiket işi bilinmeyen bir fazda.");
        TrackRequest();
        var documentResult = await orders.GetCommonLabelAsync(Context(tenantId, connectionId, correlationId, $"{job.EffectIdempotencyKey}:poll"), labelLookupKey, cancellationToken, requestedFormat);
        if (!documentResult.IsSuccess)
        {
            TrackResultFailure(documentResult.Error);
            var error = documentResult.Error!;
            var mapped = JobExecutionResult.FromAdapterError(error);
            if (mapped.Kind == JobCompletionKind.Retry || error.Class == AdapterErrorClass.NotFound) return JobExecutionResult.Retry("COMMON_LABEL_PENDING", $"{(hepsiburada ? "Hepsiburada" : "Trendyol")} ortak etiketi henüz hazır değil.", TimeSpan.FromSeconds(20), error.RemoteRequestId);
            attempt.Status = mapped.Kind == JobCompletionKind.ManualReview ? "MANUAL_REVIEW" : "FAILED"; attempt.ErrorCode = error.Code; attempt.CompletedAt = timeProvider.GetUtcNow(); await db.SaveChangesAsync(cancellationToken); return mapped;
        }
        var document = documentResult.Value!;
        if (!string.Equals(document.Format, requestedFormat, StringComparison.OrdinalIgnoreCase) || document.Content.Length == 0 || document.Content.LongLength > 5 * 1024 * 1024)
        {
            attempt.Status = "MANUAL_REVIEW"; attempt.ErrorCode = "COMMON_LABEL_CONTRACT_INVALID"; attempt.CompletedAt = timeProvider.GetUtcNow();
            await db.SaveChangesAsync(cancellationToken);
            return JobExecutionResult.ManualReview("COMMON_LABEL_CONTRACT_INVALID", "Ortak etiket biçimi istenen biçimle eşleşmeli ve içerik 5 MiB altında olmalıdır.");
        }
        var (extension, mimeType) = requestedFormat switch
        {
            "BASE64ZPL" => ("zpl.base64", "text/plain"),
            "PDF" => ("pdf", "application/pdf"),
            "PNG" => ("png", "image/png"),
            "JPG" => ("jpg", "image/jpeg"),
            _ => ("zpl", "application/zpl")
        };
        var checksum = Convert.ToHexString(SHA256.HashData(document.Content));
        var existing = await db.ShipmentDocuments.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.PackageId == package.Id && x.DocumentKind == "COMMON_LABEL" && x.Format == requestedFormat && x.Checksum == checksum, cancellationToken);
        if (existing is null)
        {
            var safeLabelName = new string(labelLookupKey.Where(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_').Take(100).ToArray());
            if (string.IsNullOrWhiteSpace(safeLabelName)) safeLabelName = "package-label";
            var assetId = Guid.CreateVersion7(); await using var stream = new MemoryStream(document.Content, writable: false); var stored = await files.SaveAsync(tenantId, $"{assetId:N}-common-label.{extension}", mimeType, stream, 5 * 1024 * 1024, cancellationToken);
            var asset = new FileAsset { Id = assetId, TenantId = tenantId, Classification = "SHIPMENT_LABEL", RelativePath = stored, OriginalNameSafe = $"{safeLabelName}.{extension}", MimeType = mimeType, SizeBytes = document.Content.LongLength, Sha256 = checksum, Status = "ACTIVE", CreatedAt = timeProvider.GetUtcNow() };
            db.FileAssets.Add(asset);
            var version = await db.ShipmentDocuments.Where(x => x.TenantId == tenantId && x.PackageId == package.Id && x.DocumentKind == "COMMON_LABEL").Select(x => (int?)x.DocumentVersion).MaxAsync(cancellationToken) ?? 0;
            existing = new ShipmentDocument { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = connectionId, PackageId = package.Id, FileAssetId = assetId, DocumentKind = "COMMON_LABEL", Format = requestedFormat, Source = hepsiburada ? "HEPSIBURADA" : "TRENDYOL", Checksum = checksum, DocumentVersion = version + 1, CreatedAt = timeProvider.GetUtcNow() };
            db.ShipmentDocuments.Add(existing);
        }
        attempt.DocumentId = existing.Id; attempt.Status = "SUCCEEDED"; attempt.ErrorCode = null; attempt.CompletedAt = timeProvider.GetUtcNow(); await db.SaveChangesAsync(cancellationToken); return JobExecutionResult.Success();
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static bool IsAmbiguous(AdapterError error) => error.Class is AdapterErrorClass.TransientNetwork or AdapterErrorClass.Remote5xx or AdapterErrorClass.ContractViolation or AdapterErrorClass.InternalBug;
    private static bool HasItems(string payloadJson) { try { using var doc = JsonDocument.Parse(payloadJson); var root = doc.RootElement; return root.ValueKind == JsonValueKind.Array ? root.GetArrayLength() > 0 : root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array && items.GetArrayLength() > 0; } catch (JsonException) { return false; } }
    private static string UpdatePayload(ProductUpdateJobPayload payload, string phase) => phase switch { "SUBMIT_UNAPPROVED" or "POLL_UNAPPROVED" => payload.UnapprovedPayloadJson, "SUBMIT_CONTENT" or "POLL_CONTENT" => payload.ApprovedContentPayloadJson, "SUBMIT_VARIANTS" or "POLL_VARIANTS" => payload.ApprovedVariantPayloadJson, "SUBMIT_DELIVERY" or "POLL_DELIVERY" => payload.ApprovedDeliveryPayloadJson, _ => "{}" };
    private static ProductUpdateJobPayload? AdvanceUpdate(ProductUpdateJobPayload payload, string phase)
    {
        var next = phase switch
        {
            "SUBMIT_UNAPPROVED" or "POLL_UNAPPROVED" => null,
            "SUBMIT_CONTENT" or "POLL_CONTENT" => "SUBMIT_VARIANTS",
            "SUBMIT_VARIANTS" or "POLL_VARIANTS" => "SUBMIT_DELIVERY",
            "SUBMIT_DELIVERY" or "POLL_DELIVERY" => null,
            _ => null
        };
        return next is null ? null : payload with { Phase = next, ExternalOperationId = null, SubmittedAt = null };
    }


    private async Task<bool> SyncReferences(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, CancellationToken cancellationToken)
    {
        var (resourceType, parentExternalId) = ReferenceResource(payloadJson);
        if (!await IsValidReferenceScope(tenantId, connectionId, resourceType, parentExternalId, cancellationToken)) return false;
        if (resourceType == "BRANDS") return await SyncBrandReferences(tenantId, connectionId, correlationId, cancellationToken);
        var items = new List<RemoteReferenceItem>();
        var visitedCursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        do
        {
            TrackRequest();
            var pageSize = resourceType == "BRANDS" ? 2000 : 1000;
            var result = await references.ReadAsync(Context(tenantId, connectionId, correlationId, $"reference-sync:{resourceType}:{parentExternalId}:{cursor}"), new(resourceType, parentExternalId), new(cursor, pageSize), cancellationToken);
            if (!result.IsSuccess) { TrackResultFailure(result.Error); throw JobProcessingException.FromAdapter(result.Error!); }
            foreach (var _ in result.Value!.Items) TrackReceived();
            items.AddRange(result.Value!.Items);
            if (items.Count > MaxReferenceItems) throw new JobProcessingException(JobExecutionResult.ManualReview("REFERENCE_RESULT_LIMIT_EXCEEDED", "Referans yanıtı güvenli işleme sınırını aştı."));
            cursor = result.Value.NextCursor;
            if (!result.Value.HasMore) break;
            if (string.IsNullOrWhiteSpace(cursor) || !visitedCursors.Add(cursor)) throw new JobProcessingException(JobExecutionResult.ManualReview("REFERENCE_CURSOR_INVALID", "Referans sayfalama imleci eksik veya yinelendi."));
        } while (!cancellationToken.IsCancellationRequested);

        cancellationToken.ThrowIfCancellationRequested();
        if (items.Count == 0 && resourceType is "CATEGORIES" or "BRANDS") throw new JobProcessingException(JobExecutionResult.Blocked("REFERENCE_EMPTY_RESPONSE", $"Marketplace {resourceType} salt-okunur çağrısı boş koleksiyon döndürdü; mevcut snapshot korunuyor."));
        if (items.Any(x => !string.Equals(x.ResourceType, resourceType, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(x.ExternalId) || string.IsNullOrWhiteSpace(x.Name))) throw new JobProcessingException(JobExecutionResult.ManualReview("REFERENCE_CONTRACT_INVALID", "Referans yanıtı zorunlu kimlik, ad veya kapsam sözleşmesini sağlamıyor."));
        var ordered = items.OrderBy(x => x.ExternalId, StringComparer.Ordinal).ToList();
        if (ordered.Select(x => x.ExternalId).Distinct(StringComparer.Ordinal).Count() != ordered.Count) throw new JobProcessingException(JobExecutionResult.ManualReview("REFERENCE_IDENTIFIERS_DUPLICATE", "Referans yanıtı yinelenen uzak kimlik içeriyor."));
        var identifiers = ordered.Select(x => x.ExternalId).ToHashSet(StringComparer.Ordinal);
        var scopeIsValid = resourceType == "CATEGORIES"
            ? ordered.All(x => x.ParentExternalId is null || (!string.Equals(x.ExternalId, x.ParentExternalId, StringComparison.Ordinal) && identifiers.Contains(x.ParentExternalId)))
            : ordered.All(x => string.Equals(x.ParentExternalId ?? "", parentExternalId ?? "", StringComparison.Ordinal));
        if (!scopeIsValid) throw new JobProcessingException(JobExecutionResult.ManualReview("REFERENCE_CONTRACT_INVALID", "Referans yanıtı geçerli kapsam veya kategori hiyerarşisi sağlamıyor."));
        var canonical = JsonSerializer.Serialize(ordered.Select(x => new { x.ExternalId, x.ParentExternalId, x.Name, x.Path, x.Depth, x.IsLeaf, x.IsActive, x.IsRequired, x.AllowsCustomValue, x.AllowsMultipleValues }));
        var contentHash = Hash(canonical);
        var now = timeProvider.GetUtcNow();
        var sourceVersion = await db.PlatformCapabilities.AsNoTracking().Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.Code == MarketplaceCapabilities.ReferenceRead).Select(x => x.SourceVersion).SingleOrDefaultAsync(cancellationToken)
            ?? await db.PlatformConnections.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == connectionId).Select(x => x.ApiVersion).SingleAsync(cancellationToken);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var scope = parentExternalId ?? "";
        var snapshots = await db.ReferenceSnapshots.Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == resourceType && x.ScopeExternalId == scope).ToListAsync(cancellationToken);
        var snapshot = snapshots.SingleOrDefault(x => x.ContentHash == contentHash);
        foreach (var current in snapshots.Where(x => x.IsCurrent && x.Id != snapshot?.Id)) current.IsCurrent = false;
        if (snapshot is null)
        {
            snapshot = new ReferenceSnapshot { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = connectionId, ResourceType = resourceType, ScopeExternalId = scope, SourceVersion = sourceVersion, ContentHash = contentHash, FetchedAt = now, IsCurrent = true, ItemCount = ordered.Count };
            db.ReferenceSnapshots.Add(snapshot);
            for (var index = 0; index < ordered.Count; index++)
            {
                var item = ordered[index];
                db.ReferenceItems.Add(new ReferenceItem { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = connectionId, SnapshotId = snapshot.Id, ResourceType = resourceType, ExternalId = item.ExternalId, ParentExternalId = item.ParentExternalId, Name = item.Name, NormalizedName = item.Name.Trim().ToUpperInvariant(), Path = item.Path, Depth = item.Depth, IsLeaf = item.IsLeaf, IsActive = item.IsActive, IsRequired = item.IsRequired, AllowsCustomValue = item.AllowsCustomValue, AllowsMultipleValues = item.AllowsMultipleValues, PayloadHash = Hash(item.RawJson), SortOrder = index });
            }
        }
        else
        {
            snapshot.IsCurrent = true;
            snapshot.FetchedAt = now;
            snapshot.SourceVersion = sourceVersion;
        }
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private async Task<bool> SyncBrandReferences(Guid tenantId, Guid connectionId, string correlationId, CancellationToken cancellationToken)
    {
        var sourceVersion = await db.PlatformCapabilities.AsNoTracking().Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.Code == MarketplaceCapabilities.ReferenceRead).Select(x => x.SourceVersion).SingleOrDefaultAsync(cancellationToken)
            ?? await db.PlatformConnections.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == connectionId).Select(x => x.ApiVersion).SingleAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var staging = new ReferenceSnapshot
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            ConnectionId = connectionId,
            ResourceType = "BRANDS",
            SourceVersion = sourceVersion,
            ContentHash = $"PENDING-{Guid.CreateVersion7():N}",
            FetchedAt = now,
            IsCurrent = false,
            ItemCount = 0
        };
        db.ReferenceSnapshots.Add(staging);
        await db.SaveChangesAsync(cancellationToken);
        db.ChangeTracker.Clear();

        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        var itemCount = 0;
        try
        {
            do
            {
                TrackRequest();
                const int pageSize = 2000;
                var result = await references.ReadAsync(Context(tenantId, connectionId, correlationId, $"reference-sync:BRANDS::{cursor}"), new("BRANDS", null), new(cursor, pageSize), cancellationToken);
                if (!result.IsSuccess) { TrackResultFailure(result.Error); throw JobProcessingException.FromAdapter(result.Error!); }
                var pageItems = result.Value!.Items;
                if (itemCount > MaxBrandReferenceItems - pageItems.Count) throw new JobProcessingException(JobExecutionResult.ManualReview("REFERENCE_RESULT_LIMIT_EXCEEDED", "Trendyol marka referansı güvenli işleme sınırını aştı."));
                var uniquePageItems = new List<RemoteReferenceItem>(pageItems.Count);
                foreach (var item in pageItems)
                {
                    TrackReceived();
                    if (!string.Equals(item.ResourceType, "BRANDS", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(item.ExternalId) || string.IsNullOrWhiteSpace(item.Name) || item.ParentExternalId is not null)
                        throw new JobProcessingException(JobExecutionResult.ManualReview("REFERENCE_CONTRACT_INVALID", "Marka referans yanıtı zorunlu kimlik, ad veya kapsam sözleşmesini sağlamıyor."));
                    if (seen.Add(item.ExternalId)) uniquePageItems.Add(item);
                }
                for (var index = 0; index < uniquePageItems.Count; index++)
                {
                    var item = uniquePageItems[index];
                    db.ReferenceItems.Add(new ReferenceItem
                    {
                        Id = Guid.CreateVersion7(),
                        TenantId = tenantId,
                        ConnectionId = connectionId,
                        SnapshotId = staging.Id,
                        ResourceType = "BRANDS",
                        ExternalId = item.ExternalId,
                        ParentExternalId = null,
                        Name = item.Name,
                        NormalizedName = item.Name.Trim().ToUpperInvariant(),
                        Path = item.Path,
                        Depth = item.Depth,
                        IsLeaf = item.IsLeaf,
                        IsActive = item.IsActive,
                        IsRequired = item.IsRequired,
                        AllowsCustomValue = item.AllowsCustomValue,
                        AllowsMultipleValues = item.AllowsMultipleValues,
                        PayloadHash = Hash(item.RawJson),
                        SortOrder = itemCount + index
                    });
                }
                itemCount += uniquePageItems.Count;
                await db.SaveChangesAsync(cancellationToken);
                db.ChangeTracker.Clear();
                cursor = result.Value.NextCursor;
                if (!result.Value.HasMore) break;
                if (string.IsNullOrWhiteSpace(cursor)) throw new JobProcessingException(JobExecutionResult.ManualReview("REFERENCE_CURSOR_INVALID", "Marka referans sayfalama imleci eksik döndü."));
            } while (!cancellationToken.IsCancellationRequested);

            cancellationToken.ThrowIfCancellationRequested();
            if (itemCount == 0) throw new JobProcessingException(JobExecutionResult.Blocked("REFERENCE_EMPTY_RESPONSE", "Trendyol BRANDS salt-okunur çağrısı boş koleksiyon döndürdü; mevcut snapshot korunuyor."));
            var contentHash = await HashReferenceItemsAsync(tenantId, staging.Id, cancellationToken);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var snapshots = await db.ReferenceSnapshots.Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == "BRANDS" && x.ScopeExternalId == "").ToListAsync(cancellationToken);
            var existing = snapshots.SingleOrDefault(x => x.Id != staging.Id && x.ContentHash == contentHash);
            var current = existing ?? snapshots.Single(x => x.Id == staging.Id);
            foreach (var snapshot in snapshots.Where(x => x.IsCurrent && x.Id != current.Id)) snapshot.IsCurrent = false;
            current.ContentHash = contentHash;
            current.SourceVersion = sourceVersion;
            current.FetchedAt = now;
            current.ItemCount = itemCount;
            current.IsCurrent = true;
            if (existing is not null) db.ReferenceSnapshots.Remove(snapshots.Single(x => x.Id == staging.Id));
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        catch
        {
            db.ChangeTracker.Clear();
            try
            {
                var orphan = await db.ReferenceSnapshots.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == staging.Id && !x.IsCurrent, CancellationToken.None);
                if (orphan is not null)
                {
                    db.ReferenceSnapshots.Remove(orphan);
                    await db.SaveChangesAsync(CancellationToken.None);
                }
            }
            catch { db.ChangeTracker.Clear(); }
            throw;
        }
    }

    private async Task<string> HashReferenceItemsAsync(Guid tenantId, Guid snapshotId, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes("["));
        var first = true;
        await foreach (var item in db.ReferenceItems.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.SnapshotId == snapshotId && x.ResourceType == "BRANDS")
            .OrderBy(x => x.ExternalId)
            .Select(x => new { x.ExternalId, x.ParentExternalId, x.Name, x.Path, x.Depth, x.IsLeaf, x.IsActive, x.IsRequired, x.AllowsCustomValue, x.AllowsMultipleValues })
            .AsAsyncEnumerable()
            .WithCancellation(cancellationToken))
        {
            if (!first) hash.AppendData(Encoding.UTF8.GetBytes(","));
            hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(item));
            first = false;
        }
        hash.AppendData(Encoding.UTF8.GetBytes("]"));
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static (string ResourceType, string? ParentExternalId) ReferenceResource(string payloadJson)
    {
        try
        {
            using var payload = JsonDocument.Parse(payloadJson);
            var resourceType = payload.RootElement.TryGetProperty("resourceType", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()!.Trim().ToUpperInvariant() : "CATEGORIES";
            var parent = payload.RootElement.TryGetProperty("parentExternalId", out var parentValue) && parentValue.ValueKind == JsonValueKind.String ? parentValue.GetString()?.Trim() : null;
            return (resourceType, string.IsNullOrWhiteSpace(parent) ? null : parent);
        }
        catch (JsonException) { return ("", null); }
    }

    private async Task<bool> IsValidReferenceScope(Guid tenantId, Guid connectionId, string resourceType, string? parentExternalId, CancellationToken cancellationToken)
    {
        if (resourceType is "CATEGORIES" or "BRANDS") return parentExternalId is null;
        if (resourceType == "CATEGORY_ATTRIBUTES" && parentExternalId is not null)
            return await db.ReferenceItems.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == "CATEGORIES" && x.ExternalId == parentExternalId && x.IsLeaf && x.IsActive
                && db.ReferenceSnapshots.Any(snapshot => snapshot.TenantId == tenantId && snapshot.Id == x.SnapshotId && snapshot.IsCurrent && snapshot.ScopeExternalId == ""), cancellationToken);
        if (resourceType != "ATTRIBUTE_VALUES" || parentExternalId is null) return false;
        var parts = parentExternalId.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2) return false;
        return await db.ReferenceItems.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == "CATEGORY_ATTRIBUTES" && x.ExternalId == parts[1] && x.ParentExternalId == parts[0]
            && db.ReferenceSnapshots.Any(snapshot => snapshot.TenantId == tenantId && snapshot.Id == x.SnapshotId && snapshot.IsCurrent && snapshot.ScopeExternalId == parts[0]), cancellationToken);
    }

    private async Task<bool> TestConnection(Guid tenantId, Guid connectionId, string correlationId, CancellationToken cancellationToken)
    {
        var connection = await db.PlatformConnections.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == connectionId && (x.PlatformCode == "TRENDYOL" || x.PlatformCode == "SHOPIFY" || x.PlatformCode == "HEPSIBURADA"), cancellationToken); if (connection is null) return false; var now = timeProvider.GetUtcNow(); connection.LastTestedAt = now;
        IConnectionPort port = connections;
        var context = Context(tenantId, connectionId, correlationId, "connection-test"); TrackRequest(); var result = await port.TestAsync(context, cancellationToken); if (!result.IsSuccess) { TrackResultFailure(result.Error); connection.LastErrorCode = result.Error!.Code; connection.Version++; await db.SaveChangesAsync(cancellationToken); throw JobProcessingException.FromAdapter(result.Error!); }
        TrackRequest(); var discovery = await port.DiscoverCapabilitiesAsync(context, cancellationToken); if (!discovery.IsSuccess) { TrackResultFailure(discovery.Error); connection.LastErrorCode = discovery.Error!.Code; connection.Version++; await db.SaveChangesAsync(cancellationToken); throw JobProcessingException.FromAdapter(discovery.Error!); }
        foreach (var _ in discovery.Value!) TrackReceived();
        foreach (var evidence in discovery.Value!)
        {
            var capability = await db.PlatformCapabilities.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.Code == evidence.Code, cancellationToken); if (capability is null) continue;
            capability.SupportLevel = string.Equals(evidence.SupportLevel, "SUPPORTED", StringComparison.Ordinal) ? CapabilitySupportLevel.Supported : CapabilitySupportLevel.Unknown; capability.SourceUrl = evidence.SourceUrl; capability.SourceVersion = evidence.SourceVersion; capability.RequiredScope = evidence.RequiredScope; capability.ConstraintsJson = evidence.ConstraintsJson; capability.EvidenceNote = evidence.EvidenceNote; capability.FixtureChecksum = evidence.FixtureChecksum; capability.VerifiedAt = evidence.VerifiedAt; capability.Version++;
        }
        if (connection.PlatformCode == "HEPSIBURADA" && discovery.Value is not null
            && !new[] { MarketplaceCapabilities.ProductRead, MarketplaceCapabilities.OrderRead }.All(code => discovery.Value.Any(item => item.Code == code && item.SupportLevel == "SUPPORTED")))
        {
            connection.LastErrorCode = "HEPSIBURADA_REQUIRED_READ_CAPABILITY";
            connection.Version++;
            await db.SaveChangesAsync(cancellationToken);
            throw JobProcessingException.FromAdapter(new AdapterError(AdapterErrorClass.NotSupported, connection.LastErrorCode,
                "Hepsiburada bağlantısı etkinleştirilemez: ürün ve sipariş okuma yeteneklerinin ikisi de doğrulanmalıdır.", null, null, null));
        }
        if (connection.PlatformCode == "SHOPIFY" && discovery.Value is not null
            && !new[] { MarketplaceCapabilities.ProductRead, MarketplaceCapabilities.OrderRead }.All(code => discovery.Value.Any(item => item.Code == code && item.SupportLevel == "SUPPORTED")))
        {
            connection.LastErrorCode = "SHOPIFY_REQUIRED_READ_SCOPE";
            connection.Version++;
            await db.SaveChangesAsync(cancellationToken);
            var failedCapabilities = discovery.Value
                .Where(item => item.Code is MarketplaceCapabilities.ProductRead or MarketplaceCapabilities.OrderRead)
                .Where(item => !string.Equals(item.SupportLevel, "SUPPORTED", StringComparison.Ordinal))
                .Select(item => item.EvidenceNote)
                .Where(note => !string.IsNullOrWhiteSpace(note))
                .ToArray();
            var detail = failedCapabilities.Length == 0
                ? "Ürün ve sipariş okuma izinleri doğrulanamadı."
                : string.Join(" ", failedCapabilities);
            throw JobProcessingException.FromAdapter(new AdapterError(AdapterErrorClass.Authentication, "SHOPIFY_REQUIRED_READ_SCOPE", $"{detail} Gerekli izinler: read_products, read_inventory, read_orders ve read_locations.", 403, null, null));
        }
        connection.LastSuccessAt = now; connection.LastErrorCode = null; if (connection.Status == "DRAFT") connection.Status = "VERIFIED"; connection.Version++; await db.SaveChangesAsync(cancellationToken); return true;
    }


    private async Task<bool> SyncHepsiburadaOrders(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, string cursorResourceType, bool allowBaseline, CancellationToken cancellationToken)
    {
        string? externalOrderId = null;
        string? packageNumber = null;
        var full = false;
        try
        {
            using var payload = JsonDocument.Parse(payloadJson);
            if (payload.RootElement.TryGetProperty("externalOrderId", out var id) && id.ValueKind == JsonValueKind.String) externalOrderId = id.GetString();
            if (payload.RootElement.TryGetProperty("packageNumber", out var package) && package.ValueKind == JsonValueKind.String) packageNumber = package.GetString()?.Trim();
            if (payload.RootElement.TryGetProperty("full", out var fullValue) && fullValue.ValueKind is JsonValueKind.True or JsonValueKind.False) full = fullValue.GetBoolean();
        }
        catch (JsonException) { return false; }

        if (!string.IsNullOrWhiteSpace(externalOrderId))
        {
            TrackRequest();
            var single = await orders.GetAsync(Context(tenantId, connectionId, correlationId, $"hepsiburada-order-get:{externalOrderId}"), externalOrderId.Trim(), cancellationToken);
            if (!single.IsSuccess) { TrackResultFailure(single.Error); throw JobProcessingException.FromAdapter(single.Error!); }
            TrackReceived();
            var remoteOrder = single.Value!;
            if (!string.IsNullOrWhiteSpace(packageNumber))
            {
                TrackRequest();
                var tracking = await orderPackages.GetPackageTrackingInfoAsync(
                    Context(tenantId, connectionId, correlationId, $"hepsiburada-package-status:{packageNumber}"), packageNumber, cancellationToken);
                if (!tracking.IsSuccess) { TrackResultFailure(tracking.Error); throw JobProcessingException.FromAdapter(tracking.Error!); }
                TrackReceived();
                if (!string.IsNullOrWhiteSpace(tracking.Value!.OrderNumber)
                    && !string.Equals(tracking.Value.OrderNumber.Trim(), remoteOrder.OrderNumber, StringComparison.OrdinalIgnoreCase))
                    throw JobProcessingException.FromAdapter(new AdapterError(AdapterErrorClass.Validation, "HEPSIBURADA_PACKAGE_ORDER_MISMATCH", "Paket numarası girilen Hepsiburada siparişine ait değil; durum güncellenmedi.", 422, null, null));
                if (string.IsNullOrWhiteSpace(tracking.Value.Status)
                    || HepsiburadaOrderLifecycleStatusPolicy.FromRemote(tracking.Value.Status) is null)
                    throw JobProcessingException.FromAdapter(new AdapterError(AdapterErrorClass.ContractViolation, "HEPSIBURADA_PACKAGE_STATUS_UNSUPPORTED", "Hepsiburada paket durum yanıtı tanınmadı; sipariş durumu değiştirilmedi.", 502, null, null));

                // The direct package endpoint is a point-in-time GET and does
                // not return the original delivery timestamp. Record the
                // observation time while keeping the remote lifecycle status
                // and package identity authoritative.
                var observation = new RemotePackage(
                    packageNumber,
                    null,
                    tracking.Value.Status,
                    timeProvider.GetUtcNow(),
                    tracking.Value.CargoCompany,
                    tracking.Value.TrackingInfoCode,
                    [],
                    IsStatusObservation: true);
                remoteOrder = remoteOrder with
                {
                    Packages = [observation]
                };
            }
            if (!await UpsertOrder(tenantId, connectionId, remoteOrder, cancellationToken))
                throw JobProcessingException.FromAdapter(new AdapterError(
                    AdapterErrorClass.ContractViolation,
                    "HEPSIBURADA_ORDER_SYNC_NOT_APPLIED",
                    "Hepsiburada sipariş verisi alındı ancak panel bütünlük denetimi güncellemeyi uygulamadı; işlem hata olarak kaydedildi.",
                    502,
                    null,
                    null));
            return true;
        }

        var cursor = await Cursor(tenantId, connectionId, cursorResourceType, cancellationToken);
        var now = timeProvider.GetUtcNow();
        var overlapSeconds = await db.ConnectionSyncPolicies.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == "ORDERS")
            .Select(x => (int?)x.OverlapSeconds)
            .SingleOrDefaultAsync(cancellationToken) ?? DefaultOrderSyncOverlapSeconds;
        var overlap = TimeSpan.FromSeconds(Math.Clamp(overlapSeconds, 0, 86_399));
        var hasSnapshots = await db.Orders.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId, cancellationToken);
        var retryAfterFailure = cursor.ConsecutiveFailureCount > 0 || !string.IsNullOrWhiteSpace(cursor.LastError);
        var forceBaseline = allowBaseline && (full || !hasSnapshots || retryAfterFailure);
        HepsiburadaOrderSyncState? state = null;
        if (!forceBaseline && !string.IsNullOrWhiteSpace(cursor.OpaqueCursor))
        {
            try
            {
                var saved = JsonSerializer.Deserialize<HepsiburadaOrderSyncState>(cursor.OpaqueCursor);
                if (saved is { Version: HepsiburadaOrderSyncStateVersion, WindowIndex: >= 0, Offset: >= 0 }) state = saved;
            }
            catch (JsonException) { }
        }
        if (state is null)
        {
            var anchor = now;
            var oldestAvailable = anchor.AddMonths(-3);
            var watermark = cursor.LastModifiedWatermark ?? cursor.LastSuccessAt ?? anchor.AddHours(-24);
            if (watermark > anchor) watermark = anchor;
            var start = forceBaseline ? oldestAvailable : watermark.Subtract(overlap);
            if (start < oldestAvailable) start = oldestAvailable;
            state = new(HepsiburadaOrderSyncStateVersion, anchor, start, 0, 0);
        }

        while (true)
        {
            var windowEnd = state.AnchorEnd.AddDays(-state.WindowIndex);
            if (windowEnd > state.AnchorEnd) windowEnd = state.AnchorEnd;
            if (windowEnd <= state.StartAt)
            {
                cursor.OpaqueCursor = null;
                cursor.LastModifiedWatermark = state.AnchorEnd;
                cursor.LastSuccessAt = timeProvider.GetUtcNow();
                cursor.LastError = null;
                cursor.LastErrorAt = null;
                cursor.ConsecutiveFailureCount = 0;
                cursor.Version++;
                await db.SaveChangesAsync(cancellationToken);
                return true;
            }

            var windowStart = windowEnd.Subtract(TimeSpan.FromHours(24));
            if (windowStart < state.StartAt) windowStart = state.StartAt;
            TrackRequest();
            var page = await orders.PollAsync(
                Context(tenantId, connectionId, correlationId, $"hepsiburada-orders:{state.WindowIndex}:{state.Offset}"),
                new OrderPollWindow(windowStart, windowEnd),
                new(state.Offset.ToString(System.Globalization.CultureInfo.InvariantCulture), 10),
                cancellationToken);
            if (!page.IsSuccess) { TrackResultFailure(page.Error); throw JobProcessingException.FromAdapter(page.Error!); }
            foreach (var _ in page.Value!.Items) TrackReceived();
            await UpsertOrders(tenantId, connectionId, page.Value.Items, cancellationToken);

            if (page.Value.HasMore)
            {
                if (!int.TryParse(page.Value.NextCursor, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var nextOffset)
                    || nextOffset <= state.Offset)
                    throw new InvalidOperationException("Hepsiburada sayfalaması hasMore=true döndürdü ancak ileri offset üretmedi.");
                state = state with { Offset = nextOffset };
            }
            else
            {
                state = state with { WindowIndex = state.WindowIndex + 1, Offset = 0 };
            }
            cursor.OpaqueCursor = JsonSerializer.Serialize(state);
            cursor.Version++;
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private const string HepsiburadaOrderSyncStateVersion = "hepsiburada-orders-v1";
    private sealed record HepsiburadaOrderSyncState(string Version, DateTimeOffset AnchorEnd, DateTimeOffset StartAt, int WindowIndex, int Offset);

    private async Task<bool> SyncHepsiburadaPackages(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, CancellationToken cancellationToken)
    {
        if (!configuration.GetValue("Marketplace:PersistOrderSnapshots", true)) return true;

        var full = false;
        try
        {
            using var payload = JsonDocument.Parse(payloadJson);
            if (payload.RootElement.TryGetProperty("full", out var fullValue) && fullValue.ValueKind is JsonValueKind.True or JsonValueKind.False)
                full = fullValue.GetBoolean();
        }
        catch (JsonException) { return false; }

        var cursor = await Cursor(tenantId, connectionId, "PACKAGES", cancellationToken);
        var now = timeProvider.GetUtcNow();
        var overlapSeconds = await db.ConnectionSyncPolicies.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == "PACKAGES")
            .Select(x => (int?)x.OverlapSeconds)
            .SingleOrDefaultAsync(cancellationToken) ?? 600;
        var overlap = TimeSpan.FromSeconds(Math.Clamp(overlapSeconds, 0, 86_399));
        var forceBaseline = full || cursor.LastSuccessAt is null || cursor.ConsecutiveFailureCount > 0 || !string.IsNullOrWhiteSpace(cursor.LastError);
        HepsiburadaPackageSyncState? state = null;
        if (!full && cursor.ConsecutiveFailureCount == 0 && string.IsNullOrWhiteSpace(cursor.LastError) && !string.IsNullOrWhiteSpace(cursor.OpaqueCursor))
        {
            try
            {
                var saved = JsonSerializer.Deserialize<HepsiburadaPackageSyncState>(cursor.OpaqueCursor);
                if (saved is { Version: HepsiburadaPackageSyncStateVersion, WindowIndex: >= 0, Offset: >= 0 }) state = saved;
            }
            catch (JsonException) { }
        }
        if (state is null)
        {
            var anchor = now;
            var oldestAvailable = anchor.AddDays(-30);
            var watermark = cursor.LastModifiedWatermark ?? cursor.LastSuccessAt ?? anchor.AddHours(-24);
            if (watermark > anchor) watermark = anchor;
            var start = forceBaseline ? oldestAvailable : watermark.Subtract(overlap);
            if (start < oldestAvailable) start = oldestAvailable;
            state = new(HepsiburadaPackageSyncStateVersion, anchor, start, 0, 0);
        }

        while (true)
        {
            var windowEnd = state.AnchorEnd.AddDays(-state.WindowIndex);
            if (windowEnd > state.AnchorEnd) windowEnd = state.AnchorEnd;
            if (windowEnd <= state.StartAt)
            {
                cursor.OpaqueCursor = null;
                cursor.LastModifiedWatermark = state.AnchorEnd;
                cursor.LastSuccessAt = timeProvider.GetUtcNow();
                cursor.LastError = null;
                cursor.LastErrorAt = null;
                cursor.ConsecutiveFailureCount = 0;
                cursor.Version++;
                await db.SaveChangesAsync(cancellationToken);
                return true;
            }

            var windowStart = windowEnd.Subtract(TimeSpan.FromHours(24));
            if (windowStart < state.StartAt) windowStart = state.StartAt;
            TrackRequest();
            var page = await orderPackages.PollPackagesAsync(
                Context(tenantId, connectionId, correlationId, $"hepsiburada-packages:{state.WindowIndex}:{state.Offset}"),
                new PackagePollWindow(windowStart, windowEnd),
                new(state.Offset.ToString(System.Globalization.CultureInfo.InvariantCulture), 10),
                cancellationToken);
            if (!page.IsSuccess) { TrackResultFailure(page.Error); throw JobProcessingException.FromAdapter(page.Error!); }
            foreach (var item in page.Value!.Items) TrackReceived();
            foreach (var issue in page.Value.Issues ?? [])
                await RecordIssue(tenantId, $"package-contract:{connectionId}:{issue.Identity}:{issue.Code}", issue.Code, issue.Message, cancellationToken);

            foreach (var orderPackagesForOrder in page.Value.Items
                         .GroupBy(x => x.ExternalOrderId, StringComparer.Ordinal)
                         .OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                var snapshots = orderPackagesForOrder.Select(x => x.OrderSnapshot).Where(x => x is not null).Select(x => x!).ToArray();
                var mergedOrder = TrendyolJsonMapper.MergeOrderPackages(snapshots, orderPackagesForOrder.Key);
                if (mergedOrder is null && orderPackagesForOrder.Any(item => item.Package.IsStatusObservation))
                {
                    TrackRequest();
                    var details = await orders.GetAsync(Context(tenantId, connectionId, correlationId, $"hepsiburada-status-order:{orderPackagesForOrder.Key}"), orderPackagesForOrder.Key, cancellationToken);
                    if (!details.IsSuccess)
                    {
                        if (details.Error?.HttpStatus == 404)
                        {
                            await RecordIssue(tenantId, $"package-status-order:{connectionId}:{orderPackagesForOrder.Key}", "HEPSIBURADA_STATUS_ORDER_DETAIL_NOT_FOUND", "Hepsiburada paket durumunda sipariş var ancak detay servisi siparişi bulamadı; paket durumu bu turda uygulanmadı.", cancellationToken);
                            continue;
                        }
                        TrackResultFailure(details.Error);
                        throw JobProcessingException.FromAdapter(details.Error!);
                    }
                    TrackReceived();
                    mergedOrder = details.Value;
                }
                if (mergedOrder is null)
                {
                    await RecordIssue(tenantId, $"package-order-snapshot:{connectionId}:{orderPackagesForOrder.Key}:{state.WindowIndex}:{state.Offset}", "HEPSIBURADA_PACKAGE_ORDER_SNAPSHOT_MISSING", "Hepsiburada paket yanıtından bağlı sipariş satırları oluşturulamadı; paket yerel siparişe uygulanmadı.", cancellationToken);
                    continue;
                }

                var packages = mergedOrder.Packages.ToList();
                foreach (var observation in orderPackagesForOrder.Where(item => item.Package.IsStatusObservation))
                {
                    var packageIndex = packages.FindIndex(package => package.ExternalPackageId == observation.Package.ExternalPackageId);
                    if (packageIndex < 0)
                    {
                        packages.Add(observation.Package);
                        continue;
                    }

                    var existingPackage = packages[packageIndex];
                    packages[packageIndex] = existingPackage with
                    {
                        RawStatus = observation.Package.OccurredAt >= existingPackage.OccurredAt ? observation.Package.RawStatus : existingPackage.RawStatus,
                        OccurredAt = observation.Package.OccurredAt > existingPackage.OccurredAt ? observation.Package.OccurredAt : existingPackage.OccurredAt,
                        CargoProviderExternalId = observation.Package.CargoProviderExternalId ?? existingPackage.CargoProviderExternalId,
                        CargoTrackingNumber = observation.Package.CargoTrackingNumber ?? existingPackage.CargoTrackingNumber,
                        Invoice = observation.Package.Invoice ?? existingPackage.Invoice,
                        IsStatusObservation = false
                    };
                }
                mergedOrder = mergedOrder with
                {
                    LastModifiedAt = packages.Select(package => package.OccurredAt).Append(mergedOrder.LastModifiedAt).Max(),
                    Packages = packages
                };
                TrackReceived();
                await UpsertOrder(tenantId, connectionId, mergedOrder, cancellationToken);
            }

            if (page.Value.HasMore)
            {
                if (!int.TryParse(page.Value.NextCursor, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var nextOffset)
                    || nextOffset <= state.Offset)
                    throw new InvalidOperationException("Hepsiburada paket sayfalaması hasMore=true döndürdü ancak ileri offset üretmedi.");
                state = state with { Offset = nextOffset };
            }
            else state = state with { WindowIndex = state.WindowIndex + 1, Offset = 0 };
            cursor.OpaqueCursor = JsonSerializer.Serialize(state);
            cursor.Version++;
            await db.SaveChangesAsync(cancellationToken);
            // Persist every offset/window so an interrupted full scan resumes
            // without repeating the completed pages.
        }
    }

    private const string HepsiburadaPackageSyncStateVersion = "hepsiburada-packages-v2";
    private sealed record HepsiburadaPackageSyncState(string Version, DateTimeOffset AnchorEnd, DateTimeOffset StartAt, int WindowIndex, int Offset);

    private async Task<bool> SyncOrders(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, string cursorResourceType, bool allowBaseline, CancellationToken cancellationToken)
    {
        if (!configuration.GetValue("Marketplace:PersistOrderSnapshots", true))
            return true;

        var platform = await db.PlatformConnections.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.Id == connectionId)
            .Select(x => x.PlatformCode)
            .SingleOrDefaultAsync(cancellationToken);
        if (platform == "SHOPIFY")
            return await SyncShopifyOrders(tenantId, connectionId, payloadJson, correlationId, cursorResourceType, allowBaseline, cancellationToken);
        if (platform == "HEPSIBURADA")
        {
            var ordersSynced = await SyncHepsiburadaOrders(tenantId, connectionId, payloadJson, correlationId, cursorResourceType, allowBaseline, cancellationToken);
            if (!ordersSynced || cursorResourceType is not ("ORDERS_HOT" or "ORDERS_RECOVERY")) return ordersSynced;
            try
            {
                using var payload = JsonDocument.Parse(payloadJson);
                if (payload.RootElement.TryGetProperty("externalOrderId", out var requestedOrderId)
                    && requestedOrderId.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(requestedOrderId.GetString())) return true;
            }
            catch (JsonException) { return false; }
            return await SyncHepsiburadaPackages(tenantId, connectionId, payloadJson, correlationId, cancellationToken);
        }

        string? externalOrderId = null;
        var full = false;
        try
        {
            using var payload = JsonDocument.Parse(payloadJson);
            if (payload.RootElement.TryGetProperty("externalOrderId", out var value) && value.ValueKind == JsonValueKind.String) externalOrderId = value.GetString();
            if (payload.RootElement.TryGetProperty("full", out var fullValue) && fullValue.ValueKind is JsonValueKind.True or JsonValueKind.False) full = fullValue.GetBoolean();
        }
        catch (JsonException) { return false; }
        if (!string.IsNullOrWhiteSpace(externalOrderId))
        {
            TrackRequest();
            var single = await orders.GetAsync(Context(tenantId, connectionId, correlationId, $"order-get:{externalOrderId}"), externalOrderId.Trim(), cancellationToken);
            if (!single.IsSuccess) { TrackResultFailure(single.Error); throw JobProcessingException.FromAdapter(single.Error!); }
            TrackReceived();
            await UpsertOrder(tenantId, connectionId, single.Value!, cancellationToken);
            return true;
        }

        var cursor = await Cursor(tenantId, connectionId, cursorResourceType, cancellationToken);
        var now = timeProvider.GetUtcNow();
        var configuredOverlapSeconds = await db.ConnectionSyncPolicies.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == "ORDERS")
            .Select(x => (int?)x.OverlapSeconds)
            .SingleOrDefaultAsync(cancellationToken) ?? DefaultOrderSyncOverlapSeconds;
        var overlap = TimeSpan.FromSeconds(Math.Clamp(configuredOverlapSeconds, 0, (int)OrderStreamWindowSpan.TotalSeconds - 1));
        // A reset/empty snapshot store must always get a complete baseline, even if
        // an older cursor survived the reset. This keeps the first import idempotent
        // without requiring any direct database mutation.
        var hasSnapshots = await db.Orders.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId, cancellationToken);
        // Recovery is the safety net after an interrupted/auth-failed sync.
        // Do not let a stale successful watermark turn the first healthy run
        // into a narrow incremental read that permanently misses the gap.
        var retryAfterFailure = cursor.ConsecutiveFailureCount > 0 || !string.IsNullOrWhiteSpace(cursor.LastError);
        var state = ReadOrderSyncState(cursor, now, overlap, allowBaseline, forceBaseline: allowBaseline && (full || !hasSnapshots || retryAfterFailure));
        var resetExpiredCursor = false;
        do
        {
            var (modifiedAfter, modifiedBefore) = OrderWindow(state);
            var storefront = TrendyolReadStorefronts.Codes[state.StoreFrontIndex];
            TrackRequest();
            var result = await orders.PollAsync(Context(tenantId, connectionId, correlationId, $"order-sync:{storefront}:{state.NextCursor ?? state.WindowIndex.ToString()}"), new(modifiedAfter, modifiedBefore, null, storefront), new(state.NextCursor, 200), cancellationToken);
            if (!result.IsSuccess && !resetExpiredCursor && !string.IsNullOrWhiteSpace(state.NextCursor) && result.Error is { Class: AdapterErrorClass.Validation, HttpStatus: 400 })
            {
                // Stream cursors can expire. Restart once from the durable watermark; preserve all other
                // validation failures for audit/retry.
                state = state with { NextCursor = null };
                cursor.OpaqueCursor = null;
                resetExpiredCursor = true;
                await Task.Delay(OrderStreamRequestInterval, cancellationToken);
                continue;
            }
            if (!result.IsSuccess && state.StoreFrontIndex > 0 && state.NextCursor is null && result.Error?.HttpStatus is 400 or 404)
            {
                // Some seller accounts do not expose every international storefront. A
                // rejected optional storefront must not prevent TR or another storefront
                // from being imported and persisted.
                if (state.StoreFrontIndex + 1 < TrendyolReadStorefronts.Codes.Length)
                {
                    state = state with { StoreFrontIndex = state.StoreFrontIndex + 1, NextCursor = null };
                    cursor.OpaqueCursor = SerializeOrderSyncState(state);
                    cursor.Version++;
                    await db.SaveChangesAsync(cancellationToken);
                    await Task.Delay(OrderStreamRequestInterval, cancellationToken);
                    continue;
                }
                if (state.Mode == OrderSyncMode.Baseline && HasEarlierOrderWindow(state))
                {
                    state = state with { WindowIndex = state.WindowIndex + 1, StoreFrontIndex = 0, NextCursor = null };
                    cursor.OpaqueCursor = SerializeOrderSyncState(state);
                    cursor.Version++;
                    await db.SaveChangesAsync(cancellationToken);
                    // Recovery is a low-priority backfill. Persist the next
                    // window and release the orders lane between windows so
                    // lifecycle/reconciliation jobs can repair current orders.
                    if (cursorResourceType == "ORDERS_RECOVERY") return true;
                    await Task.Delay(OrderStreamRequestInterval, cancellationToken);
                    continue;
                }
                cursor.OpaqueCursor = null;
                cursor.LastModifiedWatermark = state.AnchorEnd;
                cursor.Version++;
                await db.SaveChangesAsync(cancellationToken);
                break;
            }
            if (!result.IsSuccess) { TrackResultFailure(result.Error); throw JobProcessingException.FromAdapter(result.Error!); }
            foreach (var _ in result.Value!.Items) TrackReceived();
            foreach (var issue in result.Value.Issues ?? [])
                await RecordIssue(tenantId, $"order-contract:{connectionId}:{issue.Identity}:{issue.Code}", issue.Code, issue.Message, cancellationToken);
            await UpsertOrders(tenantId, connectionId, result.Value!.Items, cancellationToken);
            if (result.Value.HasMore)
            {
                if (string.IsNullOrWhiteSpace(result.Value.NextCursor)) throw new InvalidOperationException("Trendyol stream hasMore=true ancak nextCursor boş döndü.");
                state = state with { NextCursor = result.Value.NextCursor };
            }
            else if (state.StoreFrontIndex + 1 < TrendyolReadStorefronts.Codes.Length)
            {
                state = state with { StoreFrontIndex = state.StoreFrontIndex + 1, NextCursor = null };
            }
            else if (state.Mode == OrderSyncMode.Baseline && HasEarlierOrderWindow(state))
            {
                state = state with { WindowIndex = state.WindowIndex + 1, StoreFrontIndex = 0, NextCursor = null };
                if (cursorResourceType == "ORDERS_RECOVERY")
                {
                    cursor.OpaqueCursor = SerializeOrderSyncState(state);
                    cursor.Version++;
                    await db.SaveChangesAsync(cancellationToken);
                    return true;
                }
            }
            else
            {
                cursor.OpaqueCursor = null;
                cursor.LastModifiedWatermark = state.AnchorEnd;
                cursor.Version++;
                await db.SaveChangesAsync(cancellationToken);
                break;
            }
            cursor.OpaqueCursor = SerializeOrderSyncState(state);
            cursor.Version++;
            await db.SaveChangesAsync(cancellationToken);
            await Task.Delay(OrderStreamRequestInterval, cancellationToken);
        } while (!cancellationToken.IsCancellationRequested);
        return true;
    }

    private async Task<bool> SyncShopifyOrders(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, string cursorResourceType, bool allowBaseline, CancellationToken cancellationToken)
    {
        string? externalOrderId = null;
        var full = false;
        try
        {
            using var payload = JsonDocument.Parse(payloadJson);
            if (payload.RootElement.TryGetProperty("externalOrderId", out var value) && value.ValueKind == JsonValueKind.String) externalOrderId = value.GetString();
            if (payload.RootElement.TryGetProperty("full", out var fullValue) && fullValue.ValueKind is JsonValueKind.True or JsonValueKind.False) full = fullValue.GetBoolean();
        }
        catch (JsonException) { return false; }

        if (!string.IsNullOrWhiteSpace(externalOrderId))
        {
            TrackRequest();
            var single = await orders.GetAsync(Context(tenantId, connectionId, correlationId, $"shopify-order-get:{externalOrderId}"), externalOrderId.Trim(), cancellationToken);
            if (!single.IsSuccess) { TrackResultFailure(single.Error); throw JobProcessingException.FromAdapter(single.Error!); }
            TrackReceived();
            await UpsertOrder(tenantId, connectionId, single.Value!, cancellationToken, projectReservations: false, persistFinancialObservations: true);
            return true;
        }

        var cursor = await Cursor(tenantId, connectionId, cursorResourceType, cancellationToken);
        var now = timeProvider.GetUtcNow();
        var modifiedAfter = ShopifyOrderHistoryPolicy.ModifiedAfter(
            full,
            allowBaseline,
            cursor.LastModifiedWatermark,
            cursor.LastSuccessAt,
            now);
        do
        {
            TrackRequest();
            var result = await orders.PollAsync(
                Context(tenantId, connectionId, correlationId, $"shopify-order-sync:{cursor.OpaqueCursor ?? "0"}"),
                new OrderPollWindow(modifiedAfter, now, null),
                new(cursor.OpaqueCursor, 100),
                cancellationToken);
            if (!result.IsSuccess) { TrackResultFailure(result.Error); throw JobProcessingException.FromAdapter(result.Error!); }
            foreach (var _ in result.Value!.Items) TrackReceived();
            foreach (var issue in result.Value.Issues ?? [])
                await RecordIssue(tenantId, $"shopify-order-contract:{connectionId}:{issue.Identity}:{issue.Code}", issue.Code, issue.Message, cancellationToken);
            await UpsertOrders(tenantId, connectionId, result.Value.Items, cancellationToken, projectReservations: false);
            if (result.Value.HasMore)
            {
                if (string.IsNullOrWhiteSpace(result.Value.NextCursor)) throw new InvalidOperationException("Shopify sipariş sayfası hasMore=true ancak nextCursor boş döndü.");
                cursor.OpaqueCursor = result.Value.NextCursor;
                cursor.Version++;
                await db.SaveChangesAsync(cancellationToken);
                continue;
            }

            cursor.OpaqueCursor = null;
            cursor.LastModifiedWatermark = now.AddSeconds(-60);
            cursor.Version++;
            await db.SaveChangesAsync(cancellationToken);
            break;
        } while (!cancellationToken.IsCancellationRequested);
        return true;
    }

    private async Task<bool> SyncOpenOrders(Guid tenantId, Guid connectionId, string correlationId, CancellationToken cancellationToken)
    {
        var platformCode = await db.PlatformConnections.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.Id == connectionId)
            .Select(x => x.PlatformCode)
            .SingleOrDefaultAsync(cancellationToken);
        var isShopify = platformCode == "SHOPIFY";
        var isHepsiburada = platformCode == "HEPSIBURADA";
        var lifecycleBatchSize = Math.Clamp(configuration.GetValue("MarketplaceSync:OrderLifecycle:BatchSize", 25), 1, 100);
        var cursor = await Cursor(tenantId, connectionId, "ORDER_LIFECYCLE", cancellationToken);
        List<string> externalOrderIds;
        if (isHepsiburada)
        {
            var includeUnpackagedNewOrders = OpenOrderLifecyclePolicy.ShouldPollWithoutPackage(platformCode, "NEW");
            var includeUnpackagedOnHoldOrders = OpenOrderLifecyclePolicy.ShouldPollWithoutPackage(platformCode, "ON_HOLD");
            var lifecycleOrders = db.Orders.AsNoTracking().Where(order => order.TenantId == tenantId
                && order.ConnectionId == connectionId
                && (db.ShipmentPackages.Any(package => package.TenantId == tenantId
                        && package.ConnectionId == connectionId
                        && package.OrderId == order.Id
                        && package.Status != ShipmentPackageStatus.Delivered
                        && package.Status != ShipmentPackageStatus.Cancelled
                        && package.Status != ShipmentPackageStatus.Returned)
                    || ((includeUnpackagedNewOrders && order.DerivedStatus == "NEW"
                            || includeUnpackagedOnHoldOrders && order.DerivedStatus == "ON_HOLD")
                        && !db.ShipmentPackages.Any(package => package.TenantId == tenantId
                            && package.ConnectionId == connectionId
                            && package.OrderId == order.Id))));

            var lifecycleOffset = int.TryParse(cursor.OpaqueCursor, NumberStyles.None, CultureInfo.InvariantCulture, out var savedOffset)
                ? Math.Max(0, savedOffset)
                : 0;
            externalOrderIds = await lifecycleOrders
                .OrderBy(order => order.ExternalOrderId)
                .Select(order => order.ExternalOrderId)
                .Skip(lifecycleOffset)
                .Take(lifecycleBatchSize)
                .ToListAsync(cancellationToken);
            if (externalOrderIds.Count == 0 && lifecycleOffset > 0)
            {
                // Wrap to the first page so the lifecycle scan keeps polling
                // when a status request fails or an order has no new package.
                lifecycleOffset = 0;
                externalOrderIds = await lifecycleOrders
                    .OrderBy(order => order.ExternalOrderId)
                    .Select(order => order.ExternalOrderId)
                    .Take(lifecycleBatchSize)
                    .ToListAsync(cancellationToken);
            }
            cursor.OpaqueCursor = externalOrderIds.Count == lifecycleBatchSize
                ? (lifecycleOffset + externalOrderIds.Count).ToString(CultureInfo.InvariantCulture)
                : null;
        }
        else
        {
            externalOrderIds = await (from package in db.ShipmentPackages.AsNoTracking()
                                      join order in db.Orders.AsNoTracking()
                                          on new { package.TenantId, package.OrderId } equals new { order.TenantId, OrderId = order.Id }
                                      where package.TenantId == tenantId && package.ConnectionId == connectionId && package.Status != ShipmentPackageStatus.Delivered && package.Status != ShipmentPackageStatus.Cancelled && package.Status != ShipmentPackageStatus.Returned
                                      group package by order.ExternalOrderId into openOrder
                                      orderby openOrder.Min(x => x.UpdatedAt)
                                      select openOrder.Key)
                .Take(lifecycleBatchSize)
                .ToListAsync(cancellationToken);
        }

        var recoveredOrders = new List<RemoteOrder>();
        foreach (var externalOrderId in externalOrderIds)
        {
            TrackRequest();
            var result = await orders.GetAsync(Context(tenantId, connectionId, correlationId, $"order-lifecycle:{externalOrderId}"), externalOrderId, cancellationToken);
            if (!result.IsSuccess)
            {
                if (result.Error?.Class == AdapterErrorClass.NotFound) continue;
                TrackResultFailure(result.Error);
                var error = result.Error!;
                await RecordIssue(
                    tenantId,
                    $"order-lifecycle:{connectionId}:{externalOrderId}",
                    error.Code,
                    $"Sipariş yaşam döngüsü yenilenemedi; sonraki otomatik taramada tekrar denenecek. {error.SafeMessage}",
                    cancellationToken);
                continue;
            }
            TrackReceived();
            recoveredOrders.Add(result.Value!);
            await ResolveIssue(tenantId, $"order-lifecycle:{connectionId}:{externalOrderId}", cancellationToken);
        }
        if (recoveredOrders.Count > 0) await UpsertOrders(tenantId, connectionId, recoveredOrders, cancellationToken, projectReservations: !isShopify);
        if (isHepsiburada)
            await ReconcileHepsiburadaPackageStatuses(tenantId, connectionId, correlationId, cancellationToken);

        cursor.LastModifiedWatermark = timeProvider.GetUtcNow();
        cursor.Version++;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private async Task ReconcileHepsiburadaPackageStatuses(Guid tenantId, Guid connectionId, string correlationId, CancellationToken cancellationToken)
    {
        var cursor = await Cursor(tenantId, connectionId, HepsiburadaPackageStatusReconciliationPolicy.CursorResourceType, cancellationToken);
        var now = timeProvider.GetUtcNow();
        var hasContinuation = !string.IsNullOrWhiteSpace(cursor.OpaqueCursor);
        var intervalSeconds = Math.Clamp(
            configuration.GetValue("MarketplaceSync:HepsiburadaPackageStatusReconciliation:IntervalSeconds", (int)HepsiburadaPackageStatusReconciliationPolicy.DefaultInterval.TotalSeconds),
            (int)TimeSpan.FromHours(1).TotalSeconds,
            (int)TimeSpan.FromDays(7).TotalSeconds);
        if (!HepsiburadaPackageStatusReconciliationPolicy.ShouldRun(cursor.LastSuccessAt, hasContinuation, now, TimeSpan.FromSeconds(intervalSeconds)))
            return;

        var batchSize = Math.Clamp(
            configuration.GetValue("MarketplaceSync:HepsiburadaPackageStatusReconciliation:BatchSize", HepsiburadaPackageStatusReconciliationPolicy.DefaultBatchSize),
            1,
            HepsiburadaPackageStatusReconciliationPolicy.MaximumBatchSize);
        var offset = int.TryParse(cursor.OpaqueCursor, NumberStyles.None, CultureInfo.InvariantCulture, out var savedOffset)
            ? Math.Max(0, savedOffset)
            : 0;
        var packageBatch = await (from package in db.ShipmentPackages.AsNoTracking()
                                  join order in db.Orders.AsNoTracking()
                                      on new { package.TenantId, package.OrderId } equals new { order.TenantId, OrderId = order.Id }
                                  where package.TenantId == tenantId
                                      && package.ConnectionId == connectionId
                                      && package.ExternalPackageId != ""
                                      && !package.ExternalPackageId.StartsWith("order:")
                                  orderby package.Id
                                  select new
                                  {
                                      package.Id,
                                      package.ExternalPackageId,
                                      package.Status,
                                      package.StatusOccurredAt,
                                      order.ExternalOrderId,
                                      order.OrderNumber
                                  })
            .Skip(offset)
            .Take(batchSize)
            .ToListAsync(cancellationToken);

        if (packageBatch.Count == 0)
        {
            cursor.OpaqueCursor = null;
            cursor.LastSuccessAt = now;
            cursor.LastError = null;
            cursor.LastErrorAt = null;
            cursor.ConsecutiveFailureCount = 0;
            cursor.Version++;
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        foreach (var orderGroup in packageBatch.GroupBy(package => package.ExternalOrderId, StringComparer.Ordinal))
        {
            TrackRequest();
            var orderResult = await orders.GetAsync(
                Context(tenantId, connectionId, correlationId, $"hepsiburada-package-reconcile:{orderGroup.Key}"),
                orderGroup.Key,
                cancellationToken);
            if (!orderResult.IsSuccess)
            {
                if (orderResult.Error?.Class == AdapterErrorClass.NotFound || orderResult.Error?.HttpStatus == 404)
                {
                    await RecordIssue(tenantId,
                        $"hepsiburada-package-reconcile:{connectionId}:{orderGroup.Key}",
                        "HEPSIBURADA_PACKAGE_RECONCILIATION_ORDER_NOT_FOUND",
                        "Paket durumu yenilenirken Hepsiburada sipariş detayı bulunamadı; sonraki taramada yeniden denenecek.",
                        cancellationToken);
                    continue;
                }
                TrackResultFailure(orderResult.Error);
                throw JobProcessingException.FromAdapter(orderResult.Error!);
            }

            TrackReceived();
            var remoteOrder = orderResult.Value!;
            var expectedOrderNumber = orderGroup.First().OrderNumber;
            if (!string.IsNullOrWhiteSpace(remoteOrder.OrderNumber)
                && !string.Equals(remoteOrder.OrderNumber.Trim(), expectedOrderNumber.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                await RecordIssue(tenantId,
                    $"hepsiburada-package-reconcile-order-mismatch:{connectionId}:{orderGroup.Key}",
                    "HEPSIBURADA_PACKAGE_RECONCILIATION_ORDER_MISMATCH",
                    "Hepsiburada sipariş detayı yerel sipariş numarasıyla eşleşmedi; durum güncellenmedi.",
                    cancellationToken);
                continue;
            }
            await ResolveIssue(tenantId,
                $"hepsiburada-package-reconcile-order-mismatch:{connectionId}:{orderGroup.Key}",
                cancellationToken);

            var observations = new List<RemotePackage>();
            foreach (var localPackage in orderGroup)
            {
                var remotePackage = remoteOrder.Packages.FirstOrDefault(package =>
                    string.Equals(package.ExternalPackageId, localPackage.ExternalPackageId, StringComparison.Ordinal));
                var remoteStatus = remotePackage is null
                    ? ShipmentPackageStatus.ManualReview
                    : ShipmentPackageStatusPolicy.FromRemote(remotePackage.RawStatus);
                string? rawStatus = remotePackage?.RawStatus;
                string? cargoCompany = remotePackage?.CargoProviderExternalId;
                string? trackingCode = remotePackage?.CargoTrackingNumber;
                var occurredAt = remotePackage?.OccurredAt ?? now;

                if (remoteStatus == ShipmentPackageStatus.ManualReview)
                {
                    TrackRequest();
                    var tracking = await orderPackages.GetPackageTrackingInfoAsync(
                        Context(tenantId, connectionId, correlationId, $"hepsiburada-package-reconcile:{localPackage.ExternalPackageId}"),
                        localPackage.ExternalPackageId,
                        cancellationToken);
                    if (!tracking.IsSuccess)
                    {
                        if (tracking.Error?.Class == AdapterErrorClass.NotFound || tracking.Error?.HttpStatus == 404)
                        {
                            await RecordIssue(tenantId,
                                $"hepsiburada-package-reconcile:{connectionId}:{localPackage.ExternalPackageId}",
                                "HEPSIBURADA_PACKAGE_RECONCILIATION_PACKAGE_NOT_FOUND",
                                "Hepsiburada paket takip detayı bulunamadı; mevcut panel kaydı korundu.",
                                cancellationToken);
                            continue;
                        }
                        TrackResultFailure(tracking.Error);
                        throw JobProcessingException.FromAdapter(tracking.Error!);
                    }
                    TrackReceived();
                    if (!string.IsNullOrWhiteSpace(tracking.Value!.OrderNumber)
                        && !string.Equals(tracking.Value.OrderNumber.Trim(), expectedOrderNumber.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        await RecordIssue(tenantId,
                            $"hepsiburada-package-reconcile-owner:{connectionId}:{localPackage.ExternalPackageId}",
                            "HEPSIBURADA_PACKAGE_RECONCILIATION_ORDER_MISMATCH",
                            "Hepsiburada paket takip yanıtı yerel sipariş numarasıyla eşleşmedi; durum güncellenmedi.",
                            cancellationToken);
                        continue;
                    }

                    rawStatus = tracking.Value.Status;
                    cargoCompany = tracking.Value.CargoCompany;
                    trackingCode = tracking.Value.TrackingInfoCode;
                    occurredAt = now;
                    remoteStatus = string.IsNullOrWhiteSpace(rawStatus)
                        ? ShipmentPackageStatus.ManualReview
                        : ShipmentPackageStatusPolicy.FromRemote(rawStatus);
                }

                if (remoteStatus == ShipmentPackageStatus.ManualReview || string.IsNullOrWhiteSpace(rawStatus))
                {
                    await RecordIssue(tenantId,
                        $"hepsiburada-package-reconcile-status:{connectionId}:{localPackage.ExternalPackageId}",
                        "HEPSIBURADA_PACKAGE_RECONCILIATION_STATUS_UNSUPPORTED",
                        "Hepsiburada paket yanıtındaki durum tanınmadı; mevcut panel durumu korundu.",
                        cancellationToken);
                    continue;
                }

                await ResolveIssue(tenantId,
                    $"hepsiburada-package-reconcile:{connectionId}:{localPackage.ExternalPackageId}",
                    cancellationToken);
                await ResolveIssue(tenantId,
                    $"hepsiburada-package-reconcile-owner:{connectionId}:{localPackage.ExternalPackageId}",
                    cancellationToken);
                await ResolveIssue(tenantId,
                    $"hepsiburada-package-reconcile-status:{connectionId}:{localPackage.ExternalPackageId}",
                    cancellationToken);

                // The package tracking read returns its current state but not
                // the original delivery-event date. Reuse the existing event
                // time when the state is already in sync; timestamp a changed
                // state as observed now instead of inventing a historical date.
                occurredAt = HepsiburadaPackageStatusReconciliationPolicy.StatusTimestamp(
                    localPackage.Status,
                    remoteStatus,
                    localPackage.StatusOccurredAt,
                    remotePackage?.OccurredAt,
                    now);

                observations.Add(new RemotePackage(
                    localPackage.ExternalPackageId,
                    remotePackage?.OriginExternalPackageId,
                    rawStatus,
                    occurredAt,
                    cargoCompany,
                    trackingCode,
                    [],
                    remotePackage?.GrossAmount ?? 0,
                    remotePackage?.DiscountAmount ?? 0,
                    remotePackage?.NetAmount ?? 0,
                    remotePackage?.Invoice,
                    remotePackage?.CreatedBy,
                    IsStatusObservation: true));
            }

            await ResolveIssue(tenantId, $"hepsiburada-package-reconcile:{connectionId}:{orderGroup.Key}", cancellationToken);
            if (observations.Count > 0)
                await UpsertOrder(tenantId, connectionId, remoteOrder with { Packages = observations }, cancellationToken);
        }

        cursor.OpaqueCursor = (offset + packageBatch.Count).ToString(CultureInfo.InvariantCulture);
        cursor.Version++;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<bool> ReconcileOrders(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, CancellationToken cancellationToken)
    {
        var platformCode = await db.PlatformConnections.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.Id == connectionId)
            .Select(x => x.PlatformCode)
            .SingleOrDefaultAsync(cancellationToken);
        var isShopify = platformCode == "SHOPIFY";
        var lookbackDays = ReadBoundedInt(payloadJson, "lookbackDays", 1, 1, 90);
        var batchSize = ReadBoundedInt(payloadJson, "batchSize", 25, 1, 100);
        var end = timeProvider.GetUtcNow();
        var start = end.AddDays(-lookbackDays);
        // Reconciliation is deliberately driven by local order numbers and the
        // documented orderNumber read. The stream is ideal for discovery, but a
        // per-order read is the reliable repair path for already imported orders
        // when a stream cursor or stream request is temporarily unavailable.
        var externalOrderIds = await db.Orders.AsNoTracking()
            .Where(x => x.TenantId == tenantId
                && x.ConnectionId == connectionId
                && x.OrderedAt >= start
                && x.OrderedAt <= end)
            .OrderBy(x => x.UpdatedAt)
            .ThenBy(x => x.OrderedAt)
            .Select(x => x.ExternalOrderId)
            .Take(batchSize)
            .ToListAsync(cancellationToken);

        foreach (var externalOrderId in externalOrderIds)
        {
            TrackRequest();
            var result = await orders.GetAsync(Context(tenantId, connectionId, correlationId, $"order-reconcile:{externalOrderId}"), externalOrderId, cancellationToken);
            if (!result.IsSuccess)
            {
                if (result.Error?.Class == AdapterErrorClass.NotFound) continue;
                TrackResultFailure(result.Error);
                await RecordIssue(tenantId, $"order-reconcile:{connectionId}:{externalOrderId}", result.Error!.Code,
                    $"Sipariş uzlaştırılamadı; sonraki otomatik taramada tekrar denenecek. {result.Error.SafeMessage}", cancellationToken);
                continue;
            }

            TrackReceived();
            await UpsertOrder(tenantId, connectionId, result.Value!, cancellationToken, projectReservations: !isShopify, persistFinancialObservations: isShopify);
            await ResolveIssue(tenantId, $"order-reconcile:{connectionId}:{externalOrderId}", cancellationToken);
        }

        return true;
    }

    private async Task<bool> ReconcileOrderInvoices(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, CancellationToken cancellationToken)
    {
        var platformCode = await db.PlatformConnections.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.Id == connectionId)
            .Select(x => x.PlatformCode)
            .SingleOrDefaultAsync(cancellationToken);
        var isShopify = platformCode == "SHOPIFY";
        var isHepsiburada = platformCode == "HEPSIBURADA";
        var batchSize = ReadBoundedInt(payloadJson, "batchSize", 50, 1, 250);
        var externalOrderIds = isHepsiburada
            ? new List<string>()
            : await (from package in db.ShipmentPackages.AsNoTracking()
                     join order in db.Orders.AsNoTracking()
                         on new { package.TenantId, package.OrderId } equals new { order.TenantId, OrderId = order.Id }
                     where package.TenantId == tenantId
                         && package.ConnectionId == connectionId
                         && package.Status != ShipmentPackageStatus.Cancelled
                         && !DashboardMetricPolicy.InvoiceExcludedOrderStatuses.Contains(order.DerivedStatus)
                         && package.MarketplaceInvoiceStatus != MarketplaceInvoiceStatus.Invoiced
                     orderby package.MarketplaceInvoiceStatus == MarketplaceInvoiceStatus.Received ? 0 : 1,
                         package.MarketplaceInvoiceObservedAt, package.UpdatedAt
                     select order.ExternalOrderId)
                .Distinct()
                .Take(batchSize)
                .ToListAsync(cancellationToken);

        SyncCursor? invoiceCursor = null;
        Guid? lastReconciledOrderId = null;
        if (isHepsiburada)
        {
            // Order detail is the only documented source for hasInvoice. Rotate
            // across every eligible order so an unchanged first page cannot
            // starve older unpackaged claims and orders forever.
            invoiceCursor = await Cursor(tenantId, connectionId, "ORDER_INVOICE_RECONCILIATION", cancellationToken);
            var afterOrder = HepsiburadaInvoiceReconciliationBatchPolicy.ReadCursor(invoiceCursor.OpaqueCursor);
            var eligibleOrders = db.Orders.AsNoTracking()
                .Where(order => order.TenantId == tenantId
                    && order.ConnectionId == connectionId
                    && !DashboardMetricPolicy.InvoiceExcludedOrderStatuses.Contains(order.DerivedStatus)
                    && (!db.ShipmentPackages.Any(package => package.TenantId == tenantId
                            && package.ConnectionId == connectionId
                            && package.OrderId == order.Id
                            && package.Status != ShipmentPackageStatus.Cancelled)
                        || db.ShipmentPackages.Any(package => package.TenantId == tenantId
                            && package.ConnectionId == connectionId
                            && package.OrderId == order.Id
                            && package.Status != ShipmentPackageStatus.Cancelled
                            && package.MarketplaceInvoiceStatus != MarketplaceInvoiceStatus.Invoiced)));

            var afterCursorQuery = eligibleOrders;
            if (afterOrder is { } cursorOrder)
                afterCursorQuery = afterCursorQuery.Where(order => order.UpdatedAt > cursorOrder.UpdatedAt
                    || order.UpdatedAt == cursorOrder.UpdatedAt && order.OrderedAt > cursorOrder.OrderedAt
                    || order.UpdatedAt == cursorOrder.UpdatedAt && order.OrderedAt == cursorOrder.OrderedAt
                        && order.Id.CompareTo(cursorOrder.OrderId) > 0);
            var afterCursor = await afterCursorQuery
                .OrderBy(order => order.UpdatedAt)
                .ThenBy(order => order.OrderedAt)
                .ThenBy(order => order.Id)
                .Select(order => new HepsiburadaInvoiceOrderCandidate(order.Id, order.ExternalOrderId, order.UpdatedAt, order.OrderedAt))
                .Take(batchSize)
                .ToListAsync(cancellationToken);

            IReadOnlyCollection<HepsiburadaInvoiceOrderCandidate> wrapped = [];
            if (afterOrder is { } wrapOrder && afterCursor.Count < batchSize)
            {
                wrapped = await eligibleOrders
                    .Where(order => order.UpdatedAt < wrapOrder.UpdatedAt
                        || order.UpdatedAt == wrapOrder.UpdatedAt && order.OrderedAt < wrapOrder.OrderedAt
                        || order.UpdatedAt == wrapOrder.UpdatedAt && order.OrderedAt == wrapOrder.OrderedAt
                            && order.Id.CompareTo(wrapOrder.OrderId) <= 0)
                    .OrderBy(order => order.UpdatedAt)
                    .ThenBy(order => order.OrderedAt)
                    .ThenBy(order => order.Id)
                    .Select(order => new HepsiburadaInvoiceOrderCandidate(order.Id, order.ExternalOrderId, order.UpdatedAt, order.OrderedAt))
                    .Take(batchSize - afterCursor.Count)
                    .ToListAsync(cancellationToken);
            }

            var selected = HepsiburadaInvoiceReconciliationBatchPolicy.Select(afterCursor, wrapped, batchSize);
            externalOrderIds = selected.Select(candidate => candidate.ExternalOrderId).ToList();
            lastReconciledOrderId = selected.LastOrDefault()?.OrderId;
            var lastCandidate = selected.LastOrDefault();
            if (lastCandidate is not null) invoiceCursor.OpaqueCursor = HepsiburadaInvoiceReconciliationBatchPolicy.WriteCursor(lastCandidate);
        }

        foreach (var externalOrderId in externalOrderIds)
        {
            TrackRequest();
            var result = await orders.GetAsync(Context(tenantId, connectionId, correlationId, $"order-invoice-reconciliation:{externalOrderId}"), externalOrderId, cancellationToken);
            if (!result.IsSuccess)
            {
                if (result.Error?.Class == AdapterErrorClass.NotFound) continue;
                TrackResultFailure(result.Error);
                await RecordIssue(tenantId, $"order-invoice-reconciliation:{connectionId}:{externalOrderId}", result.Error!.Code,
                    $"Siparişin pazaryeri fatura durumu yenilenemedi; sonraki otomatik taramada tekrar denenecek. {result.Error.SafeMessage}", cancellationToken);
                continue;
            }

            TrackReceived();
            if (isHepsiburada)
                await MergeHepsiburadaOrderInvoiceState(tenantId, connectionId, result.Value!, cancellationToken);
            await UpsertOrder(tenantId, connectionId, result.Value!, cancellationToken, projectReservations: !isShopify, persistFinancialObservations: isShopify);
            await ResolveIssue(tenantId, $"order-invoice-reconciliation:{connectionId}:{externalOrderId}", cancellationToken);
        }

        if (invoiceCursor is not null && lastReconciledOrderId is not null)
        {
            invoiceCursor.Version++;
            await db.SaveChangesAsync(cancellationToken);
        }

        return true;
    }

    private async Task MergeHepsiburadaOrderInvoiceState(Guid tenantId, Guid connectionId, RemoteOrder remote, CancellationToken cancellationToken)
    {
        bool hasInvoice;
        try
        {
            using var snapshot = JsonDocument.Parse(remote.RawJson);
            hasInvoice = HepsiburadaJsonMapper.InvoiceUploaded(snapshot.RootElement);
        }
        catch (JsonException)
        {
            return;
        }

        var order = await db.Orders.AsNoTracking()
            .SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ExternalOrderId == remote.ExternalOrderId, cancellationToken);
        if (order is null) return;

        var packages = await db.ShipmentPackages
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.OrderId == order.Id && x.Status != ShipmentPackageStatus.Cancelled)
            .ToListAsync(cancellationToken);
        var rawStatus = hasInvoice ? "INVOICED" : "NOT_INVOICED";
        foreach (var package in packages)
        {
            var observation = new RemotePackageInvoiceObservation(rawStatus, null, null, null);
            var remotePackage = new RemotePackage(
                package.ExternalPackageId,
                null,
                package.RawStatus,
                remote.LastModifiedAt,
                package.CargoProviderExternalId,
                package.CargoTrackingNumber,
                [],
                Invoice: observation);
            await MergeMarketplaceInvoiceState(package, remotePackage, cancellationToken);
        }
    }

    private async Task<bool> SyncProducts(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, Guid? jobId, CancellationToken cancellationToken)
    {
        var connection = await db.PlatformConnections.AsNoTracking()
            .SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == connectionId && (x.PlatformCode == "TRENDYOL" || x.PlatformCode == "SHOPIFY" || x.PlatformCode == "HEPSIBURADA"), cancellationToken);
        var isShopify = connection?.PlatformCode == "SHOPIFY";
        var isHepsiburada = connection?.PlatformCode == "HEPSIBURADA";
        // Product import is read-only on the remote platform and writes only to
        // Ravencia's local catalog. Shopify observations never become local
        // price/stock authority.
        // Keep it restricted to operational connections and recognised environments.
        if (connection is null || connection.Environment is not ("STAGE" or "PRODUCTION") || connection.Status is not ("ACTIVE" or "VERIFIED")) return false;
        var inventoryPolicy = await db.ConnectionInventoryPolicies.AsNoTracking()
            .SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId, cancellationToken);

        var fullScan = ReadBoolean(payloadJson, "full");
        var newOnly = ReadBoolean(payloadJson, "newOnly");
        var existingOnly = ReadBoolean(payloadJson, "existingOnly");
        var mappingOnly = ReadBoolean(payloadJson, "mappingOnly");
        var includeArchived = ReadBoolean(payloadJson, "includeArchived");
        var includePendingApproval = !isShopify && !isHepsiburada && ReadBoolean(payloadJson, "includePendingApproval");
        var updateExistingProducts = ReadBooleanOrDefault(payloadJson, "updateExistingProducts", true);
        // Shopify's single inactive-product option intentionally covers both
        // archived variants and draft products; keep older payloads compatible.
        var includeDrafts = ReadBoolean(payloadJson, "includeDrafts") || isShopify && includeArchived;
        // Mapping inactive products is a repair/backfill operation: an
        // incremental cursor can otherwise skip archived or draft products
        // that have not changed since the last catalog sync.
        var fullCatalogForMapping = ProductImportScanPolicy.RequiresFullCatalogForMapping(mappingOnly, includeArchived, includeDrafts);
        var effectiveFullScan = fullScan || fullCatalogForMapping;
        var productLookup = ReadText(payloadJson, "productLookup");
        var singleLookup = !string.IsNullOrWhiteSpace(productLookup);
        var scanLabel = singleLookup
            ? "Tekil ürün çekimi"
            : mappingOnly
            ? "Ürün eşleme"
            : fullScan
            ? "Tam katalog taraması"
            : newOnly
                ? "Ekli olmayan ürünler taranıyor"
                : existingOnly
                    ? isShopify ? "Ekli barkodlar güncelleniyor" : "Ekli model kodları güncelleniyor"
                    : "Yeni ve değişen ürünler taranıyor";
        var archiveLabel = includeArchived ? " · Arşiv ürünleri dahil" : " · Arşiv ürünleri hariç";
        var draftLabel = includeDrafts ? " · Taslak ürünleri dahil" : " · Taslak ürünleri hariç";
        var lifecycleLabel = isShopify
            ? includeArchived ? " · Arşiv ve taslak ürünler dahil" : " · Arşiv ve taslak ürünler hariç"
            : archiveLabel + draftLabel + (includePendingApproval ? " · Onay bekleyen ürünler dahil" : " · Onay bekleyen ürünler hariç");
        var contentLabel = !newOnly && !mappingOnly
            ? updateExistingProducts ? " · Mevcut ürün bilgileri güncellenecek" : " · Mevcut ürün bilgileri korunacak"
            : "";
        var importJobId = jobId ?? Guid.CreateVersion7();
        var importSession = await db.ProductImportSessions.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.JobId == importJobId, cancellationToken);
        var now = timeProvider.GetUtcNow();
        if (importSession is null)
        {
            importSession = new ProductImportSession
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                ConnectionId = connectionId,
                JobId = importJobId,
                Phase = "SCANNING",
                CreatedAt = now,
                UpdatedAt = now,
                Version = 1
            };
            db.ProductImportSessions.Add(importSession);
            await db.SaveChangesAsync(cancellationToken);
        }
        if (importSession.Phase == "COMPLETED")
        {
            await db.ProductImportStagingRecords
                .Where(x => x.TenantId == tenantId && x.JobId == importJobId)
                .ExecuteDeleteAsync(cancellationToken);
            if (jobId is { } finishedJob)
                await UpdateProductSyncProgressAsync(tenantId, finishedJob, importSession.ReceivedProducts, importSession.TotalProducts, 100, $"{importSession.ReceivedProducts:N0} · Aktarımı tamamlandı", cancellationToken, keepExistingTotal: true);
            return true;
        }
        var previousProgress = jobId is { } progressJob
            ? await db.IntegrationJobs.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == progressJob).Select(x => new { x.ProgressProcessed, x.ProgressSkipped, x.ProgressFailed }).SingleOrDefaultAsync(cancellationToken)
            : null;
        telemetryImportProcessedCount = previousProgress?.ProgressProcessed ?? 0;
        telemetryImportSkippedCount = previousProgress?.ProgressSkipped ?? 0;
        telemetryImportFailedCount = previousProgress?.ProgressFailed ?? 0;
        var receivedProducts = importSession.ReceivedProducts;
        int? totalProducts = importSession.TotalProducts;
        var cursor = await Cursor(tenantId, connectionId, "PRODUCTS", cancellationToken);
        var scanRequired = importSession.Phase == "SCANNING";
        if (scanRequired && importSession.PageNumber == 0 && effectiveFullScan && cursor.OpaqueCursor is not null)
        {
            cursor.OpaqueCursor = null;
            cursor.Version++;
            await db.SaveChangesAsync(cancellationToken);
        }
        var hasSnapshots = await db.MarketplaceProductLinks.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId, cancellationToken);
        var existingProductExternalIds = newOnly || existingOnly || mappingOnly
            ? (await db.MarketplaceProductLinks.AsNoTracking().Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId).Select(x => x.ExternalId).ToListAsync(cancellationToken)).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : null;
        var existingVariantExternalIds = newOnly
            ? (await db.MarketplaceVariantLinks.AsNoTracking().Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId).Select(x => x.ExternalId).ToListAsync(cancellationToken)).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : null;
        var existingIdentityCodes = existingOnly
                    ? (await db.ProductVariants.AsNoTracking()
                    .Where(x => x.TenantId == tenantId && (x.ModelCode != null || isShopify && x.BarcodeNormalized != null))
                    .Select(x => new { x.ModelCode, x.BarcodeNormalized })
                    .ToListAsync(cancellationToken))
                .SelectMany(value => isShopify ? new[] { value.BarcodeNormalized } : new[] { value.ModelCode })
                .Select(identity => NormalizeCatalogKey(identity, 160))
                .Where(identity => !string.IsNullOrWhiteSpace(identity))
                .ToHashSet(StringComparer.Ordinal)
            : null;
        var hasCategoryMappings = !isShopify && !isHepsiburada && await db.CategoryMappings.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.Status == "VERIFIED", cancellationToken);
        // The first attribute backfill must revisit the already imported catalog. Keep
        // LastModifiedWatermark null until that full pass is complete so a retry cannot
        // accidentally switch to the incremental window halfway through the backfill.
        if (!isShopify && !isHepsiburada && !hasCategoryMappings && cursor.OpaqueCursor is null && cursor.LastModifiedWatermark is not null)
        {
            cursor.LastModifiedWatermark = null;
            cursor.Version++;
            await db.SaveChangesAsync(cancellationToken);
        }
        DateTimeOffset? modifiedAfter = !effectiveFullScan && !singleLookup && hasSnapshots && cursor.LastModifiedWatermark is not null ? cursor.LastModifiedWatermark.Value.AddMinutes(-2) : null;
        var productFilter = ProductImportFilter(modifiedAfter, productLookup, includePendingApproval);
        // Read the remote catalog into the durable staging pool before preparing
        // the larger Trendyol reference snapshots. The worker can therefore
        // resume the scan and finalize complete model groups without retaining
        // the full remote catalog in memory.
        ReferenceSnapshot? brandReferences = null;
        ReferenceSnapshot? categoryReferences = null;
        IReadOnlyList<ReferenceItem> categoryItems = [];
        // Keep one shared panel attribute for the same normalized name. This lets
        // an attribute such as "Bel" collect every category where Trendyol uses it
        // instead of creating a separate hidden definition for each category.
        var importedAttributeLibrary = new Dictionary<string, AttributeDefinition>(StringComparer.Ordinal);
        var categoryContexts = new Dictionary<string, CategoryAttributeContext>(StringComparer.Ordinal);
        var nextCursor = scanRequired
            ? importSession.PageNumber > 0 ? importSession.NextCursor : singleLookup || effectiveFullScan ? null : cursor.OpaqueCursor
            : null;
        var pageNumber = importSession.PageNumber;
        if (jobId is { } currentJob && scanRequired)
            await UpdateProductSyncProgressAsync(tenantId, currentJob, receivedProducts, totalProducts, null, scanLabel + lifecycleLabel + contentLabel + " · Ürün aktarım havuzu hazırlanıyor", cancellationToken);
        while (scanRequired)
        {
            pageNumber++;
            if (jobId is { } readingJob)
            {
                await UpdateProductSyncProgressAsync(
                    tenantId,
                    readingJob,
                    receivedProducts,
                    totalProducts,
                    totalProducts is { } knownTotal && knownTotal > 0
                        ? Math.Clamp((int)Math.Floor(receivedProducts * 100d / knownTotal), 0, 99)
                        : null,
                    $"{scanLabel}{lifecycleLabel} · {pageNumber}. sayfa okunuyor · Alınan {receivedProducts:N0} · İşlenen {telemetryImportProcessedCount:N0} · Atlanan {telemetryImportSkippedCount:N0} · Hatalı {telemetryImportFailedCount:N0}",
                    cancellationToken);
            }
            TrackRequest();
            var result = await products.ListCatalogAsync(
                Context(tenantId, connectionId, correlationId, $"product-sync:{nextCursor ?? "0"}"),
                new(nextCursor, 100),
                productFilter,
                cancellationToken);
            if (!result.IsSuccess) { TrackResultFailure(result.Error); throw JobProcessingException.FromAdapter(result.Error!); }
            foreach (var _ in result.Value!.Items) TrackReceived();
            receivedProducts += result.Value.Items.Count;
            totalProducts ??= result.Value.TotalCount;
            var pageSnapshots = result.Value.Items
                .Where(snapshot => includeDrafts || !snapshot.IsDraft)
                .Where(snapshot => includePendingApproval || !snapshot.IsPendingApproval)
                .Select(snapshot => includeArchived
                    ? snapshot
                    : snapshot with { Variants = snapshot.Variants.Where(variant => !variant.Archived).ToList() })
                .Where(snapshot => includeArchived || snapshot.Variants.Count > 0)
                .ToList();
            telemetryImportSkippedCount += result.Value.Items.Count - pageSnapshots.Count;
            // Trendyol can change the catalog while a long scan is running. If
            // its reported total falls behind the pages actually returned, do
            // not publish an impossible "received / total" progress state.
            if (totalProducts is > 0 && receivedProducts > totalProducts.Value) totalProducts = null;

            var receivedOrderStart = receivedProducts - result.Value.Items.Count;
            var pageHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var stagedRows = new List<ProductImportStagingRecord>();
            foreach (var (snapshot, index) in pageSnapshots.Select((snapshot, index) => (snapshot, index)))
            {
                if (string.IsNullOrWhiteSpace(snapshot.ExternalProductId)) continue;
                var snapshotJson = JsonSerializer.Serialize(snapshot);
                var snapshotHash = Hash(snapshotJson);
                if (!pageHashes.Add(snapshotHash)) continue;
                stagedRows.Add(new ProductImportStagingRecord
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = tenantId,
                    ConnectionId = connectionId,
                    JobId = importJobId,
                    ModelKey = Short(CatalogImportOrdering.ModelKey(snapshot), 256),
                    ExternalProductId = Short(snapshot.ExternalProductId, 256),
                    SnapshotHash = snapshotHash,
                    SnapshotJson = snapshotJson,
                    ReceivedOrder = receivedOrderStart + index,
                    State = "STAGED",
                    CreatedAt = timeProvider.GetUtcNow()
                });
            }
            if (stagedRows.Count > 0)
            {
                var existingHashes = await db.ProductImportStagingRecords.AsNoTracking()
                    .Where(x => x.TenantId == tenantId && x.JobId == importJobId && pageHashes.Contains(x.SnapshotHash))
                    .Select(x => x.SnapshotHash)
                    .ToListAsync(cancellationToken);
                if (existingHashes.Count > 0)
                    stagedRows.RemoveAll(row => existingHashes.Contains(row.SnapshotHash, StringComparer.OrdinalIgnoreCase));
                db.ProductImportStagingRecords.AddRange(stagedRows);
            }

            var hasMore = result.Value.HasMore;
            if (hasMore && string.IsNullOrWhiteSpace(result.Value.NextCursor))
                throw new InvalidOperationException("Pazar yeri ürün sayfası hasMore=true ancak nextPageToken boş döndü.");
            nextCursor = hasMore ? result.Value.NextCursor : null;
            var pageSession = await db.ProductImportSessions.SingleAsync(x => x.TenantId == tenantId && x.JobId == importJobId, cancellationToken);
            pageSession.NextCursor = nextCursor;
            pageSession.ReceivedProducts = receivedProducts;
            pageSession.TotalProducts = totalProducts;
            pageSession.PageNumber = pageNumber;
            pageSession.UpdatedAt = timeProvider.GetUtcNow();
            if (!hasMore) pageSession.Phase = "READY";
            await db.SaveChangesAsync(cancellationToken);
            db.ChangeTracker.Clear();

            foreach (var invalidSnapshot in pageSnapshots.Where(x => string.IsNullOrWhiteSpace(x.ExternalProductId)))
            {
                telemetryImportFailedCount++;
                telemetryFailedCount++;
                try
                {
                    await RecordProductImportFailure(tenantId, connectionId, invalidSnapshot, new InvalidOperationException("Pazar yeri ürün kimliği boş döndü."), cancellationToken);
                }
                catch (Exception issueException) when (issueException is not OperationCanceledException)
                {
                    db.ChangeTracker.Clear();
                }
            }

            if (jobId is { } receivedJob)
            {
                var percent = totalProducts is { } total && total > 0
                    ? Math.Clamp((int)Math.Floor(receivedProducts * 100d / total), 0, 99)
                    : (int?)null;
                await UpdateProductSyncProgressAsync(tenantId, receivedJob, receivedProducts, totalProducts, percent, ProductImportProgressLabel(pageNumber, totalProducts, result.Value.HasMore ? "sayfa alındı; aktarım havuzuna yazıldı" : "tarama tamamlandı; aktarım havuzu hazır", receivedProducts, mappingOnly), cancellationToken);
            }

            if (!result.Value.HasMore || cancellationToken.IsCancellationRequested) break;
        }

        cancellationToken.ThrowIfCancellationRequested();

        var stagedSession = await db.ProductImportSessions.AsNoTracking().SingleAsync(x => x.TenantId == tenantId && x.JobId == importJobId, cancellationToken);
        receivedProducts = stagedSession.ReceivedProducts;
        totalProducts = stagedSession.TotalProducts;
        pageNumber = stagedSession.PageNumber;

        if (jobId is { } referenceJob)
            await UpdateProductSyncProgressAsync(
                tenantId,
                referenceJob,
                receivedProducts,
                totalProducts,
                totalProducts is { } completeTotal && completeTotal > 0 ? 99 : null,
                ProductImportProgressLabel(pageNumber, totalProducts, "sayfalar okundu; referanslar hazırlanıyor · markalar", receivedProducts, mappingOnly),
                cancellationToken);

        // Product imports carry Trendyol's brand id, so keep the current brand
        // reference available for an automatic panel-brand mapping while the
        // catalog rows are being materialized. These calls intentionally happen
        // after remote product paging so a cold reference cache cannot hide
        // product-read progress.
        brandReferences = isShopify || isHepsiburada ? null : await EnsureReferenceSnapshot(tenantId, connectionId, "BRANDS", null, correlationId, cancellationToken);
        if (jobId is { } categoryReferenceJob)
            await UpdateProductSyncProgressAsync(
                tenantId,
                categoryReferenceJob,
                receivedProducts,
                totalProducts,
                totalProducts is { } completeTotal && completeTotal > 0 ? 99 : null,
                ProductImportProgressLabel(pageNumber, totalProducts, "sayfalar okundu; referanslar hazırlanıyor · kategoriler", receivedProducts, mappingOnly),
                cancellationToken);
        categoryReferences = isShopify ? null : await EnsureReferenceSnapshot(tenantId, connectionId, "CATEGORIES", null, correlationId, cancellationToken);
        categoryItems = categoryReferences is null
            ? []
            : await db.ReferenceItems.AsNoTracking().Where(x => x.TenantId == tenantId && x.SnapshotId == categoryReferences.Id && x.ResourceType == "CATEGORIES" && x.IsActive).ToListAsync(cancellationToken);
        if (categoryReferences is not null)
        {
            importedAttributeLibrary = (await db.AttributeDefinitions
                    .Where(x => x.TenantId == tenantId && x.IsActive)
                    .OrderBy(x => x.CreatedAt)
                    .ThenBy(x => x.Id)
                    .ToListAsync(cancellationToken))
                .Where(x => !string.IsNullOrWhiteSpace(NormalizeCatalogKey(x.Name, 160)))
                .GroupBy(x => NormalizeCatalogKey(x.Name, 160), StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        }

        if (jobId is { } orderingJob)
            await UpdateProductSyncProgressAsync(tenantId, orderingJob, receivedProducts, totalProducts, null, ProductImportProgressLabel(pageNumber, totalProducts, "aktarim havuzu hazır; model grupları başlıyor", receivedProducts, mappingOnly), cancellationToken);

        await db.ProductImportSessions.Where(x => x.TenantId == tenantId && x.JobId == importJobId).ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Phase, "FINALIZING").SetProperty(x => x.UpdatedAt, timeProvider.GetUtcNow()), cancellationToken);
        db.ChangeTracker.Clear();
        var modelKeys = await db.ProductImportStagingRecords.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.JobId == importJobId && x.State == "STAGED")
            .GroupBy(x => x.ModelKey)
            .OrderBy(group => group.Min(x => x.ReceivedOrder))
            .Select(group => group.Key)
            .ToListAsync(cancellationToken);
        var totalModelGroups = await db.ProductImportStagingRecords.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.JobId == importJobId)
            .Select(x => x.ModelKey)
            .Distinct()
            .CountAsync(cancellationToken);
        var importedModelCount = await db.ProductImportStagingRecords.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.JobId == importJobId && x.State != "STAGED")
            .Select(x => x.ModelKey)
            .Distinct()
            .CountAsync(cancellationToken);
        foreach (var modelKey in modelKeys)
        {
            var stagingRows = await db.ProductImportStagingRecords.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.JobId == importJobId && x.ModelKey == modelKey && x.State == "STAGED")
                .OrderBy(x => x.ReceivedOrder)
                .ToListAsync(cancellationToken);
            if (stagingRows.Count == 0) continue;
            var snapshotGroups = stagingRows
                .Select(row => JsonSerializer.Deserialize<RemoteCatalogProduct>(row.SnapshotJson) ?? throw new JsonException("Ürün aktarım havuzu kaydı çözümlenemedi."))
                .GroupBy(x => x.ExternalProductId, StringComparer.OrdinalIgnoreCase)
                .Select(MergeCatalogSnapshots)
                .ToList();
            try
            {
                foreach (var snapshot in snapshotGroups)
                {
                    if (mappingOnly)
                    {
                        var mapped = await MapExistingCatalogProduct(tenantId, connectionId, snapshot, isShopify, cancellationToken);
                        if (!mapped) telemetryImportSkippedCount++;
                        else telemetryImportProcessedCount++;
                        continue;
                    }
                    var productAlreadyLinked = existingProductExternalIds?.Contains(snapshot.ExternalProductId) == true;
                    var hasNewVariant = existingVariantExternalIds is not null
                        && snapshot.Variants.Any(variant => !existingVariantExternalIds.Contains(Short(variant.ExternalVariantId, 256)));
                    var matchesExistingIdentity = existingIdentityCodes is not null
                        && snapshot.Variants.Any(variant => existingIdentityCodes.Contains(NormalizeCatalogKey(isShopify ? variant.Barcode : variant.ModelCode ?? variant.Barcode, 160)));
                    if (newOnly && productAlreadyLinked && !hasNewVariant)
                    {
                        telemetryImportSkippedCount++;
                        continue;
                    }
                    if (existingOnly && !productAlreadyLinked && !matchesExistingIdentity)
                    {
                        telemetryImportSkippedCount++;
                        continue;
                    }
                    var categoryContext = categoryReferences is null
                        ? null
                        : await EnsureCategoryAttributeContext(tenantId, connectionId, categoryReferences, snapshot, categoryItems, importedAttributeLibrary, categoryContexts, correlationId, cancellationToken);
                    var changed = await UpsertCatalogProduct(tenantId, connectionId, snapshot, categoryContext, brandReferences?.Id, inventoryPolicy, cancellationToken, saveChanges: false, onlyNewVariants: newOnly && productAlreadyLinked, observeOnly: isShopify || isHepsiburada, preferBarcode: isShopify, onlyExistingVariants: existingOnly && isShopify, updateExistingProducts: updateExistingProducts, isHepsiburada: isHepsiburada);
                    existingProductExternalIds?.Add(snapshot.ExternalProductId);
                    if (existingVariantExternalIds is not null)
                        foreach (var variant in snapshot.Variants)
                            existingVariantExternalIds.Add(Short(variant.ExternalVariantId, 256));
                    if (changed) telemetryImportProcessedCount++;
                    else telemetryImportSkippedCount++;
                }
                await db.SaveChangesAsync(cancellationToken);
                await db.ProductImportStagingRecords
                    .Where(x => x.TenantId == tenantId && x.JobId == importJobId && x.ModelKey == modelKey && x.State == "STAGED")
                    .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.State, "COMPLETED").SetProperty(x => x.FinalizedAt, timeProvider.GetUtcNow()), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                db.ChangeTracker.Clear();
                categoryContexts.Clear();
                importedAttributeLibrary.Clear();
                telemetryImportFailedCount += snapshotGroups.Count;
                telemetryFailedCount += snapshotGroups.Count;
                foreach (var snapshot in snapshotGroups)
                {
                    try
                    {
                        await RecordProductImportFailure(tenantId, connectionId, snapshot, exception, cancellationToken);
                    }
                    catch (Exception issueException) when (issueException is not OperationCanceledException)
                    {
                        db.ChangeTracker.Clear();
                    }
                }
                await db.ProductImportStagingRecords
                    .Where(x => x.TenantId == tenantId && x.JobId == importJobId && x.ModelKey == modelKey && x.State == "STAGED")
                    .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.State, "FAILED").SetProperty(x => x.ErrorSummary, Short(exception.Message, 2_000)), cancellationToken);
            }
            finally
            {
                db.ChangeTracker.Clear();
                categoryContexts.Clear();
                importedAttributeLibrary.Clear();
            }

            importedModelCount++;
            if (jobId is { } itemProgressJob)
            {
                var completedProducts = telemetryImportProcessedCount + telemetryImportSkippedCount + telemetryImportFailedCount;
                var percent = totalProducts is { } total && total > 0
                    ? Math.Clamp((int)Math.Floor(completedProducts * 100d / total), 0, 99)
                    : (int?)null;
                await UpdateProductSyncProgressAsync(tenantId, itemProgressJob, receivedProducts, totalProducts, percent, ProductImportProgressLabel(pageNumber, totalProducts, $"{importedModelCount:N0}/{totalModelGroups:N0} model grubu tamamlandı", receivedProducts, mappingOnly), cancellationToken);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        if (receivedProducts > 0 && telemetryImportFailedCount >= receivedProducts)
        {
            if (jobId is { } failedJob)
                await UpdateProductSyncProgressAsync(tenantId, failedJob, receivedProducts, totalProducts, 100, ProductImportProgressLabel(pageNumber, totalProducts, "aktarımı tamamlanamadı", receivedProducts, mappingOnly), cancellationToken);
            throw new JobProcessingException(JobExecutionResult.ManualReview("PRODUCT_IMPORT_ALL_FAILED", "Ürün aktarımındaki kayıtların tamamı işlenemedi; aktarım başarılı sayılmadı."));
        }
        if (!singleLookup)
        {
            var completedCursor = await Cursor(tenantId, connectionId, "PRODUCTS", cancellationToken);
            completedCursor.OpaqueCursor = null;
            // Approved-products supports a modified-date filter. Keep a short
            // overlap so a variant changed while a page was being read is not lost.
            completedCursor.LastModifiedWatermark = timeProvider.GetUtcNow().AddSeconds(-60);
            completedCursor.Version++;
        }
        await db.SaveChangesAsync(cancellationToken);
        await db.ProductImportSessions
            .Where(x => x.TenantId == tenantId && x.JobId == importJobId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Phase, "COMPLETED").SetProperty(x => x.NextCursor, (string?)null).SetProperty(x => x.CompletedAt, timeProvider.GetUtcNow()).SetProperty(x => x.UpdatedAt, timeProvider.GetUtcNow()), cancellationToken);
        await db.ProductImportStagingRecords
            .Where(x => x.TenantId == tenantId && x.JobId == importJobId)
            .ExecuteDeleteAsync(cancellationToken);
        if (jobId is { } completedJob)
            await UpdateProductSyncProgressAsync(tenantId, completedJob, receivedProducts, null, 100, ProductImportProgressLabel(pageNumber, totalProducts, "aktarımı tamamlandı", receivedProducts, mappingOnly), cancellationToken, keepExistingTotal: true);
        return true;
    }

    private Task<int> UpdateProductSyncProgressAsync(Guid tenantId, Guid jobId, int current, int? total, int? percent, string label, CancellationToken cancellationToken, bool keepExistingTotal = false)
    {
        var query = db.IntegrationJobs.Where(x => x.TenantId == tenantId && x.Id == jobId && (x.JobType == MarketplaceJobTypes.ProductSync || x.JobType == MarketplaceJobTypes.ShopifyProductSync || x.JobType == MarketplaceJobTypes.HepsiburadaProductSync));
        return keepExistingTotal
            ? query.ExecuteUpdateAsync(setters => setters.SetProperty(x => x.ProgressCurrent, current).SetProperty(x => x.ProgressPercent, percent).SetProperty(x => x.ProgressLabel, label).SetProperty(x => x.ProgressReceived, current).SetProperty(x => x.ProgressProcessed, telemetryImportProcessedCount).SetProperty(x => x.ProgressSkipped, telemetryImportSkippedCount).SetProperty(x => x.ProgressFailed, telemetryImportFailedCount), cancellationToken)
            : query.ExecuteUpdateAsync(setters => setters.SetProperty(x => x.ProgressCurrent, current).SetProperty(x => x.ProgressTotal, total).SetProperty(x => x.ProgressPercent, percent).SetProperty(x => x.ProgressLabel, label).SetProperty(x => x.ProgressReceived, current).SetProperty(x => x.ProgressProcessed, telemetryImportProcessedCount).SetProperty(x => x.ProgressSkipped, telemetryImportSkippedCount).SetProperty(x => x.ProgressFailed, telemetryImportFailedCount), cancellationToken);
    }

    private string ProductImportProgressLabel(int pageNumber, int? total, string suffix, int received, bool mappingOnly)
    {
        var totalPart = total is > 0 ? $" / {total:N0}" : "";
        var processedLabel = mappingOnly ? "Eşlenen" : "İşlenen";
        var skippedLabel = mappingOnly ? "Eşleşmeyen" : "Atlanan";
        return $"{received:N0}{totalPart} · Alınan {received:N0} · {processedLabel} {telemetryImportProcessedCount:N0} · {skippedLabel} {telemetryImportSkippedCount:N0} · Hatalı {telemetryImportFailedCount:N0} · {pageNumber}. sayfa {suffix}";
    }

    private async Task RecordProductImportFailure(Guid tenantId, Guid connectionId, RemoteCatalogProduct snapshot, Exception exception, CancellationToken cancellationToken)
    {
        var externalProductId = Short(snapshot.ExternalProductId, 256);
        var issueIdentity = string.IsNullOrWhiteSpace(externalProductId)
            ? $"missing:{Hash(JsonSerializer.Serialize(snapshot))[..16]}"
            : externalProductId;
        var root = exception.GetBaseException();
        var constraint = root is PostgresException postgres
            ? $"sqlState={postgres.SqlState}; constraint={postgres.ConstraintName ?? "—"}; table={postgres.TableName ?? "—"}; detail={postgres.Detail ?? postgres.MessageText}"
            : $"exception={root.GetType().Name}; detail={root.Message}";
        var summary = Short($"Pazar yeri ürünü '{(string.IsNullOrWhiteSpace(externalProductId) ? "kimlik yok" : externalProductId)}' aktarılmadı; sonraki ürünle devam edildi. Veritabanı ayrıntısı: {constraint}", 2_000);
        await RecordIssue(tenantId, $"product-sync-import:{connectionId}:{issueIdentity}", "PRODUCT_IMPORT_FAILED", summary, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<ReferenceSnapshot?> EnsureReferenceSnapshot(Guid tenantId, Guid connectionId, string resourceType, string? parentExternalId, string correlationId, CancellationToken cancellationToken)
    {
        var scope = string.IsNullOrWhiteSpace(parentExternalId) ? "" : parentExternalId.Trim();
        var current = await db.ReferenceSnapshots
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == resourceType && x.ScopeExternalId == scope && x.IsCurrent)
            .OrderByDescending(x => x.FetchedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (current is not null && timeProvider.GetUtcNow() - current.FetchedAt < TimeSpan.FromHours(24)) return current;

        var payload = JsonSerializer.Serialize(new { resourceType, parentExternalId });
        try
        {
            if (!await SyncReferences(tenantId, connectionId, payload, correlationId, cancellationToken)) return current;
        }
        catch (JobProcessingException exception)
        {
            await RecordIssue(tenantId, $"product-reference-sync:{connectionId}:{resourceType}:{scope}", exception.Result.ErrorCode ?? "REFERENCE_SYNC_FAILED", exception.Result.ErrorSummary ?? "Pazar yeri kategori/özellik referansı eşitlenemedi; mevcut panel kayıtları korundu.", cancellationToken);
            return current;
        }

        return await db.ReferenceSnapshots
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == resourceType && x.ScopeExternalId == scope && x.IsCurrent)
            .OrderByDescending(x => x.FetchedAt)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task<CategoryAttributeContext?> EnsureCategoryAttributeContext(
        Guid tenantId,
        Guid connectionId,
        ReferenceSnapshot categorySnapshot,
        RemoteCatalogProduct snapshot,
        IReadOnlyList<ReferenceItem> categoryItems,
        IDictionary<string, AttributeDefinition> importedAttributeLibrary,
        IDictionary<string, CategoryAttributeContext> cache,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var externalCategoryId = Short(snapshot.CategoryExternalId, 256);
        if (string.IsNullOrWhiteSpace(externalCategoryId)) return null;
        if (cache.TryGetValue(externalCategoryId, out var cached))
        {
            await EnsureObservedVariantAttributeValues(tenantId, cached, snapshot, cancellationToken);
            await EnsureExactWebColorValueMappings(tenantId, connectionId, cached, cancellationToken);
            return cached;
        }

        var categoryItem = categoryItems.FirstOrDefault(x => string.Equals(x.ExternalId, externalCategoryId, StringComparison.Ordinal));
        if (categoryItem is null)
        {
            await RecordIssue(tenantId, $"product-category-reference:{connectionId}:{externalCategoryId}", "PRODUCT_CATEGORY_REFERENCE_MISSING", "Ürünün kategori kimliği güncel pazar yeri snapshot'ında bulunamadı; kategori özellikleri eşlenmedi.", cancellationToken);
            return null;
        }

        var attributeSnapshot = await EnsureReferenceSnapshot(tenantId, connectionId, "CATEGORY_ATTRIBUTES", externalCategoryId, correlationId, cancellationToken);
        if (attributeSnapshot is null) return null;
        var remoteAttributes = await db.ReferenceItems.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.SnapshotId == attributeSnapshot.Id && x.ResourceType == "CATEGORY_ATTRIBUTES" && x.IsActive)
            .OrderBy(x => x.SortOrder).ThenBy(x => x.Name)
            .ToListAsync(cancellationToken);

        var observedNames = snapshot.Variants
            .SelectMany(x => x.Options.Keys)
            .Select(x => NormalizeCatalogKey(x, 320))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet(StringComparer.Ordinal);
        var observedOptionValues = snapshot.Variants
            .SelectMany(x => x.Options)
            .Select(pair => (Axis: ObservedVariantValueAxis(pair.Key), Value: NormalizeCatalogKey(pair.Value, 320)))
            .Where(item => item.Axis is not null && !string.IsNullOrWhiteSpace(item.Value))
            .GroupBy(item => item.Axis!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(item => item.Value).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        var candidateRemoteAttributes = remoteAttributes
            .Where(x => x.IsRequired == true || observedNames.Contains(NormalizeCatalogKey(x.Name, 320)) || VariantOptionAxis(x.Name) is not null)
            .ToList();
        var valuesByAttribute = new Dictionary<string, IReadOnlyList<ReferenceItem>>(StringComparer.Ordinal);
        foreach (var remoteAttribute in candidateRemoteAttributes)
        {
            var valueSnapshot = await EnsureReferenceSnapshot(tenantId, connectionId, "ATTRIBUTE_VALUES", $"{externalCategoryId}/{remoteAttribute.ExternalId}", correlationId, cancellationToken);
            if (valueSnapshot is null) continue;
            var values = await db.ReferenceItems.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.SnapshotId == valueSnapshot.Id && x.ResourceType == "ATTRIBUTE_VALUES" && x.IsActive)
                .OrderBy(x => x.SortOrder).ThenBy(x => x.Name)
                .ToListAsync(cancellationToken);
            if (ObservedVariantValueAxis(remoteAttribute.Name) is { } axis && observedOptionValues.TryGetValue(axis, out var usedValues))
                values = values.Where(value => usedValues.Contains(NormalizeCatalogKey(value.Name, 320))).ToList();
            // A category definition without active values is not a usable
            // product attribute. The exception is the real Renk slicer: it
            // can legitimately contain seller-defined values even when
            // Trendyol's category value endpoint returns an empty list.
            // Keep that field in the context so observed variant values can
            // populate the local Renk option instead of falling back to Web
            // Color.
            if (values.Count > 0 || IsRealColorOptionKey(remoteAttribute.Name))
                valuesByAttribute[remoteAttribute.ExternalId] = values;
        }

        var localCategory = await EnsureMappedCategory(tenantId, connectionId, categorySnapshot, categoryItem, cancellationToken);
        var attributes = new Dictionary<string, LocalCategoryAttribute>(StringComparer.Ordinal);
        foreach (var remoteAttribute in candidateRemoteAttributes.Where(x => valuesByAttribute.ContainsKey(x.ExternalId)))
        {
            var values = valuesByAttribute[remoteAttribute.ExternalId];
            var localAttribute = await EnsureMappedAttribute(tenantId, connectionId, localCategory, categoryItem, attributeSnapshot, remoteAttribute, values, importedAttributeLibrary, cancellationToken);
            attributes[NormalizeCatalogKey(remoteAttribute.Name, 320)] = localAttribute;
            attributes.TryAdd(NormalizeCatalogKey(localAttribute.Definition.Name, 320), localAttribute);
        }

        // Hide stale requirements created by older importer versions when the
        // marketplace no longer provides any values for the mapped attribute.
        // Keep the row for audit/recovery, but remove it from the product
        // editor and required-attribute validation until values return.
        var importedAttributeIds = await db.AttributeMappings.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ScopeExternalId == externalCategoryId && x.Status == "VERIFIED")
            .Select(x => x.LocalId)
            .ToListAsync(cancellationToken);
        var visibleAttributeIds = attributes.Values.Select(x => x.Definition.Id).ToHashSet();
        if (importedAttributeIds.Count > 0)
        {
            var staleRequirements = await db.CategoryAttributeRequirements
                .Where(x => x.TenantId == tenantId && x.CategoryId == localCategory.Id && importedAttributeIds.Contains(x.AttributeId) && !visibleAttributeIds.Contains(x.AttributeId))
                .ToListAsync(cancellationToken);
            foreach (var stale in staleRequirements)
            {
                if (!stale.IsPanelScoped && stale.Role == "ATTRIBUTE") continue;
                stale.IsPanelScoped = false;
                stale.Role = "ATTRIBUTE";
                stale.IsRequired = false;
                stale.AllowsCustomValue = false;
                stale.Version++;
            }
        }

        var result = new CategoryAttributeContext(localCategory, externalCategoryId, attributes);
        await EnsureObservedVariantAttributeValues(tenantId, result, snapshot, cancellationToken);
        await EnsureExactWebColorValueMappings(tenantId, connectionId, result, cancellationToken);
        cache[externalCategoryId] = result;
        return result;
    }

    private async Task EnsureObservedVariantAttributeValues(
        Guid tenantId,
        CategoryAttributeContext categoryContext,
        RemoteCatalogProduct snapshot,
        CancellationToken cancellationToken)
    {
        foreach (var mapped in categoryContext.Attributes.Values
            .GroupBy(item => item.Definition.Id)
            .Select(group => group
                .OrderBy(item => IsWebColorOptionKey(item.Remote.Name) ? 1 : 0)
                .First()))
        {
            // Web Color is a presentation field. It may map to the same local
            // Renk attribute, but its marketplace values must never be added as
            // local option values. Only collect values from the real slicer
            // field (normally Trendyol's Renk/Color field).
            if (IsWebColorOptionKey(mapped.Remote.Name)) continue;
            var remoteName = NormalizeCatalogKey(mapped.Remote.Name, 320);
            var axis = VariantOptionAxis(mapped.Remote.Name);
            var values = snapshot.Variants
                .SelectMany(x => x.Options)
                .Where(pair => NormalizeCatalogKey(pair.Key, 320) == remoteName
                    || (axis is not null && VariantOptionAxis(pair.Key) == axis && !IsWebColorOptionKey(pair.Key)))
                .Select(pair => pair.Value.Trim())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (values.Count == 0) continue;
            mapped.Definition.DataType = AttributeDataType.SingleSelect;
            mapped.Definition.SelectionMode = "SINGLE";
            foreach (var valueText in values)
            {
                var normalized = NormalizeCatalogKey(valueText, 320);
                if (!categoryContext.ObservedVariantValueKeys.Add($"{mapped.Definition.Id:D}:{normalized}"))
                    continue;
                var value = db.AttributeValues.Local.FirstOrDefault(x => x.TenantId == tenantId && x.AttributeId == mapped.Definition.Id && x.NormalizedValue == normalized)
                    ?? await db.AttributeValues.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.AttributeId == mapped.Definition.Id && x.NormalizedValue == normalized, cancellationToken);
                if (value is not null)
                {
                    value.IsActive = true;
                    continue;
                }
                db.AttributeValues.Add(new AttributeValue
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = tenantId,
                    AttributeId = mapped.Definition.Id,
                    Value = Short(valueText, 320),
                    NormalizedValue = normalized,
                    SortOrder = values.IndexOf(valueText),
                    IsActive = true,
                    Version = 1
                });
            }
        }
    }

    private async Task<Category> EnsureMappedCategory(Guid tenantId, Guid connectionId, ReferenceSnapshot categorySnapshot, ReferenceItem remoteCategory, CancellationToken cancellationToken)
    {
        var mapping = await db.CategoryMappings.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ScopeExternalId == categorySnapshot.ScopeExternalId && x.ExternalId == remoteCategory.ExternalId, cancellationToken);
        Category? category = mapping is null ? null : await db.Categories.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == mapping.LocalId, cancellationToken);
        var leafName = CategoryLeafName(remoteCategory.Name, remoteCategory.Path);
        var normalized = NormalizeCatalogKey(remoteCategory.Path, 160);
        var legacyNormalized = NormalizeCatalogKey(remoteCategory.Name, 160);
        if (string.IsNullOrWhiteSpace(normalized)) normalized = NormalizeCatalogKey(leafName, 160);
        category ??= db.Categories.Local.FirstOrDefault(x => x.TenantId == tenantId && x.ParentId is null && (x.NormalizedName == normalized || x.NormalizedName == legacyNormalized))
            ?? await db.Categories.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ParentId == null && (x.NormalizedName == normalized || x.NormalizedName == legacyNormalized), cancellationToken);
        if (category is null)
        {
            var now = timeProvider.GetUtcNow();
            category = new Category { Id = Guid.CreateVersion7(), TenantId = tenantId, Name = leafName, NormalizedName = normalized, Path = leafName, Depth = 0, IsLeaf = true, IsActive = true, CreatedAt = now, UpdatedAt = now, Version = 1 };
            db.Categories.Add(category);
        }
        else
        {
            category.Name = leafName;
            category.NormalizedName = normalized;
            category.Path = leafName;
            category.IsLeaf = true;
            category.IsActive = true;
            category.UpdatedAt = timeProvider.GetUtcNow();
            category.Version++;
        }

        if (mapping is null)
        {
            mapping = new CategoryMapping { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = connectionId, SnapshotId = categorySnapshot.Id, LocalId = category.Id, ScopeExternalId = categorySnapshot.ScopeExternalId, ExternalId = remoteCategory.ExternalId, Status = "VERIFIED", VerifiedAt = timeProvider.GetUtcNow(), Version = 1 };
            db.CategoryMappings.Add(mapping);
        }
        else
        {
            mapping.SnapshotId = categorySnapshot.Id;
            mapping.Status = "VERIFIED";
            mapping.VerifiedAt = timeProvider.GetUtcNow();
            mapping.Version++;
        }
        return category;
    }

    private async Task<LocalCategoryAttribute> EnsureMappedAttribute(
        Guid tenantId,
        Guid connectionId,
        Category category,
        ReferenceItem remoteCategory,
        ReferenceSnapshot attributeSnapshot,
        ReferenceItem remoteAttribute,
        IReadOnlyList<ReferenceItem> values,
        IDictionary<string, AttributeDefinition> importedAttributeLibrary,
        CancellationToken cancellationToken)
    {
        var optionAxis = VariantOptionAxis(remoteAttribute.Name);
        var canonicalName = IsWebColorOptionKey(remoteAttribute.Name) ? "Renk" : optionAxis switch
        {
            "COLOR" => "Renk",
            "SIZE" => "Beden",
            _ => Short(remoteAttribute.Name, 160)
        };
        var code = Short($"TRD_{remoteCategory.ExternalId}_{remoteAttribute.ExternalId}", 96);
        var dataType = values.Count > 0
            ? (remoteAttribute.AllowsMultipleValues == true ? AttributeDataType.MultiSelect : AttributeDataType.SingleSelect)
            : AttributeDataType.Text;
        var attributeKey = NormalizeCatalogKey(canonicalName, 160);
        var attribute = importedAttributeLibrary.TryGetValue(attributeKey, out var sharedAttribute)
            ? sharedAttribute
            : db.AttributeDefinitions.Local.FirstOrDefault(x => x.TenantId == tenantId && x.IsActive && NormalizeCatalogKey(x.Name, 160) == attributeKey)
                ?? (await db.AttributeDefinitions
                    .Where(x => x.TenantId == tenantId && x.IsActive)
                    .OrderBy(x => x.CreatedAt)
                    .ThenBy(x => x.Id)
                    .ToListAsync(cancellationToken))
                    .FirstOrDefault(x => NormalizeCatalogKey(x.Name, 160) == attributeKey)
                ?? db.AttributeDefinitions.Local.FirstOrDefault(x => x.TenantId == tenantId && x.Code == code)
                ?? await db.AttributeDefinitions.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Code == code, cancellationToken);
        if (values.Count == 0 && attribute is not null && (attribute.DataType is AttributeDataType.SingleSelect or AttributeDataType.MultiSelect))
            dataType = attribute.DataType;
        if (attribute is null && optionAxis is not null)
        {
            var existingAttributes = await db.AttributeDefinitions
                .Where(x => x.TenantId == tenantId && x.IsActive)
                .OrderBy(x => x.CreatedAt)
                .ToListAsync(cancellationToken);
            attribute = db.AttributeDefinitions.Local.FirstOrDefault(x => x.TenantId == tenantId && x.IsActive && VariantOptionAxis(x.Name) == optionAxis)
                ?? existingAttributes.FirstOrDefault(x => VariantOptionAxis(x.Name) == optionAxis);
        }
        if (attribute is null)
        {
            attribute = new AttributeDefinition { Id = Guid.CreateVersion7(), TenantId = tenantId, Code = code, Name = canonicalName, DataType = dataType, SelectionMode = dataType == AttributeDataType.MultiSelect ? "MULTI" : dataType == AttributeDataType.SingleSelect ? "SINGLE" : null, IsActive = true, CreatedAt = timeProvider.GetUtcNow(), UpdatedAt = timeProvider.GetUtcNow(), Version = 1 };
            db.AttributeDefinitions.Add(attribute);
        }
        else
        {
            attribute.Name = canonicalName;
            attribute.DataType = dataType;
            attribute.SelectionMode = dataType == AttributeDataType.MultiSelect ? "MULTI" : dataType == AttributeDataType.SingleSelect ? "SINGLE" : null;
            attribute.IsActive = true;
            attribute.UpdatedAt = timeProvider.GetUtcNow();
            attribute.Version++;
        }
        importedAttributeLibrary[attributeKey] = attribute;

        if (IsWebColorOptionKey(remoteAttribute.Name))
        {
            var legacyColorAttributes = await db.AttributeDefinitions
                .Where(x => x.TenantId == tenantId && x.IsActive && x.Id != attribute.Id)
                .ToListAsync(cancellationToken);
            foreach (var legacyColorAttribute in legacyColorAttributes.Where(x => IsWebColorOptionKey(x.Name)))
            {
                legacyColorAttribute.IsActive = false;
                legacyColorAttribute.UpdatedAt = timeProvider.GetUtcNow();
                legacyColorAttribute.Version++;
            }
        }

        // The same canonical option can be represented by multiple marketplace
        // fields. Reuse a requirement already added in this import context
        // before querying the database, otherwise two pending inserts can
        // violate the category/attribute uniqueness constraint on SaveChanges.
        var requirement = db.CategoryAttributeRequirements.Local.FirstOrDefault(x =>
                x.TenantId == tenantId
                && x.CategoryId == category.Id
                && x.AttributeId == attribute.Id)
            ?? await db.CategoryAttributeRequirements.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.CategoryId == category.Id && x.AttributeId == attribute.Id, cancellationToken);
        // Web Color may share the panel Renk source with the real Renk
        // option. Never let that presentation field demote an existing
        // variant-option requirement to a plain attribute.
        var role = requirement?.Role == "OPTION" || IsVariantOptionName(remoteAttribute.Name) ? "OPTION" : "ATTRIBUTE";
        if (requirement is null)
            db.CategoryAttributeRequirements.Add(new CategoryAttributeRequirement { Id = Guid.CreateVersion7(), TenantId = tenantId, CategoryId = category.Id, AttributeId = attribute.Id, IsRequired = remoteAttribute.IsRequired == true, AllowsCustomValue = remoteAttribute.AllowsCustomValue == true, IsPanelScoped = true, DisplayOrder = remoteAttribute.SortOrder ?? 0, Role = role, Version = 1 });
        else
        {
            requirement.IsRequired = remoteAttribute.IsRequired == true;
            requirement.AllowsCustomValue = remoteAttribute.AllowsCustomValue == true;
            requirement.DisplayOrder = remoteAttribute.SortOrder ?? requirement.DisplayOrder;
            requirement.Role = role;
            requirement.IsPanelScoped = true;
            requirement.Version++;
        }

        // Several marketplace fields (for example the real color slicer and
        // Web Color) can intentionally share the same panel attribute. Their
        // external field is part of the mapping identity, so both mappings
        // must be retained even when they are pending in this save batch.
        var attributeMapping = db.AttributeMappings.Local.FirstOrDefault(x =>
                x.TenantId == tenantId
                && x.ConnectionId == connectionId
                && x.LocalId == attribute.Id
                && x.ScopeExternalId == remoteCategory.ExternalId
                && x.ExternalId == remoteAttribute.ExternalId)
            ?? await db.AttributeMappings.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.LocalId == attribute.Id && x.ScopeExternalId == remoteCategory.ExternalId && x.ExternalId == remoteAttribute.ExternalId, cancellationToken);
        if (attributeMapping is null)
            db.AttributeMappings.Add(new AttributeMapping { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = connectionId, SnapshotId = attributeSnapshot.Id, LocalId = attribute.Id, ScopeExternalId = remoteCategory.ExternalId, ExternalId = remoteAttribute.ExternalId, Status = "VERIFIED", VerifiedAt = timeProvider.GetUtcNow(), Version = 1 });
        else
        {
            attributeMapping.SnapshotId = attributeSnapshot.Id;
            attributeMapping.ExternalId = remoteAttribute.ExternalId;
            attributeMapping.Status = "VERIFIED";
            attributeMapping.VerifiedAt = timeProvider.GetUtcNow();
            attributeMapping.Version++;
        }

        // Web Color is a marketplace presentation field, not a second local
        // option group. Its many-to-one value mapping is maintained explicitly
        // in the mapping workspace (for example Mürdüm -> Mor), so importing
        // the remote list must not create marketplace-named values under the
        // panel's Renk attribute or overwrite those custom mappings.
        if (IsWebColorOptionKey(remoteAttribute.Name))
            return new LocalCategoryAttribute(attribute, remoteAttribute, values, role);

        foreach (var remoteValue in values)
        {
            var normalizedValue = NormalizeCatalogKey(remoteValue.Name, 320);
            var value = db.AttributeValues.Local.FirstOrDefault(x => x.TenantId == tenantId && x.AttributeId == attribute.Id && x.NormalizedValue == normalizedValue)
                ?? await db.AttributeValues.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.AttributeId == attribute.Id && x.NormalizedValue == normalizedValue, cancellationToken);
            if (value is null)
            {
                value = new AttributeValue { Id = Guid.CreateVersion7(), TenantId = tenantId, AttributeId = attribute.Id, Value = Short(remoteValue.Name, 320), NormalizedValue = normalizedValue, SortOrder = remoteValue.SortOrder ?? 0, IsActive = true, Version = 1 };
                db.AttributeValues.Add(value);
            }
            else
            {
                value.Value = Short(remoteValue.Name, 320);
                value.SortOrder = remoteValue.SortOrder ?? value.SortOrder;
                value.IsActive = true;
                value.Version++;
            }
            var valueScope = $"{remoteCategory.ExternalId}/{remoteAttribute.ExternalId}";
            var valueSnapshot = await db.ReferenceSnapshots.AsNoTracking().Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == "ATTRIBUTE_VALUES" && x.ScopeExternalId == valueScope && x.IsCurrent).OrderByDescending(x => x.FetchedAt).FirstOrDefaultAsync(cancellationToken);
            if (valueSnapshot is null) continue;
            var valueMapping = db.AttributeValueMappings.Local.FirstOrDefault(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.LocalId == value.Id && x.ScopeExternalId == valueScope)
                ?? await db.AttributeValueMappings.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.LocalId == value.Id && x.ScopeExternalId == valueScope, cancellationToken);
            valueMapping ??= db.AttributeValueMappings.Local.FirstOrDefault(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ScopeExternalId == valueScope && x.ExternalId == remoteValue.ExternalId)
                ?? await db.AttributeValueMappings.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ScopeExternalId == valueScope && x.ExternalId == remoteValue.ExternalId, cancellationToken);
            if (valueMapping is null)
                db.AttributeValueMappings.Add(new AttributeValueMapping { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = connectionId, SnapshotId = valueSnapshot.Id, LocalId = value.Id, ScopeExternalId = valueScope, ExternalId = remoteValue.ExternalId, Status = "VERIFIED", VerifiedAt = timeProvider.GetUtcNow(), Version = 1 });
            else
            {
                valueMapping.SnapshotId = valueSnapshot.Id;
                valueMapping.ExternalId = remoteValue.ExternalId;
                valueMapping.Status = "VERIFIED";
                valueMapping.VerifiedAt = timeProvider.GetUtcNow();
                valueMapping.Version++;
            }
        }
        return new LocalCategoryAttribute(attribute, remoteAttribute, values, role);
    }

    private async Task EnsureExactWebColorValueMappings(
        Guid tenantId,
        Guid connectionId,
        string categoryExternalId,
        AttributeDefinition localAttribute,
        ReferenceItem remoteAttribute,
        IReadOnlyList<ReferenceItem> remoteValues,
        CategoryAttributeContext categoryContext,
        CancellationToken cancellationToken)
    {
        var hasNewTrackedValue = db.AttributeValues.Local.Any(x =>
            x.TenantId == tenantId
            && x.AttributeId == localAttribute.Id
            && x.IsActive
            && !categoryContext.ExactWebColorValueIds.Contains(x.Id));
        if (categoryContext.ExactWebColorInitializedAttributeIds.Contains(localAttribute.Id) && !hasNewTrackedValue)
            return;

        var valueScope = $"{categoryExternalId}/{remoteAttribute.ExternalId}";
        var valueSnapshot = await db.ReferenceSnapshots.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == "ATTRIBUTE_VALUES" && x.ScopeExternalId == valueScope && x.IsCurrent)
            .OrderByDescending(x => x.FetchedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (valueSnapshot is null) return;

        var trackedLocalValues = db.AttributeValues.Local
            .Where(x => x.TenantId == tenantId && x.AttributeId == localAttribute.Id && x.IsActive)
            .ToList();
        var trackedLocalValueIds = trackedLocalValues.Select(x => x.Id).ToHashSet();
        var localValues = trackedLocalValues
            .Concat(await db.AttributeValues
                .Where(x => x.TenantId == tenantId && x.AttributeId == localAttribute.Id && x.IsActive && !trackedLocalValueIds.Contains(x.Id))
                .ToListAsync(cancellationToken))
            .ToList();
        var remoteByValue = remoteValues
            .GroupBy(x => NormalizeCatalogKey(x.Name, 320), StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);

        categoryContext.ExactWebColorInitializedAttributeIds.Add(localAttribute.Id);
        foreach (var localValue in localValues.Where(x => categoryContext.ExactWebColorValueIds.Add(x.Id)))
        {
            if (!remoteByValue.TryGetValue(localValue.NormalizedValue, out var remoteValue)) continue;

            var existing = db.AttributeValueMappings.Local.FirstOrDefault(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.LocalId == localValue.Id && x.ScopeExternalId == valueScope)
                ?? await db.AttributeValueMappings.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.LocalId == localValue.Id && x.ScopeExternalId == valueScope, cancellationToken);
            // A saved custom mapping takes precedence over the exact-name
            // default and is intentionally never overwritten by a sync.
            if (existing is not null) continue;

            db.AttributeValueMappings.Add(new AttributeValueMapping
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                ConnectionId = connectionId,
                SnapshotId = valueSnapshot.Id,
                LocalId = localValue.Id,
                ScopeExternalId = valueScope,
                ExternalId = remoteValue.ExternalId,
                Status = "VERIFIED",
                VerifiedAt = timeProvider.GetUtcNow(),
                Version = 1
            });
        }
    }

    private async Task EnsureExactWebColorValueMappings(
        Guid tenantId,
        Guid connectionId,
        CategoryAttributeContext categoryContext,
        CancellationToken cancellationToken)
    {
        foreach (var mapped in categoryContext.Attributes.Values
            .Where(x => IsWebColorOptionKey(x.Remote.Name))
            .GroupBy(x => x.Definition.Id)
            .Select(x => x.First()))
        {
            // Exact matches are safe defaults for the special mapping. They
            // make values such as “Çok Renkli” publishable immediately while
            // leaving non-identical, user-defined mappings (for example
            // Mürdüm -> Mor) untouched.
            await EnsureExactWebColorValueMappings(tenantId, connectionId, categoryContext.ExternalCategoryId, mapped.Definition, mapped.Remote, mapped.Values, categoryContext, cancellationToken);
        }
    }

    private async Task UpsertProductAttributeAssignments(Guid tenantId, Guid connectionId, Product product, ProductVariant variant, IReadOnlyDictionary<string, string> options, CategoryAttributeContext categoryContext, CancellationToken cancellationToken)
    {
        var sortOrder = 0;
        foreach (var pair in options)
        {
            if (IsWebColorOptionKey(pair.Key))
            {
                sortOrder++;
                continue;
            }
            if (!TryGetMappedAttribute(categoryContext.Attributes, pair.Key, out var mapped))
            {
                sortOrder++;
                continue;
            }
            if (IsCatalogProductOption(mapped, pair.Key))
            {
                sortOrder++;
                continue;
            }

            var valueKey = NormalizeCatalogKey(pair.Value, 320);
            var remoteValue = mapped.Values.FirstOrDefault(x => NormalizeCatalogKey(x.Name, 320) == valueKey);
            Guid? valueId = null;
            string? textValue = null;
            decimal? numberValue = null;
            bool? booleanValue = null;
            var localValue = await ResolveMappedAttributeValue(tenantId, connectionId, categoryContext, mapped, remoteValue, pair.Value, cancellationToken);
            if (localValue is not null)
            {
                valueId = localValue.Id;
            }
            else if (mapped.Remote.AllowsCustomValue == true)
            {
                if (mapped.Definition.DataType == AttributeDataType.Number && decimal.TryParse(pair.Value, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var number)) numberValue = number;
                else if (mapped.Definition.DataType == AttributeDataType.Boolean && bool.TryParse(pair.Value, out var boolean)) booleanValue = boolean;
                else textValue = Short(pair.Value, 320);
            }
            else
            {
                await RecordIssue(tenantId, $"product-attribute-value:{product.Id}:{variant.Id}:{mapped.Definition.Id}:{valueKey}", "PRODUCT_ATTRIBUTE_VALUE_UNMAPPED", $"Pazar yeri ürün özelliği '{pair.Key}: {pair.Value}' için güncel panel değeri bulunamadı; atama yapılmadı.", cancellationToken);
                sortOrder++;
                continue;
            }
            if (valueId is null && textValue is null && numberValue is null && booleanValue is null)
            {
                await RecordIssue(tenantId, $"product-attribute-value:{product.Id}:{variant.Id}:{mapped.Definition.Id}:{valueKey}", "PRODUCT_ATTRIBUTE_VALUE_UNMAPPED", $"Pazar yeri ürün özelliği '{pair.Key}: {pair.Value}' için panel değeri eşlenemedi; atama yapılmadı.", cancellationToken);
                sortOrder++;
                continue;
            }

            var assignment = db.ProductAttributeAssignments.Local.FirstOrDefault(x => x.TenantId == tenantId && x.ProductId == product.Id && x.VariantId == variant.Id && x.AttributeId == mapped.Definition.Id)
                ?? await db.ProductAttributeAssignments.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ProductId == product.Id && x.VariantId == variant.Id && x.AttributeId == mapped.Definition.Id, cancellationToken);
            if (assignment is null)
            {
                assignment = new ProductAttributeAssignment { Id = Guid.CreateVersion7(), TenantId = tenantId, ProductId = product.Id, VariantId = variant.Id, AttributeId = mapped.Definition.Id, ValueId = valueId, TextValue = textValue, NumberValue = numberValue, BooleanValue = booleanValue, SortOrder = sortOrder, Version = 1 };
                db.ProductAttributeAssignments.Add(assignment);
            }
            else
            {
                assignment.ValueId = valueId;
                assignment.TextValue = textValue;
                assignment.NumberValue = numberValue;
                assignment.BooleanValue = booleanValue;
                assignment.SortOrder = sortOrder;
                assignment.Version++;
            }
            sortOrder++;
        }

        await UpsertDerivedWebColorAttributeAssignment(tenantId, connectionId, product, variant, options, categoryContext, cancellationToken);
    }

    private async Task UpsertDerivedWebColorAttributeAssignment(
        Guid tenantId,
        Guid connectionId,
        Product product,
        ProductVariant variant,
        IReadOnlyDictionary<string, string> options,
        CategoryAttributeContext categoryContext,
        CancellationToken cancellationToken)
    {
        var webColor = categoryContext.Attributes.Values
            .Where(item => IsWebColorOptionKey(item.Remote.Name))
            .GroupBy(item => item.Definition.Id)
            .Select(group => group.First())
            .FirstOrDefault();
        var realColor = options.FirstOrDefault(pair => IsRealColorOptionKey(pair.Key));
        if (webColor is null || string.IsNullOrWhiteSpace(realColor.Key) || string.IsNullOrWhiteSpace(realColor.Value)) return;
        if (!TryGetMappedAttribute(categoryContext.Attributes, realColor.Key, out var colorAttribute)) return;

        var remoteColorValue = colorAttribute.Values.FirstOrDefault(value => NormalizeCatalogKey(value.Name, 320) == NormalizeCatalogKey(realColor.Value, 320));
        var localColorValue = await ResolveMappedAttributeValue(tenantId, connectionId, categoryContext, colorAttribute, remoteColorValue, realColor.Value, cancellationToken);
        if (localColorValue is null)
        {
            await RecordIssue(tenantId, $"product-web-color-value:{product.Id}:{variant.Id}:{NormalizeCatalogKey(realColor.Value, 320)}", "PRODUCT_WEBCOLOR_VALUE_UNMAPPED", $"Pazar yeri Renk değeri '{realColor.Value}' için panel Renk değeri bulunamadı; Web Color atanmadı.", cancellationToken);
            return;
        }

        var webColorExternalId = await ResolveWebColorExternalIdAsync(tenantId, connectionId, categoryContext, webColor, localColorValue, cancellationToken);
        if (string.IsNullOrWhiteSpace(webColorExternalId))
        {
            await RecordIssue(tenantId, $"product-web-color-mapping:{product.Id}:{variant.Id}:{localColorValue.Id}", "PRODUCT_WEBCOLOR_MAPPING_REQUIRED", $"Panel Renk değeri '{localColorValue.Value}' için Web Color eşlemesi bulunamadı; Web Color atanmadı.", cancellationToken);
            return;
        }

        var assignment = db.ProductAttributeAssignments.Local.FirstOrDefault(x => x.TenantId == tenantId && x.ProductId == product.Id && x.VariantId == variant.Id && x.AttributeId == webColor.Definition.Id)
            ?? await db.ProductAttributeAssignments.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ProductId == product.Id && x.VariantId == variant.Id && x.AttributeId == webColor.Definition.Id, cancellationToken);
        if (assignment is null)
        {
            db.ProductAttributeAssignments.Add(new ProductAttributeAssignment
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                ProductId = product.Id,
                VariantId = variant.Id,
                AttributeId = webColor.Definition.Id,
                ValueId = localColorValue.Id,
                SortOrder = webColor.Remote.SortOrder ?? 0,
                Version = 1
            });
        }
        else
        {
            assignment.ValueId = localColorValue.Id;
            assignment.TextValue = null;
            assignment.NumberValue = null;
            assignment.BooleanValue = null;
            assignment.SortOrder = webColor.Remote.SortOrder ?? assignment.SortOrder;
            assignment.Version++;
        }
    }

    private async Task<string?> ResolveWebColorExternalIdAsync(
        Guid tenantId,
        Guid connectionId,
        CategoryAttributeContext categoryContext,
        LocalCategoryAttribute webColor,
        AttributeValue localColorValue,
        CancellationToken cancellationToken)
    {
        var valueScope = $"{categoryContext.ExternalCategoryId}/{webColor.Remote.ExternalId}";
        var mapping = db.AttributeValueMappings.Local.FirstOrDefault(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.LocalId == localColorValue.Id && x.ScopeExternalId == valueScope && x.Status == "VERIFIED")
            ?? await db.AttributeValueMappings.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.LocalId == localColorValue.Id && x.ScopeExternalId == valueScope && x.Status == "VERIFIED", cancellationToken);
        if (mapping is not null && webColor.Values.Any(value => value.ExternalId == mapping.ExternalId)) return mapping.ExternalId;

        var exact = webColor.Values.FirstOrDefault(value => NormalizeCatalogKey(value.Name, 320) == NormalizeCatalogKey(localColorValue.Value, 320));
        if (exact is not null) return exact.ExternalId;
        foreach (var fallbackKey in CatalogColorMappingPolicy.FallbackKeys(localColorValue.Value))
        {
            var fallback = webColor.Values.FirstOrDefault(value => NormalizeCatalogKey(value.Name, 320).Replace("-", "", StringComparison.Ordinal) == fallbackKey);
            if (fallback is not null) return fallback.ExternalId;
        }
        return null;
    }

    private async Task PromoteCommonImportedAttributes(Guid tenantId, Product product, IReadOnlyList<ProductVariant> importedVariants, CategoryAttributeContext categoryContext, CancellationToken cancellationToken)
    {
        var variantIds = importedVariants.Select(x => x.Id).Distinct().ToArray();
        if (variantIds.Length == 0) return;

        var importedAttributeIds = categoryContext.Attributes.Values
            .Where(x => x.Role != "OPTION")
            .Select(x => x.Definition.Id)
            .ToHashSet();
        if (importedAttributeIds.Count == 0) return;

        var trackedAssignments = db.ProductAttributeAssignments.Local
            .Where(x => x.TenantId == tenantId && x.ProductId == product.Id && x.VariantId.HasValue && variantIds.Contains(x.VariantId.Value))
            .ToList();
        var storedAssignments = await db.ProductAttributeAssignments
            .Where(x => x.TenantId == tenantId && x.ProductId == product.Id && x.VariantId.HasValue && variantIds.Contains(x.VariantId.Value))
            .ToListAsync(cancellationToken);
        var assignments = trackedAssignments
            .Concat(storedAssignments)
            .GroupBy(x => x.Id)
            .Select(x => x.First())
            .Where(x => importedAttributeIds.Contains(x.AttributeId))
            .ToList();

        foreach (var attributeId in importedAttributeIds)
        {
            var byVariant = assignments
                .Where(x => x.AttributeId == attributeId && x.VariantId is not null)
                .GroupBy(x => x.VariantId!.Value)
                .ToDictionary(x => x.Key, x => x.ToList());
            var common = byVariant.Count == variantIds.Length && byVariant.Values.All(x => x.Count == 1)
                ? byVariant.Values.Select(x => x[0]).ToList()
                : [];
            var first = common.FirstOrDefault();
            var isCommon = first is not null && common.Skip(1).All(x => SameImportedAttributeValue(first, x));
            var global = db.ProductAttributeAssignments.Local.FirstOrDefault(x => x.TenantId == tenantId && x.ProductId == product.Id && x.VariantId == null && x.AttributeId == attributeId)
                ?? await db.ProductAttributeAssignments.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ProductId == product.Id && x.VariantId == null && x.AttributeId == attributeId, cancellationToken);

            if (!isCommon)
            {
                if (global is not null) db.ProductAttributeAssignments.Remove(global);
                continue;
            }

            var mapped = categoryContext.Attributes.Values.Single(x => x.Definition.Id == attributeId);
            if (global is null)
            {
                global = new ProductAttributeAssignment
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = tenantId,
                    ProductId = product.Id,
                    VariantId = null,
                    AttributeId = attributeId,
                    ValueId = first!.ValueId,
                    TextValue = first.TextValue,
                    NumberValue = first.NumberValue,
                    BooleanValue = first.BooleanValue,
                    SortOrder = mapped.Remote.SortOrder ?? first.SortOrder,
                    Version = 1
                };
                db.ProductAttributeAssignments.Add(global);
            }
            else
            {
                global.ValueId = first!.ValueId;
                global.TextValue = first.TextValue;
                global.NumberValue = first.NumberValue;
                global.BooleanValue = first.BooleanValue;
                global.SortOrder = mapped.Remote.SortOrder ?? first.SortOrder;
                global.Version++;
            }

            db.ProductAttributeAssignments.RemoveRange(common);
        }
    }

    private static bool SameImportedAttributeValue(ProductAttributeAssignment left, ProductAttributeAssignment right) =>
        left.ValueId == right.ValueId
        && string.Equals(left.TextValue, right.TextValue, StringComparison.Ordinal)
        && left.NumberValue == right.NumberValue
        && left.BooleanValue == right.BooleanValue;

    private async Task<AttributeValue?> ResolveMappedAttributeValue(
        Guid tenantId,
        Guid connectionId,
        CategoryAttributeContext categoryContext,
        LocalCategoryAttribute mapped,
        ReferenceItem? remoteValue,
        string remoteValueText,
        CancellationToken cancellationToken)
    {
        var valueScope = $"{categoryContext.ExternalCategoryId}/{mapped.Remote.ExternalId}";
        if (remoteValue is not null)
        {
            var mapping = db.AttributeValueMappings.Local.FirstOrDefault(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ScopeExternalId == valueScope && x.ExternalId == remoteValue.ExternalId && x.Status == "VERIFIED")
                ?? await db.AttributeValueMappings.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ScopeExternalId == valueScope && x.ExternalId == remoteValue.ExternalId && x.Status == "VERIFIED", cancellationToken);
            if (mapping is not null)
            {
                var mappedValue = db.AttributeValues.Local.FirstOrDefault(x => x.TenantId == tenantId && x.Id == mapping.LocalId && x.AttributeId == mapped.Definition.Id && x.IsActive)
                    ?? await db.AttributeValues.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == mapping.LocalId && x.AttributeId == mapped.Definition.Id && x.IsActive, cancellationToken);
                if (mappedValue is not null) return mappedValue;
            }
        }

        var normalized = NormalizeCatalogKey(remoteValueText, 320);
        if (IsWebColorOptionKey(mapped.Remote.Name))
        {
            var localValues = await db.AttributeValues.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.AttributeId == mapped.Definition.Id && x.IsActive)
                .ToListAsync(cancellationToken);
            foreach (var fallbackKey in CatalogColorMappingPolicy.FallbackKeys(remoteValueText))
            {
                var fallback = localValues.FirstOrDefault(value => NormalizeCatalogKey(value.Value, 320).Replace("-", "", StringComparison.Ordinal) == fallbackKey);
                if (fallback is not null) return fallback;
            }
        }
        return db.AttributeValues.Local.FirstOrDefault(x => x.TenantId == tenantId && x.AttributeId == mapped.Definition.Id && x.IsActive && x.NormalizedValue == normalized)
            ?? await db.AttributeValues.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.AttributeId == mapped.Definition.Id && x.IsActive && x.NormalizedValue == normalized, cancellationToken);
    }

    private async Task<bool> MapExistingCatalogProduct(Guid tenantId, Guid connectionId, RemoteCatalogProduct snapshot, bool preferBarcode, CancellationToken cancellationToken)
    {
        var externalProductId = Short(snapshot.ExternalProductId, 256);
        if (string.IsNullOrWhiteSpace(externalProductId)) return false;
        var remoteVariants = snapshot.Variants
            .Where(variant => !string.IsNullOrWhiteSpace(variant.ExternalVariantId))
            .ToList();
        if (remoteVariants.Count == 0) return false;

        var now = timeProvider.GetUtcNow();
        var link = await db.MarketplaceProductLinks.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ExternalId == externalProductId, cancellationToken);
        var modelCodes = !preferBarcode
            ? remoteVariants.Select(variant => variant.ModelCode?.Trim()).Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            : Array.Empty<string>();
        var barcodes = remoteVariants.Select(variant => NormalizeCatalogKey(variant.Barcode, 160)).Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!).Distinct(StringComparer.Ordinal).ToArray();
        var skus = remoteVariants.Select(variant => NormalizeCatalogKey(variant.Sku, 160)).Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!).Distinct(StringComparer.Ordinal).ToArray();
        var rawSkus = remoteVariants.Select(variant => variant.Sku?.Trim()).Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (modelCodes.Length == 0 && barcodes.Length == 0 && skus.Length == 0) return false;

        // A Shopify product can contain several colour variants while the
        // panel stores each colour as its own Product. Load every candidate
        // variant first; requiring one ProductId here incorrectly rejected
        // valid multi-product matches.
        var localVariants = await db.ProductVariants.AsNoTracking()
            .Where(variant => variant.TenantId == tenantId
                && ((variant.BarcodeNormalized != null && barcodes.Contains(variant.BarcodeNormalized))
                    || (variant.SkuNormalized != null && skus.Contains(variant.SkuNormalized))
                    || (preferBarcode && variant.ModelCode != null && rawSkus.Contains(variant.ModelCode))
                    || (!preferBarcode && variant.ModelCode != null && modelCodes.Contains(variant.ModelCode))))
            .ToListAsync(cancellationToken);
        if (localVariants.Count == 0 && link is null) return false;

        var candidateProductIds = localVariants.Select(variant => variant.ProductId).Distinct().ToArray();
        Guid? productId = link?.ProductId;
        if (productId is null)
        {
            var conflictingProductIds = candidateProductIds.Length == 0
                ? []
                : await db.MarketplaceProductLinks.AsNoTracking()
                    .Where(existing => existing.TenantId == tenantId && existing.ConnectionId == connectionId && candidateProductIds.Contains(existing.ProductId) && existing.ExternalId != externalProductId)
                    .Select(existing => existing.ProductId)
                    .Distinct()
                    .ToListAsync(cancellationToken);
            productId = candidateProductIds.FirstOrDefault(candidate => !conflictingProductIds.Contains(candidate));
            if (productId == Guid.Empty) return false;
        }

        if (!await db.Products.AsNoTracking().AnyAsync(product => product.TenantId == tenantId && product.Id == productId.Value, cancellationToken)) return false;
        if (link is null && await db.MarketplaceProductLinks.AsNoTracking().AnyAsync(existing => existing.TenantId == tenantId && existing.ConnectionId == connectionId && existing.ProductId == productId.Value && existing.ExternalId != externalProductId, cancellationToken)) return false;

        var localVariantIds = localVariants.Select(variant => variant.Id).ToArray();
        var externalVariantIds = remoteVariants.Select(variant => Short(variant.ExternalVariantId, 256)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var existingVariantLinks = await db.MarketplaceVariantLinks
            .Where(existing => existing.TenantId == tenantId && existing.ConnectionId == connectionId && (localVariantIds.Contains(existing.VariantId) || externalVariantIds.Contains(existing.ExternalId)))
            .ToListAsync(cancellationToken);
        var claimedVariantIds = existingVariantLinks
            .Where(existing => !externalVariantIds.Contains(existing.ExternalId, StringComparer.OrdinalIgnoreCase))
            .Select(existing => existing.VariantId)
            .ToHashSet();
        var matchedVariantCount = 0;

        foreach (var remote in remoteVariants)
        {
            var externalVariantId = Short(remote.ExternalVariantId, 256);
            var remoteBarcode = NormalizeCatalogKey(remote.Barcode, 160);
            var remoteSku = NormalizeCatalogKey(remote.Sku, 160);
            var remoteModelCode = NormalizeCatalogKey(remote.ModelCode, 160);
            var barcodeMatches = string.IsNullOrWhiteSpace(remoteBarcode)
                ? []
                : localVariants.Where(variant => string.Equals(variant.BarcodeNormalized, remoteBarcode, StringComparison.Ordinal)).ToList();
            var skuMatches = string.IsNullOrWhiteSpace(remoteSku)
                ? []
                : localVariants.Where(variant => string.Equals(variant.SkuNormalized, remoteSku, StringComparison.Ordinal)
                    || preferBarcode && string.Equals(NormalizeCatalogKey(variant.ModelCode, 160), remoteSku, StringComparison.Ordinal)).ToList();
            var modelMatches = !preferBarcode && string.IsNullOrWhiteSpace(remoteModelCode)
                ? []
                : !preferBarcode
                    ? localVariants.Where(variant => string.Equals(NormalizeCatalogKey(variant.ModelCode, 160), remoteModelCode, StringComparison.Ordinal)).ToList()
                    : [];
            var matches = barcodeMatches.Count > 0 ? barcodeMatches : skuMatches.Count > 0 ? skuMatches : modelMatches;
            var candidate = matches.Count == 1 ? matches[0] : null;
            if (candidate is null || claimedVariantIds.Contains(candidate.Id)) continue;

            var externalLink = existingVariantLinks.FirstOrDefault(existing => string.Equals(existing.ExternalId, externalVariantId, StringComparison.OrdinalIgnoreCase));
            if (externalLink is not null)
            {
                if (externalLink.VariantId == candidate.Id) matchedVariantCount++;
                claimedVariantIds.Add(externalLink.VariantId);
                continue;
            }
            var localLink = existingVariantLinks.FirstOrDefault(existing => existing.VariantId == candidate.Id);
            if (localLink is not null) continue;

            db.MarketplaceVariantLinks.Add(new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = connectionId, VariantId = candidate.Id, ExternalId = externalVariantId, Version = 1 });
            existingVariantLinks.Add(new MarketplaceVariantLink { TenantId = tenantId, ConnectionId = connectionId, VariantId = candidate.Id, ExternalId = externalVariantId, Version = 1 });
            claimedVariantIds.Add(candidate.Id);
            matchedVariantCount++;
            telemetryInsertedCount++;
        }

        if (matchedVariantCount == 0) return false;
        if (link is null)
        {
            link = new MarketplaceProductLink
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                ConnectionId = connectionId,
                ProductId = productId.Value,
                ExternalId = externalProductId,
                SyncStatus = "MAPPED",
                LastImportedAt = now,
                Version = 1
            };
            db.MarketplaceProductLinks.Add(link);
            telemetryInsertedCount++;
        }
        else
        {
            link.LastImportedAt = now;
            link.SyncStatus = "MAPPED";
            link.Version++;
        }

        return true;
    }

    private async Task<bool> UpsertCatalogProduct(Guid tenantId, Guid connectionId, RemoteCatalogProduct snapshot, CategoryAttributeContext? categoryContext, Guid? brandReferenceSnapshotId, ConnectionInventoryPolicy? inventoryPolicy, CancellationToken cancellationToken, bool saveChanges = true, bool onlyNewVariants = false, bool observeOnly = false, bool preferBarcode = false, bool onlyExistingVariants = false, bool updateExistingProducts = true, bool isHepsiburada = false)
    {
        var now = timeProvider.GetUtcNow();
        var externalProductId = Short(snapshot.ExternalProductId, 256);
        // Keep the import contract version in the hash. This invalidates older
        // snapshots after changing option-role or brand-mapping interpretation,
        // so catalog records receive the corrected local relationships on the
        // next scan.
        var remoteHash = Hash(JsonSerializer.Serialize(new
        {
            Snapshot = snapshot,
            // Reprocess existing catalog products after changing mapped
            // attribute assignment semantics, not only newly fetched rows.
            OptionRoleVersion = observeOnly
                ? "catalog-options-v10-shopify-variant-first-match"
                : "catalog-options-v9-web-color-adapter"
        }));
        var isNewProduct = false;
        var link = await db.MarketplaceProductLinks.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ExternalId == externalProductId, cancellationToken);
        Product? product = link is null
            ? null
            : await db.Products.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == link.ProductId, cancellationToken);
        if (observeOnly)
        {
            // Shopify has no main-product/model-code identity for this
            // integration. Resolve local products from their variants first,
            // even when an older product link already exists. A Shopify product
            // may contain one model's colours while Ravencia stores each colour
            // as a separate Product row, so existing-only sync may need to link
            // variants across more than one local product.
            var allowCrossProductVariantMatch = preferBarcode && onlyExistingVariants;
            var identityCodes = snapshot.Variants
                .Select(variant => variant.Barcode)
                .Select(value => NormalizeCatalogKey(value, 160))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!)
                .ToHashSet(StringComparer.Ordinal);
            if (identityCodes.Count > 0)
            {
                var identityProductIds = await db.ProductVariants.AsNoTracking()
                    .Where(x => x.TenantId == tenantId
                        && x.BarcodeNormalized != null
                        && identityCodes.Contains(x.BarcodeNormalized))
                    .Select(x => x.ProductId)
                    .Distinct()
                    .Take(2)
                    .ToListAsync(cancellationToken);
                if (identityProductIds.Count > 1)
                {
                    if (!allowCrossProductVariantMatch)
                    {
                        await RecordIssue(tenantId, $"product-sync-barcode-conflict:{connectionId}:{identityCodes.First()}", "PRODUCT_BARCODE_CONFLICT", "Shopify barkodu birden fazla yerel üründe bulundu; otomatik eşleştirme yapılmadı.", cancellationToken);
                        return false;
                    }

                    // Keep the existing product link stable when one exists;
                    // individual variants are resolved globally by barcode
                    // below and can therefore belong to sibling colour rows.
                    var representativeProductId = link?.ProductId ?? identityProductIds[0];
                    product = await db.Products.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == representativeProductId, cancellationToken);
                }
                if (identityProductIds.Count == 1)
                {
                    var variantMatchedProduct = await db.Products.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == identityProductIds[0], cancellationToken);
                    if (variantMatchedProduct is not null && link is not null && link.ProductId != variantMatchedProduct.Id)
                    {
                        await RecordIssue(tenantId, $"product-sync-link-conflict:{connectionId}:{externalProductId}", "PRODUCT_LINK_CONFLICT", "Shopify ürünü aynı bağlantıda başka bir dış ürünle eşleşmiş yerel ürüne bağlanmadı.", cancellationToken);
                        return false;
                    }
                    if (variantMatchedProduct is not null && await db.MarketplaceProductLinks.AnyAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ProductId == variantMatchedProduct.Id && x.ExternalId != externalProductId, cancellationToken))
                    {
                        await RecordIssue(tenantId, $"product-sync-link-conflict:{connectionId}:{externalProductId}", "PRODUCT_LINK_CONFLICT", "Shopify varyant barkodu aynı bağlantıda başka bir dış ürünle eşleşmiş yerel ürüne bağlanmadı.", cancellationToken);
                        return false;
                    }
                    product = variantMatchedProduct;
                }
            }
        }
        if (product is null)
        {
            isNewProduct = true;
            product = new Product
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                Title = ProductTitle(snapshot.Title, externalProductId),
                Description = snapshot.Description ?? "",
                Status = ProductStatus.Active,
                CreatedAt = now,
                UpdatedAt = now,
                Version = 1
            };
            db.Products.Add(product);
            telemetryInsertedCount++;
            if (link is null)
            {
                link = new MarketplaceProductLink { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = connectionId, ProductId = product.Id, ExternalId = externalProductId, LastImportedPayloadHash = remoteHash, SyncStatus = "SYNCED", Version = 1 };
                db.MarketplaceProductLinks.Add(link);
                telemetryInsertedCount++;
            }
        }
        else if (link is null && observeOnly)
        {
            link = new MarketplaceProductLink
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                ConnectionId = connectionId,
                ProductId = product.Id,
                ExternalId = externalProductId,
                LastImportedPayloadHash = remoteHash,
                SyncStatus = "SYNCED",
                Version = 1
            };
            db.MarketplaceProductLinks.Add(link);
            telemetryInsertedCount++;
        }

        var expectedVariantExternalIds = snapshot.Variants
            .Select(variant => Short(variant.ExternalVariantId, 256))
            .Where(externalId => !string.IsNullOrWhiteSpace(externalId))
            .Select(MarketplaceVariantLinkCoverage.Normalize)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var linkedVariantExternalIds = expectedVariantExternalIds.Length == 0
            ? []
            : await (from variantLink in db.MarketplaceVariantLinks.AsNoTracking()
                     join variant in db.ProductVariants.AsNoTracking()
                         on new { variantLink.TenantId, variantLink.VariantId }
                         equals new { variant.TenantId, VariantId = variant.Id }
                     where variantLink.TenantId == tenantId
                         && variantLink.ConnectionId == connectionId
                         && variant.ProductId == product!.Id
                         && expectedVariantExternalIds.Contains(variantLink.ExternalId.Trim().ToUpper())
                     select variantLink.ExternalId.Trim().ToUpper()).ToListAsync(cancellationToken);
        var hasCompleteVariantLinks = MarketplaceVariantLinkCoverage.IsComplete(expectedVariantExternalIds, linkedVariantExternalIds);

        // A catalog scan can revisit hundreds of products whose last imported
        // payload and local projection are already in sync. In that case the
        // inventory/media reconciliation below only repeats database reads;
        // the version marker is sufficient to prove that no local edit is
        // waiting to be preserved and that this exact remote payload was
        // already applied. Keep the legacy reconciliation path for older links
        // that do not have a trustworthy imported-product version yet.
        if (!onlyNewVariants
            && !isNewProduct
            && link is not null
            && string.Equals(link.LastImportedPayloadHash, remoteHash, StringComparison.OrdinalIgnoreCase)
            && link.LastImportedProductVersion == product!.Version
            && string.Equals(link.SyncStatus, "SYNCED", StringComparison.OrdinalIgnoreCase)
            && hasCompleteVariantLinks)
        {
            telemetrySkippedCount++;
            return observeOnly;
        }

        if (!onlyNewVariants && !isNewProduct && link is not null && hasCompleteVariantLinks && string.Equals(link.LastImportedPayloadHash, remoteHash, StringComparison.OrdinalIgnoreCase) && await CatalogSnapshotAlreadyApplied(tenantId, product.Id, snapshot, cancellationToken))
        {
            // A previous import may have stored the remote observation while
            // leaving the untouched local projection at zero. Reconcile that
            // legacy state even when the payload itself has not changed.
            await SyncCatalogInventoryForPreservedProduct(tenantId, connectionId, product, snapshot, inventoryPolicy, now, cancellationToken, observeOnly, preferBarcode);
            if (saveChanges)
                await db.SaveChangesAsync(cancellationToken);
            telemetrySkippedCount++;
            // A Shopify read-only observation is still a successful match even
            // when the local product hash is unchanged. The local catalog is
            // intentionally preserved, but progress must report the linked
            // product as processed rather than silently skipped.
            return observeOnly;
        }

        var preserveDueToLocalChanges = link is not null && (ProductImportMergePolicy.PreserveLocalChanges(product.Version, link.LastImportedProductVersion, link.DirtyFieldsJson) || observeOnly && !isNewProduct);
        var preserveDueToImportOption = link is not null && !updateExistingProducts && !isNewProduct;
        var preserveLocal = preserveDueToLocalChanges || preserveDueToImportOption;
        if (!onlyNewVariants && !preserveLocal)
        {
            product.Title = ProductTitle(snapshot.Title, externalProductId);
            product.Description = snapshot.Description ?? "";
            product.Status = ProductStatus.Active;
            product.ArchivedAt = null;
            product.UpdatedAt = now;
            product.Version++;
            telemetryUpdatedCount++;
        }
        else if (preserveLocal)
        {
            telemetrySkippedCount++;
            if (isHepsiburada)
                await RepairHepsiburadaVariantLinks(tenantId, connectionId, product, snapshot, cancellationToken);
        }

        var brand = observeOnly ? null : await UpsertCatalogBrand(tenantId, snapshot.BrandName, cancellationToken);
        await EnsureImportedBrandMapping(tenantId, connectionId, brand, snapshot.BrandExternalId, brandReferenceSnapshotId, now, cancellationToken);
        var category = observeOnly ? null : categoryContext?.LocalCategory ?? await UpsertCatalogCategory(tenantId, snapshot.CategoryName, cancellationToken);
        if (!preserveLocal && !onlyNewVariants)
        {
            product.BrandId = brand?.Id;
            product.CategoryId = category?.Id;
        }

        var importedNewVariantCount = 0;
        if (!preserveLocal || onlyNewVariants || observeOnly)
        {
            if (!onlyNewVariants)
                await NormalizeLegacyWebColorOptions(tenantId, product.Id, cancellationToken);
            var existingVariantExternalIds = onlyNewVariants
                ? (await db.MarketplaceVariantLinks.AsNoTracking()
                        .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId)
                        .Select(x => x.ExternalId)
                        .ToListAsync(cancellationToken))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase)
                : null;
            var importedVariants = new List<ProductVariant>(snapshot.Variants.Count);
            foreach (var (remote, sortOrder) in snapshot.Variants.Select((remote, index) => (remote, index)))
            {
                var externalVariantId = Short(remote.ExternalVariantId, 256);
                if (onlyNewVariants && existingVariantExternalIds!.Contains(externalVariantId))
                    continue;
                var importedVariant = await UpsertCatalogVariant(tenantId, connectionId, product, remote, sortOrder, categoryContext, inventoryPolicy, now, cancellationToken, observeOnly, preferBarcode, onlyExistingVariants);
                if (importedVariant is not null)
                {
                    importedVariants.Add(importedVariant);
                    if (onlyNewVariants) importedNewVariantCount++;
                    existingVariantExternalIds?.Add(externalVariantId);
                }
            }
            if (categoryContext is not null && importedVariants.Count > 0)
                await PromoteCommonImportedAttributes(tenantId, product, importedVariants, categoryContext, cancellationToken);

            if (!onlyNewVariants)
            {
                var productImageUrls = CatalogImageIdentity.DistinctUrls(
                    snapshot.ImageUrls.Concat(snapshot.Variants.SelectMany(variant => variant.ImageUrls ?? [])));
                await UpsertCatalogMedia(tenantId, product, null, productImageUrls, product.Title, cancellationToken);
            }
        }
        else
        {
            // Local product edits must not block stock observations. Match
            // existing variants without touching their local content/options,
            // then apply the inventory part of the remote snapshot. Trendyol
            // media is a separate remote projection, so a manual product edit
            // must not leave an older Shopify gallery in place.
            await SyncCatalogInventoryForPreservedProduct(tenantId, connectionId, product, snapshot, inventoryPolicy, now, cancellationToken, observeOnly, preferBarcode, syncMedia: !observeOnly && updateExistingProducts);
            if (!observeOnly && updateExistingProducts && snapshot.ImageUrls.Count > 0)
            {
                var productImageUrls = CatalogImageIdentity.DistinctUrls(snapshot.ImageUrls);
                await UpsertCatalogMedia(tenantId, product, null, productImageUrls, product.Title, cancellationToken);
            }
        }
        link ??= await db.MarketplaceProductLinks.SingleAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ExternalId == externalProductId, cancellationToken);
        link.LastImportedPayloadHash = remoteHash;
        link.LastImportedAt = now;
        if (preserveDueToLocalChanges && !onlyNewVariants)
        {
            link.SyncStatus = "LOCAL_CHANGES_PENDING";
            link.DirtyFieldsJson ??= "[\"product\"]";
        }
        else if (!preserveLocal)
        {
            link.LastImportedProductVersion = product.Version;
            link.SyncStatus = "SYNCED";
            link.DirtyFieldsJson = null;
            link.LastError = null;
        }
        link.Version++;
        if (saveChanges)
            await db.SaveChangesAsync(cancellationToken);
        return onlyNewVariants
            ? importedNewVariantCount > 0
            : observeOnly
                ? !isNewProduct
                : !preserveLocal;
    }

    private async Task RepairHepsiburadaVariantLinks(Guid tenantId, Guid connectionId, Product product, RemoteCatalogProduct snapshot, CancellationToken cancellationToken)
    {
        var localVariants = await db.ProductVariants
            .Where(variant => variant.TenantId == tenantId && variant.ProductId == product.Id)
            .ToListAsync(cancellationToken);
        var localVariantIds = localVariants.Select(variant => variant.Id).ToArray();
        var externalIds = snapshot.Variants
            .Select(variant => MarketplaceVariantLinkCoverage.Normalize(Short(variant.ExternalVariantId, 256)))
            .Where(externalId => externalId.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (localVariantIds.Length > 0 || externalIds.Length > 0)
        {
            var existingLinks = await db.MarketplaceVariantLinks
                .Where(link => link.TenantId == tenantId
                    && link.ConnectionId == connectionId
                    && (localVariantIds.Contains(link.VariantId)
                        || externalIds.Contains(link.ExternalId.Trim().ToUpper())))
                .ToListAsync(cancellationToken);
            var linkedExternalIds = existingLinks
                .Select(link => MarketplaceVariantLinkCoverage.Normalize(link.ExternalId))
                .ToHashSet(StringComparer.Ordinal);
            var linkedVariantIds = existingLinks.Select(link => link.VariantId).ToHashSet();

            foreach (var remote in snapshot.Variants)
            {
                var externalId = Short(remote.ExternalVariantId, 256);
                var externalKey = MarketplaceVariantLinkCoverage.Normalize(externalId);
                if (externalKey.Length == 0 || linkedExternalIds.Contains(externalKey)) continue;

                var skuKey = NormalizeCatalogKey(remote.Sku, 160);
                var externalSkuKey = NormalizeCatalogKey(externalId, 160);
                var barcodeKey = NormalizeCatalogKey(remote.Barcode, 160);
                var candidates = localVariants
                    .Where(variant =>
                        skuKey.Length > 0 && string.Equals(variant.SkuNormalized, skuKey, StringComparison.Ordinal)
                        || externalSkuKey.Length > 0 && string.Equals(variant.SkuNormalized, externalSkuKey, StringComparison.Ordinal)
                        || barcodeKey.Length > 0 && string.Equals(variant.BarcodeNormalized, barcodeKey, StringComparison.Ordinal))
                    .Select(variant => variant.Id)
                    .Distinct()
                    .ToArray();
                if (candidates.Length != 1 || linkedVariantIds.Contains(candidates[0])) continue;

                var link = new MarketplaceVariantLink
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = tenantId,
                    ConnectionId = connectionId,
                    VariantId = candidates[0],
                    ExternalId = externalId,
                    Version = 1
                };
                db.MarketplaceVariantLinks.Add(link);
                existingLinks.Add(link);
                linkedExternalIds.Add(externalKey);
                linkedVariantIds.Add(link.VariantId);
                telemetryInsertedCount++;
            }
        }

        var hasUsableMedia = await (from media in db.ProductMedia.AsNoTracking()
                                    join asset in db.FileAssets.AsNoTracking()
                                        on new { media.TenantId, FileAssetId = media.FileAssetId }
                                        equals new { asset.TenantId, FileAssetId = asset.Id }
                                    where media.TenantId == tenantId
                                        && media.ProductId == product.Id
                                        && media.Status == "ACTIVE"
                                        && asset.Status == "ACTIVE"
                                        && (asset.Classification == "PRODUCT_MEDIA_URL" || asset.Classification == "PRODUCT_MEDIA")
                                    select media.Id).AnyAsync(cancellationToken);
        if (!hasUsableMedia)
        {
            var imageUrls = CatalogImageIdentity.DistinctUrls(
                snapshot.ImageUrls.Concat(snapshot.Variants.SelectMany(variant => variant.ImageUrls ?? [])));
            if (imageUrls.Count > 0)
                await UpsertCatalogMedia(tenantId, product, null, imageUrls, product.Title, cancellationToken);
        }
    }

    private async Task SyncCatalogInventoryForPreservedProduct(
        Guid tenantId,
        Guid connectionId,
        Product product,
        RemoteCatalogProduct snapshot,
        ConnectionInventoryPolicy? inventoryPolicy,
        DateTimeOffset now,
        CancellationToken cancellationToken,
        bool observeOnly = false,
        bool preferBarcode = false,
        bool syncMedia = false)
    {
        var variants = await db.ProductVariants
            .Where(x => x.TenantId == tenantId && x.ProductId == product.Id)
            .ToListAsync(cancellationToken);
        if (variants.Count == 0) return;

        var externalIds = snapshot.Variants
            .Select(x => Short(x.ExternalVariantId, 256))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var links = externalIds.Length == 0
            ? new List<MarketplaceVariantLink>()
            : await db.MarketplaceVariantLinks
                .AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && externalIds.Contains(x.ExternalId))
                .ToListAsync(cancellationToken);

        foreach (var remote in snapshot.Variants)
        {
            var externalId = Short(remote.ExternalVariantId, 256);
            var linkedVariantId = links.FirstOrDefault(x => string.Equals(x.ExternalId, externalId, StringComparison.Ordinal))?.VariantId;
            var sku = Short(string.IsNullOrWhiteSpace(remote.Sku) ? remote.Barcode ?? remote.ExternalVariantId : remote.Sku, 160);
            var skuNormalized = NormalizeCatalogKey(sku, 160);
            var barcodeNormalized = NormalizeCatalogKey(remote.Barcode, 160);
            var variant = linkedVariantId is Guid variantId
                ? variants.FirstOrDefault(x => x.Id == variantId)
                : null;
            if (preferBarcode)
            {
                if (!string.IsNullOrWhiteSpace(barcodeNormalized))
                    variant ??= variants.FirstOrDefault(x => x.BarcodeNormalized == barcodeNormalized);
            }
            else
            {
                variant ??= variants.FirstOrDefault(x => x.SkuNormalized == skuNormalized);
                if (variant is null && !string.IsNullOrWhiteSpace(barcodeNormalized))
                    variant = variants.FirstOrDefault(x => x.BarcodeNormalized == barcodeNormalized);
            }
            if (variant is null) continue;

            await UpsertCatalogOfferAndInventory(tenantId, connectionId, variant, remote, inventoryPolicy, now, cancellationToken, updateOffer: false, observeOnly: observeOnly);
            if (syncMedia && remote.ImageUrls is not null)
                await UpsertCatalogMedia(tenantId, product, variant.Id, remote.ImageUrls, $"{product.Title} · {OptionSignature(remote.Options)}", cancellationToken);
        }
    }

    private async Task NormalizeLegacyWebColorOptions(Guid tenantId, Guid productId, CancellationToken cancellationToken)
    {
        var options = await db.ProductOptions
            .Where(x => x.TenantId == tenantId && x.ProductId == productId)
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);
        var legacy = options.Where(x => IsWebColorOptionKey(x.Label) || IsWebColorOptionKey(x.NormalizedKey)).ToList();
        if (legacy.Count == 0) return;

        var canonical = options.FirstOrDefault(x => IsColorOptionKey(x.Label) && !IsWebColorOptionKey(x.Label))
            ?? options.FirstOrDefault(x => string.Equals(x.NormalizedKey, NormalizeCatalogKey("Renk", 160), StringComparison.Ordinal));
        if (canonical is null)
        {
            canonical = legacy[0];
            canonical.Label = "Renk";
            canonical.NormalizedKey = NormalizeCatalogKey("Renk", 160);
            legacy.RemoveAt(0);
        }

        foreach (var duplicate in legacy)
        {
            var assignments = await db.VariantOptionValues
                .Where(x => x.TenantId == tenantId && x.OptionId == duplicate.Id)
                .ToListAsync(cancellationToken);
            db.VariantOptionValues.RemoveRange(assignments);
            var values = await db.ProductOptionValues
                .Where(x => x.TenantId == tenantId && x.OptionId == duplicate.Id)
                .ToListAsync(cancellationToken);
            db.ProductOptionValues.RemoveRange(values);
            db.ProductOptions.Remove(duplicate);
        }
    }

    private async Task<bool> CatalogSnapshotAlreadyApplied(Guid tenantId, Guid productId, RemoteCatalogProduct snapshot, CancellationToken cancellationToken)
    {
        var product = await db.Products.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == productId, cancellationToken);
        if (product is null || !string.Equals(product.Title, ProductTitle(snapshot.Title, snapshot.ExternalProductId), StringComparison.Ordinal) || !string.Equals(product.Description, snapshot.Description ?? "", StringComparison.Ordinal)) return false;
        var variantCount = await db.ProductVariants.AsNoTracking().CountAsync(x => x.TenantId == tenantId && x.ProductId == productId, cancellationToken);
        if (variantCount < snapshot.Variants.Count) return false;

        var expectedUrls = CatalogImageIdentity.DistinctUrls(snapshot.ImageUrls);
        var mediaRows = await (from media in db.ProductMedia.AsNoTracking()
                               join asset in db.FileAssets.AsNoTracking()
                                   on new { media.TenantId, media.FileAssetId } equals new { asset.TenantId, FileAssetId = asset.Id }
                               where media.TenantId == tenantId
                                   && media.ProductId == productId
                                   && media.VariantId == null
                                   && media.Status == "ACTIVE"
                                   && asset.Status == "ACTIVE"
                                   && asset.ArchivedAt == null
                                   && (asset.Classification == "PRODUCT_MEDIA_URL" || asset.Classification == "PRODUCT_MEDIA")
                               orderby media.SortOrder
                               select new { media.FileAssetId, asset.Classification, asset.RelativePath }).ToListAsync(cancellationToken);
        var actualUrls = mediaRows
            .Select(row => row.Classification == "PRODUCT_MEDIA_URL"
                ? row.RelativePath
                : $"/api/v1/files/product-media/{row.FileAssetId:D}/content")
            .ToList();
        return actualUrls.SequenceEqual(expectedUrls, StringComparer.OrdinalIgnoreCase);
    }

    private async Task<Brand?> UpsertCatalogBrand(Guid tenantId, string? name, CancellationToken cancellationToken)
    {
        var normalized = NormalizeCatalogKey(name, 160);
        if (string.IsNullOrWhiteSpace(normalized)) return null;
        var brand = db.Brands.Local.FirstOrDefault(x => x.TenantId == tenantId && x.NormalizedName == normalized)
            ?? await db.Brands.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.NormalizedName == normalized, cancellationToken);
        if (brand is not null)
        {
            var nextName = Short(name!.Trim(), 160);
            if (brand.Name != nextName || !brand.IsActive)
            {
                brand.Name = nextName; brand.IsActive = true; brand.UpdatedAt = timeProvider.GetUtcNow(); brand.Version++;
            }
            return brand;
        }
        brand = new Brand { Id = Guid.CreateVersion7(), TenantId = tenantId, Name = Short(name!.Trim(), 160), NormalizedName = normalized, IsActive = true, CreatedAt = timeProvider.GetUtcNow(), UpdatedAt = timeProvider.GetUtcNow(), Version = 1 };
        db.Brands.Add(brand);
        return brand;
    }

    private async Task EnsureImportedBrandMapping(
        Guid tenantId,
        Guid connectionId,
        Brand? brand,
        string? externalBrandId,
        Guid? brandReferenceSnapshotId,
        DateTimeOffset verifiedAt,
        CancellationToken cancellationToken)
    {
        if (brand is null || brandReferenceSnapshotId is null || string.IsNullOrWhiteSpace(externalBrandId)) return;

        var externalId = Short(externalBrandId.Trim(), 256);
        var referenceExists = await db.ReferenceItems.AsNoTracking().AnyAsync(
            x => x.TenantId == tenantId
                && x.ConnectionId == connectionId
                && x.SnapshotId == brandReferenceSnapshotId.Value
                && x.ResourceType == "BRANDS"
                && x.ExternalId == externalId
                && x.IsActive,
            cancellationToken);
        if (!referenceExists) return;

        var mapping = db.BrandMappings.Local.FirstOrDefault(x =>
                x.TenantId == tenantId
                && x.ConnectionId == connectionId
                && x.LocalId == brand.Id
                && x.ScopeExternalId == "")
            ?? await db.BrandMappings.SingleOrDefaultAsync(x =>
                x.TenantId == tenantId
                && x.ConnectionId == connectionId
                && x.LocalId == brand.Id
                && x.ScopeExternalId == "",
                cancellationToken);

        if (mapping is null)
        {
            db.BrandMappings.Add(new BrandMapping
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                ConnectionId = connectionId,
                SnapshotId = brandReferenceSnapshotId.Value,
                LocalId = brand.Id,
                ScopeExternalId = "",
                ExternalId = externalId,
                Status = "VERIFIED",
                VerifiedAt = verifiedAt,
                Version = 1
            });
            return;
        }

        if (mapping.SnapshotId == brandReferenceSnapshotId.Value
            && string.Equals(mapping.ExternalId, externalId, StringComparison.Ordinal)
            && string.Equals(mapping.Status, "VERIFIED", StringComparison.Ordinal))
            return;

        mapping.SnapshotId = brandReferenceSnapshotId.Value;
        mapping.ExternalId = externalId;
        mapping.Status = "VERIFIED";
        mapping.VerifiedAt = verifiedAt;
        mapping.Version++;
    }

    private async Task<Category?> UpsertCatalogCategory(Guid tenantId, string? name, CancellationToken cancellationToken)
    {
        var leafName = CategoryLeafName(name, name);
        var normalized = NormalizeCatalogKey(leafName, 160);
        if (string.IsNullOrWhiteSpace(normalized)) return null;
        var legacyNormalized = NormalizeCatalogKey(name, 160);
        var category = db.Categories.Local.FirstOrDefault(x => x.TenantId == tenantId && x.ParentId == null && (x.NormalizedName == normalized || x.NormalizedName == legacyNormalized))
            ?? await db.Categories.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ParentId == null && (x.NormalizedName == normalized || x.NormalizedName == legacyNormalized), cancellationToken);
        if (category is not null)
        {
            if (category.Name != leafName || category.NormalizedName != normalized || category.Path != leafName || !category.IsActive || !category.IsLeaf)
            {
                category.Name = leafName; category.NormalizedName = normalized; category.Path = leafName; category.IsActive = true; category.IsLeaf = true; category.UpdatedAt = timeProvider.GetUtcNow(); category.Version++;
            }
            return category;
        }
        category = new Category { Id = Guid.CreateVersion7(), TenantId = tenantId, Name = leafName, NormalizedName = normalized, Path = leafName, Depth = 0, IsLeaf = true, IsActive = true, CreatedAt = timeProvider.GetUtcNow(), UpdatedAt = timeProvider.GetUtcNow(), Version = 1 };
        db.Categories.Add(category);
        return category;
    }

    private async Task<ProductVariant?> UpsertCatalogVariant(Guid tenantId, Guid connectionId, Product product, RemoteCatalogVariant remote, int sortOrder, CategoryAttributeContext? categoryContext, ConnectionInventoryPolicy? inventoryPolicy, DateTimeOffset now, CancellationToken cancellationToken, bool observeOnly = false, bool preferBarcode = false, bool onlyExistingVariants = false)
    {
        var sku = Short(string.IsNullOrWhiteSpace(remote.Sku) ? remote.Barcode ?? remote.ExternalVariantId : remote.Sku, 160);
        var skuNormalized = NormalizeCatalogKey(sku, 160);
        if (string.IsNullOrWhiteSpace(skuNormalized)) return null;
        var barcode = string.IsNullOrWhiteSpace(remote.Barcode) ? null : Short(remote.Barcode.Trim(), 160);
        var barcodeNormalized = NormalizeCatalogKey(barcode, 160);
        var externalVariantId = Short(remote.ExternalVariantId, 256);
        var optionSignature = categoryContext is null ? OptionSignature(remote.Options) : await PanelOptionSignatureAsync(tenantId, connectionId, categoryContext, remote.Options, cancellationToken);
        var link = await db.MarketplaceVariantLinks.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ExternalId == externalVariantId, cancellationToken);
        ProductVariant? variant = null;
        if (link is not null)
        {
            var linkedVariant = await db.ProductVariants.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == link.VariantId, cancellationToken);
            // A stale Shopify variant link must not override a barcode match
            // inside the resolved local parent product.
            if (linkedVariant is not null
                && linkedVariant.ProductId == product.Id
                && (!preferBarcode || string.Equals(linkedVariant.BarcodeNormalized, barcodeNormalized, StringComparison.Ordinal)))
                variant = linkedVariant;
        }
        if (preferBarcode && !string.IsNullOrWhiteSpace(barcodeNormalized))
            variant ??= db.ProductVariants.Local.FirstOrDefault(x => x.TenantId == tenantId && x.ProductId == product.Id && x.BarcodeNormalized == barcodeNormalized)
                ?? await db.ProductVariants.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ProductId == product.Id && x.BarcodeNormalized == barcodeNormalized, cancellationToken);
        if (preferBarcode && onlyExistingVariants && variant is null && !string.IsNullOrWhiteSpace(barcodeNormalized))
            variant = db.ProductVariants.Local.FirstOrDefault(x => x.TenantId == tenantId && x.BarcodeNormalized == barcodeNormalized)
                ?? await db.ProductVariants.Where(x => x.TenantId == tenantId && x.BarcodeNormalized == barcodeNormalized).OrderBy(x => x.Id).FirstOrDefaultAsync(cancellationToken);
        if (!preferBarcode)
            variant ??= db.ProductVariants.Local.FirstOrDefault(x => x.TenantId == tenantId && x.ProductId == product.Id && x.SkuNormalized == skuNormalized)
                ?? await db.ProductVariants.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.SkuNormalized == skuNormalized, cancellationToken);
        if (variant is not null && variant.ProductId != product.Id && !(preferBarcode && onlyExistingVariants))
        {
            await RecordIssue(tenantId, $"product-sync-variant-conflict:{connectionId}:{externalVariantId}", "PRODUCT_VARIANT_CONFLICT", "Pazar yeri varyantı başka bir yerel üründe kullanılan stok koduyla eşleşti; mevcut kayıt korunarak atlandı.", cancellationToken);
            return null;
        }
        if (!preferBarcode && variant is null && !string.IsNullOrWhiteSpace(barcodeNormalized))
            variant = await db.ProductVariants.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ProductId == product.Id && x.BarcodeNormalized == barcodeNormalized, cancellationToken);
        if (variant is null && onlyExistingVariants)
            return null;

        var isNewVariant = variant is null;
        if (variant is null)
        {
            variant = new ProductVariant { Id = Guid.CreateVersion7(), TenantId = tenantId, ProductId = product.Id, SortOrder = sortOrder, Sku = sku, SkuNormalized = skuNormalized, Barcode = barcode, BarcodeNormalized = barcodeNormalized, ModelCode = Short(remote.ModelCode, 160), OptionSignature = optionSignature, Status = remote.Archived ? ProductStatus.Archived : ProductStatus.Active, CreatedAt = now, UpdatedAt = now, Version = 1 };
            db.ProductVariants.Add(variant);
            telemetryInsertedCount++;
        }
        else
        {
            var nextModelCode = preferBarcode ? null : Short(remote.ModelCode, 160);
            var nextStatus = remote.Archived ? ProductStatus.Archived : ProductStatus.Active;
            if (!observeOnly && (variant.SortOrder != sortOrder || variant.Sku != sku || variant.SkuNormalized != skuNormalized || variant.Barcode != barcode || variant.BarcodeNormalized != barcodeNormalized || variant.ModelCode != nextModelCode || variant.OptionSignature != optionSignature || variant.Status != nextStatus))
            {
                variant.SortOrder = sortOrder; variant.Sku = sku; variant.SkuNormalized = skuNormalized; variant.Barcode = barcode; variant.BarcodeNormalized = barcodeNormalized; variant.ModelCode = nextModelCode; variant.OptionSignature = optionSignature; variant.Status = nextStatus; variant.UpdatedAt = now; variant.Version++;
                telemetryUpdatedCount++;
            }
        }
        if (link is not null && link.VariantId != variant.Id)
        {
            var conflictingVariantLink = db.MarketplaceVariantLinks.Local.FirstOrDefault(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.VariantId == variant.Id && x.Id != link.Id)
                ?? await db.MarketplaceVariantLinks.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.VariantId == variant.Id && x.Id != link.Id, cancellationToken);
            if (conflictingVariantLink is not null)
            {
                await RecordIssue(tenantId, $"product-sync-variant-link-conflict:{connectionId}:{externalVariantId}", "PRODUCT_VARIANT_LINK_CONFLICT", "Aynı yerel varyantın başka bir Shopify varyant linki zaten var; ikinci link güvenli biçimde atlandı.", cancellationToken);
                return null;
            }
            link.VariantId = variant.Id;
            link.Version++;
        }
        if (link is null)
        {
            var existingVariantLink = db.MarketplaceVariantLinks.Local.FirstOrDefault(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.VariantId == variant.Id)
                ?? await db.MarketplaceVariantLinks.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.VariantId == variant.Id, cancellationToken);
            if (existingVariantLink is null)
            {
                db.MarketplaceVariantLinks.Add(new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = connectionId, VariantId = variant.Id, ExternalId = externalVariantId, Version = 1 });
                telemetryInsertedCount++;
            }
            else if (!string.Equals(existingVariantLink.ExternalId, externalVariantId, StringComparison.Ordinal))
            {
            await RecordIssue(tenantId, $"product-sync-variant-link-conflict:{connectionId}:{externalVariantId}", "PRODUCT_VARIANT_LINK_CONFLICT", "Aynı yerel varyantın başka bir pazar yeri varyant bağlantısı zaten var; ikinci bağlantı güvenli biçimde atlandı.", cancellationToken);
                return null;
            }
        }
        if (!observeOnly || isNewVariant)
            await UpsertCatalogOptions(tenantId, connectionId, product.Id, variant.Id, remote.Options, categoryContext, cancellationToken);
        if ((!observeOnly || isNewVariant) && categoryContext is not null)
            await UpsertProductAttributeAssignments(tenantId, connectionId, product, variant, remote.Options, categoryContext, cancellationToken);
        await UpsertCatalogOfferAndInventory(tenantId, connectionId, variant, remote, inventoryPolicy, now, cancellationToken, observeOnly: observeOnly);
        if ((!observeOnly || isNewVariant) && remote.ImageUrls is not null)
            await UpsertCatalogMedia(tenantId, product, variant.Id, remote.ImageUrls, $"{product.Title} · {optionSignature}", cancellationToken);
        return variant;
    }

    private static RemoteCatalogProduct MergeCatalogSnapshots(IEnumerable<RemoteCatalogProduct> source)
    {
        var snapshots = source.ToList();
        var first = snapshots[0];
        var variants = snapshots
            .SelectMany(snapshot => snapshot.Variants)
            .GroupBy(VariantMergeKey, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var entries = group.ToList();
                var last = entries[^1];
                var hasImagePayload = entries.Any(entry => entry.ImageUrls is not null);
                var imageUrls = CatalogImageIdentity.DistinctUrls(entries
                    .Where(entry => entry.ImageUrls is not null)
                    .SelectMany(entry => entry.ImageUrls!));
                return last with { ImageUrls = hasImagePayload ? imageUrls : null };
            })
            .ToList();
        var images = CatalogImageIdentity.DistinctUrls(
            snapshots.SelectMany(snapshot => snapshot.ImageUrls.Concat(snapshot.Variants.SelectMany(variant => variant.ImageUrls ?? []))));
        return first with
        {
            ProductMainId = snapshots.Select(x => x.ProductMainId).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)),
            Title = snapshots.Select(x => x.Title).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? first.Title,
            Description = snapshots.Select(x => x.Description).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? first.Description,
            BrandExternalId = snapshots.Select(x => x.BrandExternalId).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)),
            BrandName = snapshots.Select(x => x.BrandName).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)),
            CategoryExternalId = snapshots.Select(x => x.CategoryExternalId).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)),
            CategoryName = snapshots.Select(x => x.CategoryName).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)),
            ImageUrls = images,
            Variants = variants,
            RawJson = snapshots[^1].RawJson
        };
    }

    private static string VariantMergeKey(RemoteCatalogVariant variant) =>
        !string.IsNullOrWhiteSpace(variant.ExternalVariantId)
            ? $"id:{variant.ExternalVariantId}"
            : $"sku:{NormalizeCatalogKey(variant.Sku, 160)}";

    private async Task UpsertCatalogOfferAndInventory(Guid tenantId, Guid connectionId, ProductVariant variant, RemoteCatalogVariant remote, ConnectionInventoryPolicy? inventoryPolicy, DateTimeOffset now, CancellationToken cancellationToken, bool updateOffer = true, bool observeOnly = false)
    {
        if (observeOnly)
        {
            await UpsertShopifyInventoryObservations(tenantId, connectionId, variant.Id, remote, now, cancellationToken);
        }
        else if (remote.StockQuantity is decimal stockQuantity)
        {
            var observedRemoteQuantity = decimal.Round(Math.Max(0m, stockQuantity), 4, MidpointRounding.ToEven);
            var applyRemoteQuantityToOnHand = InventoryAuthorityPolicy.ShouldApplyRemoteQuantityToOnHand(inventoryPolicy?.AuthorityMode);
            var inventory = db.InventoryItems.Local.FirstOrDefault(x => x.TenantId == tenantId && x.VariantId == variant.Id && x.LocationCode == "MAIN")
                ?? await db.InventoryItems.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.VariantId == variant.Id && x.LocationCode == "MAIN", cancellationToken);
            if (inventory is null)
            {
                db.InventoryItems.Add(new InventoryItem
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = tenantId,
                    VariantId = variant.Id,
                    LocationCode = "MAIN",
                    // The first catalog snapshot initializes an untouched
                    // inventory row. Subsequent writes still require the
                    // explicit remote-authoritative mode.
                    OnHand = observeOnly ? 0 : observedRemoteQuantity,
                    Reserved = 0,
                    Available = observeOnly ? 0 : observedRemoteQuantity,
                    ObservedRemoteQuantity = observedRemoteQuantity,
                    ObservedRemoteAt = now,
                    ReconciledAt = now,
                    ProjectionVersion = 1,
                    Version = 1
                });
            }
            else
            {
                var reconciliationWasMissing = inventory.ReconciledAt is null;
                var seedInitialOnHand = InventoryAuthorityPolicy.ShouldSeedInitialOnHand(inventory);
                var applyQuantityToOnHand = !observeOnly && (applyRemoteQuantityToOnHand || seedInitialOnHand);
                var projectionChanged = applyQuantityToOnHand && inventory.OnHand != observedRemoteQuantity;
                var observationChanged = inventory.ObservedRemoteQuantity != observedRemoteQuantity;
                if (projectionChanged)
                {
                    inventory.OnHand = observedRemoteQuantity;
                    inventory.Available = InventoryProjection.Available(inventory.OnHand, inventory.Reserved);
                    inventory.ProjectionVersion++;
                }
                inventory.ObservedRemoteQuantity = observedRemoteQuantity;
                inventory.ObservedRemoteAt = now;
                inventory.ReconciledAt = now;
                if (projectionChanged || observationChanged || reconciliationWasMissing)
                    inventory.Version++;
            }
        }

        if (!updateOffer) return;
        if (remote.SalePrice is null && remote.ListPrice is null) return;
        var salePrice = decimal.Round(Math.Max(0m, remote.SalePrice ?? remote.ListPrice ?? 0m), 4, MidpointRounding.ToEven);
        var listPrice = decimal.Round(Math.Max(salePrice, Math.Max(0m, remote.ListPrice ?? salePrice)), 4, MidpointRounding.ToEven);
        var currency = NormalizeCurrency(remote.Currency);
        var vatRate = decimal.Round(Math.Max(0m, remote.VatRate ?? 0m), 4, MidpointRounding.ToEven);
        var status = remote.Archived ? "INACTIVE" : "ACTIVE";
        var offer = db.ChannelOffers.Local.FirstOrDefault(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.VariantId == variant.Id)
            ?? await db.ChannelOffers.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.VariantId == variant.Id, cancellationToken);
        var observationSource = observeOnly ? "SHOPIFY" : "TRENDYOL";
        if (offer is null)
        {
            offer = new ChannelOffer
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                ConnectionId = connectionId,
                VariantId = variant.Id,
                ListPrice = listPrice,
                SalePrice = salePrice,
                Currency = currency,
                VatRate = vatRate,
                VatInclusion = "INCLUDED",
                RoundingMode = "HALF_EVEN",
                SafetyStock = decimal.Round(Math.Max(0m, inventoryPolicy?.DefaultSafetyStock ?? 0m), 4, MidpointRounding.ToEven),
                Status = status,
                PriceVersion = 1,
                Version = 1
            };
            db.ChannelOffers.Add(offer);
            db.ChannelPriceHistory.Add(new ChannelPriceHistory
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                OfferId = offer.Id,
                PriceVersion = offer.PriceVersion,
                ListPrice = offer.ListPrice,
                SalePrice = offer.SalePrice,
                Currency = offer.Currency,
                Reason = $"{observationSource}_CATALOG_IMPORT",
                ActorSource = $"SYSTEM:{observationSource}",
                EffectiveAt = now
            });
            return;
        }

        var priceChanged = offer.ListPrice != listPrice || offer.SalePrice != salePrice || !string.Equals(offer.Currency, currency, StringComparison.OrdinalIgnoreCase) || offer.VatRate != vatRate;
        var statusChanged = offer.Status != status;
        if (!priceChanged && !statusChanged) return;
        offer.ListPrice = listPrice;
        offer.SalePrice = salePrice;
        offer.Currency = currency;
        offer.VatRate = vatRate;
        offer.Status = status;
        offer.Version++;
        if (priceChanged)
        {
            offer.PriceVersion++;
            db.ChannelPriceHistory.Add(new ChannelPriceHistory
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                OfferId = offer.Id,
                PriceVersion = offer.PriceVersion,
                ListPrice = offer.ListPrice,
                SalePrice = offer.SalePrice,
                Currency = offer.Currency,
                Reason = $"{observationSource}_CATALOG_IMPORT",
                ActorSource = $"SYSTEM:{observationSource}",
                EffectiveAt = now
            });
        }
    }

    private async Task UpsertShopifyInventoryObservations(Guid tenantId, Guid connectionId, Guid variantId, RemoteCatalogVariant remote, DateTimeOffset observedAt, CancellationToken cancellationToken)
    {
        var levels = remote.InventoryLevels is { Count: > 0 }
            ? remote.InventoryLevels
            : remote.StockQuantity is decimal aggregate
                ? [new RemoteInventoryLevel("__aggregate__", null, aggregate, remote.RawJson)]
                : [];
        if (levels.Count == 0) return;

        var mappings = await db.ConnectionLocationMappings.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.Status == "ACTIVE")
            .ToListAsync(cancellationToken);

        foreach (var level in levels)
        {
            var externalLocationId = Short(level.ExternalLocationId, 256);
            var mapping = mappings.FirstOrDefault(x => string.Equals(Short(x.ExternalLocationId, 256), externalLocationId, StringComparison.OrdinalIgnoreCase));
            var quantity = decimal.Round(Math.Max(0m, level.Quantity), 4, MidpointRounding.ToEven);
            var observation = db.ChannelInventoryObservations.Local.FirstOrDefault(x =>
                    x.TenantId == tenantId
                    && x.ConnectionId == connectionId
                    && x.VariantId == variantId
                    && string.Equals(x.ExternalLocationId, externalLocationId, StringComparison.Ordinal))
                ?? await db.ChannelInventoryObservations.SingleOrDefaultAsync(x =>
                    x.TenantId == tenantId
                    && x.ConnectionId == connectionId
                    && x.VariantId == variantId
                    && x.ExternalLocationId == externalLocationId,
                    cancellationToken);

            if (observation is null)
            {
                db.ChannelInventoryObservations.Add(new ChannelInventoryObservation
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = tenantId,
                    ConnectionId = connectionId,
                    VariantId = variantId,
                    LocationId = mapping?.LocationId,
                    ExternalLocationId = externalLocationId,
                    Quantity = quantity,
                    ObservedAt = observedAt,
                    Version = 1
                });
                continue;
            }

            if (observation.LocationId != mapping?.LocationId || observation.Quantity != quantity || observation.ObservedAt != observedAt)
            {
                observation.LocationId = mapping?.LocationId;
                observation.Quantity = quantity;
                observation.ObservedAt = observedAt;
                observation.Version++;
            }
        }
    }

    private static string NormalizeCurrency(string? value)
    {
        var currency = value?.Trim().ToUpperInvariant();
        return currency is { Length: 3 } && currency.All(character => character is >= 'A' and <= 'Z') ? currency : "TRY";
    }

    private async Task<string> PanelOptionSignatureAsync(Guid tenantId, Guid connectionId, CategoryAttributeContext categoryContext, IReadOnlyDictionary<string, string> options, CancellationToken cancellationToken)
    {
        var hasRealColorSource = options.Keys.Any(IsRealColorOptionKey);
        var candidates = new List<(string PanelLabel, bool IsWebColorSource, LocalCategoryAttribute? Mapped, string RemoteValue, int Order)>();
        var order = 0;
        foreach (var pair in options)
        {
            // Web Color is a category attribute derived from the real Renk
            // slicer. It must never create a second product option axis.
            if (IsWebColorOptionKey(pair.Key))
            {
                order++;
                continue;
            }
            if (TryGetMappedAttribute(categoryContext.Attributes, pair.Key, out var mapped) && IsCatalogProductOption(mapped, pair.Key))
            {
                var panelLabel = mapped.Definition.Name;
                var isWebColorSource = IsWebColorOptionKey(pair.Key) || IsWebColorOptionKey(mapped.Remote.Name);
                if (isWebColorSource && hasRealColorSource)
                {
                    order++;
                    continue;
                }
                candidates.Add((panelLabel, isWebColorSource, mapped, pair.Value, order));
            }
            else if (IsVariantOptionName(pair.Key) && !IsWebColorOptionKey(pair.Key))
            {
                // A missing Renk mapping must not make the importer fall back
                // to Web Color. Preserve the real slicer value under the
                // canonical local option name until it is mapped explicitly.
                candidates.Add((IsColorOptionKey(pair.Key) ? "Renk" : pair.Key, false, null, pair.Value, order));
            }
            order++;
        }

        var panelOptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in candidates.GroupBy(candidate => NormalizeCatalogKey(candidate.PanelLabel, 160), StringComparer.Ordinal))
        {
            // Trendyol can expose both the slicer value (Renk: Tavşanlı) and
            // the marketplace display value (Web Color: Çok Renkli). They
            // map to the same panel option, so the real Renk value must win
            // regardless of the source dictionary order.
            var selected = group
                .OrderBy(candidate => candidate.IsWebColorSource ? 1 : 0)
                .ThenBy(candidate => candidate.Order)
                .First();
            panelOptions[selected.PanelLabel] = selected.Mapped is null
                ? CleanCatalogOptionValue(selected.RemoteValue)
                : await PanelOptionValueAsync(tenantId, connectionId, categoryContext, selected.Mapped, selected.RemoteValue, cancellationToken);
        }
        if (panelOptions.Count > 0) return OptionSignature(panelOptions);
        var fallbackOptions = options.Where(pair => IsVariantOptionName(pair.Key) && !IsWebColorOptionKey(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        return OptionSignature(fallbackOptions);
    }

    private static bool IsWebColorOptionKey(string value)
    {
        var normalized = NormalizeCatalogKey(value, 320).Replace("-", "", StringComparison.Ordinal);
        return normalized is "WEBCOLOR" or "WEBCOLOUR" or "WEBRENK";
    }

    private static string? VariantOptionAxis(string value)
    {
        if (IsColorOptionKey(value)) return "COLOR";
        if (IsSizeOptionKey(value)) return "SIZE";
        return null;
    }

    private static string? ObservedVariantValueAxis(string value) => VariantOptionAxis(value);

    private static bool IsColorOptionKey(string value)
    {
        var normalized = NormalizeCatalogKey(value, 320).Replace(" ", "", StringComparison.Ordinal);
        return normalized is "RENK" or "COLOR" or "COLOUR";
    }

    private static bool IsRealColorOptionKey(string value) => IsColorOptionKey(value) && !IsWebColorOptionKey(value);

    private static bool IsVariantOptionName(string value) => VariantOptionAxis(value) is not null;

    private static bool IsCatalogProductOption(LocalCategoryAttribute mapped, string remoteName) =>
        mapped.Role == "OPTION" || IsVariantOptionName(remoteName);

    private static bool TryGetMappedAttribute(IReadOnlyDictionary<string, LocalCategoryAttribute> attributes, string remoteName, out LocalCategoryAttribute mapped)
    {
        if (attributes.TryGetValue(NormalizeCatalogKey(remoteName, 320), out var direct)
            && (IsWebColorOptionKey(remoteName) || !IsWebColorOptionKey(direct.Remote.Name)))
        {
            mapped = direct;
            return true;
        }

        var axis = VariantOptionAxis(remoteName);
        if (axis is null)
        {
            mapped = null!;
            return false;
        }

        var wantsWebColor = IsWebColorOptionKey(remoteName);
        mapped = attributes.Values
            .Where(item => VariantOptionAxis(item.Remote.Name) == axis || VariantOptionAxis(item.Definition.Name) == axis)
            .OrderByDescending(item => IsWebColorOptionKey(item.Remote.Name) == wantsWebColor)
            .ThenByDescending(item => item.Role == "OPTION")
            .FirstOrDefault()!;
        // If the feed contains the real Renk field but only Web Color is
        // mapped locally, do not silently replace the real value with the
        // marketplace presentation value. The caller can preserve the raw
        // Renk value instead.
        if (mapped is not null && !wantsWebColor && IsWebColorOptionKey(mapped.Remote.Name))
        {
            mapped = null!;
            return false;
        }
        return mapped is not null;
    }

    private static bool IsSizeOptionKey(string value)
    {
        var normalized = NormalizeCatalogKey(value, 320).Replace(" ", "", StringComparison.Ordinal);
        return normalized is "BEDEN" or "SIZE" or "SIZ" or "NUMARA" or "NUMBER";
    }

    private async Task<string> PanelOptionValueAsync(Guid tenantId, Guid connectionId, CategoryAttributeContext categoryContext, LocalCategoryAttribute mapped, string remoteValue, CancellationToken cancellationToken)
    {
        var cleanedRemoteValue = CleanCatalogOptionValue(remoteValue);
        var remote = mapped.Values.FirstOrDefault(value => NormalizeCatalogKey(value.Name, 320) == NormalizeCatalogKey(cleanedRemoteValue, 320));
        var localValue = await ResolveMappedAttributeValue(tenantId, connectionId, categoryContext, mapped, remote, cleanedRemoteValue, cancellationToken);
        return localValue?.Value ?? cleanedRemoteValue;
    }

    private async Task UpsertCatalogOptions(Guid tenantId, Guid connectionId, Guid productId, Guid variantId, IReadOnlyDictionary<string, string> options, CategoryAttributeContext? categoryContext, CancellationToken cancellationToken)
    {
        var order = 0;
        var processedPanelOptions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in options.OrderBy(x => IsWebColorOptionKey(x.Key) ? 1 : 0).ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (IsWebColorOptionKey(pair.Key)) continue;
            var optionKey = NormalizeCatalogKey(pair.Key, 160); var valueKey = NormalizeCatalogKey(pair.Value, 160);
            if (string.IsNullOrWhiteSpace(optionKey) || string.IsNullOrWhiteSpace(valueKey)) continue;
            LocalCategoryAttribute? mapped = null;
            if (categoryContext is not null && (!TryGetMappedAttribute(categoryContext.Attributes, pair.Key, out mapped) || (mapped.Role != "OPTION" && !IsVariantOptionName(pair.Key)))) continue;
            var panelLabel = mapped?.Definition.Name ?? pair.Key;
            var panelValue = mapped is null ? pair.Value : await PanelOptionValueAsync(tenantId, connectionId, categoryContext!, mapped, pair.Value, cancellationToken);
            optionKey = NormalizeCatalogKey(panelLabel, 160);
            if (categoryContext is not null && !processedPanelOptions.Add(optionKey)) continue;
            valueKey = NormalizeCatalogKey(panelValue, 160);
            var option = db.ProductOptions.Local.FirstOrDefault(x => x.TenantId == tenantId && x.ProductId == productId && x.NormalizedKey == optionKey)
                ?? await db.ProductOptions.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.ProductId == productId && x.NormalizedKey == optionKey, cancellationToken);
            if (option is null) { option = new ProductOption { Id = Guid.CreateVersion7(), TenantId = tenantId, ProductId = productId, Label = Short(panelLabel, 160), NormalizedKey = optionKey, SortOrder = order }; db.ProductOptions.Add(option); }
            var optionValue = db.ProductOptionValues.Local.FirstOrDefault(x => x.TenantId == tenantId && x.OptionId == option.Id && x.NormalizedKey == valueKey)
                ?? await db.ProductOptionValues.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.OptionId == option.Id && x.NormalizedKey == valueKey, cancellationToken);
            if (optionValue is null) { optionValue = new ProductOptionValue { Id = Guid.CreateVersion7(), TenantId = tenantId, OptionId = option.Id, Label = Short(panelValue, 160), NormalizedKey = valueKey, SortOrder = order }; db.ProductOptionValues.Add(optionValue); }
            var assignment = db.VariantOptionValues.Local.FirstOrDefault(x => x.TenantId == tenantId && x.VariantId == variantId && x.OptionId == option.Id)
                ?? await db.VariantOptionValues.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.VariantId == variantId && x.OptionId == option.Id, cancellationToken);
            if (assignment is null) db.VariantOptionValues.Add(new VariantOptionValue { Id = Guid.CreateVersion7(), TenantId = tenantId, VariantId = variantId, OptionId = option.Id, OptionValueId = optionValue.Id });
            else assignment.OptionValueId = optionValue.Id;
            order++;
        }
    }

    private async Task UpsertCatalogMedia(Guid tenantId, Product product, Guid? variantId, IReadOnlyList<string> sourceUrls, string altText, CancellationToken cancellationToken)
    {
        var urls = CatalogImageIdentity.DistinctUrls(sourceUrls);
        var existing = await db.ProductMedia.Where(x => x.TenantId == tenantId && x.ProductId == product.Id && x.VariantId == variantId).ToListAsync(cancellationToken);
        var existingAssetIds = existing.Select(x => x.FileAssetId).Distinct().ToArray();
        var existingAssets = existingAssetIds.Length == 0
            ? []
            : await db.FileAssets
                .Where(x => x.TenantId == tenantId && existingAssetIds.Contains(x.Id))
                .ToListAsync(cancellationToken);
        foreach (var row in existing.Where(x => x.SortOrder >= urls.Count)) row.Status = "ARCHIVED";
        for (var index = 0; index < urls.Count; index++)
        {
            var url = urls[index];
            var asset = existingAssets.FirstOrDefault(x => x.Classification == "PRODUCT_MEDIA_URL" && string.Equals(CatalogImageIdentity.NormalizeUrl(x.RelativePath), url, StringComparison.OrdinalIgnoreCase))
                ?? db.FileAssets.Local.FirstOrDefault(x => x.TenantId == tenantId && x.Classification == "PRODUCT_MEDIA_URL" && string.Equals(CatalogImageIdentity.NormalizeUrl(x.RelativePath), url, StringComparison.OrdinalIgnoreCase))
                ?? await db.FileAssets.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Classification == "PRODUCT_MEDIA_URL" && x.RelativePath == url, cancellationToken);
            if (asset is null)
            {
                asset = new FileAsset { Id = Guid.CreateVersion7(), TenantId = tenantId, Classification = "PRODUCT_MEDIA_URL", RelativePath = url, OriginalNameSafe = Path.GetFileName(new Uri(url).AbsolutePath), MimeType = ImageMime(url), SizeBytes = 0, Sha256 = Hash(url), Status = "ACTIVE", CreatedAt = timeProvider.GetUtcNow() };
                db.FileAssets.Add(asset);
            }
            else
            {
                if (!string.Equals(asset.RelativePath, url, StringComparison.Ordinal)) asset.RelativePath = url;
                asset.Sha256 = Hash(url);
                if (asset.Status != "ACTIVE" || asset.ArchivedAt is not null) { asset.Status = "ACTIVE"; asset.ArchivedAt = null; }
            }
            var media = existing.SingleOrDefault(x => x.SortOrder == index);
            if (media is null) db.ProductMedia.Add(new ProductMedia { Id = Guid.CreateVersion7(), TenantId = tenantId, ProductId = product.Id, VariantId = variantId, FileAssetId = asset.Id, MediaRole = index == 0 ? "PRIMARY" : "GALLERY", SortOrder = index, AltText = Short(altText, 320), Status = "ACTIVE" });
            else
            {
                var nextRole = index == 0 ? "PRIMARY" : "GALLERY";
                var nextAltText = Short(altText, 320);
                if (media.FileAssetId != asset.Id || media.MediaRole != nextRole || media.AltText != nextAltText || media.Status != "ACTIVE")
                {
                    media.FileAssetId = asset.Id; media.MediaRole = nextRole; media.AltText = nextAltText; media.Status = "ACTIVE";
                }
            }
        }
    }

    private static string ProductTitle(string? title, string externalId) => Short(string.IsNullOrWhiteSpace(title) ? $"Pazar yeri ürünü {externalId}" : title.Trim(), 320);
    private static string CleanCatalogOptionValue(string? value) => string.IsNullOrWhiteSpace(value) ? "" : value.Trim().Trim('"', '“', '”').Trim();
    private static string OptionSignature(IReadOnlyDictionary<string, string> options) => string.Join(" | ", options.Select(x => $"{Short(x.Key, 80)}: {Short(CleanCatalogOptionValue(x.Value), 120)}"));
    private TimeSpan ProductUpdatePollDelay(DateTimeOffset submittedAt)
    {
        var now = timeProvider.GetUtcNow();
        return ProductUpdatePollingPolicy.Delay(
            submittedAt,
            now,
            TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("MarketplaceSync:ProductUpdate:FirstWindowSeconds", 600), 60, 86_400)),
            TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("MarketplaceSync:ProductUpdate:SecondWindowSeconds", 1_800), 120, 172_800)),
            TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("MarketplaceSync:ProductUpdate:ThirdWindowSeconds", 3_600), 180, 259_200)),
            TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("MarketplaceSync:ProductUpdate:FirstDelaySeconds", 120), 1, 3_600)),
            TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("MarketplaceSync:ProductUpdate:SecondDelaySeconds", 300), 1, 3_600)),
            TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("MarketplaceSync:ProductUpdate:ThirdDelaySeconds", 900), 1, 7_200)),
            TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("MarketplaceSync:ProductUpdate:FinalDelaySeconds", 1_800), 1, 14_400)));
    }
    private static string CategoryLeafName(string? name, string? path)
    {
        var source = string.IsNullOrWhiteSpace(path) ? name : path;
        var segments = source?.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
        return Short(segments.Length > 0 ? segments[^1] : name, 160);
    }

    private static string Short(string? value, int maximum) => string.IsNullOrWhiteSpace(value) ? "" : value.Trim().Length <= maximum ? value.Trim() : value.Trim()[..maximum];
    private static string NormalizeCatalogKey(string? value, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var form = value.Trim().Normalize(System.Text.NormalizationForm.FormD);
        var builder = new StringBuilder(form.Length);
        foreach (var ch in form)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) == System.Globalization.UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(ch)) builder.Append(char.ToUpperInvariant(ch)); else if (builder.Length > 0 && builder[^1] != '-') builder.Append('-');
        }
        return builder.ToString().Trim('-')[..Math.Min(maximum, builder.ToString().Trim('-').Length)];
    }
    private static string ImageMime(string url) => new Uri(url).AbsolutePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg";

    // Bump the durable state version so the next order sync starts a bounded
    // three-month baseline instead of continuing the old incremental cursor.
    private const string OrderSyncStateVersion = "orders-v4";
    private static readonly TimeSpan OrderStreamWindowSpan = TimeSpan.FromDays(14);
    private static readonly TimeSpan OrderStreamRequestInterval = TimeSpan.FromSeconds(5);
    private const int DefaultOrderSyncOverlapSeconds = 120;
    private enum OrderSyncMode { Baseline, Incremental }
    private sealed record OrderSyncState(string Version, OrderSyncMode Mode, DateTimeOffset AnchorEnd, DateTimeOffset StartAt, int WindowIndex, string? NextCursor, int StoreFrontIndex = 0);
    private sealed class CategoryAttributeContext(Category LocalCategory, string ExternalCategoryId, IReadOnlyDictionary<string, LocalCategoryAttribute> Attributes)
    {
        public Category LocalCategory { get; } = LocalCategory;
        public string ExternalCategoryId { get; } = ExternalCategoryId;
        public IReadOnlyDictionary<string, LocalCategoryAttribute> Attributes { get; } = Attributes;
        public HashSet<string> ObservedVariantValueKeys { get; } = new(StringComparer.Ordinal);
        public HashSet<Guid> ExactWebColorInitializedAttributeIds { get; } = [];
        public HashSet<Guid> ExactWebColorValueIds { get; } = [];
    }
    private sealed record LocalCategoryAttribute(AttributeDefinition Definition, ReferenceItem Remote, IReadOnlyList<ReferenceItem> Values, string Role);

    private static OrderSyncState ReadOrderSyncState(SyncCursor cursor, DateTimeOffset now, TimeSpan overlap, bool allowBaseline, bool forceBaseline = false)
    {
        if (!forceBaseline && !string.IsNullOrWhiteSpace(cursor.OpaqueCursor))
        {
            try
            {
                var state = JsonSerializer.Deserialize<OrderSyncState>(cursor.OpaqueCursor);
                if (state is { Version: OrderSyncStateVersion, WindowIndex: >= 0, StoreFrontIndex: >= 0 } && state.StoreFrontIndex < TrendyolReadStorefronts.Codes.Length) return state;
            }
            catch (JsonException) { }
        }

        var baseline = allowBaseline && (forceBaseline || (cursor.LastSuccessAt is null && cursor.LastModifiedWatermark is null && string.IsNullOrWhiteSpace(cursor.OpaqueCursor)));
        var anchor = now;
        var oldestAvailable = anchor.AddMonths(-3);
        var watermark = cursor.LastModifiedWatermark ?? cursor.LastSuccessAt ?? anchor.Subtract(OrderStreamWindowSpan);
        if (watermark > anchor) watermark = anchor;
        var start = baseline ? oldestAvailable : watermark.Subtract(overlap);
        if (start < oldestAvailable) start = oldestAvailable;

        // A normal run reads only the changes since the last completed anchor,
        // plus the configured safety overlap. A single stream request may span
        // at most 14 days, so a larger gap reuses durable multi-window state.
        var mode = baseline || anchor - start > OrderStreamWindowSpan
            ? OrderSyncMode.Baseline
            : OrderSyncMode.Incremental;
        return new(OrderSyncStateVersion, mode, anchor, start, 0, null, 0);
    }

    private static (DateTimeOffset Start, DateTimeOffset End) OrderWindow(OrderSyncState state)
    {
        if (state.Mode == OrderSyncMode.Incremental)
            return (state.StartAt, state.AnchorEnd);

        var end = state.AnchorEnd - TimeSpan.FromTicks(state.WindowIndex * (OrderStreamWindowSpan.Ticks + TimeSpan.TicksPerMillisecond));
        var start = end - OrderStreamWindowSpan;
        return (start < state.StartAt ? state.StartAt : start, end);
    }

    private static bool HasEarlierOrderWindow(OrderSyncState state)
    {
        if (state.Mode != OrderSyncMode.Baseline) return false;
        var nextEnd = state.AnchorEnd - TimeSpan.FromTicks((state.WindowIndex + 1L) * (OrderStreamWindowSpan.Ticks + TimeSpan.TicksPerMillisecond));
        return nextEnd >= state.StartAt;
    }

    private static string SerializeOrderSyncState(OrderSyncState state) => JsonSerializer.Serialize(state);

    private async Task<bool> IngestWebhook(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, CancellationToken cancellationToken)
    {
        string raw; string externalMessageId; string resourceType;
        try { using var payload = JsonDocument.Parse(payloadJson); raw = payload.RootElement.GetProperty("rawJson").GetString() ?? ""; externalMessageId = payload.RootElement.GetProperty("externalMessageId").GetString() ?? ""; resourceType = payload.RootElement.TryGetProperty("resourceType", out var type) ? type.GetString() ?? "" : ""; }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException) { return false; }

        var platform = await db.PlatformConnections.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == connectionId).Select(x => x.PlatformCode).SingleOrDefaultAsync(cancellationToken);
        var source = $"{platform}_WEBHOOK";
        if (platform == "HEPSIBURADA")
        {
            try
            {
                using var document = JsonDocument.Parse(raw);
                if (string.Equals(resourceType, "CLAIM_PACKAGE", StringComparison.Ordinal))
                {
                    var order = HepsiburadaJsonMapper.ClaimPackageOrder(document.RootElement);
                    TrackReceived();
                    await UpsertOrder(tenantId, connectionId, order, cancellationToken);
                    foreach (var claimElement in HepsiburadaJsonMapper.ClaimPackageClaims(document.RootElement))
                    {
                        var claim = HepsiburadaJsonMapper.ReturnClaim(claimElement);
                        await UpsertReturn(tenantId, connectionId, correlationId, claim, new Dictionary<string, string?>(StringComparer.Ordinal), cancellationToken);
                    }
                }
                else
                {
                    var claim = HepsiburadaJsonMapper.ReturnClaim(document.RootElement);
                    TrackReceived();
                    await UpsertReturn(tenantId, connectionId, correlationId, claim, new Dictionary<string, string?>(StringComparer.Ordinal), cancellationToken);
                }
            }
            catch (JsonException) { return false; }
        }
        else if (platform == "SHOPIFY")
        {
            if (!configuration.GetValue("Marketplace:PersistOrderSnapshots", true)) return true;
            string? externalOrderId = null;
            try
            {
                using var document = JsonDocument.Parse(raw);
                if (document.RootElement.TryGetProperty("id", out var id))
                    externalOrderId = id.ValueKind == JsonValueKind.Number ? id.GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture) : id.GetString();
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException) { return false; }
            if (string.IsNullOrWhiteSpace(externalOrderId)) return false;
            TrackRequest();
            var order = await orders.GetAsync(Context(tenantId, connectionId, $"webhook:{externalMessageId}", $"shopify-webhook:{connectionId}:{externalMessageId}"), externalOrderId, cancellationToken);
            if (!order.IsSuccess) { TrackResultFailure(order.Error); throw JobProcessingException.FromAdapter(order.Error!); }
            TrackReceived();
            await UpsertOrder(tenantId, connectionId, order.Value!, cancellationToken, projectReservations: false, persistFinancialObservations: true);
        }
        else
        {
            AdapterPageResult<RemoteOrder> page; try { page = TrendyolJsonMapper.Orders(raw); } catch (JsonException) { return false; }
            foreach (var issue in page.Issues ?? [])
                await RecordIssue(tenantId, $"order-webhook-contract:{connectionId}:{issue.Identity}:{issue.Code}", issue.Code, issue.Message, cancellationToken);
            if (configuration.GetValue("Marketplace:PersistOrderSnapshots", true))
                await UpsertOrders(tenantId, connectionId, page.Items, cancellationToken);
        }
        var inbox = await db.InboxMessages.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Source == source && x.ExternalMessageId == externalMessageId, cancellationToken); if (inbox is not null) inbox.ProcessedAt = timeProvider.GetUtcNow(); await db.SaveChangesAsync(cancellationToken); return true;
    }

    private sealed class OrderIngestionBatch
    {
        public Dictionary<string, Order> OrdersByExternalId { get; } = new(StringComparer.Ordinal);
        public Dictionary<Guid, List<OrderLine>> LinesByOrder { get; } = [];
        public Dictionary<Guid, Dictionary<string, ShipmentPackage>> PackagesByOrder { get; } = [];
        public Dictionary<Guid, HashSet<string>> EventIdsByOrder { get; } = [];
        public Dictionary<string, PackageLineAllocation> AllocationsByKey { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, Guid> VariantIdsByKey { get; } = new(StringComparer.Ordinal);
        public List<(OrderLine Line, DateTimeOffset ModifiedAt)> ReservationSources { get; } = [];
    }

    private async Task UpsertOrders(Guid tenantId, Guid connectionId, IReadOnlyList<RemoteOrder> remotes, CancellationToken cancellationToken, bool projectReservations = true)
    {
        if (remotes.Count == 0) return;
        var platformCode = await db.PlatformConnections.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.Id == connectionId)
            .Select(x => x.PlatformCode)
            .SingleOrDefaultAsync(cancellationToken);
        var isShopify = platformCode == "SHOPIFY";
        var isHepsiburada = platformCode == "HEPSIBURADA";
        // The stream is package-shaped: one order can occur once per package.
        // Merge the page before materializing the order so split-package line
        // quantities are summed instead of the last package overwriting them.
        var mergedRemotes = remotes
            .Where(remote => !string.IsNullOrWhiteSpace(remote.ExternalOrderId))
            .GroupBy(remote => remote.ExternalOrderId, StringComparer.Ordinal)
            .Select(group => isShopify
                // Shopify returns a complete order snapshot, including all
                // fulfillments and refunds. The Trendyol package merger only
                // sums active package quantities and would erase Shopify
                // cancellation quantities, so retain the newest snapshot.
                ? group.OrderByDescending(remote => remote.LastModifiedAt).First()
                : isHepsiburada
                    ? MergeHepsiburadaOrderLines(group)
                : TrendyolJsonMapper.MergeOrderPackages(group, group.Key) ?? group.OrderByDescending(remote => remote.LastModifiedAt).First())
            .ToList();
        if (mergedRemotes.Count == 0) return;
        var conflictingPackages = OrderPackageIdentityGuard.FindConflicts(mergedRemotes);
        var conflictedOrderIds = conflictingPackages
            .SelectMany(conflict => new[] { conflict.FirstOrderId, conflict.ConflictingOrderId })
            .ToHashSet(StringComparer.Ordinal);
        foreach (var conflict in conflictingPackages)
            await RecordIssue(
                tenantId,
                $"order-package-identity:{connectionId}:{conflict.ExternalPackageId}",
                "ORDER_PACKAGE_ID_CONFLICT",
                $"Paket kimliği {conflict.ExternalPackageId} birden fazla siparişe bağlandı; çakışan siparişler bu taramada uygulanmadı.",
                cancellationToken);

        var remotePackageOwners = mergedRemotes
            .SelectMany(remote => remote.Packages.Select(package => new { package.ExternalPackageId, remote.ExternalOrderId }))
            .Where(x => !string.IsNullOrWhiteSpace(x.ExternalPackageId))
            .GroupBy(x => x.ExternalPackageId, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.First().ExternalOrderId, StringComparer.Ordinal);
        var candidatePackageIds = remotePackageOwners.Keys.ToArray();
        if (candidatePackageIds.Length > 0)
        {
            var persistedPackageOwners = await (from package in db.ShipmentPackages.AsNoTracking()
                                                join order in db.Orders.AsNoTracking()
                                                    on new { package.TenantId, package.OrderId } equals new { order.TenantId, OrderId = order.Id }
                                                where package.TenantId == tenantId
                                                    && package.ConnectionId == connectionId
                                                    && candidatePackageIds.Contains(package.ExternalPackageId)
                                                select new { package.ExternalPackageId, order.ExternalOrderId })
                .ToListAsync(cancellationToken);
            foreach (var owner in persistedPackageOwners)
            {
                if (!remotePackageOwners.TryGetValue(owner.ExternalPackageId, out var remoteOrderId)
                    || string.Equals(owner.ExternalOrderId, remoteOrderId, StringComparison.Ordinal)) continue;
                conflictedOrderIds.Add(remoteOrderId);
                await RecordIssue(
                    tenantId,
                    $"order-package-identity:{connectionId}:{owner.ExternalPackageId}",
                    "ORDER_PACKAGE_ID_CONFLICT",
                    $"Paket kimliği {owner.ExternalPackageId} mevcut siparişle çakıştı; çakışan sipariş bu taramada uygulanmadı.",
                    cancellationToken);
            }
        }
        if (conflictedOrderIds.Count > 0)
            mergedRemotes = mergedRemotes.Where(remote => !conflictedOrderIds.Contains(remote.ExternalOrderId)).ToList();
        if (mergedRemotes.Count == 0) return;
        var batch = new OrderIngestionBatch();
        var externalIds = mergedRemotes.Select(x => x.ExternalOrderId).ToArray();
        var orders = await db.Orders.Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && externalIds.Contains(x.ExternalOrderId)).ToListAsync(cancellationToken);
        foreach (var order in orders) batch.OrdersByExternalId[order.ExternalOrderId] = order;
        var orderIds = orders.Select(x => x.Id).ToArray();
        if (orderIds.Length > 0)
        {
            var lines = await db.OrderLines.Where(x => x.TenantId == tenantId && orderIds.Contains(x.OrderId)).ToListAsync(cancellationToken);
            foreach (var group in lines.GroupBy(x => x.OrderId)) batch.LinesByOrder[group.Key] = group.ToList();

            var packages = await db.ShipmentPackages.Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && orderIds.Contains(x.OrderId)).ToListAsync(cancellationToken);
            foreach (var group in packages.GroupBy(x => x.OrderId)) batch.PackagesByOrder[group.Key] = group.ToDictionary(x => x.ExternalPackageId, StringComparer.Ordinal);

            var packageIds = packages.Select(x => x.Id).ToArray();
            if (packageIds.Length > 0)
            {
                var allocations = await db.PackageLineAllocations.AsNoTracking().Where(x => x.TenantId == tenantId && packageIds.Contains(x.PackageId)).ToListAsync(cancellationToken);
                foreach (var allocation in allocations) batch.AllocationsByKey[AllocationKey(allocation.PackageId, allocation.OrderLineId, allocation.SourceEventId)] = allocation;
            }

            var history = await db.OrderStatusHistory.AsNoTracking().Where(x => x.TenantId == tenantId && orderIds.Contains(x.OrderId)).Select(x => new { x.OrderId, x.SourceEventId }).ToListAsync(cancellationToken);
            foreach (var group in history.GroupBy(x => x.OrderId)) batch.EventIdsByOrder[group.Key] = group.Select(x => x.SourceEventId).ToHashSet(StringComparer.Ordinal);
        }

        var variantIds = await ResolveOrderLineVariantIds(tenantId, remotes.SelectMany(x => x.Lines).ToList(), cancellationToken);
        foreach (var pair in variantIds) batch.VariantIdsByKey[pair.Key] = pair.Value;
        foreach (var remote in mergedRemotes) await UpsertOrder(tenantId, connectionId, remote, cancellationToken, batch, saveChanges: false, projectReservations: projectReservations, persistFinancialObservations: isShopify);
        if (projectReservations)
            await ProjectOrderReservations(tenantId, connectionId, batch.ReservationSources, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    private static RemoteOrder MergeHepsiburadaOrderLines(IEnumerable<RemoteOrder> candidates)
    {
        var matches = candidates.ToArray();
        var latest = matches.OrderByDescending(remote => remote.LastModifiedAt).First();
        var lines = matches
            .SelectMany(remote => remote.Lines)
            .GroupBy(line => line.ExternalLineId, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(line => line.RawStatus, StringComparer.Ordinal).First())
            .ToArray();
        var packages = matches
            .SelectMany(remote => remote.Packages)
            .GroupBy(package => package.ExternalPackageId, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(package => package.OccurredAt).First())
            .ToArray();
        var gross = lines.Sum(line => line.UnitPrice * line.Quantity);
        var discount = Math.Min(gross, matches.Sum(remote => remote.DiscountAmount));
        return latest with
        {
            LastModifiedAt = matches.Max(remote => remote.LastModifiedAt),
            GrossAmount = gross,
            DiscountAmount = discount,
            NetAmount = Math.Max(0m, gross - discount),
            Lines = lines,
            Packages = packages
        };
    }

    private async Task<bool> UpsertOrder(Guid tenantId, Guid connectionId, RemoteOrder remote, CancellationToken cancellationToken, OrderIngestionBatch? batch = null, bool saveChanges = true, bool projectReservations = true, bool persistFinancialObservations = false)
    {
        var platformCode = await db.PlatformConnections.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.Id == connectionId)
            .Select(x => x.PlatformCode)
            .SingleOrDefaultAsync(cancellationToken);
        var isShopify = platformCode == "SHOPIFY";
        var isHepsiburada = platformCode == "HEPSIBURADA";
        var now = timeProvider.GetUtcNow();
        var order = batch?.OrdersByExternalId.GetValueOrDefault(remote.ExternalOrderId)
            ?? await db.Orders.SingleOrDefaultAsync(x => x.TenantId == tenantId
                && x.ConnectionId == connectionId
                && x.ExternalOrderId == remote.ExternalOrderId, cancellationToken);

        if (isHepsiburada && batch is null && order is not null && remote.Lines.Count == 0)
        {
            var persistedLines = await db.OrderLines.AsNoTracking()
                .Where(line => line.TenantId == tenantId && line.OrderId == order.Id)
                .OrderBy(line => line.ExternalLineId)
                .Select(line => new RemoteOrderLine(
                    line.ExternalLineId,
                    line.Sku,
                    line.Barcode,
                    line.TitleSnapshot,
                    line.OrderedQuantity,
                    line.UnitPrice,
                    line.VatRate,
                    line.RawStatus,
                    line.SourceSnapshotJson ?? "{}",
                    line.CancelledQuantity))
                .ToListAsync(cancellationToken);
            if (HepsiburadaPackageStatusReconciliationPolicy.TryHydrateStatusObservationLines(remote, persistedLines, out var hydratedOrder))
                remote = hydratedOrder;
        }

        IReadOnlyDictionary<string, decimal> remoteLineQuantities;
        if (remote.Lines.Count == 0 || remote.Packages.Count == 0 && !isHepsiburada)
        {
            await RecordIssue(tenantId, $"order-contract:{connectionId}:{remote.ExternalOrderId}:{remote.LastModifiedAt.ToUnixTimeMilliseconds()}", "ORDER_CONTRACT_INVALID", "Pazar yeri siparişinde satır veya paket verisi eksikti; eksik sipariş projeksiyonu uygulanmadı.", cancellationToken);
            if (saveChanges) await db.SaveChangesAsync(cancellationToken);
            return false;
        }
        else
        {
            remoteLineQuantities = new Dictionary<string, decimal>(StringComparer.Ordinal);
            if (!PackageIngestionSafety.TryGetOrderedQuantities(remote.Lines, out remoteLineQuantities))
            {
                await RecordIssue(tenantId, $"order-lines:{connectionId}:{remote.ExternalOrderId}:{remote.LastModifiedAt.ToUnixTimeMilliseconds()}", "ORDER_LINE_QUANTITY_INVARIANT_REJECTED", "Sipariş satır kimliği veya miktarı geçersizdi; olayın hiçbir parçası uygulanmadı.", cancellationToken);
                if (saveChanges) await db.SaveChangesAsync(cancellationToken);
                return false;
            }
        }
        var detailedPackages = remote.Packages.Where(package => !package.IsStatusObservation).ToArray();
        var allocatedLineIds = detailedPackages.SelectMany(x => x.Allocations).Select(x => x.ExternalLineId).ToHashSet(StringComparer.Ordinal);
        if (detailedPackages.Length > 0 && remoteLineQuantities.Keys.Any(lineId => !allocatedLineIds.Contains(lineId)))
        {
            await RecordIssue(tenantId, $"order-coverage:{connectionId}:{remote.ExternalOrderId}:{remote.LastModifiedAt.ToUnixTimeMilliseconds()}", "ORDER_LINE_COVERAGE_INVALID", "Pazar yeri cevabındaki sipariş satırlarının tamamı paket tahsisinde yer almıyordu; eksik veri uygulanmadı.", cancellationToken);
            if (saveChanges) await db.SaveChangesAsync(cancellationToken);
            return false;
        }
        // A new order must arrive as a complete package aggregate. Once the
        // order already exists, a later stream page may contain only one
        // sibling package; that fragment is validated per package below and
        // merged with the persisted projection instead of being rejected as an
        // incomplete order.
        if (order is null && detailedPackages.Length > 0 && !PackageIngestionSafety.TryNormalizeOrder(remoteLineQuantities, detailedPackages, out _))
        {
            var rejectedEventId = remote.Packages.Count > 0
                ? PackageIngestionSafety.EventId(remote.Packages[0].ExternalPackageId, remote.Packages[0].OccurredAt)
                : remote.ExternalOrderId;
            await RecordIssue(tenantId, $"package-quantity:{connectionId}:{rejectedEventId}", "PACKAGE_QUANTITY_INVARIANT_REJECTED", "Paket miktarları sipariş satırı bütünlüğünü sağlamadı; olayın hiçbir parçası uygulanmadı.", cancellationToken);
            if (saveChanges) await db.SaveChangesAsync(cancellationToken);
            return false;
        }
        if (order is not null)
        {
            var repairCandidates = batch is not null
                ? (batch.PackagesByOrder.GetValueOrDefault(order.Id)?.Values.Where(x => x.Status == ShipmentPackageStatus.ManualReview).ToList() ?? [])
                : await db.ShipmentPackages.Where(x => x.TenantId == tenantId && x.OrderId == order.Id && x.Status == ShipmentPackageStatus.ManualReview).ToListAsync(cancellationToken);
            foreach (var candidate in repairCandidates) { var canonical = ShipmentPackageStatusPolicy.FromRemote(candidate.RawStatus); if (canonical != ShipmentPackageStatus.ManualReview) { candidate.Status = canonical; candidate.UpdatedAt = now; candidate.Version++; } }
        }
        // Do not short-circuit empty-line replays: the same remote package can need a safe local canonical projection repair after a previously unknown raw status becomes recognized.
        var orderIsFresh = order is null || remote.LastModifiedAt >= order.LastRemoteModifiedAt;
        if (!orderIsFresh) telemetrySkippedCount++;
        if (order is null) { order = new Order { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = connectionId, ExternalOrderId = remote.ExternalOrderId, OrderNumber = remote.OrderNumber, Currency = remote.Currency, CustomerSnapshotJson = remote.CustomerSnapshotJson, ShipmentAddressSnapshotJson = remote.ShipmentAddressSnapshotJson, InvoiceAddressSnapshotJson = remote.InvoiceAddressSnapshotJson, DerivedStatus = "NEW", ShipmentDueAt = remote.ShipmentDueAt, CreatedAt = now, Version = 1 }; db.Orders.Add(order); batch?.OrdersByExternalId.TryAdd(remote.ExternalOrderId, order); telemetryInsertedCount++; }
        if (orderIsFresh)
        {
            var customerSnapshot = remote.CustomerSnapshotJson;
            var shipmentAddressSnapshot = remote.ShipmentAddressSnapshotJson;
            var invoiceAddressSnapshot = remote.InvoiceAddressSnapshotJson;
            var grossAmount = remote.GrossAmount;
            var discountAmount = remote.DiscountAmount;
            var netAmount = remote.NetAmount;
            if (isShopify && ShopifyOrderCsvSnapshotPolicy.HasImport(order.CustomerSnapshotJson))
            {
                customerSnapshot = ShopifyOrderCsvSnapshotPolicy.MergeRemoteSnapshot(customerSnapshot, order.CustomerSnapshotJson);
                shipmentAddressSnapshot = ShopifyOrderCsvSnapshotPolicy.MergeRemoteSnapshot(shipmentAddressSnapshot, order.ShipmentAddressSnapshotJson);
                invoiceAddressSnapshot = ShopifyOrderCsvSnapshotPolicy.MergeRemoteSnapshot(invoiceAddressSnapshot, order.InvoiceAddressSnapshotJson);
                (grossAmount, discountAmount, netAmount) = ShopifyOrderCsvSnapshotPolicy.MergeRemoteAmounts(
                    order.CustomerSnapshotJson, remote.GrossAmount, remote.DiscountAmount, remote.NetAmount);
            }
            else if (isHepsiburada)
            {
                // Keep authoritative invoice/cargo observations when an order
                // list read omits the detail-only fields.
                customerSnapshot = ShopifyOrderCsvSnapshotPolicy.MergeRemoteSnapshot(customerSnapshot, order.CustomerSnapshotJson);
                shipmentAddressSnapshot = ShopifyOrderCsvSnapshotPolicy.MergeRemoteSnapshot(shipmentAddressSnapshot, order.ShipmentAddressSnapshotJson);
                invoiceAddressSnapshot = ShopifyOrderCsvSnapshotPolicy.MergeRemoteSnapshot(invoiceAddressSnapshot, order.InvoiceAddressSnapshotJson);
            }
            order.OrderNumber = remote.OrderNumber; order.Currency = remote.Currency; order.GrossAmount = grossAmount; order.DiscountAmount = discountAmount; order.NetAmount = netAmount; order.OrderedAt = remote.OrderedAt; order.ShipmentDueAt = remote.ShipmentDueAt; order.LastRemoteModifiedAt = remote.LastModifiedAt; order.CustomerSnapshotJson = customerSnapshot; order.ShipmentAddressSnapshotJson = shipmentAddressSnapshot; order.InvoiceAddressSnapshotJson = invoiceAddressSnapshot; order.UpdatedAt = now; if (db.Entry(order).State != EntityState.Added) { order.Version++; telemetryUpdatedCount++; }
        }
        if (persistFinancialObservations && orderIsFresh)
            await UpsertShopifyFinancialObservations(tenantId, order.Id, remote, cancellationToken);
        var existingLines = batch is not null
            ? batch.LinesByOrder.GetValueOrDefault(order.Id) ?? []
            : await db.OrderLines.Where(x => x.TenantId == tenantId && x.OrderId == order.Id).ToListAsync(cancellationToken);
        if (!orderIsFresh && isHepsiburada)
            telemetryUpdatedCount += HepsiburadaStaleOrderEnrichmentPolicy.Apply(order, existingLines, remote, now);
        var lines = existingLines
            .GroupBy(x => x.ExternalLineId, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);
        var linesByExternalId = existingLines
            .GroupBy(x => x.ExternalLineId, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);
        var linesBySnapshot = existingLines
            .Where(x => !string.IsNullOrWhiteSpace(x.SourceSnapshotJson) && x.SourceSnapshotJson != "{}")
            .GroupBy(x => x.SourceSnapshotJson!, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);
        var packagesByExternalId = batch is not null
            ? batch.PackagesByOrder.GetValueOrDefault(order.Id) ?? new Dictionary<string, ShipmentPackage>(StringComparer.Ordinal)
            : await db.ShipmentPackages.Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.OrderId == order.Id).ToDictionaryAsync(x => x.ExternalPackageId, StringComparer.Ordinal, cancellationToken);
        var knownEventIds = batch is not null
            ? batch.EventIdsByOrder.GetValueOrDefault(order.Id) ?? new HashSet<string>(StringComparer.Ordinal)
            : (await db.OrderStatusHistory.AsNoTracking().Where(x => x.TenantId == tenantId && x.OrderId == order.Id).Select(x => x.SourceEventId).ToListAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
        foreach (var localEvent in db.OrderStatusHistory.Local.Where(x => x.TenantId == tenantId && x.OrderId == order.Id)) knownEventIds.Add(localEvent.SourceEventId);
        var packageIds = packagesByExternalId.Values.Select(x => x.Id).ToArray();
        var allocationsByKey = batch is not null
            ? batch.AllocationsByKey.Where(x => packageIds.Contains(x.Value.PackageId)).ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal)
            : (await db.PackageLineAllocations.AsNoTracking().Where(x => x.TenantId == tenantId && packageIds.Contains(x.PackageId)).ToListAsync(cancellationToken)).ToDictionary(x => AllocationKey(x.PackageId, x.OrderLineId, x.SourceEventId), StringComparer.Ordinal);
        var variantIdsByKey = batch?.VariantIdsByKey ?? await ResolveOrderLineVariantIds(tenantId, remote.Lines, cancellationToken);
        if (batch is not null)
        {
            batch.LinesByOrder[order.Id] = existingLines;
            batch.PackagesByOrder[order.Id] = packagesByExternalId;
            batch.EventIdsByOrder[order.Id] = knownEventIds;
        }
        if (orderIsFresh) foreach (var remoteLine in remote.Lines)
        {
            var line = linesByExternalId.GetValueOrDefault(remoteLine.ExternalLineId);
            if (line is null && isHepsiburada)
                line = HepsiburadaOrderLineMatcher.FindExistingLine(remoteLine, existingLines, remote.Lines);
            if (line is null && !string.IsNullOrWhiteSpace(remoteLine.SourceSnapshotJson) && remoteLine.SourceSnapshotJson != "{}") line = linesBySnapshot.GetValueOrDefault(remoteLine.SourceSnapshotJson);
            if (line is null) { line = new OrderLine { Id = Guid.CreateVersion7(), TenantId = tenantId, OrderId = order.Id, ExternalLineId = remoteLine.ExternalLineId, Sku = remoteLine.Sku, TitleSnapshot = remoteLine.Title, RawStatus = remoteLine.RawStatus, Version = 1 }; db.OrderLines.Add(line); existingLines.Add(line); telemetryInsertedCount++; }

            if (line.VariantId is null)
            {
                var barcodeKey = VariantLookupKey(remoteLine.Barcode, true);
                var skuKey = VariantLookupKey(remoteLine.Sku, false);
                line.VariantId = barcodeKey is not null && variantIdsByKey.TryGetValue(barcodeKey, out var barcodeVariant)
                    ? barcodeVariant
                    : skuKey is not null && variantIdsByKey.TryGetValue(skuKey, out var skuVariant) ? skuVariant : null;
            }
            var orderedQuantity = db.Entry(line).State == EntityState.Added
                ? remoteLine.Quantity
                : Math.Max(line.OrderedQuantity, remoteLine.Quantity);
            var importedUnitPrice = isShopify && remoteLine.UnitPrice == 0 && ShopifyOrderCsvSnapshotPolicy.HasImport(line.SourceSnapshotJson)
                ? ShopifyOrderCsvSnapshotPolicy.ImportedAmount(line.SourceSnapshotJson, "unitPrice")
                : null;
            var sku = isShopify ? ShopifyOrderCsvSnapshotPolicy.PreserveRemoteSku(remoteLine.Sku, line) ?? line.Sku : remoteLine.Sku;
            var title = isShopify ? ShopifyOrderCsvSnapshotPolicy.PreserveRemoteTitle(remoteLine.Title, line) : remoteLine.Title;
            var sourceSnapshot = isShopify || isHepsiburada
                // Hepsiburada order details may omit the image field present in
                // paid-order/package lists. Keep previously observed line data
                // when a later read does not repeat it.
                ? ShopifyOrderCsvSnapshotPolicy.MergeRemoteSnapshot(remoteLine.SourceSnapshotJson, line.SourceSnapshotJson)
                : remoteLine.SourceSnapshotJson;
            var previousExternalLineId = line.ExternalLineId;
            if (!string.Equals(previousExternalLineId, remoteLine.ExternalLineId, StringComparison.Ordinal))
            {
                lines.Remove(previousExternalLineId);
                linesByExternalId.Remove(previousExternalLineId);
            }
            line.ExternalLineId = remoteLine.ExternalLineId;
            line.Sku = sku; line.Barcode = remoteLine.Barcode ?? line.Barcode; line.TitleSnapshot = title; line.SourceSnapshotJson = sourceSnapshot; line.OrderedQuantity = orderedQuantity; line.UnitPrice = importedUnitPrice ?? remoteLine.UnitPrice; line.VatRate = remoteLine.VatRate; line.RawStatus = remoteLine.RawStatus; if (db.Entry(line).State != EntityState.Added) line.Version++; lines[remoteLine.ExternalLineId] = line;
            linesByExternalId[remoteLine.ExternalLineId] = line;
            if (!string.IsNullOrWhiteSpace(remoteLine.SourceSnapshotJson) && remoteLine.SourceSnapshotJson != "{}") linesBySnapshot[remoteLine.SourceSnapshotJson] = line;
        }
        if (isHepsiburada)
        {
            var knownLineGross = lines.Values.Sum(line => line.UnitPrice * line.OrderedQuantity);
            if (knownLineGross > 0)
            {
                order.GrossAmount = knownLineGross;
                if (remote.DiscountAmount > 0 || order.DiscountAmount == 0) order.DiscountAmount = Math.Min(knownLineGross, remote.DiscountAmount);
                order.NetAmount = Math.Max(0m, knownLineGross - order.DiscountAmount);
            }
        }
        var externalLineIdsById = lines.Values.ToDictionary(line => line.Id, line => line.ExternalLineId);
        foreach (var remotePackage in remote.Packages)
        {
            var target = ShipmentPackageStatusPolicy.FromRemote(remotePackage.RawStatus); var eventId = PackageIngestionSafety.EventId(remotePackage.ExternalPackageId, remotePackage.OccurredAt); var orderedQuantities = lines.ToDictionary(x => x.Key, x => x.Value.OrderedQuantity, StringComparer.Ordinal);
            var package = packagesByExternalId.GetValueOrDefault(remotePackage.ExternalPackageId);
            var packageAllocations = remotePackage.Allocations;
            if (remotePackage.IsStatusObservation && packageAllocations.Count == 0 && package is not null)
            {
                packageAllocations = allocationsByKey.Values
                    .Where(allocation => allocation.PackageId == package.Id && externalLineIdsById.ContainsKey(allocation.OrderLineId))
                    .GroupBy(allocation => allocation.OrderLineId)
                    .Select(group => group.OrderByDescending(allocation => allocation.SourceEventId, StringComparer.Ordinal).First())
                    .Select(allocation =>
                    {
                        var activeQuantity = allocation.AllocatedQuantity;
                        var shippedQuantity = target is ShipmentPackageStatus.Shipped or ShipmentPackageStatus.Delivered or ShipmentPackageStatus.Undelivered
                            ? activeQuantity
                            : allocation.ShippedQuantity;
                        var deliveredQuantity = target == ShipmentPackageStatus.Delivered ? activeQuantity : allocation.DeliveredQuantity;
                        return new RemotePackageAllocation(externalLineIdsById[allocation.OrderLineId], activeQuantity, allocation.CancelledQuantity, shippedQuantity, deliveredQuantity, allocation.ReturnedQuantity);
                    })
                    .ToArray();
            }
            IReadOnlyDictionary<string, NormalizedPackageAllocation> safeAllocations = new Dictionary<string, NormalizedPackageAllocation>(StringComparer.Ordinal);
            if (packageAllocations.Count > 0 || !remotePackage.IsStatusObservation)
            {
                if (!PackageIngestionSafety.TryNormalizeAll(orderedQuantities, packageAllocations, target, out safeAllocations))
                {
                    await RecordIssue(tenantId, $"package-quantity:{connectionId}:{eventId}", "PACKAGE_QUANTITY_INVARIANT_REJECTED", "Package miktarları sipariş satırı bütünlüğünü sağlamadı; olayın hiçbir parçası uygulanmadı.", cancellationToken);
                    continue;
                }
            }
            var packageObservation = remotePackage with { Allocations = packageAllocations };
            if (package is not null) await MergeMarketplaceInvoiceState(package, remotePackage, cancellationToken);
            if (package is not null && package.Status == ShipmentPackageStatus.ManualReview && package.RawStatus == remotePackage.RawStatus && target != ShipmentPackageStatus.ManualReview) { package.Status = target; package.UpdatedAt = now; package.Version++; continue; }
            var eventAlreadyRecorded = knownEventIds.Contains(eventId);
            if (eventAlreadyRecorded)
            {
                // Shopify can enrich the same fulfillment event later with
                // delivery information while keeping its original createdAt.
                // Treat that as an idempotent status enrichment, not as a
                // reason to discard the authoritative forward transition.
                var statusChanged = package is not null
                    && (isHepsiburada
                        ? HepsiburadaPackageStatusReconciliationPolicy.ShouldAcceptStatusObservation(
                            package.Status,
                            package.StatusOccurredAt,
                            target,
                            remotePackage.OccurredAt,
                            remotePackage.IsStatusObservation)
                        : PackageIngestionSafety.ShouldAccept(package.Status, package.StatusOccurredAt, target, remotePackage.OccurredAt))
                    && package.Status != target;
                var packageMetadataChanged = package is not null
                    && ((!string.IsNullOrWhiteSpace(remotePackage.CargoProviderExternalId) && package.CargoProviderExternalId != remotePackage.CargoProviderExternalId)
                        || (!string.IsNullOrWhiteSpace(remotePackage.CargoTrackingNumber) && package.CargoTrackingNumber != remotePackage.CargoTrackingNumber));
                if (statusChanged && package is not null)
                {
                    package.Status = target;
                    package.RawStatus = remotePackage.RawStatus;
                    package.StatusOccurredAt = remotePackage.OccurredAt;
                    package.GrossAmount = remotePackage.GrossAmount;
                    package.DiscountAmount = remotePackage.DiscountAmount;
                    package.NetAmount = remotePackage.NetAmount;

                    foreach (var remoteAllocation in packageObservation.Allocations)
                    {
                        if (!lines.TryGetValue(remoteAllocation.ExternalLineId, out var line)
                            || !safeAllocations.TryGetValue(remoteAllocation.ExternalLineId, out var safe)) continue;
                        var allocationKey = AllocationKey(package.Id, line.Id, eventId);
                        var allocation = allocationsByKey.GetValueOrDefault(allocationKey);
                        if (allocation is null)
                        {
                            allocation = new PackageLineAllocation { Id = Guid.CreateVersion7(), TenantId = tenantId, PackageId = package.Id, OrderLineId = line.Id, SourceEventId = eventId };
                            db.PackageLineAllocations.Add(allocation);
                            allocationsByKey[allocationKey] = allocation;
                        }
                        allocation.AllocatedQuantity = safe.ActiveAllocatedQuantity;
                        allocation.CancelledQuantity = safe.CancelledQuantity;
                        allocation.ShippedQuantity = safe.ShippedQuantity;
                        allocation.DeliveredQuantity = safe.DeliveredQuantity;
                        allocation.ReturnedQuantity = safe.ReturnedQuantity;
                    }
                }
                if (package is not null && (statusChanged || packageMetadataChanged))
                {
                    package.OriginExternalPackageId = string.IsNullOrWhiteSpace(remotePackage.OriginExternalPackageId) ? package.OriginExternalPackageId : remotePackage.OriginExternalPackageId;
                    package.CargoProviderExternalId = string.IsNullOrWhiteSpace(remotePackage.CargoProviderExternalId) ? package.CargoProviderExternalId : remotePackage.CargoProviderExternalId;
                    package.CargoTrackingNumber = string.IsNullOrWhiteSpace(remotePackage.CargoTrackingNumber) ? package.CargoTrackingNumber : remotePackage.CargoTrackingNumber;
                    package.UpdatedAt = now;
                    package.Version++;
                    telemetryUpdatedCount++;
                }
                // The initial projection used shipmentPackageStatus before the
                // authoritative top-level status. When the same package event
                // is replayed after that mapper correction, repair only this
                // known ReadyToShip -> New projection mismatch. This is a
                // local idempotent repair; it does not create a new remote
                // event or perform any marketplace write.
                if (package is not null
                    && package.Status == ShipmentPackageStatus.ReadyToShip
                    && string.Equals(package.RawStatus, "ReadyToShip", StringComparison.OrdinalIgnoreCase)
                    && target == ShipmentPackageStatus.New
                    && string.Equals(remotePackage.RawStatus, "Created", StringComparison.OrdinalIgnoreCase))
                {
                    package.Status = target;
                    package.RawStatus = remotePackage.RawStatus;
                    package.StatusOccurredAt = remotePackage.OccurredAt;
                    package.UpdatedAt = now;
                    package.Version++;
                }
                if (package is not null && package.Status == ShipmentPackageStatus.ManualReview && package.RawStatus == remotePackage.RawStatus && target != ShipmentPackageStatus.ManualReview) { package.Status = target; package.UpdatedAt = now; package.Version++; }
                continue;
            }
            var accept = package is null || (isHepsiburada
                ? HepsiburadaPackageStatusReconciliationPolicy.ShouldAcceptStatusObservation(
                    package.Status,
                    package.StatusOccurredAt,
                    target,
                    remotePackage.OccurredAt,
                    remotePackage.IsStatusObservation)
                : PackageIngestionSafety.ShouldAccept(package.Status, package.StatusOccurredAt, target, remotePackage.OccurredAt));
            if (package is null) { package = new ShipmentPackage { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = connectionId, OrderId = order.Id, ExternalPackageId = remotePackage.ExternalPackageId, CreatedBy = remotePackage.CreatedBy, Status = target, RawStatus = remotePackage.RawStatus, StatusOccurredAt = remotePackage.OccurredAt, CreatedAt = now, Version = 1 }; db.ShipmentPackages.Add(package); packagesByExternalId[remotePackage.ExternalPackageId] = package; telemetryInsertedCount++; await MergeMarketplaceInvoiceState(package, remotePackage, cancellationToken); }
            else if (accept) { package.Status = target; package.RawStatus = remotePackage.RawStatus; package.StatusOccurredAt = remotePackage.OccurredAt; package.Version++; }
            else if (remotePackage.OccurredAt >= package.StatusOccurredAt && package.Status != target) await RecordIssue(tenantId, $"package-transition:{package.Id}:{remotePackage.RawStatus}", "PACKAGE_TRANSITION_REJECTED", "Out-of-order veya izin verilmeyen package geçişi mevcut durumu geriye götürmedi.", cancellationToken);
            if (package is not null && !remotePackage.IsStatusObservation && !string.IsNullOrWhiteSpace(remotePackage.CreatedBy)) package.CreatedBy = remotePackage.CreatedBy;
            if (accept && package is not null)
            {
                package.OriginExternalPackageId = string.IsNullOrWhiteSpace(remotePackage.OriginExternalPackageId) ? package.OriginExternalPackageId : remotePackage.OriginExternalPackageId; package.CargoProviderExternalId = string.IsNullOrWhiteSpace(remotePackage.CargoProviderExternalId) ? package.CargoProviderExternalId : remotePackage.CargoProviderExternalId; package.CargoTrackingNumber = string.IsNullOrWhiteSpace(remotePackage.CargoTrackingNumber) ? package.CargoTrackingNumber : remotePackage.CargoTrackingNumber; package.GrossAmount = remotePackage.GrossAmount; package.DiscountAmount = remotePackage.DiscountAmount; package.NetAmount = remotePackage.NetAmount; package.UpdatedAt = now; db.OrderStatusHistory.Add(new OrderStatusHistory { Id = Guid.CreateVersion7(), TenantId = tenantId, OrderId = order.Id, PackageId = package.Id, CanonicalStatus = Wire(target), RawStatus = remotePackage.RawStatus, SourceEventId = eventId, OccurredAt = remotePackage.OccurredAt, RecordedAt = now }); knownEventIds.Add(eventId);
                foreach (var remoteAllocation in packageObservation.Allocations) if (lines.TryGetValue(remoteAllocation.ExternalLineId, out var line) && safeAllocations.TryGetValue(remoteAllocation.ExternalLineId, out var safe)) { var allocationKey = AllocationKey(package.Id, line.Id, eventId); var allocation = allocationsByKey.GetValueOrDefault(allocationKey); if (allocation is null) { allocation = new PackageLineAllocation { Id = Guid.CreateVersion7(), TenantId = tenantId, PackageId = package.Id, OrderLineId = line.Id, SourceEventId = eventId, AllocatedQuantity = safe.ActiveAllocatedQuantity, CancelledQuantity = safe.CancelledQuantity, ShippedQuantity = safe.ShippedQuantity, DeliveredQuantity = safe.DeliveredQuantity, ReturnedQuantity = safe.ReturnedQuantity }; db.PackageLineAllocations.Add(allocation); allocationsByKey[allocationKey] = allocation; telemetryInsertedCount++; } }
            }
        }
        var projectedLineQuantities = PackageLineProjectionPolicy.Recalculate(packagesByExternalId.Values.ToList(), allocationsByKey.Values.ToList());
        foreach (var line in lines.Values)
        {
            if (!projectedLineQuantities.TryGetValue(line.Id, out var projection)) continue;
            line.CancelledQuantity = projection.CancelledQuantity;
            line.ShippedQuantity = projection.ShippedQuantity;
            line.DeliveredQuantity = projection.DeliveredQuantity;
            line.ReturnedQuantity = projection.ReturnedQuantity;
        }
        var persistedStatuses = batch is not null
            ? packagesByExternalId.Values.Select(x => x.Status).ToList()
            : await db.ShipmentPackages.AsNoTracking().Where(x => x.TenantId == tenantId && x.OrderId == order.Id).Select(x => x.Status).ToListAsync(cancellationToken);
        var acceptedStatuses = persistedStatuses.ToList();
        acceptedStatuses.AddRange(db.ShipmentPackages.Local.Where(x => x.TenantId == tenantId && x.OrderId == order.Id).Select(x => x.Status));
        var derivedStatus = isHepsiburada
            ? HepsiburadaOrderLifecycleStatusPolicy.AggregatePackages(acceptedStatuses)
            : ShipmentPackageStatusPolicy.Aggregate(acceptedStatuses);
        var lifecycleBaseline = acceptedStatuses.Count == 0 ? order.DerivedStatus : Wire(derivedStatus);
        var hasUndeliveredHepsiburadaPackage = isHepsiburada && acceptedStatuses.Contains(ShipmentPackageStatus.Undelivered);
        if (isHepsiburada && !hasUndeliveredHepsiburadaPackage && HepsiburadaOrderLifecycleStatusPolicy.Reconcile(
                lifecycleBaseline,
                remote.LifecycleStatus,
                lines.Values.Select(line => line.RawStatus)) is { } hepsiburadaStatus)
            derivedStatus = hepsiburadaStatus;
        order.DerivedStatus = Wire(derivedStatus);
        if (isShopify)
        {
            var manualStatus = await db.OrderStatusHistory.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.OrderId == order.Id && x.RawStatus.StartsWith("MANUAL_SHOPIFY_STATUS:"))
                .OrderByDescending(x => x.OccurredAt)
                .Select(x => new { x.CanonicalStatus, x.OccurredAt })
                .FirstOrDefaultAsync(cancellationToken);
            if (manualStatus is not null && manualStatus.OccurredAt >= order.LastRemoteModifiedAt)
                order.DerivedStatus = manualStatus.CanonicalStatus;
        }
        if (projectReservations && batch is null)
            await ProjectOrderReservations(tenantId, connectionId, lines.Values.Select(line => (line, remote.LastModifiedAt)).ToList(), cancellationToken);
        else if (projectReservations && batch is not null)
            batch.ReservationSources.AddRange(lines.Values.Select(line => (line, remote.LastModifiedAt)));
        if (saveChanges) await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private async Task UpsertShopifyFinancialObservations(Guid tenantId, Guid orderId, RemoteOrder remote, CancellationToken cancellationToken)
    {
        var rows = await db.OrderFinancialAllocations
            .Where(x => x.TenantId == tenantId && x.OrderId == orderId && x.SourceKey.StartsWith("shopify:"))
            .ToListAsync(cancellationToken);
        var byKey = rows.ToDictionary(x => x.SourceKey, StringComparer.Ordinal);

        Upsert("shopify:payment-status", $"PAYMENT_STATUS:{Short(remote.PaymentStatus, 48)}", 0m);
        Upsert("shopify:cancellation-status", $"CANCELLATION_STATUS:{Short(remote.CancellationStatus, 48)}", 0m);
        Upsert("shopify:refund-summary", $"REFUND_STATUS:{Short(remote.RefundStatus, 48)}", remote.RefundedAmount);

        var currentRefundKeys = (remote.Refunds ?? [])
            .Where(refund => !string.IsNullOrWhiteSpace(refund.ExternalRefundId))
            .Select(refund => $"shopify:refund:{Short(refund.ExternalRefundId, 180)}")
            .ToHashSet(StringComparer.Ordinal);
        foreach (var stale in rows.Where(row => row.SourceKey.StartsWith("shopify:refund:") && row.SourceKey != "shopify:refund-summary" && !currentRefundKeys.Contains(row.SourceKey)))
            db.OrderFinancialAllocations.Remove(stale);
        foreach (var refund in remote.Refunds ?? [])
        {
            if (string.IsNullOrWhiteSpace(refund.ExternalRefundId)) continue;
            Upsert($"shopify:refund:{Short(refund.ExternalRefundId, 180)}", "REFUND", refund.Amount, refund.Currency);
        }
        return;

        void Upsert(string sourceKey, string allocationType, decimal amount, string? currency = null)
        {
            if (byKey.TryGetValue(sourceKey, out var row))
            {
                row.AllocationType = allocationType;
                row.Amount = Math.Max(0, amount);
                row.Currency = Short(currency ?? remote.Currency, 3).ToUpperInvariant();
                return;
            }
            db.OrderFinancialAllocations.Add(new OrderFinancialAllocation
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                OrderId = orderId,
                AllocationType = allocationType,
                Amount = Math.Max(0, amount),
                Currency = Short(currency ?? remote.Currency, 3).ToUpperInvariant(),
                SourceKey = sourceKey
            });
        }
    }

    private async Task MergeMarketplaceInvoiceState(ShipmentPackage package, RemotePackage remotePackage, CancellationToken cancellationToken)
    {
        var observedAt = timeProvider.GetUtcNow();
        var observation = remotePackage.Invoice;
        var incomingStatus = MarketplaceInvoiceStatePolicy.FromRemote(
            observation?.RawStatus,
            remotePackage.RawStatus,
            observation?.InvoiceNumber,
            observation?.InvoiceUrl);
        if (!MarketplaceInvoiceStatePolicy.ShouldApply(
                package.MarketplaceInvoiceStatus,
                package.MarketplaceInvoiceSourceUpdatedAt,
                package.MarketplaceInvoiceObservedAt,
                incomingStatus,
                observation?.SourceUpdatedAt,
                observedAt)) return;

        package.MarketplaceInvoiceStatus = incomingStatus;
        package.MarketplaceInvoiceRawStatus = observation?.RawStatus ?? package.MarketplaceInvoiceRawStatus;
        package.MarketplaceInvoiceNumber = observation?.InvoiceNumber ?? package.MarketplaceInvoiceNumber;
        package.MarketplaceInvoiceUrl = observation?.InvoiceUrl ?? package.MarketplaceInvoiceUrl;
        package.MarketplaceInvoiceSourceUpdatedAt = observation?.SourceUpdatedAt ?? package.MarketplaceInvoiceSourceUpdatedAt;
        package.MarketplaceInvoiceObservedAt = observedAt;
        package.UpdatedAt = timeProvider.GetUtcNow();
        package.Version++;
        telemetryUpdatedCount++;

        var invoice = await db.Invoices.SingleOrDefaultAsync(x => x.TenantId == package.TenantId && x.PackageId == package.Id, cancellationToken);
        if (invoice is null) return;

        if (incomingStatus == MarketplaceInvoiceStatus.Invoiced && invoice.Status is InvoiceStatus.Submitted or InvoiceStatus.Accepted or InvoiceStatus.MarketplacePending)
        {
            invoice.Status = InvoiceStatus.Completed;
            invoice.LastErrorCode = null;
            invoice.UpdatedAt = observedAt;
            invoice.Version++;
        }
        else if (incomingStatus == MarketplaceInvoiceStatus.Rejected && invoice.Status is InvoiceStatus.Submitted or InvoiceStatus.Accepted or InvoiceStatus.MarketplacePending)
        {
            invoice.Status = InvoiceStatus.MarketplaceFailed;
            invoice.LastErrorCode = "REMOTE_INVOICE_REJECTED";
            invoice.UpdatedAt = observedAt;
            invoice.Version++;
        }
    }

    private async Task<Dictionary<string, Guid>> ResolveOrderLineVariantIds(Guid tenantId, IReadOnlyList<RemoteOrderLine> remoteLines, CancellationToken cancellationToken)
    {
        var skuKeys = remoteLines.Select(line => VariantLookupKey(line.Sku, false)).Where(key => key is not null).Select(key => key![2..]).Distinct(StringComparer.Ordinal).ToArray();
        var barcodeKeys = remoteLines.Select(line => VariantLookupKey(line.Barcode, true)).Where(key => key is not null).Select(key => key![2..]).Distinct(StringComparer.Ordinal).ToArray();
        if (skuKeys.Length == 0 && barcodeKeys.Length == 0) return new Dictionary<string, Guid>(StringComparer.Ordinal);
        var rows = await db.ProductVariants.AsNoTracking()
            .Where(x => x.TenantId == tenantId && (skuKeys.Contains(x.SkuNormalized) || barcodeKeys.Contains(x.BarcodeNormalized)))
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken);
        var result = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var skuKey = VariantLookupKey(row.SkuNormalized, false);
            var barcodeKey = VariantLookupKey(row.BarcodeNormalized, true);
            if (skuKey is not null) result.TryAdd(skuKey, row.Id);
            if (barcodeKey is not null) result.TryAdd(barcodeKey, row.Id);
        }
        return result;
    }

    private static string? VariantLookupKey(string? value, bool barcode)
    {
        var normalized = NormalizeCatalogKey(value, 160);
        return string.IsNullOrWhiteSpace(normalized) ? null : (barcode ? "b:" : "s:") + normalized;
    }

    private static string AllocationKey(Guid packageId, Guid orderLineId, string sourceEventId) => $"{packageId:D}:{orderLineId:D}:{sourceEventId}";

    private async Task ProjectOrderReservations(Guid tenantId, Guid connectionId, IEnumerable<(OrderLine Line, DateTimeOffset ModifiedAt)> sourceLines, CancellationToken cancellationToken)
    {
        var sources = sourceLines.Where(source => source.Line.VariantId is not null).GroupBy(source => source.Line.Id).Select(group => group.Last()).ToList();
        var lines = sources.Select(source => source.Line).ToList();
        if (lines.Count == 0) return;
        var inventoryPolicy = await db.ConnectionInventoryPolicies.AsNoTracking()
            .SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId, cancellationToken);
        var modifiedAtByLine = sources.ToDictionary(source => source.Line.Id, source => source.ModifiedAt);
        var variantIds = lines.Select(line => line.VariantId!.Value).Distinct().ToArray();
        var items = await db.InventoryItems
            .Where(x => x.TenantId == tenantId && x.LocationCode == "MAIN" && variantIds.Contains(x.VariantId))
            .ToListAsync(cancellationToken);
        var itemsByVariant = items.ToDictionary(x => x.VariantId);
        foreach (var item in db.InventoryItems.Local.Where(x => x.TenantId == tenantId && x.LocationCode == "MAIN" && variantIds.Contains(x.VariantId))) itemsByVariant.TryAdd(item.VariantId, item);
        var sourceIds = lines.Select(line => line.Id.ToString("D")).ToArray();
        var itemIds = itemsByVariant.Values.Select(item => item.Id).ToArray();
        var reservations = itemIds.Length == 0
            ? []
            : await db.StockReservations
                .Where(x => x.TenantId == tenantId && x.SourceType == "ORDER_LINE" && itemIds.Contains(x.InventoryItemId) && sourceIds.Contains(x.SourceId))
                .ToListAsync(cancellationToken);
        var reservationsByKey = reservations.ToDictionary(x => (x.InventoryItemId, x.SourceId), x => x);
        List<StockLedgerEntry> orderLedgerRows = itemIds.Length == 0
            ? []
            : await db.StockLedgerEntries.AsNoTracking()
                .Where(x => x.TenantId == tenantId && itemIds.Contains(x.InventoryItemId) && sourceIds.Contains(x.SourceId)
                    && (x.MovementType == "ORDER_SHIPPED"
                        || x.MovementType == "ORDER_SHIPMENT_PROGRESS"
                        || x.MovementType == "ORDER_RECEIVED"
                        || x.MovementType == "ORDER_STOCK_BASELINE"))
                .ToListAsync(cancellationToken);
        var persistedLedgerIds = orderLedgerRows.Select(x => x.Id).ToHashSet();
        orderLedgerRows.AddRange(db.StockLedgerEntries.Local.Where(x => x.TenantId == tenantId
            && itemIds.Contains(x.InventoryItemId) && sourceIds.Contains(x.SourceId) && persistedLedgerIds.Add(x.Id)
            && (x.MovementType == "ORDER_SHIPPED" || x.MovementType == "ORDER_SHIPMENT_PROGRESS"
                || x.MovementType == "ORDER_RECEIVED" || x.MovementType == "ORDER_STOCK_BASELINE")));
        var legacyShippedLedger = orderLedgerRows
            .Where(x => x.MovementType == "ORDER_SHIPPED")
            .GroupBy(x => x.SourceId)
            .ToDictionary(group => group.Key, group => Math.Max(0m, -group.Sum(x => x.QuantityDelta)), StringComparer.Ordinal);
        var shippedLedger = orderLedgerRows
            .Where(x => x.MovementType is "ORDER_SHIPPED" or "ORDER_SHIPMENT_PROGRESS")
            .GroupBy(x => x.SourceId)
            .ToDictionary(group => group.Key, group => Math.Max(0m, -group.Sum(x => x.QuantityDelta)), StringComparer.Ordinal);
        var receivedLedgerDeltas = orderLedgerRows
            .Where(x => x.MovementType == "ORDER_RECEIVED")
            .GroupBy(x => x.SourceId)
            .ToDictionary(group => group.Key, group => group.Sum(x => x.QuantityDelta), StringComparer.Ordinal);
        var stockBaselines = orderLedgerRows
            .Where(x => x.MovementType == "ORDER_STOCK_BASELINE")
            .GroupBy(x => x.SourceId)
            .ToDictionary(group => group.Key, group => group.Sum(x => x.QuantityDelta), StringComparer.Ordinal);
        var now = timeProvider.GetUtcNow();
        var outbox = new Dictionary<string, (Guid VariantId, string EventId)>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            var variantId = line.VariantId!.Value;
            if (!itemsByVariant.TryGetValue(variantId, out var item)) continue;
            var sourceId = line.Id.ToString("D");
            reservationsByKey.TryGetValue((item.Id, sourceId), out var reservation);
            var current = reservation is { Status: ReservationStatus.Active } ? reservation.Quantity : 0m;
            var targetShipped = Math.Min(
                Math.Max(0m, line.OrderedQuantity),
                Math.Max(line.ShippedQuantity, line.DeliveredQuantity));
            var consumedShipped = shippedLedger.GetValueOrDefault(sourceId);
            var shipmentDelta = Math.Max(0m, targetShipped - consumedShipped);
            var eventId = $"{line.Id:N}:{modifiedAtByLine[line.Id].ToUnixTimeMilliseconds()}";

            if (!stockBaselines.TryGetValue(sourceId, out var baselineQuantity))
            {
                baselineQuantity = OrderInventoryStockPolicy.InitialBaselineQuantity(
                    legacyShippedLedger.GetValueOrDefault(sourceId),
                    targetShipped);
                stockBaselines[sourceId] = baselineQuantity;
                db.StockLedgerEntries.Add(new StockLedgerEntry
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = tenantId,
                    InventoryItemId = item.Id,
                    MovementType = "ORDER_STOCK_BASELINE",
                    QuantityDelta = baselineQuantity,
                    SourceType = "ORDER_LINE",
                    SourceId = sourceId,
                    SourceEventId = $"baseline:{sourceId}",
                    IdempotencyKey = $"order-stock-baseline:{item.Id:N}:{sourceId}",
                    OccurredAt = modifiedAtByLine[line.Id],
                    RecordedAt = now,
                    CorrelationId = $"order:{line.OrderId:N}"
                });
            }

            var targetCommitted = Math.Max(
                OrderInventoryStockPolicy.TargetCommittedQuantity(line.OrderedQuantity, line.CancelledQuantity, targetShipped),
                consumedShipped);
            var receivedLedgerDelta = receivedLedgerDeltas.GetValueOrDefault(sourceId);
            // Physical stock leaves OnHand as soon as the order is ingested.
            // Shipment progress is tracked separately so a later fulfillment
            // update cannot deduct the same unit for a second time.
            var onHandDelta = OrderInventoryStockPolicy.OnHandDelta(baselineQuantity, receivedLedgerDelta, targetCommitted);
            var actualOnHandDelta = OrderInventoryStockPolicy.LimitToAvailableStock(
                item.OnHand, item.Available, current, onHandDelta, inventoryPolicy?.NegativeStockAllowed == true);
            if (actualOnHandDelta != 0)
            {
                item.OnHand = decimal.Round(item.OnHand + actualOnHandDelta, 4, MidpointRounding.ToEven);
                item.Available = InventoryProjection.Available(item.OnHand, item.Reserved);
                item.ProjectionVersion++;
                item.Version++;
                var committedBeforeMovement = Math.Max(0m, baselineQuantity - receivedLedgerDelta);
                var committedAfterStockDelta = Math.Max(0m, committedBeforeMovement - actualOnHandDelta);
                var stockEventId = $"{eventId}:committed:{committedBeforeMovement.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)}:{committedAfterStockDelta.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)}";
                db.StockLedgerEntries.Add(new StockLedgerEntry
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = tenantId,
                    InventoryItemId = item.Id,
                    MovementType = "ORDER_RECEIVED",
                    QuantityDelta = actualOnHandDelta,
                    SourceType = "ORDER_LINE",
                    SourceId = sourceId,
                    SourceEventId = stockEventId,
                    IdempotencyKey = $"order-stock-received:{item.Id:N}:{stockEventId}",
                    OccurredAt = modifiedAtByLine[line.Id],
                    RecordedAt = now,
                    CorrelationId = $"order:{line.OrderId:N}"
                });
                receivedLedgerDeltas[sourceId] = receivedLedgerDelta + actualOnHandDelta;
                outbox[StockProjectionOutboxPolicy.DedupKey(connectionId, variantId, item.ProjectionVersion)] = (variantId, stockEventId);
            }
            var committedAfterMovement = Math.Max(0m, baselineQuantity - receivedLedgerDeltas.GetValueOrDefault(sourceId));
            var desired = Math.Max(0m, targetCommitted - committedAfterMovement);
            if (desired > 0 && actualOnHandDelta != onHandDelta)
            {
                await RecordIssue(
                    tenantId,
                    $"stock-order-consumption:{line.Id:N}",
                    "STOCK_ORDER_CONSUMPTION_BLOCKED",
                    "Sipariş stoğu kısmen düşürüldü; kalan miktar rezervasyonda tutuluyor çünkü fiziksel stok yetersiz.",
                    cancellationToken);
            }

            if (current != desired)
            {
                if (reservation is null && desired > 0)
                {
                    reservation = new StockReservation { Id = Guid.CreateVersion7(), TenantId = tenantId, InventoryItemId = item.Id, SourceType = "ORDER_LINE", SourceId = sourceId, Quantity = desired, Status = ReservationStatus.Active, Version = 1 };
                    db.StockReservations.Add(reservation);
                    reservationsByKey[(item.Id, sourceId)] = reservation;
                }
                else if (reservation is not null)
                {
                    if (desired == 0) { reservation.Status = ReservationStatus.Released; reservation.ReleasedAt = now; }
                    else { reservation.Quantity = desired; reservation.Status = ReservationStatus.Active; reservation.ReleasedAt = null; }
                    reservation.Version++;
                }
                var delta = desired - current;
                item.Reserved = Math.Max(0, item.Reserved + delta);
                item.Available = InventoryProjection.Available(item.OnHand, item.Reserved);
                item.ProjectionVersion++;
                item.Version++;
                var reservationEventId = $"{eventId}:{desired.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)}";
                db.StockLedgerEntries.Add(new StockLedgerEntry { Id = Guid.CreateVersion7(), TenantId = tenantId, InventoryItemId = item.Id, MovementType = delta > 0 ? "ORDER_RESERVED" : "ORDER_RESERVATION_RELEASED", QuantityDelta = -delta, SourceType = "ORDER_LINE", SourceId = sourceId, SourceEventId = reservationEventId, IdempotencyKey = $"order-reservation:{reservationEventId}", OccurredAt = modifiedAtByLine[line.Id], RecordedAt = now, CorrelationId = $"order:{line.OrderId:N}" });
                outbox[StockProjectionOutboxPolicy.DedupKey(connectionId, variantId, item.ProjectionVersion)] = (variantId, reservationEventId);
            }

            if (shipmentDelta > 0)
            {
                var shipmentEventId = $"{line.Id:N}:shipment-progress:{targetShipped.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)}";
                db.StockLedgerEntries.Add(new StockLedgerEntry
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = tenantId,
                    InventoryItemId = item.Id,
                    MovementType = "ORDER_SHIPMENT_PROGRESS",
                    QuantityDelta = -shipmentDelta,
                    SourceType = "ORDER_LINE",
                    SourceId = sourceId,
                    SourceEventId = shipmentEventId,
                    IdempotencyKey = $"order-shipment-progress:{item.Id:N}:{shipmentEventId}",
                    OccurredAt = modifiedAtByLine[line.Id],
                    RecordedAt = now,
                    CorrelationId = $"order:{line.OrderId:N}"
                });
                shippedLedger[sourceId] = consumedShipped + shipmentDelta;
            }
        }
        if (outbox.Count == 0) return;
        var dedupKeys = outbox.Keys.ToArray();
        var existingDedupKeys = await db.IntegrationJobs.AsNoTracking()
            .Where(x => x.TenantId == tenantId && dedupKeys.Contains(x.JobDedupKey))
            .Select(x => x.JobDedupKey)
            .ToHashSetAsync(StringComparer.Ordinal, cancellationToken);
        foreach (var (dedup, details) in outbox)
        {
            if (existingDedupKeys.Contains(dedup) || db.IntegrationJobs.Local.Any(x => x.TenantId == tenantId && x.JobDedupKey == dedup)) continue;
            var payload = JsonSerializer.Serialize(new { variantId = details.VariantId, sourceEventId = details.EventId });
            db.IntegrationJobs.Add(new IntegrationJob { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = connectionId, JobType = MarketplaceJobTypes.StockProjectionDispatch, PayloadJson = payload, PayloadVersion = 1, PayloadHash = Hash(payload), JobDedupKey = dedup, EffectIdempotencyKey = dedup, Priority = 1, Status = JobStatus.Pending, AvailableAt = now, MaxAttempts = 10, CorrelationId = $"stock:{details.VariantId:N}", CreatedAt = now, Version = 1 });
        }
    }

    private async Task<JobExecutionResult> DispatchStockProjection(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, CancellationToken cancellationToken)
    {
        Guid variantId;
        try { using var payload = JsonDocument.Parse(payloadJson); variantId = payload.RootElement.GetProperty("variantId").GetGuid(); }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException) { return JobExecutionResult.Blocked("STOCK_PROJECTION_PAYLOAD_INVALID", "Stok projection işi geçersiz payload içeriyor."); }
        var connection = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == connectionId, cancellationToken);
        if (connection is null) return JobExecutionResult.Blocked("CONNECTION_NOT_FOUND", "Bağlantı bulunamadı.");
        // Stock projection is an automatic write path. Stage manual writes remain
        // available, but an automatic projection must never enqueue a remote write
        // while the explicit external-write switch is off.
        if (!WritesEnabled(connection.SettingsJson)) return JobExecutionResult.Success();
        if (!await ExternalWritePolicyEnabledAsync(tenantId, connectionId, MarketplaceExternalWritePolicies.Stock, cancellationToken)) return JobExecutionResult.Success();
        if (!await db.ChannelOffers.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.VariantId == variantId && x.Status == "ACTIVE", cancellationToken)) return JobExecutionResult.Success();
        var build = await new PriceInventoryComposer(db).BuildAsync(tenantId, connectionId, cancellationToken, variantId);
        if (!build.Succeeded) return JobExecutionResult.Blocked(build.Error!.Code, build.Error.Message);
        var draft = build.Value!;
        var dedup = PriceInventoryOutboxPolicy.DedupKey(connectionId, draft.Lines);
        if (await db.IntegrationJobs.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.JobType == MarketplaceJobTypes.PriceInventorySync && x.JobDedupKey == dedup, cancellationToken)) return JobExecutionResult.Success();
        var id = Guid.CreateVersion7(); var now = timeProvider.GetUtcNow();
        var jobPayload = JsonSerializer.Serialize(new PriceInventoryJobPayload(id, connectionId, "SUBMIT", draft.PayloadHash, draft.PayloadJson, draft.Lines, null, null, variantId));
        var writeDelay = await ExternalWriteDelaySecondsAsync(tenantId, connectionId, MarketplaceExternalWritePolicies.Stock, cancellationToken);
        db.IntegrationJobs.Add(new IntegrationJob { Id = id, TenantId = tenantId, ConnectionId = connectionId, JobType = MarketplaceJobTypes.PriceInventorySync, PayloadJson = jobPayload, PayloadVersion = 1, PayloadHash = Hash(jobPayload), JobDedupKey = dedup, EffectIdempotencyKey = dedup, Priority = 1, Status = JobStatus.Pending, AvailableAt = now.AddSeconds(writeDelay), MaxAttempts = 10, CorrelationId = correlationId, CreatedAt = now, Version = 1 });
        await db.SaveChangesAsync(cancellationToken);
        return JobExecutionResult.Success();
    }

    private bool WritesEnabled(string settingsJson)
    {
        if (!configuration.GetValue<bool>("FeatureFlags:ExternalWrites")) return false;
        try
        {
            using var document = JsonDocument.Parse(settingsJson);
            return document.RootElement.TryGetProperty("ExternalWritesEnabled", out var enabled) && enabled.ValueKind == JsonValueKind.True;
        }
        catch (JsonException) { return false; }
    }

    private async Task<bool> ExternalWriteMasterAllowedAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken)
    {
        var connection = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == connectionId, cancellationToken);
        return connection is not null && (string.Equals(connection.Environment, "STAGE", StringComparison.OrdinalIgnoreCase) || WritesEnabled(connection.SettingsJson));
    }

    private async Task<bool> HasHepsiburadaPublicationEvidenceAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken)
    {
        return await HasHepsiburadaWriteEvidenceAsync(tenantId, connectionId, cancellationToken, MarketplaceCapabilities.ProductWrite, MarketplaceCapabilities.PriceWrite, MarketplaceCapabilities.InventoryWrite);
    }

    private async Task<bool> HasHepsiburadaWriteEvidenceAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken, params string[] required)
    {
        var connection = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == connectionId && x.PlatformCode == "HEPSIBURADA", cancellationToken);
        if (connection is null || required.Length == 0) return false;
        var capabilities = await db.PlatformCapabilities.AsNoTracking().Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && required.Contains(x.Code)).ToListAsync(cancellationToken);
        return required.All(code => CapabilityEvidencePolicy.IsVerifiedWriteCapability(capabilities.SingleOrDefault(x => x.Code == code), connection, code));
    }

    private async Task<bool> ExternalWriteAllowedAsync(Guid tenantId, Guid connectionId, string resourceType, CancellationToken cancellationToken)
    {
        var connection = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == connectionId, cancellationToken);
        if (connection is null) return false;
        if (!string.Equals(connection.Environment, "STAGE", StringComparison.OrdinalIgnoreCase) && !WritesEnabled(connection.SettingsJson)) return false;
        return await ExternalWritePolicyEnabledAsync(tenantId, connectionId, resourceType, cancellationToken);
    }

    private async Task<bool> ExternalWritePolicyEnabledAsync(Guid tenantId, Guid connectionId, string resourceType, CancellationToken cancellationToken)
    {
        var policy = await db.ConnectionSyncPolicies.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == resourceType, cancellationToken);
        return policy is null || policy.Enabled;
    }

    private async Task<int> ExternalWriteDelaySecondsAsync(Guid tenantId, Guid connectionId, string resourceType, CancellationToken cancellationToken)
    {
        var policy = await db.ConnectionSyncPolicies.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == resourceType, cancellationToken);
        return policy is null ? 0 : Math.Clamp(policy.IntervalSeconds, 0, 86_400);
    }

    // Trendyol's claims feed is date-bounded for the initial scan. Invalidating
    // the previous all-time cursor makes the next read start at three months.
    private const string ReturnSyncStateVersion = "returns-v10";
    private sealed record ReturnSyncState(string Version, int StoreFrontIndex, int Page, bool Full = true, DateTimeOffset? StartAt = null, DateTimeOffset? EndAt = null);
    private const string HepsiburadaReturnSyncStateVersion = "hepsiburada-returns-v1";
    private static readonly string[] HepsiburadaClaimStatuses = ["NewRequest", "AwaitingAction", "InDispute", "Accepted", "Rejected", "Refunded", "Cancelled", "AwaitingPreApproval"];
    private sealed record HepsiburadaReturnSyncState(string Version, int StatusIndex, int Offset);

    private async Task<bool> SyncReturns(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, CancellationToken cancellationToken)
    {
        var platformCode = await db.PlatformConnections.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.Id == connectionId)
            .Select(x => x.PlatformCode)
            .SingleOrDefaultAsync(cancellationToken);
        if (string.Equals(platformCode, "HEPSIBURADA", StringComparison.OrdinalIgnoreCase))
            return await SyncHepsiburadaReturns(tenantId, connectionId, correlationId, cancellationToken);

        var cursor = await Cursor(tenantId, connectionId, "RETURNS", cancellationToken);
        var configuredOverlapSeconds = await db.ConnectionSyncPolicies.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == "RETURNS")
            .Select(x => (int?)x.OverlapSeconds)
            .SingleOrDefaultAsync(cancellationToken) ?? 900;
        var overlap = TimeSpan.FromSeconds(Math.Clamp(configuredOverlapSeconds, 60, 86_400));
        // A failed sync can leave a valid-looking watermark even though the
        // remote read never completed (for example after an auth outage). The
        // next successful run must backfill the bounded baseline instead of
        // advancing from that stale watermark and silently skipping older
        // claims.
        var retryAfterFailure = cursor.ConsecutiveFailureCount > 0 || !string.IsNullOrWhiteSpace(cursor.LastError);
        var state = ReadReturnSyncState(cursor, timeProvider.GetUtcNow(), overlap, ReadBoolean(payloadJson, "forceFull") || retryAfterFailure);
        var productSnapshots = new Dictionary<string, string?>(StringComparer.Ordinal);
        do
        {
            var storefront = TrendyolReadStorefronts.ReturnCodes[state.StoreFrontIndex];
            // getClaims startDate/endDate are claim-creation filters, while the
            // local cursor watermark is lastModifiedDate. Reusing that watermark
            // as startDate can silently skip claims, so each read starts from page 0
            // and remains idempotent at the local claim key.
            var window = state.Full || state.StartAt is null || state.EndAt is null
                ? new ReturnPollWindow(null, null, storefront)
                : new ReturnPollWindow(state.StartAt, state.EndAt, storefront);
            TrackRequest();
            var result = await returns.PollAsync(Context(tenantId, connectionId, correlationId, $"return-sync:{storefront}:{state.Page}"), window, new(state.Page.ToString(), 50), cancellationToken);
            if (!result.IsSuccess && state.StoreFrontIndex > 0 && state.Page == 0 && result.Error?.HttpStatus is 400 or 404)
            {
                if (state.StoreFrontIndex + 1 < TrendyolReadStorefronts.ReturnCodes.Length)
                {
                    state = state with { StoreFrontIndex = state.StoreFrontIndex + 1, Page = 0 };
                    cursor.OpaqueCursor = SerializeReturnSyncState(state);
                    cursor.Version++;
                    await db.SaveChangesAsync(cancellationToken);
                    continue;
                }
                cursor.OpaqueCursor = null;
                cursor.LastModifiedWatermark = timeProvider.GetUtcNow();
                cursor.Version++;
                await db.SaveChangesAsync(cancellationToken);
                break;
            }
            if (!result.IsSuccess) { TrackResultFailure(result.Error); throw JobProcessingException.FromAdapter(result.Error!); }
            foreach (var _ in result.Value!.Items) TrackReceived();
            foreach (var claim in result.Value!.Items) await UpsertReturn(tenantId, connectionId, correlationId, claim, productSnapshots, cancellationToken);
            if (result.Value.HasMore)
            {
                var nextPage = int.TryParse(result.Value.NextCursor, out var parsedPage) ? parsedPage : state.Page + 1;
                state = state with { Page = nextPage };
            }
            else if (state.StoreFrontIndex + 1 < TrendyolReadStorefronts.ReturnCodes.Length)
            {
                state = state with { StoreFrontIndex = state.StoreFrontIndex + 1, Page = 0 };
            }
            else
            {
                cursor.OpaqueCursor = null;
                cursor.LastModifiedWatermark = timeProvider.GetUtcNow();
                cursor.Version++;
                await db.SaveChangesAsync(cancellationToken);
                break;
            }
            cursor.OpaqueCursor = SerializeReturnSyncState(state);
            cursor.Version++;
            await db.SaveChangesAsync(cancellationToken);
        } while (!cancellationToken.IsCancellationRequested);
        return true;
    }

    private async Task<bool> SyncHepsiburadaReturns(Guid tenantId, Guid connectionId, string correlationId, CancellationToken cancellationToken)
    {
        var cursor = await Cursor(tenantId, connectionId, "RETURNS", cancellationToken);
        var state = ReadHepsiburadaReturnSyncState(cursor.OpaqueCursor);
        var productSnapshots = new Dictionary<string, string?>(StringComparer.Ordinal);
        while (!cancellationToken.IsCancellationRequested)
        {
            var status = HepsiburadaClaimStatuses[state.StatusIndex];
            TrackRequest();
            var result = await returns.PollAsync(
                Context(tenantId, connectionId, correlationId, $"hepsiburada-return-sync:{status}:{state.Offset}"),
                new ReturnPollWindow(null, null, Status: status),
                new(state.Offset.ToString(CultureInfo.InvariantCulture), 100),
                cancellationToken);
            if (!result.IsSuccess)
            {
                TrackResultFailure(result.Error);
                throw JobProcessingException.FromAdapter(result.Error!);
            }

            var pageResult = result.Value!;
            foreach (var issue in pageResult.Issues ?? [])
            {
                TrackSkipped();
                await RecordIssue(tenantId, $"hepsiburada-return:{connectionId}:{status}:{issue.Identity}", issue.Code, issue.Message, cancellationToken);
            }
            foreach (var claim in pageResult.Items)
            {
                TrackReceived();
                await ResolveIssue(tenantId, $"hepsiburada-return:{connectionId}:{status}:{claim.ExternalClaimId}", cancellationToken);
                await UpsertReturn(tenantId, connectionId, correlationId, claim, productSnapshots, cancellationToken);
            }

            if (pageResult.HasMore)
            {
                var nextOffset = int.TryParse(pageResult.NextCursor, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedOffset)
                    ? Math.Max(state.Offset + 1, parsedOffset)
                    : state.Offset + Math.Max(1, pageResult.Items.Count + (pageResult.Issues?.Count ?? 0));
                state = state with { Offset = nextOffset };
            }
            else if (state.StatusIndex + 1 < HepsiburadaClaimStatuses.Length)
            {
                state = new(HepsiburadaReturnSyncStateVersion, state.StatusIndex + 1, 0);
            }
            else
            {
                cursor.OpaqueCursor = null;
                cursor.LastModifiedWatermark = timeProvider.GetUtcNow();
                cursor.Version++;
                await db.SaveChangesAsync(cancellationToken);
                break;
            }

            cursor.OpaqueCursor = JsonSerializer.Serialize(state);
            cursor.Version++;
            await db.SaveChangesAsync(cancellationToken);
        }
        return true;
    }

    private static HepsiburadaReturnSyncState ReadHepsiburadaReturnSyncState(string? opaqueCursor)
    {
        if (!string.IsNullOrWhiteSpace(opaqueCursor))
        {
            try
            {
                var state = JsonSerializer.Deserialize<HepsiburadaReturnSyncState>(opaqueCursor);
                if (state is { Version: HepsiburadaReturnSyncStateVersion, StatusIndex: >= 0, Offset: >= 0 }
                    && state.StatusIndex < HepsiburadaClaimStatuses.Length)
                    return state;
            }
            catch (JsonException) { }
        }
        return new(HepsiburadaReturnSyncStateVersion, 0, 0);
    }

    private async Task<bool> SyncOpenReturns(Guid tenantId, Guid connectionId, string correlationId, CancellationToken cancellationToken)
    {
        var batchSize = Math.Clamp(configuration.GetValue("MarketplaceSync:ReturnLifecycle:BatchSize", 25), 1, 100);
        var openClaims = await db.ReturnClaims.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId
                && x.Status != ReturnClaimStatus.Completed && x.Status != ReturnClaimStatus.Cancelled)
            // Repair incomplete return-cargo projections before re-reading
            // claims that already have both return-cargo fields populated.
            .OrderBy(x => x.CargoProviderName == null || x.CargoTrackingNumber == null ? 0 : 1)
            .ThenBy(x => x.UpdatedAt)
            .ThenBy(x => x.Id)
            .Select(x => x.ExternalClaimId)
            .Take(batchSize)
            .ToListAsync(cancellationToken);
        if (openClaims.Count == 0) return true;

        var productSnapshots = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var externalClaimId in openClaims)
        {
            TrackRequest();
            var result = await returns.GetAsync(
                Context(tenantId, connectionId, correlationId, $"return-lifecycle:{externalClaimId}"),
                externalClaimId,
                cancellationToken);
            if (!result.IsSuccess)
            {
                if (result.Error?.Class == AdapterErrorClass.NotFound)
                {
                    await RecordIssue(tenantId, $"return-lifecycle:{connectionId}:{externalClaimId}", "REMOTE_RETURN_NOT_FOUND", "Açık iade kaydı Trendyol'da ClaimId ile bulunamadı; yerel durum korunarak incelemeye alındı.", cancellationToken);
                    await db.SaveChangesAsync(cancellationToken);
                    continue;
                }
                TrackResultFailure(result.Error);
                throw JobProcessingException.FromAdapter(result.Error!);
            }
            TrackReceived();

            await UpsertReturn(tenantId, connectionId, correlationId, result.Value!, productSnapshots, cancellationToken);
            await ResolveIssue(tenantId, $"return-lifecycle:{connectionId}:{externalClaimId}", cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }
        return true;
    }

    private async Task<bool> ReconcileReturns(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, CancellationToken cancellationToken)
    {
        var lookbackDays = ReadBoundedInt(payloadJson, "lookbackDays", 3, 1, 90);
        var end = timeProvider.GetUtcNow();
        var productSnapshots = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var storefront in TrendyolReadStorefronts.ReturnCodes)
        {
            var page = 0;
            do
            {
                TrackRequest();
                var result = await returns.PollAsync(Context(tenantId, connectionId, correlationId, $"return-reconcile:{lookbackDays}:{storefront}:{page}"), new(end.AddDays(-lookbackDays), end, storefront), new(page.ToString(), 50), cancellationToken);
                if (!result.IsSuccess) { TrackResultFailure(result.Error); throw JobProcessingException.FromAdapter(result.Error!); }
                foreach (var _ in result.Value!.Items) TrackReceived();
                foreach (var claim in result.Value!.Items) await UpsertReturn(tenantId, connectionId, correlationId, claim, productSnapshots, cancellationToken);
                if (!result.Value.HasMore) break;
                page = int.TryParse(result.Value.NextCursor, out var parsed) ? parsed : page + 1;
            } while (!cancellationToken.IsCancellationRequested);
        }
        return true;
    }

    private async Task<bool> ReconcileStock(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, CancellationToken cancellationToken)
    {
        var lookbackHours = ReadBoundedInt(payloadJson, "lookbackHours", 1, 1, 24 * 30);
        var changedAfter = timeProvider.GetUtcNow().AddHours(-lookbackHours);
        var variants = await (from offer in db.ChannelOffers.AsNoTracking()
                              join item in db.InventoryItems.AsNoTracking()
                                  on new { offer.TenantId, offer.VariantId } equals new { item.TenantId, item.VariantId }
                              where offer.TenantId == tenantId && offer.ConnectionId == connectionId && offer.Status == "ACTIVE"
                                  && (offer.LastStockProjectionVersion != item.ProjectionVersion || item.ReconciledAt == null || item.ReconciledAt < changedAfter)
                              orderby item.ReconciledAt
                              select new { item.Id, item.VariantId })
            .Take(500)
            .ToListAsync(cancellationToken);
        foreach (var candidate in variants)
        {
            var result = await DispatchStockProjection(tenantId, connectionId, JsonSerializer.Serialize(new { variantId = candidate.VariantId }), correlationId, cancellationToken);
            if (!result.Succeeded)
            {
                if (result.Kind == JobCompletionKind.Retry)
                    throw new JobProcessingException(result);
                return false;
            }

            // ReconciledAt is an observation watermark, not an enqueue
            // timestamp. It is updated only when a remote catalog read
            // observes stock; a disabled, blocked, or merely queued write must
            // remain visible as stale until that observation exists.
        }
        return true;
    }

    private static int ReadBoundedInt(string payloadJson, string propertyName, int fallback, int minimum, int maximum)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            return document.RootElement.TryGetProperty(propertyName, out var value) && value.TryGetInt32(out var parsed)
                ? Math.Clamp(parsed, minimum, maximum)
                : fallback;
        }
        catch (JsonException) { return fallback; }
    }

    private static bool ReadBoolean(string payloadJson, string propertyName)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            return document.RootElement.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.True;
        }
        catch (JsonException) { return false; }
    }

    private static bool ReadBooleanOrDefault(string payloadJson, string propertyName, bool fallback)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            return document.RootElement.TryGetProperty(propertyName, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? value.GetBoolean()
                : fallback;
        }
        catch (JsonException) { return fallback; }
    }

    private static string? ReadText(string payloadJson, string propertyName)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            return document.RootElement.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException) { return null; }
    }

    private static ProductReadFilter ProductImportFilter(DateTimeOffset? modifiedAfter, string? lookup, bool includePendingApproval)
    {
        var value = lookup?.Trim();
        if (string.IsNullOrWhiteSpace(value)) return new(modifiedAfter, IncludePendingApproval: includePendingApproval);
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            const string marker = "-p-";
            var markerIndex = uri.AbsolutePath.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex >= 0)
            {
                var contentId = uri.AbsolutePath[(markerIndex + marker.Length)..].Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(contentId)) return new(null, ContentId: contentId, ProductUrl: value, IncludePendingApproval: includePendingApproval);
            }
            return new(null, ProductMainId: value, ProductUrl: value, IncludePendingApproval: includePendingApproval);
        }
        return new(null, ProductMainId: value, IncludePendingApproval: includePendingApproval);
    }

    private static ReturnSyncState ReadReturnSyncState(SyncCursor cursor, DateTimeOffset now, TimeSpan overlap, bool forceFull = false)
    {
        if (forceFull) return InitialReturnWindow(now);
        if (!string.IsNullOrWhiteSpace(cursor.OpaqueCursor))
        {
            try
            {
                var state = JsonSerializer.Deserialize<ReturnSyncState>(cursor.OpaqueCursor);
                if (state is { Version: ReturnSyncStateVersion, StoreFrontIndex: >= 0, Page: >= 0 } && state.StoreFrontIndex < TrendyolReadStorefronts.ReturnCodes.Length) return state;
                if (int.TryParse(cursor.OpaqueCursor, out var oldPage) && oldPage >= 0) return InitialReturnWindow(now) with { Page = oldPage };
            }
            catch (JsonException) { }
        }
        var watermark = cursor.LastModifiedWatermark ?? cursor.LastSuccessAt;
        return watermark is null
            ? InitialReturnWindow(now)
            : new(ReturnSyncStateVersion, 0, 0, false, watermark.Value.Subtract(overlap), now);
    }

    private static string SerializeReturnSyncState(ReturnSyncState state) => JsonSerializer.Serialize(state);
    private static ReturnSyncState InitialReturnWindow(DateTimeOffset now) => new(ReturnSyncStateVersion, 0, 0, false, now.AddMonths(-3), now);

    private async Task UpsertReturn(Guid tenantId, Guid connectionId, string correlationId, RemoteReturnClaim remote, Dictionary<string, string?> productSnapshots, CancellationToken cancellationToken)
    {
        var order = await db.Orders.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && (x.ExternalOrderId == remote.ExternalOrderId || x.OrderNumber == remote.ExternalOrderId), cancellationToken);
        if (order is null)
        {
            // Claims carry order-line identity even when the order falls
            // outside the order API history window. Reconstruct a minimal
            // local read model from the platform's own claim contract rather
            // than issuing a doomed remote lookup per claim.
            var platformCode = await db.PlatformConnections.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.Id == connectionId)
                .Select(x => x.PlatformCode)
                .SingleOrDefaultAsync(cancellationToken);
            var claimOrder = platformCode == "HEPSIBURADA"
                ? HepsiburadaJsonMapper.OrderFromReturnClaim(remote.RawJson)
                : TrendyolJsonMapper.OrderFromReturnClaim(remote.RawJson);
            if (claimOrder is not null)
            {
                // The claim already contains the product snapshot. Avoid
                // remote product lookups during a historical return scan.
                await UpsertOrder(tenantId, connectionId, claimOrder, cancellationToken);
                await ResolveIssue(tenantId, $"return-order:{connectionId}:{remote.ExternalOrderId}", cancellationToken);
                order = await db.Orders.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && (x.ExternalOrderId == remote.ExternalOrderId || x.OrderNumber == remote.ExternalOrderId), cancellationToken);
            }
        }
        if (order is null)
        {
            await RecordIssue(tenantId, $"return-order:{connectionId}:{remote.ExternalOrderId}", "RETURN_ORDER_NOT_FOUND", "Return claim yerel order ile eşleşmedi; sessiz kayıt oluşturulmadı.", cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            return;
        }
        // A previous scan may have reconstructed the order after recording the
        // diagnostic. Resolve that stale diagnostic on the next successful read.
        await ResolveIssue(tenantId, $"return-order:{connectionId}:{remote.ExternalOrderId}", cancellationToken);
        var now = timeProvider.GetUtcNow(); var target = CanonicalReturn(remote.RawStatus, remote.CargoTrackingLink); var claim = await db.ReturnClaims.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ExternalClaimId == remote.ExternalClaimId, cancellationToken);
        var remoteCargoProvider = string.IsNullOrWhiteSpace(remote.CargoProviderName) ? null : remote.CargoProviderName.Trim();
        var remoteCargoTracking = string.IsNullOrWhiteSpace(remote.CargoTrackingNumber) ? null : remote.CargoTrackingNumber.Trim();
        if (claim is null) { claim = new ReturnClaim { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = connectionId, OrderId = order.Id, ExternalClaimId = remote.ExternalClaimId, Status = target, RawStatus = remote.RawStatus, CargoProviderName = remoteCargoProvider, CargoTrackingNumber = remoteCargoTracking, LastRemoteModifiedAt = remote.LastModifiedAt, CreatedAt = now, UpdatedAt = now, Version = 1 }; db.ReturnClaims.Add(claim); telemetryInsertedCount++; }
        else
        {
            var claimChanged = false;
            var remoteIsFresh = remote.LastModifiedAt >= claim.LastRemoteModifiedAt;
            if (remoteIsFresh && ReturnClaimStateMachine.CanTransition(claim.Status, target))
            {
                claim.Status = target;
                claim.RawStatus = remote.RawStatus;
                claim.LastRemoteModifiedAt = remote.LastModifiedAt;
                claimChanged = true;
            }
            if (remoteIsFresh)
            {
                if (!string.Equals(claim.ReasonCode, remote.ReasonCode, StringComparison.Ordinal)) { claim.ReasonCode = remote.ReasonCode; claimChanged = true; }
                if (!string.Equals(claim.ReasonText, remote.ReasonText, StringComparison.Ordinal)) { claim.ReasonText = remote.ReasonText; claimChanged = true; }
                if (claim.ActionDueAt != remote.ActionDueAt) { claim.ActionDueAt = remote.ActionDueAt; claimChanged = true; }
            }

            // Cargo may be returned by a historical/status replay with an old
            // or missing lastModifiedDate. Merge non-empty cargo independently
            // of the claim timestamp, but never replace known values with null.
            if (remoteCargoProvider is not null && !string.Equals(claim.CargoProviderName, remoteCargoProvider, StringComparison.Ordinal)) { claim.CargoProviderName = remoteCargoProvider; claimChanged = true; }
            if (remoteCargoTracking is not null && !string.Equals(claim.CargoTrackingNumber, remoteCargoTracking, StringComparison.Ordinal)) { claim.CargoTrackingNumber = remoteCargoTracking; claimChanged = true; }
            if (claimChanged) { claim.UpdatedAt = now; claim.Version++; telemetryUpdatedCount++; }
        }
        var remoteLines = remote.Lines.Count > 0 ? remote.Lines : TrendyolJsonMapper.ReturnLines(remote.RawJson);
        foreach (var remoteLine in remoteLines)
        {
            var candidateIds = new[] { remoteLine.ExternalOrderLineId }
                .Concat(remoteLine.AlternateExternalOrderLineIds ?? [])
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            OrderLine? orderLine = null;
            foreach (var candidateId in candidateIds)
            {
                orderLine = await db.OrderLines.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.OrderId == order.Id && x.ExternalLineId == candidateId, cancellationToken);
                if (orderLine is not null) break;
            }
            if (orderLine is null) continue;
            var line = await db.ReturnLines.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ClaimId == claim.Id && x.ExternalLineId == remoteLine.ExternalLineId, cancellationToken);
            if (line is null) db.ReturnLines.Add(new ReturnLine { Id = Guid.CreateVersion7(), TenantId = tenantId, ClaimId = claim.Id, OrderLineId = orderLine.Id, ExternalLineId = remoteLine.ExternalLineId, Quantity = remoteLine.Quantity }); else line.Quantity = remoteLine.Quantity;
        }
        var decision = await db.ReturnDecisions.Where(x => x.TenantId == tenantId && x.ClaimId == claim.Id && (x.Status == "PENDING" || x.Status == "SUBMITTED")).OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(cancellationToken);
        if (decision is not null)
        {
            var confirmed = decision.Action == "APPROVE" && target is ReturnClaimStatus.Approved or ReturnClaimStatus.Completed
                || decision.Action == "REJECT" && target is ReturnClaimStatus.Rejected or ReturnClaimStatus.Disputed
                || decision.Action == "PREAPPROVAL_CONFIRM" && (target is ReturnClaimStatus.AwaitingShipment or ReturnClaimStatus.InTransit
                    || target == ReturnClaimStatus.ActionRequired && !HepsiburadaReturnActionPolicy.IsAwaitingPreApproval(remote.RawStatus));
            var conflictingTerminal = decision.Action == "APPROVE" && target is ReturnClaimStatus.Rejected or ReturnClaimStatus.Cancelled
                || decision.Action == "REJECT" && target is ReturnClaimStatus.Approved or ReturnClaimStatus.Completed
                || decision.Action == "PREAPPROVAL_CONFIRM" && target is ReturnClaimStatus.Approved or ReturnClaimStatus.Rejected or ReturnClaimStatus.Cancelled;
            if (confirmed) { decision.Status = "SUCCEEDED"; decision.ErrorCode = null; decision.CompletedAt = now; }
            else if (conflictingTerminal) { decision.Status = "MANUAL_REVIEW"; decision.ErrorCode = "RETURN_ACTION_READBACK_CONFLICT"; decision.CompletedAt = now; }
            else decision.Status = "SUBMITTED";
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<bool> ShipmentAction(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, CancellationToken cancellationToken)
    {
        ShipmentActionJobPayload? payload;
        try { payload = JsonSerializer.Deserialize<ShipmentActionJobPayload>(payloadJson, JsonOptions); }
        catch (JsonException) { return false; }
        if (payload is null || payload.JobId == Guid.Empty || payload.PackageId == Guid.Empty || string.IsNullOrWhiteSpace(payload.Action)) return false;
        var job = await db.IntegrationJobs.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == payload.JobId && x.ConnectionId == connectionId && x.JobType == MarketplaceJobTypes.ShipmentAction, cancellationToken);
        var package = await db.ShipmentPackages.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == payload.PackageId && x.ConnectionId == connectionId, cancellationToken);
        if (job is null || package is null) return false;
        if (!await ExternalWriteAllowedAsync(tenantId, connectionId, MarketplaceExternalWritePolicies.Shipment, cancellationToken)) throw new JobProcessingException(JobExecutionResult.Blocked("EXTERNAL_WRITE_POLICY_DISABLED", "Kargo dış yazma akışı kapalı; pazar yeri isteği gönderilmedi."));
        var effect = await db.ExternalEffectRecords.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.EffectType == MarketplaceJobTypes.ShipmentAction && x.IdempotencyKey == job.EffectIdempotencyKey, cancellationToken);
        if (effect is not null && effect.CompletedAt is null) throw new JobProcessingException(JobExecutionResult.ManualReview("EXTERNAL_EFFECT_AMBIGUOUS", "Önceki paket aksiyonunun sonucu kesinleştirilemedi; tekrar gönderim engellendi."));
        if (effect is not null && effect.CompletedAt is not null) return true;
        effect = new ExternalEffectRecord { Id = Guid.CreateVersion7(), TenantId = tenantId, EffectType = MarketplaceJobTypes.ShipmentAction, IdempotencyKey = job.EffectIdempotencyKey, CreatedAt = timeProvider.GetUtcNow() };
        db.ExternalEffectRecords.Add(effect); await db.SaveChangesAsync(cancellationToken);
        TrackRequest();
        var result = await orders.ExecutePackageActionAsync(Context(tenantId, connectionId, correlationId, job.EffectIdempotencyKey), new(package.ExternalPackageId, payload.Action, payload.PayloadJson), cancellationToken);
        if (!result.IsSuccess)
        {
            TrackResultFailure(result.Error);
            if (IsAmbiguous(result.Error!)) throw new JobProcessingException(JobExecutionResult.ManualReview("EXTERNAL_EFFECT_AMBIGUOUS", "Paket aksiyonunun uzak tarafta uygulanıp uygulanmadığı kesinleştirilemedi.", result.Error!.RemoteRequestId));
            db.ExternalEffectRecords.Remove(effect); await db.SaveChangesAsync(cancellationToken); throw JobProcessingException.FromAdapter(result.Error!);
        }
        effect.CompletedAt = timeProvider.GetUtcNow(); await db.SaveChangesAsync(cancellationToken);
        var orderNumber = await db.Orders.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == package.OrderId).Select(x => x.OrderNumber).SingleAsync(cancellationToken);
        TrackRequest();
        var readback = await orders.GetAsync(Context(tenantId, connectionId, correlationId, $"{job.EffectIdempotencyKey}:readback"), orderNumber, cancellationToken);
        if (readback.IsSuccess) TrackReceived();
        if (readback.IsSuccess) await UpsertOrder(tenantId, connectionId, readback.Value!, cancellationToken);
        else { await RecordIssue(tenantId, $"shipment-readback:{connectionId}:{package.ExternalPackageId}:{payload.Action}", "SHIPMENT_ACTION_READBACK_PENDING", "Paket aksiyonu kabul edildi ancak anlık order read-back tamamlanamadı; planlı sync kesinleştirecek.", cancellationToken); await db.SaveChangesAsync(cancellationToken); }
        return true;
    }

    private async Task<bool> ReturnAction(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, CancellationToken cancellationToken)
    {
        try
        {
            using var payload = JsonDocument.Parse(payloadJson);
            var decisionId = payload.RootElement.GetProperty("decisionId").GetGuid();
            var selectedReturnLineIds = payload.RootElement.TryGetProperty("returnLineIds", out var selectedLinesElement) && selectedLinesElement.ValueKind == JsonValueKind.Array
                ? selectedLinesElement.EnumerateArray().Select(value => value.GetGuid()).Distinct().ToArray()
                : null;
            var decision = await db.ReturnDecisions.SingleAsync(x => x.TenantId == tenantId && x.Id == decisionId, cancellationToken);
            if (decision.Status == "SUCCEEDED") return true;
            if (decision.Status == "MANUAL_REVIEW") throw new JobProcessingException(JobExecutionResult.ManualReview(decision.ErrorCode ?? "RETURN_ACTION_REVIEW_REQUIRED", "İade kararı manuel inceleme bekliyor.", decision.ExternalOperationId));
            var claim = await db.ReturnClaims.AsNoTracking().SingleAsync(x => x.TenantId == tenantId && x.Id == decision.ClaimId && x.ConnectionId == connectionId, cancellationToken);
            if (!await ExternalWriteAllowedAsync(tenantId, connectionId, MarketplaceExternalWritePolicies.Return, cancellationToken)) throw new JobProcessingException(JobExecutionResult.Blocked("EXTERNAL_WRITE_POLICY_DISABLED", "İade dış yazma akışı kapalı; pazar yeri isteği gönderilmedi."));
            var returnLines = db.ReturnLines.AsNoTracking().Where(x => x.TenantId == tenantId && x.ClaimId == claim.Id);
            if (selectedReturnLineIds is not null) returnLines = returnLines.Where(x => selectedReturnLineIds.Contains(x.Id));
            var lineIds = await returnLines.OrderBy(x => x.Id).Select(x => x.ExternalLineId).ToListAsync(cancellationToken);
            if (lineIds.Count == 0) { decision.Status = "FAILED"; decision.ErrorCode = "RETURN_LINES_REQUIRED"; decision.CompletedAt = timeProvider.GetUtcNow(); await db.SaveChangesAsync(cancellationToken); return false; }
            var evidenceRows = await (from evidence in db.ReturnEvidence.AsNoTracking() where evidence.TenantId == tenantId && evidence.DecisionId == decisionId join asset in db.FileAssets.AsNoTracking() on new { evidence.TenantId, Id = evidence.FileAssetId } equals new { asset.TenantId, asset.Id } select asset).ToListAsync(cancellationToken);
            var evidenceFiles = new List<ReturnEvidenceFile>(); long totalBytes = 0;
            foreach (var asset in evidenceRows)
            {
                if (asset.SizeBytes <= 0 || asset.SizeBytes > 10 * 1024 * 1024 || totalBytes + asset.SizeBytes > 25 * 1024 * 1024) { decision.Status = "FAILED"; decision.ErrorCode = "RETURN_EVIDENCE_LIMIT_EXCEEDED"; decision.CompletedAt = timeProvider.GetUtcNow(); await db.SaveChangesAsync(cancellationToken); return false; }
                await using var source = await files.OpenReadAsync(tenantId, asset.RelativePath, cancellationToken); await using var buffer = new MemoryStream(); await source.CopyToAsync(buffer, cancellationToken); if (buffer.Length != asset.SizeBytes) { decision.Status = "MANUAL_REVIEW"; decision.ErrorCode = "RETURN_EVIDENCE_SIZE_MISMATCH"; decision.CompletedAt = timeProvider.GetUtcNow(); await db.SaveChangesAsync(cancellationToken); throw new JobProcessingException(JobExecutionResult.ManualReview(decision.ErrorCode, "İade kanıt dosyasının kayıtlı boyutu ile okunan içerik eşleşmedi.")); }
                var bytes = buffer.ToArray(); var checksum = Convert.ToHexString(SHA256.HashData(bytes)); if (!string.Equals(checksum, asset.Sha256, StringComparison.OrdinalIgnoreCase)) { decision.Status = "MANUAL_REVIEW"; decision.ErrorCode = "RETURN_EVIDENCE_CHECKSUM_MISMATCH"; decision.CompletedAt = timeProvider.GetUtcNow(); await db.SaveChangesAsync(cancellationToken); throw new JobProcessingException(JobExecutionResult.ManualReview(decision.ErrorCode, "İade kanıt dosyasının checksum doğrulaması başarısız oldu.")); }
                evidenceFiles.Add(new(asset.OriginalNameSafe ?? $"evidence-{asset.Id:N}", asset.MimeType, bytes)); totalBytes += bytes.LongLength;
            }
            var existingEffect = await db.ExternalEffectRecords.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.EffectType == MarketplaceJobTypes.ReturnAction && x.IdempotencyKey == decision.IdempotencyKey, cancellationToken);
            if (existingEffect is not null && existingEffect.CompletedAt is null) { decision.Status = "MANUAL_REVIEW"; decision.ErrorCode = "EXTERNAL_EFFECT_AMBIGUOUS"; decision.CompletedAt = timeProvider.GetUtcNow(); await db.SaveChangesAsync(cancellationToken); throw new JobProcessingException(JobExecutionResult.ManualReview(decision.ErrorCode, "Önceki iade aksiyonunun sonucu kesinleştirilemedi; tekrar gönderim engellendi.")); }
            if (existingEffect is not null && existingEffect.CompletedAt is not null)
            {
                decision.Status = "SUBMITTED"; decision.CompletedAt = null; await db.SaveChangesAsync(cancellationToken);
                TrackRequest();
                var readback = await returns.GetAsync(Context(tenantId, connectionId, correlationId, $"{decision.IdempotencyKey}:readback"), claim.ExternalClaimId, cancellationToken);
                if (readback.IsSuccess) TrackReceived();
                if (readback.IsSuccess) await UpsertReturn(tenantId, connectionId, correlationId, readback.Value!, new(StringComparer.Ordinal), cancellationToken);
                else { await RecordIssue(tenantId, $"return-readback:{connectionId}:{claim.ExternalClaimId}:{decision.Action}", "RETURN_ACTION_READBACK_PENDING", "İade aksiyonu daha önce kabul edildi ancak read-back tamamlanamadı; planlı return sync kesinleştirecek.", cancellationToken); await db.SaveChangesAsync(cancellationToken); }
                return true;
            }
            var effect = new ExternalEffectRecord { Id = Guid.CreateVersion7(), TenantId = tenantId, EffectType = MarketplaceJobTypes.ReturnAction, IdempotencyKey = decision.IdempotencyKey, CreatedAt = timeProvider.GetUtcNow() }; db.ExternalEffectRecords.Add(effect); await db.SaveChangesAsync(cancellationToken);
            TrackRequest();
            var result = await returns.ExecuteAsync(Context(tenantId, connectionId, correlationId, decision.IdempotencyKey), new(claim.ExternalClaimId, lineIds, decision.Action, decision.ReasonCode, decision.Explanation, evidenceFiles), cancellationToken);
            var now = timeProvider.GetUtcNow();
            if (result.IsSuccess)
            {
                TrackReceived();
                effect.CompletedAt = now; decision.Status = "SUBMITTED"; decision.ExternalOperationId = result.Value!.ExternalOperationId; decision.ErrorCode = null; decision.CompletedAt = null; await db.SaveChangesAsync(cancellationToken);
                TrackRequest();
                var readback = await returns.GetAsync(Context(tenantId, connectionId, correlationId, $"{decision.IdempotencyKey}:readback"), claim.ExternalClaimId, cancellationToken);
                if (readback.IsSuccess) TrackReceived();
                if (readback.IsSuccess) await UpsertReturn(tenantId, connectionId, correlationId, readback.Value!, new(StringComparer.Ordinal), cancellationToken);
                else { await RecordIssue(tenantId, $"return-readback:{connectionId}:{claim.ExternalClaimId}:{decision.Action}", "RETURN_ACTION_READBACK_PENDING", "İade aksiyonu kabul edildi ancak anlık read-back tamamlanamadı; planlı return sync kesinleştirecek.", cancellationToken); await db.SaveChangesAsync(cancellationToken); }
                return true;
            }
            var error = result.Error!; decision.ErrorCode = error.Code; decision.ExternalOperationId ??= error.RemoteRequestId;
            TrackResultFailure(error);
            if (IsAmbiguous(error)) { decision.Status = "MANUAL_REVIEW"; decision.CompletedAt = now; await db.SaveChangesAsync(cancellationToken); throw new JobProcessingException(JobExecutionResult.ManualReview("EXTERNAL_EFFECT_AMBIGUOUS", "İade aksiyonunun uzak tarafta uygulanıp uygulanmadığı kesinleştirilemedi.", error.RemoteRequestId)); }
            db.ExternalEffectRecords.Remove(effect); decision.Status = "FAILED"; decision.CompletedAt = now; await db.SaveChangesAsync(cancellationToken); throw JobProcessingException.FromAdapter(error);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException) { return false; }
    }

    private async Task<SyncCursor> Cursor(Guid tenantId, Guid connectionId, string resource, CancellationToken cancellationToken) { var cursor = await db.SyncCursors.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == resource, cancellationToken); if (cursor is not null) return cursor; cursor = new SyncCursor { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = connectionId, ResourceType = resource, Version = 1 }; db.SyncCursors.Add(cursor); return cursor; }
    private async Task RecordIssue(Guid tenantId, string key, string code, string summary, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var issue = db.OperationalIssues.Local.FirstOrDefault(x => x.TenantId == tenantId && x.DedupeKey == key)
            ?? await db.OperationalIssues.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.DedupeKey == key, cancellationToken);
        if (issue is null)
        {
            db.OperationalIssues.Add(new OperationalIssue { Id = Guid.CreateVersion7(), TenantId = tenantId, DedupeKey = key, Code = code, Summary = summary, Status = IssueStatus.Open, FirstSeenAt = now, LastSeenAt = now, OccurrenceCount = 1 });
            return;
        }

        issue.LastSeenAt = now;
        issue.OccurrenceCount++;
    }
    private async Task ResolveIssue(Guid tenantId, string key, CancellationToken cancellationToken) { var issue = await db.OperationalIssues.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.DedupeKey == key, cancellationToken); if (issue is not null) issue.Status = IssueStatus.Resolved; }
    private AdapterContext Context(Guid tenantId, Guid connectionId, string correlationId, string idempotency) => new(tenantId, connectionId, correlationId, idempotency, timeProvider.GetUtcNow().AddMinutes(2));
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static ReturnClaimStatus CanonicalReturn(string raw, string? cargoTrackingLink = null) => raw.ToUpperInvariant() switch { "CREATED" when !string.IsNullOrWhiteSpace(cargoTrackingLink) => ReturnClaimStatus.InTransit, "CREATED" or "NEWREQUEST" => ReturnClaimStatus.Requested, "AWAITINGPREAPPROVAL" or "WAITINGINACTION" or "AWAITINGACTION" or "INANALYSIS" or "WAITINGFRAUDCHECK" => ReturnClaimStatus.ActionRequired, "WAITINGFORSHIPMENT" => ReturnClaimStatus.AwaitingShipment, "WAITINGINCARGO" => ReturnClaimStatus.InTransit, "INTRANSIT" or "RETURNINTRANSIT" or "SHIPPED" => ReturnClaimStatus.InTransit, "ACCEPTED" => ReturnClaimStatus.Approved, "REJECTED" => ReturnClaimStatus.Rejected, "UNRESOLVED" or "INDISPUTE" => ReturnClaimStatus.Disputed, "COMPLETED" or "REFUNDED" => ReturnClaimStatus.Completed, "CANCELLED" => ReturnClaimStatus.Cancelled, _ => ReturnClaimStatus.ActionRequired };
    private static string Wire<T>(T value) where T : Enum => string.Concat(value.ToString().Select((ch, index) => char.IsUpper(ch) && index > 0 ? "_" + ch : ch.ToString())).ToUpperInvariant();
}
