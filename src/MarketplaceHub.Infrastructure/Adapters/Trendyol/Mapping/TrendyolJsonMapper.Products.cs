using System.Text.Json;
using MarketplaceHub.Application;
using ListingSaleState = MarketplaceHub.Domain.ListingSaleState;

namespace MarketplaceHub.Infrastructure.Adapters.Trendyol.Mapping;

public static partial class TrendyolJsonMapper
{
    public static AdapterPageResult<RemoteProduct> Products(string json)
    {
        using var document = JsonDocument.Parse(json); var root = document.RootElement; var rows = new List<RemoteProduct>();
        foreach (var product in Content(root))
        {
            var contentId = Text(product, "contentId", "id", "productMainId");
            if (product.TryGetProperty("variants", out var variants) && variants.ValueKind == JsonValueKind.Array)
                foreach (var variant in variants.EnumerateArray()) rows.Add(new(contentId, NullText(variant, "variantId"), NullText(variant, "barcode"), NullText(variant, "stockCode"), product.GetRawText()));
            else if (!string.IsNullOrWhiteSpace(NullText(product, "barcode")))
                rows.Add(new(contentId, NullText(product, "variantId", "id"), NullText(product, "barcode"), NullText(product, "stockCode"), product.GetRawText()));
        }
        var currentPage = Long(root, "page");
        var pageSize = Math.Max(1, Long(root, "size"));
        var totalPages = Long(root, "totalPages");
        var token = NullText(root, "nextPageToken");
        string? next = null;
        if (totalPages > 0 && currentPage + 1 < totalPages && (currentPage + 1) * pageSize < 10_000)
            next = $"p:{currentPage + 1}";
        else if (!string.IsNullOrWhiteSpace(token))
            next = $"t:{token}";
        var total = NullableLong(root, "totalElements", "totalCount", "totalResults");
        return new(rows, next, !string.IsNullOrWhiteSpace(next), total is > int.MaxValue ? int.MaxValue : (int?)total);
    }

    public static AdapterPageResult<RemoteCatalogProduct> CatalogProducts(string json) => CatalogProducts(json, isPendingApproval: false);

    public static AdapterPageResult<RemoteCatalogProduct> PendingApprovalCatalogProducts(string json) => CatalogProducts(json, isPendingApproval: true);

    // "pendingApproval=3, rejected=1" for an unapproved-list page, for the worker log ("none" when the page is empty).
    public static string UnapprovedStatusCounts(string json)
    {
        using var document = JsonDocument.Parse(json);
        var counts = Content(document.RootElement)
            .GroupBy(product => NullText(product, "status") ?? "(none)", StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => $"{group.Key}={group.Count()}")
            .ToList();
        return counts.Count == 0 ? "none" : string.Join(", ", counts);
    }

    // An unapproved row's `status`: pendingApproval or rejected (offline doc "Ürün Filtreleme - Onaysız Ürün v2"); any
    // other or none stays unknown (null).
    internal static string? UnapprovedStatus(JsonElement product) => NullText(product, "status")?.Trim() switch
    {
        var status when string.Equals(status, "pendingApproval", StringComparison.OrdinalIgnoreCase) => "PENDING_APPROVAL",
        var status when string.Equals(status, "rejected", StringComparison.OrdinalIgnoreCase) => "REJECTED",
        _ => null
    };

