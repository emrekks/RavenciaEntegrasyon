using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Adapters.Trendyol;

// One Trendyol content as its approved list shows it: its id and the barcodes of its variants still on sale or closed
// (archived variants left out).
internal sealed record TrendyolContentObservation(string ContentId, IReadOnlyCollection<string> Barcodes);

// The category attributes Trendyol flags as varianter (a variant axis inside one content, e.g. 338 Beden) or slicer
// (splits contents, e.g. Renk), read from the category attribute list (getCategoryAttributes: "varianter", "slicer").
internal sealed record TrendyolAttributeRoles(IReadOnlySet<string> Varianters, IReadOnlySet<string> Slicers)
{
    public bool IsVarianter(string attributeId) => Varianters.Contains(attributeId);

    // From the category attribute rows' raw JSON ({"attribute":{"id":338,...},"varianter":true,"slicer":false,...}).
    public static TrendyolAttributeRoles FromCategoryAttributes(IEnumerable<string> rawJsonRows)
    {
        var varianters = new HashSet<string>(StringComparer.Ordinal);
        var slicers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in rawJsonRows)
        {
            JsonNode? row;
            try { row = JsonNode.Parse(raw); }
            catch (JsonException) { continue; }
            var id = row?["attribute"]?["id"]?.ToJsonString().Trim('"');
            if (string.IsNullOrWhiteSpace(id)) continue;
            if (row!["varianter"] is JsonValue varianter && varianter.TryGetValue<bool>(out var isVarianter) && isVarianter) varianters.Add(id);
            if (row["slicer"] is JsonValue slicer && slicer.TryGetValue<bool>(out var isSlicer) && isSlicer) slicers.Add(id);
        }
        return new(varianters, slicers);
    }
}

// Trendyol keeps one content per colour: a panel card with several colours is several contents under one productMainId,
// each with its own images and attributes (live 6.11.2026: RYP006, six colours x three sizes, six contents). The
// content request (content-bulk-update) takes one item per contentId. So the update composer writes the card's content
// per barcode, without a contentId; right before the send the job asks Trendyol which content each barcode is in and
// sends each content once, with the data of its own variants. A content is never written with another content's data:
//  - a content whose live variants include a barcode this card does not have is refused (another card or listing
//    shares it);
//  - a content whose card variants disagree on title, description or images is refused (the panel holds two contents'
//    data for it);
//  - a content's attribute list leaves out the category's varianter (the size) and needs every other attribute equal on
//    all its variants, else the content is refused (ContentAttributes);
//  - a barcode Trendyol does not list as approved is left out (there is no content of it to update).
internal static class TrendyolContentGroups
{
    // A content request still keyed by barcode: no item carries a contentId yet.
    public static bool NeedsResolution(string contentPayloadJson)
    {
        var items = Items(contentPayloadJson);
        return items is { Count: > 0 } && items.Any(item => item is JsonObject value && !value.ContainsKey("contentId"));
    }

    // Every item names a numeric contentId and no barcode: the only shape the content request may leave the panel in.
    public static bool IsResolved(string contentPayloadJson)
    {
        var items = Items(contentPayloadJson);
        return items is not null && items.All(item => item is JsonObject value
            && !value.ContainsKey("barcode")
            && value["contentId"] is JsonValue id && id.TryGetValue<long>(out var contentId) && contentId > 0);
    }

