using System.Text.Json;
using MarketplaceHub.Application;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Adapters.Shopify;

public sealed partial class ShopifyHttpClient
{
    // The E-Arşiv internet sale facts (report item 18), added only when Shopify sent them: the payment gateways of the
    // order (paymentGatewayNames) and the shop's primary domain (shop.primaryDomain.url of the same query).
    internal static string WithInternetSaleFacts(string customerJson, JsonElement order, string? shopUrl)
    {
        var gateways = order.TryGetProperty("paymentGatewayNames", out var names) && names.ValueKind == JsonValueKind.Array
            ? names.EnumerateArray().Where(name => name.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(name.GetString())).Select(name => name.GetString()!.Trim()).ToArray()
            : [];
        if (gateways.Length == 0 && string.IsNullOrWhiteSpace(shopUrl)) return customerJson;
        var snapshot = System.Text.Json.Nodes.JsonNode.Parse(customerJson)!.AsObject();
        if (gateways.Length > 0) snapshot["paymentGatewayNames"] = new System.Text.Json.Nodes.JsonArray([.. gateways.Select(name => (System.Text.Json.Nodes.JsonNode)System.Text.Json.Nodes.JsonValue.Create(name)!)]);
        if (!string.IsNullOrWhiteSpace(shopUrl)) snapshot["shopPrimaryDomainUrl"] = shopUrl.Trim();
        return snapshot.ToJsonString();
    }

