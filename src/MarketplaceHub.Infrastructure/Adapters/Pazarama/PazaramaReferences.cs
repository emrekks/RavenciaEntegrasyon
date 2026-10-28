using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Adapters.Pazarama;

// Reference reads (phase 3). Pazarama returns the whole category tree as one flat list and a
// category's attributes together with their values, so those are single pages. Brands are paged
// by number; the cursor carries the page number and the first brand id of page one so a server
// that ignores the page number (and keeps answering page one) cannot loop the scan forever.
// BRAND_SEARCH (scope = a brand name) reads the same list filtered by name, so a panel brand the
// paged scan never reached still lands in the brand snapshot; its rows come back as BRANDS rows.
public sealed partial class PazaramaHttpClient : IReferenceDataPort
{
    internal const string CategoryTree = "category/getCategoryTree";
    internal const int BrandPageSize = 500;
    internal const int BrandSearchSize = 100;

    // The attribute list and every attribute value list of a category come from the same Pazarama call;
    // keeping the last answer for a short while turns the repeated reads of one import into one request.
    private static readonly ConcurrentDictionary<(Guid ConnectionId, string CategoryId), (DateTimeOffset At, string Json)> CategoryDetails = new();
    private static readonly TimeSpan CategoryDetailLifetime = TimeSpan.FromMinutes(2);

    internal static string CategoryAttributes(string categoryId) => $"category/getCategoryWithAttributes?Id={Uri.EscapeDataString(categoryId)}";

    internal static string BrandPage(int page, int size) => $"{Brands}?Page={page.ToString(CultureInfo.InvariantCulture)}&Size={size.ToString(CultureInfo.InvariantCulture)}";

    internal static string BrandSearch(string name, int size) => $"{Brands}?name={Uri.EscapeDataString(name.Trim())}&Page=1&Size={size.ToString(CultureInfo.InvariantCulture)}";

    public async Task<AdapterResult<AdapterPageResult<RemoteReferenceItem>>> ReadAsync(AdapterContext context, ReferenceResource resource, AdapterPageRequest page, CancellationToken cancellationToken)
    {
        var type = resource.ResourceType.Trim().ToUpperInvariant();
        if (type is not ("CATEGORIES" or "CATEGORY_ATTRIBUTES" or "ATTRIBUTE_VALUES" or "BRANDS" or "BRAND_SEARCH"))
            return NotSupported<AdapterPageResult<RemoteReferenceItem>>("PAZARAMA_REFERENCE_UNSUPPORTED", $"Pazarama için {resource.ResourceType} referansı desteklenmiyor.");
        var categoryId = type == "ATTRIBUTE_VALUES" ? resource.ParentExternalId?.Split('/')[0] : resource.ParentExternalId;
        var attributeId = type == "ATTRIBUTE_VALUES" ? resource.ParentExternalId?.Split('/').ElementAtOrDefault(1) : null;
        if (type is "CATEGORY_ATTRIBUTES" or "ATTRIBUTE_VALUES" && (string.IsNullOrWhiteSpace(categoryId) || type == "ATTRIBUTE_VALUES" && string.IsNullOrWhiteSpace(attributeId)))
            return Failure<AdapterPageResult<RemoteReferenceItem>>(AdapterErrorClass.Validation, "PAZARAMA_REFERENCE_SCOPE_INVALID", "Pazarama kategori özellikleri için kategori, değerler için kategori/özellik kapsamı gerekir.", HttpStatusCode.UnprocessableEntity);
        if (type == "BRAND_SEARCH" && string.IsNullOrWhiteSpace(resource.ParentExternalId))
            return Failure<AdapterPageResult<RemoteReferenceItem>>(AdapterErrorClass.Validation, "PAZARAMA_REFERENCE_SCOPE_INVALID", "Pazarama marka araması için marka adı gerekir.", HttpStatusCode.UnprocessableEntity);

        var account = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (account is null) return CredentialMissing<AdapterPageResult<RemoteReferenceItem>>();
        var (brandPage, firstBrandId) = PazaramaReferenceMapper.BrandCursor(page.Cursor);
        var path = type switch
        {
            "CATEGORIES" => CategoryTree,
            "BRANDS" => BrandPage(brandPage, BrandPageSize),
            "BRAND_SEARCH" => BrandSearch(resource.ParentExternalId!, BrandSearchSize),
            _ => CategoryAttributes(categoryId!.Trim())
        };
        var detailKey = (context.ConnectionId, categoryId?.Trim().ToLowerInvariant() ?? "");
        var now = timeProvider.GetUtcNow();
        if (type is "CATEGORY_ATTRIBUTES" or "ATTRIBUTE_VALUES" && CategoryDetails.TryGetValue(detailKey, out var cached) && now - cached.At < CategoryDetailLifetime)
        {
            using var cachedDocument = JsonDocument.Parse(cached.Json);
            return MapReference(type, cachedDocument.RootElement, categoryId, attributeId, brandPage, firstBrandId, null);
        }
        var result = await SendAsync(account, HttpMethod.Get, path, cancellationToken);
        if (!result.IsSuccess) return AdapterResult<AdapterPageResult<RemoteReferenceItem>>.Failure(result.Error!, result.RateLimit);
        using var document = result.Value!;
        try
        {
            if (!Data(document.RootElement, out var data))
                throw new JsonException("Pazarama reference response has no data.");
            if (type is "CATEGORY_ATTRIBUTES" or "ATTRIBUTE_VALUES")
            {
                if (CategoryDetails.Count > 200) CategoryDetails.Clear();
                CategoryDetails[detailKey] = (now, data.GetRawText());
            }
            return MapReference(type, data, categoryId, attributeId, brandPage, firstBrandId, result.RateLimit);
        }
        catch (JsonException)
        {
            return Failure<AdapterPageResult<RemoteReferenceItem>>(AdapterErrorClass.ContractViolation, "PAZARAMA_REFERENCE_CONTRACT_INVALID", "Pazarama kategori/marka yanıtı beklenen yapıda değil.", HttpStatusCode.BadGateway);
        }
    }

