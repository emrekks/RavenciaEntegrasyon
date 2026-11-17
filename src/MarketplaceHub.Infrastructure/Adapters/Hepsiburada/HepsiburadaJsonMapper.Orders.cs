using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;

namespace MarketplaceHub.Infrastructure.Adapters.Hepsiburada;

internal static partial class HepsiburadaJsonMapper
{
    public static (IReadOnlyList<JsonElement> Items, int? TotalCount) OrderPage(JsonElement root) => ListingPage(root);

    // The open-package list has no order number of its own; each of its items names its order.
    public static string? PackageOrderNumber(JsonElement item)
    {
        var orderNumber = Text(item, "orderNumber", "OrderNumber", "orderNo", "OrderNo");
        if (!string.IsNullOrWhiteSpace(orderNumber)) return orderNumber;
        var items = Find(item, "items", "Items", "lineItems", "LineItems", "orderItems", "OrderItems");
        if (items.ValueKind != JsonValueKind.Array) return null;
        return items.EnumerateArray()
            .Select(line => Text(line, "orderNumber", "OrderNumber", "orderNo", "OrderNo"))
            .FirstOrDefault(number => !string.IsNullOrWhiteSpace(number));
    }

    public static RemoteOrderPackage OrderPackage(JsonElement item)
    {
        var orderNumber = PackageOrderNumber(item);
        var packageNumber = Text(item, "packageNumber", "PackageNumber");
        if (string.IsNullOrWhiteSpace(orderNumber) || string.IsNullOrWhiteSpace(packageNumber))
            throw new JsonException("Hepsiburada paket yanıtında orderNumber veya packageNumber yok; güvenli sipariş eşlemesi yapılamadı.");

        var occurredAt = Date(item, "lastStatusUpdateDate", "LastStatusUpdateDate", "shippedDate", "ShippedDate", "unpackedDate", "UnpackedDate", "packageDate", "PackageDate", "updatedAt", "UpdatedAt", "createdAt", "CreatedAt", "orderDate", "OrderDate");
        if (occurredAt is null) throw new JsonException("Hepsiburada paket yanıtında kullanılabilir tarih alanı yok.");

        var allocations = new List<RemotePackageAllocation>();
        // A line item that cannot be read (no id, SKU, quantity or price) is neither allocated nor kept as a line: the
        // package counts it (UnreadableOrderLines), so it is not silently incomplete.
        var unreadable = 0;
        var items = Find(item, "lineItems", "LineItems", "items", "Items", "orderItems", "OrderItems");
        if (items.ValueKind == JsonValueKind.Array)
        {
            foreach (var line in items.EnumerateArray())
            {
                if (!TryMapLine(line, out _, out _)) { unreadable++; continue; }
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
            Invoice: InvoiceObservation(item),
            UnreadableLineCount: unreadable,
            // The open package's cargo barcode ("Siparişin kargo barkod numarasıdır", hepsiburada-api-son.md:9282).
            CargoBarcode: Text(item, "barcode", "Barcode"));
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
        // The list's own event date comes first: a delivered row also carries its shipped date, which must not
        // date the delivery (and let an older "Shipped" observation look newer than "Delivered").
        var occurredAt = rawStatus switch
        {
            "Delivered" => Date(item, "DeliveredDate", "lastStatusUpdateDate", "UndeliveredDate", "ShippedDate"),
            "Undelivered" => Date(item, "UndeliveredDate", "lastStatusUpdateDate", "DeliveredDate", "ShippedDate"),
            _ => Date(item, "ShippedDate", "lastStatusUpdateDate", "DeliveredDate", "UndeliveredDate")
        };
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
            IsStatusObservation: true,
            CargoBarcode: Text(item, "barcode", "Barcode"));
        return new(orderNumber, package);
    }

    public static string? OrderNumber(JsonElement item) => Text(item, "orderNumber", "OrderNumber", "orderNo", "OrderNo");

    public static RemoteOrder PaidOrderLine(JsonElement item) =>
        PaidOrderLine(item, out var refusal) ?? throw new JsonException(refusal!.Message);

