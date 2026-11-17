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

    // The property named by the first of the names (in priority order) that the object has, compared ignoring case
    // (Hepsiburada mixes "orderNumber" and "OrderNumber" for one field). One walk over the properties: a property that
    // matches an earlier name wins whatever order the JSON lists them in; of two matching the same name the first listed
    // wins. False, with a default value, when the element is not an object or no name is present.
    internal static bool TryFindIgnoringCase(JsonElement element, out JsonElement value, params string[] names)
    {
        value = default;
        if (element.ValueKind != JsonValueKind.Object) return false;
        var best = names.Length;
        foreach (var property in element.EnumerateObject())
        {
            for (var index = 0; index < best; index++)
            {
                if (!string.Equals(property.Name, names[index], StringComparison.OrdinalIgnoreCase)) continue;
                value = property.Value;
                best = index;
                break;
            }
            if (best == 0) break;
        }
        return best < names.Length;
    }

    // A JSON whole number only. The element must be an object.
    internal static long? Long(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : null;

    // ---- Named variants (ADAPTOR-TY-HB-8): readings a marketplace mapper has always had, kept exactly. ----

    // Raw text: the first of the names present with a non-null value, as written (a string untrimmed, a number, true or
    // false, an object or array as its JSON text). The element must be an object. Trendyol's mapper.
    internal static string? RawText(JsonElement element, params string[] names)
    {
        foreach (var name in names)
            if (element.TryGetProperty(name, out var value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)) return value.ToString();
        return null;
    }

    // The first of the names whose value reads as a dot-decimal amount (RemoteJson.Decimal); an unreadable value gives
    // way to the next name. The element must be an object. Trendyol's mapper.
    internal static decimal? FirstDecimal(JsonElement element, params string[] names)
    {
        foreach (var name in names)
            if (element.TryGetProperty(name, out var value) && RemoteJson.Decimal(value) is { } number) return number;
        return null;
    }

    // Loose truth: JSON true, the text "true" (any case) or a non-zero whole number is true; anything else, missing
    // included, is false. The element must be an object. Trendyol's mapper.
    internal static bool LooseTrue(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) return false;
        return value.ValueKind == JsonValueKind.True
            || value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var parsed) && parsed
            || value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number != 0;
    }

    // Loose yes/no: the first of the names present that reads as true or false (JSON true/false, or a value whose text
    // is "true"/"false"); null when none does. The element must be an object. Trendyol's mapper.
    internal static bool? LooseBool(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.True) return true;
            if (value.ValueKind == JsonValueKind.False) return false;
            if (bool.TryParse(value.ToString(), out var parsed)) return parsed;
        }
        return null;
    }

    // A JSON number only (a numeric text is not read), at full decimal precision. The element must be an object.
    // Hepsiburada's accounting amount value.
    internal static decimal? JsonNumber(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number) ? number : null;

    // Text ignoring case: the first of the names present (TryFindIgnoringCase) decides; a string as sent (untrimmed, a
    // blank string is text), a number or true/false as its text; any other kind is null without trying the next name.
    // Hepsiburada's mapper.
    internal static string? TextIgnoringCase(JsonElement element, params string[] names) =>
        TryFindIgnoringCase(element, out var value, names) ? ScalarText(value) : null;

    // As TextIgnoringCase, but a name whose value is of another kind (null, object, array) gives way to the next name.
    // Hepsiburada's question mapper.
    internal static string? FirstTextIgnoringCase(JsonElement element, params string[] names)
    {
        foreach (var name in names)
            if (TryFindIgnoringCase(element, out var value, name) && ScalarText(value) is { } text) return text;
        return null;
    }

    // Text by JSON order: the first property, in the order the JSON lists them, whose name matches any of the names
    // (ignoring case) and whose value is a string or a number, as its text (untrimmed). The element must be an object.
    // Hepsiburada's HTTP client (answers of its write and status calls).
    internal static string? FirstListedTextIgnoringCase(JsonElement element, params string[] names)
    {
        foreach (var property in element.EnumerateObject())
            if (names.Any(name => string.Equals(name, property.Name, StringComparison.OrdinalIgnoreCase)) && property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number)
                return property.Value.ToString();
        return null;
    }

    // Money ignoring case: the first of the names present (TryFindIgnoringCase); an object is read through its
    // amount/value field; then a JSON number, a dot-decimal text, or Turkish money text ("130,50", "1.234,50").
    // Hepsiburada's mapper (its catalog documents prices with a decimal comma).
    internal static decimal? MoneyIgnoringCase(JsonElement element, params string[] names)
    {
        TryFindIgnoringCase(element, out var value, names);
        if (value.ValueKind == JsonValueKind.Object) TryFindIgnoringCase(value, out value, "amount", "Amount", "value", "Value");
        // O5: a comma is never read as a thousands separator ("1234,50" is not 123450).
        if (RemoteJson.Decimal(value) is { } number) return number;
        return value.ValueKind == JsonValueKind.String ? TurkishMoney(value.GetString()) : null;
    }

    // Only the exact Turkish money shape: a decimal comma with one or two digits after it, dots only as thousands groups.
    internal static decimal? TurkishMoney(string? text)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed) || !System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"^-?(\d{1,3}(\.\d{3})+|\d+),\d{1,2}$")) return null;
        return decimal.TryParse(trimmed, System.Globalization.NumberStyles.AllowLeadingSign | System.Globalization.NumberStyles.AllowDecimalPoint | System.Globalization.NumberStyles.AllowThousands,
            System.Globalization.CultureInfo.GetCultureInfo("tr-TR"), out var amount) ? amount : null;
    }

    private static string? ScalarText(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.ToString(),
        _ => null
    };
}
