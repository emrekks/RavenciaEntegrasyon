using System.Text.Json;

namespace MarketplaceHub.Infrastructure.Adapters;

// The property readers the marketplace mappers had each copied. Numbers go through RemoteJson (dot-decimal only).
// The variants differ on purpose and are named for what they accept; each keeps the reading its callers had.
internal static class RemoteFields
{
    // A string or number property as trimmed text; null when the element is not an object, the property is missing,
    // blank or of another kind.
    internal static string? Text(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value)) return null;
        var text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    // As Text, and the word "null" written as text is missing too (Pazarama writes it in order, question and return fields).
    internal static string? TextOrNullWord(JsonElement element, string property)
    {
        var text = Text(element, property);
        return text == "null" ? null : text;
    }

    // A non-blank string property, trimmed; numbers are not read. The element must be an object (TryGetProperty throws
    // otherwise), as the invoice-link readers always had it.
    internal static string? StringText(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : null;

    // O5: text amounts are dot-decimal; "1234,50" stays unknown instead of being read as 123450.
    internal static decimal? Decimal(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) ? RemoteJson.Decimal(value) : null;

    // A JSON true/false only. The element must be an object.
    internal static bool? Bool(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;

    // A JSON whole number only. The element must be an object.
    internal static long? Long(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : null;
}
