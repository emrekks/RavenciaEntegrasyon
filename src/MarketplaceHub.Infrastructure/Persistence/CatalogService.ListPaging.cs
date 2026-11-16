using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Npgsql;

namespace MarketplaceHub.Infrastructure.Persistence;

// KATALOG-10: the product list (newest first, no stock / platform-state filter, no price or stock sort) pages its models in
// SQL by the stored model link: a model is COALESCE(products.FamilyId, products.Id), its row the newest card (CreatedAt,
// Id), and only the page's cards, variants and stock are read. The in-memory grouping (ProductFamilyKey: brand + the
// first variant's model-code key, else the card alone) stays the rule: the SQL path runs only while the tenant's links
// give exactly that grouping (FamilyIdMatchesModelCodeRuleAsync), so a link that is missing, late or ambiguous (KENE01's
// two brands, a card whose link failed) sends the request down today's in-memory path instead of showing other rows.
public sealed partial class CatalogService
{
    // True when grouping the tenant's cards by COALESCE(FamilyId, Id) gives the same groups as the model-code rule: every
    // code is one SqlKey computes like CatalogKeyNormalizer.Key and the two partitions are equal (as many groups each as
    // pairs of both). Equal for every card holds for every filtered subset of them.
    internal async Task<bool> FamilyIdMatchesModelCodeRuleAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var code = "c.\"ModelCode\"";
        var sql = $"""
            WITH first_code AS (
                SELECT DISTINCT ON (v."ProductId") v."ProductId", v."ModelCode"
                FROM catalog.product_variants v
                WHERE v."TenantId" = @tenant AND v."ModelCode" IS NOT NULL AND v."ModelCode" ~ '[^[:space:]]'
                ORDER BY v."ProductId", v."SortOrder", v."Id"
            ), cards AS (
                SELECT p."Id", p."FamilyId", p."BrandId", c."ModelCode",
                    CASE WHEN c."ModelCode" IS NULL OR {ProductModelLinks.SqlKey(code)} = '' THEN 'p:' || p."Id"::text
                         ELSE 'k:' || coalesce(p."BrandId"::text, '-') || ':' || {ProductModelLinks.SqlKey(code)} END AS rule_key,
                    coalesce(p."FamilyId", p."Id") AS link_key,
                    c."ModelCode" IS NULL OR {ProductModelLinks.SqlKeySafe(code)} AS safe
                FROM catalog.products p LEFT JOIN first_code c ON c."ProductId" = p."Id"
                WHERE p."TenantId" = @tenant
            )
            SELECT coalesce(bool_and(safe), true)
                AND count(DISTINCT rule_key) = count(DISTINCT link_key)
                AND count(DISTINCT rule_key) = count(DISTINCT (rule_key, link_key)) AS "Value"
            FROM cards
            """;
        return await db.Database.SqlQueryRaw<bool>(sql, new NpgsqlParameter("tenant", tenantId)).SingleAsync(cancellationToken);
    }

    // KATALOG-10: the rule check reads every card and variant of the tenant; the list asks it on every request. Its answer
    // is kept per tenant as long as the family count (15 s), and dropped at once when a card or variant of the tenant is
    // saved in this process or the model links are written (CatalogListGenerations). A write in the other process (the
    // worker) is seen within the 15 s, as the list's count already is.
    internal static readonly TimeSpan FamilyRuleCacheLifetime = TimeSpan.FromSeconds(15);

    internal async Task<bool> FamilyIdMatchesModelCodeRuleCachedAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var key = $"catalog:family-rule:v1:{tenantId:N}:{CatalogListGenerations.Of(tenantId)}";
        if (countCache.Get(key) is bool cached) return cached;
        var generation = CatalogListGenerations.Of(tenantId);
        var matches = await FamilyIdMatchesModelCodeRuleAsync(tenantId, cancellationToken);
        // A save during the check moved the generation: the answer is not kept.
        if (generation == CatalogListGenerations.Of(tenantId))
            countCache.Set(key, matches, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = FamilyRuleCacheLifetime, Size = 1 });
        return matches;
    }

    // The cards of the filtered catalog whose model is in the platform state on the marketplace (or on any, "ALL"), by the
    // narrow card and variant rows: the families are grouped by the model-code rule, as the list groups them.
    private async Task<HashSet<Guid>> PlatformStateProductIdsAsync(Guid tenantId, IReadOnlyList<Product> allProducts, IReadOnlyList<ProductVariant> allVariants, string platformFilterCode, string platformFilterState, CancellationToken cancellationToken)
    {
        var allProductIds = allProducts.Select(x => x.Id).ToArray();
        var variantsByProductForPlatform = allVariants
            .GroupBy(variant => variant.ProductId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<ProductVariant>)group.ToList());
        var familyGroups = allProducts
            .GroupBy(product => ProductFamilyKey(product, variantsByProductForPlatform.GetValueOrDefault(product.Id) ?? []), StringComparer.Ordinal)
            .ToList();
        var matchingFamilyKeys = new HashSet<string>(StringComparer.Ordinal);
        // "ALL" (Tüm Platformlar › a state, owner 14 Nov 2026): the family is in that state on at least one marketplace.
        foreach (var code in platformFilterCode == AllPlatformsFilterCode ? ProductFilterPlatformCodes : [platformFilterCode])
        {
            var (matchedVariantIds, missingVariantIds, offSaleVariantIds) = await PlatformVariantStatesAsync(tenantId, code, allProducts, allVariants, cancellationToken);
            // Hatalı / Onay Bekliyor: a card of the family whose publication there the marketplace refused or failed
            // (the products list's red mark), or still reviews (ProductListingHealth.ErrorStatuses / WaitingStatuses).
            var flaggedProductIds = platformFilterState is ProductListingHealth.Error or ProductListingHealth.Waiting
                ? await PlatformFlaggedProductIdsAsync(tenantId, code, allProductIds, platformFilterState == ProductListingHealth.Error ? ProductListingHealth.ErrorStatuses : ProductListingHealth.WaitingStatuses, cancellationToken)
                : null;
            foreach (var group in familyGroups.Where(group => !matchingFamilyKeys.Contains(group.Key)))
            {
                var matches = flaggedProductIds is not null
                    ? group.Any(product => flaggedProductIds.Contains(product.Id))
                    : PlatformFilterMatchesFamily(
                        group.SelectMany(product => variantsByProductForPlatform.GetValueOrDefault(product.Id) ?? []).Select(variant => variant.Id),
                        matchedVariantIds,
                        missingVariantIds,
                        offSaleVariantIds,
                        platformFilterState);
                if (matches) matchingFamilyKeys.Add(group.Key);
            }
        }
        return allProducts
            .Where(product => matchingFamilyKeys.Contains(ProductFamilyKey(product, variantsByProductForPlatform.GetValueOrDefault(product.Id) ?? [])))
            .Select(product => product.Id)
            .ToHashSet();
    }

    // The stock filter as the in-memory list applies it (never a draft): OUT and GAP decided in the database from the MAIN
    // stock of each card and variant; LOW and OK need the colour groups (ProductStockPolicy.IsLowStock), decided on the
    // narrow variant rows of the cards that have stock.
    private async Task<IQueryable<Product>> ApplyStockFilterInSqlAsync(Guid tenantId, IQueryable<Product> query, string stock, CancellationToken cancellationToken)
    {
        var mode = stock.Trim().ToUpperInvariant();
        var nonDraft = query.Where(product => product.Status != ProductStatus.Draft);
        var main = db.InventoryItems.Where(item => item.TenantId == tenantId && item.LocationCode == "MAIN");
        var variants = db.ProductVariants.Where(variant => variant.TenantId == tenantId);
        var withTotal = nonDraft.Select(product => new { Product = product, Total = main.Where(item => variants.Any(variant => variant.Id == item.VariantId && variant.ProductId == product.Id)).Sum(item => (decimal?)item.OnHand) ?? 0m });
        switch (mode)
        {
            case "OUT":
                return withTotal.Where(row => row.Total <= 0m).Select(row => row.Product);
            case "GAP":
                return withTotal.Where(row => row.Total > 0m
                        && variants.Any(variant => variant.ProductId == row.Product.Id && (main.Where(item => item.VariantId == variant.Id).Sum(item => (decimal?)item.OnHand) ?? 0m) <= 0m))
                    .Select(row => row.Product);
            case "LOW":
            case "OK":
                var stocked = withTotal.Where(row => row.Total > 0m).Select(row => row.Product.Id);
                var rows = await variants.Where(variant => stocked.Contains(variant.ProductId))
                    .Select(variant => new { variant.Id, variant.ProductId, variant.OptionSignature, Stock = main.Where(item => item.VariantId == variant.Id).Sum(item => (decimal?)item.OnHand) })
                    .ToListAsync(cancellationToken);
                var stockByVariant = rows.Where(row => row.Stock is not null).ToDictionary(row => row.Id, row => row.Stock!.Value);
                var low = rows.GroupBy(row => row.ProductId)
                    .Where(group => ProductStockPolicy.IsLowStock(group.Select(row => new ProductVariant { Id = row.Id, TenantId = tenantId, ProductId = row.ProductId, Sku = "", SkuNormalized = "", OptionSignature = row.OptionSignature }), stockByVariant))
                    .Select(group => group.Key).ToArray();
                return mode == "LOW"
                    ? nonDraft.Where(product => stocked.Contains(product.Id) && low.Contains(product.Id))
                    : nonDraft.Where(product => stocked.Contains(product.Id) && !low.Contains(product.Id));
            default:
                return nonDraft;
        }
    }

    // Price and stock sorts: one metric per model, worked out in the database exactly as ListSortedProductFamiliesAsync works
    // it out (price: the lowest effective sale price of the model's variants, each variant's first offer active ones first;
    // stock: the MAIN stock of the variants that have an item, null without one), then the models' narrow rows are ordered
    // by the same comparer and one page of them is read.
    private async Task<PageResult<ProductView>> ListSortedProductFamiliesInSqlAsync(Guid tenantId, IQueryable<Product> query, ProductCatalogSort catalogSort, ProductCatalogSortKey? afterSortKey, int limit, string countKey, int? cachedCount, CancellationToken cancellationToken)
    {
        var heads = await query.Where(product => !query.Any(other => (other.FamilyId ?? other.Id) == (product.FamilyId ?? product.Id)
                && (other.CreatedAt > product.CreatedAt || other.CreatedAt == product.CreatedAt && other.Id.CompareTo(product.Id) > 0)))
            .Select(product => new FamilyHead(product.FamilyId ?? product.Id, product.Id, product.CreatedAt))
            .ToListAsync(cancellationToken);
        var totalCount = cachedCount ?? heads.Count;
        if (cachedCount is null)
            countCache.Set(countKey, totalCount, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(15), Size = 1 });
        Dictionary<Guid, decimal?> metrics;
        if (catalogSort.ByPrice)
        {
            var prices = from product in query
                         from variant in db.ProductVariants.Where(variant => variant.TenantId == tenantId && variant.ProductId == product.Id)
                         let offer = db.ChannelOffers.Where(offer => offer.TenantId == tenantId && offer.VariantId == variant.Id)
                             .OrderByDescending(offer => offer.Status == "ACTIVE").ThenBy(offer => offer.Id).Select(offer => (decimal?)offer.SalePrice).FirstOrDefault()
                         select new
                         {
                             Key = product.FamilyId ?? product.Id,
                             Price = offer > 0m ? offer : variant.DefaultSalePrice > 0m ? variant.DefaultSalePrice : product.DefaultSalePrice > 0m ? product.DefaultSalePrice : null
                         };
            metrics = await prices.GroupBy(row => row.Key).Select(group => new { group.Key, Metric = group.Min(row => row.Price) }).ToDictionaryAsync(row => row.Key, row => row.Metric, cancellationToken);
        }
        else
        {
            var stocks = from product in query
                         from variant in db.ProductVariants.Where(variant => variant.TenantId == tenantId && variant.ProductId == product.Id)
                         from item in db.InventoryItems.Where(item => item.TenantId == tenantId && item.LocationCode == "MAIN" && item.VariantId == variant.Id)
                         select new { Key = product.FamilyId ?? product.Id, item.OnHand };
            metrics = await stocks.GroupBy(row => row.Key).Select(group => new { group.Key, Metric = (decimal?)group.Sum(row => row.OnHand) }).ToDictionaryAsync(row => row.Key, row => row.Metric, cancellationToken);
        }
        var ordered = heads
            .Select(head => (Head: head, Key: new ProductCatalogSortKey(metrics.GetValueOrDefault(head.Key), head.CreatedAt, head.Id)))
            .OrderBy(item => item.Key, catalogSort)
            .ToList();
        var pageItems = ordered
            .Where(item => afterSortKey is not { } afterKey || catalogSort.Compare(item.Key, afterKey) > 0)
            .Take(limit)
            .ToList();
        var keys = pageItems.Select(item => item.Head.Key).ToArray();
        var members = await query.Where(product => keys.Contains(product.FamilyId ?? product.Id))
            .Select(product => new { product.Id, Key = product.FamilyId ?? product.Id, product.CreatedAt })
            .ToListAsync(cancellationToken);
        var order = keys.Select((key, index) => (key, index)).ToDictionary(item => item.key, item => item.index);
        var pageProductIds = members.OrderBy(member => order[member.Key]).ThenByDescending(member => member.CreatedAt).ThenByDescending(member => member.Id).Select(member => member.Id).ToArray();
        var fullProducts = await db.Products.AsNoTracking().Where(x => x.TenantId == tenantId && pageProductIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, cancellationToken);
        var products = pageProductIds.Where(fullProducts.ContainsKey).Select(productId => fullProducts[productId]).ToList();
        var variants = await db.ProductVariants.AsNoTracking().Where(x => x.TenantId == tenantId && pageProductIds.Contains(x.ProductId)).ToListAsync(cancellationToken);
        var views = await WithFamilyImagesAsync(tenantId, await BuildProductViewsAsync(tenantId, products, variants, cancellationToken, listShape: true), cancellationToken);
        var hasMore = pageItems.Count > 0 && catalogSort.Compare(ordered[^1].Key, pageItems[^1].Key) > 0;
        return new(views, hasMore ? cursors.EncodeProductSort(catalogSort.Value, pageItems[^1].Key) : null, hasMore, totalCount);
    }

    private sealed record FamilyHead(Guid Key, Guid Id, DateTimeOffset CreatedAt);

    // The page of models after the cursor, read in SQL: a model's head is its newest visible card (no visible card of the
    // same model is newer, or as new with a greater id), the models in their heads' order (CreatedAt, Id descending).
    private async Task<PageResult<ProductView>> ListProductFamiliesInSqlAsync(Guid tenantId, IQueryable<Product> query, DateTimeOffset afterCreatedAtAnyOffset, Guid afterId, int limit, string countKey, int? cachedCount, CancellationToken cancellationToken)
    {
        var afterCreatedAt = afterCreatedAtAnyOffset.ToUniversalTime();
        var heads = query.Where(product => !query.Any(other => (other.FamilyId ?? other.Id) == (product.FamilyId ?? product.Id)
            && (other.CreatedAt > product.CreatedAt || other.CreatedAt == product.CreatedAt && other.Id.CompareTo(product.Id) > 0)));
        var totalCount = cachedCount ?? await heads.CountAsync(cancellationToken);
        if (cachedCount is null)
            countCache.Set(countKey, totalCount, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(15), Size = 1 });
        var page = await heads
            .Where(product => afterId == Guid.Empty || product.CreatedAt < afterCreatedAt || product.CreatedAt == afterCreatedAt && product.Id.CompareTo(afterId) < 0)
            .OrderByDescending(product => product.CreatedAt).ThenByDescending(product => product.Id)
            .Select(product => new FamilyHead(product.FamilyId ?? product.Id, product.Id, product.CreatedAt))
            .Take(limit + 1)
            .ToListAsync(cancellationToken);
        var hasMore = page.Count > limit;
        if (hasMore) page.RemoveAt(page.Count - 1);
        var keys = page.Select(head => head.Key).ToArray();
        var members = await query.Where(product => keys.Contains(product.FamilyId ?? product.Id))
            .Select(product => new { product.Id, Key = product.FamilyId ?? product.Id, product.CreatedAt })
            .ToListAsync(cancellationToken);
        // The cards model by model, each model's cards newest first (the in-memory list's order).
        var order = keys.Select((key, index) => (key, index)).ToDictionary(item => item.key, item => item.index);
        var pageProductIds = members.OrderBy(member => order[member.Key]).ThenByDescending(member => member.CreatedAt).ThenByDescending(member => member.Id).Select(member => member.Id).ToArray();
        var fullProducts = await db.Products.AsNoTracking().Where(x => x.TenantId == tenantId && pageProductIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, cancellationToken);
        var products = pageProductIds.Where(fullProducts.ContainsKey).Select(productId => fullProducts[productId]).ToList();
        var variants = await db.ProductVariants.AsNoTracking().Where(x => x.TenantId == tenantId && pageProductIds.Contains(x.ProductId)).ToListAsync(cancellationToken);
        var views = await WithFamilyImagesAsync(tenantId, await BuildProductViewsAsync(tenantId, products, variants, cancellationToken, listShape: true), cancellationToken);
        var last = page.LastOrDefault();
        return new(views, hasMore ? cursors.EncodeProduct(last!.CreatedAt, last.Id) : null, hasMore, totalCount);
    }
}
