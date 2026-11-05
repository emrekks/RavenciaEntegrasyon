using System.Text.Json;
using System.Text.Json.Serialization;

namespace MarketplaceHub.Infrastructure.Persistence;

// T2-O8: the Trendyol orders an integrity check refused (package quantities, line coverage, a read without lines or
// packages), or stored with a product line that could not be read, and the order stream has since moved past. Since
// 5 Nov 2026 also every marketplace's order a mapper refused while its number was readable (AdapterPageIssue.RereadOrderId:
// no order date, an unknown currency, a malformed package); a record without a readable number keeps only its issue. Each is read again by its number at most four times, 30 minutes,
// 2, 8 and 24 hours after the previous try (about a day and a half in all), three per order read, so a refusal never
// costs more than a handful of requests. Kept on the connection's ORDERS_REJECTED_REREAD cursor.
internal static class RejectedOrderRereadPolicy
{
    public const string CursorResource = "ORDERS_REJECTED_REREAD";
    public const int MaxPerRun = 3;
    // The cursor holds 4096 characters; an entry takes about 60.
    public const int MaxTracked = 40;
    public static readonly IReadOnlyList<TimeSpan> Delays = [TimeSpan.FromMinutes(30), TimeSpan.FromHours(2), TimeSpan.FromHours(8), TimeSpan.FromHours(24)];

    public sealed record Entry(
        [property: JsonPropertyName("o")] string OrderId,
        [property: JsonPropertyName("n")] int Attempts,
        [property: JsonPropertyName("t")] DateTimeOffset NextAt);

    public static IReadOnlyList<Entry> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return (JsonSerializer.Deserialize<List<Entry>>(json) ?? [])
                .Where(entry => !string.IsNullOrWhiteSpace(entry.OrderId))
                .ToList();
        }
        catch (JsonException) { return []; }
    }

    public static string? Serialize(IReadOnlyList<Entry> entries) => entries.Count == 0 ? null : JsonSerializer.Serialize(entries);

    // Orders refused now join the list for their first try; one already listed keeps its count, so being refused again
    // by a stream read does not start its tries over. Past the cap, the orders with the most tries go first.
    public static IReadOnlyList<Entry> Note(IReadOnlyList<Entry> entries, IEnumerable<string> refusedOrderIds, DateTimeOffset now) =>
        Note(entries, refusedOrderIds, now, out _);

    // The same, naming the orders the cap pushed off the list: they are not dropped silently, the caller records them
    // (OverflowIssueCode) so the owner sees which orders will not be read again by themselves.
    public static IReadOnlyList<Entry> Note(IReadOnlyList<Entry> entries, IEnumerable<string> refusedOrderIds, DateTimeOffset now, out IReadOnlyList<string> dropped)
    {
        var listed = entries.ToList();
        var known = listed.Select(entry => entry.OrderId).ToHashSet(StringComparer.Ordinal);
        foreach (var orderId in refusedOrderIds.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim()).Distinct(StringComparer.Ordinal))
            if (known.Add(orderId)) listed.Add(new Entry(orderId, 0, now + Delays[0]));
        if (listed.Count <= MaxTracked)
        {
            dropped = [];
            return listed;
        }
        var kept = listed.OrderBy(entry => entry.Attempts).ThenByDescending(entry => entry.NextAt).Take(MaxTracked).ToList();
        var keptIds = kept.Select(entry => entry.OrderId).ToHashSet(StringComparer.Ordinal);
        dropped = listed.Where(entry => !keptIds.Contains(entry.OrderId)).Select(entry => entry.OrderId).ToList();
        return kept;
    }

    public const string OverflowIssueCode = "ORDER_REREAD_LIST_FULL";

    public static string OverflowSummary(IReadOnlyList<string> dropped) =>
        $"Yeniden okunacak sipariş listesi dolu ({MaxTracked} sipariş); {dropped.Count} sipariş listeye alınamadı ve kendiliğinden yeniden okunmayacak: {string.Join(", ", dropped.Take(20))}{(dropped.Count > 20 ? "…" : "")}. Bu siparişler pazaryerinde kontrol edilmeli.";

    public static IReadOnlyList<Entry> Due(IReadOnlyList<Entry> entries, DateTimeOffset now) => entries
        .Where(entry => entry.NextAt <= now)
        .OrderBy(entry => entry.NextAt)
        .ThenBy(entry => entry.OrderId, StringComparer.Ordinal)
        .Take(MaxPerRun)
        .ToList();

    // An order that landed leaves the list; one still refused waits for its next try, and leaves after the last one.
    public static IReadOnlyList<Entry> AfterRead(IReadOnlyList<Entry> entries, Entry read, bool stillRefused, DateTimeOffset now)
    {
        var rest = entries.Where(entry => !string.Equals(entry.OrderId, read.OrderId, StringComparison.Ordinal)).ToList();
        var tries = read.Attempts + 1;
        if (stillRefused && tries < Delays.Count) rest.Add(read with { Attempts = tries, NextAt = now + Delays[tries] });
        return rest;
    }
}
