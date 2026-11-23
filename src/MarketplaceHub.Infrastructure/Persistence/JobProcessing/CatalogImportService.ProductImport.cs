using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

using CategoryAttributeContext = MarketplaceHub.Infrastructure.Persistence.CatalogAttributeService.CategoryAttributeContext;
using ImportSummaryMode = MarketplaceHub.Infrastructure.Persistence.MarketplaceJobProcessor.ImportSummaryMode;
using LocalCategoryAttribute = MarketplaceHub.Infrastructure.Persistence.CatalogAttributeService.LocalCategoryAttribute;

namespace MarketplaceHub.Infrastructure.Persistence;

internal sealed partial class CatalogImportService
{
    // ISLEYICI-11 (16 Nov 2026): catalog pages of 100 in one import (half a million products), a guard against a list
    // that never ends.
    internal const int ProductImportPageCap = 5_000;

    // KATALOG-14 phase D5b: while a marketplace import saves, a colour that comes in apart from its model never moves the
    // model (ModelMirror, AppDbContext.MarketplaceCatalogImport).
    internal async Task<bool> SyncProducts(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, Guid? jobId, CancellationToken cancellationToken)
    {
        var previous = db.MarketplaceCatalogImport;
        db.MarketplaceCatalogImport = true;
        try { return await SyncProductsCore(tenantId, connectionId, payloadJson, correlationId, jobId, cancellationToken); }
        finally { db.MarketplaceCatalogImport = previous; }
    }

