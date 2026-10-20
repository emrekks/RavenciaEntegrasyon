using System.Text.RegularExpressions;

namespace MarketplaceHub.Infrastructure.Adapters.Trendyol;

// Trendyol asks every integration to send "User-Agent: {sellerId} - {IntegratorName}" where the name is at most
// 30 letters or digits (e.g. "123456 - SelfIntegration"). The header must also be plain printable ASCII: a Turkish
// letter or a control character cannot be sent at all, so such an identity is a configuration error (C12).
public static partial class TrendyolUserAgentPolicy
{
    public static bool IsHeaderSafe(string? identity) =>
        !string.IsNullOrWhiteSpace(identity) && identity.All(character => character is >= ' ' and <= '~');

    public static bool MatchesDocumentedFormat(string? identity, string? sellerId)
    {
        if (!IsHeaderSafe(identity) || string.IsNullOrWhiteSpace(sellerId)) return false;
        var match = DocumentedFormat().Match(identity!.Trim());
        return match.Success && string.Equals(match.Groups["seller"].Value, sellerId.Trim(), StringComparison.Ordinal);
    }

    [GeneratedRegex("^(?<seller>[0-9]+) - [A-Za-z0-9]{1,30}$", RegexOptions.CultureInvariant)]
    private static partial Regex DocumentedFormat();
}
