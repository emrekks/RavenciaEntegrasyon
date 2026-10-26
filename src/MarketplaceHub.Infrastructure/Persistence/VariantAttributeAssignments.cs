using MarketplaceHub.Domain;

namespace MarketplaceHub.Infrastructure.Persistence;

// The attribute values one variant carries, as the publication composers send them. A product-level value is the
// default for every variant; a variant that has its own value for the same attribute uses only its own. Imports
// often hold both (Renk = Kahverengi on the product and on each variant), and counting both read as two values
// for one field (Hepsiburada ATTRIBUTE_MULTIVALUE_UNSUPPORTED, live 27 Oct 2026).
public static class VariantAttributeAssignments
{
    public static IEnumerable<IGrouping<Guid, ProductAttributeAssignment>> ForVariant(IEnumerable<ProductAttributeAssignment> assignments, Guid variantId) =>
        assignments
            .Where(x => x.VariantId is null || x.VariantId == variantId)
            .GroupBy(x => x.AttributeId)
            .Select(group => group.Any(x => x.VariantId == variantId)
                ? group.Where(x => x.VariantId == variantId).GroupBy(x => x.AttributeId).Single()
                : group);
}
