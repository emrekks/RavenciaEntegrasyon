using System.Globalization;
using System.Text.Json;
using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Adapters.Hepsiburada;

internal static class HepsiburadaJsonMapper
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

    public static AdapterPageResult<RemoteReferenceItem> References(string resourceType, JsonElement root, string? parentExternalId, int page, int limit)
    {
        var data = Unwrap(root);
        var itemArray = Find(data, "items", "categories", "attributes", "categoryAttributes", "attributeValues", "attributeValueList", "enumValues", "options", "values", "content", "data", "results", "result", "records", "rows");
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
                        var type = Text(item, "type");
                        items.Add(new(resourceType, id, parentExternalId, name, name, 0, true, true, item.GetRawText(),
                            Boolean(item, "mandatory"), string.Equals(type, "string", StringComparison.OrdinalIgnoreCase), Boolean(item, "multiValue")));
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
        var merchantSku = Text(item, "merchantSku", "MerchantSku", "sellerSku", "SellerSku");
        var productId = Text(item, "productId", "ProductId", "id", "Id");
        var externalProductId = First(productId, hbSku, merchantSku);
        var externalVariantId = First(hbSku, merchantSku, productId);
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
            images);
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

    public static AdapterPageResult<RemoteProductMatch> PendingProductMatches(JsonElement root, int page, int limit)
    {
        var (items, totalCount) = ListingPage(root);
        var matches = new List<RemoteProductMatch>(items.Count);
        foreach (var item in items)
        {
            var merchantSku = Text(item, "merchantSku", "MerchantSku", "sellerSku", "SellerSku");
            if (string.IsNullOrWhiteSpace(merchantSku)) throw new JsonException("Hepsiburada eşleşme satırında merchantSku yok.");
            var matchedProduct = Find(item, "matchedProduct", "catalogProduct", "product");
            if (matchedProduct.ValueKind != JsonValueKind.Object) matchedProduct = item;
            var imageUrls = Strings(matchedProduct, "imageUrls", "images", "productImages");
            if (imageUrls.Count == 0)
                imageUrls = Enumerable.Range(1, 10).Select(index => Text(matchedProduct, $"image{index}", $"Image{index}"))
                    .Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!).ToArray();
            var status = Text(item, "productStatus", "status", "Status") ?? "PRE_MATCHED";
            matches.Add(new(
                merchantSku.Trim(),
                NormalizeProductStatus(status),
                Text(matchedProduct, "hbSku", "HBSku", "hepsiburadaSku", "productCode"),
                Text(matchedProduct, "productName", "productTitle", "title", "name"),
                Text(matchedProduct, "brandName", "brand", "marka", "brandTitle"),
                imageUrls,
                Text(matchedProduct, "barcode", "Barcode", "productBarcode", "gtin"),
                item.GetRawText()));
        }

        var hasMore = totalCount is { } total
            ? (long)(page + 1) * limit < total
            : matches.Count >= limit;
        return new(matches, hasMore ? checked(page + 1).ToString(CultureInfo.InvariantCulture) : null, hasMore, totalCount);
    }

    public static bool ProductMatchDecisionAccepted(JsonElement root)
    {
        var data = Unwrap(root);
        if (Boolean(root, "success", "isSuccess") == false || Boolean(data, "success", "isSuccess") == false) return false;
        if (Integer(root, "code") is { } rootCode && rootCode != 0) return false;
        if (Integer(data, "code") is { } dataCode && dataCode != 0) return false;
        return true;
    }

    public static string ProductImportTrackingId(JsonElement root)
    {
        var data = Unwrap(root);
        var trackingId = First(Text(root, "trackingId", "traceId"), Text(data, "trackingId", "traceId"));
        return !string.IsNullOrWhiteSpace(trackingId) ? trackingId : throw new JsonException("Hepsiburada ürün aktarım yanıtında trackingId yok.");
    }

    public static string InventoryUploadId(JsonElement root)
    {
        var data = Unwrap(root);
        var uploadId = First(Text(root, "inventoryUploadId", "uploadId", "id"), Text(data, "inventoryUploadId", "uploadId", "id"));
        return !string.IsNullOrWhiteSpace(uploadId) ? uploadId : throw new JsonException("Hepsiburada listing yanıtında inventoryUploadId yok.");
    }

    public static RemoteOperationStatus InventoryUploadStatus(JsonElement root, string uploadId)
    {
        var data = Unwrap(root);
        var rawStatus = First(Text(root, "status", "uploadStatus", "inventoryStatus"), Text(data, "status", "uploadStatus", "inventoryStatus"));
        if (string.IsNullOrWhiteSpace(rawStatus)) throw new JsonException("Hepsiburada listing yükleme durumu eksik.");
        var normalized = rawStatus.Trim().Replace(' ', '_').Replace('-', '_').ToUpperInvariant();
        var status = normalized switch
        {
            "PROCESSING" or "IN_PROGRESS" or "PENDING" or "QUEUED" => "IN_PROGRESS",
            "COMPLETED" or "COMPLETE" or "SUCCESS" or "SUCCEEDED" or "FAILED" or "PARTIAL_SUCCESS" or "COMPLETED_WITH_ERRORS" => "COMPLETED",
            _ => throw new JsonException("Hepsiburada listing yükleme durumu tanınmıyor.")
        };
        if (status == "IN_PROGRESS") return new($"LISTING_INVENTORY:{uploadId}", status, []);

        var errorItems = Find(data, "errors", "validationErrors", "errorList");
        if (errorItems.ValueKind == JsonValueKind.Undefined) errorItems = Find(root, "errors", "validationErrors", "errorList");
        if (errorItems.ValueKind is not (JsonValueKind.Array or JsonValueKind.Undefined or JsonValueKind.Null))
            throw new JsonException("Hepsiburada listing hata listesi beklenen türde değil.");
        var lines = new List<RemoteOperationLine>();
        if (errorItems.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in errorItems.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) throw new JsonException("Hepsiburada listing hata satırı nesne değil.");
                var merchantSku = Text(item, "merchantSku", "MerchantSku", "sellerSku", "SellerSku");
                var hbSku = Text(item, "hepsiburadaSku", "HepsiburadaSku", "hbSku", "HBSKU");
                var externalKey = First(merchantSku, hbSku);
                if (string.IsNullOrWhiteSpace(externalKey))
                    throw new JsonException("Hepsiburada listing hata satırında merchantSku veya hbSku yok.");
                var errorCode = Text(item, "errorCode", "code", "message");
                var nestedErrors = Find(item, "errors", "messages");
                if (nestedErrors.ValueKind == JsonValueKind.Array)
                {
                    var firstNested = nestedErrors.EnumerateArray().Select(entry => entry.ValueKind == JsonValueKind.Object
                        ? Text(entry, "string", "message", "error", "code")
                        : entry.ValueKind == JsonValueKind.String ? entry.GetString() : null).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
                    errorCode ??= firstNested;
                }
                if (string.IsNullOrWhiteSpace(errorCode)) errorCode = "REMOTE_VALIDATION_FAILED";
                lines.Add(new(externalKey.Trim(), false, hbSku, errorCode, false, "REJECTED"));
            }
        }
        if (lines.GroupBy(line => line.ExternalKey, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
            throw new JsonException("Hepsiburada listing sonuçlarında yinelenen SKU var.");
        return new($"LISTING_INVENTORY:{uploadId}", status, lines);
    }

    public static RemoteOperationStatus ProductImportStatus(JsonElement root, string trackingId)
    {
        var data = Unwrap(root);
        var importStatus = First(Text(root, "importStatus", "status"), Text(data, "importStatus", "status"));
        if (string.IsNullOrWhiteSpace(importStatus)) throw new JsonException("Hepsiburada ürün aktarım durumu eksik.");
        var normalizedStatus = importStatus.Trim().ToUpperInvariant();
        var status = normalizedStatus switch
        {
            "PROCESSING" or "IN_PROGRESS" or "PENDING" => "IN_PROGRESS",
            "SUCCESS" or "COMPLETED" or "FAILED" or "PARTIAL_SUCCESS" => "COMPLETED",
            _ => throw new JsonException("Hepsiburada ürün aktarım durumu tanınmıyor.")
        };

        var items = Find(data, "items", "products", "productStatuses", "productList", "results");
        if (items.ValueKind == JsonValueKind.Undefined) items = data.ValueKind == JsonValueKind.Array ? data : Find(root, "items", "products", "productStatuses", "productList", "results");
        var lines = new List<RemoteOperationLine>();
        if (items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                var merchantSku = Text(item, "merchantSku", "MerchantSku", "sellerSku", "SellerSku");
                var productStatus = Text(item, "productStatus", "status", "Status");
                if (string.IsNullOrWhiteSpace(merchantSku) || string.IsNullOrWhiteSpace(productStatus)) throw new JsonException("Hepsiburada ürün aktarım satırında merchantSku veya productStatus yok.");
                var normalizedProductStatus = NormalizeProductStatus(productStatus);
                var succeeded = normalizedProductStatus is "SUCCESS" or "ACCEPTED" or "APPROVED" or "APPROVAL_PENDING" or "WAITING_APPROVAL" or "IN_REVIEW" or "PRODUCT_CREATED"
                    or "WAITING" or "INCELENECEK" or "GOREV_ACILMIS" or "KATALOG_SURECINDE" or "MATCHED" or "ESLESEN" or "PRE_MATCHED" or "MATCHED_WITH_STAGED" or "ON_KATALOG_ESLESEN" or "CREATED" or "SATISA_HAZIR" or "SALE_READY";
                var rejected = normalizedProductStatus is "MISSING_INFO" or "URUN_BILGILERI_EKSIK" or "REJECTED" or "FAILED" or "INVALID";
                if (!succeeded && !rejected) throw new JsonException("Hepsiburada ürün durum satırında tanınmayan ürün durumu var.");
                var errorCode = succeeded ? null : ProductImportErrorCode(item);
                lines.Add(new(merchantSku.Trim(), succeeded, Text(item, "hbSku", "HBSku"), errorCode, false, normalizedProductStatus));
            }
        }
        else if (items.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
            throw new JsonException("Hepsiburada ürün aktarım satır listesi beklenen türde değil.");

        if (status == "COMPLETED" && lines.Count > 0 && lines.GroupBy(x => x.ExternalKey, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
            throw new JsonException("Hepsiburada ürün aktarım yanıtında merchantSku yineleniyor.");
        return new(trackingId, status, lines);
    }

    public static int? ProductImportTotalCount(JsonElement root)
    {
        var data = Unwrap(root);
        return Integer(root, "totalElements", "totalCount", "total") ?? Integer(data, "totalElements", "totalCount", "total");
    }

    private static string ProductImportErrorCode(JsonElement item)
    {
        var errors = Find(item, "validationResults", "errors", "errorMessages", "messages");
        if (errors.ValueKind == JsonValueKind.Array)
        {
            foreach (var error in errors.EnumerateArray())
            {
                var code = Text(error, "code", "errorCode", "field", "message");
                if (!string.IsNullOrWhiteSpace(code)) return code;
            }
        }
        return Text(item, "errorCode", "rejectionCode") ?? "REMOTE_VALIDATION_FAILED";
    }

    private static string NormalizeProductStatus(string value)
    {
        var normalized = value.Trim().ToUpperInvariant()
            .Replace('İ', 'I').Replace('ı', 'I').Replace('Ş', 'S').Replace('ş', 'S')
            .Replace('Ğ', 'G').Replace('ğ', 'G').Replace('Ü', 'U').Replace('ü', 'U')
            .Replace('Ö', 'O').Replace('ö', 'O').Replace('Ç', 'C').Replace('ç', 'C');
        return string.Join('_', normalized.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    public static (IReadOnlyList<JsonElement> Items, int? TotalCount) OrderPage(JsonElement root) => ListingPage(root);

    public static bool InvoiceUploaded(JsonElement root)
    {
        var value = Boolean(Unwrap(root), "hasInvoice", "HasInvoice");
        return value ?? throw new JsonException("Hepsiburada sipariş yanıtında hasInvoice alanı yok veya boolean değil.");
    }

    public static (IReadOnlyList<JsonElement> Items, int? TotalCount) PackagePage(JsonElement root)
    {
        var data = Unwrap(root);
        var items = Find(data, "items", "Items", "packages", "Packages", "packageList", "PackageList", "content", "Content", "results", "Results");
        if (items.ValueKind == JsonValueKind.Undefined && data.ValueKind == JsonValueKind.Array) items = data;

        var totalCount = Integer(data, "totalCount", "TotalCount", "totalElements", "TotalElements")
            ?? Integer(root, "totalCount", "TotalCount", "totalElements", "TotalElements");
        if (items.ValueKind != JsonValueKind.Array)
        {
            if (totalCount == 0) return ([], totalCount);
            throw new JsonException("Hepsiburada paket sayfa yanıtında paket listesi yok.");
        }

        return (items.EnumerateArray().ToArray(), totalCount);
    }

    public static (string? Status, string? CargoCompany, string? TrackingInfoCode, string? OrderNumber) PackageTrackingInfo(JsonElement root, string expectedPackageNumber)
    {
        var data = Unwrap(root);
        var items = Find(data, "items", "packages", "content");
        if (items.ValueKind == JsonValueKind.Undefined && data.ValueKind == JsonValueKind.Array) items = data;
        var candidates = items.ValueKind == JsonValueKind.Array ? items.EnumerateArray().ToArray() : [data];
        foreach (var item in candidates)
        {
            var packageNumber = Text(item, "packageNumber", "PackageNumber");
            if (!string.Equals(packageNumber, expectedPackageNumber, StringComparison.Ordinal)) continue;
            return (
                Text(item, "status", "Status"),
                Text(item, "cargoCompany", "CargoCompany", "cargoCompanyName", "CargoCompanyName"),
                Text(item, "trackingInfoCode", "TrackingInfoCode"),
                Text(item, "orderNumber", "OrderNumber", "orderNo", "OrderNo"));
        }

        throw new JsonException("Hepsiburada kargo yanıtında istenen packageNumber bulunamadı.");
    }

    public static IReadOnlyList<RemoteCargoCompany> ChangeableCargoCompanies(JsonElement root)
    {
        var data = Unwrap(root);
        var items = Find(data, "items", "cargoCompanies", "companies", "content");
        if (items.ValueKind == JsonValueKind.Undefined && data.ValueKind == JsonValueKind.Array) items = data;
        if (items.ValueKind != JsonValueKind.Array) throw new JsonException("Hepsiburada değiştirilebilir kargo firması yanıtında liste yok.");
        var companies = new List<RemoteCargoCompany>();
        foreach (var item in items.EnumerateArray())
        {
            var shortName = Text(item, "shortName", "cargoCompanyShortName", "code", "id");
            var name = Text(item, "name", "cargoCompanyName", "displayName") ?? shortName;
            if (string.IsNullOrWhiteSpace(shortName) || string.IsNullOrWhiteSpace(name)) throw new JsonException("Hepsiburada kargo firması kimliği veya adı eksik.");
            companies.Add(new(shortName.Trim(), name.Trim()));
        }
        return companies.DistinctBy(item => item.ShortName, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static IReadOnlyList<RemotePackageableLine> PackageableLineItems(JsonElement root)
    {
        var data = Unwrap(root);
        var items = Find(data, "items", "lineItems", "packageableLineItems", "content");
        if (items.ValueKind == JsonValueKind.Undefined && data.ValueKind == JsonValueKind.Array) items = data;
        if (items.ValueKind != JsonValueKind.Array) throw new JsonException("Hepsiburada paketlenebilir kalem yanıtında liste yok.");
        var lines = new List<RemotePackageableLine>();
        foreach (var item in items.EnumerateArray())
        {
            var id = Text(item, "id", "lineItemId", "orderLineId");
            var quantity = Integer(item, "quantity", "availableQuantity", "packageableQuantity") ?? 1;
            if (string.IsNullOrWhiteSpace(id) || quantity < 1) throw new JsonException("Hepsiburada paketlenebilir kalem kimliği veya miktarı eksik.");
            lines.Add(new(id.Trim(), quantity));
        }
        return lines.GroupBy(line => line.LineItemId, StringComparer.Ordinal).Select(group => new RemotePackageableLine(group.Key, group.Max(line => line.Quantity))).ToArray();
    }

    public static string PackageNumber(JsonElement root)
    {
        var data = Unwrap(root);
        var packageNumber = First(Text(root, "packageNumber", "PackageNumber"), Text(data, "packageNumber", "PackageNumber"));
        return !string.IsNullOrWhiteSpace(packageNumber) ? packageNumber.Trim() : throw new JsonException("Hepsiburada paket oluşturma yanıtında packageNumber yok.");
    }

    public static (IReadOnlyList<JsonElement> Items, int? TotalCount) ClaimPage(JsonElement root) => ListingPage(root);

    public static bool IsClaimPackageNotification(JsonElement root) =>
        !string.IsNullOrWhiteSpace(Text(root, "packageNumber", "PackageNumber"))
        && Find(root, "claims", "Claims").ValueKind == JsonValueKind.Array;

    public static IReadOnlyList<JsonElement> ClaimPackageClaims(JsonElement root)
    {
        var claims = Find(root, "claims", "Claims");
        return claims.ValueKind == JsonValueKind.Array
            ? claims.EnumerateArray().ToArray()
            : throw new JsonException("Hepsiburada talep paketi talep listesi içermiyor.");
    }

    public static string? PackageIdentity(JsonElement item) => Text(item, "packageNumber", "PackageNumber", "id", "Id");
    public static string? ReturnClaimIdentity(JsonElement item) => Text(item, "claimNumber", "ClaimNumber", "number", "Number", "claimId", "ClaimId");

    public static RemoteReturnClaim ReturnClaim(JsonElement item)
    {
        var claimId = Text(item, "claimNumber", "ClaimNumber", "number", "Number", "claimId", "ClaimId");
        var orderNumber = Text(item, "orderNumber", "OrderNumber", "orderNo", "OrderNo");
        var status = Text(item, "status", "Status");
        var claimDate = Date(item, "claimDate", "ClaimDate", "createdAt", "CreatedAt");
        if (string.IsNullOrWhiteSpace(claimId) || string.IsNullOrWhiteSpace(orderNumber) || string.IsNullOrWhiteSpace(status) || claimDate is null)
            throw new JsonException("Hepsiburada talep kaydında talep, sipariş, durum veya tarih bilgisi eksik.");

        var lineElements = new List<JsonElement>();
        var lines = Find(item, "lines", "Lines");
        if (lines.ValueKind == JsonValueKind.Array) lineElements.AddRange(lines.EnumerateArray());
        else
        {
            var line = Find(item, "line", "Line");
            if (line.ValueKind == JsonValueKind.Object) lineElements.Add(line);
            else if (Text(item, "lineItemId", "LineItemId") is not null) lineElements.Add(item);
        }
        if (lineElements.Count == 0) throw new JsonException("Hepsiburada talep kaydında sipariş kalemi bağlantısı yok.");

        var remoteLines = new List<RemoteReturnLine>(lineElements.Count);
        foreach (var line in lineElements)
        {
            var nestedLine = Find(line, "line", "Line");
            var orderLine = nestedLine.ValueKind == JsonValueKind.Object ? nestedLine : line;
            // Claims can expose the order's canonical lineItemId at the claim
            // level while the nested line object carries its own id. Prefer
            // the documented order-line identity and keep the nested id as an
            // alternate for accounts whose order payload uses that alias.
            var lineId = First(
                Text(orderLine, "lineItemId", "LineItemId"),
                Text(line, "lineItemId", "LineItemId"),
                Text(item, "lineItemId", "LineItemId"),
                Text(orderLine, "orderLineId", "OrderLineId"),
                Text(line, "orderLineId", "OrderLineId"),
                Text(item, "orderLineId", "OrderLineId"),
                Text(orderLine, "orderItemId", "OrderItemId"),
                Text(line, "orderItemId", "OrderItemId"),
                Text(item, "orderItemId", "OrderItemId"),
                Text(orderLine, "id", "Id"),
                Text(line, "id", "Id"));
            var quantity = Decimal(line, "quantity", "Quantity") ?? Decimal(item, "quantity", "Quantity");
            if (string.IsNullOrWhiteSpace(lineId) || quantity is null or <= 0)
                throw new JsonException("Hepsiburada talep kaleminde lineItemId veya geçerli miktar yok.");
            var alternateLineIds = new[]
            {
                Text(orderLine, "lineItemId", "LineItemId"),
                Text(line, "lineItemId", "LineItemId"),
                Text(item, "lineItemId", "LineItemId"),
                Text(orderLine, "orderLineId", "OrderLineId"),
                Text(line, "orderLineId", "OrderLineId"),
                Text(item, "orderLineId", "OrderLineId"),
                Text(orderLine, "orderItemId", "OrderItemId"),
                Text(line, "orderItemId", "OrderItemId"),
                Text(item, "orderItemId", "OrderItemId"),
                Text(orderLine, "id", "Id"),
                Text(line, "id", "Id")
            }
                .Where(candidate => !string.IsNullOrWhiteSpace(candidate) && !string.Equals(candidate, lineId, StringComparison.Ordinal))
                .OfType<string>()
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            remoteLines.Add(new(lineId, lineId, quantity.Value, alternateLineIds));
        }

        var delivery = ReturnDelivery(Find(item, "delivery", "Delivery"));
        var statusDate = status.ToUpperInvariant() switch
        {
            "ACCEPTED" => Date(item, "acceptedDate", "AcceptedDate"),
            "REJECTED" => Date(item, "rejectedDate", "RejectedDate", "rejecttedDate", "RejecttedDate"),
            "REFUNDED" => Date(item, "refundDate", "RefundDate"),
            "CANCELLED" => Date(item, "cancelDate", "CancelDate", "cancelledDate", "CancelledDate"),
            "INDISPUTE" => Date(item, "inDisputeDate", "InDisputeDate", "disputeDate", "DisputeDate", "disputedDate", "DisputedDate"),
            "AWAITINGPREAPPROVAL" => Date(item, "markedAwaitingPreApprovalDate", "MarkedAwaitingPreApprovalDate", "awaitingPreApprovalDate", "AwaitingPreApprovalDate"),
            "AWAITINGACTION" => Date(item, "markedAwaitingActionDate", "MarkedAwaitingActionDate", "awaitingActionDate", "AwaitingActionDate"),
            _ => null
        };
        var modifiedAt = Date(item, "lastModifiedAt", "LastModifiedAt", "lastModifiedDate", "LastModifiedDate", "lastStatusUpdateDate", "LastStatusUpdateDate", "updatedAt", "UpdatedAt", "statusDate", "StatusDate") ?? statusDate ?? claimDate.Value;
        var explanation = Text(item, "explanation", "Explanation");
        var rejection = Text(item, "merchantRejectionStatement", "MerchantRejectionStatement");
        var reasonText = string.IsNullOrWhiteSpace(rejection) ? explanation
            : string.IsNullOrWhiteSpace(explanation) ? rejection
            : $"{explanation}\n{rejection}";
        var claimType = Text(item, "claimType", "ClaimType", "type", "Type");
        var reasonCode = string.Equals(claimType, "MissingInvoice", StringComparison.OrdinalIgnoreCase)
            ? claimType
            : Text(item, "reason", "Reason") ?? claimType;
        var cargoCompany = First(
            Text(delivery, "cargoCompany", "CargoCompany", "cargoProviderName", "CargoProviderName"),
            Text(item, "cargoCompany", "CargoCompany", "cargoProviderName", "CargoProviderName"));
        var trackingNumber = First(
            delivery.ValueKind == JsonValueKind.String ? delivery.GetString() : null,
            Text(delivery, "trackingNumber", "TrackingNumber", "trackingInfoCode", "TrackingInfoCode", "deliveryBarcode", "DeliveryBarcode", "barcode", "Barcode", "code", "Code"),
            Text(item, "cargoTrackingNumber", "CargoTrackingNumber", "trackingInfoCode", "TrackingInfoCode", "trackingNumber", "TrackingNumber", "deliveryBarcode", "DeliveryBarcode"));
        var trackingLink = First(
            Text(delivery, "trackingUrl", "TrackingUrl", "trackingInfoUrl", "TrackingInfoUrl"),
            Text(item, "trackingUrl", "TrackingUrl", "trackingInfoUrl", "TrackingInfoUrl"));
        return new(
            claimId,
            orderNumber,
            status,
            reasonCode,
            reasonText,
            Date(item, "awaitingActionExpireDate", "AwaitingActionExpireDate"),
            modifiedAt,
            remoteLines,
            item.GetRawText(),
            cargoCompany,
            trackingNumber,
            trackingLink);
    }

    private static JsonElement ReturnDelivery(JsonElement delivery)
    {
        if (delivery.ValueKind == JsonValueKind.Object)
            return string.Equals(Text(delivery, "direction"), "MerchantToCustomer", StringComparison.OrdinalIgnoreCase)
                ? default
                : delivery;

        if (delivery.ValueKind != JsonValueKind.Array) return delivery;

        JsonElement fallback = default;
        foreach (var candidate in delivery.EnumerateArray())
        {
            if (candidate.ValueKind != JsonValueKind.Object) continue;
            var direction = Text(candidate, "direction");
            if (string.Equals(direction, "CustomerToMerchant", StringComparison.OrdinalIgnoreCase)) return candidate;
            if (!string.Equals(direction, "MerchantToCustomer", StringComparison.OrdinalIgnoreCase) && fallback.ValueKind == JsonValueKind.Undefined)
                fallback = candidate;
        }
        return fallback;
    }

    public static RemoteOrder? OrderFromReturnClaim(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = Unwrap(document.RootElement);
        var orderNumber = Text(root, "orderNumber", "OrderNumber", "orderNo", "OrderNo");
        if (string.IsNullOrWhiteSpace(orderNumber)) return null;

        var claimDate = Date(root, "claimDate", "ClaimDate");
        var orderedAt = Date(root, "orderDate", "OrderDate") ?? claimDate ?? DateTimeOffset.UnixEpoch;
        var modifiedAt = Date(root, "lastModifiedAt", "LastModifiedAt", "updatedAt", "UpdatedAt") ?? claimDate ?? orderedAt;
        var lineElements = new List<JsonElement>();
        var linesElement = Find(root, "lines", "Lines");
        if (linesElement.ValueKind == JsonValueKind.Array) lineElements.AddRange(linesElement.EnumerateArray());
        else if (linesElement.ValueKind == JsonValueKind.Object) lineElements.Add(linesElement);
        else
        {
            var line = Find(root, "line", "Line");
            if (line.ValueKind == JsonValueKind.Object) lineElements.Add(line);
            else if (Text(root, "lineItemId", "LineItemId") is not null) lineElements.Add(root);
        }

        var remoteLines = new List<RemoteOrderLine>(lineElements.Count);
        foreach (var entry in lineElements)
        {
            var nestedLine = Find(entry, "line", "Line");
            var line = nestedLine.ValueKind == JsonValueKind.Object ? nestedLine : entry;
            var lineId = First(
                Text(line, "lineItemId", "LineItemId"),
                Text(entry, "lineItemId", "LineItemId"),
                Text(root, "lineItemId", "LineItemId"),
                Text(line, "orderLineId", "OrderLineId"),
                Text(entry, "orderLineId", "OrderLineId"),
                Text(root, "orderLineId", "OrderLineId"),
                Text(line, "orderItemId", "OrderItemId"),
                Text(entry, "orderItemId", "OrderItemId"),
                Text(root, "orderItemId", "OrderItemId"),
                Text(line, "id", "Id"),
                Text(entry, "id", "Id"));
            var quantity = Decimal(entry, "quantity", "Quantity") ?? Decimal(line, "quantity", "Quantity") ?? Decimal(root, "quantity", "Quantity");
            if (string.IsNullOrWhiteSpace(lineId) || quantity is null or <= 0) continue;

            var sku = First(
                Text(line, "merchantSku", "MerchantSku", "sku", "Sku", "hbSku", "HBSku"),
                Text(entry, "merchantSku", "MerchantSku", "sku", "Sku", "hbSku", "HBSku"),
                Text(root, "merchantSku", "MerchantSku", "sku", "Sku", "hbSku", "HBSku")) ?? lineId;
            var unitPrice = Money(line, "merchantUnitPrice", "price", "priceAmount", "unitPrice")
                ?? Money(entry, "merchantUnitPrice", "price", "priceAmount", "unitPrice")
                ?? Money(root, "merchantUnitPrice", "price", "priceAmount", "unitPrice");
            var totalPrice = Money(line, "merchantTotalPrice", "totalPrice", "totalPriceAmount", "lineTotal")
                ?? Money(entry, "merchantTotalPrice", "totalPrice", "totalPriceAmount", "lineTotal")
                ?? Money(root, "merchantTotalPrice", "totalPrice", "totalPriceAmount", "lineTotal");
            if (unitPrice is null && totalPrice is not null) unitPrice = totalPrice.Value / quantity.Value;

            remoteLines.Add(new(
                lineId,
                sku,
                Text(line, "barcode", "Barcode", "gtin", "GTIN") ?? Text(root, "barcode", "Barcode", "gtin", "GTIN"),
                First(Text(line, "productName", "ProductName", "name", "Name"), Text(entry, "productName", "ProductName", "name", "Name"), Text(root, "productName", "ProductName", "name", "Name")) ?? sku,
                quantity.Value,
                unitPrice ?? 0m,
                Decimal(line, "vatRate", "VatRate") ?? Decimal(root, "vatRate", "VatRate") ?? 0m,
                "ClaimCreated",
                line.GetRawText()));
        }
        if (remoteLines.Count == 0) return null;

        var gross = remoteLines.Sum(line => line.UnitPrice * line.Quantity);
        var currency = Text(root, "currency", "Currency", "currencyCode", "CurrencyCode")
            ?? Text(Find(root, "price", "Price", "totalPrice", "TotalPrice"), "currency", "Currency", "currencyCode", "CurrencyCode")
            ?? "TRY";
        var customer = OrderCustomerSnapshot(root, Find(root, "customer", "Customer"));

        // A claim can arrive after the platform's order-history window. Keep
        // the reconstruction limited to claim lines and do not invent a
        // shipment package; a later authoritative order read replaces it.
        return new(
            orderNumber,
            orderNumber,
            orderedAt,
            modifiedAt,
            currency.Length == 3 ? currency.ToUpperInvariant() : "TRY",
            gross,
            0,
            gross,
            customer,
            "{}",
            "{}",
            remoteLines,
            [],
            root.GetRawText());
    }

    public static RemoteOrder ClaimPackageOrder(JsonElement root)
    {
        var packageNumber = Text(root, "packageNumber", "PackageNumber");
        var packageStatus = Text(root, "status", "Status", "packageStatus", "PackageStatus");
        var claimsElement = Find(root, "claims", "Claims");
        if (string.IsNullOrWhiteSpace(packageNumber) || string.IsNullOrWhiteSpace(packageStatus) || claimsElement.ValueKind != JsonValueKind.Array || claimsElement.GetArrayLength() == 0)
            throw new JsonException("Hepsiburada talep paketi numara, durum veya talep listesi içermiyor.");

        var parsedClaims = claimsElement.EnumerateArray()
            .Select(item =>
            {
                var claim = ReturnClaim(item);
                var order = OrderFromReturnClaim(item.GetRawText());
                return (Claim: claim, Order: order);
            })
            .ToArray();
        var orderNumbers = parsedClaims.Select(item => item.Claim.ExternalOrderId).Distinct(StringComparer.Ordinal).ToArray();
        if (orderNumbers.Length != 1 || parsedClaims.Any(item => item.Order is null))
            throw new JsonException("Hepsiburada talep paketi tek bir siparişe ve tanınan sipariş kalemlerine bağlanmalıdır.");

        var lines = parsedClaims.SelectMany(item => item.Order!.Lines)
            .GroupBy(line => line.ExternalLineId, StringComparer.Ordinal)
            .Select(group => group.First() with { Quantity = group.Max(line => line.Quantity) })
            .ToArray();
        var allocations = parsedClaims.SelectMany(item => item.Claim.Lines)
            .GroupBy(line => line.ExternalOrderLineId, StringComparer.Ordinal)
            .Select(group => new RemotePackageAllocation(group.Key, group.Sum(line => line.Quantity), 0, 0, 0, 0))
            .ToArray();
        if (lines.Length == 0 || allocations.Length == 0)
            throw new JsonException("Hepsiburada talep paketi geçerli sipariş kalemi içermiyor.");

        var occurredAt = Date(root, "createdDate", "CreatedDate", "createdAt", "CreatedAt", "packageDate", "PackageDate")
            ?? parsedClaims.Max(item => item.Claim.LastModifiedAt);
        var package = new RemotePackage(
            packageNumber,
            null,
            packageStatus,
            occurredAt,
            Text(root, "cargoCompany", "CargoCompany"),
            Text(root, "barcode", "Barcode"),
            allocations,
            CreatedBy: "REPLACEMENT");
        var order = parsedClaims[0].Order!;
        var gross = lines.Sum(line => line.UnitPrice * line.Quantity);
        var shipmentAddress = new Dictionary<string, string?>
        {
            ["recipientName"] = Text(root, "recipientName", "RecipientName"),
            ["shippingAddressDetail"] = Text(root, "shippingAddressDetail", "ShippingAddressDetail"),
            ["shippingCountryCode"] = Text(root, "shippingCountryCode", "ShippingCountryCode"),
            ["shippingDistrict"] = Text(root, "shippingDistrict", "ShippingDistrict"),
            ["shippingTown"] = Text(root, "shippingTown", "ShippingTown"),
            ["shippingCity"] = Text(root, "shippingCity", "ShippingCity")
        };
        return order with
        {
            LastModifiedAt = occurredAt,
            GrossAmount = gross,
            NetAmount = gross,
            ShipmentAddressSnapshotJson = JsonSerializer.Serialize(shipmentAddress),
            Lines = lines,
            Packages = [package],
            RawJson = root.GetRawText()
        };
    }

    public static RemoteOrderPackage OrderPackage(JsonElement item)
    {
        var orderNumber = Text(item, "orderNumber", "OrderNumber", "orderNo", "OrderNo");
        var packageNumber = Text(item, "packageNumber", "PackageNumber");
        if (string.IsNullOrWhiteSpace(orderNumber) || string.IsNullOrWhiteSpace(packageNumber))
            throw new JsonException("Hepsiburada paket yanıtında orderNumber veya packageNumber yok; güvenli sipariş eşlemesi yapılamadı.");

        var occurredAt = Date(item, "lastStatusUpdateDate", "LastStatusUpdateDate", "shippedDate", "ShippedDate", "unpackedDate", "UnpackedDate", "packageDate", "PackageDate", "updatedAt", "UpdatedAt", "createdAt", "CreatedAt", "orderDate", "OrderDate");
        if (occurredAt is null) throw new JsonException("Hepsiburada paket yanıtında kullanılabilir tarih alanı yok.");

        var allocations = new List<RemotePackageAllocation>();
        var items = Find(item, "lineItems", "LineItems", "items", "Items", "orderItems", "OrderItems");
        if (items.ValueKind == JsonValueKind.Array)
        {
            foreach (var line in items.EnumerateArray())
            {
                var lineId = Text(line, "lineItemId", "LineItemId", "orderLineId", "OrderLineId", "orderItemId", "OrderItemId");
                var quantity = Decimal(line, "quantity", "Quantity", "allocatedQuantity", "AllocatedQuantity");
                if (!string.IsNullOrWhiteSpace(lineId) && quantity is > 0)
                    allocations.Add(new(lineId, quantity.Value, 0, 0, 0, 0));
            }
        }

        var package = new RemotePackage(
            packageNumber,
            null,
            Text(item, "status", "Status", "packageStatus", "PackageStatus") ?? "Open",
            occurredAt.Value,
            Text(item, "cargoCompany", "CargoCompany", "cargoCompanyName", "CargoCompanyName"),
            Text(item, "trackingInfoCode", "TrackingInfoCode"),
            allocations,
            GrossAmount: Money(item, "totalPrice", "TotalPrice", "totalAmount", "TotalAmount") ?? 0m,
            NetAmount: Money(item, "netAmount", "NetAmount", "totalPrice", "TotalPrice") ?? 0m,
            Invoice: InvoiceObservation(item));
        return new(orderNumber, package, PackageOrderSnapshot(item, orderNumber, package));
    }

    public static RemoteOrderPackage OrderStatusPackage(JsonElement item, string status)
    {
        var orderNumber = Text(item, "orderNumber", "OrderNumber", "orderNo", "OrderNo");
        var packageNumber = Text(item, "packageNumber", "PackageNumber");
        if (string.IsNullOrWhiteSpace(orderNumber) || string.IsNullOrWhiteSpace(packageNumber))
            throw new JsonException("Hepsiburada sevkiyat durumunda sipariş veya paket numarası yok.");

        var rawStatus = status.Trim().ToUpperInvariant() switch
        {
            "SHIPPED" => "Shipped",
            "DELIVERED" => "Delivered",
            "UNDELIVERED" => "Undelivered",
            _ => throw new JsonException("Hepsiburada sevkiyat durumu desteklenmiyor.")
        };
        var occurredAt = Date(item,
            "ShippedDate", "shippedDate",
            "DeliveredDate", "deliveredDate",
            "UndeliveredDate", "undeliveredDate",
            "lastStatusUpdateDate", "LastStatusUpdateDate");
        if (occurredAt is null) throw new JsonException("Hepsiburada sevkiyat durumunda olay tarihi yok.");

        var package = new RemotePackage(
            packageNumber,
            null,
            rawStatus,
            occurredAt.Value,
            null,
            Text(item, "trackingInfoCode", "TrackingInfoCode"),
            [],
            Invoice: rawStatus == "Delivered" ? InvoiceObservation(item) : null,
            CreatedBy: "HEPSIBURADA_STATUS_FEED",
            IsStatusObservation: true);
        return new(orderNumber, package);
    }

    public static string? OrderNumber(JsonElement item) => Text(item, "orderNumber", "OrderNumber", "orderNo", "OrderNo");

    public static RemoteOrder PaidOrderLine(JsonElement item)
    {
        var orderNumber = OrderNumber(item);
        if (string.IsNullOrWhiteSpace(orderNumber)) throw new JsonException("Hepsiburada ödemesi tamamlanmış sipariş satırında orderNumber yok.");
        var nestedLines = Find(item, "lineItems", "LineItems", "orderItems", "OrderItems");
        if (nestedLines.ValueKind == JsonValueKind.Array || Find(item, "items", "Items").ValueKind == JsonValueKind.Array)
            return Order(item, orderNumber);

        var line = MapLine(item);
        var orderedAt = Date(item, "orderDate", "OrderDate", "orderedAt", "OrderedAt", "createdAt", "CreatedAt");
        if (orderedAt is null) throw new JsonException("Hepsiburada ödemesi tamamlanmış sipariş satırında orderDate yok.");
        var gross = Money(item, "totalPrice", "TotalPrice", "lineTotal", "LineTotal") ?? line.UnitPrice * line.Quantity;
        var discount = Money(item, "totalMerchantDiscount", "TotalMerchantDiscount", "merchantDiscount", "MerchantDiscount", "discountAmount", "DiscountAmount") ?? 0m;
        var invoice = Find(item, "invoice", "Invoice");
        var invoiceAddress = Find(invoice, "address", "Address");
        if (invoiceAddress.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) invoiceAddress = Find(item, "invoiceAddress", "InvoiceAddress", "billingAddress", "BillingAddress");
        var customer = Find(item, "customer", "Customer");
        var customerSnapshot = customer.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            ? JsonSerializer.Serialize(new { name = Text(item, "customerName", "CustomerName", "recipientName", "RecipientName"), email = Text(item, "email", "Email"), phoneNumber = Text(item, "phoneNumber", "PhoneNumber") })
            : Snapshot(customer);
        customerSnapshot = EnrichOrderSnapshot(item, customerSnapshot);
        var status = Text(item, "status", "Status", "orderStatus", "OrderStatus") ?? "Open";
        var currency = Currency(item, "unitPrice", "UnitPrice", "totalPrice", "TotalPrice") ?? "TRY";
        return new(
            orderNumber,
            orderNumber,
            orderedAt.Value,
            Date(item, "lastStatusUpdateDate", "LastStatusUpdateDate", "lastModifiedAt", "LastModifiedAt") ?? orderedAt.Value,
            currency.Length == 3 ? currency.ToUpperInvariant() : "TRY",
            Math.Max(gross, gross - discount),
            discount,
            Math.Max(0m, gross - discount),
            customerSnapshot,
            Snapshot(Find(item, "shippingAddress", "ShippingAddress", "deliveryAddress", "DeliveryAddress")),
            Snapshot(invoiceAddress.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? invoice : invoiceAddress),
            [line],
            [],
            item.GetRawText(),
            Date(item, "dueDate", "DueDate", "shipmentDueAt", "ShipmentDueAt"),
            Text(item, "paymentStatus", "PaymentStatus") ?? "Received",
            status.Contains("cancel", StringComparison.OrdinalIgnoreCase) ? "CANCELLED" : "NOT_CANCELLED",
            LifecycleStatus: status);
    }

    private static RemoteOrder PackageOrderSnapshot(JsonElement item, string orderNumber, RemotePackage package)
    {
        var orderedAt = Date(item, "orderDate", "OrderDate", "orderedAt", "OrderedAt", "createdAt", "CreatedAt") ?? package.OccurredAt;
        var itemArray = Find(item, "lineItems", "LineItems", "items", "Items", "orderItems", "OrderItems");
        IReadOnlyList<RemoteOrderLine> lines = itemArray.ValueKind == JsonValueKind.Array
            ? itemArray.EnumerateArray().Select(line => MapLine(line)).GroupBy(line => line.ExternalLineId, StringComparer.Ordinal).Select(group => group.First()).ToArray()
            : [];
        if (lines.Count == 0 && !string.IsNullOrWhiteSpace(Text(item, "lineItemId", "LineItemId", "orderLineId", "OrderLineId")))
            lines = [MapLine(item)];
        var shipmentAddress = Find(item, "shippingAddress", "ShippingAddress", "deliveryAddress", "DeliveryAddress");
        if (shipmentAddress.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            shipmentAddress = AddressSnapshot(item, "shippingAddressDetail", "recipientName", "shippingCountryCode", "shippingDistrict", "shippingTown", "shippingCity", "shippingPostalCode", "email", "phoneNumber");
        var invoice = Find(item, "invoice", "Invoice", "invoiceAddress", "InvoiceAddress", "billingAddress", "BillingAddress");
        if (invoice.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            invoice = AddressSnapshot(item, "billingAddress", "companyName", "taxOffice", "taxNumber", "identityNo", "billingDistrict", "billingTown", "billingCity", "billingPostalCode");
        var customerSnapshot = OrderCustomerSnapshot(item, Find(item, "customer", "Customer"));
        var status = Text(item, "status", "Status", "packageStatus", "PackageStatus") ?? package.RawStatus;
        var currency = Currency(item, "totalPrice", "TotalPrice")
            ?? (itemArray.ValueKind == JsonValueKind.Array ? itemArray.EnumerateArray().Select(line => Currency(line, "price", "Price", "totalPrice", "TotalPrice")).FirstOrDefault(value => value is not null) : null)
            ?? "TRY";
        var gross = package.GrossAmount > 0 ? package.GrossAmount : lines.Sum(line => line.UnitPrice * line.Quantity);
        return new(
            orderNumber,
            orderNumber,
            orderedAt,
            package.OccurredAt,
            currency.Length == 3 ? currency.ToUpperInvariant() : "TRY",
            gross,
            package.DiscountAmount,
            package.NetAmount > 0 ? package.NetAmount : Math.Max(0m, gross - package.DiscountAmount),
            customerSnapshot,
            Snapshot(shipmentAddress),
            Snapshot(invoice),
            lines,
            [package],
            item.GetRawText(),
            Date(item, "dueDate", "DueDate", "shipmentDueAt", "ShipmentDueAt"),
            Text(item, "paymentStatus", "PaymentStatus") ?? "Received",
            status.Contains("cancel", StringComparison.OrdinalIgnoreCase) ? "CANCELLED" : "NOT_CANCELLED");
    }

    private static JsonElement AddressSnapshot(JsonElement source, params string[] names)
    {
        var address = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            var value = Text(source, name);
            if (!string.IsNullOrWhiteSpace(value)) address[name] = value;
        }
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(address));
        return document.RootElement.Clone();
    }

    private static string? Currency(JsonElement source, params string[] moneyFields)
    {
        foreach (var field in moneyFields)
        {
            var money = Find(source, field);
            var currency = Text(money, "currency", "Currency", "currencyCode", "CurrencyCode");
            if (!string.IsNullOrWhiteSpace(currency)) return currency;
        }
        return Text(source, "currency", "Currency", "currencyCode", "CurrencyCode");
    }

    private static string OrderCustomerSnapshot(JsonElement order, JsonElement customer, bool? invoiceUploaded = null)
    {
        var snapshot = customer.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? "{}" : Snapshot(customer);
        var name = First(
            Text(customer, "fullName", "customerName", "recipientName", "buyerName", "name"),
            Text(order, "customerName", "recipientName", "buyerName", "name"));
        var email = First(Text(customer, "customerEmail", "email"), Text(order, "customerEmail", "email"));
        var phone = First(Text(customer, "customerPhone", "customerPhoneNumber", "phone", "phoneNumber"), Text(order, "customerPhone", "customerPhoneNumber", "phone", "phoneNumber"));
        var customerId = First(Text(customer, "customerId", "id"), Text(order, "customerId"));
        snapshot = EnrichSnapshot(snapshot,
            ("name", name),
            ("email", email),
            ("phoneNumber", phone),
            ("customerId", customerId));
        return EnrichOrderSnapshot(order, snapshot, invoiceUploaded);
    }

    private static string EnrichOrderSnapshot(JsonElement source, string snapshot, bool? invoiceUploaded = null)
    {
        var invoiceStatus = Text(source, "marketplaceInvoiceStatus", "MarketplaceInvoiceStatus", "invoiceStatus", "InvoiceStatus");
        if (string.IsNullOrWhiteSpace(invoiceStatus) && (Boolean(source, "hasInvoice", "HasInvoice") ?? invoiceUploaded) is { } hasInvoice)
            invoiceStatus = hasInvoice ? "INVOICED" : "NOT_INVOICED";

        var cargo = Text(source, "cargoCompany", "CargoCompany", "cargoCompanyName", "CargoCompanyName");
        if (string.IsNullOrWhiteSpace(cargo))
        {
            var cargoModel = Find(source, "cargoCompanyModel", "CargoCompanyModel");
            cargo = Text(cargoModel, "name", "Name", "shortName", "ShortName");
        }
        return EnrichSnapshot(snapshot, ("marketplaceInvoiceStatus", invoiceStatus), ("marketplaceCargoProviderName", cargo));
    }

    private static string EnrichSnapshot(string snapshot, params (string Name, string? Value)[] fields)
    {
        if (!fields.Any(field => !string.IsNullOrWhiteSpace(field.Value))) return snapshot;
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(snapshot) ? "{}" : snapshot);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return snapshot;
            var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in document.RootElement.EnumerateObject()) values[property.Name] = property.Value.Clone();
            foreach (var field in fields)
                if (!string.IsNullOrWhiteSpace(field.Value)) values[field.Name] = field.Value;
            return JsonSerializer.Serialize(values);
        }
        catch (JsonException) { return snapshot; }
    }

    internal static string? ImageUrl(JsonElement source)
    {
        if (source.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in source.EnumerateObject())
            {
                if (property.Name.Equals("imageUrl", StringComparison.OrdinalIgnoreCase)
                    || property.Name.Equals("productImageUrl", StringComparison.OrdinalIgnoreCase)
                    || property.Name.Equals("productImageUrlFormat", StringComparison.OrdinalIgnoreCase)
                    || property.Name.Equals("emaproductImageUrlFormat", StringComparison.OrdinalIgnoreCase)
                    || property.Name.Equals("productImage", StringComparison.OrdinalIgnoreCase)
                    || property.Name.Equals("image", StringComparison.OrdinalIgnoreCase)
                    || property.Name.Equals("images", StringComparison.OrdinalIgnoreCase)
                    || property.Name.Equals("imageUrls", StringComparison.OrdinalIgnoreCase)
                    || property.Name.Equals("productImages", StringComparison.OrdinalIgnoreCase)
                    || property.Name.Equals("productImageUrls", StringComparison.OrdinalIgnoreCase))
                {
                    var value = ImageUrlValue(property.Value);
                    if (!string.IsNullOrWhiteSpace(value)) return value;
                }
                var nested = ImageUrl(property.Value);
                if (!string.IsNullOrWhiteSpace(nested)) return nested;
            }
        }
        else if (source.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in source.EnumerateArray())
            {
                var nested = ImageUrl(item);
                if (!string.IsNullOrWhiteSpace(nested)) return nested;
            }
        }
        return null;
    }

    private static string? ImageUrlValue(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString()?.Trim();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray())
            {
                var nested = ImageUrlValue(item);
                if (!string.IsNullOrWhiteSpace(nested)) return nested;
            }
        if (value.ValueKind == JsonValueKind.Object)
        {
            var url = Text(value, "url", "imageUrl", "src", "href", "value");
            if (!string.IsNullOrWhiteSpace(url)) return url.Trim();
            return ImageUrl(value);
        }
        return null;
    }

    public static RemoteOrder Order(JsonElement root, string requestedOrderNumber)
    {
        var order = Unwrap(root);
        var orderNumber = Text(order, "orderNumber", "OrderNumber", "orderNo", "OrderNo") ?? requestedOrderNumber;
        if (string.IsNullOrWhiteSpace(orderNumber)) throw new JsonException("Hepsiburada sipariş numarası eksik.");
        var orderedAt = Date(order, "orderDate", "OrderDate", "orderedAt", "OrderedAt", "createdAt", "CreatedAt");
        if (orderedAt is null) throw new JsonException("Hepsiburada sipariş tarihi eksik veya geçersiz.");
        var linesElement = Find(order, "lineItems", "LineItems", "items", "Items", "orderItems", "OrderItems");
        if (linesElement.ValueKind == JsonValueKind.Object && TryFind(linesElement, out var nested, "items", "Items", "lineItems", "LineItems")) linesElement = nested;
        if (linesElement.ValueKind != JsonValueKind.Array) throw new JsonException("Hepsiburada sipariş yanıtında satır listesi yok.");

        var lineItems = linesElement.EnumerateArray().ToArray();
        var lines = lineItems.Select(item => MapLine(item, missingStatus: null)).ToList();
        var gross = Money(order, "totalPrice", "TotalPrice", "totalAmount", "TotalAmount", "grossAmount", "GrossAmount")
            ?? lines.Sum(line => line.UnitPrice * line.Quantity);
        var discount = Money(order, "discountAmount", "DiscountAmount", "totalDiscount", "TotalDiscount") ?? 0m;
        var net = Money(order, "netAmount", "NetAmount", "payableAmount", "PayableAmount") ?? Math.Max(0m, gross - discount);
        var currency = Text(order, "currency", "Currency", "currencyCode", "CurrencyCode")
            ?? Text(Find(order, "totalPrice", "TotalPrice"), "currency", "Currency")
            ?? "TRY";
        var orderStatus = Text(order, "status", "Status", "orderStatus", "OrderStatus");
        var status = string.Equals(orderStatus?.Trim(), "ClaimCreated", StringComparison.OrdinalIgnoreCase)
            ? ConsistentLineItemStatus(lines)
            : orderStatus ?? ConsistentLineItemStatus(lines);
        var paymentStatus = Text(order, "paymentStatus", "PaymentStatus") ?? "Received";
        var customer = Find(order, "customer", "Customer");
        var shipmentAddress = Find(order, "deliveryAddress", "DeliveryAddress", "shipmentAddress", "ShipmentAddress");
        var invoiceAddress = Find(order, "invoiceAddress", "InvoiceAddress", "billingAddress", "BillingAddress");
        if (invoiceAddress.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            var invoice = Find(order, "invoice", "Invoice");
            invoiceAddress = Find(invoice, "address", "Address");
            if (invoiceAddress.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
                invoiceAddress = invoice;
        }
        var modified = Date(order, "lastStatusUpdateDate", "LastStatusUpdateDate", "lastModifiedAt", "LastModifiedAt") ?? orderedAt.Value;
        var invoiceUploaded = Boolean(order, "hasInvoice", "HasInvoice")
            ?? lineItems.Select(item => Boolean(item, "hasInvoice", "HasInvoice")).FirstOrDefault(value => value.HasValue);
        var packages = Packages(order, lines, orderedAt.Value, invoiceUploaded);
        var dueAt = Date(order, "dueDate", "DueDate", "shipmentDueAt", "ShipmentDueAt")
            ?? lineItems.Select(item => Date(item, "dueDate", "DueDate", "shipmentDueAt", "ShipmentDueAt")).FirstOrDefault(value => value.HasValue);
        // The common sales model uses the order number for GetAsync lookups. Hepsiburada's
        // detail endpoint is keyed by orderNumber, so keep that value as the stable key.
        return new(
            orderNumber,
            orderNumber,
            orderedAt.Value,
            modified,
            currency.Length == 3 ? currency.ToUpperInvariant() : "TRY",
            Math.Max(gross, net + discount),
            discount,
            net,
            OrderCustomerSnapshot(order, customer, invoiceUploaded),
            Snapshot(shipmentAddress),
            Snapshot(invoiceAddress),
            lines,
            packages,
            order.GetRawText(),
            dueAt,
            paymentStatus,
            status?.Contains("cancel", StringComparison.OrdinalIgnoreCase) == true ? "CANCELLED" : "NOT_CANCELLED",
            LifecycleStatus: status);
    }

    private static string? ConsistentLineItemStatus(IReadOnlyList<RemoteOrderLine> lines)
    {
        var statuses = lines
            .Select(line => line.RawStatus?.Trim())
            .Where(status => !string.IsNullOrWhiteSpace(status)
                && !string.Equals(status, "ClaimCreated", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // The order detail normally carries an order-level status. Some
        // responses only expose it on each line item, so use that as a
        // fallback only when all available line statuses agree.
        return statuses.Length == 1 ? statuses[0] : null;
    }

    private static RemoteOrderLine MapLine(JsonElement line, string? missingStatus = "Open")
    {
        var lineId = Text(line, "id", "Id", "lineItemId", "LineItemId", "orderLineId", "OrderLineId");
        var sku = Text(line, "merchantSku", "MerchantSku", "sellerSku", "SellerSku")
            ?? Text(line, "sku", "Sku", "hbSku", "HBSku");
        if (string.IsNullOrWhiteSpace(lineId) || string.IsNullOrWhiteSpace(sku)) throw new JsonException("Hepsiburada sipariş satırının kimlik veya SKU alanı eksik.");
        var quantity = Decimal(line, "quantity", "Quantity") ?? 0m;
        var unitPrice = Money(line, "price", "Price", "unitPrice", "UnitPrice") ?? 0m;
        if (unitPrice == 0m)
        {
            var total = Money(line, "totalPrice", "TotalPrice", "lineTotal", "LineTotal");
            if (total is not null && quantity > 0) unitPrice = total.Value / quantity;
        }
        var status = Text(line, "status", "Status", "lineStatus", "LineStatus") ?? missingStatus ?? string.Empty;
        var cancelled = status.Contains("cancel", StringComparison.OrdinalIgnoreCase) ? quantity : 0m;
        var snapshot = EnrichSnapshot(line.GetRawText(), ("imageUrl", ImageUrl(line)));
        return new(lineId, sku, Text(line, "barcode", "Barcode", "productBarcode", "ProductBarcode"), Text(line, "name", "Name", "productName", "ProductName") ?? sku, quantity, unitPrice, Decimal(line, "vatRate", "VatRate") ?? 0m, status, snapshot, cancelled);
    }

    private static IReadOnlyList<RemotePackage> Packages(JsonElement order, IReadOnlyList<RemoteOrderLine> lines, DateTimeOffset orderDate, bool? orderInvoiceUploaded)
    {
        var element = Find(order, "packages", "Packages", "shipments", "Shipments");
        if (element.ValueKind != JsonValueKind.Array) return [];
        var packages = new List<RemotePackage>();
        foreach (var package in element.EnumerateArray())
        {
            var packageId = Text(package, "packageNumber", "PackageNumber", "packageId", "PackageId", "id", "Id");
            var packageItems = Find(package, "items", "Items", "orderItems", "OrderItems", "lineItems", "LineItems");
            if (string.IsNullOrWhiteSpace(packageId) || packageItems.ValueKind != JsonValueKind.Array) continue;
            var status = Text(package, "status", "Status") ?? "UNKNOWN";
            var tracking = Text(package, "trackingInfoCode", "TrackingInfoCode");
            var invoice = InvoiceObservation(package) ?? (orderInvoiceUploaded is { } hasInvoice
                ? new RemotePackageInvoiceObservation(hasInvoice ? "INVOICED" : "NOT_INVOICED", null, null, null)
                : null);
            var allocations = new List<RemotePackageAllocation>();
            foreach (var item in packageItems.EnumerateArray())
            {
                var lineId = Text(item, "orderLineId", "OrderLineId", "lineItemId", "LineItemId", "orderItemId", "OrderItemId");
                var quantity = Decimal(item, "quantity", "Quantity", "allocatedQuantity", "AllocatedQuantity");
                if (lineId is null || quantity is null || quantity <= 0 || !lines.Any(line => string.Equals(line.ExternalLineId, lineId, StringComparison.Ordinal))) continue;
                allocations.Add(new(lineId, quantity.Value, 0, 0, 0, 0));
            }
            if (allocations.Count == 0) continue;
            var occurred = Date(package, "shippedDate", "ShippedDate", "createdAt", "CreatedAt") ?? orderDate;
            packages.Add(new RemotePackage(packageId, null, status, occurred, Text(package, "cargoCompany", "CargoCompany"), tracking, allocations, Invoice: invoice));
        }
        return packages;
    }

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

    private static JsonElement Unwrap(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array) return root;
        foreach (var name in new[] { "data", "Data", "result", "Result" })
            if (TryFind(root, out var child, name) && child.ValueKind is JsonValueKind.Object or JsonValueKind.Array) return child;
        return root;
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

    private static string Snapshot(JsonElement value) => value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? "{}" : value.GetRawText();
    private static string? First(params string?[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    private static RemotePackageInvoiceObservation? InvoiceObservation(JsonElement source)
    {
        var status = Text(source, "invoiceStatus", "InvoiceStatus");
        var uploaded = Boolean(source, "hasInvoice", "HasInvoice");
        status ??= uploaded is { } hasInvoice ? (hasInvoice ? "INVOICED" : "NOT_INVOICED") : null;
        return status is null
            ? null
            : new(status, Text(source, "invoiceNumber", "InvoiceNumber"), Text(source, "invoiceUrl", "InvoiceUrl"), Date(source, "invoiceUpdatedAt", "InvoiceUpdatedAt"));
    }
    private static bool? Boolean(JsonElement element, params string[] names)
    {
        var value = Find(element, names);
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False) return value.GetBoolean();
        if (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var parsed)) return parsed;
        return null;
    }
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
    private static string? Text(JsonElement element, params string[] names)
    {
        var value = Find(element, names);
        if (value.ValueKind == JsonValueKind.String) return value.GetString();
        if (value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False) return value.ToString();
        return null;
    }
    private static decimal? Decimal(JsonElement element, params string[] names)
    {
        var value = Find(element, names);
        if (value.ValueKind == JsonValueKind.Object) value = Find(value, "amount", "Amount", "value", "Value");
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number)) return number;
        if (value.ValueKind == JsonValueKind.String && System.Decimal.TryParse(value.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out number)) return number;
        return null;
    }
    private static decimal? Money(JsonElement element, params string[] names) => Decimal(element, names);
    private static int? Integer(JsonElement element, params string[] names)
    {
        var value = Find(element, names);
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number)) return number;
        return null;
    }
    private static DateTimeOffset? Date(JsonElement element, params string[] names)
    {
        var value = Text(element, names);
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date) ? date : null;
    }
    private static JsonElement Find(JsonElement element, params string[] names) => TryFind(element, out var value, names) ? value : default;
    private static bool TryFind(JsonElement element, out JsonElement value, params string[] names)
    {
        if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject())
                if (names.Any(name => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))) { value = property.Value; return true; }
        value = default;
        return false;
    }
}
