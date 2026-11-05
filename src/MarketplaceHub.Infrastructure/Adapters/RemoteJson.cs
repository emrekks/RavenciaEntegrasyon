using System.Globalization;
using System.Text.Json;

namespace MarketplaceHub.Infrastructure.Adapters;

// Reads numbers from marketplace responses without trusting the value's kind. JsonElement.TryGetInt32 and friends
// throw InvalidOperationException on a null, string or object value instead of returning false, so a field the
// marketplace sends as null would end a whole read; here anything that is not a usable number is unknown (null).
//
// Text numbers are read in the dot-decimal form the JSON and SOAP APIs document ("1234.50"). A comma is never read:
// with NumberStyles.Number or Any and the invariant culture, "1234,50" parses as 123450 (the comma is taken as a
// thousands separator), which turns a price into a hundred times itself. Such text stays unknown. Fields a
// marketplace documents in the Turkish format (Hepsiburada's tr-TR listing prices) keep their own parser.
internal static class RemoteJson
{
    private const NumberStyles DotDecimal = NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite
        | NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent;

    internal static bool TryParseDecimal(string? text, out decimal value)
    {
        value = 0;
        return !string.IsNullOrWhiteSpace(text) && decimal.TryParse(text, DotDecimal, CultureInfo.InvariantCulture, out value);
    }

    internal static decimal? ParseDecimal(string? text) => TryParseDecimal(text, out var value) ? value : null;

    // A JSON number, or a dot-decimal number written as text.
    internal static decimal? Decimal(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number when value.TryGetDecimal(out var number) => number,
        JsonValueKind.String => ParseDecimal(value.GetString()),
        _ => null
    };

    internal static decimal? Decimal(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) ? Decimal(value) : null;

    // A JSON number only (our own job payloads, where a text amount is a malformed payload).
    internal static decimal? NumberDecimal(JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number) ? number : null;

    internal static decimal? NumberDecimal(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) ? NumberDecimal(value) : null;

    // Whole numbers only from JSON numbers (or, where noted by the caller, digits in text through ParseInt32).
    internal static int? Int32(JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;

    internal static int? Int32(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) ? Int32(value) : null;

    internal static long? Int64(JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : null;

    internal static long? Int64(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) ? Int64(value) : null;

    internal static double? Double(JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) ? number : null;

    internal static double? Double(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) ? Double(value) : null;

    // An epoch in milliseconds that DateTimeOffset can hold; anything else is unknown.
    internal static DateTimeOffset? EpochMilliseconds(JsonElement value)
    {
        if (Int64(value) is not { } milliseconds) return null;
        try { return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds); }
        catch (ArgumentOutOfRangeException) { return null; }
    }
}
