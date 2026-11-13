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
        var rows = await (
                from variant in db.ProductVariants.AsNoTracking()
                join product in db.Products.AsNoTracking() on variant.ProductId equals product.Id
                where variant.TenantId == tenantId && product.TenantId == tenantId && variant.ModelCode != null && variant.ModelCode != ""
                    && variant.ProductId != ownProductId
                    && !db.MarketplaceProductLinks.Any(link => link.TenantId == tenantId && link.ConnectionId == connectionId && link.ProductId == variant.ProductId)
                    && !db.MarketplaceVariantLinks.Any(link => link.TenantId == tenantId && link.ConnectionId == connectionId
                        && db.ProductVariants.Any(sibling => sibling.TenantId == tenantId && sibling.Id == link.VariantId && sibling.ProductId == variant.ProductId))
                select new { variant.ProductId, product.Title, variant.ModelCode, product.CreatedAt })
            .Distinct()
            .ToListAsync(cancellationToken);
        return ModelCodeUniquenessPolicy.FirstHolder(keys, rows
            .OrderBy(row => row.CreatedAt).ThenBy(row => row.ProductId)
            .Select(row => new ModelCodeUniquenessPolicy.Holder(row.ProductId, row.Title, row.ModelCode!)));
    }

    public static async Task<ModelCodeUniquenessPolicy.Holder?> FindOtherAsync(AppDbContext db, Guid tenantId, IEnumerable<string?> modelCodes, IReadOnlyCollection<Guid> ownProductIds, CancellationToken cancellationToken)
    {
        var keys = ModelCodeUniquenessPolicy.Keys(modelCodes);
        if (keys.Count == 0) return null;
        var own = ownProductIds.ToArray();
        var rows = await (
                from variant in db.ProductVariants.AsNoTracking()
                join product in db.Products.AsNoTracking() on variant.ProductId equals product.Id
                where variant.TenantId == tenantId && product.TenantId == tenantId && variant.ModelCode != null && variant.ModelCode != ""
                    && !own.Contains(variant.ProductId)
                select new { variant.ProductId, product.Title, variant.ModelCode, product.CreatedAt })
            .Distinct()
            .ToListAsync(cancellationToken);
        return ModelCodeUniquenessPolicy.FirstHolder(keys, rows
            .OrderBy(row => row.CreatedAt).ThenBy(row => row.ProductId)
            .Select(row => new ModelCodeUniquenessPolicy.Holder(row.ProductId, row.Title, row.ModelCode!)));
    }
}
