using MarketplaceHub.Domain;

namespace MarketplaceHub.Infrastructure.Persistence;

internal static class ReferenceMappingRevalidationPolicy
{
    public static bool CanRevalidate(string resourceType, ReferenceItem previous, ReferenceItem current)
    {
        if (!string.Equals(previous.ResourceType, resourceType, StringComparison.Ordinal)
            || !string.Equals(current.ResourceType, resourceType, StringComparison.Ordinal)
            || !string.Equals(previous.ExternalId, current.ExternalId, StringComparison.Ordinal)
            || !string.Equals(previous.ParentExternalId, current.ParentExternalId, StringComparison.Ordinal)
            || !string.Equals(previous.Name, current.Name, StringComparison.Ordinal)
            || !string.Equals(previous.Path, current.Path, StringComparison.Ordinal)
            || previous.Depth != current.Depth
            || previous.IsLeaf != current.IsLeaf
            || previous.IsActive != current.IsActive
            || previous.IsRequired != current.IsRequired
            || previous.AllowsCustomValue != current.AllowsCustomValue
            || previous.AllowsMultipleValues != current.AllowsMultipleValues)
            return false;

        return resourceType switch
        {
            "CATEGORIES" or "BRANDS" or "CATEGORY_ATTRIBUTES" or "ATTRIBUTE_VALUES" => current.IsActive,
            _ => false
        };
    }
}
