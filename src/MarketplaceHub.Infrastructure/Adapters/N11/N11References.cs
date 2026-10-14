using System.Globalization;
using System.Net;
using System.Text.Json;
using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Adapters.N11;

// Reference reads (phase 3). n11 returns the whole category tree in one
// request and a category's attributes together with their values, so every
// resource is a single page. Brands are not a separate list on n11: the brand
// is the category attribute "Marka" (id 1).
public sealed partial class N11HttpClient : IReferenceDataPort
{
    internal const string CategoryTree = "cdn/categories";

    internal static string CategoryAttributes(string categoryId) => $"cdn/category/{Uri.EscapeDataString(categoryId)}/attribute";

    public async Task<AdapterResult<AdapterPageResult<RemoteReferenceItem>>> ReadAsync(AdapterContext context, ReferenceResource resource, AdapterPageRequest page, CancellationToken cancellationToken)
    {
        var type = resource.ResourceType.Trim().ToUpperInvariant();
        if (type is not ("CATEGORIES" or "CATEGORY_ATTRIBUTES" or "ATTRIBUTE_VALUES"))
            return NotSupported<AdapterPageResult<RemoteReferenceItem>>("N11_REFERENCE_UNSUPPORTED", "N11'de ayrı marka listesi yok; marka, kategori özelliklerindeki \"Marka\" alanından seçilir.");
        var categoryId = type == "ATTRIBUTE_VALUES" ? resource.ParentExternalId?.Split('/')[0] : resource.ParentExternalId;
        var attributeId = type == "ATTRIBUTE_VALUES" ? resource.ParentExternalId?.Split('/').ElementAtOrDefault(1) : null;
        if (type != "CATEGORIES" && (string.IsNullOrWhiteSpace(categoryId) || type == "ATTRIBUTE_VALUES" && string.IsNullOrWhiteSpace(attributeId)))
            return Failure<AdapterPageResult<RemoteReferenceItem>>(AdapterErrorClass.Validation, "N11_REFERENCE_SCOPE_INVALID", "N11 kategori özellikleri için kategori, değerler için kategori/özellik kapsamı gerekir.", HttpStatusCode.UnprocessableEntity);

        var account = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (account is null) return CredentialMissing<AdapterPageResult<RemoteReferenceItem>>();
        var result = await SendAsync(account, HttpMethod.Get, type == "CATEGORIES" ? CategoryTree : CategoryAttributes(categoryId!.Trim()), cancellationToken);
        if (!result.IsSuccess) return AdapterResult<AdapterPageResult<RemoteReferenceItem>>.Failure(result.Error!, result.RateLimit);
        using var document = result.Value!;
        try
        {
            IReadOnlyList<RemoteReferenceItem> items = type switch
            {
                "CATEGORIES" => N11ReferenceMapper.Categories(document.RootElement),
                "CATEGORY_ATTRIBUTES" => N11ReferenceMapper.Attributes(document.RootElement, categoryId!.Trim()),
                _ => N11ReferenceMapper.Values(document.RootElement, categoryId!.Trim(), attributeId!.Trim())
            };
            return AdapterResult<AdapterPageResult<RemoteReferenceItem>>.Success(new(items, null, false, items.Count), result.RateLimit);
        }
        catch (JsonException)
        {
            return Failure<AdapterPageResult<RemoteReferenceItem>>(AdapterErrorClass.ContractViolation, "N11_REFERENCE_CONTRACT_INVALID", "N11 kategori yanıtı beklenen yapıda değil.", HttpStatusCode.BadGateway);
        }
    }
}

public static class N11ReferenceMapper
{
    // The tree comes as nested subCategories; a node whose subCategories is
    // null or empty is a leaf, the only level products can be listed on.
    public static IReadOnlyList<RemoteReferenceItem> Categories(JsonElement root)
    {
        var items = new List<RemoteReferenceItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Walk(JsonElement nodes, string? parentId, string parentPath, int depth)
        {
            foreach (var node in nodes.EnumerateArray())
            {
                var id = Text(node, "id");
                var name = Text(node, "name");
                if (id is null || name is null || !seen.Add(id)) continue;
                var path = parentPath.Length == 0 ? name : $"{parentPath} / {name}";
                var children = node.TryGetProperty("subCategories", out var sub) && sub.ValueKind == JsonValueKind.Array ? sub : default;
                var isLeaf = children.ValueKind != JsonValueKind.Array || children.GetArrayLength() == 0;
                items.Add(new("CATEGORIES", id, parentId, name, path, depth, isLeaf, true, Shallow(node)));
                if (!isLeaf) Walk(children, id, path, depth + 1);
            }
        }
        Walk(CategoryArray(root), null, "", 0);
        if (items.Count == 0) throw new JsonException("n11 category tree is empty.");
        return items;
    }

    public static IReadOnlyList<RemoteReferenceItem> Attributes(JsonElement root, string categoryId) =>
        AttributeArray(root)
            .Select(attribute => (attribute, id: Text(attribute, "attributeId") ?? Text(attribute, "id"), name: Text(attribute, "attributeName") ?? Text(attribute, "name")))
            .Where(x => x.id is not null && x.name is not null)
            .GroupBy(x => x.id!, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(x => Number(x.attribute, "attributeOrder") ?? int.MaxValue)
            .Select(x => new RemoteReferenceItem("CATEGORY_ATTRIBUTES", x.id!, categoryId, x.name!, x.name!, 0, true, true, Shallow(x.attribute),
                IsRequired: Bool(x.attribute, "isMandatory"),
                AllowsCustomValue: Bool(x.attribute, "isCustomValue"),
                AllowsMultipleValues: false))
            .ToList();

    public static IReadOnlyList<RemoteReferenceItem> Values(JsonElement root, string categoryId, string attributeId)
    {
        var scope = $"{categoryId}/{attributeId}";
        var attribute = AttributeArray(root).FirstOrDefault(x => (Text(x, "attributeId") ?? Text(x, "id")) == attributeId);
        if (attribute.ValueKind != JsonValueKind.Object || !attribute.TryGetProperty("attributeValues", out var values) || values.ValueKind != JsonValueKind.Array) return [];
        return values.EnumerateArray()
            .Select(value => (value, id: Text(value, "id"), name: Text(value, "value")))
            .Where(x => x.id is not null && x.name is not null)
            .GroupBy(x => x.id!, StringComparer.Ordinal)
            .Select(group => group.First())
            .Select(x => new RemoteReferenceItem("ATTRIBUTE_VALUES", x.id!, scope, x.name!, x.name!, 0, true, true, x.value.GetRawText()))
            .ToList();
    }

    private static JsonElement CategoryArray(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array) return root;
        foreach (var name in new[] { "categories", "content", "data", "subCategories" })
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array) return array;
        throw new JsonException("n11 category tree has no category array.");
    }

    private static IEnumerable<JsonElement> AttributeArray(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("categoryAttributes", out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object)
            : throw new JsonException("n11 category attribute response has no categoryAttributes array.");

    // Attribute and category payloads are hashed for change detection; the
    // nested children and value lists are left out so a new brand value does
    // not count as a change of the attribute itself.
    private static string Shallow(JsonElement element)
    {
        var copy = System.Text.Json.Nodes.JsonNode.Parse(element.GetRawText())!.AsObject();
        copy.Remove("subCategories");
        copy.Remove("attributeValues");
        return copy.ToJsonString();
    }

    private static string? Text(JsonElement element, string property)
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

    private static bool? Bool(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;

    private static int? Number(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;
}
