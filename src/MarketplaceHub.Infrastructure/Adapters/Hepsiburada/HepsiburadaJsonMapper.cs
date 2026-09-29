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
        var itemArray = Find(data, "items", "categories", "attributes", "categoryAttributes", "values", "content", "data");
        if (itemArray.ValueKind == JsonValueKind.Undefined && data.ValueKind == JsonValueKind.Array) itemArray = data;
        if (itemArray.ValueKind != JsonValueKind.Array) throw new JsonException("Hepsiburada referans yanıtında veri listesi yok.");

        var totalCount = Integer(root, "totalElements", "totalCount", "TotalElements", "TotalCount")
            ?? Integer(data, "totalElements", "totalCount", "TotalElements", "TotalCount");
        var totalPages = Integer(root, "totalPages", "TotalPages") ?? Integer(data, "totalPages", "TotalPages");
        var items = new List<RemoteReferenceItem>();
        foreach (var item in itemArray.EnumerateArray())
        {
            switch (resourceType)
            {
                case "CATEGORIES":
                {
                    var id = Text(item, "categoryId", "id", "categoryID");
                    var name = Text(item, "name");
                    if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name)) throw new JsonException("Hepsiburada kategori kimliği veya adı eksik.");
                    var path = Text(item, "paths", "path") ?? name;
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
                    var id = Text(item, "id", "valueId", "attributeValueId");
                    var name = Text(item, "value", "name");
                    if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name)) throw new JsonException("Hepsiburada enum değer kimliği veya adı eksik.");
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

    public static (IReadOnlyList<JsonElement> Items, int? TotalCount) OrderPage(JsonElement root) => ListingPage(root);

    public static (IReadOnlyList<JsonElement> Items, int? TotalCount) PackagePage(JsonElement root) => ListingPage(root);

    public static (IReadOnlyList<JsonElement> Items, int? TotalCount) ClaimPage(JsonElement root) => ListingPage(root);

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
            var lineId = Text(line, "lineItemId", "LineItemId", "id", "Id");
            var quantity = Decimal(line, "quantity", "Quantity") ?? Decimal(item, "quantity", "Quantity");
            if (string.IsNullOrWhiteSpace(lineId) || quantity is null or <= 0)
                throw new JsonException("Hepsiburada talep kaleminde lineItemId veya geçerli miktar yok.");
            remoteLines.Add(new(lineId, lineId, quantity.Value));
        }

        var delivery = Find(item, "delivery", "Delivery");
        var statusDate = status.ToUpperInvariant() switch
        {
            "ACCEPTED" => Date(item, "acceptedDate", "AcceptedDate"),
            "REJECTED" => Date(item, "rejectedDate", "RejectedDate", "rejecttedDate", "RejecttedDate"),
            "REFUNDED" => Date(item, "refundDate", "RefundDate"),
            "CANCELLED" => Date(item, "cancelDate", "CancelDate", "cancelledDate", "CancelledDate"),
            _ => null
        };
        var modifiedAt = Date(item, "lastModifiedAt", "LastModifiedAt", "updatedAt", "UpdatedAt") ?? statusDate ?? claimDate.Value;
        var explanation = Text(item, "explanation", "Explanation");
        var rejection = Text(item, "merchantRejectionStatement", "MerchantRejectionStatement");
        var reasonText = string.IsNullOrWhiteSpace(rejection) ? explanation
            : string.IsNullOrWhiteSpace(explanation) ? rejection
            : $"{explanation}\n{rejection}";
        return new(
            claimId,
            orderNumber,
            status,
            Text(item, "reason", "Reason") ?? Text(item, "claimType", "ClaimType", "type", "Type"),
            reasonText,
            Date(item, "awaitingActionExpireDate", "AwaitingActionExpireDate"),
            modifiedAt,
            remoteLines,
            item.GetRawText(),
            Text(delivery, "cargoCompany", "CargoCompany", "cargoProviderName", "CargoProviderName"),
            Text(delivery, "trackingNumber", "TrackingNumber", "barcode", "Barcode", "code", "Code"),
            Text(delivery, "trackingUrl", "TrackingUrl", "trackingInfoUrl", "TrackingInfoUrl"));
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
            Text(item, "trackingInfoCode", "TrackingInfoCode", "barcode", "Barcode"),
            allocations);
        return new(orderNumber, package);
    }

    public static string? OrderNumber(JsonElement item) => Text(item, "orderNumber", "OrderNumber", "orderNo", "OrderNo");

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

        var lines = linesElement.EnumerateArray().Select(MapLine).ToList();
        var gross = Money(order, "totalPrice", "TotalPrice", "totalAmount", "TotalAmount", "grossAmount", "GrossAmount")
            ?? lines.Sum(line => line.UnitPrice * line.Quantity);
        var discount = Money(order, "discountAmount", "DiscountAmount", "totalDiscount", "TotalDiscount") ?? 0m;
        var net = Money(order, "netAmount", "NetAmount", "payableAmount", "PayableAmount") ?? Math.Max(0m, gross - discount);
        var currency = Text(order, "currency", "Currency", "currencyCode", "CurrencyCode")
            ?? Text(Find(order, "totalPrice", "TotalPrice"), "currency", "Currency")
            ?? "TRY";
        var status = Text(order, "status", "Status", "orderStatus", "OrderStatus") ?? "Open";
        var paymentStatus = Text(order, "paymentStatus", "PaymentStatus") ?? "Received";
        var customer = Find(order, "customer", "Customer");
        var shipmentAddress = Find(order, "deliveryAddress", "DeliveryAddress", "shipmentAddress", "ShipmentAddress");
        var invoiceAddress = Find(order, "invoiceAddress", "InvoiceAddress", "billingAddress", "BillingAddress");
        var packages = Packages(order, lines, orderedAt.Value);
        var modified = Date(order, "lastStatusUpdateDate", "LastStatusUpdateDate", "lastModifiedAt", "LastModifiedAt") ?? orderedAt.Value;
        var dueAt = Date(order, "dueDate", "DueDate", "shipmentDueAt", "ShipmentDueAt");
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
            Snapshot(customer),
            Snapshot(shipmentAddress),
            Snapshot(invoiceAddress),
            lines,
            packages,
            order.GetRawText(),
            dueAt,
            paymentStatus,
            status.Contains("cancel", StringComparison.OrdinalIgnoreCase) ? "CANCELLED" : "NOT_CANCELLED");
    }

    private static RemoteOrderLine MapLine(JsonElement line)
    {
        var lineId = Text(line, "id", "Id", "lineItemId", "LineItemId", "orderLineId", "OrderLineId");
        var sku = Text(line, "merchantSku", "MerchantSku", "sku", "Sku", "hbSku", "HBSku");
        if (string.IsNullOrWhiteSpace(lineId) || string.IsNullOrWhiteSpace(sku)) throw new JsonException("Hepsiburada sipariş satırının kimlik veya SKU alanı eksik.");
        var quantity = Decimal(line, "quantity", "Quantity") ?? 0m;
        var unitPrice = Money(line, "price", "Price", "unitPrice", "UnitPrice") ?? 0m;
        if (unitPrice == 0m)
        {
            var total = Money(line, "totalPrice", "TotalPrice", "lineTotal", "LineTotal");
            if (total is not null && quantity > 0) unitPrice = total.Value / quantity;
        }
        var status = Text(line, "status", "Status", "lineStatus", "LineStatus") ?? "Open";
        var cancelled = status.Contains("cancel", StringComparison.OrdinalIgnoreCase) ? quantity : 0m;
        return new(lineId, sku, Text(line, "barcode", "Barcode"), Text(line, "name", "Name", "productName", "ProductName") ?? sku, quantity, unitPrice, Decimal(line, "vatRate", "VatRate") ?? 0m, status, line.GetRawText(), cancelled);
    }

    private static IReadOnlyList<RemotePackage> Packages(JsonElement order, IReadOnlyList<RemoteOrderLine> lines, DateTimeOffset orderDate)
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
            var tracking = Text(package, "trackingInfoCode", "TrackingInfoCode", "barcode", "Barcode");
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
            packages.Add(new RemotePackage(packageId, null, status, occurred, Text(package, "cargoCompany", "CargoCompany"), tracking, allocations));
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

    private static string Snapshot(JsonElement value) => value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? "{}" : value.GetRawText();
    private static string? First(params string?[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
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