    private static AdapterResult<AdapterPageResult<RemoteReferenceItem>> MapReference(string type, JsonElement data, string? categoryId, string? attributeId, int brandPage, string? firstBrandId, RateLimitMetadata? rateLimit)
    {
        try
        {
            if (type == "BRAND_SEARCH")
            {
                var found = PazaramaReferenceMapper.BrandMatches(data);
                return AdapterResult<AdapterPageResult<RemoteReferenceItem>>.Success(new(found, null, false, found.Count), rateLimit);
            }
            if (type == "BRANDS")
            {
                var (items, next) = PazaramaReferenceMapper.Brands(data, brandPage, firstBrandId, BrandPageSize);
                return AdapterResult<AdapterPageResult<RemoteReferenceItem>>.Success(new(items, next, next is not null), rateLimit);
            }
            IReadOnlyList<RemoteReferenceItem> mapped = type switch
            {
                "CATEGORIES" => PazaramaReferenceMapper.Categories(data),
                "CATEGORY_ATTRIBUTES" => PazaramaReferenceMapper.Attributes(data, categoryId!.Trim()),
                _ => PazaramaReferenceMapper.Values(data, categoryId!.Trim(), attributeId!.Trim())
            };
            return AdapterResult<AdapterPageResult<RemoteReferenceItem>>.Success(new(mapped, null, false, mapped.Count), rateLimit);
        }
        catch (JsonException)
        {
            return Failure<AdapterPageResult<RemoteReferenceItem>>(AdapterErrorClass.ContractViolation, "PAZARAMA_REFERENCE_CONTRACT_INVALID", "Pazarama kategori/marka yanıtı beklenen yapıda değil.", HttpStatusCode.BadGateway);
        }
    }
}

public static class PazaramaReferenceMapper
{
    private const string EmptyGuid = "00000000-0000-0000-0000-000000000000";

    // Appended to a BRANDS snapshot's source version when every active panel brand was looked up by
    // name and every answer was name-filtered; see MarketplaceJobProcessor.SyncBrandReferences.
    public const string BrandNameLookupMarker = "+name-lookup";

