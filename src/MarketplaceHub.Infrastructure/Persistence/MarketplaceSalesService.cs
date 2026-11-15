using System.Data;
using System.Linq.Expressions;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.Trendyol;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.Extensions.Configuration;

namespace MarketplaceHub.Infrastructure.Persistence;

public sealed partial class MarketplaceSalesService(AppDbContext db, CursorCodec cursors, IConfiguration configuration, IProductVisualLookupPort productVisuals, IOrderPort orders, IReturnPort returns, IPrivateFileStorage files, TimeProvider timeProvider) : IMarketplaceSalesService
{
    // A real Trendyol package supersedes the zero-ID payment placeholder, and a
    // package another package names as its origin (a split UnPacked parent, a
    // resent original) is superseded by it. Retain their history without
    // displaying, counting or invoicing them as live packages.
    internal IQueryable<ShipmentPackage> CurrentShipmentPackages() => CurrentShipmentPackages(db);

    // Also used by the dashboard and invoicing, so their package sets follow the same rule as the order screens.
    internal static IQueryable<ShipmentPackage> CurrentShipmentPackages(AppDbContext db) => CurrentShipmentPackages(db, db.ShipmentPackages.AsNoTracking());

    // The same rule over a caller's source, e.g. tracked packages that the invoicing precheck updates.
    internal static IQueryable<ShipmentPackage> CurrentShipmentPackages(AppDbContext db, IQueryable<ShipmentPackage> packages) => packages
        .Where(package => package.RawStatus != ShipmentPackageSupersession.LocallyUnpackedRawStatus)
        .Where(package => (package.ExternalPackageId != "0"
            || !db.PlatformConnections.Any(connection => connection.TenantId == package.TenantId
                && connection.Id == package.ConnectionId && connection.PlatformCode == "TRENDYOL")
            || !db.ShipmentPackages.Any(real => real.TenantId == package.TenantId
                && real.ConnectionId == package.ConnectionId && real.OrderId == package.OrderId
                && real.ExternalPackageId != "0" && real.ExternalPackageId != ""))
            && !db.ShipmentPackages.Any(child => child.TenantId == package.TenantId
                && child.ConnectionId == package.ConnectionId && child.OrderId == package.OrderId
                && child.OriginExternalPackageId == package.ExternalPackageId
                && child.ExternalPackageId != package.ExternalPackageId));

    // The shared catalog key (CatalogKeyNormalizer), so order and return lines find variants stored by the panel
    // or the import under the same key (K1).
    private static string NormalizeCatalogKey(string? value, int maximum) => CatalogKeyNormalizer.Key(value, maximum);

    // The codes an order or return line may name a linked variant by: the link id and the marketplace's stock code
    // (a Hepsiburada link is keyed by its hbSku and keeps the merchant SKU beside it).
    internal static IEnumerable<string> LinkIdentities(MarketplaceVariantLink link)
    {
        yield return MarketplaceVariantLinkCoverage.Normalize(link.ExternalId);
        if (!string.IsNullOrWhiteSpace(link.ExternalSku)) yield return MarketplaceVariantLinkCoverage.Normalize(link.ExternalSku);
    }

    // The marketplace's own ids for the sold item in a line snapshot (Trendyol productCode/contentId, Hepsiburada
    // hbSku/listingId, Shopify variant_id), which a variant link is keyed by. They still find the linked variant after
    // the barcode or stock code was changed on one side only: Trendyol line MZ052BOD-16 is listing 1696621714, linked
    // to the panel variant still coded MZ051BYC-16, and matched no variant by its codes.
    private static readonly string[] SourceListingIdFields = ["productCode", "contentId", "hbSku", "listingId", "variantId", "variant_id"];

    internal static string[] LineMarketplaceIdentities(string? sku, string? sourceSnapshotJson)
    {
        var identities = new List<string>();
        var normalizedSku = MarketplaceVariantLinkCoverage.Normalize(sku);
        if (normalizedSku.Length > 0) identities.Add(normalizedSku);
        if (!string.IsNullOrWhiteSpace(sourceSnapshotJson))
        {
            try
            {
                using var document = JsonDocument.Parse(sourceSnapshotJson);
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                    foreach (var field in SourceListingIdFields)
                    {
                        if (!document.RootElement.TryGetProperty(field, out var value)) continue;
                        var text = value.ValueKind switch { JsonValueKind.String => value.GetString(), JsonValueKind.Number => value.GetRawText(), _ => null };
                        var normalized = MarketplaceVariantLinkCoverage.Normalize(text);
                        if (normalized.Length > 0 && normalized != "0" && !identities.Contains(normalized)) identities.Add(normalized);
                    }
            }
            catch (JsonException) { }
        }
        return identities.ToArray();
    }

    internal static string[] LineMarketplaceIdentities(OrderLine line) => LineMarketplaceIdentities(line.Sku, line.SourceSnapshotJson);

    internal static string[] CatalogLookupKeys(string? value, int maximum = 160)
    {
        var normalized = NormalizeCatalogKey(value, maximum);
        if (normalized.Length == 0) return [];
        // Stored variant keys may still be in a legacy form until the deferred recompute; look up those too.
        var keys = CatalogKeyNormalizer.LookupKeys(value, maximum).ToList();
        var withoutMarketplacePadding = normalized.TrimStart('0');
        if (withoutMarketplacePadding.Length > 0
            && withoutMarketplacePadding.Length < normalized.Length
            && withoutMarketplacePadding.Any(char.IsLetter))
            keys.Add(withoutMarketplacePadding);
        return keys.ToArray();
    }

    internal static Dictionary<string, ProductVariant> BarcodeVariantLookup(IEnumerable<ProductVariant> variants) => variants
        .Where(variant => !string.IsNullOrWhiteSpace(variant.Barcode))
        .SelectMany(variant => CatalogLookupKeys(variant.Barcode).Select(key => (Key: key, Variant: variant)))
        .GroupBy(candidate => candidate.Key, StringComparer.OrdinalIgnoreCase)
        .Where(group => group.Select(candidate => candidate.Variant.Id).Distinct().Count() == 1)
        .ToDictionary(group => group.Key, group => group.First().Variant, StringComparer.OrdinalIgnoreCase);

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

