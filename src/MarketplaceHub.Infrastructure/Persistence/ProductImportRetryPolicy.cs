using System.Text.Json;
using System.Text.Json.Serialization;

namespace MarketplaceHub.Infrastructure.Persistence;

// ISLEYICI-1 retry (16 Nov 2026): the product models a catalog import could not store, read again one by one. Each model
// that failed is listed on the connection's PRODUCTS_RETRY cursor by the code the marketplace looks a single product up
// by (Trendyol and Hepsiburada: productMainId) and gets a single-product import job of its own, 30 minutes, 2 hours and
// 8 hours after the failure (three tries). A retry that stores the model takes it off the list (the import resolves its
// PRODUCT_IMPORT_FAILED issue as any successful read does); one still failing after its last try, or one whose try never
// ran (a job that ended dead), leaves the list and keeps its open issue. Reads only: nothing is sent to a marketplace.
// This is the remedy for a model that keeps failing; the PRODUCTS watermark hold (at most MaxHeldRuns runs) is the other.
internal static class ProductImportRetryPolicy
{
    public const string CursorResource = "PRODUCTS_RETRY";
    // The cursor holds 4096 characters; an entry takes about 80.
    public const int MaxTracked = 40;
    public static readonly IReadOnlyList<TimeSpan> Delays = [TimeSpan.FromMinutes(30), TimeSpan.FromHours(2), TimeSpan.FromHours(8)];
    // A try whose job never reported back this long after it was due (a dead job) is given up.
    public static readonly TimeSpan StaleAfter = TimeSpan.FromDays(1);
    // The payload flag of a retry job: its completion updates the list instead of adding to it.
    public const string PayloadFlag = "importRetry";

    public static bool Supports(string? platformCode) => platformCode is "TRENDYOL" or "HEPSIBURADA";

    public sealed record Entry(
        [property: JsonPropertyName("k")] string LookupKey,
        [property: JsonPropertyName("n")] int Attempts,
        [property: JsonPropertyName("t")] DateTimeOffset NextAt);

    public static IReadOnlyList<Entry> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return (JsonSerializer.Deserialize<List<Entry>>(json) ?? []).Where(entry => !string.IsNullOrWhiteSpace(entry.LookupKey)).ToList();
        }
        catch (JsonException) { return []; }
    }

    public static string? Serialize(IReadOnlyList<Entry> entries) => entries.Count == 0 ? null : JsonSerializer.Serialize(entries);

    // The models failed now join the list for their first try (one already listed keeps its place); tries long overdue
    // are given up. Added: the entries whose first try is to be queued now. The list never grows past MaxTracked: a model
    // past the cap keeps only its issue.
    public static IReadOnlyList<Entry> Note(IReadOnlyList<Entry> entries, IEnumerable<string> failedKeys, DateTimeOffset now, out IReadOnlyList<Entry> added)
    {
        var listed = entries.Where(entry => now - entry.NextAt < StaleAfter).ToList();
        var known = listed.Select(entry => entry.LookupKey).ToHashSet(StringComparer.Ordinal);
        var fresh = new List<Entry>();
        foreach (var key in failedKeys.Where(key => !string.IsNullOrWhiteSpace(key)).Select(key => key.Trim()).Distinct(StringComparer.Ordinal))
        {
            if (listed.Count >= MaxTracked) break;
            if (!known.Add(key)) continue;
            var entry = new Entry(key, 0, now + Delays[0]);
            listed.Add(entry);
            fresh.Add(entry);
        }
        added = fresh;
        return listed;
    }

    // After a retry of the model: stored, it leaves the list; failed (or not found), it waits for its next try, or leaves
    // the list after its last one. Next: the entry whose next try is to be queued, if any.
    public static IReadOnlyList<Entry> AfterRetry(IReadOnlyList<Entry> entries, string lookupKey, bool stored, DateTimeOffset now, out Entry? next)
    {
        next = null;
        var current = entries.FirstOrDefault(entry => string.Equals(entry.LookupKey, lookupKey, StringComparison.Ordinal));
        if (current is null) return entries;
        var rest = entries.Where(entry => !ReferenceEquals(entry, current)).ToList();
        if (stored) return rest;
        var attempts = current.Attempts + 1;
        if (attempts >= Delays.Count) return rest;
        next = new Entry(lookupKey, attempts, now + Delays[attempts]);
        rest.Add(next);
        return rest;
    }

    // One job per try of a model: a try is queued once, whatever happens to its job.
    public static string JobDedupKey(Guid connectionId, string lookupKey, int attempt) =>
        $"product-import-retry:{connectionId:N}:{TextValues.Sha256Hex(lookupKey)[..24]}:{attempt}";
}
