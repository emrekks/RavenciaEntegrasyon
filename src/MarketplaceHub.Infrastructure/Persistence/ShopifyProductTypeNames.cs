using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MarketplaceHub.Infrastructure.Persistence;

// Shopify's categories are the store's own product types (free text). A panel category is mapped to one of them on
// Eşleştirmeler; the user may also type a type the store does not hold yet, which the first publication creates.
internal static class ShopifyProductTypeNames
{
    internal const int MaximumLength = 255;
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");

    // A typed product type: trimmed, 1-255 characters, no control characters; null when it is not one.
    internal static string? Clean(string? text)
    {
        var value = text?.Trim();
        if (string.IsNullOrEmpty(value) || value.Length > MaximumLength || value.Any(char.IsControl)) return null;
        return value;
    }

    // Names compared for "Birebir Eşle" and for a typed type the store already holds: trimmed, inner spaces collapsed,
    // Turkish lower case (Bluz = BLUZ = bluz, İç Giyim = iç giyim).
    internal static string Key(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLower(Turkish);

    internal static string PayloadHash(string productType) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(productType))));

    internal sealed record ExactMatch(Guid CategoryId, string CategoryName, string ProductType);

    internal sealed record ExactMatchPlan(IReadOnlyList<ExactMatch> Matches, int WithoutTypeCount, int AmbiguousCount);

    // "Birebir Eşle": every unmapped panel category whose name is a product type of the store, compared by Key. A name
    // that two of the store's types share (Bluz and bluz) is left to the user; nothing new is created.
    internal static ExactMatchPlan PlanExactMatches(IEnumerable<(Guid Id, string Name)> unmappedCategories, IEnumerable<string> productTypes)
    {
        var typesByKey = productTypes
            .Where(type => !string.IsNullOrWhiteSpace(type))
            .GroupBy(Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Distinct(StringComparer.Ordinal).ToList(), StringComparer.Ordinal);
        var matches = new List<ExactMatch>();
        var withoutType = 0;
        var ambiguous = 0;
        foreach (var (id, name) in unmappedCategories)
        {
            if (!typesByKey.TryGetValue(Key(name), out var types)) withoutType++;
            else if (types.Count > 1) ambiguous++;
            else matches.Add(new(id, name.Trim(), types[0]));
        }
        return new(matches.OrderBy(match => match.CategoryName, StringComparer.Create(Turkish, true)).ToList(), withoutType, ambiguous);
    }
}
