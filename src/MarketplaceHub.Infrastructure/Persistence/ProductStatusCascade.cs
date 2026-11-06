using MarketplaceHub.Domain;

namespace MarketplaceHub.Infrastructure.Persistence;

// K5 (owner decision 2026-11-06): a product-level status change (bulk Arşivle / Aktifleştir, the editor's Durum) moves the
// product's variants with it, except a variant archived on its own before (the marketplace removed it, or why is not
// known): reopening the product never reopens that one. A variant the product archives remembers it (ArchivedWithProduct),
// so the next activation reopens exactly those.
internal static class ProductStatusCascade
{
    public static bool Moves(ProductVariant variant, ProductStatus target) =>
        variant.Status != target && (variant.Status != ProductStatus.Archived || variant.ArchivedWithProduct == true);

    public static void Apply(ProductVariant variant, ProductStatus target, DateTimeOffset at)
    {
        variant.ArchivedWithProduct = target == ProductStatus.Archived ? true : null;
        variant.Status = target;
        variant.UpdatedAt = at;
        variant.Version++;
    }

    // A variant created archived (a new row of an archived product or colour card) is archived with its product.
    public static void MarkCreated(IEnumerable<ProductVariant> variants)
    {
        foreach (var variant in variants)
            if (variant.Status == ProductStatus.Archived) variant.ArchivedWithProduct = true;
    }
}
