using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
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
    private async Task<bool> ReadHepsiburadaOrderEvent(Guid tenantId, Guid connectionId, JsonElement root, string eventKey, string? eventPath, string correlationId, CancellationToken cancellationToken)
    {
        var reference = HepsiburadaEventReference(eventPath);
        // An address change carries the whole new delivery address, while a read of the order may count as older
        // than the order and keep the old one: the body is written first.
        if (reference is ("orders", var addressOrder) && WebhookEventPath.Normalize(eventPath)?.EndsWith("/shippingaddress", StringComparison.Ordinal) == true)
            await ApplyHepsiburadaShippingAddressAsync(tenantId, connectionId, addressOrder, root, eventKey, cancellationToken);
        var orderNumbers = new List<string>();
        void AddOrder(string? value)
        {
            if (!string.IsNullOrWhiteSpace(value) && !orderNumbers.Contains(value.Trim(), StringComparer.Ordinal)) orderNumbers.Add(value.Trim());
        }
        // A package can hold the lines of several orders of one buyer: every orderNumber in the body is read.
        foreach (var number in FindStrings(root, "orderNumber")) AddOrder(number);
        foreach (var number in FindStrings(root, "orderNumbers")) AddOrder(number);
        if (reference is ("orders", var pathOrder)) AddOrder(pathOrder);
        var packageNumber = FindString(root, "packageNumber") ?? (reference is ("packages", var pathPackage) ? pathPackage : null);
        var lineItemId = FindString(root, "lineItemId") ?? (reference is ("lineitems", var pathLine) ? pathLine : null);
        // An unpacked package no longer exists on Hepsiburada (its lines return to the order and reads stop listing
        // it): the panel's package is closed as the panel's own unpack does, and its orders are read without it.
        var unpack = WebhookEventPath.Normalize(eventPath)?.EndsWith("/unpack", StringComparison.Ordinal) == true;
        if (unpack && packageNumber is not null)
        {
            var unpackedAt = DateTimeOffset.TryParse(FindString(root, "unpackedDate"), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var date) ? date : timeProvider.GetUtcNow();
            var packages = await (from package in db.ShipmentPackages.AsNoTracking()
                                  join order in db.Orders.AsNoTracking() on package.OrderId equals order.Id
                                  where package.TenantId == tenantId && order.ConnectionId == connectionId && package.ExternalPackageId == packageNumber
                                  select new { package.Id, order.ExternalOrderId }).ToListAsync(cancellationToken);
            foreach (var package in packages)
            {
                await MarkHepsiburadaPackageUnpackedAsync(tenantId, connectionId, package.Id, unpackedAt, cancellationToken);
                AddOrder(package.ExternalOrderId);
            }
        }

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
        if (orderNumbers.Count == 0 && packageNumber is not null && !unpack)
        {
            TrackRequest();
            var tracking = await orderPackages.GetPackageTrackingInfoAsync(Context(tenantId, connectionId, correlationId, $"hepsiburada-webhook-package:{packageNumber}"), packageNumber, cancellationToken);
            if (!tracking.IsSuccess) { TrackResultFailure(tracking.Error); throw JobProcessingException.FromAdapter(tracking.Error!); }
            AddOrder(tracking.Value!.OrderNumber);
        }
        // Nothing to read: the scheduled reads still bring the change.
        if (orderNumbers.Count == 0) return true;
        // An event naming more orders than one run reads (an unpack of many orders) hands the rest to targeted
        // order reads of their own instead of dropping them; they are queued first, so a retry of this event
        // after a failed read finds them already there.
        await QueueHepsiburadaOrderReadsAsync(tenantId, connectionId, orderNumbers.Skip(HepsiburadaPackagePolicy.MaxOrderReadsPerRun), eventKey, correlationId, cancellationToken);
        var completed = true;
        foreach (var orderNumber in orderNumbers.Take(HepsiburadaPackagePolicy.MaxOrderReadsPerRun))
        {
            var payload = JsonSerializer.Serialize(new { connectionId, externalOrderId = orderNumber, packageNumber = orderNumbers.Count == 1 && !unpack ? packageNumber : null });
            completed &= await SyncHepsiburadaOrders(tenantId, connectionId, payload, correlationId, "ORDERS", false, cancellationToken);
        }
        return completed;
    }

    // The new delivery address of PUT /orders/{n}/shippingaddress (addressId, address, name, email, countryCode, phone
    // numbers, district, city, town: the names of the order's shippingAddress) replaces the stored one's fields.
    // An event whose order read failed runs again later, after address events that arrived meanwhile: it is applied only
    // while no later address event of the order has arrived, so a retry never brings an older address back.
    internal async Task ApplyHepsiburadaShippingAddressAsync(Guid tenantId, Guid connectionId, string orderNumber, JsonElement root, string eventKey, CancellationToken cancellationToken)
    {
        if (root.ValueKind != JsonValueKind.Object) return;
        if (FindString(root, "orderNumber") is { } bodyOrder && !string.Equals(bodyOrder, orderNumber, StringComparison.Ordinal)) return;
        var order = await db.Orders.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ExternalOrderId == orderNumber, cancellationToken);
        if (order is null) return;
        if (await LaterHepsiburadaEventArrivedAsync(tenantId, eventKey, cancellationToken)) return;
        var address = new System.Text.Json.Nodes.JsonObject();
        foreach (var property in root.EnumerateObject())
            if (!string.Equals(property.Name, "orderNumber", StringComparison.OrdinalIgnoreCase))
                address[property.Name] = System.Text.Json.Nodes.JsonNode.Parse(property.Value.GetRawText());
        if (address.Count == 0) return;
        var merged = MarketplaceHub.Infrastructure.Imports.ShopifyOrderCsvSnapshotPolicy.MergeRemoteSnapshot(address.ToJsonString(), order.ShipmentAddressSnapshotJson);
        if (string.Equals(merged, order.ShipmentAddressSnapshotJson, StringComparison.Ordinal)) return;
        order.ShipmentAddressSnapshotJson = merged;
        order.UpdatedAt = timeProvider.GetUtcNow();
        order.Version++;
        await db.SaveChangesAsync(cancellationToken);
    }

    // True when another event of the same path (its message id up to the body hash: "{connection}:order_event:{path}:")
    // reached the inbox after this one. An event without its inbox row has no arrival time to compare and counts as latest.
    private async Task<bool> LaterHepsiburadaEventArrivedAsync(Guid tenantId, string eventKey, CancellationToken cancellationToken)
    {
        const string source = "HEPSIBURADA_WEBHOOK";
        var separator = eventKey.LastIndexOf(':');
        if (separator <= 0) return false;
        var samePath = eventKey[..(separator + 1)];
        var receivedAt = await db.InboxMessages.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.Source == source && x.ExternalMessageId == eventKey)
            .Select(x => (DateTimeOffset?)x.ReceivedAt).SingleOrDefaultAsync(cancellationToken);
        return receivedAt is { } at && await db.InboxMessages.AsNoTracking()
            .AnyAsync(x => x.TenantId == tenantId && x.Source == source && x.ExternalMessageId.StartsWith(samePath) && x.ReceivedAt > at, cancellationToken);
    }

    // One targeted Hepsiburada order read per order, keyed by the event (its message id) so the same event
    // never queues an order twice while a later event for the same order still gets its own read.
    private async Task QueueHepsiburadaOrderReadsAsync(Guid tenantId, Guid connectionId, IEnumerable<string> orderNumbers, string eventKey, string correlationId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var queued = 0;
        foreach (var orderNumber in orderNumbers)
        {
            var dedup = $"hepsiburada-webhook-order:{connectionId:N}:{Hash($"{orderNumber}\n{eventKey}")}";
            if (await db.IntegrationJobs.AnyAsync(x => x.TenantId == tenantId && x.JobType == MarketplaceJobTypes.HepsiburadaOrderSync && x.JobDedupKey == dedup, cancellationToken)) continue;
            var payload = JsonSerializer.Serialize(new { connectionId, externalOrderId = orderNumber, packageNumber = (string?)null });
            db.IntegrationJobs.Add(new IntegrationJob
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                ConnectionId = connectionId,
                JobType = MarketplaceJobTypes.HepsiburadaOrderSync,
                PayloadJson = payload,
                PayloadVersion = 1,
                PayloadHash = Hash(payload),
                JobDedupKey = dedup,
                EffectIdempotencyKey = dedup,
                Priority = JobPriorityPolicy.For(MarketplaceJobTypes.HepsiburadaOrderSync, userInitiated: false),
                Status = JobStatus.Pending,
                AvailableAt = now,
                CorrelationId = correlationId,
                CreatedAt = now,
                Version = 1
            });
            queued++;
        }
        if (queued > 0) await db.SaveChangesAsync(cancellationToken);
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
