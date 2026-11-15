using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

// The products of a model-code family (ProductFamilyKeyPolicy): the database narrows the caller's variants to
// the same brand, the model-code key is compared in memory. With the card's model (familyId, Model Kaydı Faz 2) the
// family is the model's linked cards plus the rule's cards linked to no model, less a card linked to another model
// (ProductModelLinks.FamilyAwareAsync); a card without a model keeps the rule alone.
public static class ProductFamilyScope
{
    public static async Task<List<Guid>> ProductIdsAsync(AppDbContext db, Guid tenantId, Guid? brandId, string? modelCode, IQueryable<ProductVariant> variants, CancellationToken cancellationToken, Guid? familyId = null)
    {
        var ruleIds = await RuleProductIdsAsync(db, tenantId, brandId, modelCode, variants, cancellationToken);
        return familyId is null ? ruleIds : await ProductModelLinks.FamilyAwareAsync(db, tenantId, familyId, ruleIds, db.Products.AsNoTracking(), cancellationToken);
    }

    private static async Task<List<Guid>> RuleProductIdsAsync(AppDbContext db, Guid tenantId, Guid? brandId, string? modelCode, IQueryable<ProductVariant> variants, CancellationToken cancellationToken)
    {
        if (ProductFamilyKeyPolicy.ModelCodeKey(modelCode) is not { } key) return [];
        // KATALOG-8: only the brand's cards that may hold the key are read (ModelCodeOwnership.ProductIdsWithKeys), not
        // every coded variant of the brand; the key is still compared in memory.
        var candidates = ModelCodeOwnership.ProductIdsWithKeys(db, tenantId, [key]);
        var rows = await variants
            .Where(variant => variant.TenantId == tenantId
                && variant.ModelCode != null
                && candidates.Contains(variant.ProductId)
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
