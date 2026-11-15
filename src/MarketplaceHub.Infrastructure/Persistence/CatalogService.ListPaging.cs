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