    // The reasons of a rejected row as Trendyol wrote them: each `rejectReasonDetails[].rejectReason` (its
    // `rejectReasonDetail` when the title is empty), distinct, joined by " · ", at most 1,000 characters; null when the
    // row names none. Nothing is made up for a row without one.
    internal static string? RejectionReasons(JsonElement product)
    {
        if (!product.TryGetProperty("rejectReasonDetails", out var reasons) || reasons.ValueKind != JsonValueKind.Array) return null;
        var texts = reasons.EnumerateArray()
            .Where(reason => reason.ValueKind == JsonValueKind.Object)
            .Select(reason => NullText(reason, "rejectReason") is { } title && !string.IsNullOrWhiteSpace(title) ? title : NullText(reason, "rejectReasonDetail"))
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Select(text => text!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (texts.Count == 0) return null;
        var joined = string.Join(" · ", texts);
        return joined.Length <= 1_000 ? joined : joined[..1_000];
    }

    private static AdapterPageResult<RemoteCatalogProduct> CatalogProducts(string json, bool isPendingApproval)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        // The unapproved list holds products waiting for approval and rejected ones; every row is kept (owner, 14 Nov
        // 2026: rejected ones are imported too) and carries the approval state and rejection reasons it was given.
        var rows = Content(root)
            .Select(product => isPendingApproval
                ? ProductSnapshot(product, isPendingApproval) with { ApprovalStatus = UnapprovedStatus(product), RejectionReason = RejectionReasons(product) }
                : ProductSnapshot(product, isPendingApproval))
            .Where(x => !string.IsNullOrWhiteSpace(x.ExternalProductId))
            .ToList();
        var currentPage = Long(root, "page");
        var pageSize = Math.Max(1, Long(root, "size"));
        var totalPages = Long(root, "totalPages");
        var token = NullText(root, "nextPageToken");
        string? next = null;
        if (totalPages > 0 && currentPage + 1 < totalPages && (currentPage + 1) * pageSize < 10_000)
            next = $"p:{currentPage + 1}";
        else if (!string.IsNullOrWhiteSpace(token))
            next = $"t:{token}";
        var total = NullableLong(root, "totalElements", "totalCount", "totalResults");
        return new(rows, next, !string.IsNullOrWhiteSpace(next), total is > int.MaxValue ? int.MaxValue : (int?)total);
    }

    public static RemoteCatalogProduct ProductSnapshot(string json)
    {
        using var document = JsonDocument.Parse(json);
        return ProductSnapshot(document.RootElement);
    }

