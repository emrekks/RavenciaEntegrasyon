using System.Text.Json;
using MarketplaceHub.Application;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

public sealed partial class MarketplaceJobProcessor
{
    // A Hepsiburada order event (new order, package, cancellation, shipping, delivery, address) is a
    // trigger: the order it names is read again, so every event kind is handled the same way and a
    // repeated or late event cannot overwrite newer data.
    // Package events (intransit, deliver, undeliver) carry no order number: the package is looked up
    // in the panel first and asked from Hepsiburada only when unknown. unpack lists its orders in
    // orderNumbers[]; a line item cancel names the line. The event path (packages/{no}/deliver,
    // lineitems/{id}/cancel, orders/{no}/shippingaddress) fills in what the body leaves out.
    private async Task<bool> ReadHepsiburadaOrderEvent(Guid tenantId, Guid connectionId, JsonElement root, string? eventPath, string correlationId, CancellationToken cancellationToken)
    {
        var reference = HepsiburadaEventReference(eventPath);
        var orderNumbers = new List<string>();
        void AddOrder(string? value)
        {
            if (!string.IsNullOrWhiteSpace(value) && !orderNumbers.Contains(value.Trim(), StringComparer.Ordinal)) orderNumbers.Add(value.Trim());
        }
        AddOrder(FindString(root, "orderNumber"));
        foreach (var number in FindStrings(root, "orderNumbers")) AddOrder(number);
        if (reference is ("orders", var pathOrder)) AddOrder(pathOrder);
        var packageNumber = FindString(root, "packageNumber") ?? (reference is ("packages", var pathPackage) ? pathPackage : null);
        var lineItemId = FindString(root, "lineItemId") ?? (reference is ("lineitems", var pathLine) ? pathLine : null);

        if (orderNumbers.Count == 0 && packageNumber is not null)
            AddOrder(await (from package in db.ShipmentPackages.AsNoTracking()
                            join order in db.Orders.AsNoTracking() on package.OrderId equals order.Id
                            where package.TenantId == tenantId && order.ConnectionId == connectionId && package.ExternalPackageId == packageNumber
                            select order.ExternalOrderId).FirstOrDefaultAsync(cancellationToken));
        if (orderNumbers.Count == 0 && lineItemId is not null)
            AddOrder(await (from line in db.OrderLines.AsNoTracking()
                            join order in db.Orders.AsNoTracking() on line.OrderId equals order.Id
                            where order.TenantId == tenantId && order.ConnectionId == connectionId && line.ExternalLineId == lineItemId
                            select order.ExternalOrderId).FirstOrDefaultAsync(cancellationToken));
        if (orderNumbers.Count == 0 && packageNumber is not null)
        {
            TrackRequest();
            var tracking = await orderPackages.GetPackageTrackingInfoAsync(Context(tenantId, connectionId, correlationId, $"hepsiburada-webhook-package:{packageNumber}"), packageNumber, cancellationToken);
            if (!tracking.IsSuccess) { TrackResultFailure(tracking.Error); throw JobProcessingException.FromAdapter(tracking.Error!); }
            AddOrder(tracking.Value!.OrderNumber);
        }
        // Nothing to read: the scheduled reads still bring the change.
        if (orderNumbers.Count == 0) return true;
        var completed = true;
        foreach (var orderNumber in orderNumbers.Take(HepsiburadaPackagePolicy.MaxOrderReadsPerRun))
        {
            var payload = JsonSerializer.Serialize(new { connectionId, externalOrderId = orderNumber, packageNumber = orderNumbers.Count == 1 ? packageNumber : null });
            completed &= await SyncHepsiburadaOrders(tenantId, connectionId, payload, correlationId, "ORDERS", false, cancellationToken);
        }
        return completed;
    }

    // ("packages", "123") from "packages/123/deliver": the kind and id of an order event path.
    internal static (string Kind, string Id)? HepsiburadaEventReference(string? eventPath)
    {
        var segments = WebhookEventPath.Normalize(eventPath)?.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments is { Length: >= 3 } && segments[0] is "packages" or "lineitems" or "orders"
            ? (segments[0], Uri.UnescapeDataString(segments[1]))
            : null;
    }

    // The event path kept in an order event's message id ("{connection}:order_event:{path}:{hash}").
    internal static string? OrderEventPath(string externalMessageId)
    {
        const string marker = "order_event:";
        var start = externalMessageId.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return null;
        start += marker.Length;
        var end = externalMessageId.LastIndexOf(':');
        return end > start ? externalMessageId[start..end] : null;
    }

    // Every string or number under a property of that name (a single value or an array), searched depth-first.
    internal static IReadOnlyList<string> FindStrings(JsonElement element, string name)
    {
        var found = new List<string>();
        Walk(element);
        return found;

        void Walk(JsonElement current)
        {
            if (current.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in current.EnumerateArray()) Walk(item);
                return;
            }
            if (current.ValueKind != JsonValueKind.Object) return;
            foreach (var property in current.EnumerateObject())
            {
                if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    Walk(property.Value);
                    continue;
                }
                var values = property.Value.ValueKind == JsonValueKind.Array ? property.Value.EnumerateArray().ToArray() : [property.Value];
                foreach (var value in values)
                {
                    var text = value.ValueKind switch { JsonValueKind.String => value.GetString(), JsonValueKind.Number => value.GetRawText(), _ => null };
                    if (!string.IsNullOrWhiteSpace(text)) found.Add(text.Trim());
                }
            }
        }
    }

    // The first string or number property of that name (case-insensitive), searched depth-first.
    internal static string? FindString(JsonElement element, string name)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        var value = property.Value.ValueKind switch
                        {
                            JsonValueKind.String => property.Value.GetString(),
                            JsonValueKind.Number => property.Value.GetRawText(),
                            _ => null
                        };
                        if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
                    }
                }
                foreach (var property in element.EnumerateObject())
                    if (FindString(property.Value, name) is { } nested) return nested;
                return null;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    if (FindString(item, name) is { } nested) return nested;
                return null;
            default:
                return null;
        }
    }
}
