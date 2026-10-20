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

    // C13: Trendyol (orderDate, stream and V2 date filters) and n11 (shipmentPackages dates, invoiceDateTime) document
    // their epoch-millisecond dates as "GMT+3". The adapters keep sending and reading them as plain UTC epochs; the
    // owner chose to correct the reading side once a live value proves the shift, so nothing here changes the requests.
    // These two helpers state the documented reading exactly: a "GMT+3 epoch" is the Turkey wall-clock time
    // written as if it were UTC, i.e. three hours ahead of the true UTC epoch.
    internal static long ToTurkeyWallClockEpochMilliseconds(DateTimeOffset instant) =>
        new DateTimeOffset(instant.ToOffset(Offset).DateTime, TimeSpan.Zero).ToUnixTimeMilliseconds();

    internal static DateTimeOffset FromTurkeyWallClockEpochMilliseconds(long value) =>
        new DateTimeOffset(DateTimeOffset.FromUnixTimeMilliseconds(value).DateTime, Offset).ToUniversalTime();

    // True when the text ends with Z or an offset such as +03:00 / +0300 / +03 (G29). The offset must follow a time
    // (hh:mm[:ss[.fff]]), so the day of a bare date ("2026-10-19") is never read as an offset.
    internal static bool HasOffset(string text) => OffsetSuffix().IsMatch(text);

    [GeneratedRegex(@"(Z|(?<=\d{2}:\d{2}(?::\d{2}(?:\.\d+)?)?)[+-]\d{2}(?::?\d{2})?)$")]
    private static partial Regex OffsetSuffix();
}