    public static IReadOnlyList<string> Barcodes(string contentPayloadJson) =>
        (Items(contentPayloadJson) ?? [])
            .Select(item => (item as JsonObject)?["barcode"]?.GetValue<string>()?.Trim())
            .Where(barcode => !string.IsNullOrWhiteSpace(barcode))
            .Select(barcode => barcode!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    // The contents a barcode lookup answered: the one content whose variants hold exactly this barcode, null when no
    // approved content has it. Two contents answering for one barcode cannot be told apart and are refused.
    public static ServiceResult<TrendyolContentObservation?> Observe(string barcode, IEnumerable<RemoteCatalogProduct> contents)
    {
        var holders = contents
            .Where(content => !string.IsNullOrWhiteSpace(content.ExternalProductId)
                && content.Variants.Any(variant => string.Equals(variant.Barcode?.Trim(), barcode, StringComparison.OrdinalIgnoreCase)))
            .GroupBy(content => content.ExternalProductId.Trim(), StringComparer.Ordinal)
            .ToList();
        if (holders.Count == 0) return ServiceResult<TrendyolContentObservation?>.Ok(null);
        if (holders.Count > 1)
            return ServiceResult<TrendyolContentObservation?>.Fail("PRODUCT_CONTENT_AMBIGUOUS", $"Trendyol '{barcode}' barkodunu birden fazla içerikte gösteriyor ({string.Join(", ", holders.Select(group => group.Key))}); yanlış ilana yazmamak için içerik güncellemesi gönderilmedi.", 409);
        var barcodes = holders[0]
            .SelectMany(content => content.Variants)
            .Where(variant => !variant.Archived || string.Equals(variant.Barcode?.Trim(), barcode, StringComparison.OrdinalIgnoreCase))
            .Select(variant => variant.Barcode?.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return ServiceResult<TrendyolContentObservation?>.Ok(new(holders[0].Key, barcodes));
    }

    // The content request carries attributes (ATTRIBUTES was picked): the category's varianter must be known to build it.
    public static bool CarriesAttributes(string contentPayloadJson) =>
        (Items(contentPayloadJson) ?? []).Any(item => item is JsonObject value && value["attributes"] is JsonArray);

    // The card's barcodes no approved content holds: Resolve leaves them out, so the content request does not carry them.
    public static IReadOnlyList<string> Unresolved(string contentPayloadJson, IReadOnlyDictionary<string, TrendyolContentObservation> contentByBarcode)
    {
        var lookup = new Dictionary<string, TrendyolContentObservation>(contentByBarcode, StringComparer.OrdinalIgnoreCase);
        return Barcodes(contentPayloadJson).Where(barcode => !lookup.ContainsKey(barcode)).ToList();
    }

    // The content request with one item per Trendyol content, each from its own variants' data.
    public static ServiceResult<string> Resolve(string contentPayloadJson, IReadOnlyDictionary<string, TrendyolContentObservation> contentByBarcode, TrendyolAttributeRoles? roles = null)
    {
        var items = Items(contentPayloadJson);
        if (items is null) return ServiceResult<string>.Fail("PRODUCT_UPDATE_PAYLOAD_INVALID", "Ürün içerik güncelleme payload'ı okunamadı.", 422);
        var cardBarcodes = new HashSet<string>(Barcodes(contentPayloadJson), StringComparer.OrdinalIgnoreCase);
        var lookup = new Dictionary<string, TrendyolContentObservation>(contentByBarcode, StringComparer.OrdinalIgnoreCase);
        var groups = new List<(TrendyolContentObservation Content, List<JsonObject> Items)>();
        foreach (var node in items)
        {
            if (node is not JsonObject item || item["barcode"]?.GetValue<string>()?.Trim() is not { Length: > 0 } barcode)
                return ServiceResult<string>.Fail("PRODUCT_UPDATE_PAYLOAD_INVALID", "Ürün içerik güncellemesinde barkodu olmayan bir varyant var.", 422);
            if (!lookup.TryGetValue(barcode, out var content)) continue;
            var group = groups.FindIndex(entry => entry.Content.ContentId == content.ContentId);
            if (group < 0) groups.Add((content, [item]));
            else groups[group].Items.Add(item);
        }
        if (groups.Count == 0)
            return ServiceResult<string>.Fail("PRODUCT_CONTENT_NOT_APPROVED", "Ürünün hiçbir barkodu Trendyol'un onaylı ürünlerinde bulunamadı; güncellenecek içerik yok.", 409);

        var output = new JsonArray();
        foreach (var (content, groupItems) in groups)
        {
            if (!long.TryParse(content.ContentId, NumberStyles.None, CultureInfo.InvariantCulture, out var contentId) || contentId <= 0)
                return ServiceResult<string>.Fail("REMOTE_CONTENT_ID_INVALID", "Trendyol contentId sayısal değil; otomatik güncelleme güvenli biçimde durduruldu.", 409);
            var foreign = content.Barcodes.Where(barcode => !cardBarcodes.Contains(barcode)).Order(StringComparer.Ordinal).ToList();
            if (foreign.Count > 0)
                return ServiceResult<string>.Fail("PRODUCT_CONTENT_SHARED", $"Trendyol içeriği {content.ContentId} bu üründe olmayan barkodları da taşıyor ({string.Join(", ", foreign.Take(5))}); yanlış ilana yazmamak için içerik güncellemesi gönderilmedi.", 409);
            var first = groupItems[0];
            var differing = groupItems.Skip(1).FirstOrDefault(item => !SameContent(first, item));
            if (differing is not null)
                return ServiceResult<string>.Fail("PRODUCT_CONTENT_GROUP_MISMATCH", $"Trendyol içeriği {content.ContentId} içindeki '{first["barcode"]}' ve '{differing["barcode"]}' varyantlarının panelde başlığı, açıklaması veya görselleri farklı; yanlış ilana yazmamak için içerik güncellemesi gönderilmedi.", 409);
            var attributes = ContentAttributes(groupItems, roles);
            if (!attributes.Succeeded)
                return ServiceResult<string>.Fail("PRODUCT_CONTENT_GROUP_MISMATCH", $"Trendyol içeriği {content.ContentId}: {attributes.Error!.Message}; yanlış ilana yazmamak için içerik güncellemesi gönderilmedi.", 409);
            var resolved = new JsonObject { ["contentId"] = contentId };
            // Only the fields the item carries go (the picked ones; content-bulk-update keeps a field left out).
            foreach (var name in new[] { "title", "description", "images" })
                if (first[name] is { } value) resolved[name] = value.DeepClone();
            if (attributes.Value is { } contentAttributes) resolved["attributes"] = contentAttributes;
            output.Add(resolved);
        }
        return ServiceResult<string>.Ok(new JsonObject { ["items"] = output }.ToJsonString());
    }

    private static bool SameContent(JsonObject left, JsonObject right) =>
        new[] { "title", "description", "images" }.All(name => JsonNode.DeepEquals(left[name], right[name]));

    // One content's attribute list, built for the content and not taken from its first variant (owner, 6.11.2026):
    //  - a varianter attribute (the size: one value per variant inside the content) is left out. Trendyol's guide: the
    //    value of a slicer or varianter attribute cannot be updated on an approved product, and the varianter is a
    //    variant's property, not the content's, so no variant's value stands for the content;
    //  - every other attribute, the slicer included (it is what makes this content: one colour), must have the same
    //    value on every variant of the content; one that differs, or that some variants lack, stops the content;
    //  - when the category's varianter/slicer flags could not be read (roles null) nothing is left out and any
    //    difference stops the content: the panel does not guess which attribute is the varianter.
    // Null value: no variant carries attributes (the update did not pick them), so the content request has none.
    internal static ServiceResult<JsonArray?> ContentAttributes(IReadOnlyList<JsonObject> groupItems, TrendyolAttributeRoles? roles)
    {
        if (groupItems.All(item => item["attributes"] is not JsonArray)) return ServiceResult<JsonArray?>.Ok(null);
        var perItem = groupItems.Select(AttributeValues).ToList();
        var ids = perItem.SelectMany(values => values.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var differing = new List<string>();
        foreach (var id in ids)
        {
            if (roles?.IsVarianter(id) == true) continue;
            var values = perItem.Select(values => values.TryGetValue(id, out var value) ? value : null).ToList();
            if (values.Any(value => value is null) || values.Distinct(StringComparer.Ordinal).Count() > 1) differing.Add(id);
        }
        if (differing.Count > 0)
        {
            var names = string.Join(", ", differing.Take(5).Select(id => AttributeLabel(groupItems, id)));
            return ServiceResult<JsonArray?>.Fail("PRODUCT_CONTENT_GROUP_MISMATCH", roles is null
                ? $"aynı içerikteki varyantların özellikleri farklı: {names} (kategorinin varyant özelliği okunamadığı için hangisinin beden olduğu bilinmiyor)"
                : $"aynı içerikteki varyantların özellikleri farklı: {names}", 409);
        }
        var output = new JsonArray();
        foreach (var node in (groupItems[0]["attributes"] as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            var id = AttributeId(node);
            if (id is not null && roles?.IsVarianter(id) == true) continue;
            output.Add(node.DeepClone());
        }
        return ServiceResult<JsonArray?>.Ok(output);
    }

    private static string? AttributeId(JsonObject node) => node["attributeId"]?.ToJsonString().Trim('"') is { Length: > 0 } id ? id : null;

    private static Dictionary<string, string> AttributeValues(JsonObject item)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (item["attributes"] is not JsonArray attributes) return values;
        foreach (var node in attributes.OfType<JsonObject>())
        {
            var id = node["attributeId"]?.ToJsonString().Trim('"');
            if (string.IsNullOrWhiteSpace(id)) continue;
            var value = node.DeepClone().AsObject();
            value.Remove("attributeId");
            value.Remove("attributeName");
            values[id] = value.ToJsonString();
        }
        return values;
    }

    private static string AttributeLabel(IReadOnlyList<JsonObject> groupItems, string id) =>
        groupItems.SelectMany(item => (item["attributes"] as JsonArray)?.OfType<JsonObject>() ?? [])
            .Where(node => node["attributeId"]?.ToJsonString().Trim('"') == id)
            .Select(node => node["attributeName"] is JsonValue name && name.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null)
            .FirstOrDefault(label => label is not null) ?? $"özellik {id}";

    private static JsonArray? Items(string payloadJson)
    {
        try { return JsonNode.Parse(payloadJson)?["items"] as JsonArray; }
        catch (JsonException) { return null; }
    }
}