    private async Task<bool> SyncProductsCore(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, Guid? jobId, CancellationToken cancellationToken)
    {
        var connection = await db.PlatformConnections.AsNoTracking()
            .SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == connectionId && (x.PlatformCode == "TRENDYOL" || x.PlatformCode == "SHOPIFY" || x.PlatformCode == "HEPSIBURADA" || x.PlatformCode == "N11" || x.PlatformCode == "PAZARAMA" || x.PlatformCode == "PTTAVM"), cancellationToken);
        var isShopify = connection?.PlatformCode == "SHOPIFY";
        var isHepsiburada = connection?.PlatformCode == "HEPSIBURADA";
        var isN11 = connection?.PlatformCode == "N11";
        var isPazarama = connection?.PlatformCode == "PAZARAMA";
        var isPttAvm = connection?.PlatformCode == "PTTAVM";
        // n11, Pazarama and PTT AVM list a flat catalog: pending listings are real listings, there is no
        // category-mapping or watermark logic and no brand reference, and the import only observes
        // the remote catalog (n11 keys it by stock code, Pazarama and PTT AVM by barcode).
        var flatCatalog = isN11 || isPazarama || isPttAvm;
        // Product import is read-only on the remote platform and writes only to
        // Ravencia's local catalog. Shopify observations never become local
        // price/stock authority.
        // Keep it restricted to operational connections and recognised environments.
        // KUYRUK-9 (16 Nov 2026): named instead of the generic F3_JOB_REJECTED; still blocked, as before.
        if (connection is null || connection.Environment is not ("STAGE" or "PRODUCTION") || connection.Status is not ("ACTIVE" or "VERIFIED"))
            throw new JobProcessingException(JobExecutionResult.Blocked("PRODUCT_IMPORT_CONNECTION_NOT_ELIGIBLE", "Ürün çekimi yalnız etkin ya da doğrulanmış, canlı veya test ortamındaki desteklenen mağazalarda yapılır; bu mağazada ürün okunmadı."));
        var inventoryPolicy = await db.ConnectionInventoryPolicies.AsNoTracking()
            .SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId, cancellationToken);

        var fullScan = ReadBoolean(payloadJson, "full");
        var newOnly = ReadBoolean(payloadJson, "newOnly");
        var existingOnly = ReadBoolean(payloadJson, "existingOnly");
        var mappingOnly = ReadBoolean(payloadJson, "mappingOnly");
        var optionsOnly = ReadBoolean(payloadJson, "optionsOnly");
        var includeArchived = ReadBoolean(payloadJson, "includeArchived");
        // n11 and Pazarama listings awaiting approval are real listings that also take price and
        // stock updates, so they are always part of their import.
        var includePendingApproval = flatCatalog || !isShopify && !isHepsiburada && ReadBoolean(payloadJson, "includePendingApproval");
        var updateExistingProducts = ReadBooleanOrDefault(payloadJson, "updateExistingProducts", true);
        // "Mevcut Ürün Bilgilerini Güncelle" ticked by the user: the job names it itself (a job without it keeps the edits).
        var updateExistingProductsRequested = ReadBoolean(payloadJson, "updateExistingProducts");
        // The single legacy flag still sits in jobs queued before the split: it stands for both halves.
        var legacyUpdatePricesAndStock = ReadBoolean(payloadJson, "updateExistingPricesAndStock");
        var updatePrices = ReadBooleanOrDefault(payloadJson, "updatePrices", legacyUpdatePricesAndStock);
        var updateStock = ReadBooleanOrDefault(payloadJson, "updateStock", legacyUpdatePricesAndStock);
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
            : optionsOnly
            ? "Varyant seçenekleri yenileniyor"
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
        var contentLabel = optionsOnly
            ? " · yalnızca varyant seçenekleri değişecek"
            : !newOnly && !mappingOnly
            ? updateExistingProducts ? " · Mevcut ürün bilgileri güncellenecek" : " · Mevcut ürün bilgileri korunacak"
            : "";
        // A run that reads the products' information (full scan, the incremental scan, "Mevcut ürünleri güncelle", a single
        // lookup) completes the listing profiles of the products it matched; new-only, mapping-only and options-only runs
        // do not (owner decision 6 Nov 2026).
        // Prices and/or stock alone, without "Mevcut Ürün Bilgilerini Güncelle" (ProductImportScanPolicy.FocusedPriceStock).
        var focusedPriceStock = ProductImportScanPolicy.FocusedPriceStock(connection.PlatformCode, existingOnly, singleLookup, newOnly, mappingOnly, optionsOnly, updateExistingProducts, updatePrices, updateStock);
        if (focusedPriceStock)
            contentLabel = updatePrices && updateStock ? " · Yalnız fiyat ve stok güncellenecek" : updatePrices ? " · Yalnız fiyat güncellenecek" : " · Yalnız stok güncellenecek";
        run.ImportSummaryMode = focusedPriceStock ? ImportSummaryMode.FocusedPriceStock
            : mappingOnly && !isPttAvm ? ImportSummaryMode.Mapping
            : newOnly && !isShopify && !isHepsiburada && !flatCatalog ? ImportSummaryMode.NewOnly
            : ImportSummaryMode.Standard;
        var completesListingProfiles = !newOnly && !mappingOnly && !optionsOnly && !focusedPriceStock && ImportedListingProfiles.Supported(connection.PlatformCode);
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
                await UpdateProductSyncProgressAsync(tenantId, finishedJob, importSession.ReceivedProducts, importSession.TotalProducts, 100, "Çekim tamamlandı", cancellationToken, keepExistingTotal: true);
            return true;
        }
        var previousProgress = jobId is { } progressJob
            ? await db.IntegrationJobs.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == progressJob).Select(x => new { x.ProgressProcessed, x.ProgressSkipped, x.ProgressFailed }).SingleOrDefaultAsync(cancellationToken)
            : null;
        run.ImportProcessedCount = previousProgress?.ProgressProcessed ?? 0;
        run.ImportSkippedCount = previousProgress?.ProgressSkipped ?? 0;
        run.ImportFailedCount = previousProgress?.ProgressFailed ?? 0;
        var receivedProducts = importSession.ReceivedProducts;
        run.ProductImportTelemetry = true;
        run.ImportReceivedCount = receivedProducts;
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
        var hasCategoryMappings = !isShopify && !isHepsiburada && !flatCatalog && await db.CategoryMappings.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.Status == "VERIFIED", cancellationToken);
        // The first attribute backfill must revisit the already imported catalog. Keep
        // LastModifiedWatermark null until that full pass is complete so a retry cannot
        // accidentally switch to the incremental window halfway through the backfill.
        if (!isShopify && !isHepsiburada && !flatCatalog && !hasCategoryMappings && cursor.OpaqueCursor is null && cursor.LastModifiedWatermark is not null)
        {
            cursor.LastModifiedWatermark = null;
            cursor.Version++;
            await db.SaveChangesAsync(cancellationToken);
        }
        // "Mevcut Ürünleri Güncelle" (Fiyat ve Stokları Güncelle) reads the whole catalog, not the changed-since window.
        var modifiedAfter = ProductImportScanPolicy.IncrementalReadStart(effectiveFullScan, existingOnly, singleLookup, hasSnapshots, cursor.LastModifiedWatermark);
        var productFilter = ProductImportFilter(modifiedAfter, productLookup, includePendingApproval);
        // Read the remote catalog into the durable staging pool before preparing
        // the larger Trendyol reference snapshots. The worker can therefore
        // resume the scan and finalize complete model groups without retaining
        // the full remote catalog in memory.
        ReferenceSnapshot? categoryReferences = null;
        IReadOnlyList<ReferenceItem> categoryItems = [];
        // Keep one shared panel attribute for the same normalized name. This lets
        // an attribute such as "Bel" collect every category where Trendyol uses it
        // instead of creating a separate hidden definition for each category.
        var importedAttributeLibrary = new Dictionary<string, AttributeDefinition>(StringComparer.Ordinal);
        var categoryContexts = new Dictionary<string, CategoryAttributeContext>(StringComparer.Ordinal);
        var nextCursor = scanRequired
            ? importSession.PageNumber > 0 ? importSession.NextCursor : singleLookup || effectiveFullScan || existingOnly ? null : cursor.OpaqueCursor
            : null;
        var pageNumber = importSession.PageNumber;
        if (jobId is { } currentJob && scanRequired)
            await UpdateProductSyncProgressAsync(tenantId, currentJob, receivedProducts, totalProducts, null, scanLabel + lifecycleLabel + contentLabel + " · Ürün çekim havuzu hazırlanıyor", cancellationToken);
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
                    $"{scanLabel}{lifecycleLabel} · {pageNumber}. sayfa okunuyor · Alınan {TrCount(receivedProducts)} · İşlenen {TrCount(run.ImportProcessedCount)} · Atlanan {TrCount(run.ImportSkippedCount)} · Hatalı {TrCount(run.ImportFailedCount)}",
                    cancellationToken);
            }
            TrackRequest();
            // A staged snapshot is stamped with the moment its page read began: the stock it carries is no newer.
            var pageReadAt = timeProvider.GetUtcNow();
            var result = await products.ListCatalogAsync(
                Context(tenantId, connectionId, correlationId, $"product-sync:{nextCursor ?? "0"}"),
                new(nextCursor, 100),
                productFilter,
                cancellationToken);
            if (!result.IsSuccess) { TrackResultFailure(result.Error); throw JobProcessingException.FromAdapter(result.Error!); }
            if (singleLookup && receivedProducts == 0 && nextCursor is null && result.Value!.Items.Count == 0 && !result.Value.HasMore
                && ProductImportMergePolicy.UpperCaseLookupRetry(productFilter) is { } upperCaseFilter)
            {
                productFilter = upperCaseFilter;
                pageNumber--;
                continue;
            }
            // A product read is not a product changed: the cursor's changed count is what the import processed
            // (CursorImportCounts), not every product the page brought.
            run.ReceivedCount += result.Value!.Items.Count;
            receivedProducts += result.Value.Items.Count;
            run.ImportReceivedCount = receivedProducts;
            totalProducts ??= result.Value.TotalCount;
            var pageSnapshots = result.Value.Items
                .Where(snapshot => includeDrafts || !snapshot.IsDraft)
                .Where(snapshot => includePendingApproval || !snapshot.IsPendingApproval)
                .Select(snapshot => includeArchived
                    ? snapshot
                    : snapshot with { Variants = snapshot.Variants.Where(variant => !variant.Archived).ToList() })
                .Where(snapshot => includeArchived || snapshot.Variants.Count > 0)
                .ToList();
            run.ImportSkippedCount += result.Value.Items.Count - pageSnapshots.Count;
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
                    CreatedAt = pageReadAt
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
            // Every code on the page counts as seen, archived and draft ones too: they still exist at the marketplace.
            await MarkVariantLinksSeen(tenantId, connectionId, result.Value.Items, cancellationToken);

            var hasMore = result.Value.HasMore;
            if (hasMore && string.IsNullOrWhiteSpace(result.Value.NextCursor))
                throw new InvalidOperationException("Pazar yeri ürün sayfası hasMore=true ancak nextPageToken boş döndü.");
            // ISLEYICI-11: a catalog that names the page just read as the next one, or never ends, stops with its own code
            // before this page's position is saved: the scan stays at the page saved before, so a retry reads on from there.
            // The page number is the session's (it survives a resumed scan).
            if (hasMore && nextCursor is not null && string.Equals(result.Value.NextCursor, nextCursor, StringComparison.Ordinal))
                throw PagedReadLoop.GuardFailure("PRODUCT_IMPORT_PAGE_REPEATED", "Pazaryeri ürün listesi okunan sayfayı yeniden sonraki sayfa olarak verdi; ürün aktarımı durduruldu.");
            if (hasMore && pageNumber >= ProductImportPageCap)
                throw PagedReadLoop.GuardFailure("PRODUCT_IMPORT_PAGE_CAP", $"Pazaryeri ürün listesi {ProductImportPageCap} sayfayı aştı; ürün çekimi kaldığı sayfada durduruldu.");
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
                run.ImportFailedCount++;
                run.FailedCount++;
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
                await UpdateProductSyncProgressAsync(tenantId, receivedJob, receivedProducts, totalProducts, percent, ProductImportProgressLabel(totalProducts, receivedProducts, mappingOnly), cancellationToken);
            }

            if (!result.Value.HasMore || cancellationToken.IsCancellationRequested) break;
        }

        cancellationToken.ThrowIfCancellationRequested();
        // A whole, unfiltered catalog read to its last page is the only scan that can tell a code is gone. Trendyol
        // leaves listings awaiting approval out unless asked; flat catalogs link by the panel's codes, which their
        // listings need not carry, so they are left to the push signal.
        if (scanRequired && effectiveFullScan && !singleLookup && modifiedAfter is null
            && (isHepsiburada || isShopify || connection.PlatformCode == "TRENDYOL" && includePendingApproval))
            await MarkMissingVariantLinks(tenantId, connectionId, connection.PlatformCode, importSession.CreatedAt, cancellationToken);

        var stagedSession = await db.ProductImportSessions.AsNoTracking().SingleAsync(x => x.TenantId == tenantId && x.JobId == importJobId, cancellationToken);
        receivedProducts = stagedSession.ReceivedProducts;
        run.ImportReceivedCount = receivedProducts;
        // The whole catalog is read here, so what was received is the total the processing below works through
        // (Trendyol's cursor paging reports none): the bar and percent run on it instead of staying unknown.
        totalProducts = stagedSession.TotalProducts ?? (receivedProducts > 0 ? receivedProducts : null);
        pageNumber = stagedSession.PageNumber;

        if (jobId is { } referenceJob)
            await UpdateProductSyncProgressAsync(
                tenantId,
                referenceJob,
                receivedProducts,
                totalProducts,
                totalProducts is { } completeTotal && completeTotal > 0 ? 99 : null,
                ProductImportProgressLabel(totalProducts, receivedProducts, mappingOnly),
                cancellationToken);

        // The import maps the listing's brand from what the listing carries (ImportBrandId, ImportedBrandRule): it needs
        // no brand list of its own and calls no marketplace for it, so the brand list is synced from the mapping page
        // alone. The calls below
        // intentionally happen after remote product paging so a cold reference cache cannot hide product-read progress.
        if (jobId is { } categoryReferenceJob)
            await UpdateProductSyncProgressAsync(
                tenantId,
                categoryReferenceJob,
                receivedProducts,
                totalProducts,
                totalProducts is { } completeTotal && completeTotal > 0 ? 99 : null,
                ProductImportProgressLabel(totalProducts, receivedProducts, mappingOnly),
                cancellationToken);
        // Mapping never reads category references (it only links), and options-only and Shopify runs never did. A new-only
        // run and the focused price/stock path read them on the first product that needs a category context (a product or
        // size the panel lacks), so a run that finds everything in the panel reads no Trendyol reference at all.
        var categoryReferencesWanted = !isShopify && !optionsOnly && !mappingOnly;
        var categoryReferencesLoaded = false;
        async Task LoadCategoryReferencesAsync()
        {
            categoryReferencesLoaded = true;
            categoryReferences = await EnsureReferenceSnapshot(tenantId, connectionId, "CATEGORIES", null, correlationId, cancellationToken);
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
        }
        if (categoryReferencesWanted && !newOnly && !focusedPriceStock) await LoadCategoryReferencesAsync();

        if (jobId is { } orderingJob)
            await UpdateProductSyncProgressAsync(tenantId, orderingJob, receivedProducts, totalProducts, null, ProductImportProgressLabel(totalProducts, receivedProducts, mappingOnly), cancellationToken);

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
        // New products are listed newest first (CreatedAt descending). A product takes the date the marketplace created
        // it (CatalogImportOrdering.RemoteCreatedAt); one without such a date is stamped by its place in the import, the
        // marketplace's first product newest and each later one a millisecond older.
        var catalogOrderStamp = timeProvider.GetUtcNow();
        var catalogOrderPosition = 0;
        // A product's PRODUCT_IMPORT_FAILED issue is raised when its model's write throws (RecordProductImportFailure, for
        // every product of the model); the retry run resolves it once the model is stored. Any run now resolves it for the
        // products it wrote without that write throwing, once their model is saved (Y5 review, 23 Nov 2026: twelve stayed
        // open after later imports). Read once.
        var importFailurePrefix = $"product-sync-import:{connectionId}:";
        var openImportFailures = (await db.OperationalIssues.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.Code == "PRODUCT_IMPORT_FAILED" && x.Status != IssueStatus.Resolved && x.DedupeKey.StartsWith(importFailurePrefix))
                .Select(x => x.DedupeKey)
                .ToListAsync(cancellationToken))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var modelKey in modelKeys)
        {
            var stagingRows = await db.ProductImportStagingRecords.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.JobId == importJobId && x.ModelKey == modelKey && x.State == "STAGED")
                .OrderBy(x => x.ReceivedOrder)
                .ToListAsync(cancellationToken);
            if (stagingRows.Count == 0) continue;
            var snapshotGroups = stagingRows
                .Select(row => JsonSerializer.Deserialize<RemoteCatalogProduct>(row.SnapshotJson) ?? throw new JsonException("Ürün çekim havuzu kaydı çözümlenemedi."))
                .GroupBy(x => x.ExternalProductId, StringComparer.OrdinalIgnoreCase)
                .Select(MergeCatalogSnapshots)
                .ToList();
            // A model whose save hits a version conflict (DbUpdateConcurrencyException) is read and written once more from
            // a clean change tracker before its products count as failed (MZ002K, 14 Nov 2026: 12 cards lost to one
            // conflict nobody could trace). The counters and the link sets the first attempt moved are put back first.
            var modelAttempt = 0;
        retryModel:
            modelAttempt++;
            var attemptCounters = (run.ImportProcessedCount, run.ImportSkippedCount, run.AlreadyInPanelCount, run.NewProductCount, run.NewSizeCount, run.UnaddedSizeCount, run.UnchangedVariantCount);
            List<string> attemptAddedProductIds = [];
            List<string> attemptAddedVariantIds = [];
            // The products this attempt wrote (stored or mapped): only those settle an earlier import failure; one the run
            // skipped (new-only, existing-only, unchanged) or refused (a code clash) proves nothing about it.
            List<string> attemptWrittenProductIds = [];
            run.CatalogStockReadAt = stagingRows.Min(row => row.CreatedAt);
            try
            {
                if (isHepsiburada) await RekeyHepsiburadaVariantLinks(tenantId, connectionId, snapshotGroups, cancellationToken);
                foreach (var snapshot in snapshotGroups)
                {
                    if (mappingOnly)
                    {
                        var mapped = await MapExistingCatalogProduct(tenantId, connectionId, snapshot, isShopify, cancellationToken, crossMatchCodes: flatCatalog, reportUnmatched: flatCatalog, seedOffers: flatCatalog, inventoryPolicy: inventoryPolicy, seedSource: isPazarama ? "PAZARAMA" : isPttAvm ? "PTTAVM" : "N11", pttAvm: isPttAvm);
                        if (mapped) attemptWrittenProductIds.Add(snapshot.ExternalProductId);
                        if (!mapped) run.ImportSkippedCount++;
                        else run.ImportProcessedCount++;
                        continue;
                    }
                    var productAlreadyLinked = existingProductExternalIds?.Contains(snapshot.ExternalProductId) == true;
                    if (optionsOnly)
                    {
                        if (!productAlreadyLinked)
                        {
                            run.ImportSkippedCount++;
                            continue;
                        }
                        var optionsUpdated = await UpdateCatalogOptionsOnly(tenantId, connectionId, snapshot, cancellationToken);
                        if (optionsUpdated)
                        {
                            run.ImportProcessedCount++;
                            await ResolveIssue(tenantId, $"product-sync-import:{connectionId}:{Short(snapshot.ExternalProductId, 256)}", cancellationToken);
                        }
                        else run.ImportSkippedCount++;
                        continue;
                    }
                    var hasNewVariant = existingVariantExternalIds is not null
                        && snapshot.Variants.Any(variant => !existingVariantExternalIds.Contains(Short(variant.ExternalVariantId, 256)));
                    var matchesExistingIdentity = existingIdentityCodes is not null
                        && snapshot.Variants.Any(variant => existingIdentityCodes.Contains(NormalizeCatalogKey(isShopify ? variant.Barcode : variant.ModelCode ?? variant.Barcode, 160)));
                    if (newOnly && productAlreadyLinked && !hasNewVariant)
                    {
                        run.ImportSkippedCount++;
                        RecordProductRead(snapshot, JobReadOutcomes.Unchanged, null);
                        continue;
                    }
                    if (existingOnly && !productAlreadyLinked && !matchesExistingIdentity)
                    {
                        run.ImportSkippedCount++;
                        RecordProductRead(snapshot, JobReadOutcomes.Skipped, "Panelde eşleşen ürün yok; yalnız mevcut ürünler güncelleniyordu.");
                        continue;
                    }
                    if (focusedPriceStock)
                    {
                        run.ProductUnaddedSizeCount = 0;
                        var focused = await ApplyFocusedPriceStock(tenantId, connectionId, snapshot, inventoryPolicy, updatePrices, updateStock, cancellationToken);
                        // A single lookup of a product the panel does not hold yet still adds it below, as it always did.
                        if (focused != FocusedPriceStockOutcome.NotInPanel || !singleLookup)
                        {
                            if (focused == FocusedPriceStockOutcome.Changed) run.ImportProcessedCount++;
                            else run.ImportSkippedCount++;
                            RecordProductRead(snapshot,
                                focused switch { FocusedPriceStockOutcome.Changed => JobReadOutcomes.Updated, FocusedPriceStockOutcome.Unchanged => JobReadOutcomes.Unchanged, _ => JobReadOutcomes.Skipped },
                                focused switch
                                {
                                    FocusedPriceStockOutcome.NotInPanel => "Panelde eşleşen ürün yok; yalnız fiyat/stok güncelleniyordu, ürün eklenmedi.",
                                    FocusedPriceStockOutcome.Conflict => "Varyantları panelde birden fazla ürüne ait; fiyat/stok yazılmadı.",
                                    _ => run.ProductUnaddedSizeCount > 0 ? UnaddedSizesNote(run.ProductUnaddedSizeCount) : null
                                });
                            continue;
                        }
                    }
                    // "Yeni Ürünleri Aktar" adds only what the panel lacks (owner, 12 Nov 2026): a listing whose every variant
                    // the panel already holds (by link, stock code or barcode) is left alone before any category context is
                    // built; one with new sizes adds those sizes alone, every variant the panel holds untouched.
                    HashSet<string>? newOnlyAddIds = null;
                    if (run.ImportSummaryMode == ImportSummaryMode.NewOnly && existingVariantExternalIds is not null)
                    {
                        var unlinked = snapshot.Variants.Where(variant => !existingVariantExternalIds.Contains(Short(variant.ExternalVariantId, 256))).ToList();
                        var missing = unlinked.Count == 0 ? [] : await RemoteVariantsMissingFromPanel(tenantId, connectionId, snapshot with { Variants = unlinked }, cancellationToken);
                        if (missing.Count == 0)
                        {
                            run.ImportSkippedCount++;
                            run.AlreadyInPanelCount++;
                            RecordProductRead(snapshot, JobReadOutcomes.Unchanged, "Panelde zaten var.");
                            continue;
                        }
                        newOnlyAddIds = missing.Select(variant => Short(variant.ExternalVariantId, 256)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    }
                    if (categoryReferencesWanted && !categoryReferencesLoaded) await LoadCategoryReferencesAsync();
                    var categoryContext = categoryReferences is null
                        ? null
                        : await EnsureCategoryAttributeContext(tenantId, connectionId, categoryReferences, snapshot, categoryItems, importedAttributeLibrary, categoryContexts, correlationId, cancellationToken);
                    run.ProductUnaddedSizeCount = 0;
                    run.ProductImportedNewVariantCount = 0;
                    var changed = await UpsertCatalogProduct(tenantId, connectionId, snapshot, categoryContext, inventoryPolicy, cancellationToken, saveChanges: false, onlyNewVariants: newOnly && productAlreadyLinked, observeOnly: isShopify || isHepsiburada || flatCatalog, preferBarcode: isShopify, matchBySku: isN11, onlyExistingVariants: existingOnly && isShopify, updateExistingProducts: updateExistingProducts, overwriteLocalChanges: ProductImportMergePolicy.OverwriteLocalChanges(updateExistingProducts, singleLookup, existingOnly, isShopify || isHepsiburada || flatCatalog, fullScanRequested: fullScan && updateExistingProductsRequested), updatePrices: updatePrices, updateStock: updateStock, isHepsiburada: isHepsiburada, observedPlatformName: isPazarama ? "Pazarama" : isPttAvm ? "PttAVM" : null, createdAt: CatalogImportOrdering.RemoteCreatedAt(snapshot, catalogOrderStamp) ?? catalogOrderStamp.AddMilliseconds(-catalogOrderPosition++), allowNewProduct: !existingOnly, onlyAddExternalVariantIds: newOnlyAddIds, brandMappedByExternalId: ConnectionReferencePlan.SupportsBrands(connection.PlatformCode));
                    if (existingProductExternalIds?.Add(snapshot.ExternalProductId) == true) attemptAddedProductIds.Add(snapshot.ExternalProductId);
                    if (existingVariantExternalIds is not null)
                        foreach (var variant in snapshot.Variants)
                            if (existingVariantExternalIds.Add(Short(variant.ExternalVariantId, 256))) attemptAddedVariantIds.Add(Short(variant.ExternalVariantId, 256));
                    if (changed) attemptWrittenProductIds.Add(snapshot.ExternalProductId);
                    if (changed) run.ImportProcessedCount++;
                    else run.ImportSkippedCount++;
                    if (run.ImportSummaryMode == ImportSummaryMode.NewOnly)
                    {
                        if (changed && !productAlreadyLinked) run.NewProductCount++;
                        else if (changed) run.NewSizeCount += run.ProductImportedNewVariantCount;
                        else run.AlreadyInPanelCount++;
                    }
                    RecordProductRead(snapshot, !changed ? JobReadOutcomes.Unchanged : productAlreadyLinked ? JobReadOutcomes.Updated : JobReadOutcomes.New, run.ProductUnaddedSizeCount > 0 ? UnaddedSizesNote(run.ProductUnaddedSizeCount) : null);
                }
                // PTT AVM lists colour and size rows, not variants; its mapping writes the variant codes itself. A new-only or
                // focused price/stock run records the codes of the links it made alone: the links the panel had stay as
                // they were.
                if (!isPttAvm) await RecordVariantLinkCodes(tenantId, connectionId, snapshotGroups, cancellationToken, onlyAdded: run.ImportSummaryMode is ImportSummaryMode.NewOnly or ImportSummaryMode.FocusedPriceStock);
                await db.SaveChangesAsync(cancellationToken);
                // Model Kaydı Faz 2: the listing each model is on this marketplace, from the variant links just saved
                // (MarketplaceModelLinks.ModelListings: Shopify product, Trendyol productMainId, N11 main:, Pazarama group:, PTT AVM product).
                await MarketplaceModelLinks.RecordAsync(db, tenantId, connectionId, MarketplaceModelLinks.ModelListings(connection.PlatformCode, snapshotGroups), timeProvider.GetUtcNow(), cancellationToken);
                // The title, description and desi the marketplace gave for each variant these reads link on the connection
                // (every mode: a match-only, observe-only or new-sizes run reads them too), dated with the read, for the
                // "Platformlarda Güncelle" summary (MarketplaceKnownListingValueStore). Nothing of the panel card is written.
                await MarketplaceKnownListingValueStore.RecordImportAsync(db, tenantId, connectionId, connection.PlatformCode, snapshotGroups, () => MarketplaceStockReadAt(timeProvider.GetUtcNow()), cancellationToken);
                if (completesListingProfiles) await CompleteImportedListingProfiles(tenantId, connectionId, snapshotGroups, cancellationToken);
                // Trendyol's waiting and refused products show as such, with the reasons it gave (ImportedApprovalStates).
                if (connection.PlatformCode == "TRENDYOL") await RecordImportedApprovalStates(tenantId, connectionId, snapshotGroups, cancellationToken);
                await db.ProductImportStagingRecords
                    .Where(x => x.TenantId == tenantId && x.JobId == importJobId && x.ModelKey == modelKey && x.State == "STAGED")
                    .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.State, "COMPLETED").SetProperty(x => x.FinalizedAt, timeProvider.GetUtcNow()), cancellationToken);
                if (openImportFailures.Count > 0)
                {
                    var storedKeys = attemptWrittenProductIds
                        .Select(externalProductId => $"{importFailurePrefix}{Short(externalProductId, 256)}")
                        .Where(openImportFailures.Contains)
                        .ToArray();
                    if (storedKeys.Length > 0)
                    {
                        var resolvedAt = timeProvider.GetUtcNow();
                        await db.OperationalIssues
                            .Where(x => x.TenantId == tenantId && x.Code == "PRODUCT_IMPORT_FAILED" && x.Status != IssueStatus.Resolved && storedKeys.Contains(x.DedupeKey))
                            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Status, IssueStatus.Resolved).SetProperty(x => x.ResolvedAt, resolvedAt), cancellationToken);
                        openImportFailures.ExceptWith(storedKeys);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            // KATALOG-14 phase D5b review 3: a deadlock or serialization failure with another save of the same model (Excel,
            // Toplu Değer Ata) wrote nothing either: the model is read and written again, up to three attempts.
            catch (Exception conflict) when (conflict is DbUpdateConcurrencyException ? modelAttempt == 1 : SerializableWriteRetry.IsLockConflict(conflict) && modelAttempt < SerializableWriteRetry.MaxAttempts)
            {
                if (conflict is DbUpdateConcurrencyException versionConflict)
                    logger?.LogWarning(conflict, "Product import model {ModelKey} of job {JobId} hit a version conflict; retrying it once. Conflicting rows: {ConflictingRows}", modelKey, importJobId, ConcurrencyConflictRows(versionConflict));
                else
                    logger?.LogWarning(conflict, "Product import model {ModelKey} of job {JobId} met another save of the model (deadlock); retrying it (attempt {Attempt})", modelKey, importJobId, modelAttempt);
                db.ChangeTracker.Clear();
                categoryContexts.Clear();
                importedAttributeLibrary.Clear();
                (run.ImportProcessedCount, run.ImportSkippedCount, run.AlreadyInPanelCount, run.NewProductCount, run.NewSizeCount, run.UnaddedSizeCount, run.UnchangedVariantCount) = attemptCounters;
                foreach (var id in attemptAddedProductIds) existingProductExternalIds?.Remove(id);
                foreach (var id in attemptAddedVariantIds) existingVariantExternalIds?.Remove(id);
                goto retryModel;
            }
            catch (Exception exception)
            {
                var conflictRows = exception is DbUpdateConcurrencyException failedConflict ? ConcurrencyConflictRows(failedConflict) : null;
                if (conflictRows is not null)
                    logger?.LogWarning(exception, "Product import model {ModelKey} of job {JobId} hit a version conflict again on its retry; its products count as failed. Conflicting rows: {ConflictingRows}", modelKey, importJobId, conflictRows);
                db.ChangeTracker.Clear();
                categoryContexts.Clear();
                importedAttributeLibrary.Clear();
                run.ImportFailedCount += snapshotGroups.Count;
                run.FailedCount += snapshotGroups.Count;
                foreach (var snapshot in snapshotGroups) RecordProductRead(snapshot, JobReadOutcomes.Failed, Short(exception.Message, 300));
                foreach (var snapshot in snapshotGroups)
                {
                    try
                    {
                        await RecordProductImportFailure(tenantId, connectionId, snapshot, exception, cancellationToken, conflictRows);
                    }
                    catch (Exception issueException) when (issueException is not OperationCanceledException)
                    {
                        db.ChangeTracker.Clear();
                    }
                }
                var stagingError = conflictRows is null ? exception.Message : $"{exception.Message} Çakışan kayıtlar: {conflictRows}";
                await db.ProductImportStagingRecords
                    .Where(x => x.TenantId == tenantId && x.JobId == importJobId && x.ModelKey == modelKey && x.State == "STAGED")
                    .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.State, "FAILED").SetProperty(x => x.ErrorSummary, Short(stagingError, 2_000)), cancellationToken);
            }
            finally
            {
                run.CatalogStockReadAt = null;
                // An import reads no "N Renge Uygulandı": what its saves reported (a retried attempt included) is dropped.
                db.ModelMirrorReport.Clear();
                db.ModelOrderHint.Clear();
                db.ChangeTracker.Clear();
                categoryContexts.Clear();
                importedAttributeLibrary.Clear();
            }

            importedModelCount++;
            if (jobId is { } itemProgressJob)
            {
                var completedProducts = run.ImportProcessedCount + run.ImportSkippedCount + run.ImportFailedCount;
                var percent = totalProducts is { } total && total > 0
                    ? Math.Clamp((int)Math.Floor(completedProducts * 100d / total), 0, 99)
                    : (int?)null;
                await UpdateProductSyncProgressAsync(tenantId, itemProgressJob, receivedProducts, totalProducts, percent, ProductImportProgressLabel(totalProducts, receivedProducts, mappingOnly), cancellationToken);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        // The run's staging rows by state, before they are deleted below: a FAILED model keeps the PRODUCTS watermark
        // where it was and a run without a COMPLETED model failed as a whole (ProductImportScanPolicy).
        var stagingStates = await db.ProductImportStagingRecords.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.JobId == importJobId && (x.State == "FAILED" || x.State == "COMPLETED"))
            .GroupBy(x => x.State)
            .Select(group => new { State = group.Key, Rows = group.Count() })
            .ToListAsync(cancellationToken);
        var failedModels = stagingStates.Where(x => x.State == "FAILED").Sum(x => x.Rows);
        var completedModels = stagingStates.Where(x => x.State == "COMPLETED").Sum(x => x.Rows);
        // ISLEYICI-1 retry: the models this run could not store are read again one by one (ProductImportRetryPolicy); a
        // retry run reports on its own model. Saved here, so a run that failed as a whole still keeps its list.
        if (singleLookup && ReadBoolean(payloadJson, ProductImportRetryPolicy.PayloadFlag))
            await CompleteProductImportRetryAsync(tenantId, connectionId, connection.PlatformCode, importJobId, productLookup!, stored: failedModels == 0 && completedModels > 0, payloadJson, correlationId, cancellationToken);
        else if (!singleLookup && !mappingOnly && !optionsOnly && failedModels > 0)
            await NoteFailedModelsForRetryAsync(tenantId, connectionId, connection.PlatformCode, importJobId, payloadJson, correlationId, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        if (ProductImportScanPolicy.AllFailed(run.ImportFailedCount, completedModels))
        {
            if (jobId is { } failedJob)
                await UpdateProductSyncProgressAsync(tenantId, failedJob, receivedProducts, totalProducts, 100, ProductImportProgressLabel(totalProducts, receivedProducts, mappingOnly), cancellationToken);
            throw new JobProcessingException(JobExecutionResult.ManualReview("PRODUCT_IMPORT_ALL_FAILED", "Ürün çekimindeki kayıtların tamamı işlenemedi; çekim başarılı sayılmadı."));
        }
        if (!singleLookup)
        {
            var completedCursor = await Cursor(tenantId, connectionId, "PRODUCTS", cancellationToken);
            completedCursor.OpaqueCursor = null;
            // ISLEYICI-1 follow-up: a failed model holds the watermark for at most MaxHeldRuns runs in a row. The count
            // lives in a row of its own, made only once a run holds the watermark.
            var holdCursor = await db.SyncCursors.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == ProductImportScanPolicy.WatermarkHoldResource, cancellationToken);
            var completed = ProductImportScanPolicy.CompletedWatermark(importSession.CreatedAt, completedCursor.LastModifiedWatermark,
                scoped: newOnly || existingOnly || mappingOnly || optionsOnly, failedModels, ProductImportScanPolicy.HeldRuns(holdCursor?.OpaqueCursor));
            completedCursor.LastModifiedWatermark = completed.Watermark;
            completedCursor.Version++;
            var heldCursorValue = ProductImportScanPolicy.HeldRunsCursor(completed.HeldRuns);
            if (holdCursor is null && heldCursorValue is not null)
                holdCursor = await Cursor(tenantId, connectionId, ProductImportScanPolicy.WatermarkHoldResource, cancellationToken);
            if (holdCursor is not null && holdCursor.OpaqueCursor != heldCursorValue)
            {
                holdCursor.OpaqueCursor = heldCursorValue;
                holdCursor.Version++;
            }
            var releaseKey = $"product-sync-watermark-released:{connectionId}";
            if (completed.Released)
                await RecordIssue(tenantId, releaseKey, "PRODUCT_IMPORT_FAILED_LEFT_BEHIND",
                    $"Bazı ürünler art arda {ProductImportScanPolicy.MaxHeldRuns} ürün çekiminde panele alınamadı; değişen ürün taraması bu ürünleri beklemeden ilerledi. Alınamayan ürünler Operasyon Sorunları'ndaki ürün çekim uyarılarında duruyor; Tam Katalog Taraması ya da tekil ürün çekimi onları yeniden okur.",
                    cancellationToken);
            // A whole catalog read without a failed model has read the products left behind again.
            else if (failedModels == 0 && effectiveFullScan && !(newOnly || existingOnly || mappingOnly || optionsOnly))
                await ResolveIssue(tenantId, releaseKey, cancellationToken);
        }
        await db.SaveChangesAsync(cancellationToken);
        await db.ProductImportSessions
            .Where(x => x.TenantId == tenantId && x.JobId == importJobId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Phase, "COMPLETED").SetProperty(x => x.NextCursor, (string?)null).SetProperty(x => x.CompletedAt, timeProvider.GetUtcNow()).SetProperty(x => x.UpdatedAt, timeProvider.GetUtcNow()), cancellationToken);
        await db.ProductImportStagingRecords
            .Where(x => x.TenantId == tenantId && x.JobId == importJobId)
            .ExecuteDeleteAsync(cancellationToken);
        if (jobId is { } completedJob)
            await UpdateProductSyncProgressAsync(tenantId, completedJob, receivedProducts, null, 100, ProductImportProgressLabel(totalProducts, receivedProducts, mappingOnly), cancellationToken, keepExistingTotal: true);
        return true;
    }

    // The listing profiles of the products this batch matched (ImportedListingProfiles). The batch is already saved; a
    // failure here leaves the import as it is (the next run completes the profile; an update never makes one).
    internal async Task CompleteImportedListingProfiles(Guid tenantId, Guid connectionId, IReadOnlyList<RemoteCatalogProduct> snapshots, CancellationToken cancellationToken)
    {
        var codes = snapshots.SelectMany(snapshot => snapshot.Variants)
            .SelectMany(variant => new[] { Short(variant.ExternalVariantId, 256), string.IsNullOrWhiteSpace(variant.Barcode) ? null : Short(variant.Barcode.Trim(), 160) })
            .Where(code => !string.IsNullOrWhiteSpace(code)).Select(code => code!).ToHashSet(StringComparer.Ordinal);
        try
        {
            var productIds = await ImportedListingProfiles.ProductIdsForCodesAsync(db, tenantId, connectionId, codes, cancellationToken);
            await ImportedListingProfiles.EnsureAsync(db, tenantId, connectionId, productIds, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
        }
    }

    private async Task RecordImportedApprovalStates(Guid tenantId, Guid connectionId, IReadOnlyList<RemoteCatalogProduct> snapshots, CancellationToken cancellationToken)
    {
        try
        {
            await ImportedApprovalStates.ApplyAsync(db, tenantId, connectionId, snapshots, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
        }
    }

    private Task<int> UpdateProductSyncProgressAsync(Guid tenantId, Guid jobId, int current, int? total, int? percent, string label, CancellationToken cancellationToken, bool keepExistingTotal = false)
    {
        var query = db.IntegrationJobs.Where(x => x.TenantId == tenantId && x.Id == jobId && (x.JobType == MarketplaceJobTypes.ProductSync || x.JobType == MarketplaceJobTypes.ShopifyProductSync || x.JobType == MarketplaceJobTypes.HepsiburadaProductSync || x.JobType == MarketplaceJobTypes.CatalogStructureImport));
        return keepExistingTotal
            ? query.ExecuteUpdateAsync(setters => setters.SetProperty(x => x.ProgressCurrent, current).SetProperty(x => x.ProgressPercent, percent).SetProperty(x => x.ProgressLabel, label).SetProperty(x => x.ProgressReceived, current).SetProperty(x => x.ProgressProcessed, run.ImportProcessedCount).SetProperty(x => x.ProgressSkipped, run.ImportSkippedCount).SetProperty(x => x.ProgressFailed, run.ImportFailedCount), cancellationToken)
            : query.ExecuteUpdateAsync(setters => setters.SetProperty(x => x.ProgressCurrent, current).SetProperty(x => x.ProgressTotal, total).SetProperty(x => x.ProgressPercent, percent).SetProperty(x => x.ProgressLabel, label).SetProperty(x => x.ProgressReceived, current).SetProperty(x => x.ProgressProcessed, run.ImportProcessedCount).SetProperty(x => x.ProgressSkipped, run.ImportSkippedCount).SetProperty(x => x.ProgressFailed, run.ImportFailedCount), cancellationToken);
    }

    // Counts only: the internal stage (page number, "referanslar hazırlanıyor", model groups) is not shown to the user.
    // Grouped with dots (1.018), whatever culture the container runs in.
    internal static string TrCount(int value) => value.ToString("N0", System.Globalization.CultureInfo.InvariantCulture).Replace(',', '.');

    private string ProductImportProgressLabel(int? total, int received, bool mappingOnly)
    {
        var failedPart = $" · Hatalı {TrCount(run.ImportFailedCount)}";
        switch (run.ImportSummaryMode)
        {
            case ImportSummaryMode.FocusedPriceStock:
                return $"Alınan {TrCount(received)} · Fiyatı değişen {TrCount(run.PriceChangedVariantIds.Count)} varyant · Stoğu değişen {TrCount(run.StockChangedVariantIds.Count)} varyant · Değişmeyen {TrCount(run.UnchangedVariantCount)} · Yeni beden {TrCount(run.UnaddedSizeCount)}{failedPart}";
            case ImportSummaryMode.NewOnly:
                return $"Alınan {TrCount(received)} · Yeni ürün {TrCount(run.NewProductCount)} · Yeni beden {TrCount(run.NewSizeCount)} · Zaten panelde {TrCount(run.AlreadyInPanelCount)}{failedPart}";
            case ImportSummaryMode.Mapping:
                // "Eşlenen" also tells the panel's job card to name its counters for mapping.
                return $"Alınan {TrCount(received)} · Eşlenen {TrCount(run.MappedCount)} · Zaten eşli {TrCount(run.AlreadyMappedCount)} · Panelde karşılığı yok {TrCount(run.NoCounterpartCount)} · Çakışma {TrCount(run.MappingConflictCount)}{failedPart}";
        }
        var processedLabel = mappingOnly ? "Eşlenen" : "İşlenen";
        var skippedLabel = mappingOnly ? "Eşleşmeyen" : "Atlanan";
        var unaddedPart = run.UnaddedSizeCount > 0 ? $" · Eklenmeyen yeni beden {TrCount(run.UnaddedSizeCount)}" : "";
        return $"Alınan {TrCount(received)} · {processedLabel} {TrCount(run.ImportProcessedCount)} · {skippedLabel} {TrCount(run.ImportSkippedCount)} · Hatalı {TrCount(run.ImportFailedCount)}{unaddedPart}";
    }

    internal static string UnaddedSizesNote(int count) =>
        $"{count} yeni beden var; eklemek için Yeni Ürünleri Çek ya da Mevcut Ürün Bilgilerini Güncelle ile çekin.";

    private async Task RecordProductImportFailure(Guid tenantId, Guid connectionId, RemoteCatalogProduct snapshot, Exception exception, CancellationToken cancellationToken, string? conflictRows = null)
    {
        var externalProductId = Short(snapshot.ExternalProductId, 256);
        var issueIdentity = string.IsNullOrWhiteSpace(externalProductId)
            ? $"missing:{Hash(JsonSerializer.Serialize(snapshot))[..16]}"
            : externalProductId;
        var root = exception.GetBaseException();
        var constraint = root is PostgresException postgres
            ? $"sqlState={postgres.SqlState}; constraint={postgres.ConstraintName ?? "—"}; table={postgres.TableName ?? "—"}; detail={postgres.Detail ?? postgres.MessageText}"
            : $"exception={root.GetType().Name}; detail={root.Message}{(conflictRows is null ? "" : $" rows={conflictRows}")}";
        var summary = Short($"Pazar yeri ürünü '{(string.IsNullOrWhiteSpace(externalProductId) ? "kimlik yok" : externalProductId)}' panele alınamadı; sonraki ürünle devam edildi. Veritabanı ayrıntısı: {constraint}", 2_000);
        await RecordIssue(tenantId, $"product-sync-import:{connectionId}:{issueIdentity}", "PRODUCT_IMPORT_FAILED", summary, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    // The rows a version conflict was about, read from the exception's entries before the change tracker is cleared:
    // table, primary key, state and the version the save expected, e.g. "catalog.products Id=… Modified Version=3".
    internal static string ConcurrencyConflictRows(DbUpdateConcurrencyException conflict)
    {
        var rows = conflict.Entries.Take(5).Select(entry =>
        {
            var type = entry.Metadata;
            var table = type.GetTableName() is { } name ? (type.GetSchema() is { } schema ? $"{schema}.{name}" : name) : type.ClrType.Name;
            var key = type.FindPrimaryKey()?.Properties.Select(property => $"{property.Name}={entry.Property(property.Name).CurrentValue}") ?? [];
            var tokens = type.GetProperties().Where(property => property.IsConcurrencyToken).Select(property => $"{property.Name}={entry.Property(property.Name).OriginalValue}");
            return string.Join(" ", new[] { table }.Concat(key).Append(entry.State.ToString()).Concat(tokens));
        }).ToList();
        if (rows.Count == 0) return "kayıt bilgisi yok";
        var more = conflict.Entries.Count - rows.Count;
        return string.Join("; ", rows) + (more > 0 ? $"; +{more}" : "");
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
}
