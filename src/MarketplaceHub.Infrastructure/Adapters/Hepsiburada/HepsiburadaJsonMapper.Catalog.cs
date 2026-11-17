using System.Globalization;
using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;

namespace MarketplaceHub.Infrastructure.Adapters.Hepsiburada;

internal static partial class HepsiburadaJsonMapper
{
    public static (IReadOnlyList<JsonElement> Items, int? TotalCount) ListingPage(JsonElement root)
    {
        var data = Unwrap(root);
        var items = Find(data, "items", "Items", "listings", "Listings", "claims", "Claims");
        if (items.ValueKind == JsonValueKind.Undefined && data.ValueKind == JsonValueKind.Array) items = data;
        if (items.ValueKind != JsonValueKind.Array) throw new JsonException("Hepsiburada sayfa yanıtında items listesi yok.");
        var total = Integer(data, "totalCount", "TotalCount", "totalElements", "TotalElements");
        return (items.EnumerateArray().ToArray(), total);
    }

    // The listing service names the catalogue SKU "hepsiburadaSku" next to the seller's "merchantSku".
    public static IReadOnlyList<(string MerchantSku, string HepsiburadaSku)> ListingSkuPairs(IEnumerable<JsonElement> items) =>
        items.Select(item => (Merchant: Text(item, "merchantSku", "MerchantSku"), Catalogue: Text(item, "hepsiburadaSku", "HepsiburadaSku", "hbSku", "HBSku")))
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Merchant) && !string.IsNullOrWhiteSpace(pair.Catalogue))
            .Select(pair => (pair.Merchant!.Trim(), pair.Catalogue!.Trim()))
            .ToArray();

    private static JsonElement? CategoryAttributeLists(string resourceType, JsonElement data)
    {
        if (resourceType != "CATEGORY_ATTRIBUTES" || data.ValueKind != JsonValueKind.Object) return null;
        var combined = new List<string>();
        foreach (var key in new[] { "attributes", "variantAttributes", "variantTypeAttributes" })
            if (TryFind(data, out var group, key) && group.ValueKind == JsonValueKind.Array)
                combined.AddRange(group.EnumerateArray().Select(item => item.GetRawText()));
        if (combined.Count == 0) return null;
        using var document = JsonDocument.Parse("[" + string.Join(",", combined) + "]");
        return document.RootElement.Clone();
    }

    // Product fields of Hepsiburada's upload model that appear next to the attributes.
    private static readonly HashSet<string> StructureProductFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "Barcode", "GarantiSuresi", "Image1", "Image2", "Image3", "Image4", "Image5", "kg", "Marka", "merchantSku",
        "price", "stock", "tax_vat_rate", "UrunAciklamasi", "UrunAdi", "VaryantGroupID", "Video1"
    };

    public static AdapterPageResult<RemoteReferenceItem> References(string resourceType, JsonElement root, string? parentExternalId, int page, int limit)
    {
        var data = Unwrap(root);
        // Hepsiburada returns a category's attributes in several lists. The variant list holds
        // Renk/Beden; baseAttributes are product fields (barcode, price, ...), not attributes.
        var itemArray = CategoryAttributeLists(resourceType, data) ?? Find(data, "items", "categories", "attributes", "categoryAttributes", "attributeValues", "attributeValueList", "enumValues", "options", "values", "content", "data", "results", "result", "records", "rows");
        if (itemArray.ValueKind == JsonValueKind.Undefined && data.ValueKind == JsonValueKind.Array) itemArray = data;
        var isSingleValue = resourceType == "ATTRIBUTE_VALUES"
            && data.ValueKind == JsonValueKind.Object
            && Text(data, "id", "valueId", "attributeValueId") is not null
            && Text(data, "value", "name", "attributeValue", "attributeValueName") is not null;
        if (itemArray.ValueKind == JsonValueKind.Undefined && data.ValueKind == JsonValueKind.Object)
        {
            var candidateArrays = data.EnumerateObject()
                .Where(property => property.Value.ValueKind == JsonValueKind.Array
                    && !string.Equals(property.Name, "errors", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(property.Name, "validationErrors", StringComparison.OrdinalIgnoreCase))
                .Select(property => property.Value)
                .Take(2)
                .ToArray();
            if (candidateArrays.Length == 1) itemArray = candidateArrays[0];
        }
        if (itemArray.ValueKind != JsonValueKind.Array && !isSingleValue) throw new JsonException("Hepsiburada referans yanıtında veri listesi yok.");

        var totalCount = Integer(root, "totalElements", "totalCount", "TotalElements", "TotalCount")
            ?? Integer(data, "totalElements", "totalCount", "TotalElements", "TotalCount");
        var totalPages = Integer(root, "totalPages", "TotalPages") ?? Integer(data, "totalPages", "TotalPages");
        var items = new List<RemoteReferenceItem>();
        JsonElement[] sourceItems = isSingleValue && itemArray.ValueKind != JsonValueKind.Array ? new[] { data } : itemArray.EnumerateArray().ToArray();
        foreach (var item in sourceItems)
        {
            switch (resourceType)
            {
                case "CATEGORIES":
                    {
                        var id = Text(item, "categoryId", "id", "categoryID");
                        var name = Text(item, "name");
                        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name)) throw new JsonException("Hepsiburada kategori kimliği veya adı eksik.");
                        var path = CategoryPath(item, name) ?? name;
                        var status = Text(item, "status");
                        var available = Boolean(item, "available");
                        var active = (string.Equals(status, "ACTIVE", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(status, "AKTIF", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(status, "AKTİF", StringComparison.OrdinalIgnoreCase)) && available == true;
                        // This endpoint is filtered to leaf=true, so parent category rows are
                        // intentionally absent. Preserve the full display path and keep the
                        // snapshot flat so parent references never point outside the snapshot.
                        items.Add(new(resourceType, id, null, name, path, PathDepth(path), Boolean(item, "leaf") == true, active, item.GetRawText()));
                        break;
                    }
                case "CATEGORY_ATTRIBUTES":
                    {
                        var id = Text(item, "id", "attributeId");
                        var name = Text(item, "name");
                        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name)) throw new JsonException("Hepsiburada kategori özelliği kimliği veya adı eksik.");
                        // Only "enum" attributes take a value from the value list; "string" and the other
                        // non-enum types (an image URL such as "Paket Görseli") take the value as text, and
                        // Hepsiburada has no value list for them.
                        var type = Text(item, "type");
                        items.Add(new(resourceType, id, parentExternalId, name, name, 0, true, true, item.GetRawText(),
                            Boolean(item, "mandatory"), !string.IsNullOrWhiteSpace(type) && !string.Equals(type, "enum", StringComparison.OrdinalIgnoreCase), Boolean(item, "multiValue")));
                        break;
                    }
                case "ATTRIBUTE_VALUES":
                    {
                        var name = Text(item, "value", "name", "attributeValue", "attributeValueName");
                        if (string.IsNullOrWhiteSpace(name))
                        {
                            var missing = "value";
                            var shape = string.Join(", ", item.ValueKind == JsonValueKind.Object
                                ? item.EnumerateObject().Take(12).Select(property => $"{SafeReferenceFieldName(property.Name)}:{ReferenceValueKind(property.Value.ValueKind)}")
                                : new[] { $"item:{ReferenceValueKind(item.ValueKind)}" });
                            throw new JsonException($"Hepsiburada enum değerinde {missing} alanı eksik (alan türleri: {shape}).");
                        }
                        // Production returns enum options as { value: string } without an id.
                        // The catalog guide says to use value; retain an explicit remote id when
                        // provided, otherwise use the submitted value as the stable scoped key.
                        var remoteId = Text(item, "id", "valueId", "attributeValueId");
                        var id = string.IsNullOrWhiteSpace(remoteId) ? name : remoteId;
                        items.Add(new(resourceType, id, parentExternalId, name, name, 0, true, true, item.GetRawText()));
                        break;
                    }
                default:
                    throw new JsonException("Hepsiburada referans türü desteklenmiyor.");
            }
        }

        var hasMore = totalPages is { } pages
            ? page + 1 < pages
            : totalCount is { } total
                ? (long)(page + 1) * limit < total
                : items.Count >= limit;
        var nextCursor = hasMore ? checked(page + 1).ToString(CultureInfo.InvariantCulture) : null;
        return new(items, nextCursor, hasMore, totalCount);
    }

    public static int? ReferenceApiErrorCode(JsonElement root)
    {
        var success = Boolean(root, "success", "isSuccess");
        var code = Integer(root, "code", "errorCode");
        if (success == false) return code is > 0 ? code : -1;
        if (code is > 0) return code;
        var data = Unwrap(root);
        success = Boolean(data, "success", "isSuccess");
        code = Integer(data, "code", "errorCode");
        if (success == false) return code is > 0 ? code : -1;
        return code is > 0 ? code : null;
    }

    public static RemoteProduct Product(JsonElement item)
    {
        var hbSku = Text(item, "hbSku", "HBSku", "sku", "Sku");
        // The listing service names the variant's own code (HBCV…) "hepsiburadaSku"; price and stock go by it, so it
        // identifies the variant. The product keeps the identity it had before that name was read.
        var hepsiburadaSku = Text(item, "hepsiburadaSku", "HepsiburadaSku") ?? hbSku;
        var merchantSku = Text(item, "merchantSku", "MerchantSku", "sellerSku", "SellerSku");
        var productId = Text(item, "productId", "ProductId", "id", "Id");
        var externalProductId = First(productId, hbSku, merchantSku);
        var externalVariantId = First(hepsiburadaSku, merchantSku, productId);
        if (externalProductId is null || externalVariantId is null) throw new JsonException("Hepsiburada listing kimlik alanları eksik.");
        return new(externalProductId, externalVariantId, Text(item, "barcode", "Barcode", "productBarcode"), merchantSku, item.GetRawText());
    }

    public static RemoteCatalogProduct CatalogProduct(JsonElement item)
    {
        var simple = Product(item);
        var status = Text(item, "status", "Status", "listingStatus", "ListingStatus") ?? "UNKNOWN";
        var archived = status.Contains("archive", StringComparison.OrdinalIgnoreCase)
            || status.Contains("inactive", StringComparison.OrdinalIgnoreCase)
            || status.Contains("not salable", StringComparison.OrdinalIgnoreCase);
        var title = Text(item, "productName", "ProductName", "name", "Name", "title", "Title") ?? simple.Sku ?? simple.ExternalProductId;
        var stock = Decimal(item, "availableStock", "AvailableStock", "stock", "Stock", "quantity", "Quantity");
        var listPrice = Decimal(item, "price", "Price", "listPrice", "ListPrice");
        var salePrice = Decimal(item, "salePrice", "SalePrice", "finalPrice", "FinalPrice") ?? listPrice;
        var currency = Text(item, "currency", "Currency") ?? "TRY";
        var options = Options(item);
        var images = Strings(item, "imageUrls", "ImageUrls", "images", "Images", "imageUrl", "ImageUrl");
        if (images.Count == 0 && ImageUrl(item) is { } imageUrl) images = [imageUrl];
        var variant = new RemoteCatalogVariant(
            simple.ExternalVariantId!,
            simple.Sku ?? simple.ExternalVariantId!,
            simple.Barcode,
            Text(item, "modelCode", "ModelCode", "merchantSku", "MerchantSku"),
            options,
            archived,
            salePrice,
            listPrice,
            Decimal(item, "vatRate", "VatRate"),
            stock,
            currency,
            item.GetRawText(),
            images,
            // Hepsiburada names the desi "kg" in its catalog attributes (the publication sends it so).
            Desi: Desi(item),
            // A listing Hepsiburada stocks and ships itself: the panel sends its price only. Absent stays unknown.
            FulfilledByMarketplace: Boolean(item, "isFulfilledByHB"),
            SaleState: ListingSaleState.From(Boolean(item, "isSalable", "IsSalable")),
            SaleStateReason: SaleStateReason(item));
        return new(
            simple.ExternalProductId,
            Text(item, "productMainId", "ProductMainId", "productId", "ProductId"),
            title,
            Text(item, "description", "Description") ?? "",
            Text(item, "brandId", "BrandId"),
            Text(item, "brand", "BrandName"),
            Text(item, "categoryId", "CategoryId"),
            Text(item, "categoryName", "CategoryName"),
            images,
            [variant],
            item.GetRawText());
    }


    // Why a listing is not salable (Listing Bilgilerini Çekme: isSalable, isSuspended, isLocked, isFrozen,
    // deactivationReasons). Live 2026-10-22: StockIsLessThanOrEqualToZero, PriceIsLessThanOrEqualToZero, ByMerchant.
    // A seller's own closing outranks the stock and price reasons it comes with; stock is the reason only when it is
    // the only one named.
    internal static string? SaleStateReason(JsonElement item)
    {
        if (Boolean(item, "isSalable", "IsSalable") != false) return null;
        if (Boolean(item, "isSuspended", "IsSuspended") == true) return ListingSaleState.Suspended;
        if (Boolean(item, "isLocked", "IsLocked") == true) return ListingSaleState.Locked;
        if (Boolean(item, "isFrozen", "IsFrozen") == true) return ListingSaleState.Frozen;
        var reasons = Strings(item, "deactivationReasons", "DeactivationReasons");
        bool Named(string reason) => reasons.Any(value => string.Equals(value, reason, StringComparison.OrdinalIgnoreCase));
        if (Named("ByMerchant")) return ListingSaleState.BySeller;
        if (Named("PriceIsLessThanOrEqualToZero")) return ListingSaleState.PriceZero;
        if (reasons.Count > 0 && reasons.All(value => string.Equals(value, "StockIsLessThanOrEqualToZero", StringComparison.OrdinalIgnoreCase))) return ListingSaleState.StockZero;
        return null;
    }

    // all-products-of-merchant: the portal documents the endpoint without a response
    // example, so field discovery is deliberately tolerant. Category and attribute
    // values are searched under every key Hepsiburada's catalog models use.
    private static readonly string[] StructureAttributeKeys =
        ["baseAttributes", "variantTypeAttributes", "variantAttributes", "productAttributes", "attributes", "properties", "Attributes", "Properties"];

    public static AdapterPageResult<RemoteCatalogProduct> CatalogStructurePage(JsonElement root, int page, int limit)
    {
        var data = Unwrap(root);
        var items = data.ValueKind == JsonValueKind.Array ? data : Find(data, "content", "items", "products", "productList", "listings", "data");
        if (items.ValueKind != JsonValueKind.Array) throw new JsonException("Hepsiburada mağaza ürün yanıtında ürün listesi yok.");
        var total = Integer(root, "totalElements", "totalCount", "total") ?? Integer(data, "totalElements", "totalCount", "total");
        var products = items.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object).Select(CatalogStructureProduct).ToArray();
        var totalPages = Integer(root, "totalPages") ?? Integer(data, "totalPages");
        var hasMore = totalPages is { } pages ? page + 1 < pages : total is { } count ? (long)(page + 1) * limit < count : products.Length >= limit;
        return new(products, hasMore ? checked(page + 1).ToString(CultureInfo.InvariantCulture) : null, hasMore, total);
    }

    public static RemoteCatalogProduct CatalogStructureProduct(JsonElement item)
    {
        var merchantSku = Text(item, "merchantSku", "MerchantSku", "sellerSku");
        var hbSku = Text(item, "hbSku", "HbSku", "hepsiburadaSku");
        var identity = hbSku ?? merchantSku ?? Text(item, "barcode", "Barcode", "productId", "id") ?? Guid.CreateVersion7().ToString("N");
        var category = Find(item, "category", "Category");
        var categoryId = Text(item, "categoryId", "CategoryId")
            ?? (category.ValueKind == JsonValueKind.Object ? Text(category, "categoryId", "id", "Id") : null);
        var categoryName = Text(item, "categoryName", "CategoryName")
            ?? (category.ValueKind == JsonValueKind.Object ? Text(category, "name", "categoryName", "Name") : category.ValueKind == JsonValueKind.String ? category.GetString() : null);
        var variant = new RemoteCatalogVariant(
            identity,
            merchantSku ?? identity,
            Text(item, "barcode", "Barcode"),
            Text(item, "VaryantGroupID", "variantGroupId", "modelCode", "ModelCode"),
            StructureOptions(item),
            false,
            null, null, null, null, null,
            item.GetRawText(),
            Desi: Desi(item));
        return new(
            identity,
            Text(item, "VaryantGroupID", "variantGroupId", "productId"),
            Text(item, "productName", "ProductName", "UrunAdi", "name", "title") ?? identity,
            "",
            null,
            Text(item, "brand", "brandName", "Marka"),
            categoryId,
            categoryName,
            [],
            [variant],
            item.GetRawText());
    }

    private static IReadOnlyDictionary<string, string> StructureOptions(JsonElement item)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in StructureAttributeKeys)
        {
            if (!TryFind(item, out var source, key)) continue;
            if (source.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in source.EnumerateObject())
                    if (!StructureProductFields.Contains(property.Name) && StructureValue(property.Value) is { } text) result.TryAdd(property.Name, text);
            }
            else if (source.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in source.EnumerateArray().Where(entry => entry.ValueKind == JsonValueKind.Object))
                {
                    var name = Text(entry, "name", "Name", "key", "Key", "attributeName", "id", "attributeId");
                    var valueElement = Find(entry, "value", "Value", "values", "Values", "attributeValue", "valueName");
                    if (!string.IsNullOrWhiteSpace(name) && !StructureProductFields.Contains(name) && StructureValue(valueElement) is { } text) result.TryAdd(name, text);
                }
            }
        }
        return result;
    }

    private static readonly string[] DesiFieldNames = ["desi", "Desi", "dimensionalWeight", "kg"];

    // Hepsiburada gives the desi as a top-level field or, in its catalog models, as the "kg"/"desi"
    // entry of an attribute list (baseAttributes, ...); text values may use a decimal comma.
    private static decimal? Desi(JsonElement item)
    {
        var direct = Find(item, DesiFieldNames);
        if (direct.ValueKind == JsonValueKind.Object) direct = Find(direct, "amount", "Amount", "value", "Value");
        if (ImportedDesi.FromJson(direct) is { } desi) return desi;
        foreach (var key in StructureAttributeKeys)
        {
            if (!TryFind(item, out var source, key)) continue;
            if (source.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in source.EnumerateObject())
                    if (DesiFieldNames.Contains(property.Name, StringComparer.OrdinalIgnoreCase) && ImportedDesi.FromText(StructureValue(property.Value)) is { } nested) return nested;
            }
            else if (source.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in source.EnumerateArray().Where(entry => entry.ValueKind == JsonValueKind.Object))
                {
                    var names = new[] { Text(entry, "name", "Name", "attributeName"), Text(entry, "id", "Id", "attributeId", "key", "Key") };
                    if (names.Any(name => name is not null && DesiFieldNames.Contains(name.Trim(), StringComparer.OrdinalIgnoreCase))
                        && ImportedDesi.FromText(StructureValue(Find(entry, "value", "Value", "values", "Values", "attributeValue", "valueName"))) is { } listed)
                        return listed;
                }
            }
        }
        return null;
    }

    private static string? StructureValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => string.IsNullOrWhiteSpace(value.GetString()) ? null : value.GetString()!.Trim(),
        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.ToString(),
        JsonValueKind.Array => string.Join(", ", value.EnumerateArray().Select(StructureValue).Where(text => text is not null)) is { Length: > 0 } joined ? joined : null,
        JsonValueKind.Object => Text(value, "value", "Value", "name", "Name"),
        _ => null
    };

    private static IReadOnlyDictionary<string, string> Options(JsonElement item)
    {
        var properties = Find(item, "properties", "Properties", "attributes", "Attributes");
        if (properties.ValueKind == JsonValueKind.Object)
            return properties.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.ToString(), StringComparer.OrdinalIgnoreCase);
        if (properties.ValueKind == JsonValueKind.Array)
            return properties.EnumerateArray()
                .Select(property => (Name: Text(property, "name", "Name", "key", "Key"), Value: Text(property, "value", "Value")))
                .Where(pair => !string.IsNullOrWhiteSpace(pair.Name) && pair.Value is not null)
                .ToDictionary(pair => pair.Name!, pair => pair.Value!, StringComparer.OrdinalIgnoreCase);
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> Strings(JsonElement element, params string[] names)
    {
        var value = Find(element, names);
        if (value.ValueKind == JsonValueKind.String) return string.IsNullOrWhiteSpace(value.GetString()) ? [] : [value.GetString()!];
        if (value.ValueKind == JsonValueKind.Array) return value.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : null).Where(item => !string.IsNullOrWhiteSpace(item)).Select(item => item!).ToArray();
        return [];
    }

    private static string SafeReferenceFieldName(string name) => new(name.Take(32).Select(character => char.IsAsciiLetterOrDigit(character) || character == '_' ? character : '_').ToArray());

    private static string ReferenceValueKind(JsonValueKind kind) => kind switch
    {
        JsonValueKind.Object => "object",
        JsonValueKind.Array => "array",
        JsonValueKind.String => "string",
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        JsonValueKind.Null => "null",
        _ => "undefined"
    };
    private static int PathDepth(string path)
    {
        var delimiter = path.Contains('>') ? '>' : path.Contains('/') ? '/' : path.Contains('|') ? '|' : '\0';
        return delimiter == '\0' ? 0 : Math.Max(0, path.Split(delimiter, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length - 1);
    }
    private static string? CategoryPath(JsonElement item, string categoryName)
    {
        var value = Find(item, "paths", "path", "categoryPath", "fullPath", "breadcrumb", "breadcrumbs", "hierarchy", "categoryHierarchy");
        var path = CategoryPathValue(value);
        if (!string.IsNullOrWhiteSpace(path)) return AppendCategoryName(path, categoryName);

        var parentPath = Text(item, "parentCategoryPath", "parentPath");
        if (!string.IsNullOrWhiteSpace(parentPath)) return AppendCategoryName(parentPath, categoryName);

        var parent = Find(item, "parentCategory", "parent");
        if (parent.ValueKind == JsonValueKind.Object)
        {
            var parentName = Text(parent, "name", "categoryName", "label", "title");
            if (!string.IsNullOrWhiteSpace(parentName))
            {
                var ancestorPath = CategoryPath(parent, parentName) ?? parentName;
                return AppendCategoryName(ancestorPath, categoryName);
            }
        }

        var directParentName = Text(item, "parentCategoryName", "parentName");
        return string.IsNullOrWhiteSpace(directParentName) ? null : AppendCategoryName(directParentName, categoryName);
    }
    private static string? CategoryPathValue(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String) return NormalizeCategoryPath(value.GetString());
        if (value.ValueKind == JsonValueKind.Array)
        {
            var segments = value.EnumerateArray()
                .Select(segment => segment.ValueKind == JsonValueKind.Object
                    ? Text(segment, "name", "categoryName", "label", "title")
                    : segment.ValueKind == JsonValueKind.String ? segment.GetString() : null)
                .Where(segment => !string.IsNullOrWhiteSpace(segment))
                .Select(segment => segment!.Trim())
                .ToArray();
            return segments.Length > 0 ? string.Join(" > ", segments) : null;
        }
        if (value.ValueKind == JsonValueKind.Object)
        {
            var path = Text(value, "path", "fullPath", "breadcrumb");
            if (!string.IsNullOrWhiteSpace(path)) return NormalizeCategoryPath(path);
            var segments = Find(value, "paths", "items", "segments", "breadcrumbs", "hierarchy", "categories");
            return CategoryPathValue(segments);
        }
        return null;
    }
    private static string AppendCategoryName(string path, string categoryName)
    {
        var normalized = NormalizeCategoryPath(path) ?? "";
        var lastSegment = normalized.Split(" > ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
        return string.Equals(lastSegment, categoryName.Trim(), StringComparison.OrdinalIgnoreCase)
            ? normalized
            : string.IsNullOrWhiteSpace(normalized) ? categoryName.Trim() : $"{normalized} > {categoryName.Trim()}";
    }
    private static string? NormalizeCategoryPath(string? path) => string.IsNullOrWhiteSpace(path)
        ? null
        : string.Join(" > ", path.Split(['>', '/', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
