using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Adapters.Trendyol;

// One Trendyol content as its approved list shows it: its id and the barcodes of its variants still on sale or closed
// (archived variants left out).
internal sealed record TrendyolContentObservation(string ContentId, IReadOnlyCollection<string> Barcodes);

// Trendyol keeps one content per colour: a panel card with several colours is several contents under one productMainId,
// each with its own images and attributes (live 6.11.2026: RYP006, six colours x three sizes, six contents). The
// content request (content-bulk-update) takes one item per contentId. So the update composer writes the card's content
// per barcode, without a contentId; right before the send the job asks Trendyol which content each barcode is in and
// sends each content once, with the data of its own variants. A content is never written with another content's data:
//  - a content whose live variants include a barcode this card does not have is refused (another card or listing
//    shares it);
//  - a content whose card variants disagree on title, description or images is refused (the panel holds two contents'
//    data for it);
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

    // The content request with one item per Trendyol content, each from its own variants' data.
    public static ServiceResult<string> Resolve(string contentPayloadJson, IReadOnlyDictionary<string, TrendyolContentObservation> contentByBarcode)
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
            var resolved = new JsonObject { ["contentId"] = contentId };
            // Only the fields the item carries go (the picked ones; content-bulk-update keeps a field left out).
            foreach (var name in new[] { "title", "description", "images", "attributes" })
                if (first[name] is { } value) resolved[name] = value.DeepClone();
            output.Add(resolved);
        }
        return ServiceResult<string>.Ok(new JsonObject { ["items"] = output }.ToJsonString());
    }

    private static bool SameContent(JsonObject left, JsonObject right) =>
        new[] { "title", "description", "images" }.All(name => JsonNode.DeepEquals(left[name], right[name]));

    private static JsonArray? Items(string payloadJson)
    {
        try { return JsonNode.Parse(payloadJson)?["items"] as JsonArray; }
        catch (JsonException) { return null; }
    }
}
