using System.Text.Json;
using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Adapters.Trendyol.Mapping;

public static partial class TrendyolJsonMapper
{
    public static IReadOnlyList<RemoteReferenceItem> References(string resourceType, string json, string? parentExternalId)
    {
        using var document = JsonDocument.Parse(json); var root = document.RootElement; var rows = new List<RemoteReferenceItem>();
        if (resourceType == "BRANDS")
        {
            var array = root.TryGetProperty("brands", out var brands) ? brands : root;
            if (array.ValueKind == JsonValueKind.Array) foreach (var item in array.EnumerateArray()) rows.Add(Reference(resourceType, item, "id", "name", null, 0, true));
        }
        else if (resourceType == "CATEGORY_ATTRIBUTES" && root.TryGetProperty("categoryAttributes", out var attributes) && attributes.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in attributes.EnumerateArray()) if (item.TryGetProperty("attribute", out var attribute)) rows.Add(new(resourceType, Text(attribute, "id"), parentExternalId, Text(attribute, "name"), Text(attribute, "name"), 0, true, true, item.GetRawText(), Bool(item, "required") || Bool(item, "isRequired"), Bool(item, "allowCustom") || Bool(item, "allowsCustomValue"), Bool(item, "allowMultipleAttributeValues") || Bool(item, "allowsMultipleValues")));
        }
        else if (resourceType == "ATTRIBUTE_VALUES")
        {
            foreach (var item in Content(root)) rows.Add(Reference(resourceType, item, "attributeValueId", "attributeValue", parentExternalId, 0, true));
        }
        else if (resourceType == "CATEGORIES" && root.TryGetProperty("categories", out var categories) && categories.ValueKind == JsonValueKind.Array) FlattenCategories(categories, null, "", 0, rows);
        return rows;
    }

    private static void FlattenCategories(JsonElement categories, string? parentId, string parentPath, int depth, List<RemoteReferenceItem> rows)
    {
        foreach (var item in categories.EnumerateArray())
        {
            var id = Text(item, "id"); var name = Text(item, "name"); var path = parentPath.Length == 0 ? name : $"{parentPath} / {name}"; var hasChildren = item.TryGetProperty("subCategories", out var children) && children.ValueKind == JsonValueKind.Array && children.GetArrayLength() > 0;
            rows.Add(new("CATEGORIES", id, parentId, name, path, depth, !hasChildren, true, item.GetRawText())); if (hasChildren) FlattenCategories(children, id, path, depth + 1, rows);
        }
    }

    private static RemoteReferenceItem Reference(string type, JsonElement item, string idName, string nameName, string? parent, int depth, bool leaf) { var name = Text(item, nameName); return new(type, Text(item, idName), parent, name, name, depth, leaf, true, item.GetRawText()); }
}
