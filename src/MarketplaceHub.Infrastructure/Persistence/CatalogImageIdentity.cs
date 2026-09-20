namespace MarketplaceHub.Infrastructure.Persistence;

internal static class CatalogImageIdentity
{
    public static IReadOnlyList<string> DistinctUrls(IEnumerable<string?> values)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            var normalized = NormalizeUrl(value);
            if (normalized is null || !seen.Add(normalized)) continue;
            result.Add(normalized);
        }

        return result;
    }

    public static IReadOnlyList<string> DistinctDisplayUrls(IEnumerable<string?> values)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            var displayUrl = value.Trim();
            if (!seen.Add(Key(displayUrl))) continue;
            result.Add(displayUrl);
        }

        return result;
    }

    public static string Key(string value) => NormalizeUrl(value) ?? value.Trim();

    public static string? NormalizeUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var candidate = value.Trim();
        if (candidate.StartsWith("//", StringComparison.Ordinal)) candidate = "https:" + candidate;
        else if (candidate.StartsWith("/", StringComparison.Ordinal)) candidate = "https://cdn.dsmcdn.com" + candidate;
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return null;

        // CDN resize, cache-busting and signed query parameters can change
        // between catalog reads while the underlying image stays the same.
        // The stable image identity is the HTTPS path; preserve the first
        // path spelling so a later import can reuse the existing asset.
        return uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }
}
