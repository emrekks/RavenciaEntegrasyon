using System.Net;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Adapters.PttAvm;

// Reference reads (phase 3). GetCategoryTree(parent_id=0) answers PTT AVM's whole category tree in one call, as
// nested Category elements with their children. PTT AVM offers no category attribute, attribute value or brand
// service: CATEGORIES is read, attribute reads answer an empty list and brands are refused. A category without
// children is a leaf (where products are listed).
public sealed partial class PttAvmHttpClient : IReferenceDataPort
{
    internal const string CategoryTree = "GetCategoryTree";

    public async Task<AdapterResult<AdapterPageResult<RemoteReferenceItem>>> ReadAsync(AdapterContext context, ReferenceResource resource, AdapterPageRequest page, CancellationToken cancellationToken)
    {
        var type = resource.ResourceType.Trim().ToUpperInvariant();
        // The product import asks for the attributes of every imported category; PTT AVM has none to give.
        if (type is "CATEGORY_ATTRIBUTES" or "ATTRIBUTE_VALUES")
            return AdapterResult<AdapterPageResult<RemoteReferenceItem>>.Success(new([], null, false, 0));
        if (type != "CATEGORIES")
            return NotSupported<AdapterPageResult<RemoteReferenceItem>>("PTTAVM_REFERENCE_UNSUPPORTED", $"PTT AVM {resource.ResourceType} referansı sağlamıyor; yalnız kategori ağacı okunur.");
        var load = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        if (load.Account is null) return CredentialFailure<AdapterPageResult<RemoteReferenceItem>>(load);
        return await ReadCategoriesAsync(load.Account, cancellationToken);
    }

    internal async Task<AdapterResult<AdapterPageResult<RemoteReferenceItem>>> ReadCategoriesAsync(PttAvmRequestContext account, CancellationToken cancellationToken)
    {
        var result = await SendSoapAsync(account, CategoryTree, [new("parent_id", "0")], cancellationToken);
        if (!result.IsSuccess) return AdapterResult<AdapterPageResult<RemoteReferenceItem>>.Failure(result.Error!, result.RateLimit);
        if (MainCategoryError(result.Value!, "category_tree") is { } error)
            return Failure<AdapterPageResult<RemoteReferenceItem>>(AdapterErrorClass.ContractViolation, "PTTAVM_REFERENCE_CONTRACT_INVALID", $"PTT AVM kategori ağacı okunamadı ({error}).", HttpStatusCode.BadGateway);
        var items = PttAvmReferenceMapper.Categories(result.Value!);
        if (items.Count == 0)
            return Failure<AdapterPageResult<RemoteReferenceItem>>(AdapterErrorClass.ContractViolation, "PTTAVM_REFERENCE_CONTRACT_INVALID", "PTT AVM kategori ağacı boş döndü.", HttpStatusCode.BadGateway);
        return AdapterResult<AdapterPageResult<RemoteReferenceItem>>.Success(new(items, null, false, items.Count), result.RateLimit);
    }
}

public static class PttAvmReferenceMapper
{
    // Walks the nested tree; a repeated id keeps its first place, so a malformed answer cannot loop or duplicate.
    public static IReadOnlyList<RemoteReferenceItem> Categories(XElement result)
    {
        var tree = result.Elements().FirstOrDefault(x => x.Name.LocalName == "category_tree");
        var items = new List<RemoteReferenceItem>();
        if (tree is null || PttAvmHttpClient.IsNil(tree)) return items;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in Children(tree)) Walk(root, null, [], items, seen);
        // Parents first, so a reader that inserts in order never meets an unknown parent.
        return items.OrderBy(x => x.Depth).ThenBy(x => x.Path, StringComparer.Ordinal).ToList();
    }

    private static void Walk(XElement node, string? parentId, IReadOnlyList<string> parentPath, List<RemoteReferenceItem> items, HashSet<string> seen)
    {
        var id = PttAvmHttpClient.ContractValue(node, "id");
        var name = PttAvmHttpClient.ContractValue(node, "name");
        if (id is null || name is null || id == "0" || !seen.Add(id)) return;
        var path = parentPath.Append(name).ToList();
        var childrenElement = node.Elements().FirstOrDefault(x => x.Name.LocalName == "children");
        var children = childrenElement is null || PttAvmHttpClient.IsNil(childrenElement) ? [] : Children(childrenElement).ToList();
        var raw = new JsonObject { ["id"] = id, ["name"] = name, ["parent_id"] = parentId ?? "0" };
        if (PttAvmHttpClient.ContractValue(node, "updated_at") is { } updated) raw["updated_at"] = updated;
        items.Add(new("CATEGORIES", id, parentId, name, string.Join(" / ", path), path.Count - 1, children.Count == 0, true, raw.ToJsonString()));
        foreach (var child in children) Walk(child, id, path, items, seen);
    }

    // GetCategoryTree nests Category, GetCategory nests category: either spelling is a child.
    private static IEnumerable<XElement> Children(XElement parent) =>
        parent.Elements().Where(x => string.Equals(x.Name.LocalName, "Category", StringComparison.OrdinalIgnoreCase));
}
