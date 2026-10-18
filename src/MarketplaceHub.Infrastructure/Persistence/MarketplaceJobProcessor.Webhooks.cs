using System.Text.Json;
using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Persistence;

public sealed partial class MarketplaceJobProcessor
{
    // A Hepsiburada order event (new order, package, cancellation, shipping, delivery, address) is a
    // trigger: the order it names is read again, so every event kind is handled the same way and a
    // repeated or late event cannot overwrite newer data.
    private async Task<bool> ReadHepsiburadaOrderEvent(Guid tenantId, Guid connectionId, JsonElement root, string correlationId, CancellationToken cancellationToken)
    {
        var orderNumber = FindString(root, "orderNumber");
        var packageNumber = FindString(root, "packageNumber");
        if (orderNumber is null && packageNumber is not null)
        {
            TrackRequest();
            var tracking = await orderPackages.GetPackageTrackingInfoAsync(Context(tenantId, connectionId, correlationId, $"hepsiburada-webhook-package:{packageNumber}"), packageNumber, cancellationToken);
            if (!tracking.IsSuccess) { TrackResultFailure(tracking.Error); throw JobProcessingException.FromAdapter(tracking.Error!); }
            orderNumber = string.IsNullOrWhiteSpace(tracking.Value!.OrderNumber) ? null : tracking.Value.OrderNumber.Trim();
        }
        // Nothing to read: the scheduled reads still bring the change.
        if (orderNumber is null) return true;
        var payload = JsonSerializer.Serialize(new { connectionId, externalOrderId = orderNumber, packageNumber });
        return await SyncHepsiburadaOrders(tenantId, connectionId, payload, correlationId, "ORDERS", false, cancellationToken);
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