    private static RemoteCatalogProduct ProductSnapshot(JsonElement product, bool isPendingApproval = false)
    {
        var productId = Text(product, "contentId", "id", "productMainId");
        var productMainId = NullText(product, "productMainId", "contentId");
        var title = Text(product, "title", "name");
        var description = NullText(product, "description") ?? "";
        var brand = NamedReference(product, "brand");
        var category = NamedReference(product, "category");
        var images = ImageUrls(product);
        // Trendyol v2 places the slicer attribute (most commonly Renk) on the
        // content object, while the size/stock attributes stay on each
        // variant. Carry the content attributes down so every local variant
        // keeps the complete option signature.
        var contentOptions = Options(product);
        var variants = new List<RemoteCatalogVariant>();
        var seenColorGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (product.TryGetProperty("variants", out var variantArray) && variantArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var variant in variantArray.EnumerateArray())
            {
                var variantId = NullText(variant, "variantId", "id", "barcode", "stockCode") ?? "";
                var barcode = NullText(variant, "barcode");
                var sku = NullText(variant, "stockCode", "merchantSku", "sku") ?? barcode ?? variantId;
                if (string.IsNullOrWhiteSpace(variantId) || string.IsNullOrWhiteSpace(sku)) continue;
                var variantOptions = MergeOptions(contentOptions, Options(variant));
                var explicitVariantImages = VariantImageUrls(variant);
                var linkedVariantImages = LinkedImageUrls(product, variant);
                var hasImagePayload = explicitVariantImages is not null || linkedVariantImages.Count > 0;
                List<string>? variantImages = hasImagePayload
                    ? (explicitVariantImages ?? []).Concat(linkedVariantImages)
                        .Where(url => !string.IsNullOrWhiteSpace(url)).Select(url => url.Trim())
                        .Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                    : null;
                // In the official response, content.images belongs to the
                // content-level slicer (for example one colour) and is shared
                // by all size variants in that content. Keep the complete
                // image set from the first size of each colour; copying that
                // set to every size makes the local gallery fill with
                // duplicate black/blue/etc. images.
                var colorValue = ColorOptionValue(variantOptions);
                if (colorValue is not null)
                {
                    var firstVariantForColor = seenColorGroups.Add(colorValue);
                    if (!firstVariantForColor)
                    {
                        // An empty, non-null payload is intentional here. The
                        // importer uses it to archive stale media previously
                        // assigned to duplicate size variants.
                        variantImages = hasImagePayload || images.Count > 0 ? [] : null;
                    }
                    else
                    {
                        variantImages = (variantImages ?? []).ToList();
                        if (variantImages.Count == 0 && images.Count > 0)
                        {
                            variantImages = images.ToList();
                            hasImagePayload = true;
                        }
                    }
                }
                variants.Add(new(
                    variantId,
                    sku,
                    barcode,
                    NullText(variant, "modelCode", "productMainId") ?? productMainId,
                    variantOptions,
                    Bool(variant, "archived"),
                    DecimalNullable(variant, "salePrice") ?? NestedDecimalNullable(variant, "price", "salePrice"),
                    DecimalNullable(variant, "listPrice") ?? NestedDecimalNullable(variant, "price", "listPrice"),
                    DecimalNullable(variant, "vatRate"),
                    DecimalNullable(variant, "quantity") ?? NestedDecimalNullable(variant, "stock", "quantity"),
                    NestedText(variant, "price", "currency"),
                    variant.GetRawText(),
                    variantImages,
                    Desi: ImportedDesi.From(DecimalNullable(variant, "dimensionalWeight") ?? DecimalNullable(product, "dimensionalWeight")),
                    SaleState: isPendingApproval ? null : SaleState(variant),
                    SaleStateReason: isPendingApproval ? null : SaleStateReason(variant)));
            }
        }
        if (variants.Count == 0)
        {
            var barcode = NullText(product, "barcode");
            var sku = NullText(product, "stockCode", "merchantSku", "sku") ?? barcode;
            var variantId = NullText(product, "variantId", "id", "barcode", "stockCode");
            if (!string.IsNullOrWhiteSpace(variantId) && !string.IsNullOrWhiteSpace(sku))
                variants.Add(new(variantId!, sku!, barcode, NullText(product, "modelCode", "productMainId") ?? productMainId, contentOptions, Bool(product, "archived"), DecimalNullable(product, "salePrice") ?? NestedDecimalNullable(product, "price", "salePrice"), DecimalNullable(product, "listPrice") ?? NestedDecimalNullable(product, "price", "listPrice"), DecimalNullable(product, "vatRate"), DecimalNullable(product, "quantity") ?? NestedDecimalNullable(product, "stock", "quantity"), NestedText(product, "price", "currency"), product.GetRawText(), VariantImageUrls(product), Desi: ImportedDesi.From(DecimalNullable(product, "dimensionalWeight")), SaleState: isPendingApproval ? null : SaleState(product), SaleStateReason: isPendingApproval ? null : SaleStateReason(product)));
        }
        var productImages = images;
        var allImages = productImages
            .Concat(variants.SelectMany(variant => variant.ImageUrls ?? []))
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Select(url => url.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new(productId, productMainId, title, description, brand.Id, brand.Name, category.Id, category.Name, allImages, variants, product.GetRawText(), IsPendingApproval: isPendingApproval);
    }

    private static (string? Id, string? Name) NamedReference(JsonElement value, string propertyName)
    {
        if (!value.TryGetProperty(propertyName, out var reference) || reference.ValueKind != JsonValueKind.Object) return (null, null);
        return (NullText(reference, "id", "code"), NullText(reference, "name", "title"));
    }

