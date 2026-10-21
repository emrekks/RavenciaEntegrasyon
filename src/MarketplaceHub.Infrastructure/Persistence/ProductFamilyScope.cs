using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

// The products of a model-code family (ProductFamilyKeyPolicy): the database narrows the caller's variants to
// the same brand, the model-code key is compared in memory.
public static class ProductFamilyScope
{
    public static async Task<List<Guid>> ProductIdsAsync(AppDbContext db, Guid tenantId, Guid? brandId, string? modelCode, IQueryable<ProductVariant> variants, CancellationToken cancellationToken)
    {
        if (ProductFamilyKeyPolicy.ModelCodeKey(modelCode) is null) return [];
        var rows = await variants
            .Where(variant => variant.TenantId == tenantId
                && variant.ModelCode != null
                && db.Products.Any(product => product.TenantId == tenantId && product.Id == variant.ProductId && product.BrandId == brandId))
            .Select(variant => new { variant.ProductId, variant.ModelCode })
            .Distinct()
            .ToListAsync(cancellationToken);
        return rows
            .Where(row => ProductFamilyKeyPolicy.SameFamily(brandId, row.ModelCode, brandId, modelCode))
            .Select(row => row.ProductId)
            .Distinct()
            .ToList();
    }
}
