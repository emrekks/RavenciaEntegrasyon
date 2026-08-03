namespace MarketplaceHub.Infrastructure.Adapters.TrendyolEFaturam.Contracts;

public static class TrendyolEFaturamContractGuard
{
    public static bool IsPinnedApiVersion(string value) => string.Equals(value, "1.0.0", StringComparison.Ordinal);
    public static bool IsAllowedEnvironment(string value) => value is "STAGE" or "PRODUCTION";
    public static bool IsTaxIdFormat(string value) => value.Length is 10 or 11 && value.All(char.IsAsciiDigit);
    public static bool IsPermanentDocumentUrl(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;
}