    private static IReadOnlyList<string> ImageUrls(JsonElement product)
    {
        var urls = new List<string>();
        foreach (var propertyName in new[] { "images", "imageUrls", "media", "colorImages", "colorImageUrls" })
            if (product.TryGetProperty(propertyName, out var images)) urls.AddRange(ImageValues(images));
        return urls.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IReadOnlyList<string> LinkedImageUrls(JsonElement product, JsonElement variant)
    {
        var identifiers = new[] { "variantId", "id", "barcode", "stockCode", "merchantSku", "sku" }
            .Select(name => NullText(variant, name))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (identifiers.Count == 0) return [];
        var urls = new List<string>();
        foreach (var propertyName in new[] { "images", "imageUrls", "media", "colorImages", "colorImageUrls" })
        {
            if (!product.TryGetProperty(propertyName, out var images) || images.ValueKind != JsonValueKind.Array) continue;
            urls.AddRange(images.EnumerateArray()
                .Where(image => image.ValueKind == JsonValueKind.Object && ImageReferenceMatches(image, identifiers))
                .SelectMany(ImageValues));
        }
        return urls.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool ImageReferenceMatches(JsonElement image, IReadOnlySet<string> identifiers)
    {
        foreach (var name in new[] { "variantId", "barcode", "stockCode", "merchantSku", "sku" })
        {
            var value = NullText(image, name);
            if (!string.IsNullOrWhiteSpace(value) && identifiers.Contains(value.Trim())) return true;
        }
        if (!image.TryGetProperty("variantIds", out var values) || values.ValueKind != JsonValueKind.Array) return false;
        return values.EnumerateArray().Any(value => value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined or JsonValueKind.Object or JsonValueKind.Array) && identifiers.Contains(value.ToString().Trim()));
    }

    private static IReadOnlyList<string>? VariantImageUrls(JsonElement variant)
    {
        var foundProperty = false;
        foreach (var propertyName in new[] { "images", "imageUrls", "media", "imageUrl", "image", "colorImages", "colorImageUrls", "variantImages" })
        {
            if (!variant.TryGetProperty(propertyName, out var value)) continue;
            foundProperty = true;
            var urls = ImageValues(value);
            if (urls.Count > 0) return urls;
        }

        return foundProperty ? [] : null;
    }

    private static IReadOnlyList<string> ImageValues(JsonElement images)
    {
        if (images.ValueKind == JsonValueKind.Object)
        {
            var direct = NullText(images, "url", "imageUrl", "src", "link");
            if (!string.IsNullOrWhiteSpace(direct)) return [direct.Trim()];
            foreach (var propertyName in new[] { "images", "imageUrls", "media", "colorImages", "colorImageUrls", "variantImages" })
            {
                if (images.TryGetProperty(propertyName, out var nested))
                    return ImageValues(nested);
            }
        }

        IEnumerable<JsonElement> values = images.ValueKind == JsonValueKind.Array
            ? images.EnumerateArray()
            : [images];
        return values
            .Select(image => image.ValueKind == JsonValueKind.Object ? NullText(image, "url", "imageUrl", "src", "link") : image.ValueKind == JsonValueKind.String ? image.GetString() : null)
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Select(url => url!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyDictionary<string, string> Options(JsonElement value)
    {
        // Catalog responses have used both the approved-products names and the
        // catalog names for the same fields. Keep the source order: it is the
        // marketplace's variant order and is later used for the gallery.
        var entries = new List<(string Key, string Value, bool HasValueId)>();
        foreach (var propertyName in new[] { "attributes", "variantAttributes", "options", "optionValues" })
        {
            if (!value.TryGetProperty(propertyName, out var attributes) || attributes.ValueKind != JsonValueKind.Array) continue;
            foreach (var attribute in attributes.EnumerateArray())
            {
                var key = attribute.ValueKind == JsonValueKind.Object
                    ? NullText(attribute, "attributeName", "attribute_name", "optionName", "name", "label")
                    : null;
                var optionValue = attribute.ValueKind == JsonValueKind.Object
                    ? NullText(attribute, "attributeValue", "attribute_value", "optionValue", "value", "text")
                    : null;
                var hasValueId = attribute.ValueKind == JsonValueKind.Object
                    && !string.IsNullOrWhiteSpace(NullText(attribute, "attributeValueId", "attribute_value_id", "optionValueId"));
                if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(optionValue)) entries.Add((key.Trim(), optionValue.Trim(), hasValueId));
            }
        }

        return entries
            .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => IsVariantColorOptionKey(group.Key) && group.Any(entry => entry.HasValueId)
                    ? group.FirstOrDefault(entry => !entry.HasValueId).Value ?? group.Last().Value
                    : group.Last().Value,
                StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsVariantColorOptionKey(string value)
    {
        var normalized = value.Replace(" ", "", StringComparison.Ordinal).Trim().ToUpperInvariant();
        return normalized is "RENK" or "COLOR" or "COLOUR";
    }

    private static IReadOnlyDictionary<string, string> MergeOptions(IReadOnlyDictionary<string, string> parent, IReadOnlyDictionary<string, string> variant)
    {
        var merged = new Dictionary<string, string>(parent, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in variant) merged[pair.Key] = pair.Value;
        return merged;
    }

    private static string? ColorOptionValue(IReadOnlyDictionary<string, string> options) => options
        .Where(pair => IsColorOptionKey(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
        .OrderBy(pair => IsWebColorOptionKey(pair.Key) ? 1 : 0)
        .Select(pair => pair.Value.Trim().ToUpperInvariant())
        .FirstOrDefault();

    private static bool IsWebColorOptionKey(string value)
    {
        var normalized = value.Replace(" ", "", StringComparison.Ordinal).Trim().ToUpperInvariant();
        return normalized is "WEBCOLOR" or "WEBCOLOUR" or "WEBRENK";
    }

    private static bool IsColorOptionKey(string value)
    {
        var normalized = value.Replace(" ", "", StringComparison.Ordinal).Trim().ToUpperInvariant();
        return normalized is "RENK" or "WEBCOLOR" or "COLOR" or "COLOUR";
    }

    public static RemoteProduct Product(string json, string barcode)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var contentId = Text(root, "contentId", "id", "productMainId");
        var variantId = NullText(root, "variantId", "id");
        var sku = NullText(root, "stockCode", "merchantSku");
        return new(contentId, variantId, NullText(root, "barcode") ?? barcode, sku, root.GetRawText());
    }

    public static RemotePublicationStatus? ApprovedPublicationStatus(string json, string barcode)
    {
        using var document = JsonDocument.Parse(json);
        foreach (var product in Content(document.RootElement))
        {
            var contentId = NullText(product, "contentId");
            if (!product.TryGetProperty("variants", out var variants) || variants.ValueKind != JsonValueKind.Array) continue;
            foreach (var variant in variants.EnumerateArray())
            {
                var remoteBarcode = NullText(variant, "barcode");
                if (!string.Equals(remoteBarcode, barcode, StringComparison.OrdinalIgnoreCase)) continue;
                var status = Bool(variant, "blacklisted") ? "BLACKLISTED"
                    : Bool(variant, "locked") ? "LOCKED"
                    : Bool(variant, "archived") ? "ARCHIVED"
                    : "APPROVED";
                return new(remoteBarcode!, status, contentId, NullText(variant, "variantId"), null, variant.GetRawText());
            }
        }
        return null;
    }

    public static RemotePublicationStatus? UnapprovedPublicationStatus(string json, string barcode)
    {
        using var document = JsonDocument.Parse(json);
        foreach (var product in Content(document.RootElement))
        {
            var remoteBarcode = NullText(product, "barcode");
            if (!string.Equals(remoteBarcode, barcode, StringComparison.OrdinalIgnoreCase)) continue;
            var rawStatus = Text(product, "status");
            var status = string.Equals(rawStatus, "pendingApproval", StringComparison.OrdinalIgnoreCase) ? "PENDING_APPROVAL"
                : string.Equals(rawStatus, "rejected", StringComparison.OrdinalIgnoreCase) ? "REJECTED"
                : "UNKNOWN";
            string? rejection = null;
            if (product.TryGetProperty("rejectReasonDetails", out var reasons) && reasons.ValueKind == JsonValueKind.Array)
                rejection = reasons.EnumerateArray().Select(reason => NullText(reason, "rejectReason")).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
            return new(remoteBarcode!, status, null, null, rejection, product.GetRawText());
        }
        return null;
    }

    public static RemoteOperationStatus Batch(string json, string requestedId)
    {
        using var document = JsonDocument.Parse(json); var root = document.RootElement; var lines = new List<RemoteOperationLine>(); var itemStatuses = new List<string>();
        if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            foreach (var item in items.EnumerateArray())
            {
                var key = "";
                if (item.TryGetProperty("requestItem", out var request) && request.ValueKind == JsonValueKind.Object)
                {
                    key = Text(request, "barcode", "contentId", "stockCode");
                    // The approved-product updates nest the item: content and variant results under updateRequest
                    // ({contentId, ...} / {barcode, ...}), the delivery result under request. Source: offline
                    // trendyol-api-son.md ~9472-9585 ("Onaylı Ürün Content/Varyant/Teslimat Güncelleme" answers).
                    foreach (var nested in new[] { "updateRequest", "request" })
                        if (string.IsNullOrWhiteSpace(key) && request.TryGetProperty(nested, out var inner) && inner.ValueKind == JsonValueKind.Object)
                            key = Text(inner, "barcode", "contentId", "stockCode");
                }
                if (string.IsNullOrWhiteSpace(key)) key = Text(item, "barcode", "contentId", "stockCode");
                var status = Text(item, "status"); var succeeded = string.Equals(status, "SUCCESS", StringComparison.OrdinalIgnoreCase); var allFailures = item.TryGetProperty("failureReasons", out var reasons) && reasons.ValueKind == JsonValueKind.Array ? reasons.EnumerateArray().Select(x => x.ToString()).Where(x => !string.IsNullOrWhiteSpace(x)).ToList() : [];
                var failure = allFailures.FirstOrDefault();
                lines.Add(new(key, succeeded, null, failure, !succeeded && !string.Equals(status, "FAILED", StringComparison.OrdinalIgnoreCase), Reasons: allFailures.Count == 0 ? null : allFailures));
                itemStatuses.Add(status);
            }
        var batchStatus = Text(root, "status");
        if (string.IsNullOrWhiteSpace(batchStatus)) batchStatus = PriceInventoryBatchStatus(root, itemStatuses);
        int? itemCount = root.TryGetProperty("itemCount", out var count) && count.ValueKind == JsonValueKind.Number && count.TryGetInt32(out var reported) ? reported : null;
        return new(NullText(root, "batchRequestId") ?? requestedId, batchStatus, lines, ReportedItemCount: itemCount);
    }

    // toplu-işlem-kontrolü: a price-and-inventory batch answers no batch-level status ("Batch status alanı tarafınıza
    // dönmeyecektir"); its items do, SUCCESS or FAILED. The batch is complete once every item has one of them and as many
    // items are listed as itemCount says; until then it is still running. A batch with no items yet is running too.
    internal static string PriceInventoryBatchStatus(JsonElement root, IReadOnlyList<string> itemStatuses)
    {
        if (itemStatuses.Count == 0) return "IN_PROGRESS";
        if (itemStatuses.Any(status => !string.Equals(status, "SUCCESS", StringComparison.OrdinalIgnoreCase) && !string.Equals(status, "FAILED", StringComparison.OrdinalIgnoreCase)))
            return "IN_PROGRESS";
        if (root.TryGetProperty("itemCount", out var count) && count.ValueKind == JsonValueKind.Number && count.TryGetInt64(out var itemCount) && itemCount > itemStatuses.Count)
            return "IN_PROGRESS";
        return "COMPLETED";
    }
    // The approved list's onSale (Ürün Filtreleme - Onaylı Ürün v2): the variant is on sale now. A product still waiting
    // for approval has no sale state; an answer without the field says nothing.
    private static string? SaleState(JsonElement variant) => ListingSaleState.From(BoolAny(variant, "onSale"));

    // Why a variant is off sale, from the same answer: blacklisted, locked or archived as Trendyol flags them, else a
    // stock of 0 in the same answer. Off sale with stock and no flag names no reason.
    private static string? SaleStateReason(JsonElement variant)
    {
        if (BoolAny(variant, "onSale") != false) return null;
        if (BoolAny(variant, "blacklisted") == true) return ListingSaleState.Blacklisted;
        if (BoolAny(variant, "locked") == true) return ListingSaleState.Locked;
        if (BoolAny(variant, "archived") == true) return ListingSaleState.Archived;
        var quantity = DecimalNullable(variant, "quantity") ?? NestedDecimalNullable(variant, "stock", "quantity");
        return quantity is <= 0 ? ListingSaleState.StockZero : null;
    }
}
