namespace MarketplaceHub.Application;

public static class ProductCopyCodePolicy
{
    public static string WithCopySuffix(string value, int maxLength)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, 7);
        const string suffix = " Kopya";
        var source = value.Trim();
        var prefixLength = Math.Min(source.Length, maxLength - suffix.Length);
        var prefix = source[..prefixLength].TrimEnd();
        return prefix.Length == 0 ? suffix.TrimStart() : $"{prefix}{suffix}";
    }

    public static string WithUniqueCopySuffix(string value, string uniqueToken, ISet<string> existingNormalizedValues, int maxLength = 160)
    {
        ArgumentNullException.ThrowIfNull(existingNormalizedValues);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, 16);

        var token = new string(uniqueToken.Where(char.IsAsciiLetterOrDigit).ToArray()).ToUpperInvariant();
        if (token.Length == 0) token = "COPY";

        var source = value.Trim();
        var attempt = 1;
        while (true)
        {
            var disambiguator = attempt == 1 ? string.Empty : $"-{attempt}";
            var suffix = $"-{token}{disambiguator}-KOPYA";
            var prefixLength = Math.Min(source.Length, maxLength - suffix.Length);
            var candidate = $"{source[..prefixLength].TrimEnd()}{suffix}";
            var normalized = Normalize(candidate);
            if (existingNormalizedValues.Add(normalized)) return candidate;
            attempt++;
        }
    }

    private static string Normalize(string value) => value.Trim().ToUpperInvariant();
}