    // The Returns page reads cards (a claim with accepted and waiting lines is two cards), filtered,
    // counted and paged by the server (MarketplaceSalesService.ReturnList.cs). "latest" keeps the
    // plain claim list by last change, unsplit and unpaged, for tools and tests.
    public async Task<PageResult<ReturnListView>> ReturnsAsync(Guid tenantId, int limit, string? after, ReturnListQuery options, bool latest, CancellationToken cancellationToken, int page = 1)
    {
        if (!await HasOperationalReturnConnectionAsync(tenantId, cancellationToken)) return new([], null, false);
        if (!latest) return await ReturnCardPageAsync(tenantId, limit, page, after, options, cancellationToken);

        var query = ReturnBaseQuery(tenantId);
        ApplyReturnFilters(ref query, options);
        var totalCount = await query.CountAsync(cancellationToken);
        var claims = await query.OrderBy(x => x.LastRemoteModifiedAt == null).ThenByDescending(x => x.LastRemoteModifiedAt).ThenByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).Take(limit + 1).ToListAsync(cancellationToken);
        var rows = await ReturnViewsAsync(tenantId, claims, cancellationToken);
        return new(rows.Take(limit).ToList(), null, rows.Count > limit, totalCount);
    }

    private Task<bool> HasOperationalReturnConnectionAsync(Guid tenantId, CancellationToken cancellationToken) =>
        db.PlatformConnections.AsNoTracking()
            .AnyAsync(x => x.TenantId == tenantId
                && (x.PlatformCode == "TRENDYOL" || x.PlatformCode == "HEPSIBURADA" || x.PlatformCode == "N11" || x.PlatformCode == "PAZARAMA" || x.PlatformCode == "PTTAVM")
                && (x.Status == "ACTIVE" || x.Status == "VERIFIED"), cancellationToken);

    private IQueryable<ReturnClaim> ReturnBaseQuery(Guid tenantId) =>
        OrderQueueQueries.ListedReturns(db, tenantId, timeProvider.GetUtcNow());

    // One list view per claim, in the claims' order.
    private async Task<List<ReturnListView>> ReturnViewsAsync(Guid tenantId, List<ReturnClaim> claims, CancellationToken cancellationToken)
    {
        var orderIds = claims.Select(x => x.OrderId).Distinct().ToArray();
        var claimIds = claims.Select(x => x.Id).ToArray();
        var approvedAtByClaim = claimIds.Length == 0
            ? new Dictionary<Guid, DateTimeOffset?>()
            : await db.ReturnDecisions.AsNoTracking()
                .Where(x => x.TenantId == tenantId && claimIds.Contains(x.ClaimId) && x.Action == "APPROVE" && x.Status == "SUCCEEDED")
                .GroupBy(x => x.ClaimId)
                .Select(group => new { ClaimId = group.Key, ApprovedAt = group.Max(decision => decision.CompletedAt) })
                .ToDictionaryAsync(x => x.ClaimId, x => x.ApprovedAt, cancellationToken);
        var orders = await db.Orders.AsNoTracking().Where(x => x.TenantId == tenantId && orderIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, cancellationToken);
        var connectionIds = claims.Select(x => x.ConnectionId).Concat(orders.Values.Select(x => x.ConnectionId)).Distinct().ToArray();
        var connections = await db.PlatformConnections.AsNoTracking().Where(x => x.TenantId == tenantId && connectionIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, cancellationToken);
        var returnLines = await db.ReturnLines.AsNoTracking().Where(x => x.TenantId == tenantId && claimIds.Contains(x.ClaimId)).ToListAsync(cancellationToken);
        var orderLineIds = returnLines.Select(x => x.OrderLineId).Distinct().ToArray();
        var orderLines = await db.OrderLines.AsNoTracking().Where(x => x.TenantId == tenantId && orderLineIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, cancellationToken);
        var returnLineSkus = orderLines.Values.Select(x => x.Sku).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var returnLineSkuKeys = orderLines.Values.SelectMany(x => CatalogLookupKeys(x.Sku, 160)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var returnLineBarcodes = orderLines.Values.Select(x => x.Barcode).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var returnLineBarcodeKeys = orderLines.Values.SelectMany(x => CatalogLookupKeys(x.Barcode, 160)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var returnLinkKeys = returnLineSkuKeys.Concat(orderLines.Values.SelectMany(LineMarketplaceIdentities)).Distinct(StringComparer.Ordinal).ToArray();
        var returnVariantLinks = returnLinkKeys.Length == 0
            ? []
            : await db.MarketplaceVariantLinks.AsNoTracking()
                .Where(x => x.TenantId == tenantId
                    && connectionIds.Contains(x.ConnectionId)
                    && (returnLinkKeys.Contains(x.ExternalId.Trim().ToUpper()) || x.ExternalSku != null && returnLinkKeys.Contains(x.ExternalSku.Trim().ToUpper())))
                .ToListAsync(cancellationToken);
        var linkedReturnVariantIds = returnVariantLinks.Select(x => x.VariantId).Distinct().ToArray();
        var returnVariantIds = orderLines.Values.Where(x => x.VariantId.HasValue).Select(x => x.VariantId!.Value).Distinct().ToArray();
        var returnVariants = await db.ProductVariants.AsNoTracking().Where(x => x.TenantId == tenantId
                && (returnVariantIds.Contains(x.Id)
                    || linkedReturnVariantIds.Contains(x.Id)
                    || returnLineSkus.Contains(x.Sku)
                    || returnLineSkuKeys.Contains(x.SkuNormalized)
                    || returnLineBarcodes.Contains(x.Barcode)
                    || returnLineBarcodeKeys.Contains(x.BarcodeNormalized)))
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken);
        var returnVariantsById = returnVariants.ToDictionary(x => x.Id);
        var returnVariantsBySku = returnVariants.GroupBy(x => x.Sku, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
        var returnVariantsByNormalizedSku = returnVariants.GroupBy(x => x.SkuNormalized, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
        var returnVariantsByBarcode = BarcodeVariantLookup(returnVariants);
        var returnVariantsByMarketplaceIdentity = returnVariantLinks
            .SelectMany(link => LinkIdentities(link).Select(identity => (Key: (link.ConnectionId, ExternalId: identity), link.VariantId)))
            .GroupBy(entry => entry.Key)
            .ToDictionary(group => group.Key, group => group.First().VariantId);
        var returnImageUrls = await MediaUrls(tenantId, returnVariants.Select(x => (Guid?)x.Id), cancellationToken);
        ProductVariant? ResolveReturnVariant(OrderLine line)
        {
            if (line.VariantId is { } variantId && returnVariantsById.TryGetValue(variantId, out var direct)) return direct;
            if (returnVariantsBySku.TryGetValue(line.Sku, out var bySku)) return bySku;
            foreach (var key in CatalogLookupKeys(line.Sku, 160))
                if (returnVariantsByNormalizedSku.TryGetValue(key, out var byNormalizedSku)) return byNormalizedSku;
            foreach (var key in CatalogLookupKeys(line.Barcode, 160))
                if (returnVariantsByBarcode.TryGetValue(key, out var byBarcode)) return byBarcode;
            var connectionId = orders.GetValueOrDefault(line.OrderId)?.ConnectionId;
            foreach (var identity in LineMarketplaceIdentities(line))
                if (returnVariantsByMarketplaceIdentity.TryGetValue((connectionId ?? Guid.Empty, identity), out var linkedId) && returnVariantsById.GetValueOrDefault(linkedId) is { } linked) return linked;
            return null;
        }
        var packages = await db.ShipmentPackages.AsNoTracking().Where(x => x.TenantId == tenantId && orderIds.Contains(x.OrderId)).OrderByDescending(x => x.StatusOccurredAt).ToListAsync(cancellationToken);
        var invoices = await db.Invoices.AsNoTracking().Where(x => x.TenantId == tenantId && orderIds.Contains(x.OrderId) && x.OriginalInvoiceId == null
            && db.PlatformConnections.Any(connection => connection.TenantId == tenantId && connection.Id == x.ProviderConnectionId && (connection.Status == "ACTIVE" || connection.Status == "VERIFIED"))).OrderByDescending(x => x.CreatedAt).ToListAsync(cancellationToken);
        var invoiceIds = invoices.Select(invoice => invoice.Id).ToArray();
        var returnDeliveryStates = invoiceIds.Length == 0
            ? []
            : await db.MarketplaceDeliveryStates.AsNoTracking()
                .Where(delivery => delivery.TenantId == tenantId && invoiceIds.Contains(delivery.InvoiceId))
                .Select(delivery => new { delivery.InvoiceId, delivery.PackageId, delivery.Status, delivery.UpdatedAt })
                .ToListAsync(cancellationToken);
        var returnDeliveryAttempts = invoiceIds.Length == 0
            ? []
            : await db.MarketplaceDeliveries.AsNoTracking()
                .Where(delivery => delivery.TenantId == tenantId && invoiceIds.Contains(delivery.InvoiceId))
                .Select(delivery => new { delivery.InvoiceId, delivery.PackageId, delivery.Status })
                .ToListAsync(cancellationToken);
        // "Kısmen Onaylandı" at order level: every claim of the listed orders (also those outside this page), with
        // their lines' own statuses (OrderReturnPartialApprovalPolicy).
        var siblingClaims = orderIds.Length == 0 ? [] : await db.ReturnClaims.AsNoTracking()
            .Where(x => x.TenantId == tenantId && orderIds.Contains(x.OrderId))
            .Select(x => new { x.Id, x.OrderId, x.Status })
            .ToListAsync(cancellationToken);
        var siblingClaimIds = siblingClaims.Select(x => x.Id).ToArray();
        var siblingLineStatuses = siblingClaimIds.Length == 0 ? [] : (await db.ReturnLines.AsNoTracking()
                .Where(x => x.TenantId == tenantId && siblingClaimIds.Contains(x.ClaimId))
                .Select(x => new { x.ClaimId, x.RawStatus })
                .ToListAsync(cancellationToken))
            .GroupBy(x => x.ClaimId)
            .ToDictionary(x => x.Key, x => (IReadOnlyCollection<string?>)x.Select(line => line.RawStatus).ToList());
        var partiallyApprovedOrders = siblingClaims
            .GroupBy(x => x.OrderId)
            .Where(group => OrderReturnPartialApprovalPolicy.IsPartiallyApproved(group.Select(x => (x.Status, siblingLineStatuses.GetValueOrDefault(x.Id) ?? []))))
            .Select(group => group.Key)
            .ToHashSet();
        var rows = claims.Select(claim =>
        {
            var order = orders.GetValueOrDefault(claim.OrderId);
            var connection = order is null ? connections.GetValueOrDefault(claim.ConnectionId) : connections.GetValueOrDefault(order.ConnectionId);
            var claimConnection = connections.GetValueOrDefault(claim.ConnectionId);
            var platformCode = claimConnection?.PlatformCode ?? connection?.PlatformCode ?? "TRENDYOL";
            var claimLines = returnLines.Where(x => x.ClaimId == claim.Id).ToList();
            var package = packages.FirstOrDefault(x => x.OrderId == claim.OrderId);
            var outboundPackage = packages.FirstOrDefault(x => x.OrderId == claim.OrderId && !string.IsNullOrWhiteSpace(x.CargoTrackingNumber)) ?? package;
            var invoice = invoices.FirstOrDefault(x => x.OrderId == claim.OrderId);
            var packageDeliveryConfirmed = invoice is not null && package is not null
                && MarketplaceInvoiceDeliveryEvidencePolicy.HasConfirmedDelivery(
                    returnDeliveryStates.Where(state => state.InvoiceId == invoice.Id && state.PackageId == package.Id)
                        .OrderByDescending(state => state.UpdatedAt).Select(state => state.Status).FirstOrDefault(),
                    returnDeliveryAttempts.Any(attempt => attempt.InvoiceId == invoice.Id && attempt.PackageId == package.Id && attempt.Status == "CONFIRMED"));
            DateTimeOffset? approvedAt = claim.Status is ReturnClaimStatus.Approved or ReturnClaimStatus.Completed
                ? approvedAtByClaim.GetValueOrDefault(claim.Id) ?? claim.LastRemoteModifiedAt
                : null;
            var firstLine = claimLines.Select(x => orderLines.GetValueOrDefault(x.OrderLineId)).FirstOrDefault(x => x is not null);
            var firstVariant = firstLine is null ? null : ResolveReturnVariant(firstLine);
            var lineViews = claimLines.Select(returnLine =>
            {
                var line = orderLines.GetValueOrDefault(returnLine.OrderLineId);
                if (line is null) return null;
                var source = SourceLine(line.SourceSnapshotJson);
                var variant = ResolveReturnVariant(line);
                // The marketplace's photo with the catalog's behind it: a marketplace address that stops loading (a
                // Pazarama img.pzrmcdn.com link answered 403 on 6 Nov 2026) falls back to our own picture.
                // With neither a marketplace nor a catalog photo (the catalog may be empty) the barcode lookup the order
                // screens use gives the photo, so a return line shows what its order line shows.
                var images = ReturnDetailLineImageUrls(source.ImageUrl, variant is null ? null : returnImageUrls.GetValueOrDefault(variant.Id),
                    string.IsNullOrWhiteSpace(line.Barcode) ? string.IsNullOrWhiteSpace(line.Sku) ? variant?.Barcode : line.Sku : line.Barcode, claim.ConnectionId);
                return new OrderLineView(line.Id, line.Sku, line.Barcode ?? variant?.Barcode, line.TitleSnapshot, returnLine.Quantity, line.CancelledQuantity, line.ShippedQuantity, line.DeliveredQuantity, line.ReturnedQuantity, line.UnitPrice, line.VatRate, line.RawStatus, line.VariantId ?? variant?.Id, ReturnLineModelCode(variant, source.ModelCode), variant?.OptionSignature ?? source.OptionSignature, images.ImageUrl,
                    FallbackImageUrl: images.FallbackImageUrl,
                    ReturnAmounts: ReturnMarketplaceAmounts.Line(platformCode, line.SourceSnapshotJson, returnLine.Quantity, claim.ExternalClaimId));
            }).Where(line => line is not null).Select(line => line!).ToList();
            return new ReturnListView(claim.Id, claim.ExternalClaimId, order?.OrderNumber ?? "—", ReturnStatusWire(claim.Status, claimLines.Select(x => x.RawStatus).ToList()), claim.RawStatus, claim.ReasonText, ReturnActionDueAt(platformCode, claim.RawStatus, claim.ActionDueAt, claim.LastRemoteModifiedAt), claim.Version,
                order is null ? "—" : Customer(order.CustomerSnapshotJson, order.InvoiceAddressSnapshotJson, order.ShipmentAddressSnapshotJson).Name,
                order?.OrderedAt, order?.NetAmount ?? 0, order?.Currency ?? "TRY", claim.CargoProviderName, claim.CargoTrackingNumber, lineViews.FirstOrDefault()?.ImageUrl, claimLines.Count, firstLine?.Barcode ?? firstVariant?.Barcode,
                lineViews, package?.ExternalPackageId, order is null ? "FATURA_BEKLIYOR" : ReturnInvoiceLabel(invoice, package?.MarketplaceInvoiceStatus ?? MarketplaceInvoiceStatus.Unknown, order.CustomerSnapshotJson, package is null ? [] : [package.RawStatus], packageDeliveryConfirmed), order?.GrossAmount ?? 0, order?.DiscountAmount ?? 0,
                order is not null && Customer(order.CustomerSnapshotJson, order.InvoiceAddressSnapshotJson, order.ShipmentAddressSnapshotJson).IsMicroExport,
                claimConnection?.Id ?? connection?.Id, platformCode, claimConnection?.DisplayName ?? connection?.DisplayName ?? "Trendyol", outboundPackage?.CargoProviderExternalId, outboundPackage?.CargoTrackingNumber, claim.ReasonCode, approvedAt, claim.CargoTrackingLink,
                claimLines.Where(x => !string.IsNullOrWhiteSpace(x.RawStatus)).GroupBy(x => x.OrderLineId).ToDictionary(x => x.Key, x => Wire(MarketplaceReturnStatus.Canonicalize(x.First().RawStatus))),
                IsEstimatedReturnDueAt(platformCode, claim),
                ReturnClaimAmounts(claimLines.Count, lineViews),
                CustomerRequest: claim.CustomerRequest,
                OrderReturnStatus: partiallyApprovedOrders.Contains(claim.OrderId) ? ReturnClaimPartialApprovalPolicy.PartiallyApproved : null,
                RequestedAt: claim.RequestedAt);
        }).ToList();
        return rows;
    }

    // The status the return screens show: PARTIALLY_APPROVED ("Kısmen Onaylandı") for an approved claim whose lines
    // were decided both ways (ReturnClaimPartialApprovalPolicy), otherwise the stored status.
    internal static string ReturnStatusWire(ReturnClaimStatus status, IReadOnlyCollection<string?> lineStatuses) =>
        ReturnClaimPartialApprovalPolicy.IsPartiallyApproved(status, lineStatuses) ? ReturnClaimPartialApprovalPolicy.PartiallyApproved : Wire(status);

    internal static string? ReturnLineModelCode(ProductVariant? variant, string? sourceModelCode) =>
        string.IsNullOrWhiteSpace(variant?.ModelCode) ? sourceModelCode : variant.ModelCode;

    // The deadline is an estimate when the marketplace sent none: stored as
    // derived, or derived here from the status.
    internal static bool IsEstimatedReturnDueAt(string? platformCode, ReturnClaim claim) =>
        claim.ActionDueAt is null
            ? ReturnActionDueAt(platformCode, claim.RawStatus, null, claim.LastRemoteModifiedAt) is not null
            : claim.ActionDueAtEstimated;

    internal static DateTimeOffset? ReturnActionDueAt(string? platformCode, string? rawStatus, DateTimeOffset? actionDueAt, DateTimeOffset? lastRemoteModifiedAt)
    {
        if (actionDueAt is not null) return actionDueAt;
        var isTrendyolWaitingInAction = string.Equals(platformCode, "TRENDYOL", StringComparison.OrdinalIgnoreCase)
            && (string.Equals(rawStatus?.Trim(), "WaitingInAction", StringComparison.OrdinalIgnoreCase)
                || string.Equals(rawStatus?.Trim(), "WAITING_IN_ACTION", StringComparison.OrdinalIgnoreCase));
        if (isTrendyolWaitingInAction) return lastRemoteModifiedAt?.AddHours(48);
        var isTrendyolRejected = string.Equals(platformCode, "TRENDYOL", StringComparison.OrdinalIgnoreCase)
            && (string.Equals(rawStatus?.Trim(), "Rejected", StringComparison.OrdinalIgnoreCase)
                || string.Equals(rawStatus?.Trim(), "REJECTED", StringComparison.OrdinalIgnoreCase));
        return isTrendyolRejected ? lastRemoteModifiedAt?.AddDays(7) : null;
    }

    private void ApplyReturnFilters(ref IQueryable<ReturnClaim> query, ReturnListQuery options)
    {
        var status = options.Status?.Trim().ToUpperInvariant();
        if (!string.IsNullOrWhiteSpace(status) && status != "ALL")
        {
            query = status switch
            {
                "REQUESTED" => query.Where(x => x.Status == ReturnClaimStatus.Requested),
                "SHIPPING" => query.Where(x => x.Status == ReturnClaimStatus.AwaitingShipment || x.Status == ReturnClaimStatus.InTransit),
                "ACTION_REQUIRED" => query.Where(x => x.Status == ReturnClaimStatus.ActionRequired),
                "APPROVED" => query.Where(x => x.Status == ReturnClaimStatus.Approved || x.Status == ReturnClaimStatus.Completed
                    || (x.Status == ReturnClaimStatus.ActionRequired && db.ReturnLines.Any(line => line.TenantId == x.TenantId && line.ClaimId == x.Id && line.RawStatus != null && line.RawStatus.ToUpper() == "ACCEPTED"))),
                "REJECTED" => query.Where(x => x.Status == ReturnClaimStatus.Rejected || x.Status == ReturnClaimStatus.Cancelled),
                "DISPUTED" => query.Where(x => x.Status == ReturnClaimStatus.Disputed),
                // The UI's Analysis tab is reserved for explicitly submitted
                // analysis cases. ActionRequired claims belong only to their
                // own operational tab and must not leak into Analysis.
                "REVIEW" => query.Where(_ => false),
                "SUSPENDED" => query.Where(_ => false),
                _ => query.Where(_ => false)
            };
        }

        var search = options.Search?.Trim();
        var searchCustomer = search is not null && SearchesCustomerSnapshot(search);
        var customerPattern = CustomerSnapshotSearchPattern(search ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(search))
        {
            // The page's own search ignores case the Turkish way; this database pre-filter must
            // never be narrower than it.
            // The reason and the product title are folded the Turkish way on both sides (TurkishSearchText).
            var (asTyped, folded) = SearchPatterns(search);
            var reasonCodeMatches = db.ReturnClaims.Where(TurkishSearchText.FoldedILike<ReturnClaim>(claim => claim.ReasonCode, folded, LikeEscape));
            var reasonTextMatches = db.ReturnClaims.Where(TurkishSearchText.FoldedILike<ReturnClaim>(claim => claim.ReasonText, folded, LikeEscape));
            var titleMatches = db.OrderLines.Where(TurkishSearchText.FoldedILike<OrderLine>(line => line.TitleSnapshot, folded, LikeEscape));
            query = query.Where(claim => EF.Functions.ILike(claim.ExternalClaimId, asTyped, LikeEscape)
                || reasonCodeMatches.Any(match => match.TenantId == claim.TenantId && match.Id == claim.Id)
                || reasonTextMatches.Any(match => match.TenantId == claim.TenantId && match.Id == claim.Id)
                || db.Orders.Any(order => order.TenantId == claim.TenantId && order.Id == claim.OrderId
                    && (EF.Functions.ILike(order.OrderNumber, asTyped, LikeEscape) || (searchCustomer && Regex.IsMatch(order.CustomerSnapshotJson, customerPattern, RegexOptions.IgnoreCase))))
                || db.ReturnLines.Any(returnLine => returnLine.TenantId == claim.TenantId && returnLine.ClaimId == claim.Id
                    && (titleMatches.Any(line => line.TenantId == returnLine.TenantId && line.Id == returnLine.OrderLineId)
                        || db.OrderLines.Any(line => line.TenantId == returnLine.TenantId && line.Id == returnLine.OrderLineId
                            && ((line.Barcode != null && EF.Functions.ILike(line.Barcode, asTyped, LikeEscape)) || EF.Functions.ILike(line.Sku, asTyped, LikeEscape))))));
        }

        var customer = options.Customer?.Trim();
        if (!string.IsNullOrWhiteSpace(customer))
        {
            var customerFilterPattern = CustomerSnapshotSearchPattern(customer);
            var customerNumber = SearchPatterns(customer).AsTyped;
            query = query.Where(x => db.Orders.Any(order => order.TenantId == x.TenantId && order.Id == x.OrderId && (Regex.IsMatch(order.CustomerSnapshotJson, customerFilterPattern, RegexOptions.IgnoreCase) || EF.Functions.ILike(order.OrderNumber, customerNumber, LikeEscape))));
        }
        var orderNumber = options.OrderNumber?.Trim();
        if (!string.IsNullOrWhiteSpace(orderNumber))
            query = query.Where(x => db.Orders.Any(order => order.TenantId == x.TenantId && order.Id == x.OrderId && order.OrderNumber.Contains(orderNumber)));
        var claimCode = options.ClaimCode?.Trim();
        if (!string.IsNullOrWhiteSpace(claimCode)) query = query.Where(x => x.ExternalClaimId.Contains(claimCode));
        var barcode = options.Barcode?.Trim();
        if (!string.IsNullOrWhiteSpace(barcode))
            query = query.Where(x => db.ReturnLines.Any(returnLine => returnLine.TenantId == x.TenantId && returnLine.ClaimId == x.Id && db.OrderLines.Any(line => line.TenantId == x.TenantId && line.Id == returnLine.OrderLineId && ((line.Barcode != null && line.Barcode.Contains(barcode)) || line.Sku.Contains(barcode)))));
        var reason = options.Reason?.Trim();
        if (!string.IsNullOrWhiteSpace(reason)) query = query.Where(x => x.ReasonCode == reason || (x.ReasonText != null && x.ReasonText.Contains(reason)));
        // "İade Talep Tarihi" (owner, 11 Nov 2026): the marketplace's own request date of the claim (ReturnClaim.RequestedAt), the
        // page sending Istanbul day bounds; a claim the marketplace has not dated is left out only while a range is set.
        if (options.DateFrom?.ToUniversalTime() is { } dateFrom) query = query.Where(x => x.RequestedAt != null && x.RequestedAt >= dateFrom);
        if (options.DateTo?.ToUniversalTime() is { } dateTo) query = query.Where(x => x.RequestedAt != null && x.RequestedAt <= dateTo);
    }

    public async Task<ServiceResult<ReturnDetailView>> ReturnAsync(Guid tenantId, Guid id, CancellationToken cancellationToken)
    {
        var claim = await db.ReturnClaims.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id
            && db.PlatformConnections.Any(connection => connection.TenantId == tenantId && connection.Id == x.ConnectionId && (connection.Status == "ACTIVE" || connection.Status == "VERIFIED")), cancellationToken);
        if (claim is null) return NotFound<ReturnDetailView>();
        var order = await db.Orders.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == claim.OrderId, cancellationToken);
        if (order is null) return NotFound<ReturnDetailView>();
        var stageConnection = await IsStageConnection(tenantId, claim.ConnectionId, cancellationToken);
        var externalWritesEnabled = stageConnection || await WritesEnabled(tenantId, claim.ConnectionId, cancellationToken);
        var platformCode = await db.PlatformConnections.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == claim.ConnectionId).Select(x => x.PlatformCode).SingleOrDefaultAsync(cancellationToken);
        var isHepsiburada = string.Equals(platformCode, "HEPSIBURADA", StringComparison.OrdinalIgnoreCase);
        var awaitingPreApproval = isHepsiburada && HepsiburadaReturnActionPolicy.IsAwaitingPreApproval(claim.RawStatus);
        // ActionRequired is the provider's decision point. Supported return
        // adapters expose explicit APPROVE/REJECT implementations, so the
        // UI must not hide those controls just because a production capability
        // evidence row was not recorded. The write policy is still enforced by
        // ProcessReturnActionInstantAsync enforces the policy before any external
        // request is made. Return decisions are intentionally not queued.
        var openDecisionLines = await OpenReturnDecisions(tenantId, claim.Id, isHepsiburada).Select(x => x.ReturnLineIds).ToListAsync(cancellationToken);
        var sourceLines = await db.ReturnLines.AsNoTracking().Where(x => x.TenantId == tenantId && x.ClaimId == id).OrderBy(x => x.Id).ToListAsync(cancellationToken);
        bool HeldByDecision(Guid lineId) => openDecisionLines.Any(lineIds => ReturnDecisionLinePolicy.Overlaps(lineIds, [lineId]));
        // The claim waits on a sent decision only while every line still open for a decision is covered by one:
        // a decision on some lines leaves the others free for their own.
        var decisionPending = openDecisionLines.Count > 0
            && sourceLines.Where(line => isHepsiburada || ReturnLineStatusPolicy.IsActionable(line.RawStatus)).All(line => HeldByDecision(line.Id));
        var actions = claim.Status switch
        {
            ReturnClaimStatus.Requested when awaitingPreApproval => HepsiburadaReturnActionPolicy.AllowedActions(claim.RawStatus, decisionPending),
            // "Teslim Aldım" opens Onayla/Reddet only where the decision covers the whole claim; Trendyol, n11 and Pazarama
            // decide by the lines' own statuses, which stay in transit until the marketplace says otherwise (SIPARIS-STOK-1).
            ReturnClaimStatus.Requested or ReturnClaimStatus.InTransit => ReturnReceiptAllowed(platformCode) ? ["RECEIVE"] : Array.Empty<string>(),
            ReturnClaimStatus.AwaitingShipment when isHepsiburada => Array.Empty<string>(),
            ReturnClaimStatus.ActionRequired when isHepsiburada => HepsiburadaReturnActionPolicy.AllowedActions(claim.RawStatus, decisionPending),
            // n11 approvals and denials both go through the API (ClaimReturnApprove / ClaimReturnDeny); a denial always
            // carries the shipment back to the buyer or reason 41 (N11ReturnDenyRule) and is never sent on its own.
            ReturnClaimStatus.ActionRequired when !decisionPending => ReturnActions,
            ReturnClaimStatus.ActionRequired => Array.Empty<string>(),
            _ => await CapabilityValues(tenantId, claim.ConnectionId, MarketplaceCapabilities.ReturnWrite, "allowedActions", cancellationToken)
        };
        var approvedAt = claim.Status is ReturnClaimStatus.Approved or ReturnClaimStatus.Completed
            ? await db.ReturnDecisions.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.ClaimId == claim.Id && x.Action == "APPROVE" && x.Status == "SUCCEEDED")
                .OrderByDescending(x => x.CompletedAt)
                .Select(x => x.CompletedAt)
                .FirstOrDefaultAsync(cancellationToken)
            : null;
        approvedAt ??= claim.Status is ReturnClaimStatus.Approved or ReturnClaimStatus.Completed ? claim.LastRemoteModifiedAt : null;
        var orderLineIds = sourceLines.Select(x => x.OrderLineId).ToArray();
        var orderLines = await db.OrderLines.AsNoTracking().Where(x => x.TenantId == tenantId && orderLineIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, cancellationToken);
        var dispositions = await db.ReturnStockDispositions.AsNoTracking().Where(x => x.TenantId == tenantId && x.ClaimId == id).GroupBy(x => x.ReturnLineId).Select(x => new { ReturnLineId = x.Key, Quantity = x.Sum(y => y.Quantity) }).ToDictionaryAsync(x => x.ReturnLineId, x => x.Quantity, cancellationToken);
        var pendingStockLineIds = (await db.ReturnDecisions.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.ClaimId == id && x.StockStatus == ReturnPendingStockPolicy.Pending)
                .Select(x => x.StockLinesJson)
                .ToListAsync(cancellationToken))
            .SelectMany(json => ReturnPendingStock.Read(json).Select(line => line.ReturnLineId))
            .ToHashSet();
        var lineSkuKeys = orderLines.Values.SelectMany(x => CatalogLookupKeys(x.Sku, 160)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var lineBarcodeKeys = orderLines.Values.SelectMany(x => CatalogLookupKeys(x.Barcode, 160)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var linkKeys = lineSkuKeys.Concat(orderLines.Values.SelectMany(LineMarketplaceIdentities)).Distinct(StringComparer.Ordinal).ToArray();
        var variantLinks = linkKeys.Length == 0
            ? []
            : await db.MarketplaceVariantLinks.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.ConnectionId == claim.ConnectionId
                    && (linkKeys.Contains(x.ExternalId.Trim().ToUpper()) || x.ExternalSku != null && linkKeys.Contains(x.ExternalSku.Trim().ToUpper())))
                .ToListAsync(cancellationToken);
        var candidateVariantIds = orderLines.Values.Where(x => x.VariantId.HasValue).Select(x => x.VariantId!.Value)
            .Concat(variantLinks.Select(x => x.VariantId)).Distinct().ToArray();
        var variants = await db.ProductVariants.AsNoTracking()
            .Where(x => x.TenantId == tenantId
                && (candidateVariantIds.Contains(x.Id)
                    || lineSkuKeys.Contains(x.SkuNormalized)
                    || x.BarcodeNormalized != null && lineBarcodeKeys.Contains(x.BarcodeNormalized)))
            .ToListAsync(cancellationToken);
        var variantsById = variants.ToDictionary(x => x.Id);
        var variantsBySku = variants.GroupBy(x => x.SkuNormalized, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
        var variantsByBarcode = BarcodeVariantLookup(variants);
        var variantsByExternalSku = variantLinks
            .SelectMany(link => LinkIdentities(link).Select(identity => (Identity: identity, link.VariantId)))
            .GroupBy(x => x.Identity, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.First().VariantId, StringComparer.Ordinal);
        ProductVariant? ResolveReturnDetailVariant(OrderLine line)
        {
            if (line.VariantId is { } directId && variantsById.TryGetValue(directId, out var direct)) return direct;
            foreach (var key in CatalogLookupKeys(line.Sku, 160))
                if (variantsBySku.TryGetValue(key, out var bySku)) return bySku;
            foreach (var key in CatalogLookupKeys(line.Barcode, 160))
                if (variantsByBarcode.TryGetValue(key, out var byBarcode)) return byBarcode;
            foreach (var identity in LineMarketplaceIdentities(line))
                if (variantsByExternalSku.TryGetValue(identity, out var linkedId) && variantsById.GetValueOrDefault(linkedId) is { } linked) return linked;
            return null;
        }
        var resolvedVariants = orderLines.Values.Select(ResolveReturnDetailVariant).Where(x => x is not null).Select(x => x!).ToArray();
        var variantIds = resolvedVariants.Select(x => x.Id).Distinct().ToArray();
        var inventoryVariants = await db.InventoryItems.AsNoTracking().Where(x => x.TenantId == tenantId && variantIds.Contains(x.VariantId) && x.LocationCode == "MAIN").Select(x => x.VariantId).ToListAsync(cancellationToken);
        var imageUrls = await MediaUrls(tenantId, variantIds.Select(x => (Guid?)x), cancellationToken);
        var lines = sourceLines.Select(line =>
        {
            var source = orderLines.GetValueOrDefault(line.OrderLineId);
            var sourceSnapshot = source is null ? null : SourceLine(source.SourceSnapshotJson);
            var variant = source is null ? null : ResolveReturnDetailVariant(source);
            var disposed = dispositions.GetValueOrDefault(line.Id);
            var images = ReturnDetailLineImageUrls(sourceSnapshot?.ImageUrl, variant is null ? null : imageUrls.GetValueOrDefault(variant.Id), string.IsNullOrWhiteSpace(source?.Barcode) ? source?.Sku : source.Barcode, claim.ConnectionId);
            return new ReturnLineView(line.Id, line.ExternalLineId, line.OrderLineId, source?.Sku ?? "—", source?.Barcode, source?.TitleSnapshot ?? "—", line.Quantity, disposed, Math.Max(0, line.Quantity - disposed), source?.UnitPrice ?? 0,
                images.ImageUrl,
                variant is not null && inventoryVariants.Contains(variant.Id),
                variant?.OptionSignature ?? sourceSnapshot?.OptionSignature,
                variant?.ModelCode ?? sourceSnapshot?.ModelCode,
                string.IsNullOrWhiteSpace(line.RawStatus) ? null : Wire(MarketplaceReturnStatus.Canonicalize(line.RawStatus)),
                source is null ? null : ReturnMarketplaceAmounts.Line(platformCode, source.SourceSnapshotJson, line.Quantity, claim.ExternalClaimId),
                line.AcceptDetail, line.MarketplaceNote, line.Resolved, line.AcceptedBySeller,
                HeldByDecision(line.Id), ReturnDecisionLinePolicy.StockDispositionAllowed(claim.Status, line.RawStatus), pendingStockLineIds.Contains(line.Id),
                images.FallbackImageUrl);
        }).ToList();
        var stockDispositionAvailable = sourceLines.Count == 0
            ? ReturnDecisionLinePolicy.StockDispositionAllowed(claim.Status, null)
            : lines.Any(line => line.StockDispositionAvailable);
        var package = await db.ShipmentPackages.AsNoTracking().Where(x => x.TenantId == tenantId && x.OrderId == order.Id).OrderByDescending(x => x.StatusOccurredAt).FirstOrDefaultAsync(cancellationToken);
        var customer = Customer(order.CustomerSnapshotJson, order.InvoiceAddressSnapshotJson, order.ShipmentAddressSnapshotJson);
        return ServiceResult<ReturnDetailView>.Ok(new(claim.Id, claim.ExternalClaimId, order.OrderNumber, ReturnStatusWire(claim.Status, sourceLines.Select(line => line.RawStatus).ToList()), claim.RawStatus, claim.ReasonCode, claim.ReasonText, ReturnActionDueAt(platformCode, claim.RawStatus, claim.ActionDueAt, claim.LastRemoteModifiedAt), actions, claim.Version,
            customer.Name, order.OrderedAt, order.NetAmount, order.Currency, claim.CargoProviderName, claim.CargoTrackingNumber, lines, stockDispositionAvailable, approvedAt, externalWritesEnabled, decisionPending, platformCode, claim.CargoTrackingLink, IsEstimatedReturnDueAt(platformCode, claim),
            ReturnMarketplaceAmounts.Total(lines.Select(line => line.ReturnAmounts).ToList()), claim.CustomerRequest,
            ReturnDecisionScope.IsWholeClaim(platformCode), IsN11(platformCode)));
    }

    private static bool IsN11(string? platformCode) => string.Equals(platformCode, "N11", StringComparison.OrdinalIgnoreCase);

    // Marking a return received only helps where the decision is sent for the whole claim (Hepsiburada, Shopify); elsewhere
    // the approve/reject call needs lines the marketplace itself reports as waiting, so the step would open dead buttons.
    internal static bool ReturnReceiptAllowed(string? platformCode) => ReturnDecisionScope.IsWholeClaim(platformCode);

    // The carriers an n11 denial can name for the shipment back to the buyer; other marketplaces list none.
    public async Task<ServiceResult<IReadOnlyList<ReturnShipmentCompany>>> ReturnShipmentCompaniesAsync(Guid tenantId, Guid id, string correlationId, CancellationToken cancellationToken)
    {
        var claim = await db.ReturnClaims.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id
            && db.PlatformConnections.Any(connection => connection.TenantId == tenantId && connection.Id == x.ConnectionId && (connection.Status == "ACTIVE" || connection.Status == "VERIFIED")), cancellationToken);
        if (claim is null) return NotFound<IReadOnlyList<ReturnShipmentCompany>>();
        var context = new AdapterContext(tenantId, claim.ConnectionId, correlationId, $"return-shipment-companies:{claim.ConnectionId:N}", timeProvider.GetUtcNow().AddSeconds(30));
        var result = await returns.ReturnShipmentCompaniesAsync(context, cancellationToken);
        return result.IsSuccess
            ? ServiceResult<IReadOnlyList<ReturnShipmentCompany>>.Ok(result.Value!)
            : ServiceResult<IReadOnlyList<ReturnShipmentCompany>>.Fail(result.Error!.Code, result.Error.SafeMessage, result.Error.HttpStatus ?? 502);
    }

    // A claim's amounts over all its return lines: a line whose order line is missing counts as one without amounts.
    internal static ReturnAmountsView? ReturnClaimAmounts(int returnLineCount, IReadOnlyCollection<OrderLineView> lineViews) =>
        lineViews.Count < returnLineCount ? null : ReturnMarketplaceAmounts.Total(lineViews.Select(line => line.ReturnAmounts).ToList());

    // Each marketplace says per rejection reason whether evidence is needed;
    // the list changes rarely, so it is read at most once an hour per store.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, (DateTimeOffset ReadAt, IReadOnlyList<ReturnIssueReason> Reasons)> IssueReasonCache = new();
    private static readonly TimeSpan IssueReasonCacheLifetime = TimeSpan.FromHours(1);

    private async Task<IReadOnlyList<ReturnIssueReason>?> CachedIssueReasonsAsync(Guid tenantId, Guid connectionId, string correlationId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        if (IssueReasonCache.TryGetValue(connectionId, out var cached) && now - cached.ReadAt < IssueReasonCacheLifetime) return cached.Reasons;
        var result = await returns.IssueReasonsAsync(new AdapterContext(tenantId, connectionId, correlationId, $"return-issue-reasons:{connectionId:N}", now.AddSeconds(30)), cancellationToken);
        if (!result.IsSuccess || result.Value is null) return null;
        IssueReasonCache[connectionId] = (now, result.Value);
        return result.Value;
    }

    internal static bool RejectEvidenceRequired(string? reasonCode, IReadOnlyList<ReturnIssueReason>? reasons, string? platformCode)
    {
        var code = reasonCode?.Trim();
        if (reasons?.FirstOrDefault(reason => string.Equals(reason.Id, code, StringComparison.Ordinal)) is { } reason) return reason.EvidenceRequired;
        // The list could not be read (or does not name the reason): Trendyol's
        // documented no-evidence reasons stay optional, everything else needs evidence.
        return !(string.Equals(platformCode, "TRENDYOL", StringComparison.OrdinalIgnoreCase) && code is "1651" or "451" or "2101");
    }

    public async Task<ServiceResult<IReadOnlyList<ReturnIssueReason>>> ReturnIssueReasonsAsync(Guid tenantId, Guid id, string correlationId, CancellationToken cancellationToken)
    {
        var claim = await db.ReturnClaims.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id
            && db.PlatformConnections.Any(connection => connection.TenantId == tenantId && connection.Id == x.ConnectionId && (connection.Status == "ACTIVE" || connection.Status == "VERIFIED")), cancellationToken);
        if (claim is null) return NotFound<IReadOnlyList<ReturnIssueReason>>();
        var context = new AdapterContext(tenantId, claim.ConnectionId, correlationId, $"return-issue-reasons:{claim.ConnectionId:N}", timeProvider.GetUtcNow().AddSeconds(30));
        var result = await returns.IssueReasonsAsync(context, cancellationToken);
        return result.IsSuccess
            ? ServiceResult<IReadOnlyList<ReturnIssueReason>>.Ok(result.Value!)
            : ServiceResult<IReadOnlyList<ReturnIssueReason>>.Fail(result.Error!.Code, result.Error.SafeMessage, result.Error.HttpStatus ?? 502);
    }

    public Task<ServiceResult<Guid>> EnqueueReturnSyncAsync(Guid tenantId, Guid connectionId, string correlationId, CancellationToken cancellationToken) => EnqueueRead(tenantId, connectionId, MarketplaceCapabilities.ReturnRead, MarketplaceJobTypes.ReturnSync, JsonSerializer.Serialize(new { connectionId, forceFull = true }), correlationId, cancellationToken);

    public Task<ServiceResult<Guid>> EnqueueQuestionSyncAsync(Guid tenantId, Guid connectionId, string? kind, string correlationId, CancellationToken cancellationToken) => EnqueueRead(tenantId, connectionId, MarketplaceCapabilities.QuestionRead, MarketplaceJobTypes.QuestionSync, JsonSerializer.Serialize(new { connectionId, kind }), correlationId, cancellationToken);

    public async Task<ServiceResult<ReturnDetailView>> MarkReturnReceivedAsync(Guid tenantId, Guid userId, Guid claimId, long expectedVersion, string idempotencyKey, string correlationId, CancellationToken cancellationToken)
    {
        var claim = await db.ReturnClaims.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == claimId
            && db.PlatformConnections.Any(connection => connection.TenantId == tenantId && connection.Id == x.ConnectionId && (connection.Status == "ACTIVE" || connection.Status == "VERIFIED")), cancellationToken);
        if (claim is null) return NotFound<ReturnDetailView>();
        if (claim.Version != expectedVersion) return Precondition<ReturnDetailView>(claim.Version);
        if (claim.Status is not (ReturnClaimStatus.Requested or ReturnClaimStatus.InTransit)) return ServiceResult<ReturnDetailView>.Fail("RETURN_RECEIPT_NOT_ALLOWED", "Teslim alındı işlemi yalnız talep oluşturulan veya kargodaki iadeler için kullanılabilir.", 409);
        var platformCode = await db.PlatformConnections.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == claim.ConnectionId).Select(x => x.PlatformCode).SingleOrDefaultAsync(cancellationToken);
        if (!ReturnReceiptAllowed(platformCode)) return ServiceResult<ReturnDetailView>.Fail("RETURN_RECEIPT_NOT_ALLOWED", "Bu pazaryerinde iade kararı, pazaryeri iadeyi aksiyon bekliyor durumuna aldığında açılır; teslim alındı işareti kullanılmaz.", 409);
        if (!ReturnClaimStateMachine.CanTransition(claim.Status, ReturnClaimStatus.ActionRequired)) return ServiceResult<ReturnDetailView>.Fail("RETURN_STATE_CONFLICT", "İade durumu aksiyon bekliyor durumuna geçirilemedi.", 409);

        var now = timeProvider.GetUtcNow();
        claim.Status = ReturnClaimStatus.ActionRequired;
        claim.RawStatus = "RECEIVED_BY_SELLER";
        claim.UpdatedAt = now;
        claim.Version++;
        db.AuditLogs.Add(new AuditLog
        {
            TenantId = tenantId,
            ActorUserId = userId,
            Action = "RETURN_RECEIVED_BY_SELLER",
            TargetType = "ReturnClaim",
            TargetId = claimId.ToString("D"),
            Reason = "Kargo fiziksel olarak teslim alındı; iade inceleme aksiyonları açıldı.",
            CorrelationId = correlationId,
            CreatedAt = now
        });
        await db.SaveChangesAsync(cancellationToken);
        return await ReturnAsync(tenantId, claimId, cancellationToken);
    }

    public async Task<ServiceResult<ReturnDetailView>> ProcessReturnActionInstantAsync(Guid tenantId, Guid userId, Guid claimId, long expectedVersion, ReturnDecisionCommand command, string idempotencyKey, string correlationId, CancellationToken cancellationToken)
    {
        var normalizedKey = idempotencyKey.Trim();
        var prior = await db.ReturnDecisions.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.IdempotencyKey == normalizedKey, cancellationToken);
        if (prior is not null)
        {
            if (prior.ClaimId != claimId) return ServiceResult<ReturnDetailView>.Fail("IDEMPOTENCY_KEY_CONFLICT", "Bu istek anahtarı başka bir iade için kullanılmış; sayfayı yenileyip yeniden deneyin.", 409);
            if (prior.Status is "SUCCEEDED" or "SUBMITTED") return await ReturnAsync(tenantId, claimId, cancellationToken);
            if (prior.Status is "PENDING" or "MANUAL_REVIEW") return ServiceResult<ReturnDetailView>.Fail("RETURN_DECISION_IN_PROGRESS", "Bu iade kararının sonucu henüz kesinleşmedi.", 409);
            return ServiceResult<ReturnDetailView>.Fail(prior.ErrorCode ?? "RETURN_ACTION_FAILED", "Bu Idempotency-Key ile başlatılan iade kararı başarısız oldu; yeni bir anahtar ile tekrar deneyin.", 409);
        }

        var claim = await db.ReturnClaims.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == claimId
            && db.PlatformConnections.Any(connection => connection.TenantId == tenantId && connection.Id == x.ConnectionId && (connection.Status == "ACTIVE" || connection.Status == "VERIFIED")), cancellationToken);
        if (claim is null) return NotFound<ReturnDetailView>();
        var platformCode = await db.PlatformConnections.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == claim.ConnectionId).Select(x => x.PlatformCode).SingleAsync(cancellationToken);
        var isHepsiburada = string.Equals(platformCode, "HEPSIBURADA", StringComparison.OrdinalIgnoreCase);
        var effectType = isHepsiburada ? "HEPSIBURADA_INSTANT_RETURN_ACTION" : "TRENDYOL_INSTANT_RETURN_ACTION";
        if (claim.Version != expectedVersion) return Precondition<ReturnDetailView>(claim.Version);
        var action = command.Action.Trim().ToUpperInvariant();
        var preApprovalConfirm = action == "PREAPPROVAL_CONFIRM";
        var awaitingPreApproval = isHepsiburada && HepsiburadaReturnActionPolicy.IsAwaitingPreApproval(claim.RawStatus);
        if (action is not ("APPROVE" or "REJECT") && !preApprovalConfirm) return Invalid<ReturnDetailView>("action", "İade aksiyonu APPROVE, PREAPPROVAL_CONFIRM veya REJECT olmalıdır.");
        var isN11 = IsN11(platformCode);
        var wholeClaim = ReturnDecisionScope.IsWholeClaim(platformCode);
        if (action == "REJECT" && isN11 && N11ReturnDenyRule.Problem(command.ReasonCode, command.Explanation, command.ReturnShipment?.ShipmentCompanyId, command.ReturnShipment?.TrackingNumber) is { } denyProblem)
            return Invalid<ReturnDetailView>("returnShipment", denyProblem);
        if (action == "REJECT" && isN11 && command.EvidenceAssetIds is { Count: > 0 }) return Invalid<ReturnDetailView>("evidenceAssetIds", "N11 reddi kanıt dosyası almaz.");
        if (command.ReturnShipment is not null && !(action == "REJECT" && isN11)) return Invalid<ReturnDetailView>("returnShipment", "Geri gönderim bilgisi yalnız N11 iadesi reddedilirken gönderilir.");
        if (preApprovalConfirm && !awaitingPreApproval) return ServiceResult<ReturnDetailView>.Fail("HEPSIBURADA_PREAPPROVAL_ACTION_NOT_ALLOWED", "İnceleme için geri gönderim yalnız Hepsiburada AwaitingPreApproval durumunda kullanılabilir.", 409);
        if (claim.Status != ReturnClaimStatus.ActionRequired && !(awaitingPreApproval && claim.Status == ReturnClaimStatus.Requested)) return ServiceResult<ReturnDetailView>.Fail("RETURN_ACTION_NOT_ALLOWED", "İade aksiyonu yalnız sağlayıcının aksiyon beklediği durumda kullanılabilir.", 409);

        var claimLines = await db.ReturnLines.AsNoTracking().Where(x => x.TenantId == tenantId && x.ClaimId == claimId).Select(x => new { x.Id, x.RawStatus }).ToListAsync(cancellationToken);
        var claimLineIds = claimLines.Select(x => x.Id).ToList();
        if (claimLineIds.Count == 0) return Invalid<ReturnDetailView>("returnLineIds", "İade işleminde en az bir ürün satırı bulunmalıdır.");
        // Lines the marketplace already settled (for example one accepted on Trendyol) are not sent again.
        var actionableLineIds = isHepsiburada ? claimLineIds : claimLines.Where(x => ReturnLineStatusPolicy.IsActionable(x.RawStatus)).Select(x => x.Id).ToList();
        if (actionableLineIds.Count == 0) return ServiceResult<ReturnDetailView>.Fail("RETURN_ACTION_NOT_ALLOWED", "Bu iadenin işlem bekleyen ürün satırı kalmadı.", 409);
        // Only an open decision on the same lines holds this one back; the claim's other lines take their own decision.
        var openDecisionLines = await OpenReturnDecisions(tenantId, claimId, isHepsiburada).Select(x => x.ReturnLineIds).ToListAsync(cancellationToken);
        // Without a selection the decision takes the waiting lines that no open decision covers yet.
        var freeLineIds = isHepsiburada ? actionableLineIds : actionableLineIds.Where(lineId => !openDecisionLines.Any(lineIds => ReturnDecisionLinePolicy.Overlaps(lineIds, [lineId]))).ToList();
        var returnLineIds = command.ReturnLineIds?.Distinct().ToArray() ?? (freeLineIds.Count > 0 ? freeLineIds : actionableLineIds).ToArray();
        if (returnLineIds.Length == 0 || returnLineIds.Except(claimLineIds).Any()) return Invalid<ReturnDetailView>("returnLineIds", "İşlem yapılacak ürün satırları bu iadeye ait olmalıdır.");
        returnLineIds = returnLineIds.Intersect(actionableLineIds).ToArray();
        if (returnLineIds.Length == 0) return ServiceResult<ReturnDetailView>.Fail("RETURN_ACTION_NOT_ALLOWED", "Seçilen ürün satırları pazaryerinde zaten sonuçlanmış.", 409);
        // Hepsiburada and Shopify decide the whole claim (ReturnDecisionScope): a choice of some lines would be sent as
        // the whole claim's decision, so it is refused instead of being recorded as a partial one.
        if (wholeClaim && returnLineIds.Length != actionableLineIds.Count) return Invalid<ReturnDetailView>("returnLineIds", $"{(isHepsiburada ? "Hepsiburada" : "Shopify")} talep kabul/red işlemi talebin tüm ürün satırlarına uygulanır; kısmi satır seçimi desteklenmez.");
        var partialLineDecision = returnLineIds.Length < actionableLineIds.Count;
        var finalizedWith = command.FinalizedWith?.Trim();
        if (isHepsiburada && action == "APPROVE" && finalizedWith is not ("Refund" or "Change")) return Invalid<ReturnDetailView>("finalizedWith", "Hepsiburada talep kabulünde iade veya ürün değişimi seçilmelidir.");
        if ((!isHepsiburada || action != "APPROVE") && !string.IsNullOrWhiteSpace(finalizedWith)) return Invalid<ReturnDetailView>("finalizedWith", "Kabul türü yalnız Hepsiburada talebini kabul ederken gönderilebilir.");
        if (isHepsiburada && action == "APPROVE" && string.Equals(claim.ReasonCode, "MissingInvoice", StringComparison.OrdinalIgnoreCase)) return ServiceResult<ReturnDetailView>.Fail("HEPSIBURADA_MISSING_INVOICE_MANUAL", "Eksik fatura taleplerinin kabul işlemi Hepsiburada panelinden yapılmalıdır.", 409);
        if (openDecisionLines.Any(lineIds => ReturnDecisionLinePolicy.Overlaps(lineIds, returnLineIds))) return ServiceResult<ReturnDetailView>.Fail("RETURN_DECISION_IN_PROGRESS", "Bu iade kararı pazaryerine gönderildi; sonuç kesinleşene kadar yeni bir karar gönderilemez.", 409);
        if (action == "REJECT" && (string.IsNullOrWhiteSpace(command.ReasonCode) || string.IsNullOrWhiteSpace(command.Explanation) || command.Explanation.Trim().Length > 500)) return Invalid<ReturnDetailView>("explanation", "REJECT için reasonCode ve en fazla 500 karakter açıklama gerekir.");
        if (action == "REJECT" && !isHepsiburada && !isN11 && (command.EvidenceAssetIds is null || command.EvidenceAssetIds.Count == 0)
            && RejectEvidenceRequired(command.ReasonCode, await CachedIssueReasonsAsync(tenantId, claim.ConnectionId, correlationId, cancellationToken), platformCode))
            return Invalid<ReturnDetailView>("evidenceAssetIds", "Seçilen ret nedeni için en az bir kanıt dosyası gerekir.");
        if (preApprovalConfirm && (!string.IsNullOrWhiteSpace(command.FinalizedWith) || !string.IsNullOrWhiteSpace(command.ReasonCode) || !string.IsNullOrWhiteSpace(command.Explanation) || command.EvidenceAssetIds is { Count: > 0 })) return Invalid<ReturnDetailView>("action", "Hepsiburada ön onay incelemesi ek kabul, ret veya kanıt alanı almaz.");

        var stage = await IsStageConnection(tenantId, claim.ConnectionId, cancellationToken);
        if (!stage && !await IsProductionConnection(tenantId, claim.ConnectionId, cancellationToken)) return ServiceResult<ReturnDetailView>.Fail("ENVIRONMENT_INVALID", "İade işlemi yalnız test ya da canlı mağaza bağlantısında yapılabilir.", 422);
        if (!stage && !await WritesEnabled(tenantId, claim.ConnectionId, cancellationToken)) return ServiceResult<ReturnDetailView>.Fail("EXTERNAL_WRITES_DISABLED", "Global veya connection dış yazma anahtarı kapalı.", 422);
        if (isHepsiburada && !stage && !await Supported(tenantId, claim.ConnectionId, MarketplaceCapabilities.ReturnWrite, cancellationToken)) return ServiceResult<ReturnDetailView>.Fail("CAPABILITY_EVIDENCE_REQUIRED", "Hepsiburada iade talebine yanıt bu mağaza için henüz doğrulanmadı; yanıt gönderilmedi.", 422);
        if (!await ExternalWritePolicyEnabledAsync(tenantId, claim.ConnectionId, MarketplaceExternalWritePolicies.Return, cancellationToken)) return ServiceResult<ReturnDetailView>.Fail("EXTERNAL_WRITE_POLICY_DISABLED", "İade dış yazma akışı kapalı.", 422);

        // "Stoğa ekle": every approved line must already map to a local variant with a MAIN inventory item,
        // otherwise nothing is sent to the marketplace and nothing is half-done.
        var stockTargets = new List<(ReturnLine Line, InventoryItem Item)>();
        // Stock is chosen per product; the older all-or-nothing flag still means every approved line.
        var stockPlan = new Dictionary<Guid, decimal?>();
        foreach (var requested in command.StockLines ?? [])
        {
            if (requested.Quantity <= 0) return Invalid<ReturnDetailView>("stockLines", "Stoğa eklenecek adet sıfırdan büyük olmalıdır.");
            stockPlan[requested.ReturnLineId] = requested.Quantity;
        }
        foreach (var wholeLineId in command.StockLineIds ?? (command.AddToStock ? returnLineIds : []))
            stockPlan.TryAdd(wholeLineId, null);
        var stockLineIds = stockPlan.Keys.ToArray();
        if (stockLineIds.Length > 0)
        {
            if (action != "APPROVE") return Invalid<ReturnDetailView>("addToStock", "Stoğa ekleme yalnız iade kabulünde seçilebilir.");
            if (stockLineIds.Except(returnLineIds).Any()) return Invalid<ReturnDetailView>("stockLineIds", "Stoğa eklenecek ürünler onaylanan ürünler arasında olmalıdır.");
            var approvedLines = await db.ReturnLines.Where(x => x.TenantId == tenantId && x.ClaimId == claimId && stockLineIds.Contains(x.Id)).ToListAsync(cancellationToken);
            foreach (var approvedLine in approvedLines)
            {
                var variantId = await db.OrderLines.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == approvedLine.OrderLineId).Select(x => x.VariantId).SingleOrDefaultAsync(cancellationToken);
                // Only checked here, not tracked: the restock after the marketplace call reads the stock row afresh, so a sale
                // meanwhile is not a stale version (SIPARIS-STOK-5).
                var inventoryItem = variantId is null ? null : await db.InventoryItems.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.VariantId == variantId && x.LocationCode == "MAIN", cancellationToken);
                if (inventoryItem is null) return ServiceResult<ReturnDetailView>.Fail("INVENTORY_MAPPING_REQUIRED", "Seçilen ürünlerden biri yerel varyant ve stok kaydıyla eşleşmiyor; stoğa eklenemez.", 422);
                if (stockPlan[approvedLine.Id] is { } wantedUnits && wantedUnits > approvedLine.Quantity) return Invalid<ReturnDetailView>("stockLines", "Stoğa eklenecek adet iade satırı adedini aşamaz.");
                stockTargets.Add((approvedLine, inventoryItem));
            }
        }

        var now = timeProvider.GetUtcNow();
        var decision = new ReturnDecision
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            ClaimId = claimId,
            Action = action,
            ReasonCode = isHepsiburada && action == "APPROVE" ? finalizedWith : string.IsNullOrWhiteSpace(command.ReasonCode) ? null : command.ReasonCode.Trim(),
            Explanation = string.IsNullOrWhiteSpace(command.Explanation) ? null : command.Explanation.Trim(),
            IdempotencyKey = normalizedKey,
            Status = "PENDING",
            ActorUserId = userId,
            CreatedAt = now,
            ReturnLineIds = returnLineIds
        };
        // The stock choice is kept with the decision before anything is sent, so an approval whose answer is unclear
        // keeps it until a later read of the claim settles it (ReturnPendingStock).
        if (stockTargets.Count > 0)
        {
            decision.StockLinesJson = ReturnPendingStock.Serialize(stockTargets.Select(target => new ReturnPendingStockLine(target.Line.Id, stockPlan[target.Line.Id])).ToList());
            decision.StockStatus = ReturnPendingStockPolicy.Pending;
        }
        db.ReturnDecisions.Add(decision);

        var evidenceFiles = new List<ReturnEvidenceFile>(); long totalBytes = 0;
        if (command.EvidenceAssetIds is not null) foreach (var assetId in command.EvidenceAssetIds.Distinct())
        {
            var asset = await db.FileAssets.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == assetId && x.ArchivedAt == null && x.Status == "ACTIVE", cancellationToken);
            if (asset is null) return ServiceResult<ReturnDetailView>.Fail("EVIDENCE_NOT_FOUND", "İade kanıt dosyası bulunamadı; dosyayı yeniden yükleyin.", 422);
            if (asset.Classification != "RETURN_EVIDENCE" || asset.SizeBytes is <= 0 or > 10 * 1024 * 1024 || totalBytes + asset.SizeBytes > 25 * 1024 * 1024 || asset.MimeType is not ("application/pdf" or "image/jpeg" or "image/png")) return ServiceResult<ReturnDetailView>.Fail("EVIDENCE_INVALID", "İade kanıtları PDF/JPEG/PNG olmalı, dosya başına 10 MiB ve toplamda 25 MiB sınırını aşmamalıdır.", 422);
            await using var source = await files.OpenReadAsync(tenantId, asset.RelativePath, cancellationToken);
            await using var buffer = new MemoryStream();
            await source.CopyToAsync(buffer, cancellationToken);
            if (buffer.Length != asset.SizeBytes) return ServiceResult<ReturnDetailView>.Fail("RETURN_EVIDENCE_SIZE_MISMATCH", "İade kanıt dosyasının kayıtlı boyutu ile okunan içerik eşleşmedi.", 422);
            var bytes = buffer.ToArray();
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), asset.Sha256, StringComparison.OrdinalIgnoreCase)) return ServiceResult<ReturnDetailView>.Fail("RETURN_EVIDENCE_CHECKSUM_MISMATCH", "İade kanıt dosyasının checksum doğrulaması başarısız oldu.", 422);
            db.ReturnEvidence.Add(new ReturnEvidence { Id = Guid.CreateVersion7(), TenantId = tenantId, ClaimId = claimId, DecisionId = decision.Id, FileAssetId = asset.Id, EvidenceKind = asset.Classification, Checksum = asset.Sha256, CreatedAt = now });
            evidenceFiles.Add(new(asset.OriginalNameSafe ?? $"evidence-{asset.Id:N}", asset.MimeType, bytes));
            totalBytes += bytes.LongLength;
        }

        var effect = new ExternalEffectRecord { Id = Guid.CreateVersion7(), TenantId = tenantId, EffectType = effectType, IdempotencyKey = normalizedKey, CreatedAt = now, ConnectionId = claim.ConnectionId, TargetId = claim.Id, Action = $"RETURN_{action}" };
        db.ExternalEffectRecords.Add(effect);
        if (await ReserveReturnDecisionAsync(tenantId, claimId, decision, isHepsiburada, cancellationToken) is { } reservationError) return reservationError;

        var lineIds = await db.ReturnLines.AsNoTracking().Where(x => x.TenantId == tenantId && x.ClaimId == claimId && returnLineIds.Contains(x.Id)).OrderBy(x => x.Id).Select(x => x.ExternalLineId).ToListAsync(cancellationToken);
        var context = new AdapterContext(tenantId, claim.ConnectionId, correlationId, normalizedKey, now.AddSeconds(30));
        AdapterResult<ReturnActionResult> result;
        try
        {
            result = await returns.ExecuteAsync(context, new(claim.ExternalClaimId, lineIds, action, action == "APPROVE" && isHepsiburada ? null : decision.ReasonCode, decision.Explanation, evidenceFiles, isHepsiburada ? finalizedWith : null,
                action == "REJECT" && isN11 ? command.ReturnShipment : null), cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The call may or may not have reached the marketplace: the reserved decision is not left PENDING but
            // waits for the effect check, like an unclear answer, and the claim stays held until it settles.
            decision.Status = "MANUAL_REVIEW";
            decision.ErrorCode = "EXTERNAL_EFFECT_VERIFYING";
            decision.CompletedAt = timeProvider.GetUtcNow();
            await db.SaveChangesAsync(CancellationToken.None);
            return await VerifyLaterAsync<ReturnDetailView>(effect, CancellationToken.None);
        }
        if (!result.IsSuccess)
        {
            var error = result.Error;
            decision.ErrorCode = error?.Code ?? "RETURN_ACTION_FAILED";
            decision.ExternalOperationId ??= error?.RemoteRequestId;
            decision.CompletedAt = timeProvider.GetUtcNow();
            if (error is null || IsAmbiguous(error))
            {
                // The outcome is settled by the effect check or the next claim
                // read; the code marks it as waiting for that, not as a conflict.
                decision.Status = "MANUAL_REVIEW";
                decision.ErrorCode = "EXTERNAL_EFFECT_VERIFYING";
                await db.SaveChangesAsync(cancellationToken);
                return await VerifyLaterAsync<ReturnDetailView>(effect, cancellationToken);
            }
            db.ExternalEffectRecords.Remove(effect);
            decision.Status = "FAILED";
            ReturnPendingStock.Drop(decision, decision.CompletedAt.Value);
            await db.SaveChangesAsync(cancellationToken);
            return ServiceResult<ReturnDetailView>.Fail(error.Code, error.SafeMessage, error.HttpStatus is >= 400 and <= 599 ? error.HttpStatus.Value : 502);
        }

        decision.Status = "SUBMITTED";
        decision.ExternalOperationId = result.Value?.ExternalOperationId;
        decision.ErrorCode = null;
        effect.CompletedAt = timeProvider.GetUtcNow();
        // The accepted write is recorded first, on its own: a conflict in the claim or stock save below must not turn a
        // decision the marketplace already took into a server error (SIPARIS-STOK-5).
        await db.SaveChangesAsync(cancellationToken);
        var completedAt = effect.CompletedAt.Value;
        // The claim and the restock are written on fresh values; a sale or a claim read that changes the same rows in
        // between runs the attempt again. When it keeps colliding, the decision stays SUBMITTED with its pending stock
        // choice, which the next read of the claim settles (ReturnPendingStock.SettleClaimAsync).
        var settled = await SerializableWriteRetry.RunAsync(db, async token =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
            await db.Entry(claim).ReloadAsync(token);
            await db.Entry(decision).ReloadAsync(token);
            // The claim's lines decided before this one count: rejecting the last waiting lines of a claim whose other lines
            // were approved leaves it approved in part ("Kısmen Onaylandı"), so those lines still reach the invoice.
            claim.Status = ReturnClaimDecisionPolicy.StatusAfterLineDecision(claim.Status, action, returnLineIds.Length, actionableLineIds.Count,
                claimLines.Where(line => !returnLineIds.Contains(line.Id)).Select(line => line.RawStatus).ToList());
            if (!isHepsiburada && action is "APPROVE" or "REJECT")
            {
                var settledStatus = action == "APPROVE" ? "Accepted" : "Rejected";
                foreach (var settledLine in await db.ReturnLines.Where(x => x.TenantId == tenantId && x.ClaimId == claimId && returnLineIds.Contains(x.Id)).ToListAsync(token)) settledLine.RawStatus = settledStatus;
            }
            claim.RawStatus = partialLineDecision ? "PARTIAL_LINE_DECISION_PENDING" : result.Value?.Status ?? (action switch
            {
                "APPROVE" => "ACCEPTED",
                "REJECT" => "REJECTED",
                _ => "PREAPPROVAL_CONFIRM_SUBMITTED"
            });
            // The marketplace accepted the approval: the chosen lines go back to stock now, once.
            claim.UpdatedAt = completedAt;
            claim.Version++;
            await ReturnPendingStock.SettleAsync(db, decision, force: action == "APPROVE", correlationId, completedAt, token);
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return true;
        }, () => false, cancellationToken);
        if (!settled)
        {
            db.ChangeTracker.Clear();
            return await ReturnAsync(tenantId, claimId, cancellationToken);
        }

        // The action itself is already complete. Read-back is best-effort and
        // only refines the local status; it must not put the user action in a
        // background queue or hide the provider response from the panel.
        var readback = await returns.GetAsync(new AdapterContext(tenantId, claim.ConnectionId, correlationId, $"{normalizedKey}:readback", timeProvider.GetUtcNow().AddSeconds(30)), claim.ExternalClaimId, cancellationToken);
        if (readback.IsSuccess && readback.Value is not null)
        {
            var remoteStatus = MarketplaceReturnStatus.Canonicalize(readback.Value.RawStatus, readback.Value.CargoTrackingLink);
            if (!ReturnClaimStoragePolicy.ShouldPersist(remoteStatus))
            {
                // The decision just made is history: the claim is kept as
                // cancelled with it instead of being deleted.
                if (await CancelledReturnClaimCleanup.RemoveOrRetainAsync(db, tenantId, claimId, readback.Value.RawStatus, readback.Value.LastModifiedAt, timeProvider.GetUtcNow(), cancellationToken))
                {
                    decision.Status = "MANUAL_REVIEW";
                    decision.ErrorCode = "RETURN_ACTION_READBACK_CONFLICT";
                    decision.CompletedAt = timeProvider.GetUtcNow();
                    await db.SaveChangesAsync(cancellationToken);
                    return ServiceResult<ReturnDetailView>.Fail("RETURN_CLAIM_CANCELLED", "Pazaryerindeki iade talebi iptal edilmiş; iade, verilen kararla birlikte iptal olarak saklandı.", 409);
                }
                db.ChangeTracker.Clear();
                return ServiceResult<ReturnDetailView>.Fail("RETURN_CLAIM_CANCELLED", "Pazaryerindeki iade talebi iptal edilmiş; iptal kaydı saklanmadı.", 409);
            }
            // The lines decide a claim decided both ways (approved in part), as on the scheduled read (UpsertReturn).
            var remoteClaimStatus = ReturnLineStatusPolicy.Resolve(remoteStatus, readback.Value.Lines.Select(remoteLine => remoteLine.RawStatus).ToList());
            if (!partialLineDecision && ReturnClaimStateMachine.CanTransition(claim.Status, remoteClaimStatus)) claim.Status = remoteClaimStatus;
            claim.RawStatus = partialLineDecision ? "PARTIAL_LINE_DECISION_PENDING" : readback.Value.RawStatus;
            claim.CargoProviderName = readback.Value.CargoProviderName ?? claim.CargoProviderName;
            claim.CargoTrackingNumber = readback.Value.CargoTrackingNumber ?? claim.CargoTrackingNumber;
            claim.ReasonCode = readback.Value.ReasonCode;
            claim.ReasonText = readback.Value.ReasonText;
            claim.ActionDueAt = readback.Value.ActionDueAt;
            claim.LastRemoteModifiedAt = readback.Value.LastModifiedAt ?? claim.LastRemoteModifiedAt;
            claim.UpdatedAt = timeProvider.GetUtcNow();
            claim.Version++;
            // The decision is judged by its own lines: Trendyol's claim status is the first item's, which may be a line
            // this decision did not cover and that still waits for its own decision.
            var decisionStatus = ReturnDecisionLinePolicy.Outcome(remoteStatus,
                lineIds.Select(lineId => readback.Value.Lines.FirstOrDefault(remoteLine => remoteLine.ExternalLineId == lineId)?.RawStatus).ToList());
            var confirmed = action == "APPROVE" && decisionStatus is ReturnClaimStatus.Approved or ReturnClaimStatus.Completed
                || action == "REJECT" && decisionStatus is ReturnClaimStatus.Rejected or ReturnClaimStatus.Disputed
                || preApprovalConfirm && (decisionStatus is ReturnClaimStatus.AwaitingShipment or ReturnClaimStatus.InTransit
                    || decisionStatus == ReturnClaimStatus.ActionRequired && !HepsiburadaReturnActionPolicy.IsAwaitingPreApproval(readback.Value.RawStatus));
            var conflicting = action == "APPROVE" && decisionStatus is ReturnClaimStatus.Rejected or ReturnClaimStatus.Cancelled
                || action == "REJECT" && decisionStatus is ReturnClaimStatus.Approved or ReturnClaimStatus.Completed
                || preApprovalConfirm && decisionStatus is ReturnClaimStatus.Approved or ReturnClaimStatus.Rejected or ReturnClaimStatus.Cancelled;
            if (confirmed) { decision.Status = "SUCCEEDED"; decision.CompletedAt = claim.UpdatedAt; }
            else if (conflicting) { decision.Status = "MANUAL_REVIEW"; decision.ErrorCode = "RETURN_ACTION_READBACK_CONFLICT"; decision.CompletedAt = claim.UpdatedAt; }
        }
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // The read-back only refines a decision already recorded; a claim read that saved meanwhile keeps its own.
            db.ChangeTracker.Clear();
        }
        return await ReturnAsync(tenantId, claimId, cancellationToken);
    }

    // Reserves a return decision before the marketplace is called (RET-02). In one short transaction the claim's
    // version moves on only while it is still the one this request read (Version is the claim's concurrency token, so
    // the UPDATE is conditional and holds the row), the open decisions are read again under that lock, and the PENDING
    // decision commits with its effect record. Of two requests that read the same claim only one is reserved; the other
    // calls nothing and gets a conflict. Null when reserved.
    private async Task<ServiceResult<ReturnDetailView>?> ReserveReturnDecisionAsync(Guid tenantId, Guid claimId, ReturnDecision decision, bool sentDecisionHoldsClaim, CancellationToken cancellationToken)
    {
        var claim = db.ReturnClaims.Local.Single(x => x.TenantId == tenantId && x.Id == claimId);
        var lineIds = decision.ReturnLineIds ?? [];
        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            try
            {
                claim.Version++;
                claim.UpdatedAt = decision.CreatedAt;
                await db.SaveChangesAsync(cancellationToken);
                var openLines = await OpenReturnDecisions(tenantId, claimId, sentDecisionHoldsClaim).Where(x => x.Id != decision.Id).Select(x => x.ReturnLineIds).ToListAsync(cancellationToken);
                if (!openLines.Any(open => ReturnDecisionLinePolicy.Overlaps(open, lineIds)))
                {
                    await transaction.CommitAsync(cancellationToken);
                    return null;
                }
            }
            catch (DbUpdateException)
            {
                // Another request moved the claim on first (or reserved the same Idempotency-Key): answered below.
            }
            await transaction.RollbackAsync(CancellationToken.None);
        }
        db.ChangeTracker.Clear();
        var prior = await db.ReturnDecisions.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.IdempotencyKey == decision.IdempotencyKey, cancellationToken);
        if (prior is not null && prior.ClaimId == claimId && prior.Status is "SUCCEEDED" or "SUBMITTED") return await ReturnAsync(tenantId, claimId, cancellationToken);
        var stillOpen = await OpenReturnDecisions(tenantId, claimId, sentDecisionHoldsClaim).Select(x => x.ReturnLineIds).ToListAsync(cancellationToken);
        if (prior is not null || stillOpen.Any(open => ReturnDecisionLinePolicy.Overlaps(open, lineIds)))
            return ServiceResult<ReturnDetailView>.Fail("RETURN_DECISION_IN_PROGRESS", "Bu iade için başka bir karar az önce pazaryerine gönderildi; sonuç kesinleşene kadar yeni bir karar gönderilemez.", 409);
        var currentVersion = await db.ReturnClaims.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == claimId).Select(x => x.Version).FirstOrDefaultAsync(cancellationToken);
        return ServiceResult<ReturnDetailView>.Fail("CONCURRENCY_CONFLICT", "İade siz düzenlerken başka bir işlemle güncellendi. Sayfayı yenileyip tekrar deneyin.", 409);
    }

    public async Task<ServiceResult<ReturnDetailView>> ApplyDispositionAsync(Guid tenantId, Guid userId, Guid claimId, ReturnDispositionCommand command, string idempotencyKey, string correlationId, CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<ReturnStockDispositionKind>(command.Disposition, true, out var disposition)) return Invalid<ReturnDetailView>("disposition", "Disposition PASS, QUARANTINE, DAMAGED veya NOT_RECEIVED olmalıdır.");
        if (command.Quantity <= 0 || string.IsNullOrWhiteSpace(command.Reason)) return Invalid<ReturnDetailView>("quantity", "Pozitif quantity ve açıklama zorunludur.");

        // The return line is locked before anything is read, and held until the disposition commits: two operators,
        // or an operator and the automatic "Stoğa Ekle" settlement (ReturnPendingStock, which takes the same lock),
        // put the line's units somewhere one after the other, each reading what the other already did (YENİ-04).
        // Read committed, so the read after the lock sees the other's committed rows; a serializable snapshot taken
        // before the wait would not.
        // The inventory item carries a version: a sale or another stock change on the same item between the read and the
        // save is a concurrency conflict. The whole attempt (lock, reads, the delta on the fresh on-hand) runs again, a few
        // times, and a conflict that keeps coming back is a 409 STOCK_CHANGED instead of a server error (scan 13 Nov 2026).
        var outcome = await SerializableWriteRetry.RunAsync<ServiceResult<ReturnDetailView>?>(db, async token =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
            try
            {
                await ReturnPendingStock.LockLinesAsync(db, tenantId, [command.ReturnLineId], token);
                var existing = await db.ReturnStockDispositions.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.IdempotencyKey == idempotencyKey, token);
                if (existing)
                {
                    await transaction.CommitAsync(token);
                    return null;
                }

                var claim = await db.ReturnClaims.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == claimId, token);
                if (claim is null) return NotFound<ReturnDetailView>();
                var line = await db.ReturnLines.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ClaimId == claimId && x.Id == command.ReturnLineId, token);
                if (line is null) return NotFound<ReturnDetailView>();
                // Decided per line: a line approved on its own takes stock while the claim's other lines still wait.
                if (!ReturnDecisionLinePolicy.StockDispositionAllowed(claim.Status, line.RawStatus)) return ServiceResult<ReturnDetailView>.Fail("RETURN_DISPOSITION_NOT_ALLOWED", "Stok kararı yalnız onaylanan iade ürünlerine uygulanabilir.", 409);
                var already = await ReturnPendingStock.DisposedQuantityAsync(db, tenantId, line.Id, token);
                if (already + command.Quantity > line.Quantity) return ServiceResult<ReturnDetailView>.Fail("RETURN_QUANTITY_EXCEEDED", "Disposition toplamı iade satırı miktarını aşamaz.", 409);
                var variantId = await db.OrderLines.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == line.OrderLineId).Select(x => x.VariantId).SingleAsync(token);
                if (variantId is null) return ServiceResult<ReturnDetailView>.Fail("INVENTORY_MAPPING_REQUIRED", "İade satırı yerel varyantla eşleşmiyor.", 422);
                var item = await db.InventoryItems.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.VariantId == variantId && x.LocationCode == "MAIN", token);
                if (item is null) return ServiceResult<ReturnDetailView>.Fail("INVENTORY_MAPPING_REQUIRED", "MAIN inventory item bulunamadı.", 422);
                // SIPARIS-STOK-6: with the other claims of the same order line, never more back in stock than was sold.
                if (disposition == ReturnStockDispositionKind.Pass
                    && await StockMovementWriter.ReturnRestockRoomAsync(db, tenantId, line.OrderLineId, token) is var room && command.Quantity > room.Left)
                    return ServiceResult<ReturnDetailView>.Fail("RETURN_ORDER_LINE_QUANTITY_EXCEEDED", $"Bu sipariş satırından {StockMovementWriter.Units(room.Sold)} adet satıldı ve iade taleplerinden {StockMovementWriter.Units(room.Restocked)} adedi zaten stoğa eklendi; en fazla {StockMovementWriter.Units(room.Left)} adet daha stoğa eklenebilir.", 409);
                var now = timeProvider.GetUtcNow();
                db.ReturnStockDispositions.Add(new ReturnStockDisposition { Id = Guid.CreateVersion7(), TenantId = tenantId, ClaimId = claimId, ReturnLineId = line.Id, InventoryItemId = item.Id, Disposition = disposition, Quantity = command.Quantity, IdempotencyKey = idempotencyKey, Reason = command.Reason.Trim(), ActorUserId = userId, CreatedAt = now });
                if (disposition == ReturnStockDispositionKind.Pass)
                {
                    var change = StockMovementWriter.Move(db, item, command.Quantity, StockMovementRounding.None, ReturnPendingStock.RestockSource(claim, idempotencyKey, userId, correlationId, now));
                    await StockProjectionOutbox.EnqueueAsync(db, tenantId, [change], now, token);
                }
                db.AuditLogs.Add(new AuditLog { TenantId = tenantId, ActorUserId = userId, Action = "RETURN_STOCK_DISPOSITION", TargetType = "ReturnClaim", TargetId = claimId.ToString("D"), Reason = command.Reason.Trim(), CorrelationId = correlationId, CreatedAt = now });
                await db.SaveChangesAsync(token);
                await transaction.CommitAsync(token);
            }
            catch (DbUpdateException exception) when (!SerializableWriteRetry.IsRetryable(exception))
            {
                await transaction.RollbackAsync(CancellationToken.None);
                // A replay that committed meanwhile (the same key) is the same disposition.
                if (await db.ReturnStockDispositions.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.IdempotencyKey == idempotencyKey, token)) return null;
                throw;
            }
            return null;
        }, () => ServiceResult<ReturnDetailView>.Fail("STOCK_CHANGED", "Stok aynı anda başka bir işlemle (ör. yeni sipariş) değiştiği için kaydedilemedi. Sayfayı yenileyip tekrar deneyin.", 409), cancellationToken);
        return outcome ?? await ReturnAsync(tenantId, claimId, cancellationToken);
    }

    private static readonly IReadOnlyList<string> ReturnActions = ["APPROVE", "REJECT"];

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
    // Return decisions whose outcome is still open: waiting to be sent or verified, or sent while the request's effect
    // is not settled. A sent decision on line-level marketplaces is closed once the marketplace accepted it (the
    // decided lines are no longer actionable); Hepsiburada decides the whole claim and its sent decision holds the
    // claim until the claim read confirms it, as before.
    private IQueryable<ReturnDecision> OpenReturnDecisions(Guid tenantId, Guid claimId, bool sentDecisionHoldsClaim) => db.ReturnDecisions.AsNoTracking()
        .Where(x => x.TenantId == tenantId && x.ClaimId == claimId
            && (x.Status == "PENDING" || x.Status == "RETRY_SCHEDULED" || x.Status == "MANUAL_REVIEW"
                || x.Status == "SUBMITTED" && db.ExternalEffectRecords.Any(effect => effect.TenantId == x.TenantId && effect.IdempotencyKey == x.IdempotencyKey && (sentDecisionHoldsClaim || effect.CompletedAt == null))));

    private Task<bool> IsStageConnection(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) => db.PlatformConnections.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.Id == connectionId && (x.Status == "ACTIVE" || x.Status == "VERIFIED") && x.Environment == "STAGE", cancellationToken);
    private Task<bool> IsProductionConnection(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) => db.PlatformConnections.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.Id == connectionId && (x.Status == "ACTIVE" || x.Status == "VERIFIED") && x.Environment == "PRODUCTION", cancellationToken);
    private async Task<bool> WritesEnabled(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) { if (!configuration.GetValue<bool>("FeatureFlags:ExternalWrites")) return false; var settings = await db.PlatformConnections.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == connectionId).Select(x => x.SettingsJson).SingleOrDefaultAsync(cancellationToken); if (settings is null) return false; try { using var document = JsonDocument.Parse(settings); return document.RootElement.TryGetProperty("ExternalWritesEnabled", out var value) && value.ValueKind == JsonValueKind.True; } catch (JsonException) { return false; } }
    private async Task<bool> ExternalWritePolicyEnabledAsync(Guid tenantId, Guid connectionId, string resourceType, CancellationToken cancellationToken) => (await ExternalWritePolicyAsync(tenantId, connectionId, resourceType, cancellationToken)).Enabled;
    private async Task<(bool Enabled, int IntervalSeconds)> ExternalWritePolicyAsync(Guid tenantId, Guid connectionId, string resourceType, CancellationToken cancellationToken)
    {
        var policy = await db.ConnectionSyncPolicies.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == resourceType, cancellationToken);
        return policy is null ? (false, 0) : (policy.Enabled, Math.Clamp(policy.IntervalSeconds, 0, 86_400));
    }
    private async Task<IReadOnlyList<string>> CapabilityValues(Guid tenantId, Guid connectionId, string code, string property, CancellationToken cancellationToken) { var capability = await db.PlatformCapabilities.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.Code == code && x.SupportLevel == CapabilitySupportLevel.Supported, cancellationToken); if (capability?.ConstraintsJson is null) return []; try { using var doc = JsonDocument.Parse(capability.ConstraintsJson); return doc.RootElement.TryGetProperty(property, out var values) && values.ValueKind == JsonValueKind.Array ? values.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList() : []; } catch (JsonException) { return []; } }
    private Task<Dictionary<Guid, string>> MediaUrls(Guid tenantId, IEnumerable<Guid?> variantIds, CancellationToken cancellationToken) =>
        OrderLineCatalog.MediaUrlsAsync(db, tenantId, variantIds.Where(x => x is not null).Select(x => x!.Value), cancellationToken);

    // The marketplace's own photo address in a line snapshot, ready to show; null when the line carries none.
    internal static string? SourceLineImageUrl(string? json) => NormalizeImageUrl(SourceImageUrl(json ?? "{}"));

    // The order's buyer kind by the invoice's rule (report item 12), and the order type that follows from it: a micro
    // export first, then KURUMSAL for a corporate buyer (with or without its tax number), else BIREYSEL. The kind stored
    // on the order (Order.BuyerType) decides when set, so the label always equals what the "Fatura Türü" filter matched.
    internal static (OrderBuyerTypeView BuyerType, string OrderType) BuyerType(string customerJson, string invoiceAddressJson, bool isMicroExport, string? storedKind = null)
    {
        var buyer = OrderBuyerTypes.Label(storedKind, customerJson, invoiceAddressJson);
        return (buyer, isMicroExport ? "MIKRO_IHRACAT" : buyer.Kind == OrderBuyerTypeView.Individual ? "BIREYSEL" : "KURUMSAL");
    }

    private static (string Name, string? Email, string? Phone, string? TaxOrIdentityNumber, bool IsMicroExport, bool? IsEInvoiceAvailable) Customer(string customerJson, string invoiceAddressJson, string shipmentAddressJson)
    {
        var name = ResolveCustomerName(customerJson, invoiceAddressJson, shipmentAddressJson);
        var email = JsonText(customerJson, "customerEmail", "email") ?? JsonText(invoiceAddressJson, "email") ?? JsonText(shipmentAddressJson, "email");
        var phone = JsonText(customerJson, "customerPhone", "customerPhoneNumber", "phone", "phoneNumber") ?? JsonText(invoiceAddressJson, "phone", "phoneNumber", "mobilePhone") ?? JsonText(shipmentAddressJson, "phone", "phoneNumber", "mobilePhone");
        var tax = ResolveCustomerTaxOrIdentityNumber(customerJson, invoiceAddressJson, shipmentAddressJson);
        var microText = JsonText(customerJson, "shipmentPackageType", "orderType");
        // Trendyol exports through its 3P partner model explicitly return micro=false. The documented
        // 3pByTrendyol=true signal is still an export order and must be presented as such to operators.
        // Historical Stage snapshots may predate the export flags while retaining Trendyol's
        // documented PM3/Arvato export-partner identity. Keep that narrow legacy signal so
        // existing export orders are not presented as domestic orders.
        var legacyExportPartner = IsLegacyTrendyolExportPartner(name);
        var micro = JsonBool(customerJson, "micro", "microExport", "3pByTrendyol") || microText?.Contains("MICRO", StringComparison.OrdinalIgnoreCase) == true || microText?.Contains("İHRAC", StringComparison.OrdinalIgnoreCase) == true || legacyExportPartner;
        var eInvoice = JsonNullableBool(customerJson, "eInvoiceAvailable", "isEInvoice") ?? JsonNullableBool(invoiceAddressJson, "eInvoiceAvailable", "isEInvoice");
        return (name, email, phone, tax, micro, eInvoice);
    }

    private static readonly HashSet<string> FastDeliveryTypes = new(["TodayDelivery", "SameDayShipping", "FastDelivery"], StringComparer.OrdinalIgnoreCase);

    // The order row's marks, read from the package fields the marketplace sent (top level of the customer snapshot only,
    // never from a line). A field that is missing or holds another value leaves its mark out.
    // - Micro export: Trendyol micro=true only. micro and countryCode are independent (Trendyol docs), so a foreign
    //   country alone is no micro export. The country is the invoice address's: the shipment address of a micro order
    //   is Trendyol's Istanbul export depot (TR); the storefront is the fallback.
    // - Delivery point: Trendyol deliveryAddressType=CollectionPoint; n11 any deliveryAddressType (KTN, EASYPOINT, PUP).
    // - Fast delivery: Trendyol fastDeliveryType TodayDelivery/SameDayShipping/FastDelivery, unless the same package
    //   says fastDelivery=false (the two disagree; no mark rather than a wrong one).
    internal static OrderRowMarks? RowMarks(string? platformCode, string? storeFrontCode, string customerJson, string invoiceAddressJson)
    {
        var platform = platformCode?.Trim().ToUpperInvariant();
        if (platform is not ("TRENDYOL" or "N11")) return null;
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(customerJson) ? "{}" : customerJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            string? TopText(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() : null;
            bool? TopBool(string name) => root.TryGetProperty(name, out var value) ? value.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null } : null;

            OrderRowMarks marks;
            if (platform == "N11")
            {
                var pointType = TopText("deliveryAddressType") is { Length: > 0 } type && !string.Equals(type, "null", StringComparison.OrdinalIgnoreCase) ? type.ToUpperInvariant() : null;
                marks = new(false, null, pointType is not null, pointType, null);
            }
            else
            {
                var micro = TopBool("micro") == true;
                var country = micro ? MicroExportCountry(invoiceAddressJson, storeFrontCode) : null;
                var deliveryPoint = string.Equals(TopText("deliveryAddressType"), "CollectionPoint", StringComparison.OrdinalIgnoreCase);
                var fastType = TopText("fastDeliveryType") is { } fast && FastDeliveryTypes.TryGetValue(fast, out var known) && TopBool("fastDelivery") != false ? known : null;
                marks = new(micro, country, deliveryPoint, null, fastType);
            }
            return marks.MicroExport || marks.DeliveryPoint || marks.FastDeliveryType is not null ? marks : null;
        }
        catch (JsonException) { return null; }
    }

    private static string? MicroExportCountry(string invoiceAddressJson, string? storeFrontCode)
    {
        static string? Code(string? value) => value?.Trim().ToUpperInvariant() is { Length: 2 } code && code.All(char.IsAsciiLetterUpper) && code != "TR" ? code : null;
        string? invoiceCountry = null;
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(invoiceAddressJson) ? "{}" : invoiceAddressJson);
            if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("countryCode", out var value) && value.ValueKind == JsonValueKind.String)
                invoiceCountry = value.GetString();
        }
        catch (JsonException) { }
        return Code(invoiceCountry) ?? Code(storeFrontCode);
    }

    internal static string ResolveCustomerName(string customerJson, string invoiceAddressJson, string shipmentAddressJson)
    {
        var customerName = NameFrom(customerJson, "customerFirstName", "customerLastName", "customerName", "customerFullName", "buyerName", "fullName", "name");
        var invoiceName = NameFrom(invoiceAddressJson, "firstName", "lastName", "invoiceFirstName", "invoiceLastName", "fullName", "name", "company", "companyName");
        var shipmentName = NameFrom(shipmentAddressJson, "firstName", "lastName", "shippingFirstName", "shippingLastName", "fullName", "name", "company", "companyName");
        return FirstMeaningful(customerName, invoiceName, shipmentName) ?? "—";
    }

    internal static string? ResolveCustomerTaxOrIdentityNumber(string customerJson, string invoiceAddressJson, string shipmentAddressJson) =>
        JsonText(customerJson, "customerTaxNumber", "taxNumber", "identityNumber", "customerIdentityNumber", "tcIdentityNumber")
        ?? JsonText(invoiceAddressJson, "taxNumber", "identityNumber", "tcIdentityNumber")
        ?? JsonText(shipmentAddressJson, "taxNumber", "identityNumber", "tcIdentityNumber");

    private static string? NameFrom(string json, string firstName, string lastName, params string[] fullNameFields)
    {
        var first = JsonText(json, firstName);
        var last = JsonText(json, lastName);
        var combined = string.Join(' ', new[] { first, last }.Where(x => !string.IsNullOrWhiteSpace(x)));
        return !string.IsNullOrWhiteSpace(combined) ? combined : JsonText(json, fullNameFields);
    }

    private static string? FirstMeaningful(params string?[] names) => names.FirstOrDefault(name => IsMeaningfulText(name) && !IsPlaceholderCustomerName(name!));

    private static bool IsPlaceholderCustomerName(string name) => name.Trim() is "Adı Soyadı" or "Ad Soyad" or "İsim Soyisim";

    internal static bool IsLegacyTrendyolExportPartner(string name) =>
        name.Contains("PM3", StringComparison.OrdinalIgnoreCase) && name.Contains("ARVATO", StringComparison.OrdinalIgnoreCase);

    // A shipping deadline only: Trendyol's (extended) agreed hand-over date or a marketplace's due date;
    // delivery estimates and modification times are not deadlines.
    private static DateTimeOffset? OperationalDueAt(string json) =>
        JsonInstant(json, "extendedAgreedDeliveryDate", "agreedDeliveryDate", "dueDate", "shipmentDueDate", "deliveryDueAt");

    internal static string InvoiceLabel(Invoice? invoice, string customerJson, bool marketplaceDeliveryConfirmed = false) =>
        InvoiceLabel(invoice, customerJson, [], marketplaceDeliveryConfirmed);

    internal static string InvoiceLabel(Invoice? invoice, string customerJson, IEnumerable<string?> packageRawStatuses, bool marketplaceDeliveryConfirmed = false) =>
        InvoiceLabel(invoice?.Status, invoice?.InvoiceNumber, customerJson, packageRawStatuses, marketplaceDeliveryConfirmed);

    internal static string InvoiceLabel(InvoiceStatus? invoiceStatus, string? invoiceNumber, string customerJson, IEnumerable<string?> packageRawStatuses, bool marketplaceDeliveryConfirmed = false)
    {
        // The local fiscal invoice and the marketplace delivery are separate
        // facts. A fiscal invoice number does not prove that Trendyol accepted
        // the document, so Submitted/Accepted/MarketplacePending stay visible
        // as an in-progress marketplace delivery.
        if (invoiceStatus is { } status)
        {
            // A lost provider answer is not a rejection: the invoice may exist at the provider.
            if (status == InvoiceStatus.UnknownResult) return "FATURA_BELIRSIZ";
            if (status is InvoiceStatus.Rejected or InvoiceStatus.ValidationFailed or InvoiceStatus.ManualReview) return "FATURA_REDDEDILDI";
            if (status == InvoiceStatus.MarketplaceFailed) return "FATURA_PLATFORMA_AKTARILMADI";
            if (status is InvoiceStatus.Accepted or InvoiceStatus.MarketplacePending or InvoiceStatus.Submitted) return "FATURA_ISLENIYOR";
            if (status is InvoiceStatus.Cancelled or InvoiceStatus.CancelledLocal) return "FATURA_IPTAL";
            if (status == InvoiceStatus.Completed)
                return "FATURA_KONTROLDE";
            if (status is InvoiceStatus.Submitting or InvoiceStatus.Submitted)
                return "FATURA_ISLENIYOR";
            if (!string.IsNullOrWhiteSpace(invoiceNumber) && status is not (InvoiceStatus.Draft or InvoiceStatus.Validating or InvoiceStatus.Ready))
                return "FATURA_KONTROLDE";
            return "FATURA_ISLENIYOR";
        }
        // Trendyol's order payloads expose the same business fact in two
        // different places depending on the endpoint/version: invoiceStatus
        // in the package snapshot, or the package's raw status as INVOICED.
        // RawStatus must be considered before the snapshot fallback because a
        // later shipment update can replace the snapshot's invoice fields.
        if (packageRawStatuses.Any(IsInvoicedRemoteStatus))
            return "FATURA_KONTROLDE";

        var remote = JsonText(customerJson, "marketplaceInvoiceStatus", "invoiceStatus")?.Trim().ToUpperInvariant();
        if (remote is "INVOICED") return "FATURA_KONTROLDE";
        if (remote is "RECEIVED") return "FATURA_KONTROLDE";
        if (remote is "REJECTED") return "FATURA_REDDEDILDI";
        if (remote is "NOTINVOICED" or "NOT_INVOICED") return "FATURA_BEKLIYOR";
        // Missing marketplace evidence is not the same as an explicit
        // NotInvoiced response. Historical Trendyol payloads often omit the
        // invoice field entirely; presenting those packages as actionable
        // invoice work creates false Dashboard counts.
        return "FATURA_BILINMIYOR";
    }

    internal static string InvoiceLabel(Invoice? invoice, MarketplaceInvoiceStatus marketplaceStatus, string customerJson, IEnumerable<string?> packageRawStatuses, bool marketplaceDeliveryConfirmed = false) =>
        InvoiceLabel(invoice?.Status, invoice?.InvoiceNumber, marketplaceStatus, customerJson, packageRawStatuses, marketplaceDeliveryConfirmed);

    internal static string InvoiceLabel(InvoiceStatus? invoiceStatus, string? invoiceNumber, MarketplaceInvoiceStatus marketplaceStatus, string customerJson, IEnumerable<string?> packageRawStatuses, bool marketplaceDeliveryConfirmed = false)
    {
        if (invoiceStatus is { } status)
        {
            if (status == InvoiceStatus.UnknownResult) return "FATURA_BELIRSIZ";
            if (status is InvoiceStatus.Rejected or InvoiceStatus.ValidationFailed or InvoiceStatus.ManualReview or InvoiceStatus.MarketplaceFailed)
                return status == InvoiceStatus.MarketplaceFailed ? "FATURA_PLATFORMA_AKTARILMADI" : "FATURA_REDDEDILDI";
            if (status is InvoiceStatus.Cancelled or InvoiceStatus.CancelledLocal)
                return "FATURA_IPTAL";
            if (status is InvoiceStatus.Accepted or InvoiceStatus.MarketplacePending or InvoiceStatus.Submitted or InvoiceStatus.Submitting)
                return "FATURA_ISLENIYOR";
            // The marketplace observation is authoritative for the delivery
            // leg. A local invoice number only proves that our fiscal provider
            // created a document; it does not prove Trendyol accepted it.
            if (status == InvoiceStatus.Completed) return "FATURA_KONTROLDE";
            if (marketplaceStatus == MarketplaceInvoiceStatus.Rejected) return "FATURA_REDDEDILDI";
            if (marketplaceStatus == MarketplaceInvoiceStatus.Received) return "FATURA_KONTROLDE";
            return InvoiceLabel(invoiceStatus, invoiceNumber, customerJson, packageRawStatuses, marketplaceDeliveryConfirmed);
        }
        return marketplaceStatus switch
        {
            MarketplaceInvoiceStatus.Invoiced => "FATURA_KONTROLDE",
            MarketplaceInvoiceStatus.Received => "FATURA_KONTROLDE",
            MarketplaceInvoiceStatus.Rejected => "FATURA_REDDEDILDI",
            MarketplaceInvoiceStatus.NotInvoiced => "FATURA_BEKLIYOR",
            _ => InvoiceLabel(invoiceStatus, invoiceNumber, customerJson, packageRawStatuses, marketplaceDeliveryConfirmed)
        };
    }

    internal static string ReturnInvoiceLabel(Invoice? invoice, MarketplaceInvoiceStatus marketplaceStatus, string customerJson, IEnumerable<string?> packageRawStatuses, bool marketplaceDeliveryConfirmed = false)
    {
        // An absent marketplace observation does not prove that the package is
        // waiting for an invoice. Keep unknown separate so the return list does
        // not report a false pending state when Trendyol has not exposed it.
        return InvoiceLabel(invoice, marketplaceStatus, customerJson, packageRawStatuses, marketplaceDeliveryConfirmed);
    }

    // A status the seller set by hand on the invoice page ("Faturası yüklendi" / "Fatura bekliyor", for marketplaces
    // whose API cannot show an invoice uploaded in their own seller panel) decides before any other evidence.
    internal static string? ManualInvoiceLabel(string? manualInvoiceStatus) =>
        manualInvoiceStatus?.Trim().ToUpperInvariant() switch
        {
            "UPLOADED" => "FATURA_KONTROLDE",
            "PENDING" => "FATURA_BEKLIYOR",
            _ => null
        };

    // The hand-set status itself ("UPLOADED" / "PENDING"), so the screens can say the label was set by hand.
    internal static string? HandSetInvoiceStatus(string? manualInvoiceStatus) =>
        ManualInvoiceLabel(manualInvoiceStatus) is null ? null : manualInvoiceStatus!.Trim().ToUpperInvariant();

    // The orders filter and the row label read an issued invoice with these same two rules (P4, 1 Nov 2026):
    // - not on the platform: the marketplace leg failed, or it stopped with an error while waiting;
    // - needs review: the provider issued it (it has an external reference) and it stopped for review, for example
    //   because the marketplace already shows an invoice. That is not a refusal ("Reddedildi").
    internal static readonly Expression<Func<Invoice, bool>> IssuedInvoiceNotOnPlatform = invoice =>
        invoice.Status == InvoiceStatus.MarketplaceFailed
        || ((invoice.Status == InvoiceStatus.Accepted || invoice.Status == InvoiceStatus.MarketplacePending) && invoice.LastErrorCode != null);

    internal static readonly Expression<Func<Invoice, bool>> IssuedInvoiceNeedsReview = invoice =>
        invoice.Status == InvoiceStatus.ManualReview && invoice.ExternalReference != null && invoice.ExternalReference != "";

    private static readonly Func<Invoice, bool> IsIssuedInvoiceNotOnPlatform = IssuedInvoiceNotOnPlatform.Compile();
    private static readonly Func<Invoice, bool> IsIssuedInvoiceNeedsReview = IssuedInvoiceNeedsReview.Compile();

    internal const string InvoiceNeedsReviewLabel = "FATURA_INCELEME_GEREKLI";

    internal static string? IssuedInvoiceLabel(Invoice invoice) =>
        IsIssuedInvoiceNeedsReview(invoice) ? InvoiceNeedsReviewLabel
        : IsIssuedInvoiceNotOnPlatform(invoice) ? "FATURA_PLATFORMA_AKTARILMADI"
        : null;

    // The one invoice label of a package: the orders list, the order detail and the invoice page read it the same way.
    internal static string PackageInvoiceLabel(ShipmentPackage? package, Invoice? invoice, string customerJson, IEnumerable<string?> packageRawStatuses, string? platformCode, bool marketplaceDeliveryConfirmed = false) =>
        ManualInvoiceLabel(package?.ManualInvoiceStatus)
            ?? InvoiceLabelForPlatform(invoice, package?.MarketplaceInvoiceStatus ?? MarketplaceInvoiceStatus.Unknown, customerJson, packageRawStatuses, platformCode, marketplaceDeliveryConfirmed);

    // The fiscal states the invoice workspace labels (pre-provider refusal, lost answer, adjustment) read the same here.
    internal static string InvoiceLabelForPlatform(Invoice? invoice, MarketplaceInvoiceStatus marketplaceStatus, string customerJson, IEnumerable<string?> packageRawStatuses, string? platformCode, bool marketplaceDeliveryConfirmed = false)
        => invoice is not null && InvoicingBillingService.SharedFiscalLabel(invoice.Status, invoice.LastErrorCode, invoice.ExternalReference) is { } shared
            ? shared
            : invoice is not null && IssuedInvoiceLabel(invoice) is { } issued
            ? issued
            : InvoiceLabelForPlatform(invoice?.Status, invoice?.InvoiceNumber, marketplaceStatus, customerJson, packageRawStatuses, platformCode, marketplaceDeliveryConfirmed);

    internal static string InvoiceLabelForPlatform(InvoiceStatus? invoiceStatus, string? invoiceNumber, MarketplaceInvoiceStatus marketplaceStatus, string customerJson, IEnumerable<string?> packageRawStatuses, string? platformCode, bool marketplaceDeliveryConfirmed = false)
    {
        var label = InvoiceLabel(invoiceStatus, invoiceNumber, marketplaceStatus, customerJson, packageRawStatuses, marketplaceDeliveryConfirmed);
        if (string.Equals(platformCode, "SHOPIFY", StringComparison.OrdinalIgnoreCase))
        {
            if (invoiceStatus == InvoiceStatus.Draft) return "FATURA_BEKLIYOR";
            if (invoiceStatus == InvoiceStatus.Completed) return "FATURA_KONTROLDE";
            // Shopify holds only the link we write (ravencia.invoice_pdf_url): without a panel invoice and without
            // that link nothing says whether the order was invoiced elsewhere, so it is to be reviewed, not waiting.
            if (invoiceStatus is null && label is "FATURA_BILINMIYOR" or "FATURA_BEKLIYOR") return ShopifyUnconfirmedInvoiceLabel;
            if (label == "FATURA_BILINMIYOR") return "FATURA_BEKLIYOR";
        }
        // n11 gives no invoice state, only the link of an invoice that was attached to the order (empty until
        // one is); Pazarama gives nothing at all. A link means an invoice exists at the marketplace but no local
        // record matches it, so it is shown for review.
        else if (label == "FATURA_BILINMIYOR" && string.Equals(platformCode, "N11", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(JsonText(customerJson, "invoiceLink")))
            return "FATURA_KONTROLDE";
        // n11, PTT AVM and Pazarama never show an invoice uploaded in their seller panel, so an unknown state there
        // cannot be checked from the marketplace: the panel's own record decides (owner, 24 Oct 2026 for n11 / PTT
        // AVM, 31 Oct 2026 for Pazarama). No panel invoice is waiting for an invoice; an invoice the panel issued and
        // delivered marks the package invoiced (RecordConfirmedDelivery) and reads as issued.
        else if (label == "FATURA_BILINMIYOR" && IsUnverifiableInvoicePlatform(platformCode))
            return UnverifiedInvoiceLabel;
        return label;
    }

    internal const string UnverifiedInvoiceLabel = "FATURA_BEKLIYOR";

    // "İncelenecek": a Shopify order with no panel invoice and no invoice link of ours. It is still invoice work: it
    // counts and is due exactly as "Fatura Bekliyor" does (IsAwaitingInvoiceLabel).
    internal const string ShopifyUnconfirmedInvoiceLabel = "FATURA_INCELENECEK";

    internal static bool IsAwaitingInvoiceLabel(string? label) => label is "FATURA_BEKLIYOR" or ShopifyUnconfirmedInvoiceLabel;

    internal static readonly string[] UnverifiableInvoicePlatforms = ["N11", "PTTAVM", "PAZARAMA"];

    internal static bool IsUnverifiableInvoicePlatform(string? platformCode) =>
        platformCode?.Trim().ToUpperInvariant() is "N11" or "PTTAVM" or "PAZARAMA";

    internal static ShipmentPackage? SelectDisplayPackage(IEnumerable<ShipmentPackage> packages, string? platformCode)
    {
        var ordered = packages.OrderByDescending(x => x.StatusOccurredAt).ThenByDescending(x => x.UpdatedAt).ToList();
        // The package an order card shows is a live one; a cancelled split package only when
        // nothing else is left.
        if (!string.Equals(platformCode, "SHOPIFY", StringComparison.OrdinalIgnoreCase))
            return ordered.FirstOrDefault(x => x.Status != ShipmentPackageStatus.Cancelled) ?? ordered.FirstOrDefault();

        var visible = ordered.Where(x => !(x.ExternalPackageId.StartsWith("order:", StringComparison.OrdinalIgnoreCase)
            && x.ExternalPackageId.EndsWith(":remainder", StringComparison.OrdinalIgnoreCase))).ToList();
        return visible.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.CargoTrackingNumber))
            ?? visible.FirstOrDefault(x => x.Status != ShipmentPackageStatus.Cancelled)
            ?? visible.FirstOrDefault();
    }

    private static bool IsInvoicedRemoteStatus(string? status) =>
        string.Equals(status?.Trim(), "INVOICED", StringComparison.OrdinalIgnoreCase);

    private static string? InvoiceDocumentUrl(string customerJson)
    {
        return ValidInvoiceDocumentUrl(JsonText(customerJson, "invoiceLink"));
    }

    private static string? ValidInvoiceDocumentUrl(string? value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? uri.ToString() : null;
    }

    // n11 facts the order card shows besides the shared fields: cargo tracking (number and carrier
    // link), delivery type, commission rate and the discounts split by who paid them.
    internal static IReadOnlyList<OrderInfoItem> N11OrderInfo(IEnumerable<string?> lineSnapshots, string customerSnapshotJson)
    {
        var info = new List<OrderInfoItem>();
        var trackingNumber = JsonText(customerSnapshotJson, "cargoSenderNumber");
        var trackingLink = JsonText(customerSnapshotJson, "cargoTrackingLink");
        var safeLink = Uri.TryCreate(trackingLink, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? uri.ToString() : null;
        if (trackingNumber is not null) info.Add(new("Kargo takip no", trackingNumber, safeLink));
        if (JsonText(customerSnapshotJson, "deliveryAddressType") is { } deliveryType) info.Add(new("Teslimat tipi", deliveryType));

        decimal mallDiscount = 0, sellerDiscount = 0;
        var rates = new SortedSet<decimal>();
        foreach (var snapshot in lineSnapshots)
        {
            if (string.IsNullOrWhiteSpace(snapshot)) continue;
            try
            {
                using var document = JsonDocument.Parse(snapshot);
                var line = document.RootElement;
                if (line.ValueKind != JsonValueKind.Object) continue;
                mallDiscount += JsonAmount(line, "totalMallDiscountPrice");
                sellerDiscount += JsonAmount(line, "totalSellerDiscountPrice");
                if (line.TryGetProperty("commissionRate", out var rate) && rate.ValueKind == JsonValueKind.Number && rate.TryGetDecimal(out var commission))
                    rates.Add(commission - JsonAmount(line, "sellerCampaignCommissionRate"));
            }
            catch (JsonException) { }
        }
        var tr = System.Globalization.CultureInfo.GetCultureInfo("tr-TR");
        if (rates.Count > 0) info.Add(new("Komisyon oranı", string.Join(" / ", rates.Select(rate => "%" + rate.ToString("0.##", tr)))));
        if (mallDiscount > 0) info.Add(new("N11 indirimi", mallDiscount.ToString("N2", tr) + " ₺"));
        if (sellerDiscount > 0) info.Add(new("Satıcı indirimi", sellerDiscount.ToString("N2", tr) + " ₺"));
        return info;
    }

    private static decimal JsonAmount(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var amount) ? amount : 0;

    private static SourceLineView SourceLine(string? json)
    {
        var snapshot = json ?? "{}";
        var image = NormalizeImageUrl(SourceImageUrl(snapshot));
        var color = JsonText(snapshot, "productColor", "color", "colorName");
        var size = JsonText(snapshot, "productSize", "size", "sizeName");
        var options = new List<string>();
        if (!string.IsNullOrWhiteSpace(color)) options.Add($"Renk: {color}");
        if (!string.IsNullOrWhiteSpace(size)) options.Add($"Beden: {size}");
        return new(image, JsonText(snapshot, "modelCode"), options.Count == 0 ? null : string.Join(" | ", options));
    }

    // The product's own image fields, the most specific first. The first field that holds a usable address wins,
    // wherever it sits in the snapshot: a match in document order picked a claim photo listed before the product's.
    private static readonly string[] ProductImageFields = ["productImageUrl", "productImageUrlFormat", "emaproductImageUrlFormat", "imageUrl", "productImage", "image"];

    // Parts of a line snapshot whose images are not the product: a Hepsiburada claim merged into the line keeps the
    // customer's evidence photos under Reports[].ImageUrl (an images.hepsiburada.net/oms/claim address the panel cannot load).
    private static readonly HashSet<string> NonProductImageContainers = new(StringComparer.OrdinalIgnoreCase) { "Reports" };

    internal static string? SourceImageUrl(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            return FindImageUrl(document.RootElement)
                ?? ProductImageFields.Select(field => FindImageField(document.RootElement, field)).FirstOrDefault(value => value is not null);
        }
        catch (JsonException) { return null; }
    }

    // The line's image and, when it is the marketplace's, the catalog's photo to show if that address no longer loads.
    internal static (string? ImageUrl, string? FallbackImageUrl) LineImageUrls(string? sourceImageUrl, string? catalogImageUrl) =>
        sourceImageUrl is null
            ? (catalogImageUrl, null)
            : (sourceImageUrl, catalogImageUrl is null || string.Equals(catalogImageUrl, sourceImageUrl, StringComparison.Ordinal) ? null : catalogImageUrl);

    // A return detail line's photo: the marketplace's with the catalog's as fallback (LineImageUrls); with neither, the
    // barcode lookup (/orders/product-image) is the photo.
    // The lookup carries the claim's store (connectionId) when known, as the order screens' own lookup does.
    internal static (string? ImageUrl, string? FallbackImageUrl) ReturnDetailLineImageUrls(string? sourceImageUrl, string? catalogImageUrl, string? barcode, Guid? connectionId = null)
    {
        var images = LineImageUrls(sourceImageUrl, catalogImageUrl);
        return images.ImageUrl is null && !string.IsNullOrWhiteSpace(barcode)
            ? ($"/api/v1/orders/product-image?barcode={Uri.EscapeDataString(barcode)}{(connectionId is { } id && id != Guid.Empty ? $"&connectionId={id:D}" : "")}", null)
            : images;
    }

    private static string? FindImageField(JsonElement element, string field)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (NonProductImageContainers.Contains(property.Name)) continue;
                if (property.Name.Equals(field, StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.String)
                {
                    var value = property.Value.GetString();
                    if (NormalizeImageUrl(value) is not null) return value;
                }
                var nested = FindImageField(property.Value, field); if (nested is not null) return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) { var nested = FindImageField(item, field); if (nested is not null) return nested; }
        return null;
    }

    internal static string? NormalizeImageUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var candidate = value.Trim();
        // Hepsiburada returns productImageUrlFormat with a {size} token.
        // Keep the stored marketplace snapshot untouched and resolve the token
        // for the order UI when projecting the image URL.
        candidate = candidate.Replace("{size}", "500", StringComparison.OrdinalIgnoreCase);
        if (candidate.StartsWith("//", StringComparison.Ordinal)) candidate = "https:" + candidate;
        else if (candidate.StartsWith("/", StringComparison.Ordinal)) candidate = "https://cdn.dsmcdn.com" + candidate;
        return Uri.TryCreate(candidate, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? uri.ToString() : null;
    }

    private static string? FindImageUrl(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (NonProductImageContainers.Contains(property.Name)) continue;
                if (property.Name.Equals("images", StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.Array)
                    foreach (var image in property.Value.EnumerateArray())
                    {
                        var url = image.ValueKind == JsonValueKind.Object && image.TryGetProperty("url", out var value) ? value.ToString() : image.ToString();
                        if (NormalizeImageUrl(url) is not null) return url;
                    }
                var nested = FindImageUrl(property.Value); if (!string.IsNullOrWhiteSpace(nested)) return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) { var nested = FindImageUrl(item); if (!string.IsNullOrWhiteSpace(nested)) return nested; }
        return null;
    }

    private sealed record SourceLineView(string? ImageUrl, string? ModelCode, string? OptionSignature);

    internal static string? MaskIdentityNumber(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        return trimmed.Length > 4 ? $"***{trimmed[^4..]}" : "***";
    }

    // Only the delivery fields a label prints: no phone, e-mail, identity or tax number.
    internal static OrderAddressView? ListAddress(string shipmentAddressJson)
    {
        var name = NameFrom(shipmentAddressJson, "firstName", "lastName", "shippingFirstName", "shippingLastName", "fullName", "name", "company", "companyName");
        var address = new OrderAddressView(
            IsMeaningfulText(name) ? name : null,
            JsonText(shipmentAddressJson, "fullAddress"),
            JsonText(shipmentAddressJson, "address1"),
            JsonText(shipmentAddressJson, "address2"),
            JsonText(shipmentAddressJson, "neighborhood"),
            JsonText(shipmentAddressJson, "district", "countyName"),
            JsonText(shipmentAddressJson, "city", "province"),
            JsonText(shipmentAddressJson, "postalCode", "zip"),
            JsonText(shipmentAddressJson, "countryCode", "country"),
            JsonBool(shipmentAddressJson, "isDeliveryPoint"),
            JsonText(shipmentAddressJson, "deliveryPointType"));
        return address == new OrderAddressView(null, null, null, null, null, null, null, null, null) ? null : address;
    }

    private static string? JsonText(string json, params string[] names)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            return FindText(document.RootElement, new HashSet<string>(names, StringComparer.OrdinalIgnoreCase));
        }
        catch (JsonException) { return null; }
    }

    private static string? FindText(JsonElement element, HashSet<string> names)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (names.Contains(property.Name) && property.Value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined or JsonValueKind.Object or JsonValueKind.Array))
                {
                    var value = property.Value.ToString();
                    if (IsMeaningfulText(value)) return value;
                }
                var nested = FindText(property.Value, names); if (!string.IsNullOrWhiteSpace(nested)) return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) { var nested = FindText(item, names); if (!string.IsNullOrWhiteSpace(nested)) return nested; }
        return null;
    }

    private static bool IsMeaningfulText(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Any(char.IsLetterOrDigit);

    private static bool JsonBool(string json, params string[] names)
    {
        var value = JsonText(json, names);
        return bool.TryParse(value, out var parsed) && parsed || value is "1" or "YES" or "EVET";
    }

    private static bool? JsonNullableBool(string json, params string[] names)
    {
        var value = JsonText(json, names);
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (bool.TryParse(value, out var parsed)) return parsed;
        return value is "1" or "YES" or "EVET" ? true : value is "0" or "NO" or "HAYIR" ? false : null;
    }

    private static DateTimeOffset? JsonInstant(string json, params string[] names)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            var namesSet = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
            return FindInstant(document.RootElement, namesSet);
        }
        catch (JsonException) { return null; }
    }

    private static DateTimeOffset? FindInstant(JsonElement element, HashSet<string> names)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (names.Contains(property.Name))
                {
                    if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt64(out var num) && num > 0)
                    {
                        try { return DateTimeOffset.FromUnixTimeMilliseconds(num); } catch (ArgumentOutOfRangeException) { }
                    }
                    else if (property.Value.ValueKind == JsonValueKind.String)
                    {
                        var str = property.Value.GetString();
                        if (long.TryParse(str, out var numStr) && numStr > 0)
                        {
                            try { return DateTimeOffset.FromUnixTimeMilliseconds(numStr); } catch (ArgumentOutOfRangeException) { }
                        }
                        if (DateTimeOffset.TryParse(str, out var parsed)) return parsed;
                    }
                }
                var nested = FindInstant(property.Value, names);
                if (nested is not null) return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = FindInstant(item, names);
                if (nested is not null) return nested;
            }
        }
        return null;
    }
    private static ShipmentView Map(ShipmentPackage x, string orderNumber) => new(x.Id, x.OrderId, orderNumber, x.ExternalPackageId, Wire(x.Status), x.RawStatus, x.CargoTrackingNumber, x.StatusOccurredAt, x.Version, x.CargoProviderExternalId, ShipmentPackageClassification.IsResend(x.CreatedBy, x.OriginExternalPackageId), x.UnreadableLineCount, HandSetInvoiceStatus(x.ManualInvoiceStatus), x.CargoBarcode);
    private static string Wire<T>(T value) where T : Enum => string.Concat(value.ToString().Select((ch, index) => char.IsUpper(ch) && index > 0 ? "_" + ch : ch.ToString())).ToUpperInvariant();
    private const string VerifyingCode = "EXTERNAL_EFFECT_VERIFYING";
    private const string VerifyingMessage = "Pazaryerinin yanıtı kesinleşmedi; sonuç birkaç saniye içinde pazaryerinden okunup panel güncellenecek. İşlemi yeniden göndermeyin.";

    private static ServiceResult<T> Verifying<T>() => ServiceResult<T>.Fail(VerifyingCode, VerifyingMessage, 409);

    // The marketplace's answer was unclear: the action is not sent again; a verification job
    // reads the marketplace and settles the effect (see MarketplaceJobProcessor.ReconcileExternalEffect).
    private async Task<ServiceResult<T>> VerifyLaterAsync<T>(ExternalEffectRecord effect, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var dedup = ExternalEffectReconcilePolicy.JobDedupKey(effect.Id);
        if (!await db.IntegrationJobs.AnyAsync(x => x.TenantId == effect.TenantId && x.JobDedupKey == dedup, cancellationToken))
            db.IntegrationJobs.Add(ExternalEffectReconcilePolicy.NewJob(effect, now, now + ExternalEffectReconcilePolicy.FirstCheckDelay));
        await db.SaveChangesAsync(cancellationToken);
        return Verifying<T>();
    }

    private static async Task<ServiceResult<T>> QueueInsteadAsync<T>(Task<ServiceResult<Guid>> enqueue)
    {
        var queued = await enqueue;
        return queued.Error is { } error
            ? ServiceResult<T>.Fail(error.Code, error.Message, error.Status, error.FieldErrors)
            : ServiceResult<T>.Fail("ACTION_QUEUED", "Pazaryeri istek kotası şu an dolu; işlem sıraya alındı ve birkaç saniye içinde gönderilecek.", 409);
    }

    private static bool IsAmbiguous(AdapterError error) => WriteOutcomePolicy.IsUncertain(error);
    private static ServiceResult<T> Invalid<T>(string field, string message) => ServiceResult<T>.Fail("VALIDATION_FAILED", message, 422, new Dictionary<string, string[]> { [field] = [message] });
    private static ServiceResult<T> NotFound<T>() => ServiceResult<T>.Fail("RESOURCE_NOT_FOUND", "Kayıt bulunamadı.", 404);
    private static ServiceResult<T> Precondition<T>(long version) => ServiceResult<T>.Fail("CONCURRENCY_CONFLICT", "Kayıt siz düzenlerken başka bir işlemle güncellendi. Sayfayı yenileyip tekrar deneyin.", 412);
}
