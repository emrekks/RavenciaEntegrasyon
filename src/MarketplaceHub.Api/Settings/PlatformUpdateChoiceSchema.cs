using System.Text.Json;
using System.Text.Json.Nodes;
using MarketplaceHub.Application;

namespace MarketplaceHub.Api.Settings;

// The editor's "Platformlarda Güncelle" choice that Hızlı Güncelle sends (web platform-update-fields.ts,
// platformUpdateChoiceRecord): the picked fields, the open field groups, the fields and groups the window knew when it
// saved (a field added since starts picked), and the picked stores (null: none saved yet, the product's stores go).
// Kept per tenant so every device sends the same choice. Anything else is refused, never stored.
public static class PlatformUpdateChoiceSchema
{
    public const int MaximumJsonCharacters = 8_192;
    public const int MaximumConnections = 64;
    private const int MaximumKnownEntries = 40;
    private const int MaximumKnownEntryLength = 40;
    // The product update fields of the server's own table, and the price and stock parts of the price/stock send.
    private static readonly IReadOnlySet<string> Fields = new HashSet<string>([.. ProductUpdateFields.Known, "PRICE", "STOCK"], StringComparer.Ordinal);
    private static readonly IReadOnlySet<string> Groups = new HashSet<string>(["product", "shipping", "sales"], StringComparer.Ordinal);

    public static bool TryNormalize(JsonElement value, out JsonObject normalized, out string error)
    {
        normalized = new JsonObject();
        error = string.Empty;
        if (value.ValueKind != JsonValueKind.Object)
        {
            error = "Hızlı Güncelle ayarı JSON nesnesi olmalıdır.";
            return false;
        }

        if (!TryList(value, "fields", required: true, item => Fields.Contains(item), out var fields))
        {
            error = "Gönderilecek bilgiler bilinen alan kodlarından oluşmalıdır.";
            return false;
        }
        if (!TryList(value, "openGroups", required: false, item => Groups.Contains(item), out var openGroups))
        {
            error = "Açık gruplar bilinen grup adlarından oluşmalıdır.";
            return false;
        }
        // What the window knew may name a field a later panel retired (ORIGIN): checked for shape only.
        if (!TryList(value, "knownFields", required: false, item => IsCode(item, upper: true), out var knownFields)
            || !TryList(value, "knownGroups", required: false, item => IsCode(item, upper: false), out var knownGroups)
            || knownFields.Count > MaximumKnownEntries || knownGroups.Count > MaximumKnownEntries)
        {
            error = "Bilinen alan ve grup listeleri geçersiz.";
            return false;
        }

        JsonArray? connectionIds = null;
        if (value.TryGetProperty("connectionIds", out var connections) && connections.ValueKind != JsonValueKind.Null)
        {
            if (connections.ValueKind != JsonValueKind.Array || connections.GetArrayLength() > MaximumConnections)
            {
                error = $"Mağaza listesi en çok {MaximumConnections} bağlantı kimliği içerebilir.";
                return false;
            }
            var ids = new List<string>();
            foreach (var item in connections.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String || !Guid.TryParseExact(item.GetString(), "D", out var id) || id == Guid.Empty)
                {
                    error = "Mağaza listesi geçerli bağlantı kimliklerinden oluşmalıdır.";
                    return false;
                }
                var text = id.ToString("D");
                if (!ids.Contains(text)) ids.Add(text);
            }
            connectionIds = new JsonArray(ids.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray());
        }

        normalized = new JsonObject
        {
            ["fields"] = Array(fields),
            ["openGroups"] = Array(openGroups),
            ["knownFields"] = Array(knownFields),
            ["knownGroups"] = Array(knownGroups),
            ["connectionIds"] = connectionIds
        };
        return true;
    }

    private static bool TryList(JsonElement value, string name, bool required, Func<string, bool> accepts, out List<string> items)
    {
        items = [];
        if (!value.TryGetProperty(name, out var list) || list.ValueKind == JsonValueKind.Null) return !required;
        if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() > MaximumKnownEntries) return false;
        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || item.GetString() is not { } text || !accepts(text)) return false;
            if (!items.Contains(text, StringComparer.Ordinal)) items.Add(text);
        }
        return true;
    }

    private static bool IsCode(string value, bool upper) =>
        value.Length is > 0 and <= MaximumKnownEntryLength
        && value.All(character => character == '_' || (upper ? character is >= 'A' and <= 'Z' : character is >= 'a' and <= 'z'));

    private static JsonArray Array(List<string> items) => new(items.Select(item => (JsonNode?)JsonValue.Create(item)).ToArray());
}
