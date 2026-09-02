using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.Trendyol;
using MarketplaceHub.Infrastructure.Adapters.Trendyol.Mapping;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace MarketplaceHub.Infrastructure.Persistence;

public sealed class MarketplaceJobProcessor(AppDbContext db, IConnectionPort connections, IReferenceDataPort references, IProductPort products, IInventoryPricePort inventoryPrice, IOrderPort orders, IReturnPort returns, IPrivateFileStorage files, IConfiguration configuration, TimeProvider timeProvider) : IMarketplaceJobProcessor
{
    // The payload deadline is the authoritative approval bound. The worker currently
    // applies exponential backoff, but this ceiling also keeps retry accounting from
    // becoming the earlier bound if the polling schedule is made more frequent later.
    private const int ProductApprovalReconcileMaxAttempts = (7 * 24 * 12) + 1;
    private int telemetryRequestCount;
    private int telemetryReceivedCount;
    private int telemetryChangedCount;
    private int telemetryInsertedCount;
    private int telemetryUpdatedCount;
    private int telemetrySkippedCount;
    private int telemetryFailedCount;
    private int telemetryRetryCount;
    private int telemetryRateLimitCount;

    public async Task<JobExecutionResult> ProcessAsync(Guid tenantId, Guid? connectionId, string jobType, string payloadJson, string correlationId, CancellationToken cancellationToken, Guid? jobId = null)
    {
        if (connectionId is null) return JobExecutionResult.Blocked("CONNECTION_REQUIRED", "Job requires a platform connection.");
        var connectionState = await db.PlatformConnections.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == connectionId.Value).Select(x => new { x.PlatformCode, x.Status }).SingleOrDefaultAsync(cancellationToken);
        var platform = connectionState?.PlatformCode;
        if (!ActiveIntegrationScope.Contains(platform)) return JobExecutionResult.Blocked("CONNECTION_OUT_OF_SCOPE", "Connection is not active in the current integration scope.");
        // A disabled connection may still be tested so it can be reactivated, but
        // no data sync or marketplace operation may execute while it is passive.
        if (jobType != MarketplaceJobTypes.ConnectionTest && connectionState?.Status is not ("ACTIVE" or "VERIFIED"))
            return JobExecutionResult.Blocked("CONNECTION_INACTIVE", "Bağlantı pasif olduğu için işlem çalıştırılmadı.");
        var syncLock = await MarketplaceSyncExecutionLock.TryAcquireAsync(db, connectionId.Value, jobType, cancellationToken);
        if (syncLock is null) return JobExecutionResult.Retry("SYNC_LOCK_BUSY", "Aynı Trendyol mağazası için aynı senkronizasyon akışı zaten çalışıyor.", TimeSpan.FromSeconds(5));
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
                    MarketplaceJobTypes.ConnectionTest => await TestConnection(tenantId, connectionId.Value, correlationId, cancellationToken),
                    MarketplaceJobTypes.ReferenceSync => await SyncReferences(tenantId, connectionId.Value, payloadJson, correlationId, cancellationToken),
                    MarketplaceJobTypes.OrderSync => await SyncOrders(tenantId, connectionId.Value, payloadJson, correlationId, "ORDERS_HOT", allowBaseline: false, cancellationToken),
                    MarketplaceJobTypes.OrderRecoverySync => await SyncOrders(tenantId, connectionId.Value, payloadJson, correlationId, "ORDERS_RECOVERY", allowBaseline: true, cancellationToken),
                    MarketplaceJobTypes.OrderStatusSync => await SyncOpenOrders(tenantId, connectionId.Value, correlationId, cancellationToken),
                    MarketplaceJobTypes.OrderReconciliation => await ReconcileOrders(tenantId, connectionId.Value, payloadJson, correlationId, cancellationToken),
                    MarketplaceJobTypes.OrderInvoiceReconciliation => await ReconcileOrderInvoices(tenantId, connectionId.Value, payloadJson, correlationId, cancellationToken),
                    MarketplaceJobTypes.ProductSync => await SyncProducts(tenantId, connectionId.Value, payloadJson, correlationId, jobId, cancellationToken),
                    MarketplaceJobTypes.ReturnSync => await SyncReturns(tenantId, connectionId.Value, correlationId, cancellationToken),
                    MarketplaceJobTypes.ReturnStatusSync => await SyncOpenReturns(tenantId, connectionId.Value, correlationId, cancellationToken),
                    MarketplaceJobTypes.ReturnReconciliation => await ReconcileReturns(tenantId, connectionId.Value, payloadJson, correlationId, cancellationToken),
                    MarketplaceJobTypes.StockReconciliation => await ReconcileStock(tenantId, connectionId.Value, payloadJson, correlationId, cancellationToken),
                    MarketplaceJobTypes.WebhookIngest => await IngestWebhook(tenantId, connectionId.Value, payloadJson, cancellationToken),
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
        MarketplaceJobTypes.ConnectionTest => "CONNECTION_TEST",
        MarketplaceJobTypes.ReferenceSync => "REFERENCE_DATA",
        MarketplaceJobTypes.OrderSync => "ORDERS_HOT",
        MarketplaceJobTypes.OrderRecoverySync => "ORDERS_RECOVERY",
        MarketplaceJobTypes.OrderStatusSync => "ORDER_LIFECYCLE",
        MarketplaceJobTypes.OrderReconciliation => "ORDER_RECONCILIATION",
        MarketplaceJobTypes.OrderInvoiceReconciliation => "ORDER_INVOICE_RECONCILIATION",
        MarketplaceJobTypes.ReturnSync => "RETURNS",
        MarketplaceJobTypes.ReturnStatusSync => "RETURN_LIFECYCLE",
        MarketplaceJobTypes.ReturnReconciliation => "RETURN_RECONCILIATION",
        MarketplaceJobTypes.ProductSync => "PRODUCTS",
        MarketplaceJobTypes.ProductCreate => "PRODUCT_CREATE",
        MarketplaceJobTypes.ProductApprovalReconcile => "PRODUCT_APPROVAL",
        MarketplaceJobTypes.ProductUpdate => "PRODUCT_UPDATE",
        MarketplaceJobTypes.ProductArchive => "PRODUCT_ARCHIVE",
        MarketplaceJobTypes.PriceInventorySync => "PRICE_INVENTORY",
        MarketplaceJobTypes.StockProjectionDispatch => "STOCK_PROJECTION",
        MarketplaceJobTypes.StockReconciliation => "STOCK_RECONCILIATION",
        MarketplaceJobTypes.WebhookIngest => "WEBHOOK_INGEST",
        MarketplaceJobTypes.ShipmentAction => "SHIPMENT_ACTION",
        MarketplaceJobTypes.ReturnAction => "RETURN_ACTION",
        MarketplaceJobTypes.CommonLabel => "COMMON_LABEL",
        MarketplaceJobTypes.CapabilityProbe => "CAPABILITY_PROBE",
        MarketplaceJobTypes.StageTestOrder => "STAGE_TEST_ORDER",
        _ => null
    };

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
    }

    private void TrackRequest() => telemetryRequestCount++;
    private void TrackReceived(bool changed = true)
    {
        telemetryReceivedCount++;
        if (changed) telemetryChangedCount++;
        else telemetrySkippedCount++;
    }
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

        if (string.Equals(payload.Phase, "SUBMIT", StringComparison.OrdinalIgnoreCase))
        {
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
            profile.ActualStatus = "BATCH_SUBMITTED";
            profile.LastRejectionCode = null;
            profile.Version++;
            await SetListingStatus(tenantId, connectionId, profile.Id, "BATCH_SUBMITTED", null, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            return JobExecutionResult.Retry("PRODUCT_BATCH_PENDING", "Trendyol create batch sonucu bekleniyor.", TimeSpan.FromSeconds(15), operation.ExternalOperationId);
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
            profile.ActualStatus = "BATCH_IN_PROGRESS";
            profile.Version++;
            await SetListingStatus(tenantId, connectionId, profile.Id, "BATCH_IN_PROGRESS", null, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            return JobExecutionResult.Retry("PRODUCT_BATCH_PENDING", "Trendyol create batch işlemi sürüyor.", TimeSpan.FromSeconds(15), payload.ExternalOperationId);
        }
        if (!string.Equals(operationStatus.Status, "COMPLETED", StringComparison.OrdinalIgnoreCase)) return await MarkPublicationResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "PRODUCT_BATCH_STATUS_UNKNOWN", JobExecutionResult.ManualReview("PRODUCT_BATCH_STATUS_UNKNOWN", "Batch servisi tanınmayan bir durum döndürdü.", payload.ExternalOperationId), cancellationToken);

        var listings = await db.ChannelListingVariants.Where(x => x.TenantId == tenantId && x.ProfileId == profile.Id).ToListAsync(cancellationToken);
        var listingVariantIds = listings.Select(x => x.VariantId).ToArray();
        var states = await db.MarketplaceListingStates.Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && listingVariantIds.Contains(x.VariantId)).ToDictionaryAsync(x => x.VariantId, cancellationToken);
        var lines = operationStatus.Lines.Where(x => !string.IsNullOrWhiteSpace(x.ExternalKey)).ToList();
        if (lines.Count == 0 || lines.GroupBy(x => x.ExternalKey, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1)) return await MarkPublicationResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "PRODUCT_BATCH_CONTRACT_INVALID", JobExecutionResult.ManualReview("PRODUCT_BATCH_CONTRACT_INVALID", "Tamamlanan batch sonucu eksik veya yinelenen barkod satırları içeriyor.", payload.ExternalOperationId), cancellationToken);
        var byBarcode = lines.ToDictionary(x => x.ExternalKey, StringComparer.OrdinalIgnoreCase);
        if (listings.Any(x => string.IsNullOrWhiteSpace(x.ExternalBarcode) || !byBarcode.ContainsKey(x.ExternalBarcode))) return await MarkPublicationResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "PRODUCT_BATCH_CONTRACT_INVALID", JobExecutionResult.ManualReview("PRODUCT_BATCH_CONTRACT_INVALID", "Batch sonucu gönderilen tüm barkodları içermiyor.", payload.ExternalOperationId), cancellationToken);
        if (byBarcode.Keys.Any(key => listings.All(x => !string.Equals(x.ExternalBarcode, key, StringComparison.OrdinalIgnoreCase)))) return await MarkPublicationResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "PRODUCT_BATCH_CONTRACT_INVALID", JobExecutionResult.ManualReview("PRODUCT_BATCH_CONTRACT_INVALID", "Batch sonucu bilinmeyen barkod içeriyor.", payload.ExternalOperationId), cancellationToken);

        var succeeded = 0;
        foreach (var listing in listings)
        {
            var line = byBarcode[listing.ExternalBarcode!];
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
        profile.ActualStatus = succeeded == listings.Count ? "APPROVAL_PENDING" : succeeded == 0 ? "CREATE_REJECTED" : "PARTIAL_FAILURE";
        profile.LastRejectionCode = listings.Select(x => x.RejectionCode).FirstOrDefault(x => x is not null);
        profile.Version++;
        if (succeeded > 0)
            await EnsureApprovalReconciliationJob(tenantId, connectionId, payload.ProductId, profile.Id, payload.PayloadHash, correlationId, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        if (succeeded == listings.Count) return JobExecutionResult.Success();
        return succeeded == 0
            ? JobExecutionResult.Blocked("PRODUCT_BATCH_REJECTED", "Trendyol create batch içindeki tüm varyantlar reddedildi.", payload.ExternalOperationId)
            : JobExecutionResult.Blocked("PRODUCT_BATCH_PARTIAL_FAILURE", "Trendyol create batch kısmi başarısızlıkla tamamlandı.", payload.ExternalOperationId);
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
        var approvedStatuses = remoteByBarcode.Values.Where(x => x.Status == "APPROVED").ToList();
        if (approvedStatuses.Any(x => string.IsNullOrWhiteSpace(x.ExternalProductId) || string.IsNullOrWhiteSpace(x.ExternalVariantId)))
            return await MarkApprovalResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "PRODUCT_APPROVAL_IDENTIFIERS_MISSING", JobExecutionResult.ManualReview("PRODUCT_APPROVAL_IDENTIFIERS_MISSING", "Onaylanan ürün yanıtında contentId veya variantId bulunamadı."), cancellationToken);
        var approvedContentIds = approvedStatuses.Select(x => x.ExternalProductId!).Distinct(StringComparer.Ordinal).ToList();
        if (approvedContentIds.Count > 1)
            return await MarkApprovalResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "PRODUCT_APPROVAL_CONTENT_SPLIT", JobExecutionResult.ManualReview("PRODUCT_APPROVAL_CONTENT_SPLIT", "Tek yerel ürünün varyantları birden fazla Trendyol content kimliğine ayrılmış görünüyor."), cancellationToken);
        var approvedVariantIds = approvedStatuses.Select(x => x.ExternalVariantId!).ToList();
        if (approvedVariantIds.Distinct(StringComparer.Ordinal).Count() != approvedVariantIds.Count)
            return await MarkApprovalResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "PRODUCT_APPROVAL_VARIANT_ID_DUPLICATE", JobExecutionResult.ManualReview("PRODUCT_APPROVAL_VARIANT_ID_DUPLICATE", "Trendyol onay yanıtı birden fazla barkod için aynı variantId değerini döndürdü."), cancellationToken);
        if (approvedContentIds.Count == 1)
        {
            var approvedContentId = approvedContentIds[0];
            if (await db.MarketplaceProductLinks.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ExternalId == approvedContentId && x.ProductId != payload.ProductId, cancellationToken))
                return await MarkApprovalResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "PRODUCT_APPROVAL_IDENTITY_CONFLICT", JobExecutionResult.ManualReview("PRODUCT_APPROVAL_IDENTITY_CONFLICT", "Trendyol content kimliği başka bir yerel ürünle eşleşiyor."), cancellationToken);
            var localProductLink = await db.MarketplaceProductLinks.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ProductId == payload.ProductId, cancellationToken);
            if (localProductLink is not null && !string.Equals(localProductLink.ExternalId, approvedContentId, StringComparison.Ordinal))
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
            profile.LastRejectionCode = firstCode;
        }
        else if (pending > 0)
        {
            profile.ActualStatus = live + rejected > 0 ? "APPROVAL_PARTIAL_PENDING" : "APPROVAL_PENDING";
            profile.LastRejectionCode = firstCode;
        }
        else if (live == listings.Count)
        {
            profile.ActualStatus = "LIVE";
            profile.LastRejectionCode = null;
            await MarkProductLinkPublished(tenantId, connectionId, payload.ProductId, cancellationToken);
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

    private async Task EnsureApprovalReconciliationJob(Guid tenantId, Guid connectionId, Guid productId, Guid profileId, string payloadHash, string correlationId, CancellationToken cancellationToken)
    {
        var dedup = $"product-approval:{connectionId:N}:{profileId:N}:{payloadHash}";
        if (await db.IntegrationJobs.AnyAsync(x => x.TenantId == tenantId && x.JobType == MarketplaceJobTypes.ProductApprovalReconcile && x.JobDedupKey == dedup, cancellationToken)) return;
        var now = timeProvider.GetUtcNow();
        var jobId = Guid.CreateVersion7();
        var payload = JsonSerializer.Serialize(new ProductApprovalReconciliationJobPayload(jobId, productId, profileId, payloadHash, now, now.AddDays(7)));
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

        var listings = await db.ChannelListingVariants.Where(x => x.TenantId == tenantId && x.ProfileId == profile.Id).OrderBy(x => x.Id).ToListAsync(cancellationToken);
        var variantIds = listings.Select(x => x.VariantId).ToArray();
        var states = variantIds.Length == 0 ? new Dictionary<Guid, MarketplaceListingState>() : await db.MarketplaceListingStates.Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && variantIds.Contains(x.VariantId)).ToDictionaryAsync(x => x.VariantId, cancellationToken);
        if (listings.Count == 0 || states.Count != listings.Count) return await MarkPublicationResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "PRODUCT_UPDATE_STATE_INCOMPLETE", JobExecutionResult.ManualReview("PRODUCT_UPDATE_STATE_INCOMPLETE", "Ürün güncelleme listing state kayıtları eksik."), cancellationToken);
        if (states.Values.Any(x => !string.Equals(x.PayloadHash, payload.PayloadHash, StringComparison.Ordinal))) return JobExecutionResult.Blocked("PRODUCT_UPDATE_SUPERSEDED", "Daha yeni ürün payload'ı bulundu; eski güncelleme işi uzak çağrı yapmadan durduruldu.");

        var phase = payload.Phase.Trim().ToUpperInvariant();
        if (phase.StartsWith("SUBMIT_", StringComparison.Ordinal))
        {
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
            return JobExecutionResult.Retry("PRODUCT_UPDATE_BATCH_PENDING", "Trendyol ürün güncelleme batch sonucu bekleniyor.", ProductUpdatePollDelay(operation.SubmittedAt), operation.ExternalOperationId);
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
        if (string.Equals(status.Status, "IN_PROGRESS", StringComparison.OrdinalIgnoreCase)) return JobExecutionResult.Retry("PRODUCT_UPDATE_BATCH_PENDING", "Trendyol ürün güncelleme batch sonucu bekleniyor.", ProductUpdatePollDelay(payload.SubmittedAt.Value), payload.ExternalOperationId);
        if (!string.Equals(status.Status, "COMPLETED", StringComparison.OrdinalIgnoreCase)) return await MarkPublicationResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "PRODUCT_UPDATE_BATCH_STATUS_UNKNOWN", JobExecutionResult.ManualReview("PRODUCT_UPDATE_BATCH_STATUS_UNKNOWN", "Ürün güncelleme batch servisi tanınmayan durum döndürdü.", payload.ExternalOperationId), cancellationToken);

        var failed = status.Lines.Where(x => !x.Succeeded).ToList();
        if (phase == "POLL_CONTENT" && failed.Count > 0)
            return await MarkPublicationResult(tenantId, connectionId, profile, "UPDATE_REJECTED", SafeCode(failed[0].ErrorCode) ?? "PRODUCT_UPDATE_CONTENT_REJECTED", JobExecutionResult.Blocked("PRODUCT_UPDATE_CONTENT_REJECTED", "Trendyol content güncellemesi reddedildi.", payload.ExternalOperationId), cancellationToken);
        if (phase is "POLL_UNAPPROVED" or "POLL_VARIANTS" or "POLL_DELIVERY")
        {
            if (status.Lines.Count == 0) return await MarkPublicationResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "PRODUCT_UPDATE_BATCH_CONTRACT_INVALID", JobExecutionResult.ManualReview("PRODUCT_UPDATE_BATCH_CONTRACT_INVALID", "Ürün güncelleme batch sonucu satır içermiyor.", payload.ExternalOperationId), cancellationToken);
            var byBarcode = status.Lines.Where(x => !string.IsNullOrWhiteSpace(x.ExternalKey)).GroupBy(x => x.ExternalKey, StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => x.Last(), StringComparer.OrdinalIgnoreCase);
            foreach (var listing in listings)
            {
                if (string.IsNullOrWhiteSpace(listing.ExternalBarcode) || !byBarcode.TryGetValue(listing.ExternalBarcode, out var line)) continue;
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
        var rejected = listings.Count(x => string.Equals(x.ActualStatus, "UPDATE_REJECTED", StringComparison.Ordinal));
        foreach (var listing in listings.Where(x => !string.Equals(x.ActualStatus, "UPDATE_REJECTED", StringComparison.Ordinal)))
        {
            listing.ActualStatus = "APPROVAL_PENDING"; listing.RejectionCode = null;
            if (states.TryGetValue(listing.VariantId, out var state)) { state.ActualStatus = "APPROVAL_PENDING"; state.LastRejectionCode = null; state.Version++; }
        }
        profile.ActualStatus = rejected == 0 ? "APPROVAL_PENDING" : rejected == listings.Count ? "UPDATE_REJECTED" : "UPDATE_PARTIAL_FAILURE";
        profile.LastRejectionCode = listings.Select(x => x.RejectionCode).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)); profile.Version++;
        if (rejected < listings.Count) await EnsureApprovalReconciliationJob(tenantId, connectionId, payload.ProductId, profile.Id, payload.PayloadHash, correlationId, cancellationToken);
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
            var current = await new PriceInventoryComposer(db).BuildAsync(tenantId, connectionId, cancellationToken);
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
            var submit = await inventoryPrice.PushPriceAndInventoryAsync(Context(tenantId, connectionId, correlationId, job.EffectIdempotencyKey), payload.PayloadJson, cancellationToken);
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
        if (payload.Lines.Count == 0 || payload.Lines.Count > 1000 || payload.Lines.Select(x => x.Barcode).Distinct(StringComparer.OrdinalIgnoreCase).Count() != payload.Lines.Count)
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
            if (offers[line.OfferId].PriceVersion != line.PriceVersion || inventory[line.VariantId].ProjectionVersion != line.ProjectionVersion)
                return JobExecutionResult.Blocked("PRICE_INVENTORY_SUPERSEDED", "Batch sonucu alınırken fiyat veya stok daha yeni bir sürüme geçti; eski sonuç güncel kayda uygulanmadı.");
        }
        var lineByBarcode = operation.Value.Lines.Where(x => !string.IsNullOrWhiteSpace(x.ExternalKey)).GroupBy(x => x.ExternalKey, StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => x.Last(), StringComparer.OrdinalIgnoreCase);
        var success = 0; var failed = 0;
        foreach (var line in payload.Lines)
        {
            if (!lineByBarcode.TryGetValue(line.Barcode, out var remoteLine)) return JobExecutionResult.ManualReview("PRICE_INVENTORY_BATCH_CONTRACT_INVALID", "Fiyat-stok batch sonucu tüm barkodları içermiyor.", payload.ExternalOperationId);
            var offer = offers[line.OfferId];
            if (remoteLine.Succeeded) { offer.LastPriceHash = line.PriceHash; offer.LastStockProjectionVersion = line.ProjectionVersion; offer.Version++; success++; }
            else { failed++; await RecordIssue(tenantId, $"price-inventory:{connectionId}:{line.Barcode}:{SafeCode(remoteLine.ErrorCode)}", SafeCode(remoteLine.ErrorCode) ?? "PRICE_INVENTORY_LINE_REJECTED", $"Trendyol fiyat-stok satırı reddedildi: {line.Barcode}.", cancellationToken); }
        }
        await db.SaveChangesAsync(cancellationToken);
        if (failed == 0) return JobExecutionResult.Success();
        return success == 0 ? JobExecutionResult.Blocked("PRICE_INVENTORY_REJECTED", "Trendyol fiyat-stok batch içindeki tüm satırları reddetti.", payload.ExternalOperationId) : JobExecutionResult.ManualReview("PRICE_INVENTORY_PARTIAL_FAILURE", "Trendyol fiyat-stok batch kısmi başarısızlıkla tamamlandı.", payload.ExternalOperationId);
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
        var connection = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == connectionId && x.PlatformCode == "TRENDYOL", cancellationToken);
        if (connection is null || !string.Equals(connection.Environment, "STAGE", StringComparison.OrdinalIgnoreCase) || !string.Equals(connection.ExternalStoreId, "2738", StringComparison.Ordinal)) return JobExecutionResult.Blocked("STAGE_TEST_ORDER_SCOPE_REQUIRED", "Stage test siparişi yalnız Trendyol STAGE seller 2738 kapsamındadır.");
        TrackRequest();
        var result = await orders.CreateStageTestOrderAsync(Context(tenantId, connectionId, correlationId, $"stage-test-order:{payload.JobId:N}") with { IsStageCapabilityProbe = true }, payload.Barcode, cancellationToken);
        if (!result.IsSuccess) { TrackResultFailure(result.Error); throw JobProcessingException.FromAdapter(result.Error!); }
        TrackReceived();
        db.AuditLogs.Add(new AuditLog { TenantId = tenantId, ActorUserId = payload.ActorUserId, Action = "STAGE_TEST_ORDER_CREATED", TargetType = "StageTestOrder", TargetId = result.Value!.OrderNumber, Reason = "fresh-label-write-fixture", CorrelationId = correlationId, CreatedAt = timeProvider.GetUtcNow() });
        await db.SaveChangesAsync(cancellationToken);
        return JobExecutionResult.Success();
    }

    private async Task<JobExecutionResult> CommonLabel(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, CancellationToken cancellationToken)
    {
        CommonLabelJobPayload? payload;
        try { payload = JsonSerializer.Deserialize<CommonLabelJobPayload>(payloadJson, JsonOptions); }
        catch (JsonException) { return JobExecutionResult.Blocked("COMMON_LABEL_PAYLOAD_INVALID", "Ortak etiket işi payload sözleşmesini sağlamıyor."); }
        if (payload is null || payload.JobId == Guid.Empty || payload.PackageId == Guid.Empty || payload.BoxQuantity < 1 || payload.VolumetricHeight < 0 || payload.DeadlineAt <= payload.StartedAt) return JobExecutionResult.Blocked("COMMON_LABEL_PAYLOAD_INVALID", "Ortak etiket işi zorunlu alanları eksik.");
        var job = await db.IntegrationJobs.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == payload.JobId && x.ConnectionId == connectionId && x.JobType == MarketplaceJobTypes.CommonLabel, cancellationToken);
        var package = await db.ShipmentPackages.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == payload.PackageId && x.ConnectionId == connectionId, cancellationToken);
        var attempt = await db.ShipmentDocumentAttempts.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.IdempotencyKey == (job == null ? "" : job.EffectIdempotencyKey), cancellationToken);
        if (job is null || package is null || attempt is null) return JobExecutionResult.Blocked("COMMON_LABEL_STATE_MISSING", "Ortak etiket işi, paket veya deneme kaydı bulunamadı.");
        if (string.IsNullOrWhiteSpace(package.CargoTrackingNumber)) return JobExecutionResult.Blocked("CARGO_TRACKING_REQUIRED", "Ortak etiket için kargo takip numarası gerekir.");
        if (!CommonLabelCarrierPolicy.Supports(package.CargoProviderExternalId)) return JobExecutionResult.Blocked("COMMON_LABEL_CARRIER_UNSUPPORTED", "Ortak etiket yalnız Trendyol öder Aras Kargo veya TEX gönderilerinde kullanılabilir.");
        if (timeProvider.GetUtcNow() > payload.DeadlineAt) { attempt.Status = "MANUAL_REVIEW"; attempt.ErrorCode = "COMMON_LABEL_DEADLINE_EXPIRED"; attempt.CompletedAt = timeProvider.GetUtcNow(); await db.SaveChangesAsync(cancellationToken); return JobExecutionResult.ManualReview("COMMON_LABEL_DEADLINE_EXPIRED", "Ortak etiket belirlenen pencerede hazır olmadı."); }
        var phase = payload.Phase.Trim().ToUpperInvariant();
        if (phase == "SUBMIT")
        {
            if (await db.ExternalEffectRecords.AnyAsync(x => x.TenantId == tenantId && x.EffectType == MarketplaceJobTypes.CommonLabel && x.IdempotencyKey == job.EffectIdempotencyKey, cancellationToken)) return JobExecutionResult.ManualReview("EXTERNAL_EFFECT_AMBIGUOUS", "Önceki ortak etiket oluşturma çağrısının sonucu kesinleştirilemedi.");
            var effect = new ExternalEffectRecord { Id = Guid.CreateVersion7(), TenantId = tenantId, EffectType = MarketplaceJobTypes.CommonLabel, IdempotencyKey = job.EffectIdempotencyKey, CreatedAt = timeProvider.GetUtcNow() };
            db.ExternalEffectRecords.Add(effect); await db.SaveChangesAsync(cancellationToken);
            TrackRequest();
            var create = await orders.CreateCommonLabelAsync(Context(tenantId, connectionId, correlationId, job.EffectIdempotencyKey), new(package.CargoTrackingNumber, payload.BoxQuantity, payload.VolumetricHeight), cancellationToken);
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
        var documentResult = await orders.GetCommonLabelAsync(Context(tenantId, connectionId, correlationId, $"{job.EffectIdempotencyKey}:poll"), package.CargoTrackingNumber, cancellationToken);
        if (!documentResult.IsSuccess)
        {
            TrackResultFailure(documentResult.Error);
            var error = documentResult.Error!;
            var mapped = JobExecutionResult.FromAdapterError(error);
            if (mapped.Kind == JobCompletionKind.Retry || error.Class == AdapterErrorClass.NotFound) return JobExecutionResult.Retry("COMMON_LABEL_PENDING", "Trendyol ortak etiket henüz hazır değil.", TimeSpan.FromSeconds(20), error.RemoteRequestId);
            attempt.Status = mapped.Kind == JobCompletionKind.ManualReview ? "MANUAL_REVIEW" : "FAILED"; attempt.ErrorCode = error.Code; attempt.CompletedAt = timeProvider.GetUtcNow(); await db.SaveChangesAsync(cancellationToken); return mapped;
        }
        var document = documentResult.Value!;
        if (!string.Equals(document.Format, "ZPL", StringComparison.OrdinalIgnoreCase) || document.Content.Length == 0 || document.Content.LongLength > 5 * 1024 * 1024)
        {
            attempt.Status = "MANUAL_REVIEW"; attempt.ErrorCode = "COMMON_LABEL_CONTRACT_INVALID"; attempt.CompletedAt = timeProvider.GetUtcNow();
            await db.SaveChangesAsync(cancellationToken);
            return JobExecutionResult.ManualReview("COMMON_LABEL_CONTRACT_INVALID", "Ortak etiket yalnız ZPL ve 5 MiB altı içerik olarak kabul edilir.");
        }
        var checksum = Convert.ToHexString(SHA256.HashData(document.Content));
        var existing = await db.ShipmentDocuments.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.PackageId == package.Id && x.DocumentKind == "COMMON_LABEL" && x.Checksum == checksum, cancellationToken);
        if (existing is null)
        {
            var assetId = Guid.CreateVersion7(); await using var stream = new MemoryStream(document.Content, writable: false); var stored = await files.SaveAsync(tenantId, $"{assetId:N}-common-label.zpl", "application/zpl", stream, 5 * 1024 * 1024, cancellationToken);
            var asset = new FileAsset { Id = assetId, TenantId = tenantId, Classification = "SHIPMENT_LABEL", RelativePath = stored, OriginalNameSafe = $"{package.CargoTrackingNumber}.zpl", MimeType = "application/zpl", SizeBytes = document.Content.LongLength, Sha256 = checksum, Status = "ACTIVE", CreatedAt = timeProvider.GetUtcNow() };
            db.FileAssets.Add(asset);
            var version = await db.ShipmentDocuments.Where(x => x.TenantId == tenantId && x.PackageId == package.Id && x.DocumentKind == "COMMON_LABEL").Select(x => (int?)x.DocumentVersion).MaxAsync(cancellationToken) ?? 0;
            existing = new ShipmentDocument { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = connectionId, PackageId = package.Id, FileAssetId = assetId, DocumentKind = "COMMON_LABEL", Format = "ZPL", Source = "TRENDYOL", Checksum = checksum, DocumentVersion = version + 1, CreatedAt = timeProvider.GetUtcNow() };
            db.ShipmentDocuments.Add(existing);
        }
        attempt.DocumentId = existing.Id; attempt.Status = "SUCCEEDED"; attempt.ErrorCode = null; attempt.CompletedAt = timeProvider.GetUtcNow(); await db.SaveChangesAsync(cancellationToken); return JobExecutionResult.Success();
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static bool IsAmbiguous(AdapterError error) => error.Class is AdapterErrorClass.TransientNetwork or AdapterErrorClass.Remote5xx or AdapterErrorClass.ContractViolation or AdapterErrorClass.InternalBug;
    private static bool HasItems(string payloadJson) { try { using var doc = JsonDocument.Parse(payloadJson); return doc.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array && items.GetArrayLength() > 0; } catch (JsonException) { return false; } }
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
        var items = new List<RemoteReferenceItem>();
        var visitedCursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        do
        {
            TrackRequest();
            var result = await references.ReadAsync(Context(tenantId, connectionId, correlationId, $"reference-sync:{resourceType}:{parentExternalId}:{cursor}"), new(resourceType, parentExternalId), new(cursor, 1000), cancellationToken);
            if (!result.IsSuccess) { TrackResultFailure(result.Error); throw JobProcessingException.FromAdapter(result.Error!); }
            foreach (var _ in result.Value!.Items) TrackReceived();
            items.AddRange(result.Value!.Items);
            if (items.Count > 100_000) throw new JobProcessingException(JobExecutionResult.ManualReview("REFERENCE_RESULT_LIMIT_EXCEEDED", "Referans yanıtı güvenli işleme sınırını aştı."));
            cursor = result.Value.NextCursor;
            if (!result.Value.HasMore) break;
            if (string.IsNullOrWhiteSpace(cursor) || !visitedCursors.Add(cursor)) throw new JobProcessingException(JobExecutionResult.ManualReview("REFERENCE_CURSOR_INVALID", "Referans sayfalama imleci eksik veya yinelendi."));
        } while (!cancellationToken.IsCancellationRequested);

        cancellationToken.ThrowIfCancellationRequested();
        if (items.Count == 0 && resourceType is "CATEGORIES" or "BRANDS") throw new JobProcessingException(JobExecutionResult.Blocked("REFERENCE_EMPTY_RESPONSE", $"Trendyol {resourceType} salt-okunur çağrısı boş koleksiyon döndürdü; mevcut snapshot korunuyor."));
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
        var connection = await db.PlatformConnections.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == connectionId && x.PlatformCode == "TRENDYOL", cancellationToken); if (connection is null) return false; var now = timeProvider.GetUtcNow(); connection.LastTestedAt = now;
        IConnectionPort port = connections;
        var context = Context(tenantId, connectionId, correlationId, "connection-test"); TrackRequest(); var result = await port.TestAsync(context, cancellationToken); if (!result.IsSuccess) { TrackResultFailure(result.Error); connection.LastErrorCode = result.Error!.Code; connection.Version++; await db.SaveChangesAsync(cancellationToken); throw JobProcessingException.FromAdapter(result.Error!); }
        TrackRequest(); var discovery = await port.DiscoverCapabilitiesAsync(context, cancellationToken); if (!discovery.IsSuccess) { TrackResultFailure(discovery.Error); connection.LastErrorCode = discovery.Error!.Code; connection.Version++; await db.SaveChangesAsync(cancellationToken); throw JobProcessingException.FromAdapter(discovery.Error!); }
        foreach (var _ in discovery.Value!) TrackReceived();
        foreach (var evidence in discovery.Value!)
        {
            var capability = await db.PlatformCapabilities.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.Code == evidence.Code, cancellationToken); if (capability is null) continue;
            capability.SupportLevel = string.Equals(evidence.SupportLevel, "SUPPORTED", StringComparison.Ordinal) ? CapabilitySupportLevel.Supported : CapabilitySupportLevel.Unknown; capability.SourceUrl = evidence.SourceUrl; capability.SourceVersion = evidence.SourceVersion; capability.RequiredScope = evidence.RequiredScope; capability.ConstraintsJson = evidence.ConstraintsJson; capability.EvidenceNote = evidence.EvidenceNote; capability.FixtureChecksum = evidence.FixtureChecksum; capability.VerifiedAt = evidence.VerifiedAt; capability.Version++;
        }
        connection.LastSuccessAt = now; connection.LastErrorCode = null; if (connection.Status == "DRAFT") connection.Status = "VERIFIED"; connection.Version++; await db.SaveChangesAsync(cancellationToken); return true;
    }


    private async Task<bool> SyncOrders(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, string cursorResourceType, bool allowBaseline, CancellationToken cancellationToken)
    {
        if (!configuration.GetValue("Marketplace:PersistOrderSnapshots", true))
            return true;

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
        var state = ReadOrderSyncState(cursor, now, overlap, allowBaseline, forceBaseline: allowBaseline && (full || !hasSnapshots));
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

    private async Task<bool> SyncOpenOrders(Guid tenantId, Guid connectionId, string correlationId, CancellationToken cancellationToken)
    {
        var lifecycleBatchSize = Math.Clamp(configuration.GetValue("MarketplaceSync:OrderLifecycle:BatchSize", 25), 1, 100);
        var externalOrderIds = await (from package in db.ShipmentPackages.AsNoTracking()
                                      join order in db.Orders.AsNoTracking()
                                          on new { package.TenantId, package.OrderId } equals new { order.TenantId, OrderId = order.Id }
                                      where package.TenantId == tenantId && package.ConnectionId == connectionId && package.Status != ShipmentPackageStatus.Delivered && package.Status != ShipmentPackageStatus.Cancelled && package.Status != ShipmentPackageStatus.Returned
                                      group package by order.ExternalOrderId into openOrder
                                      orderby openOrder.Min(x => x.UpdatedAt)
                                      select openOrder.Key)
            .Take(lifecycleBatchSize)
            .ToListAsync(cancellationToken);

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
        if (recoveredOrders.Count > 0) await UpsertOrders(tenantId, connectionId, recoveredOrders, cancellationToken);

        var cursor = await Cursor(tenantId, connectionId, "ORDER_LIFECYCLE", cancellationToken);
        cursor.LastModifiedWatermark = timeProvider.GetUtcNow();
        cursor.Version++;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private async Task<bool> ReconcileOrders(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, CancellationToken cancellationToken)
    {
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
            await UpsertOrder(tenantId, connectionId, result.Value!, cancellationToken);
            await ResolveIssue(tenantId, $"order-reconcile:{connectionId}:{externalOrderId}", cancellationToken);
        }

        return true;
    }

    private async Task<bool> ReconcileOrderInvoices(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, CancellationToken cancellationToken)
    {
        var batchSize = ReadBoundedInt(payloadJson, "batchSize", 50, 1, 250);
        var externalOrderIds = await (from package in db.ShipmentPackages.AsNoTracking()
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
            await UpsertOrder(tenantId, connectionId, result.Value!, cancellationToken);
            await ResolveIssue(tenantId, $"order-invoice-reconciliation:{connectionId}:{externalOrderId}", cancellationToken);
        }

        return true;
    }

    private async Task<bool> SyncProducts(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, Guid? jobId, CancellationToken cancellationToken)
    {
        var connection = await db.PlatformConnections.AsNoTracking()
            .SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == connectionId && x.PlatformCode == "TRENDYOL", cancellationToken);
        // Product import is read-only on Trendyol and writes only to the local catalog.
        // Keep it restricted to operational connections and recognised environments.
        if (connection is null || connection.Environment is not ("STAGE" or "PRODUCTION") || connection.Status is not ("ACTIVE" or "VERIFIED")) return false;

        var fullScan = ReadBoolean(payloadJson, "full");
        var processedProducts = 0;
        if (jobId is { } currentJob)
            await UpdateProductSyncProgressAsync(tenantId, currentJob, 0, null, 0, fullScan ? "Trendyol kataloğu taranıyor" : "Yeni ve değişen ürünler taranıyor", cancellationToken);
        int? totalProducts = null;
        var cursor = await Cursor(tenantId, connectionId, "PRODUCTS", cancellationToken);
        if (fullScan && cursor.OpaqueCursor is not null)
        {
            cursor.OpaqueCursor = null;
            cursor.Version++;
            await db.SaveChangesAsync(cancellationToken);
        }
        var hasSnapshots = await db.MarketplaceProductLinks.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId, cancellationToken);
        var hasCategoryMappings = await db.CategoryMappings.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.Status == "VERIFIED", cancellationToken);
        // The first attribute backfill must revisit the already imported catalog. Keep
        // LastModifiedWatermark null until that full pass is complete so a retry cannot
        // accidentally switch to the incremental window halfway through the backfill.
        if (!hasCategoryMappings && cursor.OpaqueCursor is null && cursor.LastModifiedWatermark is not null)
        {
            cursor.LastModifiedWatermark = null;
            cursor.Version++;
            await db.SaveChangesAsync(cancellationToken);
        }
        DateTimeOffset? modifiedAfter = !fullScan && hasSnapshots && cursor.LastModifiedWatermark is not null ? cursor.LastModifiedWatermark.Value.AddMinutes(-2) : null;
        var categoryReferences = await EnsureReferenceSnapshot(tenantId, connectionId, "CATEGORIES", null, correlationId, cancellationToken);
        IReadOnlyList<ReferenceItem> categoryItems = categoryReferences is null
            ? []
            : await db.ReferenceItems.AsNoTracking().Where(x => x.TenantId == tenantId && x.SnapshotId == categoryReferences.Id && x.ResourceType == "CATEGORIES" && x.IsActive).ToListAsync(cancellationToken);
        var categoryContexts = new Dictionary<string, CategoryAttributeContext>(StringComparer.Ordinal);
        var nextCursor = cursor.OpaqueCursor;
        do
        {
            TrackRequest();
            var result = await products.ListCatalogAsync(
                Context(tenantId, connectionId, correlationId, $"product-sync:{nextCursor ?? "0"}"),
                new(nextCursor, 100),
                new(modifiedAfter),
                cancellationToken);
            if (!result.IsSuccess) { TrackResultFailure(result.Error); throw JobProcessingException.FromAdapter(result.Error!); }
            foreach (var _ in result.Value!.Items) TrackReceived();
            processedProducts += result.Value.Items.Count;
            totalProducts ??= result.Value.TotalCount;

            foreach (var snapshot in result.Value!.Items
                         .Where(x => !string.IsNullOrWhiteSpace(x.ExternalProductId))
                         .GroupBy(x => x.ExternalProductId, StringComparer.OrdinalIgnoreCase)
                         .Select(MergeCatalogSnapshots))
            {
                var categoryContext = categoryReferences is null
                    ? null
                    : await EnsureCategoryAttributeContext(tenantId, connectionId, categoryReferences, snapshot, categoryItems, categoryContexts, correlationId, cancellationToken);
                await UpsertCatalogProduct(tenantId, connectionId, snapshot, categoryContext, cancellationToken);
            }

            if (jobId is { } progressJob)
            {
                var percent = totalProducts is { } total && total > 0
                    ? Math.Clamp((int)Math.Floor(processedProducts * 100d / total), 0, 99)
                    : (int?)null;
                var label = totalProducts is { } totalCount && totalCount > 0
                    ? $"{processedProducts:N0} / {totalCount:N0} ürün işlendi"
                    : $"{processedProducts:N0} ürün işlendi · toplam sayı bekleniyor";
                await UpdateProductSyncProgressAsync(tenantId, progressJob, processedProducts, totalProducts, percent, label, cancellationToken);
            }

            if (result.Value.HasMore)
            {
                if (string.IsNullOrWhiteSpace(result.Value.NextCursor)) throw new InvalidOperationException("Trendyol ürün sayfası hasMore=true ancak nextPageToken boş döndü.");
                nextCursor = result.Value.NextCursor;
                cursor.OpaqueCursor = nextCursor;
                cursor.Version++;
                await db.SaveChangesAsync(cancellationToken);
            }
            else
            {
                cursor.OpaqueCursor = null;
                // Approved-products supports a modified-date filter. Keep a short
                // overlap so a variant changed while a page was being read is not lost.
                cursor.LastModifiedWatermark = timeProvider.GetUtcNow().AddSeconds(-60);
                cursor.Version++;
                if (jobId is { } completedJob)
                    await UpdateProductSyncProgressAsync(tenantId, completedJob, processedProducts, null, 100, $"{processedProducts:N0} ürün aktarımı tamamlandı", cancellationToken, keepExistingTotal: true);
                await db.SaveChangesAsync(cancellationToken);
                break;
            }
        } while (!cancellationToken.IsCancellationRequested);
        return true;
    }

    private Task<int> UpdateProductSyncProgressAsync(Guid tenantId, Guid jobId, int current, int? total, int? percent, string label, CancellationToken cancellationToken, bool keepExistingTotal = false)
    {
        var query = db.IntegrationJobs.Where(x => x.TenantId == tenantId && x.Id == jobId && x.JobType == MarketplaceJobTypes.ProductSync);
        return keepExistingTotal
            ? query.ExecuteUpdateAsync(setters => setters.SetProperty(x => x.ProgressCurrent, current).SetProperty(x => x.ProgressPercent, percent).SetProperty(x => x.ProgressLabel, label), cancellationToken)
            : query.ExecuteUpdateAsync(setters => setters.SetProperty(x => x.ProgressCurrent, current).SetProperty(x => x.ProgressTotal, total).SetProperty(x => x.ProgressPercent, percent).SetProperty(x => x.ProgressLabel, label), cancellationToken);
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
            await RecordIssue(tenantId, $"product-reference-sync:{connectionId}:{resourceType}:{scope}", exception.Result.ErrorCode ?? "REFERENCE_SYNC_FAILED", exception.Result.ErrorSummary ?? "Trendyol kategori referansı eşitlenemedi; mevcut panel kayıtları korundu.", cancellationToken);
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
        IDictionary<string, CategoryAttributeContext> cache,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var externalCategoryId = Short(snapshot.CategoryExternalId, 256);
        if (string.IsNullOrWhiteSpace(externalCategoryId)) return null;
        if (cache.TryGetValue(externalCategoryId, out var cached)) return cached;

        var categoryItem = categoryItems.FirstOrDefault(x => string.Equals(x.ExternalId, externalCategoryId, StringComparison.Ordinal));
        if (categoryItem is null)
        {
            await RecordIssue(tenantId, $"product-category-reference:{connectionId}:{externalCategoryId}", "PRODUCT_CATEGORY_REFERENCE_MISSING", "Ürünün Trendyol kategori kimliği güncel kategori snapshot'ında bulunamadı; kategori özellikleri eşlenmedi.", cancellationToken);
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
        var valuesByAttribute = new Dictionary<string, IReadOnlyList<ReferenceItem>>(StringComparer.Ordinal);
        foreach (var remoteAttribute in remoteAttributes.Where(x => x.IsRequired == true || observedNames.Contains(NormalizeCatalogKey(x.Name, 320))))
        {
            var valueSnapshot = await EnsureReferenceSnapshot(tenantId, connectionId, "ATTRIBUTE_VALUES", $"{externalCategoryId}/{remoteAttribute.ExternalId}", correlationId, cancellationToken);
            if (valueSnapshot is null) continue;
            var values = await db.ReferenceItems.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.SnapshotId == valueSnapshot.Id && x.ResourceType == "ATTRIBUTE_VALUES" && x.IsActive)
                .OrderBy(x => x.SortOrder).ThenBy(x => x.Name)
                .ToListAsync(cancellationToken);
            valuesByAttribute[remoteAttribute.ExternalId] = values;
        }

        var localCategory = await EnsureMappedCategory(tenantId, connectionId, categorySnapshot, categoryItem, cancellationToken);
        var attributes = new Dictionary<string, LocalCategoryAttribute>(StringComparer.Ordinal);
        foreach (var remoteAttribute in remoteAttributes)
        {
            var values = valuesByAttribute.TryGetValue(remoteAttribute.ExternalId, out var fetchedValues) ? fetchedValues : [];
            var localAttribute = await EnsureMappedAttribute(tenantId, connectionId, localCategory, categoryItem, attributeSnapshot, remoteAttribute, values, cancellationToken);
            attributes[NormalizeCatalogKey(remoteAttribute.Name, 320)] = localAttribute;
        }

        var result = new CategoryAttributeContext(localCategory, externalCategoryId, attributes);
        cache[externalCategoryId] = result;
        return result;
    }

    private async Task<Category> EnsureMappedCategory(Guid tenantId, Guid connectionId, ReferenceSnapshot categorySnapshot, ReferenceItem remoteCategory, CancellationToken cancellationToken)
    {
        var mapping = await db.CategoryMappings.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ScopeExternalId == categorySnapshot.ScopeExternalId && x.ExternalId == remoteCategory.ExternalId, cancellationToken);
        Category? category = mapping is null ? null : await db.Categories.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == mapping.LocalId, cancellationToken);
        var normalized = NormalizeCatalogKey(remoteCategory.Name, 160);
        category ??= db.Categories.Local.FirstOrDefault(x => x.TenantId == tenantId && x.ParentId is null && x.NormalizedName == normalized)
            ?? await db.Categories.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ParentId == null && x.NormalizedName == normalized, cancellationToken);
        if (category is null)
        {
            var now = timeProvider.GetUtcNow();
            category = new Category { Id = Guid.CreateVersion7(), TenantId = tenantId, Name = Short(remoteCategory.Name, 160), NormalizedName = normalized, Path = Short(remoteCategory.Path, 1024), Depth = 0, IsLeaf = true, IsActive = true, CreatedAt = now, UpdatedAt = now, Version = 1 };
            db.Categories.Add(category);
        }
        else
        {
            category.Name = Short(remoteCategory.Name, 160);
            category.Path = Short(remoteCategory.Path, 1024);
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
        CancellationToken cancellationToken)
    {
        var code = Short($"TRD_{remoteCategory.ExternalId}_{remoteAttribute.ExternalId}", 96);
        var dataType = values.Count > 0
            ? (remoteAttribute.AllowsMultipleValues == true ? AttributeDataType.MultiSelect : AttributeDataType.SingleSelect)
            : AttributeDataType.Text;
        var attribute = db.AttributeDefinitions.Local.FirstOrDefault(x => x.TenantId == tenantId && x.Code == code)
            ?? await db.AttributeDefinitions.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Code == code, cancellationToken);
        if (attribute is null)
        {
            attribute = new AttributeDefinition { Id = Guid.CreateVersion7(), TenantId = tenantId, Code = code, Name = Short(remoteAttribute.Name, 160), DataType = dataType, SelectionMode = dataType == AttributeDataType.MultiSelect ? "MULTI" : dataType == AttributeDataType.SingleSelect ? "SINGLE" : null, IsActive = true, CreatedAt = timeProvider.GetUtcNow(), UpdatedAt = timeProvider.GetUtcNow(), Version = 1 };
            db.AttributeDefinitions.Add(attribute);
        }
        else
        {
            attribute.Name = Short(remoteAttribute.Name, 160);
            attribute.DataType = dataType;
            attribute.SelectionMode = dataType == AttributeDataType.MultiSelect ? "MULTI" : dataType == AttributeDataType.SingleSelect ? "SINGLE" : null;
            attribute.IsActive = true;
            attribute.UpdatedAt = timeProvider.GetUtcNow();
            attribute.Version++;
        }

        var requirement = await db.CategoryAttributeRequirements.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.CategoryId == category.Id && x.AttributeId == attribute.Id, cancellationToken);
        var role = requirement?.Role == "OPTION" ? "OPTION" : "ATTRIBUTE";
        if (requirement is null)
            db.CategoryAttributeRequirements.Add(new CategoryAttributeRequirement { Id = Guid.CreateVersion7(), TenantId = tenantId, CategoryId = category.Id, AttributeId = attribute.Id, IsRequired = remoteAttribute.IsRequired == true, AllowsCustomValue = remoteAttribute.AllowsCustomValue == true, DisplayOrder = remoteAttribute.SortOrder ?? 0, Version = 1 });
        else
        {
            requirement.IsRequired = remoteAttribute.IsRequired == true;
            requirement.AllowsCustomValue = remoteAttribute.AllowsCustomValue == true;
            requirement.DisplayOrder = remoteAttribute.SortOrder ?? requirement.DisplayOrder;
            requirement.Version++;
        }

        var attributeMapping = await db.AttributeMappings.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.LocalId == attribute.Id && x.ScopeExternalId == remoteCategory.ExternalId, cancellationToken);
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

        foreach (var remoteValue in values)
        {
            var normalizedValue = NormalizeCatalogKey(remoteValue.Name, 320);
            var value = db.AttributeValues.Local.FirstOrDefault(x => x.TenantId == tenantId && x.AttributeId == attribute.Id && x.NormalizedValue == normalizedValue)
                ?? await db.AttributeValues.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.AttributeId == attribute.Id && x.NormalizedValue == normalizedValue, cancellationToken);
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

    private async Task UpsertProductAttributeAssignments(Guid tenantId, Product product, ProductVariant variant, IReadOnlyDictionary<string, string> options, CategoryAttributeContext categoryContext, CancellationToken cancellationToken)
    {
        var sortOrder = 0;
        foreach (var pair in options.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
        {
            var optionKey = NormalizeCatalogKey(pair.Key, 320);
            if (!categoryContext.Attributes.TryGetValue(optionKey, out var mapped))
            {
                sortOrder++;
                continue;
            }
            if (mapped.Role == "OPTION")
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
            if (remoteValue is not null)
            {
                valueId = await db.AttributeValues.Where(x => x.TenantId == tenantId && x.AttributeId == mapped.Definition.Id && x.NormalizedValue == valueKey).Select(x => (Guid?)x.Id).SingleOrDefaultAsync(cancellationToken);
            }
            else if (mapped.Remote.AllowsCustomValue == true)
            {
                if (mapped.Definition.DataType == AttributeDataType.Number && decimal.TryParse(pair.Value, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var number)) numberValue = number;
                else if (mapped.Definition.DataType == AttributeDataType.Boolean && bool.TryParse(pair.Value, out var boolean)) booleanValue = boolean;
                else textValue = Short(pair.Value, 320);
            }
            else
            {
                await RecordIssue(tenantId, $"product-attribute-value:{product.Id}:{variant.Id}:{mapped.Definition.Id}:{valueKey}", "PRODUCT_ATTRIBUTE_VALUE_UNMAPPED", $"Trendyol ürün özelliği '{pair.Key}: {pair.Value}' için güncel panel değeri bulunamadı; atama yapılmadı.", cancellationToken);
                sortOrder++;
                continue;
            }
            if (valueId is null && textValue is null && numberValue is null && booleanValue is null)
            {
                await RecordIssue(tenantId, $"product-attribute-value:{product.Id}:{variant.Id}:{mapped.Definition.Id}:{valueKey}", "PRODUCT_ATTRIBUTE_VALUE_UNMAPPED", $"Trendyol ürün özelliği '{pair.Key}: {pair.Value}' için panel değeri eşlenemedi; atama yapılmadı.", cancellationToken);
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
    }

    private async Task UpsertCatalogProduct(Guid tenantId, Guid connectionId, RemoteCatalogProduct snapshot, CategoryAttributeContext? categoryContext, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var externalProductId = Short(snapshot.ExternalProductId, 256);
        var remoteHash = Hash(JsonSerializer.Serialize(snapshot));
        var isNewProduct = false;
        var link = await db.MarketplaceProductLinks.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ExternalId == externalProductId, cancellationToken);
        Product? product = link is null
            ? null
            : await db.Products.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == link.ProductId, cancellationToken);
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

        if (!isNewProduct && link is not null && string.Equals(link.LastImportedPayloadHash, remoteHash, StringComparison.OrdinalIgnoreCase) && await CatalogSnapshotAlreadyApplied(tenantId, product.Id, snapshot, cancellationToken))
        {
            telemetrySkippedCount++;
            return;
        }

        var preserveLocal = link is not null && ProductImportMergePolicy.PreserveLocalChanges(product.Version, link.LastImportedProductVersion, link.DirtyFieldsJson);
        if (!preserveLocal)
        {
            product.Title = ProductTitle(snapshot.Title, externalProductId);
            product.Description = snapshot.Description ?? "";
            product.Status = ProductStatus.Active;
            product.ArchivedAt = null;
            product.UpdatedAt = now;
            product.Version++;
            telemetryUpdatedCount++;
        }
        else
        {
            telemetrySkippedCount++;
        }

        var brand = await UpsertCatalogBrand(tenantId, snapshot.BrandName, cancellationToken);
        var category = categoryContext?.LocalCategory ?? await UpsertCatalogCategory(tenantId, snapshot.CategoryName, cancellationToken);
        if (!preserveLocal)
        {
            product.BrandId = brand?.Id;
            product.CategoryId = category?.Id;
        }

        if (!preserveLocal)
        {
            foreach (var remote in snapshot.Variants)
                await UpsertCatalogVariant(tenantId, connectionId, product, remote, categoryContext, now, cancellationToken);

            var productImageUrls = snapshot.ImageUrls
                .Concat(snapshot.Variants.SelectMany(variant => variant.ImageUrls ?? []))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            await UpsertCatalogMedia(tenantId, product, null, productImageUrls, product.Title, cancellationToken);
        }
        link ??= await db.MarketplaceProductLinks.SingleAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ExternalId == externalProductId, cancellationToken);
        link.LastImportedPayloadHash = remoteHash;
        link.LastImportedAt = now;
        if (preserveLocal)
        {
            link.SyncStatus = "LOCAL_CHANGES_PENDING";
            link.DirtyFieldsJson ??= "[\"product\"]";
        }
        else
        {
            link.LastImportedProductVersion = product.Version;
            link.SyncStatus = "SYNCED";
            link.DirtyFieldsJson = null;
            link.LastError = null;
        }
        link.Version++;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<bool> CatalogSnapshotAlreadyApplied(Guid tenantId, Guid productId, RemoteCatalogProduct snapshot, CancellationToken cancellationToken)
    {
        var variantCount = await db.ProductVariants.AsNoTracking().CountAsync(x => x.TenantId == tenantId && x.ProductId == productId, cancellationToken);
        if (variantCount < snapshot.Variants.Count) return false;

        var mediaCount = await db.ProductMedia.AsNoTracking().CountAsync(x => x.TenantId == tenantId && x.ProductId == productId && x.VariantId == null && x.Status == "ACTIVE", cancellationToken);
        return mediaCount >= snapshot.ImageUrls.Count;
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

    private async Task<Category?> UpsertCatalogCategory(Guid tenantId, string? name, CancellationToken cancellationToken)
    {
        var normalized = NormalizeCatalogKey(name, 160);
        if (string.IsNullOrWhiteSpace(normalized)) return null;
        var category = db.Categories.Local.FirstOrDefault(x => x.TenantId == tenantId && x.ParentId == null && x.NormalizedName == normalized)
            ?? await db.Categories.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ParentId == null && x.NormalizedName == normalized, cancellationToken);
        if (category is not null)
        {
            var nextName = Short(name!.Trim(), 160);
            if (category.Name != nextName || category.Path != nextName || !category.IsActive || !category.IsLeaf)
            {
                category.Name = nextName; category.Path = nextName; category.IsActive = true; category.IsLeaf = true; category.UpdatedAt = timeProvider.GetUtcNow(); category.Version++;
            }
            return category;
        }
        category = new Category { Id = Guid.CreateVersion7(), TenantId = tenantId, Name = Short(name!.Trim(), 160), NormalizedName = normalized, Path = Short(name!.Trim(), 1024), Depth = 0, IsLeaf = true, IsActive = true, CreatedAt = timeProvider.GetUtcNow(), UpdatedAt = timeProvider.GetUtcNow(), Version = 1 };
        db.Categories.Add(category);
        return category;
    }

    private async Task UpsertCatalogVariant(Guid tenantId, Guid connectionId, Product product, RemoteCatalogVariant remote, CategoryAttributeContext? categoryContext, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var sku = Short(string.IsNullOrWhiteSpace(remote.Sku) ? remote.Barcode ?? remote.ExternalVariantId : remote.Sku, 160);
        var skuNormalized = NormalizeCatalogKey(sku, 160);
        if (string.IsNullOrWhiteSpace(skuNormalized)) return;
        var barcode = string.IsNullOrWhiteSpace(remote.Barcode) ? null : Short(remote.Barcode.Trim(), 160);
        var barcodeNormalized = NormalizeCatalogKey(barcode, 160);
        var externalVariantId = Short(remote.ExternalVariantId, 256);
        var optionSignature = categoryContext is null ? OptionSignature(remote.Options) : await PanelOptionSignatureAsync(tenantId, connectionId, categoryContext, remote.Options, cancellationToken);
        var link = await db.MarketplaceVariantLinks.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ExternalId == externalVariantId, cancellationToken);
        ProductVariant? variant = link is null ? null : await db.ProductVariants.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == link.VariantId, cancellationToken);
        variant ??= db.ProductVariants.Local.FirstOrDefault(x => x.TenantId == tenantId && x.ProductId == product.Id && x.SkuNormalized == skuNormalized)
            ?? await db.ProductVariants.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.SkuNormalized == skuNormalized, cancellationToken);
        if (variant is not null && variant.ProductId != product.Id)
        {
            await RecordIssue(tenantId, $"product-sync-variant-conflict:{connectionId}:{externalVariantId}", "PRODUCT_VARIANT_CONFLICT", "Trendyol varyantı başka bir yerel üründe kullanılan stok koduyla eşleşti; mevcut kayıt korunarak atlandı.", cancellationToken);
            return;
        }
        if (variant is null && !string.IsNullOrWhiteSpace(barcodeNormalized))
            variant = await db.ProductVariants.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ProductId == product.Id && x.BarcodeNormalized == barcodeNormalized, cancellationToken);
        if (variant is null)
        {
            variant = new ProductVariant { Id = Guid.CreateVersion7(), TenantId = tenantId, ProductId = product.Id, Sku = sku, SkuNormalized = skuNormalized, Barcode = barcode, BarcodeNormalized = barcodeNormalized, ModelCode = Short(remote.ModelCode, 160), OptionSignature = optionSignature, Status = remote.Archived ? ProductStatus.Archived : ProductStatus.Active, CreatedAt = now, UpdatedAt = now, Version = 1 };
            db.ProductVariants.Add(variant);
            telemetryInsertedCount++;
        }
        else
        {
            var nextModelCode = Short(remote.ModelCode, 160);
            var nextStatus = remote.Archived ? ProductStatus.Archived : ProductStatus.Active;
            if (variant.Sku != sku || variant.SkuNormalized != skuNormalized || variant.Barcode != barcode || variant.BarcodeNormalized != barcodeNormalized || variant.ModelCode != nextModelCode || variant.OptionSignature != optionSignature || variant.Status != nextStatus)
            {
                variant.Sku = sku; variant.SkuNormalized = skuNormalized; variant.Barcode = barcode; variant.BarcodeNormalized = barcodeNormalized; variant.ModelCode = nextModelCode; variant.OptionSignature = optionSignature; variant.Status = nextStatus; variant.UpdatedAt = now; variant.Version++;
                telemetryUpdatedCount++;
            }
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
                await RecordIssue(tenantId, $"product-sync-variant-link-conflict:{connectionId}:{externalVariantId}", "PRODUCT_VARIANT_LINK_CONFLICT", "Aynı yerel varyantın başka bir Trendyol varyant linki zaten var; ikinci link güvenli biçimde atlandı.", cancellationToken);
                return;
            }
        }
        await UpsertCatalogOptions(tenantId, connectionId, product.Id, variant.Id, remote.Options, categoryContext, cancellationToken);
        if (categoryContext is not null)
            await UpsertProductAttributeAssignments(tenantId, product, variant, remote.Options, categoryContext, cancellationToken);
        await UpsertCatalogOfferAndInventory(tenantId, connectionId, variant, remote, now, cancellationToken);
        if (remote.ImageUrls is not null)
            await UpsertCatalogMedia(tenantId, product, variant.Id, remote.ImageUrls, $"{product.Title} · {optionSignature}", cancellationToken);
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
                var imageUrls = entries
                    .Where(entry => entry.ImageUrls is not null)
                    .SelectMany(entry => entry.ImageUrls!)
                    .Where(url => !string.IsNullOrWhiteSpace(url))
                    .Select(url => url.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                return last with { ImageUrls = hasImagePayload ? imageUrls : null };
            })
            .ToList();
        var images = snapshots
            .SelectMany(snapshot => snapshot.ImageUrls.Concat(snapshot.Variants.SelectMany(variant => variant.ImageUrls ?? [])))
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
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

    private async Task UpsertCatalogOfferAndInventory(Guid tenantId, Guid connectionId, ProductVariant variant, RemoteCatalogVariant remote, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (remote.StockQuantity is decimal stockQuantity)
        {
            var onHand = decimal.Round(Math.Max(0m, stockQuantity), 4, MidpointRounding.ToEven);
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
                    OnHand = onHand,
                    Reserved = 0,
                    Available = onHand,
                    ReconciledAt = now,
                    ProjectionVersion = 1,
                    Version = 1
                });
            }
            else if (inventory.OnHand != onHand)
            {
                inventory.OnHand = onHand;
                inventory.Available = InventoryProjection.Available(onHand, inventory.Reserved);
                inventory.ReconciledAt = now;
                inventory.ProjectionVersion++;
                inventory.Version++;
            }
            else if (inventory.ReconciledAt is null)
            {
                inventory.ReconciledAt = now;
            }
        }

        if (remote.SalePrice is null && remote.ListPrice is null) return;
        var salePrice = decimal.Round(Math.Max(0m, remote.SalePrice ?? remote.ListPrice ?? 0m), 4, MidpointRounding.ToEven);
        var listPrice = decimal.Round(Math.Max(salePrice, Math.Max(0m, remote.ListPrice ?? salePrice)), 4, MidpointRounding.ToEven);
        var currency = NormalizeCurrency(remote.Currency);
        var vatRate = decimal.Round(Math.Max(0m, remote.VatRate ?? 0m), 4, MidpointRounding.ToEven);
        var status = remote.Archived ? "INACTIVE" : "ACTIVE";
        var offer = db.ChannelOffers.Local.FirstOrDefault(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.VariantId == variant.Id)
            ?? await db.ChannelOffers.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.VariantId == variant.Id, cancellationToken);
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
                SafetyStock = 0,
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
                Reason = "TRENDYOL_CATALOG_IMPORT",
                ActorSource = "SYSTEM:TRENDYOL",
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
                Reason = "TRENDYOL_CATALOG_IMPORT",
                ActorSource = "SYSTEM:TRENDYOL",
                EffectiveAt = now
            });
        }
    }

    private static string NormalizeCurrency(string? value)
    {
        var currency = value?.Trim().ToUpperInvariant();
        return currency is { Length: 3 } && currency.All(character => character is >= 'A' and <= 'Z') ? currency : "TRY";
    }

    private async Task<string> PanelOptionSignatureAsync(Guid tenantId, Guid connectionId, CategoryAttributeContext categoryContext, IReadOnlyDictionary<string, string> options, CancellationToken cancellationToken)
    {
        var panelOptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in options)
        {
            var optionKey = NormalizeCatalogKey(pair.Key, 320);
            if (!categoryContext.Attributes.TryGetValue(optionKey, out var mapped) || mapped.Role != "OPTION") continue;
            panelOptions[mapped.Definition.Name] = await PanelOptionValueAsync(tenantId, connectionId, categoryContext, mapped, pair.Value, cancellationToken);
        }
        return OptionSignature(panelOptions.Count > 0 ? panelOptions : options);
    }

    private async Task<string> PanelOptionValueAsync(Guid tenantId, Guid connectionId, CategoryAttributeContext categoryContext, LocalCategoryAttribute mapped, string remoteValue, CancellationToken cancellationToken)
    {
        var remote = mapped.Values.FirstOrDefault(value => NormalizeCatalogKey(value.Name, 320) == NormalizeCatalogKey(remoteValue, 320));
        if (remote is not null)
        {
            var scope = $"{categoryContext.ExternalCategoryId}/{mapped.Remote.ExternalId}";
            var mapping = await db.AttributeValueMappings.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ScopeExternalId == scope && x.ExternalId == remote.ExternalId && x.Status == "VERIFIED", cancellationToken);
            if (mapping is not null)
            {
                var localValue = await db.AttributeValues.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == mapping.LocalId && x.AttributeId == mapped.Definition.Id && x.IsActive, cancellationToken);
                if (localValue is not null) return localValue.Value;
            }
        }
        var direct = await db.AttributeValues.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.AttributeId == mapped.Definition.Id && x.IsActive && x.NormalizedValue == NormalizeCatalogKey(remoteValue, 320), cancellationToken);
        return direct?.Value ?? remoteValue;
    }

    private async Task UpsertCatalogOptions(Guid tenantId, Guid connectionId, Guid productId, Guid variantId, IReadOnlyDictionary<string, string> options, CategoryAttributeContext? categoryContext, CancellationToken cancellationToken)
    {
        var order = 0;
        foreach (var pair in options.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
        {
            var optionKey = NormalizeCatalogKey(pair.Key, 160); var valueKey = NormalizeCatalogKey(pair.Value, 160);
            if (string.IsNullOrWhiteSpace(optionKey) || string.IsNullOrWhiteSpace(valueKey)) continue;
            LocalCategoryAttribute? mapped = null;
            if (categoryContext is not null && (!categoryContext.Attributes.TryGetValue(optionKey, out mapped) || mapped.Role != "OPTION")) continue;
            var panelLabel = mapped?.Definition.Name ?? pair.Key;
            var panelValue = mapped is null ? pair.Value : await PanelOptionValueAsync(tenantId, connectionId, categoryContext!, mapped, pair.Value, cancellationToken);
            optionKey = NormalizeCatalogKey(panelLabel, 160);
            valueKey = NormalizeCatalogKey(panelValue, 160);
            var option = db.ProductOptions.Local.FirstOrDefault(x => x.TenantId == tenantId && x.ProductId == productId && x.NormalizedKey == optionKey)
                ?? await db.ProductOptions.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ProductId == productId && x.NormalizedKey == optionKey, cancellationToken);
            if (option is null) { option = new ProductOption { Id = Guid.CreateVersion7(), TenantId = tenantId, ProductId = productId, Label = Short(panelLabel, 160), NormalizedKey = optionKey, SortOrder = order }; db.ProductOptions.Add(option); }
            var optionValue = db.ProductOptionValues.Local.FirstOrDefault(x => x.TenantId == tenantId && x.OptionId == option.Id && x.NormalizedKey == valueKey)
                ?? await db.ProductOptionValues.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.OptionId == option.Id && x.NormalizedKey == valueKey, cancellationToken);
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
        var urls = sourceUrls.Select(NormalizeCatalogImageUrl).Where(x => x is not null).Select(x => x!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var existing = await db.ProductMedia.Where(x => x.TenantId == tenantId && x.ProductId == product.Id && x.VariantId == variantId).ToListAsync(cancellationToken);
        foreach (var row in existing.Where(x => x.SortOrder >= urls.Length)) row.Status = "ARCHIVED";
        for (var index = 0; index < urls.Length; index++)
        {
            var url = urls[index];
            var asset = db.FileAssets.Local.FirstOrDefault(x => x.TenantId == tenantId && x.Classification == "PRODUCT_MEDIA_URL" && x.RelativePath == url)
                ?? await db.FileAssets.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Classification == "PRODUCT_MEDIA_URL" && x.RelativePath == url, cancellationToken);
            if (asset is null)
            {
                asset = new FileAsset { Id = Guid.CreateVersion7(), TenantId = tenantId, Classification = "PRODUCT_MEDIA_URL", RelativePath = url, OriginalNameSafe = Path.GetFileName(new Uri(url).AbsolutePath), MimeType = ImageMime(url), SizeBytes = 0, Sha256 = Hash(url), Status = "ACTIVE", CreatedAt = timeProvider.GetUtcNow() };
                db.FileAssets.Add(asset);
            }
            else if (asset.Status != "ACTIVE" || asset.ArchivedAt is not null) { asset.Status = "ACTIVE"; asset.ArchivedAt = null; }
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

    private static string ProductTitle(string? title, string externalId) => Short(string.IsNullOrWhiteSpace(title) ? $"Trendyol ürün {externalId}" : title.Trim(), 320);
    private static string OptionSignature(IReadOnlyDictionary<string, string> options) => string.Join(" | ", options.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase).Select(x => $"{Short(x.Key, 80)}: {Short(x.Value, 120)}"));
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
    private static string? NormalizeCatalogImageUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var candidate = value.Trim();
        if (candidate.StartsWith("//", StringComparison.Ordinal)) candidate = "https:" + candidate;
        else if (candidate.StartsWith("/", StringComparison.Ordinal)) candidate = "https://cdn.dsmcdn.com" + candidate;
        return Uri.TryCreate(candidate, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? uri.ToString() : null;
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
    private sealed record CategoryAttributeContext(Category LocalCategory, string ExternalCategoryId, IReadOnlyDictionary<string, LocalCategoryAttribute> Attributes);
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

    private async Task<bool> IngestWebhook(Guid tenantId, Guid connectionId, string payloadJson, CancellationToken cancellationToken)
    {
        string raw; string externalMessageId; try { using var payload = JsonDocument.Parse(payloadJson); raw = payload.RootElement.GetProperty("rawJson").GetString() ?? ""; externalMessageId = payload.RootElement.GetProperty("externalMessageId").GetString() ?? ""; } catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException) { return false; }
        AdapterPageResult<RemoteOrder> page; try { page = TrendyolJsonMapper.Orders(raw); } catch (JsonException) { return false; }
        if (configuration.GetValue("Marketplace:PersistOrderSnapshots", true))
            await UpsertOrders(tenantId, connectionId, page.Items, cancellationToken);
        var inbox = await db.InboxMessages.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Source == "TRENDYOL_WEBHOOK" && x.ExternalMessageId == externalMessageId, cancellationToken); if (inbox is not null) inbox.ProcessedAt = timeProvider.GetUtcNow(); await db.SaveChangesAsync(cancellationToken); return true;
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

    private async Task UpsertOrders(Guid tenantId, Guid connectionId, IReadOnlyList<RemoteOrder> remotes, CancellationToken cancellationToken)
    {
        if (remotes.Count == 0) return;
        var batch = new OrderIngestionBatch();
        var externalIds = remotes.Select(x => x.ExternalOrderId).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).ToArray();
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
        foreach (var remote in remotes) await UpsertOrder(tenantId, connectionId, remote, cancellationToken, batch, saveChanges: false);
        await ProjectOrderReservations(tenantId, connectionId, batch.ReservationSources, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task UpsertOrder(Guid tenantId, Guid connectionId, RemoteOrder remote, CancellationToken cancellationToken, OrderIngestionBatch? batch = null, bool saveChanges = true)
    {
        IReadOnlyDictionary<string, decimal> remoteLineQuantities;
        if (remote.Lines.Count == 0 || remote.Packages.Count == 0)
        {
            await RecordIssue(tenantId, $"order-contract:{connectionId}:{remote.ExternalOrderId}:{remote.LastModifiedAt.ToUnixTimeMilliseconds()}", "ORDER_CONTRACT_INVALID", "Trendyol siparişinde satır veya paket verisi eksikti; eksik sipariş projeksiyonu uygulanmadı.", cancellationToken);
            if (saveChanges) await db.SaveChangesAsync(cancellationToken);
            return;
        }
        else
        {
            remoteLineQuantities = new Dictionary<string, decimal>(StringComparer.Ordinal);
            if (!PackageIngestionSafety.TryGetOrderedQuantities(remote.Lines, out remoteLineQuantities))
            {
                await RecordIssue(tenantId, $"order-lines:{connectionId}:{remote.ExternalOrderId}:{remote.LastModifiedAt.ToUnixTimeMilliseconds()}", "ORDER_LINE_QUANTITY_INVARIANT_REJECTED", "Sipariş satır kimliği veya miktarı geçersizdi; olayın hiçbir parçası uygulanmadı.", cancellationToken);
                if (saveChanges) await db.SaveChangesAsync(cancellationToken);
                return;
            }
        }
        var allocatedLineIds = remote.Packages.SelectMany(x => x.Allocations).Select(x => x.ExternalLineId).ToHashSet(StringComparer.Ordinal);
        if (remoteLineQuantities.Keys.Any(lineId => !allocatedLineIds.Contains(lineId)))
        {
            await RecordIssue(tenantId, $"order-coverage:{connectionId}:{remote.ExternalOrderId}:{remote.LastModifiedAt.ToUnixTimeMilliseconds()}", "ORDER_LINE_COVERAGE_INVALID", "Trendyol cevabındaki sipariş satırlarının tamamı package allocation içinde yer almıyordu; eksik veri uygulanmadı.", cancellationToken);
            if (saveChanges) await db.SaveChangesAsync(cancellationToken);
            return;
        }
        foreach (var remotePackage in remote.Packages) if (!PackageIngestionSafety.TryNormalizeAll(remoteLineQuantities, remotePackage.Allocations, ShipmentPackageStatusPolicy.FromRemote(remotePackage.RawStatus), out _)) { var rejectedEventId = PackageIngestionSafety.EventId(remotePackage.ExternalPackageId, remotePackage.OccurredAt); await RecordIssue(tenantId, $"package-quantity:{connectionId}:{rejectedEventId}", "PACKAGE_QUANTITY_INVARIANT_REJECTED", "Package miktarları sipariş satırı bütünlüğünü sağlamadı; olayın hiçbir parçası uygulanmadı.", cancellationToken); if (saveChanges) await db.SaveChangesAsync(cancellationToken); return; }
        var now = timeProvider.GetUtcNow(); var order = batch?.OrdersByExternalId.GetValueOrDefault(remote.ExternalOrderId) ?? await db.Orders.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ExternalOrderId == remote.ExternalOrderId, cancellationToken);
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
            order.OrderNumber = remote.OrderNumber; order.Currency = remote.Currency; order.GrossAmount = remote.GrossAmount; order.DiscountAmount = remote.DiscountAmount; order.NetAmount = remote.NetAmount; order.OrderedAt = remote.OrderedAt; order.ShipmentDueAt = remote.ShipmentDueAt; order.LastRemoteModifiedAt = remote.LastModifiedAt; order.CustomerSnapshotJson = remote.CustomerSnapshotJson; order.ShipmentAddressSnapshotJson = remote.ShipmentAddressSnapshotJson; order.InvoiceAddressSnapshotJson = remote.InvoiceAddressSnapshotJson; order.UpdatedAt = now; if (db.Entry(order).State != EntityState.Added) { order.Version++; telemetryUpdatedCount++; }
        }
        var existingLines = batch is not null
            ? batch.LinesByOrder.GetValueOrDefault(order.Id) ?? []
            : await db.OrderLines.Where(x => x.TenantId == tenantId && x.OrderId == order.Id).ToListAsync(cancellationToken);
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
            if (line is null && !string.IsNullOrWhiteSpace(remoteLine.SourceSnapshotJson) && remoteLine.SourceSnapshotJson != "{}") line = linesBySnapshot.GetValueOrDefault(remoteLine.SourceSnapshotJson);
            if (line is null) { line = new OrderLine { Id = Guid.CreateVersion7(), TenantId = tenantId, OrderId = order.Id, ExternalLineId = remoteLine.ExternalLineId, Sku = remoteLine.Sku, TitleSnapshot = remoteLine.Title, RawStatus = remoteLine.RawStatus, Version = 1 }; db.OrderLines.Add(line); existingLines.Add(line); telemetryInsertedCount++; }
            else line.ExternalLineId = remoteLine.ExternalLineId;
            if (line.VariantId is null)
            {
                var barcodeKey = VariantLookupKey(remoteLine.Barcode, true);
                var skuKey = VariantLookupKey(remoteLine.Sku, false);
                line.VariantId = barcodeKey is not null && variantIdsByKey.TryGetValue(barcodeKey, out var barcodeVariant)
                    ? barcodeVariant
                    : skuKey is not null && variantIdsByKey.TryGetValue(skuKey, out var skuVariant) ? skuVariant : null;
            }
            line.Sku = remoteLine.Sku; line.Barcode = remoteLine.Barcode; line.TitleSnapshot = remoteLine.Title; line.SourceSnapshotJson = remoteLine.SourceSnapshotJson; line.OrderedQuantity = remoteLine.Quantity; line.UnitPrice = remoteLine.UnitPrice; line.VatRate = remoteLine.VatRate; line.RawStatus = remoteLine.RawStatus; if (db.Entry(line).State != EntityState.Added) line.Version++; lines[remoteLine.ExternalLineId] = line;
            linesByExternalId[remoteLine.ExternalLineId] = line;
            if (!string.IsNullOrWhiteSpace(remoteLine.SourceSnapshotJson) && remoteLine.SourceSnapshotJson != "{}") linesBySnapshot[remoteLine.SourceSnapshotJson] = line;
        }
        foreach (var remotePackage in remote.Packages)
        {
            var target = ShipmentPackageStatusPolicy.FromRemote(remotePackage.RawStatus); var eventId = PackageIngestionSafety.EventId(remotePackage.ExternalPackageId, remotePackage.OccurredAt); var orderedQuantities = lines.ToDictionary(x => x.Key, x => x.Value.OrderedQuantity, StringComparer.Ordinal);
            if (!PackageIngestionSafety.TryNormalizeAll(orderedQuantities, remotePackage.Allocations, target, out var safeAllocations)) { await RecordIssue(tenantId, $"package-quantity:{connectionId}:{eventId}", "PACKAGE_QUANTITY_INVARIANT_REJECTED", "Package miktarları sipariş satırı bütünlüğünü sağlamadı; olayın hiçbir parçası uygulanmadı.", cancellationToken); continue; }
            var package = packagesByExternalId.GetValueOrDefault(remotePackage.ExternalPackageId);
            if (package is not null) await MergeMarketplaceInvoiceState(package, remotePackage, cancellationToken);
            if (package is not null && package.Status == ShipmentPackageStatus.ManualReview && package.RawStatus == remotePackage.RawStatus && target != ShipmentPackageStatus.ManualReview) { package.Status = target; package.UpdatedAt = now; package.Version++; continue; }
            var eventAlreadyRecorded = knownEventIds.Contains(eventId);
            if (eventAlreadyRecorded)
            {
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
            var accept = package is null || PackageIngestionSafety.ShouldAccept(package.Status, package.StatusOccurredAt, target, remotePackage.OccurredAt);
            if (package is null) { package = new ShipmentPackage { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = connectionId, OrderId = order.Id, ExternalPackageId = remotePackage.ExternalPackageId, Status = target, RawStatus = remotePackage.RawStatus, StatusOccurredAt = remotePackage.OccurredAt, CreatedAt = now, Version = 1 }; db.ShipmentPackages.Add(package); packagesByExternalId[remotePackage.ExternalPackageId] = package; telemetryInsertedCount++; await MergeMarketplaceInvoiceState(package, remotePackage, cancellationToken); }
            else if (accept) { package.Status = target; package.RawStatus = remotePackage.RawStatus; package.StatusOccurredAt = remotePackage.OccurredAt; package.Version++; }
            else if (remotePackage.OccurredAt >= package.StatusOccurredAt && package.Status != target) await RecordIssue(tenantId, $"package-transition:{package.Id}:{remotePackage.RawStatus}", "PACKAGE_TRANSITION_REJECTED", "Out-of-order veya izin verilmeyen package geçişi mevcut durumu geriye götürmedi.", cancellationToken);
            if (accept)
            {
                package.OriginExternalPackageId = remotePackage.OriginExternalPackageId; package.CargoProviderExternalId = remotePackage.CargoProviderExternalId; package.CargoTrackingNumber = remotePackage.CargoTrackingNumber; package.GrossAmount = remotePackage.GrossAmount; package.DiscountAmount = remotePackage.DiscountAmount; package.NetAmount = remotePackage.NetAmount; package.UpdatedAt = now; db.OrderStatusHistory.Add(new OrderStatusHistory { Id = Guid.CreateVersion7(), TenantId = tenantId, OrderId = order.Id, PackageId = package.Id, CanonicalStatus = Wire(target), RawStatus = remotePackage.RawStatus, SourceEventId = eventId, OccurredAt = remotePackage.OccurredAt, RecordedAt = now }); knownEventIds.Add(eventId);
                foreach (var remoteAllocation in remotePackage.Allocations) if (lines.TryGetValue(remoteAllocation.ExternalLineId, out var line) && safeAllocations.TryGetValue(remoteAllocation.ExternalLineId, out var safe)) { var allocationKey = AllocationKey(package.Id, line.Id, eventId); var allocation = allocationsByKey.GetValueOrDefault(allocationKey); if (allocation is null) { allocation = new PackageLineAllocation { Id = Guid.CreateVersion7(), TenantId = tenantId, PackageId = package.Id, OrderLineId = line.Id, SourceEventId = eventId, AllocatedQuantity = safe.ActiveAllocatedQuantity, CancelledQuantity = safe.CancelledQuantity, ShippedQuantity = safe.ShippedQuantity, DeliveredQuantity = safe.DeliveredQuantity, ReturnedQuantity = safe.ReturnedQuantity }; db.PackageLineAllocations.Add(allocation); allocationsByKey[allocationKey] = allocation; telemetryInsertedCount++; line.CancelledQuantity = Math.Max(line.CancelledQuantity, allocation.CancelledQuantity); line.ShippedQuantity = Math.Max(line.ShippedQuantity, allocation.ShippedQuantity); line.DeliveredQuantity = Math.Max(line.DeliveredQuantity, allocation.DeliveredQuantity); line.ReturnedQuantity = Math.Max(line.ReturnedQuantity, allocation.ReturnedQuantity); } }
            }
        }
        var persistedStatuses = batch is not null
            ? packagesByExternalId.Values.Select(x => x.Status).ToList()
            : await db.ShipmentPackages.AsNoTracking().Where(x => x.TenantId == tenantId && x.OrderId == order.Id).Select(x => x.Status).ToListAsync(cancellationToken);
        var acceptedStatuses = persistedStatuses.ToList();
        acceptedStatuses.AddRange(db.ShipmentPackages.Local.Where(x => x.TenantId == tenantId && x.OrderId == order.Id).Select(x => x.Status));
        order.DerivedStatus = Wire(ShipmentPackageStatusPolicy.Aggregate(acceptedStatuses));
        if (batch is null)
            await ProjectOrderReservations(tenantId, connectionId, lines.Values.Select(line => (line, remote.LastModifiedAt)).ToList(), cancellationToken);
        else
            batch.ReservationSources.AddRange(lines.Values.Select(line => (line, remote.LastModifiedAt)));
        if (saveChanges) await db.SaveChangesAsync(cancellationToken);
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
        var now = timeProvider.GetUtcNow();
        var outbox = new Dictionary<string, (Guid VariantId, string EventId)>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            var variantId = line.VariantId!.Value;
            if (!itemsByVariant.TryGetValue(variantId, out var item)) continue;
            var sourceId = line.Id.ToString("D");
            var desired = OrderInventoryReservationPolicy.DesiredQuantity(line.OrderedQuantity, line.CancelledQuantity);
            reservationsByKey.TryGetValue((item.Id, sourceId), out var reservation);
            var current = reservation is { Status: ReservationStatus.Active } ? reservation.Quantity : 0m;
            if (current == desired) continue;
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
            var eventId = $"{line.Id:N}:{modifiedAtByLine[line.Id].ToUnixTimeMilliseconds()}:{desired}";
            db.StockLedgerEntries.Add(new StockLedgerEntry { Id = Guid.CreateVersion7(), TenantId = tenantId, InventoryItemId = item.Id, MovementType = delta > 0 ? "ORDER_RESERVED" : "ORDER_RESERVATION_RELEASED", QuantityDelta = -delta, SourceType = "ORDER_LINE", SourceId = sourceId, SourceEventId = eventId, IdempotencyKey = $"order-reservation:{eventId}", OccurredAt = modifiedAtByLine[line.Id], RecordedAt = now, CorrelationId = $"order:{line.OrderId:N}" });
            outbox[StockProjectionOutboxPolicy.DedupKey(connectionId, variantId, item.ProjectionVersion)] = (variantId, eventId);
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
        if (!await db.ChannelOffers.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.VariantId == variantId && x.Status == "ACTIVE", cancellationToken)) return JobExecutionResult.Success();
        var build = await new PriceInventoryComposer(db).BuildAsync(tenantId, connectionId, cancellationToken);
        if (!build.Succeeded) return JobExecutionResult.Blocked(build.Error!.Code, build.Error.Message);
        var draft = build.Value!;
        var dedup = $"price-inventory:{connectionId:N}:{draft.PayloadHash}";
        if (await db.IntegrationJobs.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.JobType == MarketplaceJobTypes.PriceInventorySync && x.JobDedupKey == dedup, cancellationToken)) return JobExecutionResult.Success();
        var id = Guid.CreateVersion7(); var now = timeProvider.GetUtcNow();
        var jobPayload = JsonSerializer.Serialize(new PriceInventoryJobPayload(id, connectionId, "SUBMIT", draft.PayloadHash, draft.PayloadJson, draft.Lines, null, null));
        db.IntegrationJobs.Add(new IntegrationJob { Id = id, TenantId = tenantId, ConnectionId = connectionId, JobType = MarketplaceJobTypes.PriceInventorySync, PayloadJson = jobPayload, PayloadVersion = 1, PayloadHash = Hash(jobPayload), JobDedupKey = dedup, EffectIdempotencyKey = dedup, Priority = 1, Status = JobStatus.Pending, AvailableAt = now, MaxAttempts = 10, CorrelationId = correlationId, CreatedAt = now, Version = 1 });
        await db.SaveChangesAsync(cancellationToken);
        return JobExecutionResult.Success();
    }

    // Trendyol's claims feed is date-bounded for the initial scan. Invalidating
    // the previous all-time cursor makes the next read start at three months.
    private const string ReturnSyncStateVersion = "returns-v10";
    private sealed record ReturnSyncState(string Version, int StoreFrontIndex, int Page, bool Full = true, DateTimeOffset? StartAt = null, DateTimeOffset? EndAt = null);

    private async Task<bool> SyncReturns(Guid tenantId, Guid connectionId, string correlationId, CancellationToken cancellationToken)
    {
        var cursor = await Cursor(tenantId, connectionId, "RETURNS", cancellationToken);
        var configuredOverlapSeconds = await db.ConnectionSyncPolicies.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == "RETURNS")
            .Select(x => (int?)x.OverlapSeconds)
            .SingleOrDefaultAsync(cancellationToken) ?? 900;
        var overlap = TimeSpan.FromSeconds(Math.Clamp(configuredOverlapSeconds, 60, 86_400));
        var state = ReadReturnSyncState(cursor, timeProvider.GetUtcNow(), overlap);
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

    private async Task<bool> SyncOpenReturns(Guid tenantId, Guid connectionId, string correlationId, CancellationToken cancellationToken)
    {
        var batchSize = Math.Clamp(configuration.GetValue("MarketplaceSync:ReturnLifecycle:BatchSize", 25), 1, 100);
        var openClaims = await db.ReturnClaims.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId
                && x.Status != ReturnClaimStatus.Completed && x.Status != ReturnClaimStatus.Cancelled)
            .OrderBy(x => x.UpdatedAt)
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
            await DispatchStockProjection(tenantId, connectionId, JsonSerializer.Serialize(new { variantId = candidate.VariantId }), correlationId, cancellationToken);
            var item = await db.InventoryItems.SingleAsync(x => x.TenantId == tenantId && x.Id == candidate.Id, cancellationToken);
            item.ReconciledAt = timeProvider.GetUtcNow();
            item.Version++;
            await db.SaveChangesAsync(cancellationToken);
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

    private static ReturnSyncState ReadReturnSyncState(SyncCursor cursor, DateTimeOffset now, TimeSpan overlap)
    {
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
            // Claims carry the order line/customer snapshot even when the
            // order package is no longer available in Trendyol's order API
            // window. Prefer this local read-model reconstruction so a full
            // return scan does not issue one doomed remote lookup per claim.
            var claimOrder = TrendyolJsonMapper.OrderFromReturnClaim(remote.RawJson);
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
        if (claim is null) { claim = new ReturnClaim { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = connectionId, OrderId = order.Id, ExternalClaimId = remote.ExternalClaimId, Status = target, RawStatus = remote.RawStatus, LastRemoteModifiedAt = remote.LastModifiedAt, CreatedAt = now, UpdatedAt = now, Version = 1 }; db.ReturnClaims.Add(claim); telemetryInsertedCount++; }
        else if (remote.LastModifiedAt >= claim.LastRemoteModifiedAt && ReturnClaimStateMachine.CanTransition(claim.Status, target)) { claim.Status = target; claim.RawStatus = remote.RawStatus; claim.LastRemoteModifiedAt = remote.LastModifiedAt; claim.UpdatedAt = now; claim.Version++; telemetryUpdatedCount++; }
        if (claim.LastRemoteModifiedAt <= remote.LastModifiedAt) { claim.ReasonCode = remote.ReasonCode; claim.ReasonText = remote.ReasonText; claim.ActionDueAt = remote.ActionDueAt; }
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
                || decision.Action == "REJECT" && target is ReturnClaimStatus.Rejected or ReturnClaimStatus.Disputed;
            var conflictingTerminal = decision.Action == "APPROVE" && target is ReturnClaimStatus.Rejected or ReturnClaimStatus.Cancelled
                || decision.Action == "REJECT" && target is ReturnClaimStatus.Approved or ReturnClaimStatus.Completed;
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
            var decision = await db.ReturnDecisions.SingleAsync(x => x.TenantId == tenantId && x.Id == decisionId, cancellationToken);
            if (decision.Status == "SUCCEEDED") return true;
            if (decision.Status == "MANUAL_REVIEW") throw new JobProcessingException(JobExecutionResult.ManualReview(decision.ErrorCode ?? "RETURN_ACTION_REVIEW_REQUIRED", "İade kararı manuel inceleme bekliyor.", decision.ExternalOperationId));
            var claim = await db.ReturnClaims.AsNoTracking().SingleAsync(x => x.TenantId == tenantId && x.Id == decision.ClaimId && x.ConnectionId == connectionId, cancellationToken);
            var lineIds = await db.ReturnLines.AsNoTracking().Where(x => x.TenantId == tenantId && x.ClaimId == claim.Id).OrderBy(x => x.Id).Select(x => x.ExternalLineId).ToListAsync(cancellationToken);
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
    private async Task RecordIssue(Guid tenantId, string key, string code, string summary, CancellationToken cancellationToken) { var now = timeProvider.GetUtcNow(); var issue = await db.OperationalIssues.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.DedupeKey == key, cancellationToken); if (issue is null) db.OperationalIssues.Add(new OperationalIssue { Id = Guid.CreateVersion7(), TenantId = tenantId, DedupeKey = key, Code = code, Summary = summary, Status = IssueStatus.Open, FirstSeenAt = now, LastSeenAt = now, OccurrenceCount = 1 }); else { issue.LastSeenAt = now; issue.OccurrenceCount++; } }
    private async Task ResolveIssue(Guid tenantId, string key, CancellationToken cancellationToken) { var issue = await db.OperationalIssues.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.DedupeKey == key, cancellationToken); if (issue is not null) issue.Status = IssueStatus.Resolved; }
    private AdapterContext Context(Guid tenantId, Guid connectionId, string correlationId, string idempotency) => new(tenantId, connectionId, correlationId, idempotency, timeProvider.GetUtcNow().AddMinutes(2));
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static ReturnClaimStatus CanonicalReturn(string raw, string? cargoTrackingLink = null) => raw.ToUpperInvariant() switch { "CREATED" when !string.IsNullOrWhiteSpace(cargoTrackingLink) => ReturnClaimStatus.InTransit, "CREATED" => ReturnClaimStatus.Requested, "WAITINGFORSHIPMENT" => ReturnClaimStatus.AwaitingShipment, "WAITINGINCARGO" => ReturnClaimStatus.InTransit, "INTRANSIT" or "RETURNINTRANSIT" or "SHIPPED" => ReturnClaimStatus.InTransit, "WAITINGINACTION" or "INANALYSIS" or "WAITINGFRAUDCHECK" => ReturnClaimStatus.ActionRequired, "ACCEPTED" => ReturnClaimStatus.Approved, "REJECTED" => ReturnClaimStatus.Rejected, "UNRESOLVED" => ReturnClaimStatus.Disputed, "COMPLETED" => ReturnClaimStatus.Completed, "CANCELLED" => ReturnClaimStatus.Cancelled, _ => ReturnClaimStatus.ActionRequired };
    private static string Wire<T>(T value) where T : Enum => string.Concat(value.ToString().Select((ch, index) => char.IsUpper(ch) && index > 0 ? "_" + ch : ch.ToString())).ToUpperInvariant();
}
