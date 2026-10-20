using System.Text.Json;
using System.Text.Json.Nodes;

namespace MarketplaceHub.Api.Settings;

// The shipping label settings are kept in the shape the panel uses (web shipping-label.ts,
// normalizeShippingLabelSettings), with the same bounds. Unknown fields are dropped, strings and numbers
// are cut to their ranges, and enumerations fall back to their default, so a hand-made request cannot
// store arbitrary JSON for every browser of the tenant to load.
public static class ShippingLabelSettingsSchema
{
    public const int MaximumBlocks = 40;
    private static readonly HashSet<string> BlockKinds = new(StringComparer.Ordinal) { "trackingBarcode", "address", "orderInfo", "packageBarcode", "sender", "custom" };
    private static readonly HashSet<string> Fields = new(StringComparer.Ordinal) { "trackingNumber", "packageNumber", "orderNumber", "customerName", "address", "cargoProvider", "senderName", "senderAddress", "customerEmail" };

    public static JsonObject Normalize(JsonElement value)
    {
        var a4LabelsPerPage = Number(value, "a4LabelsPerPage") is 2d or 4d ? (int)Number(value, "a4LabelsPerPage")!.Value : 1;
        var settings = new JsonObject
        {
            ["defaultFormat"] = Text(value, "defaultFormat") == "sticker" ? "sticker" : "a4",
            ["showA4Button"] = Flag(value, "showA4Button"),
            ["showStickerButton"] = Flag(value, "showStickerButton"),
            ["a4LabelsPerPage"] = a4LabelsPerPage,
            ["showCustomerPhone"] = Flag(value, "showCustomerPhone")
        };
        if (Text(value, "senderName") is { } senderName) settings["senderName"] = Cut(senderName, 120);
        if (Text(value, "senderAddress") is { } senderAddress) settings["senderAddress"] = Cut(senderAddress, 500);
        if (Number(value, "stickerWidthMm") is { } width) settings["stickerWidthMm"] = Clamp(width, 40, 300);
        if (Number(value, "stickerHeightMm") is { } height) settings["stickerHeightMm"] = Clamp(height, 40, 300);
        if (Number(value, "sectionGapMm") is { } gap) settings["sectionGapMm"] = Clamp(gap, 0, 20);
        if (value.TryGetProperty("layout", out var layout) && layout.ValueKind == JsonValueKind.Object)
        {
            var normalizedLayout = new JsonObject();
            foreach (var format in new[] { "a4", "sticker" })
            {
                if (layout.TryGetProperty(format, out var blocks) && blocks.ValueKind == JsonValueKind.Array)
                    normalizedLayout[format] = new JsonArray(blocks.EnumerateArray().Take(MaximumBlocks).Select(Block).Where(block => block is not null).ToArray());
            }
            settings["layout"] = normalizedLayout;
        }
        return settings;
    }

    // A block given by its kind name alone stays a name; the panel expands it.
    private static JsonNode? Block(JsonElement raw)
    {
        if (raw.ValueKind == JsonValueKind.String) return raw.GetString() is { } kindName && BlockKinds.Contains(kindName) && kindName != "custom" ? JsonValue.Create(kindName) : null;
        if (raw.ValueKind != JsonValueKind.Object) return null;
        var kind = Text(raw, "kind") is { } givenKind && BlockKinds.Contains(givenKind) ? givenKind : "custom";
        var block = new JsonObject { ["kind"] = kind, ["align"] = Text(raw, "align") is "center" or "right" ? Text(raw, "align") : "left" };
        if (Text(raw, "id") is { } id && id.Trim().Length > 0) block["id"] = Cut(id.Trim(), 80);
        if (Text(raw, "title") is { } title) block["title"] = Cut(title.Trim(), 120);
        if (Text(raw, "text") is { } text) block["text"] = Cut(text, 500);
        if (raw.TryGetProperty("fields", out var fields) && fields.ValueKind == JsonValueKind.Array)
            block["fields"] = new JsonArray(fields.EnumerateArray().Where(field => field.ValueKind == JsonValueKind.String).Select(field => field.GetString()!).Where(Fields.Contains).Distinct(StringComparer.Ordinal).Select(field => (JsonNode)JsonValue.Create(field)).ToArray());
        if (Number(raw, "fontSize") is { } fontSize) block["fontSize"] = Clamp(fontSize, 8, 72);
        if (Number(raw, "rotation") is { } rotation) block["rotation"] = Clamp(rotation, -360, 360);
        if (raw.TryGetProperty("position", out var position) && position.ValueKind == JsonValueKind.Object)
        {
            var box = new JsonObject();
            if (Number(position, "x") is { } x) box["x"] = Clamp(x, 0, 92);
            if (Number(position, "y") is { } y) box["y"] = Clamp(y, 0, 92);
            if (Number(position, "width") is { } boxWidth) box["width"] = Clamp(boxWidth, 8, 100);
            if (Number(position, "height") is { } boxHeight) box["height"] = Clamp(boxHeight, 5, 100);
            block["position"] = box;
        }
        return block;
    }

    private static string? Text(JsonElement value, string name) => value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    // Only an explicit false turns a switch off, as in the panel.
    private static bool Flag(JsonElement value, string name) => !(value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.False);
    private static double? Number(JsonElement value, string name) => value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out var number) && double.IsFinite(number) ? number : null;
    private static double Clamp(double value, double minimum, double maximum) => Math.Min(maximum, Math.Max(minimum, value));
    private static string Cut(string value, int length) => value.Length <= length ? value : value[..length];
}