    // One item of the paid-order list, or why it cannot be taken; never a throw for one bad item, so one item does not
    // stop the page (5 Nov 2026). A line the panel cannot read (TryMapLine) is the common rule's unreadable line
    // (UnreadableOrderLines): the flat list has no package to count it on, so the item is refused naming its order and
    // the caller reads that order by number, whose packages count the line. An item without its own order date or with
    // a currency the panel does not know is refused the same way (RefusedOrders); nothing is guessed.
    public static RemoteOrder? PaidOrderLine(JsonElement item, out AdapterPageIssue? refusal)
    {
        refusal = null;
        var orderNumber = OrderNumber(item);
        if (string.IsNullOrWhiteSpace(orderNumber))
        {
            refusal = RefusedOrders.Issue(Text(item, "id", "Id", "lineItemId", "LineItemId") ?? "unknown", "Hepsiburada ödemesi tamamlanmış sipariş satırı içeri alınmadı: sipariş numarası (orderNumber) yok.", null);
            return null;
        }
        var nestedLines = Find(item, "lineItems", "LineItems", "orderItems", "OrderItems");
        if (nestedLines.ValueKind == JsonValueKind.Array || Find(item, "items", "Items").ValueKind == JsonValueKind.Array)
        {
            try { return Order(item, orderNumber); }
            catch (JsonException exception)
            {
                refusal = RefusedOrders.Issue(orderNumber, $"Hepsiburada siparişi {orderNumber} içeri alınmadı: {exception.Message}", orderNumber);
                return null;
            }
        }

        if (!TryMapLine(item, out var mapped, out var reason, missingStatus: null))
        {
            refusal = UnreadableOrderLines.Issue("Hepsiburada", orderNumber, Text(item, "id", "Id", "lineItemId", "LineItemId", "orderLineId", "OrderLineId"), reason) with { RereadOrderId = orderNumber };
            return null;
        }
        var line = mapped!;
        var orderedAt = Date(item, "orderDate", "OrderDate", "orderedAt", "OrderedAt", "createdAt", "CreatedAt");
        if (orderedAt is null)
        {
            refusal = RefusedOrders.Issue(orderNumber, $"Hepsiburada siparişi {orderNumber} içeri alınmadı: sipariş tarihi (orderDate) yok; sipariş yeniden okunacak.", orderNumber);
            return null;
        }
        var rawCurrency = Currency(item, "unitPrice", "UnitPrice", "totalPrice", "TotalPrice");
        if (OrderCurrency(rawCurrency) is not { } currency)
        {
            refusal = RefusedOrders.Issue(orderNumber, $"Hepsiburada siparişi {orderNumber} içeri alınmadı: para birimi tanınmadı ({rawCurrency}); sipariş yeniden okunacak.", orderNumber);
            return null;
        }
        var gross = Money(item, "totalPrice", "TotalPrice", "lineTotal", "LineTotal") ?? line.UnitPrice * line.Quantity;
        var discount = Money(item, "totalMerchantDiscount", "TotalMerchantDiscount", "merchantDiscount", "MerchantDiscount", "discountAmount", "DiscountAmount") ?? 0m;
        var invoice = Find(item, "invoice", "Invoice");
        var invoiceAddress = Find(invoice, "address", "Address");
        var invoiceEnvelope = invoice;
        if (invoiceAddress.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            invoiceAddress = Find(item, "invoiceAddress", "InvoiceAddress", "billingAddress", "BillingAddress");
            invoiceEnvelope = default;
            if (invoiceAddress.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
                invoiceAddress = invoice;
        }
        var customer = Find(item, "customer", "Customer");
        var customerSnapshot = customer.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            ? JsonSerializer.Serialize(new { name = Text(item, "customerName", "CustomerName", "recipientName", "RecipientName"), email = Text(item, "email", "Email"), phoneNumber = Text(item, "phoneNumber", "PhoneNumber") })
            : Snapshot(customer);
        customerSnapshot = EnrichOrderSnapshot(item, customerSnapshot);
        // D3: the paid list carries Open and Unpacked orders (hepsiburada-api-son.md:8618): an item without a status is
        // unknown, not "Open". A new order read so is reviewed and a known one keeps its status
        // (HepsiburadaOrderLifecycleStatusPolicy.UnknownUnpackagedStatus).
        var rawStatus = Text(item, "status", "Status", "orderStatus", "OrderStatus");
        var status = rawStatus;
        return new(
            orderNumber,
            orderNumber,
            orderedAt.Value,
            Date(item, "lastStatusUpdateDate", "LastStatusUpdateDate", "lastModifiedAt", "LastModifiedAt") ?? orderedAt.Value,
            currency,
            Math.Max(gross, gross - discount),
            discount,
            Math.Max(0m, gross - discount),
            customerSnapshot,
            Snapshot(Find(item, "shippingAddress", "ShippingAddress", "deliveryAddress", "DeliveryAddress")),
            Snapshot(MergeInvoiceAddress(invoiceEnvelope, invoiceAddress)),
            [line],
            [],
            item.GetRawText(),
            Date(item, "dueDate", "DueDate", "shipmentDueAt", "ShipmentDueAt"),
            // ADAPTOR-TY-HB-6: a field Hepsiburada did not send is unknown, not "paid" or "not cancelled".
            Text(item, "paymentStatus", "PaymentStatus") ?? "UNKNOWN",
            CancellationStatusOf(rawStatus),
            LifecycleStatus: status);
    }

    private static RemoteOrder PackageOrderSnapshot(JsonElement item, string orderNumber, RemotePackage package)
    {
        // No silent fallbacks (5 Nov 2026): a package row without the order's own date is not dated by the package's event
        // time. The row is refused (OrderPackage's caller then reads the order by its number, whose detail has the date).
        var orderedAt = Date(item, "orderDate", "OrderDate", "orderedAt", "OrderedAt", "createdAt", "CreatedAt")
            ?? throw new JsonException($"Hepsiburada paket satırında sipariş {orderNumber} için sipariş tarihi yok; paketin olay zamanı sipariş tarihi sayılmaz.");
        var itemArray = Find(item, "lineItems", "LineItems", "items", "Items", "orderItems", "OrderItems");
        IReadOnlyList<RemoteOrderLine> lines = itemArray.ValueKind == JsonValueKind.Array
            ? ReadableLines(itemArray.EnumerateArray(), "Open").Lines.GroupBy(line => line.ExternalLineId, StringComparer.Ordinal).Select(group => group.First()).ToArray()
            : [];
        if (lines.Count == 0 && !string.IsNullOrWhiteSpace(Text(item, "lineItemId", "LineItemId", "orderLineId", "OrderLineId")))
            lines = [MapLine(item)];
        var shipmentAddress = Find(item, "shippingAddress", "ShippingAddress", "deliveryAddress", "DeliveryAddress");
        if (shipmentAddress.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            shipmentAddress = AddressSnapshot(item, "shippingAddressDetail", "recipientName", "shippingCountryCode", "shippingDistrict", "shippingTown", "shippingCity", "shippingPostalCode", "email", "phoneNumber");
        var invoiceEnvelope = Find(item, "invoice", "Invoice");
        var invoiceAddress = Find(invoiceEnvelope, "address", "Address");
        var invoice = invoiceAddress.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            ? Find(item, "invoiceAddress", "InvoiceAddress", "billingAddress", "BillingAddress")
            : MergeInvoiceAddress(invoiceEnvelope, invoiceAddress);
        if (invoice.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            invoice = invoiceEnvelope;
        if (invoice.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            invoice = AddressSnapshot(item, "billingAddress", "companyName", "taxOffice", "taxNumber", "identityNo", "billingDistrict", "billingTown", "billingCity", "billingPostalCode");
        var customerSnapshot = OrderCustomerSnapshot(item, Find(item, "customer", "Customer"));
        var status = Text(item, "status", "Status", "packageStatus", "PackageStatus") ?? package.RawStatus;
        var rawCurrency = Currency(item, "totalPrice", "TotalPrice")
            ?? (itemArray.ValueKind == JsonValueKind.Array ? itemArray.EnumerateArray().Select(line => Currency(line, "price", "Price", "totalPrice", "TotalPrice")).FirstOrDefault(value => value is not null) : null);
        var currency = OrderCurrency(rawCurrency) ?? throw new JsonException($"Hepsiburada paket satırında sipariş {orderNumber} için para birimi tanınmadı ({rawCurrency}).");
        var gross = package.GrossAmount > 0 ? package.GrossAmount : lines.Sum(line => line.UnitPrice * line.Quantity);
        return new(
            orderNumber,
            orderNumber,
            orderedAt,
            package.OccurredAt,
            currency,
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
            Text(item, "paymentStatus", "PaymentStatus") ?? "UNKNOWN",
            CancellationStatusOf(status));
    }

    // CANCELLED or NOT_CANCELLED from a status Hepsiburada sent; UNKNOWN when it sent none (ADAPTOR-TY-HB-6). D4: a status
    // this panel does not know (HepsiburadaOrderLifecycleStatusPolicy) says nothing about a cancellation either, unless it
    // names one ("Cancelled…"); ClaimCreated is a line under a return claim, not a cancellation.
    internal static string CancellationStatusOf(string? status)
    {
        if (string.IsNullOrWhiteSpace(status)) return "UNKNOWN";
        if (status.Contains("cancel", StringComparison.OrdinalIgnoreCase)) return "CANCELLED";
        return HepsiburadaOrderLifecycleStatusPolicy.FromRemote(status) is not null
            || string.Equals(status.Trim(), "ClaimCreated", StringComparison.OrdinalIgnoreCase)
            ? "NOT_CANCELLED" : "UNKNOWN";
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

    private static JsonElement MergeInvoiceAddress(JsonElement invoiceEnvelope, JsonElement invoiceAddress)
    {
        if (invoiceAddress.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return invoiceEnvelope;
        if (invoiceEnvelope.ValueKind != JsonValueKind.Object)
            return invoiceAddress;

        var properties = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        if (invoiceAddress.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in invoiceAddress.EnumerateObject())
                properties[property.Name] = property.Value.Clone();
        }
        else if (invoiceAddress.ValueKind == JsonValueKind.String)
        {
            properties["address"] = invoiceAddress.Clone();
        }
        else
        {
            return invoiceEnvelope;
        }

        foreach (var property in invoiceEnvelope.EnumerateObject())
        {
            if (property.Name.Equals("invoiceAddress", StringComparison.OrdinalIgnoreCase)
                || (invoiceAddress.ValueKind == JsonValueKind.Object && property.Name.Equals("address", StringComparison.OrdinalIgnoreCase)))
                continue;
            if (!properties.ContainsKey(property.Name)) properties[property.Name] = property.Value.Clone();
        }

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(properties));
        return document.RootElement.Clone();
    }

    // The order's currency (5 Nov 2026): a value the panel does not know is not stored as lira; the caller refuses the
    // order instead (RefusedOrders). A read without any currency field is lira: Hepsiburada sells in TRY only and its money
    // objects say "TRY" (hepsiburada-api-son.md, e.g. :8371, :12479).
    private static string? OrderCurrency(string? raw) => string.IsNullOrWhiteSpace(raw) ? "TRY" : RefusedOrders.Currency(raw);

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

        var allLineItems = linesElement.EnumerateArray().ToArray();
        // A line item that cannot be read is left out, never filled in; the package holding it counts it (Packages).
        var (lineItems, lines, _) = ReadableLines(allLineItems, missingStatus: null);
        var gross = Money(order, "totalPrice", "TotalPrice", "totalAmount", "TotalAmount", "grossAmount", "GrossAmount")
            ?? lines.Sum(line => line.UnitPrice * line.Quantity);
        var discount = Money(order, "discountAmount", "DiscountAmount", "totalDiscount", "TotalDiscount") ?? 0m;
        var net = Money(order, "netAmount", "NetAmount", "payableAmount", "PayableAmount") ?? Math.Max(0m, gross - discount);
        var rawCurrency = Text(order, "currency", "Currency", "currencyCode", "CurrencyCode")
            ?? Text(Find(order, "totalPrice", "TotalPrice"), "currency", "Currency");
        var currency = OrderCurrency(rawCurrency) ?? throw new JsonException($"Hepsiburada sipariş {orderNumber} para birimi tanınmadı ({rawCurrency}).");
        var orderStatus = Text(order, "status", "Status", "orderStatus", "OrderStatus");
        var status = string.Equals(orderStatus?.Trim(), "ClaimCreated", StringComparison.OrdinalIgnoreCase)
            ? ConsistentLineItemStatus(lines)
            : orderStatus ?? ConsistentLineItemStatus(lines);
        var paymentStatus = Text(order, "paymentStatus", "PaymentStatus") ?? "UNKNOWN";
        var customer = Find(order, "customer", "Customer");
        var shipmentAddress = Find(order, "deliveryAddress", "DeliveryAddress", "shipmentAddress", "ShipmentAddress");
        var invoiceEnvelope = Find(order, "invoice", "Invoice");
        var invoiceAddress = Find(order, "invoiceAddress", "InvoiceAddress", "billingAddress", "BillingAddress");
        if (invoiceAddress.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            invoiceAddress = Find(invoiceEnvelope, "address", "Address");
            invoiceAddress = invoiceAddress.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
                ? invoiceEnvelope
                : MergeInvoiceAddress(invoiceEnvelope, invoiceAddress);
        }
        var modified = Date(order, "lastStatusUpdateDate", "LastStatusUpdateDate", "lastModifiedAt", "LastModifiedAt") ?? orderedAt.Value;
        // Without an order-level flag the order counts as invoiced only when every line agrees; a
        // multi-package order whose packages differ has no single answer.
        var lineInvoiceFlags = allLineItems.Select(item => Boolean(item, "hasInvoice", "HasInvoice")).Distinct().ToArray();
        var invoiceUploaded = Boolean(order, "hasInvoice", "HasInvoice")
            ?? (lineInvoiceFlags.Length == 1 ? lineInvoiceFlags[0] : null);
        var packages = Packages(order, LineAliases(lineItems, lines), orderedAt.Value, invoiceUploaded);
        var dueAt = Date(order, "dueDate", "DueDate", "shipmentDueAt", "ShipmentDueAt")
            ?? lineItems.Select(item => Date(item, "dueDate", "DueDate", "shipmentDueAt", "ShipmentDueAt")).FirstOrDefault(value => value.HasValue);
        // The common sales model uses the order number for GetAsync lookups. Hepsiburada's
        // detail endpoint is keyed by orderNumber, so keep that value as the stable key.
        return new(
            orderNumber,
            orderNumber,
            orderedAt.Value,
            modified,
            currency,
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
            CancellationStatusOf(status),
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

    private static RemoteOrderLine MapLine(JsonElement line, string? missingStatus = "Open") =>
        TryMapLine(line, out var mapped, out var reason, missingStatus) ? mapped! : throw new JsonException($"Hepsiburada sipariş satırı okunamadı: {reason}");

    // The line, or why it cannot be read: no line id, no SKU, no positive quantity or no price (neither a unit price nor
    // a line total). Such a line is never filled in with 0 (UnreadableOrderLines): the order is kept without it.
    private static bool TryMapLine(JsonElement line, out RemoteOrderLine? mapped, out string reason, string? missingStatus = "Open")
    {
        mapped = null;
        reason = "";
        if (line.ValueKind != JsonValueKind.Object) { reason = "satır nesne değil."; return false; }
        var lineId = Text(line, "id", "Id", "lineItemId", "LineItemId", "orderLineId", "OrderLineId");
        var sku = Text(line, "merchantSku", "MerchantSku", "sellerSku", "SellerSku")
            ?? Text(line, "sku", "Sku", "hbSku", "HBSku");
        if (string.IsNullOrWhiteSpace(lineId) || string.IsNullOrWhiteSpace(sku)) { reason = "kimlik veya SKU alanı eksik."; return false; }
        if (Decimal(line, "quantity", "Quantity") is not { } quantity || quantity <= 0) { reason = "adet yok veya geçersiz."; return false; }
        var price = Money(line, "price", "Price", "unitPrice", "UnitPrice");
        var total = Money(line, "totalPrice", "TotalPrice", "lineTotal", "LineTotal");
        if (price is null && total is null) { reason = "fiyat yok."; return false; }
        var unitPrice = price ?? 0m;
        if (unitPrice == 0m && total is not null) unitPrice = total.Value / quantity;
        var status = Text(line, "status", "Status", "lineStatus", "LineStatus") ?? missingStatus ?? string.Empty;
        var cancelled = status.Contains("cancel", StringComparison.OrdinalIgnoreCase) ? quantity : 0m;
        var snapshot = EnrichSnapshot(line.GetRawText(), ("imageUrl", ImageUrl(line)));
        // A line's "barcode" is its package's cargo barcode (every line of the package carries the same one); the
        // product's own barcode is "productBarcode".
        var productBarcode = Text(line, "productBarcode", "ProductBarcode");
        var barcode = string.IsNullOrWhiteSpace(productBarcode) ? Text(line, "barcode", "Barcode") : productBarcode;
        mapped = new(lineId, sku, barcode, Text(line, "name", "Name", "productName", "ProductName") ?? sku, quantity, unitPrice, Decimal(line, "vatRate", "VatRate"), status, snapshot, cancelled,
            PlatformDiscount: PlatformDiscount(line));
        return true;
    }

    // Hepsiburada's own campaign discount a unit ("hbDiscount": {"unitPrice": {"amount": 194.85}}), which Hepsiburada pays
    // the seller: live, every CampaignDiscount record of Sep-Nov 2026 credited the line's hbDiscount, grossed up from the
    // product's VAT to 20 % (4091991027: 194.85 at 10 % VAT, 212.57 credited). Part of the seller's revenue, as Trendyol's
    // own discount is (DashboardRevenuePolicy); none when the line carries none.
    internal static decimal? PlatformDiscount(JsonElement line) =>
        TryFind(line, out var discount, "hbDiscount", "HbDiscount") && Money(discount, "unitPrice", "UnitPrice") is { } amount && amount > 0m ? amount : null;

    // The order detail's readable line items with their lines, in order (LineAliases pairs them by position), and how
    // many could not be read.
    private static (IReadOnlyList<JsonElement> Items, List<RemoteOrderLine> Lines, int Unreadable) ReadableLines(IEnumerable<JsonElement> lineItems, string? missingStatus)
    {
        var items = new List<JsonElement>();
        var lines = new List<RemoteOrderLine>();
        var unreadable = 0;
        foreach (var item in lineItems)
        {
            if (TryMapLine(item, out var line, out _, missingStatus)) { items.Add(item); lines.Add(line!); }
            else unreadable++;
        }
        return (items, lines, unreadable);
    }

    // Every identity a line is known by (id, lineItemId, orderLineId) leads to its ExternalLineId, so a package
    // item that names the line by another of its keys is still allocated instead of being dropped.
    private static IReadOnlyDictionary<string, string> LineAliases(IReadOnlyList<JsonElement> lineItems, IReadOnlyList<RemoteOrderLine> lines)
    {
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in lines) aliases[line.ExternalLineId] = line.ExternalLineId;
        for (var index = 0; index < lineItems.Count && index < lines.Count; index++)
            foreach (var name in LineIdentityNames)
                if (Text(lineItems[index], name) is { Length: > 0 } alias) aliases.TryAdd(alias, lines[index].ExternalLineId);
        return aliases;
    }

    private static readonly string[] LineIdentityNames = ["id", "lineItemId", "orderLineId", "orderItemId"];

    private static IReadOnlyList<RemotePackage> Packages(JsonElement order, IReadOnlyDictionary<string, string> lineAliases, DateTimeOffset orderDate, bool? orderInvoiceUploaded)
    {
        var element = Find(order, "packages", "Packages", "shipments", "Shipments");
        if (element.ValueKind != JsonValueKind.Array) return [];
        var packages = new List<RemotePackage>();
        var itemGroups = ItemPackageGroups(order);
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
            // An item of the package that names no readable line of the order, or no quantity, is a line the panel
            // could not read: the package counts it (UnreadableOrderLines) instead of dropping it silently.
            var unreadable = 0;
            foreach (var item in packageItems.EnumerateArray())
            {
                var lineId = new[] { "orderLineId", "lineItemId", "orderItemId", "id" }
                    .Select(name => Text(item, name))
                    .Select(alias => alias is not null && lineAliases.TryGetValue(alias, out var known) ? known : null)
                    .FirstOrDefault(known => known is not null);
                var quantity = Decimal(item, "quantity", "Quantity", "allocatedQuantity", "AllocatedQuantity");
                if (lineId is null || quantity is null || quantity <= 0) { unreadable++; continue; }
                allocations.Add(new(lineId, quantity.Value, 0, 0, 0, 0));
            }
            if (allocations.Count == 0 && unreadable == 0) continue;
            var occurred = Date(package, "shippedDate", "ShippedDate", "createdAt", "CreatedAt") ?? orderDate;
            var cargoBarcode = Text(package, "barcode", "Barcode")
                ?? (itemGroups.TryGetValue(packageId.Trim(), out var packageLines) ? LineCargoBarcode(packageLines) : null);
            packages.Add(new RemotePackage(packageId, null, status, occurred, Text(package, "cargoCompany", "CargoCompany"), tracking, allocations, Invoice: invoice, UnreadableLineCount: unreadable, CargoBarcode: cargoBarcode));
        }
        return packages;
    }

    // The cargo barcode the order detail's lines of one package carry: a line's "barcode" is its package's cargo barcode
    // and its own product barcode is "productBarcode" (live 4893555494: 62755438736587 and 0000RYP003T13). Taken only
    // when every line names a product barcode that differs from it and all lines agree; otherwise unknown.
    internal static string? LineCargoBarcode(IReadOnlyList<JsonElement> lines)
    {
        string? found = null;
        foreach (var line in lines)
        {
            var barcode = Text(line, "barcode", "Barcode")?.Trim();
            var productBarcode = Text(line, "productBarcode", "ProductBarcode")?.Trim();
            if (string.IsNullOrEmpty(barcode) || string.IsNullOrEmpty(productBarcode) || string.Equals(barcode, productBarcode, StringComparison.Ordinal)) return null;
            if (found is not null && !string.Equals(found, barcode, StringComparison.Ordinal)) return null;
            found = barcode;
        }
        return found;
    }
}
