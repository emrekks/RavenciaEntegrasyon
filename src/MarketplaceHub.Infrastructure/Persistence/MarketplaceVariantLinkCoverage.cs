namespace MarketplaceHub.Infrastructure.Persistence;

internal static class MarketplaceVariantLinkCoverage
{
    public static bool IsComplete(IEnumerable<string?> requiredExternalIds, IEnumerable<string?> linkedExternalIds)
    {
        var linked = linkedExternalIds
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(Normalize)
            .ToHashSet(StringComparer.Ordinal);
        return requiredExternalIds
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(Normalize)
            .Distinct(StringComparer.Ordinal)
            .All(linked.Contains);
    }

    public static string Normalize(string? value) => value?.Trim().ToUpperInvariant() ?? string.Empty;
}
