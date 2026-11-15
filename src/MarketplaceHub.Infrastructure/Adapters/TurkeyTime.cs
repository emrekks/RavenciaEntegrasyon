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

    // The one Turkey clock of every adapter: a wall-clock time read from a marketplace, the Turkey view of an instant,
    // its calendar day and its text in a marketplace's format all go through these.
    internal static DateTimeOffset FromLocal(DateTime local) =>
        new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), Offset).ToUniversalTime();

    internal static DateTimeOffset ToLocal(DateTimeOffset instant) => instant.ToOffset(Offset);

    internal static DateTime LocalDate(DateTimeOffset instant) => instant.ToOffset(Offset).Date;

    internal static DateOnly Day(DateTimeOffset instant) => DateOnly.FromDateTime(instant.ToOffset(Offset).DateTime);

    internal static string Format(DateTimeOffset instant, string format) =>
        instant.ToOffset(Offset).ToString(format, CultureInfo.InvariantCulture);

    // n11's SOAP return list has always converted through the system's Europe/Istanbul time-zone rules (fallback: a
    // fixed UTC+3 zone) instead of the fixed offset. Both agree for every date since September 2016 (no daylight saving
    // since); before that the zone rules apply Turkey's former summer time. Kept as it was for that one caller.
    internal static readonly TimeZoneInfo Zone = FindZone();

    internal static string FormatWithZoneRules(DateTimeOffset instant, string format) =>
        TimeZoneInfo.ConvertTime(instant, Zone).ToString(format, CultureInfo.InvariantCulture);

    private static TimeZoneInfo FindZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul"); }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return TimeZoneInfo.CreateCustomTimeZone("Turkey", TimeSpan.FromHours(3), "Turkey", "Turkey");
        }
    }

    internal static DateTimeOffset? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();
        if (HasOffset(text))
            return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var withOffset) ? withOffset.ToUniversalTime() : null;
        return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)
            ? FromLocal(local)
            : null;
    }

    // C13: Trendyol (orderDate, stream and V2 date filters) and n11 (shipmentPackages dates, invoiceDateTime) document
    // their epoch-millisecond dates as "GMT+3". The owner chose to correct the reading side once a live value proves the
    // shift. Trendyol's orderDate is proven (2 Nov 2026: stored three hours ahead of its arrival) and is read through
    // FromTurkeyWallClockEpochMilliseconds; its other dates and n11's dates show no shift on live and stay plain UTC
    // epochs. Requests are unchanged.
    // These two helpers state the documented reading exactly: a "GMT+3 epoch" is the Turkey wall-clock time
    // written as if it were UTC, i.e. three hours ahead of the true UTC epoch.
    internal static long ToTurkeyWallClockEpochMilliseconds(DateTimeOffset instant) =>
        new DateTimeOffset(instant.ToOffset(Offset).DateTime, TimeSpan.Zero).ToUnixTimeMilliseconds();

    internal static DateTimeOffset FromTurkeyWallClockEpochMilliseconds(long value) =>
        new DateTimeOffset(DateTimeOffset.FromUnixTimeMilliseconds(value).DateTime, Offset).ToUniversalTime();

    // True when the text ends with Z or an offset such as +03:00 / +0300 / +03 (G29). The offset must follow a time
    // (hh:mm[:ss[.fff]]), so the day of a bare date ("2026-10-19") is never read as an offset. The offset may be
    // written right after the time or after one space ("2026-10-20 14:30:00 +03:00").
    internal static bool HasOffset(string text) => OffsetSuffix().IsMatch(text);

    [GeneratedRegex(@"(Z|(?<=\d{2}:\d{2}(?::\d{2}(?:\.\d+)?)?) ?[+-]\d{2}(?::?\d{2})?)$")]
    private static partial Regex OffsetSuffix();
}
