using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;

namespace MarketplaceHub.Infrastructure.Adapters.Pazarama;

// Maps Pazarama's order list (POST order/getOrdersForApi) to the package-shaped order stream the
// processor merges for every marketplace. Pazarama tracks status per order item and has no
// package concept in the list, so one package per order carries every item; its status is the
// furthest one among the items that were not cancelled. Item statuses are translated to the
// Trendyol-style package statuses (Created, Picking, Shipped, Delivered, Cancelled, ...) the
// panel already understands.
public static class PazaramaOrderMapper
{
    public static AdapterPageResult<RemoteOrder> Page(JsonElement root, int pageSize)
    {
        var rows = new List<RemoteOrder>();
        var issues = new List<AdapterPageIssue>();
        var list = OrderArray(root) ?? throw new JsonException("Pazarama order list has no data array.");
        foreach (var order in list.EnumerateArray())
        {
            var orderNumber = Text(order, "orderNumber");
            if (orderNumber is null)
            {
                issues.Add(new("ORDER_PACKAGE_INVALID", Text(order, "orderId") ?? "unknown", "Pazarama siparişi içeri alınmadı: sipariş numarası yok."));
                continue;
            }
            try { rows.Add(Order(order, orderNumber, issues)); }
            catch (JsonException exception)
            {
                issues.Add(new("ORDER_PACKAGE_INVALID", orderNumber, $"Pazarama siparişi {orderNumber} içeri alınmadı: {exception.Message}"));
            }
        }
        // The list names no page count: a page shorter than the requested size is the last one.
        var hasMore = list.GetArrayLength() >= pageSize;
        return new(rows, null, hasMore, null, issues);
    }

