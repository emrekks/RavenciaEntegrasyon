using System.Globalization;
using System.Text.Json;

namespace MarketplaceHub.Infrastructure.Adapters.Hepsiburada;

internal static partial class HepsiburadaJsonMapper
{
    private static JsonElement Unwrap(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array) return root;
        foreach (var name in new[] { "data", "Data", "result", "Result" })
            if (TryFind(root, out var child, name) && child.ValueKind is JsonValueKind.Object or JsonValueKind.Array) return child;
        return root;
    }

    private static string Snapshot(JsonElement value) => value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? "{}" : value.GetRawText();
    private static string? First(params string?[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    private static bool? Boolean(JsonElement element, params string[] names)
    {
        var value = Find(element, names);
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False) return value.GetBoolean();
        if (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var parsed)) return parsed;
        return null;
    }
    // Hepsiburada's readings are named RemoteFields variants (ADAPTOR-TY-HB-8): names matched ignoring case, text as
    // sent (untrimmed, a blank string is not missing, true/false read as text: TextIgnoringCase), and amounts that also
    // read Turkish money text and an {amount|value} object (MoneyIgnoringCase). The golden contract fixtures pin them.
    private static string? Text(JsonElement element, params string[] names) => RemoteFields.TextIgnoringCase(element, names);
    private static IReadOnlyList<string> TextValues(JsonElement element, params string[] names)
    {
        var value = Find(element, names);
        if (value.ValueKind == JsonValueKind.Array)
            return value.EnumerateArray()
                .Select(item => item.ValueKind is JsonValueKind.String or JsonValueKind.Number ? item.ToString().Trim() : null)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item!)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        if (value.ValueKind is JsonValueKind.String or JsonValueKind.Number)
            return (value.ToString().Contains(',') ? value.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : [value.ToString().Trim()])
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        return [];
    }
    // O5: a JSON number, a dot-decimal text, or Hepsiburada's own Turkish money text ("130,50", "1.234,50").
    private static decimal? Decimal(JsonElement element, params string[] names) => RemoteFields.MoneyIgnoringCase(element, names);
    internal static decimal? TurkishMoney(string? text) => RemoteFields.TurkishMoney(text);
    private static decimal? Money(JsonElement element, params string[] names) => Decimal(element, names);
    private static int? Integer(JsonElement element, params string[] names)
    {
        var value = Find(element, names);
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number)) return number;
        return null;
    }
    // The first name (in priority order) that holds a readable date; a present but empty field does not hide the next one.
    private static DateTimeOffset? Date(JsonElement element, params string[] names)
    {
        foreach (var name in names)
            if (TurkeyDate(Text(element, name)) is { } date) return date;
        return null;
    }

    // Hepsiburada writes dates in Turkey time without an offset ("2026-10-19T14:19:58"): on live rows an
    // order dated 14:19 was already stored by the panel at 11:21 UTC. A value with an offset or Z keeps it.

    // The Hepsiburada policy for every text timestamp (orders, packages, questions): see TurkeyTime.
    internal static DateTimeOffset? TurkeyDate(string? value) => TurkeyTime.Parse(value);

    // True when the text ends with Z or an offset such as +03:00 / +0300.
    internal static bool HasOffset(string text) => TurkeyTime.HasOffset(text);

    // A query date the way Hepsiburada reads it: Turkey time, minutes.
    internal static string QueryTime(DateTimeOffset value) =>
        value.ToOffset(TurkeyTime.Offset).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    private static JsonElement Find(JsonElement element, params string[] names) => TryFind(element, out var value, names) ? value : default;
    // The names are in priority order: the first name present wins, whatever order the JSON lists its properties in.
    // One walk over the properties (it used to walk them once per name).
    private static bool TryFind(JsonElement element, out JsonElement value, params string[] names) =>
        RemoteFields.TryFindIgnoringCase(element, out value, names);
}
