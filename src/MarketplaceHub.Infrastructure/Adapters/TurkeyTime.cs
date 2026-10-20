using System.Globalization;
using System.Text.RegularExpressions;

namespace MarketplaceHub.Infrastructure.Adapters;

// Timestamp policy of the Turkish marketplaces (Hepsiburada, Pazarama): a text timestamp without an offset is
// Turkey time (UTC+3, no daylight saving), as their order dates are; one with Z or an offset keeps it. Every
// timestamp of such an adapter (orders, packages, item history, questions) is read through here, so one
// adapter never mixes "assume UTC" and "assume Turkey" for the same kind of text.
internal static partial class TurkeyTime
{
    internal static readonly TimeSpan Offset = TimeSpan.FromHours(3);

    internal static DateTimeOffset? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();
        if (HasOffset(text))
            return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var withOffset) ? withOffset.ToUniversalTime() : null;
        return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)
            ? new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), Offset).ToUniversalTime()
            : null;
    }

    // True when the text ends with Z or an offset such as +03:00 / +0300.
    internal static bool HasOffset(string text) => OffsetSuffix().IsMatch(text);

    [GeneratedRegex(@"(Z|[+-]\d{2}:?\d{2})$")]
    private static partial Regex OffsetSuffix();
}
