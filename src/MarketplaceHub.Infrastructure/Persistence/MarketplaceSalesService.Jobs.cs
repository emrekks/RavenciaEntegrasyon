using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;
using static MarketplaceHub.Infrastructure.Persistence.InvoiceLabelPolicy;
using static MarketplaceHub.Infrastructure.Persistence.OrderSnapshotReader;

namespace MarketplaceHub.Infrastructure.Persistence;

public sealed partial class MarketplaceSalesService
{
    public async Task<ServiceResult<Guid>> EnqueueOrderSyncAsync(Guid tenantId, Guid connectionId, string? externalOrderId, bool full, string correlationId, CancellationToken cancellationToken, string? packageNumber = null)
    {
        var platform = await db.PlatformConnections.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.Id == connectionId)
            .Select(x => x.PlatformCode)
            .SingleOrDefaultAsync(cancellationToken);
        var normalizedPackageNumber = packageNumber?.Trim();
        if (!string.IsNullOrWhiteSpace(normalizedPackageNumber)
            && (platform is not ("TRENDYOL" or "HEPSIBURADA") || string.IsNullOrWhiteSpace(externalOrderId) || normalizedPackageNumber.Length > 100 || normalizedPackageNumber.Any(char.IsControl)))
            return ServiceResult<Guid>.Fail("ORDER_PACKAGE_LOOKUP_INVALID", "Paket numarasıyla yenileme yalnızca tek bir Trendyol veya Hepsiburada siparişi için kullanılabilir.", 422);
        var type = MarketplaceJobTypes.ForPlatform(platform, full ? MarketplaceJobTypes.OrderRecoverySync : MarketplaceJobTypes.OrderSync);
        return await EnqueueRead(tenantId, connectionId, MarketplaceCapabilities.OrderRead, type, JsonSerializer.Serialize(new { connectionId, externalOrderId, full, packageNumber = normalizedPackageNumber }), correlationId, cancellationToken);
    }

    public async Task<ServiceResult<Guid>> EnqueueProductSyncAsync(Guid tenantId, Guid connectionId, bool full, bool newOnly, bool existingOnly, bool mappingOnly, bool optionsOnly, bool includeArchived, bool includeDrafts, bool includePendingApproval, bool updateExistingProducts, bool updatePrices, bool updateStock, string? productLookup, string correlationId, CancellationToken cancellationToken)
    {
        var platform = await db.PlatformConnections.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.Id == connectionId)
            .Select(x => x.PlatformCode)
            .SingleOrDefaultAsync(cancellationToken);
        if (optionsOnly && !string.Equals(platform, "TRENDYOL", StringComparison.OrdinalIgnoreCase))
            return ServiceResult<Guid>.Fail("PRODUCT_OPTIONS_REPAIR_UNSUPPORTED", "Varyant seçeneklerini tek başına yenileme şu anda yalnızca Trendyol bağlantılarında kullanılabilir.", 422);
        if (optionsOnly)
        {
            full = true;
            newOnly = false;
            existingOnly = true;
            mappingOnly = false;
            includeArchived = true;
            includeDrafts = false;
            includePendingApproval = true;
            updateExistingProducts = false;
            updatePrices = false;
            updateStock = false;
            productLookup = null;
        }
        includePendingApproval = includePendingApproval
            && (full || newOnly || mappingOnly || optionsOnly)
            && (!existingOnly || optionsOnly)
            && string.IsNullOrWhiteSpace(productLookup);
        // Mapping is deliberately exclusive: it may create links only, never
        // local products or imported product content, even if an older client
        // sends overlapping mode flags.
        if (mappingOnly)
        {
            full = false;
            newOnly = false;
            existingOnly = false;
            // Mapping reads the whole remote catalog (ProductImportScanPolicy.RequiresFullCatalogForMapping); whether its
            // archived (and Shopify draft) products are matched too is the owner's "Arşiv Ürünlerini Dahil Et" choice
            // (12 Nov 2026; until then it was forced on here).
            updateExistingProducts = false;
            updatePrices = false;
            updateStock = false;
            optionsOnly = false;
        }
        // A single product looked up by its code may take its marketplace price and stock too (owner, 5 Nov 2026).
        var priceStockAllowed = (full || existingOnly || !string.IsNullOrWhiteSpace(productLookup)) && !newOnly && !mappingOnly && !optionsOnly && !string.Equals(platform, "HEPSIBURADA", StringComparison.OrdinalIgnoreCase) && !string.Equals(platform, "N11", StringComparison.OrdinalIgnoreCase) && !string.Equals(platform, "PAZARAMA", StringComparison.OrdinalIgnoreCase) && !string.Equals(platform, "PTTAVM", StringComparison.OrdinalIgnoreCase);
        updatePrices = updatePrices && priceStockAllowed;
        updateStock = updateStock && priceStockAllowed;
        // "Mevcut Ürünleri Güncelle" on Trendyol with none of its three choices ticked would read the whole catalog to
        // change nothing (owner, 12 Nov 2026): the panel asks for one, and so does the API.
        if (ProductImportScanPolicy.ExistingUpdateSelectsNothing(platform, existingOnly && !optionsOnly && string.IsNullOrWhiteSpace(productLookup), updateExistingProducts, updatePrices, updateStock))
            return ServiceResult<Guid>.Fail("PRODUCT_SYNC_NOTHING_SELECTED", "En az bir seçenek işaretleyin: Mevcut Ürün Bilgilerini Güncelle, Fiyatları Güncelle veya Stokları Güncelle.", 400);
        var type = MarketplaceJobTypes.ForPlatform(platform, MarketplaceJobTypes.ProductSync);
        return await EnqueueRead(tenantId, connectionId, MarketplaceCapabilities.ProductRead, type, JsonSerializer.Serialize(new { connectionId, full, newOnly, existingOnly, mappingOnly, optionsOnly, includeArchived, includeDrafts, includePendingApproval, updateExistingProducts, updatePrices, updateStock, productLookup }), correlationId, cancellationToken);
    }

    public async Task<ServiceResult<CatalogStructureRunView>> EnqueueCatalogStructureAsync(Guid tenantId, Guid connectionId, string mode, string correlationId, CancellationToken cancellationToken)
    {
        var normalizedMode = mode?.Trim().ToUpperInvariant();
        if (normalizedMode is not ("PREVIEW" or "APPLY"))
            return ServiceResult<CatalogStructureRunView>.Fail("CATALOG_STRUCTURE_MODE_INVALID", "mode PREVIEW veya APPLY olmalıdır.", 422);
        var platform = await db.PlatformConnections.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == connectionId).Select(x => x.PlatformCode).SingleOrDefaultAsync(cancellationToken);
        if (platform is not ("TRENDYOL" or "HEPSIBURADA"))
            return ServiceResult<CatalogStructureRunView>.Fail("CATALOG_STRUCTURE_UNSUPPORTED", "Platformdan yapı çekme şu anda yalnızca Trendyol ve Hepsiburada bağlantılarında kullanılabilir.", 422);
        if (await db.CatalogStructureRuns.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && (x.Status == "QUEUED" || x.Status == "RUNNING"), cancellationToken))
            return ServiceResult<CatalogStructureRunView>.Fail("CATALOG_STRUCTURE_ALREADY_RUNNING", "Bu bağlantıda yapı çekme işlemi zaten çalışıyor.", 409);
        if (normalizedMode == "APPLY")
        {
            // Applying is only allowed after a fresh, completed preview of the same connection.
            var threshold = timeProvider.GetUtcNow().AddHours(-24);
            var hasPreview = await db.CatalogStructureRuns.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.Mode == "PREVIEW" && x.Status == "COMPLETED" && x.CompletedAt >= threshold, cancellationToken);
            if (!hasPreview)
                return ServiceResult<CatalogStructureRunView>.Fail("CATALOG_STRUCTURE_PREVIEW_REQUIRED", "Uygulamadan önce son 24 saat içinde tamamlanmış bir önizleme gerekir.", 409);
        }

        var run = new CatalogStructureRun { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = connectionId, Mode = normalizedMode, Status = "QUEUED", CreatedAt = timeProvider.GetUtcNow(), Version = 1 };
        db.CatalogStructureRuns.Add(run);
        await db.SaveChangesAsync(cancellationToken);
        var queued = await EnqueueRead(tenantId, connectionId, MarketplaceCapabilities.ProductRead, MarketplaceJobTypes.CatalogStructureImport, JsonSerializer.Serialize(new { connectionId, runId = run.Id, mode = normalizedMode }), correlationId, cancellationToken);
        if (!queued.Succeeded)
        {
            db.CatalogStructureRuns.Remove(run);
            await db.SaveChangesAsync(cancellationToken);
            return ServiceResult<CatalogStructureRunView>.Fail(queued.Error!.Code, queued.Error.Message, queued.Error.Status);
        }
        run.JobId = queued.Value;
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult<CatalogStructureRunView>.Ok(ToView(run, includeReport: false));
    }

    public async Task<ServiceResult<CatalogStructureRunView>> CatalogStructureRunAsync(Guid tenantId, Guid runId, CancellationToken cancellationToken)
    {
        var run = await db.CatalogStructureRuns.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == runId, cancellationToken);
        return run is null ? NotFound<CatalogStructureRunView>() : ServiceResult<CatalogStructureRunView>.Ok(ToView(run, includeReport: true));
    }

    public async Task<IReadOnlyList<CatalogStructureRunView>> CatalogStructureRunsAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken)
    {
        var runs = await db.CatalogStructureRuns.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId)
            .OrderByDescending(x => x.CreatedAt)
            .Take(20)
            .ToListAsync(cancellationToken);
        return runs.Select(run => ToView(run, includeReport: false)).ToList();
    }

    private static CatalogStructureRunView ToView(CatalogStructureRun run, bool includeReport)
    {
        JsonElement? report = null;
        if (includeReport && !string.IsNullOrWhiteSpace(run.ReportJson))
        {
            using var document = JsonDocument.Parse(run.ReportJson);
            report = document.RootElement.Clone();
        }
        return new(run.Id, run.ConnectionId, run.JobId, run.Mode, run.Status, run.CreatedAt, run.CompletedAt, run.ErrorSummary, report);
    }

    public Task<ServiceResult<Guid>> EnqueueReferenceSyncAsync(Guid tenantId, Guid connectionId, string resourceType, string? parentExternalId, string correlationId, CancellationToken cancellationToken)
    {
        var normalized = resourceType.Trim().ToUpperInvariant();
        var parent = string.IsNullOrWhiteSpace(parentExternalId) ? null : parentExternalId.Trim();
        var valid = normalized switch
        {
            "CATEGORIES" or "BRANDS" => parent is null,
            "CATEGORY_ATTRIBUTES" => parent is not null && !parent.Contains('/', StringComparison.Ordinal),
            "ATTRIBUTE_VALUES" => parent?.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length == 2,
            _ => false
        };
        return valid
            ? EnqueueRead(tenantId, connectionId, MarketplaceCapabilities.ReferenceRead, MarketplaceJobTypes.ReferenceSync, JsonSerializer.Serialize(new { connectionId, resourceType = normalized, parentExternalId = parent }), correlationId, cancellationToken)
            : Task.FromResult(ServiceResult<Guid>.Fail("REFERENCE_RESOURCE_UNSUPPORTED", "Bu liste için kategori ya da özellik seçimi hatalı; sayfayı yenileyip yeniden deneyin.", 422));
    }

    public async Task<ServiceResult<Guid>> EnqueueStageTestOrderAsync(Guid tenantId, Guid actorUserId, Guid connectionId, string idempotencyKey, string correlationId, CancellationToken cancellationToken)
    {
        var connection = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == connectionId && (x.PlatformCode == "TRENDYOL" || x.PlatformCode == "HEPSIBURADA"), cancellationToken);
        if (connection is null) return NotFound<Guid>();
        if (!string.Equals(connection.Environment, "STAGE", StringComparison.OrdinalIgnoreCase)
            || connection.PlatformCode == "TRENDYOL" && !string.Equals(connection.ExternalStoreId, "2738", StringComparison.Ordinal))
            return ServiceResult<Guid>.Fail("STAGE_TEST_ORDER_SCOPE_REQUIRED", "Test siparişi yalnız test mağazası bağlantılarında oluşturulabilir.", 422);
        var barcode = "9900000000486";
        string? hepsiburadaSku = null;
        if (connection.PlatformCode == "HEPSIBURADA")
        {
            var liveBarcode = await (from listing in db.ChannelListingVariants.AsNoTracking()
                                     join profile in db.ChannelListingProfiles.AsNoTracking() on new { listing.TenantId, ProfileId = listing.ProfileId } equals new { profile.TenantId, ProfileId = profile.Id }
                                     join state in db.MarketplaceListingStates.AsNoTracking() on new { listing.TenantId, profile.ConnectionId, listing.VariantId } equals new { state.TenantId, state.ConnectionId, state.VariantId }
                                     join link in db.MarketplaceVariantLinks.AsNoTracking() on new { listing.TenantId, profile.ConnectionId, listing.VariantId } equals new { link.TenantId, link.ConnectionId, link.VariantId }
                                     where listing.TenantId == tenantId && profile.ConnectionId == connectionId && profile.Enabled && state.ActualStatus == "LIVE"
                                           && listing.ExternalBarcode != null && !string.IsNullOrWhiteSpace(link.ExternalId)
                                     orderby listing.VariantId
                                     select listing.ExternalBarcode!).FirstOrDefaultAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(liveBarcode)) barcode = liveBarcode;
            else
            {
                // SIT fallback (this method only reaches here for a STAGE connection): the products Hepsiburada
                // loads into the SIT account are imported, not published from the panel, so they have no LIVE
                // listing. Use an imported link whose external id is a real hbSku and whose offer is ACTIVE.
                var importedExternalIds = await (from link in db.MarketplaceVariantLinks.AsNoTracking()
                                                 join offer in db.ChannelOffers.AsNoTracking() on new { link.TenantId, link.ConnectionId, link.VariantId } equals new { offer.TenantId, offer.ConnectionId, offer.VariantId }
                                                 where link.TenantId == tenantId && link.ConnectionId == connectionId && offer.Status == "ACTIVE"
                                                 orderby link.VariantId
                                                 select link.ExternalId).ToListAsync(cancellationToken);
                hepsiburadaSku = HepsiburadaStageCertificationPolicy.FirstHepsiburadaSku(importedExternalIds);
                if (hepsiburadaSku is null) return ServiceResult<Guid>.Fail("HEPSIBURADA_STAGE_LISTING_REQUIRED", "Test siparişi için test mağazasında Hepsiburada'ya bağlı, satışta olan bir ürün gerekir.", 409);
                barcode = hepsiburadaSku;
            }
        }
        var normalizedKey = idempotencyKey.Trim();
        var existing = await db.IntegrationJobs.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.JobType == MarketplaceJobTypes.StageTestOrder && x.EffectIdempotencyKey == normalizedKey, cancellationToken);
        if (existing is not null) return ServiceResult<Guid>.Ok(existing.Id);
        var now = timeProvider.GetUtcNow(); var jobId = Guid.CreateVersion7(); var payload = JsonSerializer.Serialize(new StageTestOrderJobPayload(jobId, actorUserId, barcode, now, hepsiburadaSku));
        var job = NewJob(tenantId, connectionId, MarketplaceJobTypes.StageTestOrder, $"stage-test-order:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedKey)))}", payload, correlationId);
        job.Id = jobId; job.EffectIdempotencyKey = normalizedKey; job.MaxAttempts = 1; db.IntegrationJobs.Add(job);
        db.AuditLogs.Add(new AuditLog { TenantId = tenantId, ActorUserId = actorUserId, Action = "STAGE_TEST_ORDER_ENQUEUED", TargetType = "PlatformConnection", TargetId = connectionId.ToString("D"), Reason = $"official-stage-test-order-fixture:{connection.PlatformCode}", CorrelationId = correlationId, CreatedAt = now });
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult<Guid>.Ok(jobId);
    }

    public Task<ServiceResult<Guid>> EnqueueReturnSyncAsync(Guid tenantId, Guid connectionId, string correlationId, CancellationToken cancellationToken) => EnqueueRead(tenantId, connectionId, MarketplaceCapabilities.ReturnRead, MarketplaceJobTypes.ReturnSync, JsonSerializer.Serialize(new { connectionId, forceFull = true }), correlationId, cancellationToken);

    public Task<ServiceResult<Guid>> EnqueueQuestionSyncAsync(Guid tenantId, Guid connectionId, string? kind, string correlationId, CancellationToken cancellationToken) => EnqueueRead(tenantId, connectionId, MarketplaceCapabilities.QuestionRead, MarketplaceJobTypes.QuestionSync, JsonSerializer.Serialize(new { connectionId, kind }), correlationId, cancellationToken);

    private async Task<ServiceResult<Guid>> EnqueueRead(Guid tenantId, Guid connectionId, string capability, string type, string payload, string correlationId, CancellationToken cancellationToken)
    {
        var connection = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == connectionId && (x.PlatformCode == "TRENDYOL" || x.PlatformCode == "SHOPIFY" || x.PlatformCode == "HEPSIBURADA" || x.PlatformCode == "N11" || x.PlatformCode == "PAZARAMA" || x.PlatformCode == "PTTAVM") && (x.Status == "ACTIVE" || x.Status == "VERIFIED"), cancellationToken);
        if (connection is null) return ServiceResult<Guid>.Fail("ACTIVE_CONNECTION_REQUIRED", "Aktif veya doğrulanmış marketplace bağlantısı gerekir.", 422);
        if (!IntegrationRuntimePolicy.AllowsManualRead(connection)) return ServiceResult<Guid>.Fail("ENVIRONMENT_INVALID", "Okuma yalnız test ya da canlı mağaza bağlantısında yapılabilir.", 422);
        type = MarketplaceJobTypes.ForPlatform(connection.PlatformCode, type);
        return await Enqueue(tenantId, connectionId, type, $"{type.ToLowerInvariant()}:{connectionId}:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)))}", payload, correlationId, cancellationToken);
    }
    private async Task<ServiceResult<Guid>> Enqueue(Guid tenantId, Guid connectionId, string type, string dedup, string payload, string correlationId, CancellationToken cancellationToken)
    {
        var recurringRead = type is MarketplaceJobTypes.ReferenceSync or MarketplaceJobTypes.HepsiburadaReferenceSync or MarketplaceJobTypes.N11ReferenceSync or MarketplaceJobTypes.PazaramaReferenceSync or MarketplaceJobTypes.PttAvmReferenceSync or MarketplaceJobTypes.OrderSync or MarketplaceJobTypes.ShopifyOrderSync or MarketplaceJobTypes.HepsiburadaOrderSync or MarketplaceJobTypes.N11OrderSync or MarketplaceJobTypes.PazaramaOrderSync or MarketplaceJobTypes.PttAvmOrderSync or MarketplaceJobTypes.OrderRecoverySync or MarketplaceJobTypes.ShopifyOrderRecoverySync or MarketplaceJobTypes.HepsiburadaOrderRecoverySync or MarketplaceJobTypes.N11OrderRecoverySync or MarketplaceJobTypes.PazaramaOrderRecoverySync or MarketplaceJobTypes.PttAvmOrderRecoverySync or MarketplaceJobTypes.OrderStatusSync or MarketplaceJobTypes.ShopifyOrderStatusSync or MarketplaceJobTypes.HepsiburadaOrderStatusSync or MarketplaceJobTypes.PazaramaOrderStatusSync or MarketplaceJobTypes.PttAvmOrderStatusSync or MarketplaceJobTypes.TrendyolOrderCargoInfoReconciliation or MarketplaceJobTypes.ProductSync or MarketplaceJobTypes.ShopifyProductSync or MarketplaceJobTypes.HepsiburadaProductSync or MarketplaceJobTypes.ReturnSync or MarketplaceJobTypes.HepsiburadaReturnSync or MarketplaceJobTypes.ReturnStatusSync or MarketplaceJobTypes.QuestionSync;
        var activeJobs = await db.IntegrationJobs.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId
                && (x.Status == JobStatus.Pending || x.Status == JobStatus.Leased || x.Status == JobStatus.RetryScheduled))
            .Select(x => new { x.Id, x.JobType, x.JobDedupKey, x.Status, x.PayloadJson, x.AttemptCount, x.StartedAt })
            .ToListAsync(cancellationToken);
        var active = activeJobs.FirstOrDefault(x => x.JobType == type && (recurringRead ? x.JobDedupKey.StartsWith(dedup, StringComparison.Ordinal) : x.JobDedupKey == dedup));
        if (active is not null) return ServiceResult<Guid>.Ok(active.Id);

        // Manual sync buttons and scheduled policies use different dedup keys.
        // Collapse them onto the same provider lane before a second job reaches
        // the worker; otherwise the advisory lock turns a normal overlap into a
        // visible SYNC_LOCK_BUSY retry storm.
        var executionGroup = MarketplaceSyncExecutionLock.GroupFor(type);
        var fullScanRequest = IsFullOrderScanRequest(type, payload);
        var requestedExternalOrderId = TargetedExternalOrderId(payload);
        var conflictingJobs = activeJobs.Where(x => MarketplaceSyncExecutionLock.GroupFor(x.JobType) == executionGroup).ToList();
        var conflicting = fullScanRequest
            ? conflictingJobs.FirstOrDefault(x => FullOrderSyncConflictPolicy.Resolve(
                type,
                x.JobType,
                x.Status,
                requestedFullScan: true,
                conflictingJobTargetsSingleOrder: HasTargetedExternalOrderId(x.PayloadJson),
                conflictingJobHasStarted: x.AttemptCount > 0 || x.StartedAt is not null) == FullOrderSyncConflictResolution.PromotePending)
                ?? conflictingJobs.FirstOrDefault()
            : requestedExternalOrderId is not null
                ? TargetedOrderSyncConflictPolicy.PreferredConflictIndex(
                    conflictingJobs.Select(x => (
                        x.JobType,
                        TargetedExternalOrderId(x.PayloadJson),
                        x.Status,
                        x.AttemptCount > 0 || x.StartedAt is not null,
                        PayloadString(x.PayloadJson, "packageNumber"))).ToList(),
                    type,
                    requestedExternalOrderId,
                    PayloadString(payload, "packageNumber")) is { } preferredIndex
                    ? conflictingJobs[preferredIndex]
                    : null
                : conflictingJobs.FirstOrDefault();
        if (conflicting is not null)
        {
            var conflictResolution = FullOrderSyncConflictPolicy.Resolve(
                type,
                conflicting.JobType,
                conflicting.Status,
                fullScanRequest,
                HasTargetedExternalOrderId(conflicting.PayloadJson),
                conflicting.AttemptCount > 0 || conflicting.StartedAt is not null);
            if (conflictResolution == FullOrderSyncConflictResolution.PromotePending)
            {
                var now = timeProvider.GetUtcNow();
                var payloadHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
                var updated = await db.IntegrationJobs
                    .Where(x => x.TenantId == tenantId && x.Id == conflicting.Id
                        && x.Status == JobStatus.Pending && x.AttemptCount == 0 && x.StartedAt == null)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(x => x.JobType, type)
                        .SetProperty(x => x.PayloadJson, payload)
                        .SetProperty(x => x.PayloadVersion, 1)
                        .SetProperty(x => x.PayloadHash, payloadHash)
                        .SetProperty(x => x.Priority, x => Math.Min(x.Priority, Priority(type)))
                        .SetProperty(x => x.AvailableAt, now)
                        .SetProperty(x => x.CreatedAt, now)
                        .SetProperty(x => x.CorrelationId, correlationId)
                        .SetProperty(x => x.ProgressCurrent, 0)
                        .SetProperty(x => x.ProgressTotal, (int?)null)
                        .SetProperty(x => x.ProgressPercent, (int?)null)
                        .SetProperty(x => x.ProgressLabel, (string?)null)
                        .SetProperty(x => x.ProgressReceived, 0)
                        .SetProperty(x => x.ProgressProcessed, 0)
                        .SetProperty(x => x.ProgressSkipped, 0)
                        .SetProperty(x => x.ProgressFailed, 0)
                        .SetProperty(x => x.Version, x => x.Version + 1),
                        cancellationToken);
                if (updated == 0)
                    return ServiceResult<Guid>.Fail("ORDER_SYNC_ALREADY_RUNNING", "Tam sipariş taraması, bağlantıdaki sipariş işlemi başlarken kuyruğa alınamadı. İşlem tamamlanınca yeniden deneyin.", 409);
                return ServiceResult<Guid>.Ok(conflicting.Id);
            }
            if (requestedExternalOrderId is not null)
            {
                var targetedResolution = TargetedOrderSyncConflictPolicy.Resolve(
                    type,
                    requestedExternalOrderId,
                    conflicting.JobType,
                    TargetedExternalOrderId(conflicting.PayloadJson),
                    conflicting.Status,
                    conflicting.AttemptCount > 0 || conflicting.StartedAt is not null,
                    PayloadString(payload, "packageNumber"),
                    PayloadString(conflicting.PayloadJson, "packageNumber"));
                if (targetedResolution == TargetedOrderSyncConflictResolution.PromotePending)
                {
                    var now = timeProvider.GetUtcNow();
                    var payloadHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
                    var updated = await db.IntegrationJobs
                        .Where(x => x.TenantId == tenantId && x.Id == conflicting.Id
                            && x.Status == JobStatus.Pending && x.AttemptCount == 0 && x.StartedAt == null)
                        .ExecuteUpdateAsync(setters => setters
                            .SetProperty(x => x.JobType, type)
                            .SetProperty(x => x.PayloadJson, payload)
                            .SetProperty(x => x.PayloadVersion, 1)
                            .SetProperty(x => x.PayloadHash, payloadHash)
                            .SetProperty(x => x.Priority, x => Math.Min(x.Priority, Priority(type)))
                            .SetProperty(x => x.AvailableAt, now)
                            .SetProperty(x => x.CreatedAt, now)
                            .SetProperty(x => x.CorrelationId, correlationId)
                            .SetProperty(x => x.ProgressCurrent, 0)
                            .SetProperty(x => x.ProgressTotal, (int?)null)
                            .SetProperty(x => x.ProgressPercent, (int?)null)
                            .SetProperty(x => x.ProgressLabel, (string?)null)
                            .SetProperty(x => x.ProgressReceived, 0)
                            .SetProperty(x => x.ProgressProcessed, 0)
                            .SetProperty(x => x.ProgressSkipped, 0)
                            .SetProperty(x => x.ProgressFailed, 0)
                            .SetProperty(x => x.MaxAttempts, x => Math.Max(x.MaxAttempts, 30))
                            .SetProperty(x => x.Version, x => x.Version + 1),
                            cancellationToken);
                    if (updated == 0)
                        return ServiceResult<Guid>.Fail("ORDER_SYNC_ALREADY_RUNNING", "Tekil sipariş okuması mevcut işlem başlarken kuyruğa alınamadı. İşlem tamamlanınca yeniden deneyin.", 409);
                    return ServiceResult<Guid>.Ok(conflicting.Id);
                }
                if (targetedResolution == TargetedOrderSyncConflictResolution.QueueBehindActiveWork)
                {
                    var queuedJob = NewJob(
                        tenantId,
                        connectionId,
                        type,
                        recurringRead ? $"{dedup}:{timeProvider.GetUtcNow().ToUnixTimeMilliseconds()}" : dedup,
                        payload,
                        correlationId);
                    EnsureTargetedOrderReadRetryBudget(queuedJob, type, payload);
                    db.IntegrationJobs.Add(queuedJob);
                    await db.SaveChangesAsync(cancellationToken);
                    return ServiceResult<Guid>.Ok(queuedJob.Id);
                }
                if (targetedResolution == TargetedOrderSyncConflictResolution.Reject)
                    return ServiceResult<Guid>.Fail("ORDER_SYNC_ALREADY_RUNNING", "Tekil sipariş okuması bağlantıda başka bir sipariş işlemi bulunduğu için kuyruğa alınamadı. İşlem tamamlanınca yeniden deneyin.", 409);
                return ServiceResult<Guid>.Ok(conflicting.Id);
            }
            if (conflictResolution == FullOrderSyncConflictResolution.Reject)
                return ServiceResult<Guid>.Fail("ORDER_SYNC_ALREADY_RUNNING", "Tam sipariş taraması, bağlantıda başka bir sipariş işlemi çalıştığı için kuyruğa alınamadı. İşlem tamamlanınca yeniden deneyin.", 409);
            if (ProductImportConcurrencyPolicy.RejectsModeCollision(type, conflicting.JobType))
                return ServiceResult<Guid>.Fail("PRODUCT_SYNC_ALREADY_RUNNING", "Bu bağlantıda başka bir ürün aktarımı çalışıyor. Önce mevcut işlemi durdurup eşlemeyi yeniden başlatın.", 409);
            return ServiceResult<Guid>.Ok(conflicting.Id);
        }

        var job = NewJob(tenantId, connectionId, type, recurringRead ? $"{dedup}:{timeProvider.GetUtcNow().ToUnixTimeMilliseconds()}" : dedup, payload, correlationId);
        EnsureTargetedOrderReadRetryBudget(job, type, payload);
        db.IntegrationJobs.Add(job); await db.SaveChangesAsync(cancellationToken); return ServiceResult<Guid>.Ok(job.Id);
    }

    private static void EnsureTargetedOrderReadRetryBudget(IntegrationJob job, string jobType, string payload)
    {
        if ((jobType is MarketplaceJobTypes.OrderSync or MarketplaceJobTypes.ShopifyOrderSync or MarketplaceJobTypes.HepsiburadaOrderSync or MarketplaceJobTypes.N11OrderSync or MarketplaceJobTypes.PazaramaOrderSync or MarketplaceJobTypes.PttAvmOrderSync)
            && TargetedExternalOrderId(payload) is not null)
            job.MaxAttempts = Math.Max(job.MaxAttempts, 30);
    }
    private static bool IsFullOrderScanRequest(string jobType, string payload)
    {
        if (jobType is not (MarketplaceJobTypes.OrderRecoverySync or MarketplaceJobTypes.ShopifyOrderRecoverySync or MarketplaceJobTypes.HepsiburadaOrderRecoverySync or MarketplaceJobTypes.N11OrderRecoverySync or MarketplaceJobTypes.PazaramaOrderRecoverySync or MarketplaceJobTypes.PttAvmOrderRecoverySync)) return false;
        try
        {
            using var document = JsonDocument.Parse(payload);
            return document.RootElement.TryGetProperty("full", out var full) && full.ValueKind == JsonValueKind.True;
        }
        catch (JsonException) { return false; }
    }

    private static bool HasTargetedExternalOrderId(string payload)
        => TargetedExternalOrderId(payload) is not null;

    private static string? TargetedExternalOrderId(string payload)
        => PayloadString(payload, "externalOrderId");

    private static string? PayloadString(string payload, string propertyName)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            if (!document.RootElement.TryGetProperty(propertyName, out var id) || id.ValueKind != JsonValueKind.String)
                return null;
            var value = id.GetString()?.Trim();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch (JsonException) { return null; }
    }
    private IntegrationJob NewJob(Guid tenantId, Guid connectionId, string type, string dedup, string payload, string correlationId) => IntegrationJobFactory.New(new IntegrationJobRequest(tenantId, connectionId, type, payload, dedup, correlationId, timeProvider.GetUtcNow()) { Priority = Priority(type), TriggerType = "user" });
    internal static int Priority(string type) => JobPriorityPolicy.ForUser(type);
    private Task<bool> Supported(Guid tenantId, Guid connectionId, string code, CancellationToken cancellationToken) => db.PlatformCapabilities.AnyAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.Code == code && x.SupportLevel == CapabilitySupportLevel.Supported, cancellationToken);
}
