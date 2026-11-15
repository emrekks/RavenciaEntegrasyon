using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;

namespace MarketplaceHub.Infrastructure.Adapters.Hepsiburada;

internal static partial class HepsiburadaJsonMapper
{
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
            trackingLink,
            Date(item, "orderDate", "OrderDate", "orderedAt", "OrderedAt"),
            // The decision's own date (acceptedDate/rejectedDate), only for the status it dates.
            DecidedAt: status.ToUpperInvariant() is "ACCEPTED" or "REJECTED" ? statusDate : null,
            CustomerRequest: ReturnCustomerRequestPolicy.FromHepsiburada(claimType, HasRequestedProduct(item)),
            // How an approved claim was closed (finalizedWith: Refund or Change); null when the claim does not say.
            ResolvedWith: ReturnCustomerRequestPolicy.ResolvedWithFromHepsiburada(Text(item, "finalizedWith", "FinalizedWith")),
            // claimDate itself (Istanbul local, read by Date); the createdAt the identity check above also accepts is not it.
            RequestedAt: Date(item, "claimDate", "ClaimDate"));
    }

    // Hepsiburada names the product a customer wants instead (requestedProduct) on an exchange claim; null otherwise.
    private static bool HasRequestedProduct(JsonElement item) =>
        TryFind(item, out var requested, "requestedProduct", "RequestedProduct") && requested.ValueKind == JsonValueKind.Object;

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

    // A local read model of an order the panel does not hold yet, built from one of its claims (the claim scan). Nothing
    // is guessed (5 Nov 2026 rule, ADAPTOR-TY-HB-1): a claim without its order's own date, with a line whose price it does
    // not carry, or with a currency the panel does not know builds no order (null; the claim waits with
    // RETURN_ORDER_NOT_FOUND until the order itself is read). Its change time is the oldest possible (UnixEpoch), as on
    // Trendyol: the claim's time is not the order's, so any later order read is newer and replaces amounts, date and lines.
    public static RemoteOrder? OrderFromReturnClaim(string json) => OrderFromReturnClaim(json, claimOnly: true);

    // claimOnly false: a claim inside a replacement-package notification (ClaimPackageOrder), which sets its own change
    // time and keeps the earlier lenient reading.
    private static RemoteOrder? OrderFromReturnClaim(string json, bool claimOnly)
    {
        using var document = JsonDocument.Parse(json);
        var root = Unwrap(document.RootElement);
        var orderNumber = Text(root, "orderNumber", "OrderNumber", "orderNo", "OrderNo");
        if (string.IsNullOrWhiteSpace(orderNumber)) return null;

        var claimDate = Date(root, "claimDate", "ClaimDate");
        // D13: a claim with neither an order date nor a claim date is not projected as an order placed in 1970. A claim
        // read alone is not dated by its claim date either: that is not when the order was placed.
        if ((claimOnly ? Date(root, "orderDate", "OrderDate") : Date(root, "orderDate", "OrderDate") ?? claimDate) is not { } orderedAt) return null;
        var modifiedAt = claimOnly
            ? DateTimeOffset.UnixEpoch
            : Date(root, "lastModifiedAt", "LastModifiedAt", "updatedAt", "UpdatedAt") ?? claimDate ?? orderedAt;
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
            if (claimOnly && unitPrice is null) return null;

            remoteLines.Add(new(
                lineId,
                sku,
                Text(line, "barcode", "Barcode", "gtin", "GTIN") ?? Text(root, "barcode", "Barcode", "gtin", "GTIN"),
                First(Text(line, "productName", "ProductName", "name", "Name"), Text(entry, "productName", "ProductName", "name", "Name"), Text(root, "productName", "ProductName", "name", "Name")) ?? sku,
                quantity.Value,
                unitPrice ?? 0m,
                // Live claims carry no VAT rate; a missing one is unknown, not 0%, and keeps the order line's rate (T-2).
                Decimal(line, "vatRate", "VatRate") ?? Decimal(root, "vatRate", "VatRate"),
                "ClaimCreated",
                line.GetRawText()));
        }
        if (remoteLines.Count == 0) return null;

        var gross = remoteLines.Sum(line => line.UnitPrice * line.Quantity);
        var rawCurrency = Text(root, "currency", "Currency", "currencyCode", "CurrencyCode")
            ?? Text(Find(root, "price", "Price", "totalPrice", "TotalPrice"), "currency", "Currency", "currencyCode", "CurrencyCode");
        // Same rule as the order read (OrderCurrency): no currency field is lira, an unknown one is not taken.
        string currency;
        if (!claimOnly) currency = rawCurrency is { Length: 3 } ? rawCurrency.ToUpperInvariant() : "TRY";
        else if (OrderCurrency(rawCurrency) is { } known) currency = known;
        else return null;
        var customer = OrderCustomerSnapshot(root, Find(root, "customer", "Customer"));

        // A claim can arrive after the platform's order-history window. Keep
        // the reconstruction limited to claim lines and do not invent a
        // shipment package; a later authoritative order read replaces it.
        return new(
            orderNumber,
            orderNumber,
            orderedAt,
            modifiedAt,
            currency,
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
                var order = OrderFromReturnClaim(item.GetRawText(), claimOnly: false);
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
            ?? parsedClaims.Max(item => item.Claim.LastModifiedAt)
            ?? throw new JsonException("Hepsiburada talep paketi hiçbir tarih içermiyor.");
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
}
