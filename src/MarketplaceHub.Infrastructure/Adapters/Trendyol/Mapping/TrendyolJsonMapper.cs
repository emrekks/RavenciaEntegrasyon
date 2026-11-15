using System.Globalization;
using System.Text.Json;

namespace MarketplaceHub.Infrastructure.Adapters.Trendyol.Mapping;

public static partial class TrendyolJsonMapper
{
    // Product and reference pages: the content list, or none when the page sends no list. A page that sends no list but
    // counts elements (totalElements > 0), or sends a content that is not a list, contradicts itself: before 6 Nov 2026
    // it read as an empty page, the catalog scan ended there and its watermark moved past products it never saw.
    private static IEnumerable<JsonElement> Content(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return [];
        if (root.TryGetProperty("content", out var content))
        {
            if (content.ValueKind == JsonValueKind.Array) return content.EnumerateArray();
            if (content.ValueKind != JsonValueKind.Null) throw new JsonException($"Trendyol page content is {content.ValueKind}, not a list.");
        }
        return NullableLong(root, "totalElements", "totalCount", "totalResults") is > 0
            ? throw new JsonException("Trendyol page counts elements but has no content list.")
            : [];
    }
    private static string ObjectSnapshot(JsonElement value, string field) { if (value.TryGetProperty(field, out var item) && item.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)) return item.GetRawText(); return "{}"; }
    private static string CustomerSnapshot(JsonElement package)
    {
        var map = new Dictionary<string, JsonElement>();
        if (package.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in package.EnumerateObject())
                map[prop.Name] = prop.Value.Clone();
        }
        if ((!map.ContainsKey("customerFirstName") || string.IsNullOrWhiteSpace(map["customerFirstName"].ToString())) && package.TryGetProperty("shipmentAddress", out var sa))
        {
            if (sa.TryGetProperty("firstName", out var fn)) map["customerFirstName"] = fn.Clone();
            if (sa.TryGetProperty("lastName", out var ln)) map["customerLastName"] = ln.Clone();
        }
        if ((!map.ContainsKey("customerFirstName") || string.IsNullOrWhiteSpace(map["customerFirstName"].ToString())) && package.TryGetProperty("invoiceAddress", out var ia))
        {
            if (ia.TryGetProperty("firstName", out var fn)) map["customerFirstName"] = fn.Clone();
            if (ia.TryGetProperty("lastName", out var ln)) map["customerLastName"] = ln.Clone();
        }
        return JsonSerializer.Serialize(map);
    }
    // Text, NullText, Decimal, TryDecimal, Bool and BoolAny stay Trendyol's own, not RemoteFields': text is any non-null
    // value as written (untrimmed, true/false and objects as their JSON), a missing text or amount is "" or 0 for the
    // mapper's non-null fields, a "true"/1 reads as true, and a non-object element throws instead of reading as
    // missing. The golden contract fixtures pin these readings. Whole numbers go through RemoteFields.Long.
    private static string Text(JsonElement value, params string[] names) => NullText(value, names) ?? "";
    private static string? NullText(JsonElement value, params string[] names) { foreach (var name in names) if (value.TryGetProperty(name, out var item) && item.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)) return item.ToString(); return null; }
    private static string? NestedText(JsonElement value, string objectName, params string[] names) => value.TryGetProperty(objectName, out var nested) && nested.ValueKind == JsonValueKind.Object ? NullText(nested, names) : null;
    private static string? ScalarText(JsonElement value, params string[] names)
    {
        foreach (var name in names)
        {
            if (!value.TryGetProperty(name, out var item)) continue;
            if (item.ValueKind is JsonValueKind.String or JsonValueKind.Number) return item.ToString();
            if (item.ValueKind == JsonValueKind.Object && string.Equals(name, "cargoProvider", StringComparison.OrdinalIgnoreCase))
            {
                var provider = NullText(item, "name", "code", "label");
                if (!string.IsNullOrWhiteSpace(provider)) return provider;
            }
        }
        return null;
    }
    private static decimal Decimal(JsonElement value, params string[] names) => TryDecimal(value, out var result, names) ? result : 0;
    private static bool TryDecimal(JsonElement value, out decimal result, params string[] names)
    {
        foreach (var name in names)
            // O5: a text amount is read dot-decimal only; "1234,50" stays unknown instead of becoming 123450.
            if (value.TryGetProperty(name, out var item) && RemoteJson.Decimal(item) is { } number) { result = number; return true; }
        result = 0;
        return false;
    }
    private static decimal? NestedDecimalNullable(JsonElement value, string objectName, params string[] names) => value.TryGetProperty(objectName, out var nested) && nested.ValueKind == JsonValueKind.Object ? DecimalNullable(nested, names) : null;
    private static decimal? DecimalNullable(JsonElement value, params string[] names)
    {
        foreach (var name in names)
        {
            if (!value.TryGetProperty(name, out var item)) continue;
            if (item.ValueKind == JsonValueKind.Object)
            {
                var nested = DecimalNullable(item, name is "price" ? "salePrice" : "listPrice");
                if (nested is not null) return nested;
            }
            else if (RemoteJson.Decimal(item) is { } result) return result;
        }
        return null;
    }
    private static long Long(JsonElement value, string name) => RemoteFields.Long(value, name) ?? 0;
    private static long? NullableLong(JsonElement value, params string[] names)
    {
        foreach (var name in names)
            if (RemoteFields.Long(value, name) is { } result) return result;
        return null;
    }
    private static bool Bool(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var item)) return false;
        return item.ValueKind == JsonValueKind.True
            || item.ValueKind == JsonValueKind.String && bool.TryParse(item.GetString(), out var parsed) && parsed
            || item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var number) && number != 0;
    }

    private static bool? BoolAny(JsonElement value, params string[] names)
    {
        foreach (var name in names)
        {
            if (!value.TryGetProperty(name, out var item)) continue;
            if (item.ValueKind == JsonValueKind.True) return true;
            if (item.ValueKind == JsonValueKind.False) return false;
            if (bool.TryParse(item.ToString(), out var parsed)) return parsed;
        }
        return null;
    }
    // orderDate is Trendyol's "GMT+3" epoch: the Turkey wall-clock time written as if it were UTC, three hours ahead of
    // the real instant (C13; proven on live 2 Nov 2026: order 11776722829 arrived 19:01:53Z with orderDate 22:01:41Z).
    // lastModifiedDate, createdDate, claimDate (documented as GMT) and agreedDeliveryDate (23:59 Istanbul) are real
    // UTC epochs and are read as they are.
    // An epoch outside the DateTimeOffset range reads as no date (ADAPTOR-TY-HB-5): its package is refused or falls back
    // on its own, instead of an ArgumentOutOfRangeException stopping the whole page.
    private static DateTimeOffset? OrderDate(JsonElement value)
    {
        if (!value.TryGetProperty("orderDate", out var item) || item.ValueKind != JsonValueKind.Number || !item.TryGetInt64(out var milliseconds)) return null;
        try { return TurkeyTime.FromTurkeyWallClockEpochMilliseconds(milliseconds); }
        catch (ArgumentOutOfRangeException) { return null; }
    }
    // D12: the epoch may come as text ("1762304400000"), as Trendyol sends other numbers; it is read the same way instead
    // of the field being taken as missing (and the package dated by its order).
    private static DateTimeOffset? Instant(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var item)) return null;
        long milliseconds;
        if (item.ValueKind == JsonValueKind.Number) { if (!item.TryGetInt64(out milliseconds)) return null; }
        else if (item.ValueKind != JsonValueKind.String || !long.TryParse(item.GetString()?.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out milliseconds)) return null;
        try { return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds); }
        catch (ArgumentOutOfRangeException) { return null; }
    }
    private static DateTimeOffset? FlexibleInstant(JsonElement value, params string[] names)
    {
        foreach (var name in names)
        {
            if (!value.TryGetProperty(name, out var item) || item.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) continue;
            if (item.ValueKind == JsonValueKind.Number && item.TryGetInt64(out var milliseconds))
            {
                try { return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds); } catch (ArgumentOutOfRangeException) { }
            }
            // D12: a text date with an offset ("…+03:00") is stored as its UTC instant; Npgsql timestamptz takes offset 0 only.
            if (DateTimeOffset.TryParse(item.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)) return parsed.ToUniversalTime();
        }
        return null;
    }
}