    // {data:[...]} in the documentation; tolerate {data:{items|orders|orderList:[...]}} too.
    private static JsonElement? OrderArray(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data)) return null;
        if (data.ValueKind == JsonValueKind.Array) return data;
        if (data.ValueKind == JsonValueKind.Null) return JsonDocument.Parse("[]").RootElement.Clone();
        if (data.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in new[] { "items", "orders", "orderList", "data" })
            if (data.TryGetProperty(name, out var inner) && inner.ValueKind == JsonValueKind.Array) return inner;
        return null;
    }

    private static RemoteOrder Order(JsonElement order, string orderNumber, List<AdapterPageIssue> issues)
    {
        var lines = new List<RemoteOrderLine>();
        var allocations = new List<RemotePackageAllocation>();
        var itemStatuses = new List<string>();
        decimal gross = 0, discount = 0, net = 0;
        var hasItemTotals = true;
        DateTimeOffset? lastItemChange = null, dueAt = null;
        string? shipmentCode = null, carrierName = null, trackingNumber = null;

        if (order.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                var itemId = Text(item, "orderItemId");
                var product = item.TryGetProperty("product", out var productValue) && productValue.ValueKind == JsonValueKind.Object ? productValue : default;
                var quantity = Decimal(item, "quantity");
                var unitPrice = Money(item, "salePrice");
                if (itemId is null || quantity is not > 0 || unitPrice is null or < 0)
                {
                    issues.Add(new("ORDER_PACKAGE_LINE_INVALID", $"{orderNumber}:{itemId ?? "?"}", $"Pazarama siparişi {orderNumber} alındı ancak bir ürün satırında kalem kimliği, adet veya fiyat eksik."));
                    continue;
                }
                var statusCode = (int?)Decimal(item, "orderItemStatus") ?? 0;
                var raw = ItemStatus(statusCode);
                itemStatuses.Add(raw);
                var stockCode = Text(product, "stockCode");
                var barcode = Text(product, "code");
                var sku = stockCode ?? barcode ?? "";
                var title = Text(product, "name") ?? Text(product, "title") ?? sku;
                var cancelled = raw == "Cancelled" ? quantity.Value : 0;
                lines.Add(new(itemId, sku, barcode, title, quantity.Value, unitPrice.Value, Decimal(product, "vatRate") ?? 0, raw, ItemSnapshot(item, product), cancelled));
                allocations.Add(new(itemId, quantity.Value, cancelled, Shipped(raw) ? quantity.Value : 0, raw is "Delivered" ? quantity.Value : 0, raw is "Returned" ? quantity.Value : 0));
                gross += unitPrice.Value * quantity.Value;
                var itemDiscount = Money(item, "discountAmount") ?? 0;
                discount += itemDiscount;
                if (Money(item, "totalPrice") is { } total) net += total;
                else hasItemTotals = false;

                shipmentCode ??= Text(item, "shipmentCode");
                if (item.TryGetProperty("cargo", out var cargo) && cargo.ValueKind == JsonValueKind.Object)
                {
                    carrierName ??= Text(cargo, "companyName");
                    trackingNumber ??= Text(cargo, "trackingNumber");
                }
                if (Instant(item, "estimatedShippingDate") is { } estimate && (dueAt is null || estimate < dueAt)) dueAt = estimate;
                if (LatestHistory(item) is { } changed && (lastItemChange is null || changed > lastItemChange)) lastItemChange = changed;
            }
        }
        if (lines.Count == 0) throw new JsonException("order has no usable item");

        if (!hasItemTotals) net = gross - discount;
        net = Math.Clamp(net, 0, gross);
        var ordered = OrderDate(order) ?? lastItemChange ?? DateTimeOffset.UnixEpoch;
        var modified = lastItemChange ?? ordered;
        var packageStatus = PackageStatus(itemStatuses);
        // Contracted cargo: the shipment code (PZ...) is what the label and the cargo process use.
        var package = new RemotePackage($"order:{orderNumber}", null, packageStatus, modified, carrierName, shipmentCode ?? trackingNumber, allocations, gross, gross - net, net);
        var shipmentAddress = AddressSnapshot(order, "shipmentAddress");
        var billingAddress = AddressSnapshot(order, "billingAddress");
        return new(orderNumber, orderNumber, ordered, modified, "TRY", gross, gross - net, net,
            CustomerSnapshot(order, billingAddress), shipmentAddress, billingAddress,
            lines, [package], order.GetRawText(), dueAt);
    }

    // Item status codes from the order documentation, in the panel's package vocabulary.
    internal static string ItemStatus(int code) => code switch
    {
        3 => "Created",
        12 => "Picking",
        // "İptal süreci başlatıldı": Pazarama is cancelling the item, so it is held rather than prepared.
        18 => "CancelRequested",
        6 or 13 => "Cancelled",
        5 or 16 or 19 => "Shipped",
        11 or 7 or 9 => "Delivered",
        14 => "Undelivered",
        8 or 10 => "Returned",
        _ => $"Unknown{code.ToString(CultureInfo.InvariantCulture)}"
    };

    private static bool Shipped(string raw) => raw is "Shipped" or "Delivered" or "Undelivered" or "Returned";

    // The package status from the statuses of the items still in the order; all cancelled is cancelled.
    internal static string PackageStatus(IReadOnlyCollection<string> itemStatuses)
    {
        var active = itemStatuses.Where(status => status != "Cancelled").ToList();
        if (active.Count == 0) return "Cancelled";
        // An item Pazarama is cancelling holds the whole package; otherwise the package is where its least
        // advanced item is, so an item still to be prepared is not hidden behind one already shipped. Each
        // line keeps its own status and shipped quantity.
        if (active.Contains("CancelRequested")) return "CancelRequested";
        return active.OrderBy(status => ShipmentPackageStatusPolicy.Rank(TrendyolShipmentPackageStatusPolicy.FromRemote(status))).First();
    }

    private static string ItemSnapshot(JsonElement item, JsonElement product)
    {
        var snapshot = JsonNode.Parse(item.GetRawText())!.AsObject();
        var image = Text(product, "imageURL") ?? Text(product, "imageUrl");
        if (image is not null) snapshot["imageUrl"] = image;
        return snapshot.ToJsonString();
    }

    // The order id (a GUID) is what Pazarama's package, invoice and cancellation services want; it
    // rides along in the customer snapshot. Invoice recipient fields are copied from the billing address.
    internal static string CustomerSnapshot(JsonElement order, string billingAddressJson)
    {
        var snapshot = new JsonObject();
        if (Text(order, "customerName") is { } fullName)
        {
            var (first, last) = SplitName(fullName);
            snapshot["customerName"] = fullName;
            snapshot["customerFirstName"] = first;
            snapshot["customerLastName"] = last;
        }
        if (Text(order, "customerEmail") is { } email) { snapshot["customerEmail"] = email; snapshot["email"] = email; }
        if (Text(order, "orderId") is { } orderId) snapshot["pazaramaOrderId"] = orderId;
        if (Text(order, "customerId") is { } customerId) snapshot["customerId"] = customerId;
        if (Text(order, "paymentType") is { } payment) snapshot["paymentType"] = payment;
        if (Text(order, "channelCodeName") is { } channel) snapshot["channelCodeName"] = channel;
        using var billing = JsonDocument.Parse(billingAddressJson);
        foreach (var (target, source) in new[] { ("tcIdentityNumber", "identityNumber"), ("taxNumber", "taxNumber"), ("taxOffice", "taxOffice"), ("companyName", "companyName"), ("invoiceType", "invoiceType") })
            if (Text(billing.RootElement, source) is { } value) snapshot[target] = value;
        if (billing.RootElement.TryGetProperty("isEInvoiceObliged", out var obliged) && obliged.ValueKind is JsonValueKind.True or JsonValueKind.False) snapshot["isEInvoice"] = obliged.GetBoolean();
        if (Decimal(order, "shipmentAmount") is { } shipping && shipping > 0) snapshot["shipmentAmount"] = shipping;
        return snapshot.ToJsonString();
    }

    // Keeps Pazarama's fields and adds the names the panel reads for every platform.
    internal static string AddressSnapshot(JsonElement order, string field)
    {
        if (!order.TryGetProperty(field, out var address) || address.ValueKind != JsonValueKind.Object) return "{}";
        var snapshot = JsonNode.Parse(address.GetRawText())!.AsObject();
        var line = Text(address, "addressDetail") ?? Text(address, "displayAddressText");
        if (line is not null)
        {
            snapshot["fullAddress"] = Text(address, "displayAddressText") ?? line;
            snapshot["address1"] = line;
        }
        if (Text(address, "nameSurname") is { } name)
        {
            var (first, last) = SplitName(name);
            snapshot["firstName"] = first;
            snapshot["lastName"] = last;
            snapshot["fullName"] = name;
        }
        if (Text(address, "cityName") is { } city) snapshot["city"] = city;
        if (Text(address, "districtName") is { } district) snapshot["district"] = district;
        if (Text(address, "neighborhoodName") is { } neighborhood) snapshot["neighborhood"] = neighborhood;
        if (Text(address, "phoneNumber") is { } phone) snapshot["phone"] = phone;
        return snapshot.ToJsonString();
    }

    private static (string First, string Last) SplitName(string fullName)
    {
        var name = fullName.Trim();
        var separator = name.LastIndexOf(' ');
        return separator <= 0 ? (name, "") : (name[..separator].Trim(), name[(separator + 1)..].Trim());
    }

    // "2026-02-17 17:23" in Turkey time.
    private static DateTimeOffset? OrderDate(JsonElement order)
    {
        var text = Text(order, "orderDate");
        if (text is null) return null;
        string[] formats = ["yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm:ss.FFFFFFF", "yyyy-MM-dd"];
        return DateTime.TryParseExact(text, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)
            ? new DateTimeOffset(local, PazaramaOrderHistoryPolicy.TurkeyOffset).ToUniversalTime()
            : DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed.ToUniversalTime() : null;
    }

    private static DateTimeOffset? LatestHistory(JsonElement item)
    {
        if (!item.TryGetProperty("orderItemStatusHistory", out var history) || history.ValueKind != JsonValueKind.Array) return null;
        DateTimeOffset? latest = null;
        foreach (var entry in history.EnumerateArray())
            if (Instant(entry, "historyCreatedDate") is { } at && (latest is null || at > latest)) latest = at;
        return latest;
    }

    private static DateTimeOffset? Instant(JsonElement element, string property)
    {
        var text = Text(element, property);
        if (text is null) return null;
        // PostgreSQL stores only UTC instants; Pazarama writes +03:00 offsets.
        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)) return parsed.ToUniversalTime();
        return null;
    }

    // Money is {currency, value, valueInt, valueString}; a bare number is accepted too.
    private static decimal? Money(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Object) return Decimal(value, "value");
        return value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number) ? number : null;
    }

    private static decimal? Decimal(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number)) return number;
        return value.ValueKind == JsonValueKind.String && decimal.TryParse(value.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
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
        return string.IsNullOrWhiteSpace(text) || text.Trim() == "null" ? null : text.Trim();
    }
}