    // The tree is a flat list with parentId. A category whose parent is missing, empty or not in
    // the list becomes a root, so the stored tree never points at a parent that does not exist.
    public static IReadOnlyList<RemoteReferenceItem> Categories(JsonElement data)
    {
        var rows = Rows(data, "categories", "items", "data").ToList();
        var byId = new Dictionary<string, (JsonElement node, string name, string? parent)>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in rows)
        {
            var id = Text(node, "id");
            var name = Text(node, "name") ?? Text(node, "displayName");
            if (id is null || name is null || id == EmptyGuid || byId.ContainsKey(id)) continue;
            var parent = Text(node, "parentId");
            byId[id] = (node, name, parent is null || parent == EmptyGuid ? null : parent);
        }
        if (byId.Count == 0) throw new JsonException("Pazarama category tree is empty.");

        var items = new List<RemoteReferenceItem>(byId.Count);
        foreach (var (id, entry) in byId)
        {
            var parent = entry.parent is not null && byId.ContainsKey(entry.parent) && !string.Equals(entry.parent, id, StringComparison.OrdinalIgnoreCase) ? entry.parent : null;
            var chain = new List<string> { entry.name };
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { id };
            for (var cursor = parent; cursor is not null && seen.Add(cursor); cursor = byId[cursor].parent is { } up && byId.ContainsKey(up) ? up : null)
                chain.Insert(0, byId[cursor].name);
            var leaf = Bool(entry.node, "leaf") ?? Bool(entry.node, "isLeaf") ?? false;
            items.Add(new("CATEGORIES", id, parent, entry.name, string.Join(" / ", chain), chain.Count - 1, leaf, true, Shallow(entry.node)));
        }
        // Parents first, so a reader that inserts in order never meets an unknown parent.
        return items.OrderBy(x => x.Depth).ThenBy(x => x.Path, StringComparer.Ordinal).ToList();
    }

    public static IReadOnlyList<RemoteReferenceItem> Attributes(JsonElement data, string categoryId) =>
        AttributeRows(data)
            .Select(attribute => (attribute, id: Text(attribute, "id"), name: Text(attribute, "name") ?? Text(attribute, "displayName")))
            .Where(x => x.id is not null && x.name is not null)
            .GroupBy(x => x.id!, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Select(x => new RemoteReferenceItem("CATEGORY_ATTRIBUTES", x.id!, categoryId, x.name!, x.name!, 0, true, true, Shallow(x.attribute),
                IsRequired: Bool(x.attribute, "isRequired"),
                // Pazarama takes attribute values only by id from its own list.
                AllowsCustomValue: false,
                AllowsMultipleValues: false))
            .ToList();

    public static IReadOnlyList<RemoteReferenceItem> Values(JsonElement data, string categoryId, string attributeId)
    {
        var scope = $"{categoryId}/{attributeId}";
        var attribute = AttributeRows(data).FirstOrDefault(x => string.Equals(Text(x, "id"), attributeId, StringComparison.OrdinalIgnoreCase));
        if (attribute.ValueKind != JsonValueKind.Object || !attribute.TryGetProperty("attributeValues", out var values) || values.ValueKind != JsonValueKind.Array) return [];
        return values.EnumerateArray()
            .Select(value => (value, id: Text(value, "id"), name: Text(value, "value") ?? Text(value, "name")))
            .Where(x => x.id is not null && x.name is not null)
            .GroupBy(x => x.id!, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Select(x => new RemoteReferenceItem("ATTRIBUTE_VALUES", x.id!, scope, x.name!, x.name!, 0, true, true, x.value.GetRawText()))
            .ToList();
    }

    // Cursor "{page}:{firstIdOfPageOne}"; an empty cursor is page one.
    public static (int Page, string? FirstId) BrandCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return (1, null);
        var parts = cursor.Split(':', 2);
        return (int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var page) && page > 0 ? page : 1, parts.Length > 1 && parts[1].Length > 0 ? parts[1] : null);
    }

    // Walks until an empty or short page. A page that starts with the id page one started with
    // means the server ignored the page number; that page is dropped and the scan ends.
    public static (IReadOnlyList<RemoteReferenceItem> Items, string? NextCursor) Brands(JsonElement data, int page, string? firstIdOfPageOne, int size)
    {
        var items = Rows(data, "brands", "items", "data")
            .Select(brand => (brand, id: Text(brand, "id"), name: Text(brand, "name")))
            .Where(x => x.id is not null && x.name is not null)
            .ToList();
        if (items.Count == 0) return ([], null);
        if (page > 1 && string.Equals(items[0].id, firstIdOfPageOne, StringComparison.OrdinalIgnoreCase)) return ([], null);
        var firstId = page == 1 ? items[0].id : firstIdOfPageOne;
        var mapped = items
            .GroupBy(x => x.id!, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Select(x => new RemoteReferenceItem("BRANDS", x.id!, null, x.name!, x.name!, 0, true, true, x.brand.GetRawText()))
            .ToList();
        var next = items.Count < size ? null : $"{(page + 1).ToString(CultureInfo.InvariantCulture)}:{firstId}";
        return (mapped, next);
    }

    // One answer of the name-filtered brand list, read as BRANDS rows so they merge into the brand snapshot.
    public static IReadOnlyList<RemoteReferenceItem> BrandMatches(JsonElement data) =>
        Rows(data, "brands", "items", "data")
            .Select(brand => (brand, id: Text(brand, "id"), name: Text(brand, "name")))
            .Where(x => x.id is not null && x.name is not null && x.id != EmptyGuid)
            .GroupBy(x => x.id!, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Select(x => new RemoteReferenceItem("BRANDS", x.id!, null, x.name!, x.name!, 0, true, true, x.brand.GetRawText()))
            .ToList();

    // Pazarama answers a name lookup with the brands whose name contains the text. The lookup only
    // proves a brand is missing when every row really carries the text: a server that ignored the
    // filter answers page one of the whole list, and that proves nothing about the name.
    public static bool BrandSearchHonoured(string name, IReadOnlyList<RemoteReferenceItem> rows)
    {
        var text = FoldBrandName(name);
        return rows.All(row => FoldBrandName(row.Name).Contains(text, StringComparison.Ordinal));
    }

    // Brand names compared the Turkish way and loosely on the dotted/dotless i, since one side often
    // writes "AKMAVI" for "AKMAVİ": a loose match can only keep a "not found on Pazarama" note away.
    public static string FoldBrandName(string name) =>
        string.Join(' ', name.Trim().ToUpper(CultureInfo.GetCultureInfo("tr-TR")).Replace('İ', 'I').Split(' ', StringSplitOptions.RemoveEmptyEntries));

    public static bool SameBrandName(string left, string right) => string.Equals(FoldBrandName(left), FoldBrandName(right), StringComparison.Ordinal);

    private static IEnumerable<JsonElement> AttributeRows(JsonElement data)
    {
        // A list holding the one requested category is read the same way as the category itself.
        if (data.ValueKind == JsonValueKind.Array) return data.EnumerateArray().SelectMany(AttributeRows);
        if (data.ValueKind == JsonValueKind.Object)
            foreach (var name in new[] { "attributes", "categoryAttributes" })
                if (data.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array)
                    return array.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object);
        throw new JsonException("Pazarama category response has no attributes array.");
    }

    private static IEnumerable<JsonElement> Rows(JsonElement data, params string[] names)
    {
        if (data.ValueKind == JsonValueKind.Array) return data.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object);
        if (data.ValueKind == JsonValueKind.Object)
            foreach (var name in names)
                if (data.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array)
                    return array.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object);
        throw new JsonException("Pazarama reference response has no list.");
    }

    // Hashed for change detection; nested lists are left out so a new brand value or a changed
    // breadcrumb does not count as a change of the category or attribute itself.
    private static string Shallow(JsonElement element)
    {
        var copy = JsonNode.Parse(element.GetRawText())!.AsObject();
        copy.Remove("attributeValues");
        copy.Remove("parentCategories");
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
}
