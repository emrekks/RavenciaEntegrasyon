using MarketplaceHub.Application;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

// The tenant's card that already holds a model code (ModelCodeUniquenessPolicy). The database narrows to the variants
// with a model code; the key is compared in memory, as the database upper case differs from the invariant one outside ASCII.
public static class ModelCodeOwnership
{
    public static Task<ModelCodeUniquenessPolicy.Holder?> FindAsync(AppDbContext db, Guid tenantId, IEnumerable<string?> modelCodes, CancellationToken cancellationToken) =>
        FindOtherAsync(db, tenantId, modelCodes, [], cancellationToken);

    // The first card outside `ownProductIds` that holds one of the model codes. Scan C-9 (13 Nov 2026): a model code an
    // edit or an Excel upload changed to the code of another product joined that product's family, and Shopify and
    // PTT AVM send a family as one product (a shirt and trousers became colours of one product). The cards being saved
    // are left out, so a family renamed as a whole is not refused for its own rows.
    // C-9, imports: the first card outside `ownProductId` that holds the model code and has no product or variant link
    // on the connection, i.e. a card the marketplace does not know as part of its model. A marketplace import changing
    // a variant's code to such a card's code would join two unrelated products; cards of the same marketplace model
    // (linked on this connection) are its family and are not counted.
    public static async Task<ModelCodeUniquenessPolicy.Holder?> FindUnlinkedHolderAsync(AppDbContext db, Guid tenantId, Guid connectionId, string modelCode, Guid ownProductId, CancellationToken cancellationToken)
    {
        var keys = ModelCodeUniquenessPolicy.Keys([modelCode]);
        if (keys.Count == 0) return null;
        // An import calls this for every code it changes: the database first narrows to the codes holding the key's
        // letters and digits in order (separators and case aside), the key decides in memory, and only the few cards
        // found are checked for links on the connection.
        // A letter outside ASCII (ı, İ, ş...) matches any one character: the database's case folding may not pair it
        // with the key's spelling.
        var pattern = "%" + string.Join("%", modelCode.Where(char.IsLetterOrDigit).Select(ch => char.IsAscii(ch) ? ch.ToString() : "_")) + "%";
        var rows = (await (
                from variant in db.ProductVariants.AsNoTracking()
                join product in db.Products.AsNoTracking() on variant.ProductId equals product.Id
                where variant.TenantId == tenantId && product.TenantId == tenantId && variant.ModelCode != null && variant.ModelCode != ""
                    && variant.ProductId != ownProductId && EF.Functions.ILike(variant.ModelCode, pattern)
                select new { variant.ProductId, product.Title, variant.ModelCode, product.CreatedAt })
            .Distinct()
            .ToListAsync(cancellationToken))
            .Where(row => ProductFamilyKeyPolicy.ModelCodeKey(row.ModelCode) is { } key && keys.Contains(key, StringComparer.Ordinal))
            .ToList();
        if (rows.Count == 0) return null;
        var holderIds = rows.Select(row => row.ProductId).Distinct().ToArray();
        var linked = (await db.MarketplaceProductLinks.AsNoTracking()
                .Where(link => link.TenantId == tenantId && link.ConnectionId == connectionId && holderIds.Contains(link.ProductId))
                .Select(link => link.ProductId)
                .ToListAsync(cancellationToken))
            .Concat(await (from link in db.MarketplaceVariantLinks.AsNoTracking()
                           join variant in db.ProductVariants.AsNoTracking() on link.VariantId equals variant.Id
                           where link.TenantId == tenantId && link.ConnectionId == connectionId && variant.TenantId == tenantId && holderIds.Contains(variant.ProductId)
                           select variant.ProductId).ToListAsync(cancellationToken))
            .ToHashSet();
        return rows.Where(row => !linked.Contains(row.ProductId))
            .OrderBy(row => row.CreatedAt).ThenBy(row => row.ProductId)
            .Select(row => new ModelCodeUniquenessPolicy.Holder(row.ProductId, row.Title, row.ModelCode!))
            .FirstOrDefault();
    }

    // KATALOG-8: the cards that may hold one of the model-code keys, narrowed in the database: a code of ASCII and Turkish
    // letters is keyed there exactly (ProductModelLinks.SqlKey), any other code is kept for the in-memory key check. The
    // callers compare the key in memory as before; only the catalog's other codes are no longer read into memory.
    internal static IQueryable<Guid> ProductIdsWithKeys(AppDbContext db, Guid tenantId, IReadOnlyCollection<string> keys)
    {
        // Only constant SQL is put together here (the key expressions); the tenant and keys are parameters.
        const string code = "v.\"ModelCode\"";
        var sql = $"""
            SELECT v."ProductId" AS "Value" FROM catalog.product_variants v
            WHERE v."TenantId" = @key_tenant AND v."ModelCode" IS NOT NULL AND v."ModelCode" ~ '[^[:space:]]'
              AND (NOT {ProductModelLinks.SqlKeySafe(code)} OR {ProductModelLinks.SqlKey(code)} = ANY(@key_keys))
            """;
        return db.Database.SqlQueryRaw<Guid>(sql, new Npgsql.NpgsqlParameter("key_tenant", tenantId), new Npgsql.NpgsqlParameter("key_keys", keys.ToArray()));
    }

    public static async Task<ModelCodeUniquenessPolicy.Holder?> FindOtherAsync(AppDbContext db, Guid tenantId, IEnumerable<string?> modelCodes, IReadOnlyCollection<Guid> ownProductIds, CancellationToken cancellationToken)
    {
        var keys = ModelCodeUniquenessPolicy.Keys(modelCodes);
        if (keys.Count == 0) return null;
        var own = ownProductIds.ToArray();
        var candidates = ProductIdsWithKeys(db, tenantId, keys);
        var rows = await (
                from variant in db.ProductVariants.AsNoTracking()
                join product in db.Products.AsNoTracking() on variant.ProductId equals product.Id
                where variant.TenantId == tenantId && product.TenantId == tenantId && variant.ModelCode != null && variant.ModelCode != ""
                    && !own.Contains(variant.ProductId) && candidates.Contains(variant.ProductId)
                select new { variant.ProductId, product.Title, variant.ModelCode, product.CreatedAt })
            .Distinct()
            .ToListAsync(cancellationToken);
        return ModelCodeUniquenessPolicy.FirstHolder(keys, rows
            .OrderBy(row => row.CreatedAt).ThenBy(row => row.ProductId)
            .Select(row => new ModelCodeUniquenessPolicy.Holder(row.ProductId, row.Title, row.ModelCode!)));
    }
}
