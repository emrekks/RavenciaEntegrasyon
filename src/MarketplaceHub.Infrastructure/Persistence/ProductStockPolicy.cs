using MarketplaceHub.Domain;

namespace MarketplaceHub.Infrastructure.Persistence;

internal static class ProductStockPolicy
{
    public static decimal TotalStock(IEnumerable<ProductVariant> variants, IReadOnlyDictionary<Guid, decimal> inventoryByVariant)
        => variants.Sum(variant => inventoryByVariant.GetValueOrDefault(variant.Id));

    public static bool IsLowStock(IEnumerable<ProductVariant> variants, IReadOnlyDictionary<Guid, decimal> inventoryByVariant)
    {
        var colorGroups = variants.GroupBy(variant => ColorOptionValue(variant.OptionSignature), StringComparer.OrdinalIgnoreCase);
        return colorGroups.Any(group =>
        {
            var variantCount = group.Count();
            var unavailableCount = group.Count(variant => inventoryByVariant.GetValueOrDefault(variant.Id) <= 0m);
            return variantCount > 0 && unavailableCount >= (variantCount + 1) / 2;
        });
    }

    private static string? ColorOptionValue(string optionSignature)
    {
        foreach (var part in optionSignature.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf(':');
            if (separator < 0) separator = part.IndexOf('=');
            if (separator < 0) continue;
            var key = part[..separator].Replace(" ", "", StringComparison.Ordinal).Trim().ToUpperInvariant();
            if (key is not ("RENK" or "WEBCOLOR" or "COLOR" or "COLOUR")) continue;
            var value = part[(separator + 1)..].Trim();
            return string.IsNullOrWhiteSpace(value) ? null : value.ToUpperInvariant();
        }

        return null;
    }
}