    internal static RemoteOrder MapOrder(JsonElement order, string? shopUrl = null)
    {
        var money = order.GetProperty("currentTotalPriceSet").GetProperty("shopMoney");
        var currency = money.GetProperty("currencyCode").GetString() ?? "TRY";
        var financialStatus = order.GetProperty("displayFinancialStatus").GetString() ?? "OPEN";
        // The order's cancelledAt is the only cancellation time this read carries: a cancelled order dates every
        // cancelled line with it. A line cut while the order stays open (an order edit) has no time of its own here,
        // so it stays unknown rather than taking updatedAt.
        var orderCancelledAt = order.TryGetProperty("cancelledAt", out var cancelledAtElement)
            && cancelledAtElement.ValueKind == JsonValueKind.String
            && cancelledAtElement.TryGetDateTimeOffset(out var cancelledAtValue)
                ? cancelledAtValue
                : (DateTimeOffset?)null;
        // Since the 2026-07 Admin API, Order.fulfillments is returned as a
        // direct list. Its nested fulfillmentLineItems field remains a
        // connection and is mapped from nodes below.
        var fulfillments = order.GetProperty("fulfillments").EnumerateArray().ToList();
        var fulfilledByLine = FulfilledQuantities(fulfillments);
        // A line item without an id, a quantity or a unit price cannot be read: it is left out, never filled in with 0,
        // and the package that holds it (its fulfillment, else the unfulfilled remainder) counts it (UnreadableOrderLines).
        var allLineNodes = order.GetProperty("lineItems").GetProperty("nodes").EnumerateArray().ToList();
        var lineNodes = allLineNodes.Where(IsReadableOrderLine).ToList();
        var unreadableLineIds = allLineNodes.Where(node => !IsReadableOrderLine(node)).Select(OrderLineIdOrNull).ToList();
        // Order.taxesIncluded: true when the line prices and the order totals both carry the tax. When false, the line
        // prices leave the tax out while currentTotalPriceSet still holds it; every line price then has its own
        // reported tax added (MapOrderLine), so a line carries what the customer paid per unit, as on every other
        // marketplace, and the invoice (which reads unit prices as VAT inclusive) agrees with the order total. A read
        // without the field keeps the prices as they came.
        var taxesIncluded = !(order.TryGetProperty("taxesIncluded", out var taxesIncludedElement) && taxesIncludedElement.ValueKind == JsonValueKind.False);
        var lines = lineNodes
            .Select(line => MapOrderLine(line, financialStatus, fulfilledByLine, taxesIncluded))
            .Select(line => line.CancelledQuantity > 0 && orderCancelledAt is not null ? line with { CancelledAt = orderCancelledAt } : line)
            .ToList();
        // currentQuantity also leaves out refunded units. A removed unit that had been fulfilled is a return (or a
        // refund after shipping), not a cancellation: it stays in the sale and is written as returned on the
        // delivered fulfillments of its line.
        var returnedLines = lines.Zip(lineNodes, (line, node) => (Line: line, Quantity: RemovedQuantity(node) - line.CancelledQuantity))
            .Where(item => item.Quantity > 0)
            .ToList();
        var returnedLeft = returnedLines
            .GroupBy(item => item.Line.ExternalLineId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Sum(item => item.Quantity), StringComparer.Ordinal);
        var packages = new List<RemotePackage>();
        var readableLineIds = lines.Select(line => line.ExternalLineId).ToHashSet(StringComparer.Ordinal);
        var unreadableLeft = unreadableLineIds.Count;
        foreach (var fulfillment in fulfillments)
        {
            var rawStatus = FulfillmentStatus(fulfillment);
            var tracking = fulfillment.GetProperty("trackingInfo").EnumerateArray().FirstOrDefault();
            var fulfillmentItems = fulfillment.GetProperty("fulfillmentLineItems").GetProperty("nodes").EnumerateArray().ToList();
            var fulfilledUnreadable = fulfillmentItems
                .Select(item => ShortId(NestedText(item, "lineItem", "id")))
                .Where(lineId => !readableLineIds.Contains(lineId) && unreadableLineIds.Contains(lineId))
                .Distinct(StringComparer.Ordinal)
                .Count();
            unreadableLeft = Math.Max(0, unreadableLeft - fulfilledUnreadable);
            var allocations = fulfillmentItems.Where(item => readableLineIds.Contains(ShortId(NestedText(item, "lineItem", "id")))).Select(item =>
            {
                var lineId = ShortId(NestedText(item, "lineItem", "id"));
                var quantity = DecimalOrNull(item, "quantity") ?? 0m;
                var cancelled = rawStatus == "CANCELLED" ? quantity : 0m;
                var active = rawStatus == "CANCELLED" ? 0m : quantity;
                var shipped = rawStatus is "SHIPPED" or "DELIVERED" ? active : 0m;
                var delivered = rawStatus == "DELIVERED" ? active : 0m;
                // Only a delivered unit is returned; a refunded unit still on its way stays active here.
                var returned = Math.Min(delivered, returnedLeft.GetValueOrDefault(lineId));
                if (returned > 0) returnedLeft[lineId] -= returned;
                return new RemotePackageAllocation(lineId, active, cancelled, shipped, delivered, returned);
            }).ToList();
            packages.Add(new(
                ShortId(fulfillment.GetProperty("id").GetString()),
                null,
                rawStatus,
                FulfillmentStatusOccurredAt(fulfillment, rawStatus),
                tracking.ValueKind == JsonValueKind.Object && tracking.TryGetProperty("company", out var company) ? company.GetString() : null,
                tracking.ValueKind == JsonValueKind.Object && tracking.TryGetProperty("number", out var number) ? number.GetString() : null,
                allocations,
                UnreadableLineCount: fulfilledUnreadable));
        }

        // Shopify exposes the original line quantity and the current quantity
        // separately. Keep the original quantity as the order line authority;
        // the synthetic remainder package makes partial cancellations and
        // partially fulfilled lines satisfy the shared package invariant.
        var remainderAllocations = lines.Select(line =>
        {
            var remaining = Math.Max(0, line.Quantity - line.CancelledQuantity - fulfilledByLine.GetValueOrDefault(line.ExternalLineId));
            return new RemotePackageAllocation(line.ExternalLineId, remaining, line.CancelledQuantity, 0, 0, 0);
        }).Where(allocation => allocation.AllocatedQuantity > 0 || allocation.CancelledQuantity > 0).ToList();
        // Unreadable lines no fulfillment holds are still to be fulfilled: the remainder holds and counts them.
        if (remainderAllocations.Count > 0 || unreadableLeft > 0)
        {
            var allCancelled = remainderAllocations.Count > 0 && unreadableLeft == 0 && remainderAllocations.All(allocation => allocation.AllocatedQuantity == 0);
            // Only the remainder holds the lines' own cancelled quantity; a cancelled fulfillment returns its items
            // to the order and is not a line cancellation, so it carries no cancellation time.
            var remainderCancelledAt = remainderAllocations.Any(allocation => allocation.CancelledQuantity > 0) ? orderCancelledAt : null;
            packages.Add(new(
                $"order:{ShortId(order.GetProperty("id").GetString())}:remainder",
                null,
                allCancelled ? "CANCELLED" : "UNFULFILLED",
                order.GetProperty("updatedAt").GetDateTimeOffset(),
                null,
                null,
                remainderAllocations,
                CancelledAt: remainderCancelledAt,
                UnreadableLineCount: unreadableLeft));
        }

        // Without taxes included, the discounts Shopify reports leave the tax out too: they take the lines' share of tax.
        var lineTotalBeforeTax = lineNodes.Sum(node => (DecimalOrNull(node, "quantity") ?? DecimalOrNull(node, "currentQuantity") ?? 0m) * UnitPriceAsReported(node));
        var lineTotalWithTax = lines.Sum(line => line.Quantity * line.UnitPrice);
        var discountTaxFactor = !taxesIncluded && lineTotalBeforeTax > 0 ? lineTotalWithTax / lineTotalBeforeTax : 1m;
        var (grossAmount, discountAmount, netAmount) = OrderAmounts(order, lines, returnedLines.Select(item => (item.Line, item.Quantity)).ToList(), discountTaxFactor);
        packages = SplitOrderAmounts(packages, lines, grossAmount, discountAmount);

        var refunds = order.TryGetProperty("refunds", out var refundsElement) && refundsElement.ValueKind == JsonValueKind.Array
            ? refundsElement.EnumerateArray().Select(refund => new RemoteOrderRefund(
                ShortId(refund.GetProperty("id").GetString()),
                refund.GetProperty("createdAt").GetDateTimeOffset(),
                DecimalOrNull(refund.GetProperty("totalRefundedSet").GetProperty("shopMoney"), "amount") ?? 0,
                refund.GetProperty("totalRefundedSet").GetProperty("shopMoney").GetProperty("currencyCode").GetString() ?? currency,
                refund.GetRawText())).ToList()
            : [];
        var refundedAmount = refunds.Sum(refund => refund.Amount);
        var refundStatus = financialStatus.ToUpperInvariant() switch
        {
            "REFUNDED" => "REFUNDED",
            "PARTIALLY_REFUNDED" => "PARTIALLY_REFUNDED",
            _ when refundedAmount > 0 => "PARTIALLY_REFUNDED",
            _ => "NOT_REFUNDED"
        };
        var orderEmail = order.TryGetProperty("email", out var orderEmailElement) && orderEmailElement.ValueKind == JsonValueKind.String ? orderEmailElement.GetString() : null;
        var orderPhone = order.TryGetProperty("phone", out var orderPhoneElement) && orderPhoneElement.ValueKind == JsonValueKind.String ? orderPhoneElement.GetString() : null;
        var customer = order.TryGetProperty("customer", out var customerElement) ? customerElement : default;
        var shippingAddress = order.TryGetProperty("shippingAddress", out var shippingAddressElement) && shippingAddressElement.ValueKind == JsonValueKind.Object ? shippingAddressElement : default;
        var billingAddress = order.TryGetProperty("billingAddress", out var billingAddressElement) && billingAddressElement.ValueKind == JsonValueKind.Object ? billingAddressElement : default;
        var addressFirstName = TextValue(shippingAddress, "firstName") ?? TextValue(billingAddress, "firstName");
        var addressLastName = TextValue(shippingAddress, "lastName") ?? TextValue(billingAddress, "lastName");
        var addressName = TextValue(shippingAddress, "name") ?? TextValue(billingAddress, "name") ?? JoinName(addressFirstName, addressLastName);
        var customerJson = customer.ValueKind == JsonValueKind.Object
            ? JsonSerializer.Serialize(new
            {
                id = TextValue(customer, "id"),
                customerName = TextValue(customer, "displayName") ?? JoinName(TextValue(customer, "firstName"), TextValue(customer, "lastName")),
                customerFirstName = TextValue(customer, "firstName"),
                customerLastName = TextValue(customer, "lastName"),
                email = NestedText(customer, "defaultEmailAddress", "emailAddress") ?? TextValue(customer, "email") ?? orderEmail,
                phone = NestedText(customer, "defaultPhoneNumber", "phoneNumber") ?? TextValue(customer, "phone") ?? orderPhone
            })
            : JsonSerializer.Serialize(new
            {
                customerName = addressName,
                customerFirstName = addressFirstName,
                customerLastName = addressLastName,
                email = orderEmail,
                phone = orderPhone
            });
        customerJson = WithInternetSaleFacts(customerJson, order, shopUrl);
        return new(
            ShortId(order.GetProperty("id").GetString()),
            order.GetProperty("name").GetString() ?? ShortId(order.GetProperty("id").GetString()),
            order.GetProperty("createdAt").GetDateTimeOffset(),
            order.GetProperty("updatedAt").GetDateTimeOffset(),
            currency,
            grossAmount,
            discountAmount,
            netAmount,
            customerJson,
            AddressJson(order, "shippingAddress"),
            AddressJson(order, "billingAddress"),
            lines,
            packages,
            order.GetRawText(),
            null,
            financialStatus,
            order.GetProperty("cancelledAt").ValueKind == JsonValueKind.Null ? "NOT_CANCELLED" : "CANCELLED",
            refundStatus,
            refundedAmount,
            refunds);
    }
    // The units of each line held by a fulfillment that is not cancelled.
    internal static Dictionary<string, decimal> FulfilledQuantities(IReadOnlyList<JsonElement> fulfillments)
    {
        var fulfilled = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var fulfillment in fulfillments)
        {
            if (FulfillmentStatus(fulfillment) == "CANCELLED") continue;
            foreach (var item in fulfillment.GetProperty("fulfillmentLineItems").GetProperty("nodes").EnumerateArray())
            {
                var quantity = DecimalOrNull(item, "quantity") ?? 0m;
                if (quantity <= 0) continue;
                var lineId = ShortId(NestedText(item, "lineItem", "id"));
                if (lineId.Length == 0) continue;
                fulfilled[lineId] = fulfilled.GetValueOrDefault(lineId) + quantity;
            }
        }
        return fulfilled;
    }

    // LineItem.quantity counts every unit ordered; currentQuantity leaves out the refunded and removed ones
    // (cancellations, order edits and returns alike).
    private static decimal RemovedQuantity(JsonElement line)
    {
        var ordered = DecimalOrNull(line, "quantity") ?? DecimalOrNull(line, "currentQuantity") ?? 0m;
        var current = DecimalOrNull(line, "currentQuantity") ?? ordered;
        return Math.Clamp(ordered - current, 0m, ordered);
    }

    // The order's sale after cancellations and before returns. currentTotalPriceSet and currentTotalDiscountsSet
    // are after cancellations, edits and returns alike; the original totalDiscountsSet still holds the discount of
    // a cancelled line, so pairing it with the current total overstated the gross (gross = net + discount). A
    // returned unit stays in the sale, so its value is added back with its share of the original discount.
    // discountTaxFactor turns a discount reported without tax (Order.taxesIncluded false) into one with tax, the way
    // the line prices were (1 when taxes are included).
    internal static (decimal Gross, decimal Discount, decimal Net) OrderAmounts(JsonElement order, IReadOnlyList<RemoteOrderLine> lines, IReadOnlyList<(RemoteOrderLine Line, decimal Quantity)> returned, decimal discountTaxFactor = 1m)
    {
        var currentTotal = MoneyAmount(order, "currentTotalPriceSet") ?? 0m;
        var originalDiscount = MoneyAmount(order, "totalDiscountsSet") is { } reportedOriginal ? Math.Round(reportedOriginal * discountTaxFactor, 2, MidpointRounding.AwayFromZero) : (decimal?)null;
        var reportedCurrent = MoneyAmount(order, "currentTotalDiscountsSet");
        var currentDiscount = (reportedCurrent is { } current ? Math.Round(current * discountTaxFactor, 2, MidpointRounding.AwayFromZero) : (decimal?)null) ?? originalDiscount ?? 0m;
        var returnedValue = returned.Sum(item => item.Line.UnitPrice * item.Quantity);
        var originalValue = lines.Sum(line => line.UnitPrice * line.Quantity);
        var returnedDiscount = returnedValue > 0 && originalValue > 0
            ? Math.Min(returnedValue, Math.Round((originalDiscount ?? currentDiscount) * returnedValue / originalValue, 2, MidpointRounding.AwayFromZero))
            : 0m;
        var discount = currentDiscount + returnedDiscount;
        var gross = currentTotal + currentDiscount + returnedValue;
        return (gross, discount, gross - discount);
    }

    private static decimal? MoneyAmount(JsonElement order, string field) =>
        order.TryGetProperty(field, out var set) && set.ValueKind == JsonValueKind.Object
        && set.TryGetProperty("shopMoney", out var money) && money.ValueKind == JsonValueKind.Object
            ? DecimalOrNull(money, "amount")
            : null;

    // A fulfilled unit is never cancelled: an order edit changes only unfulfilled units and a CANCEL restock is
    // for unfulfilled ones (RETURN is for fulfilled ones). Of the removed units, only those no fulfillment holds
    // are cancelled; the rest were refunded or returned after fulfillment.
    // A line item the panel can read: an id, a quantity (quantity or currentQuantity) and a unit price.
    internal static bool IsReadableOrderLine(JsonElement line) =>
        line.ValueKind == JsonValueKind.Object
        && OrderLineIdOrNull(line) is not null
        && (DecimalOrNull(line, "quantity") ?? DecimalOrNull(line, "currentQuantity")) is not null
        && line.TryGetProperty("originalUnitPriceSet", out var priceSet) && priceSet.ValueKind == JsonValueKind.Object
        && priceSet.TryGetProperty("shopMoney", out var shopMoney) && shopMoney.ValueKind == JsonValueKind.Object
        && DecimalOrNull(shopMoney, "amount") is not null;

    private static string? OrderLineIdOrNull(JsonElement line) =>
        line.ValueKind == JsonValueKind.Object && line.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(id.GetString())
            ? ShortId(id.GetString())
            : null;

    private static decimal UnitPriceAsReported(JsonElement line) =>
        line.TryGetProperty("originalUnitPriceSet", out var priceSet) && priceSet.ValueKind == JsonValueKind.Object
        && priceSet.TryGetProperty("shopMoney", out var shopMoney) && shopMoney.ValueKind == JsonValueKind.Object
            ? DecimalOrNull(shopMoney, "amount") ?? 0m
            : 0m;

    /// <summary>
    /// The line's tax rate as a percentage: the sum of the rates of its taxLines (LineItem.taxLines, each rate a
    /// fraction, 0.2 = 20%). Null when the line reports no tax line or a tax line without a numeric rate: the rate is
    /// unknown then, never 0 (InvoiceIssuePolicy judges the same raw line JSON).
    /// </summary>
    internal static decimal? LineTaxRatePercent(JsonElement line)
    {
        if (!line.TryGetProperty("taxLines", out var taxLines)) return null;
        // The list InvoiceIssuePolicy reads: a single object counts as one tax line there too.
        IEnumerable<JsonElement> items = taxLines.ValueKind switch
        {
            JsonValueKind.Array => taxLines.EnumerateArray().ToList(),
            JsonValueKind.Object => [taxLines],
            _ => []
        };
        if (!items.Any()) return null;
        var total = 0m;
        foreach (var taxLine in items)
        {
            if (taxLine.ValueKind != JsonValueKind.Object || DecimalOrNull(taxLine, "rate") is not { } rate) return null;
            total += rate;
        }
        return Math.Round(total * 100m, 4, MidpointRounding.AwayFromZero);
    }

    private static RemoteOrderLine MapOrderLine(JsonElement line, string financialStatus, IReadOnlyDictionary<string, decimal> fulfilledByLine, bool taxesIncluded = true)
    {
        var variant = line.TryGetProperty("variant", out var variantElement) && variantElement.ValueKind == JsonValueKind.Object ? variantElement : default;
        var lineSku = line.TryGetProperty("sku", out var skuElement) && skuElement.ValueKind == JsonValueKind.String ? skuElement.GetString() : null;
        var variantSku = variant.ValueKind == JsonValueKind.Object && variant.TryGetProperty("sku", out var variantSkuElement) && variantSkuElement.ValueKind == JsonValueKind.String ? variantSkuElement.GetString() : null;
        var barcode = variant.ValueKind == JsonValueKind.Object ? VariantBarcode(variant) : null;
        var sku = string.IsNullOrWhiteSpace(lineSku) ? variantSku : lineSku;
        var lineId = ShortId(line.GetProperty("id").GetString());
        var orderedQuantity = DecimalOrNull(line, "quantity") ?? DecimalOrNull(line, "currentQuantity") ?? 0m;
        var unfulfilled = Math.Max(0m, orderedQuantity - fulfilledByLine.GetValueOrDefault(lineId));
        var cancelledQuantity = Math.Min(RemovedQuantity(line), unfulfilled);
        var vatRate = LineTaxRatePercent(line);
        var unitPrice = UnitPriceAsReported(line);
        // Prices without tax take the line's own reported tax, so the line carries what the customer paid per unit (the
        // raw snapshot keeps Shopify's price before tax). A line without a reported rate was charged no tax: its price
        // is already what was paid.
        if (!taxesIncluded && vatRate is > 0) unitPrice = Math.Round(unitPrice * (1m + vatRate.Value / 100m), 4, MidpointRounding.AwayFromZero);
        // A line without a SKU keeps none: no made-up code ("SHOPIFY-LINE" before 5 Nov 2026), so it stays unmatched
        // until the owner links it, as on every other marketplace.
        return new(lineId, string.IsNullOrWhiteSpace(sku) ? "" : sku!, barcode, TextValue(line, "name") ?? "Shopify ürünü", orderedQuantity, unitPrice, vatRate, financialStatus, line.GetRawText(), cancelledQuantity);
    }

    internal static string FulfillmentStatus(JsonElement fulfillment)
    {
        if (fulfillment.TryGetProperty("deliveredAt", out var deliveredAt) && deliveredAt.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
            return "DELIVERED";
        if (fulfillment.TryGetProperty("displayStatus", out var displayStatus) && string.Equals(displayStatus.GetString(), "DELIVERED", StringComparison.OrdinalIgnoreCase))
            return "DELIVERED";
        if (fulfillment.TryGetProperty("events", out var events)
            && events.ValueKind == JsonValueKind.Object
            && events.TryGetProperty("nodes", out var eventNodes)
            && eventNodes.ValueKind == JsonValueKind.Array
            && eventNodes.EnumerateArray().Any(item => item.TryGetProperty("status", out var eventStatus) && string.Equals(eventStatus.GetString(), "DELIVERED", StringComparison.OrdinalIgnoreCase)))
            return "DELIVERED";
        var raw = fulfillment.TryGetProperty("status", out var status) ? status.GetString()?.Trim().ToUpperInvariant() : null;
        // FulfillmentStatus: CANCELLED, ERROR, FAILURE, OPEN, PENDING, SUCCESS. A failed or unknown fulfillment
        // has not shipped; it is named as such (manual review) instead of falling through to SHIPPED.
        return raw switch
        {
            "CANCELLED" or "CANCELED" => "CANCELLED",
            "OPEN" or "PENDING" or "IN_PROGRESS" => "PROCESSING",
            "SUCCESS" or "FULFILLED" => "SHIPPED",
            "ERROR" or "FAILURE" => "FULFILLMENT_FAILED",
            _ => "UNKNOWN"
        };
    }

    internal static DateTimeOffset FulfillmentStatusOccurredAt(JsonElement fulfillment, string rawStatus)
    {
        if (string.Equals(rawStatus, "DELIVERED", StringComparison.OrdinalIgnoreCase))
        {
            if (fulfillment.TryGetProperty("deliveredAt", out var deliveredAt)
                && deliveredAt.ValueKind == JsonValueKind.String
                && deliveredAt.TryGetDateTimeOffset(out var deliveredAtValue))
                return deliveredAtValue;

            if (fulfillment.TryGetProperty("events", out var events)
                && events.ValueKind == JsonValueKind.Object
                && events.TryGetProperty("nodes", out var eventNodes)
                && eventNodes.ValueKind == JsonValueKind.Array)
            {
                var deliveredEventAt = eventNodes.EnumerateArray()
                    .Where(item => item.TryGetProperty("status", out var status)
                        && string.Equals(status.GetString(), "DELIVERED", StringComparison.OrdinalIgnoreCase))
                    .Select(item => item.TryGetProperty("happenedAt", out var happenedAt)
                        && happenedAt.ValueKind == JsonValueKind.String
                        && happenedAt.TryGetDateTimeOffset(out var happenedAtValue)
                            ? happenedAtValue
                            : (DateTimeOffset?)null)
                    .Where(value => value.HasValue)
                    .Max();
                if (deliveredEventAt.HasValue) return deliveredEventAt.Value;
            }
        }

        if (fulfillment.TryGetProperty("updatedAt", out var updatedAt)
            && updatedAt.ValueKind == JsonValueKind.String
            && updatedAt.TryGetDateTimeOffset(out var updatedAtValue))
            return updatedAtValue;

        return fulfillment.GetProperty("createdAt").GetDateTimeOffset();
    }

    private static string AddressJson(JsonElement order, string property) => order.TryGetProperty(property, out var address) && address.ValueKind != JsonValueKind.Null ? address.GetRawText() : "{}";
    private static string? JoinName(string? firstName, string? lastName) => string.Join(" ", new[] { firstName, lastName }.Where(value => !string.IsNullOrWhiteSpace(value))).Trim() is { Length: > 0 } name ? name : null;

    // Shopify gives the order's totals only. Each package used to carry the whole order total, so an order
    // with two fulfillments counted twice. The gross and the discount are shared by the value of each package's
    // lines (allocated quantity x unit price) and the net is gross - discount, so a package's gross is its line
    // value and its own amounts agree (the invoice refuses a package whose gross, discount and payable disagree;
    // the gross used to be the net). The last package takes the rounding rest, so the packages add up to the
    // order to the kuruş. A single package keeps the order totals.
    internal static List<RemotePackage> SplitOrderAmounts(List<RemotePackage> packages, IReadOnlyList<RemoteOrderLine> lines, decimal gross, decimal discount)
    {
        if (packages.Count == 0) return packages;
        var prices = lines.GroupBy(line => line.ExternalLineId, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First().UnitPrice, StringComparer.Ordinal);
        var weights = packages.Select(package => package.Allocations.Sum(allocation => allocation.AllocatedQuantity * prices.GetValueOrDefault(allocation.ExternalLineId))).ToArray();
        var weightTotal = weights.Sum();
        if (weightTotal <= 0)
        {
            weights = packages.Select((package, index) => index == 0 ? 1m : 0m).ToArray();
            weightTotal = 1m;
        }
        var lastWeighted = Array.FindLastIndex(weights, weight => weight > 0);
        var result = new List<RemotePackage>(packages.Count);
        decimal grossLeft = gross, discountLeft = discount;
        for (var index = 0; index < packages.Count; index++)
        {
            decimal packageGross, packageDiscount;
            if (weights[index] <= 0) packageGross = packageDiscount = 0m;
            else if (index == lastWeighted) (packageGross, packageDiscount) = (grossLeft, discountLeft);
            else
            {
                var share = weights[index] / weightTotal;
                packageGross = Math.Round(gross * share, 2, MidpointRounding.AwayFromZero);
                packageDiscount = Math.Min(packageGross, Math.Round(discount * share, 2, MidpointRounding.AwayFromZero));
            }
            grossLeft -= packageGross;
            discountLeft -= packageDiscount;
            result.Add(packages[index] with { GrossAmount = packageGross, DiscountAmount = packageDiscount, NetAmount = packageGross - packageDiscount });
        }
        return result;
    }
}
