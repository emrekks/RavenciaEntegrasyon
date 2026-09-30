using System.Text.Json.Nodes;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;

namespace MarketplaceHub.Infrastructure.Persistence;

internal static class HepsiburadaStaleOrderEnrichmentPolicy
{
    private static readonly IReadOnlySet<string> RefreshableRemoteFacts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "marketplaceInvoiceStatus",
        "invoiceStatus",
        "marketplaceCargoProviderName",
        "cargoProviderName"
    };

    public static int Apply(Order order, IReadOnlyCollection<OrderLine> lines, RemoteOrder remote, DateTimeOffset observedAt)
    {
        if (!string.Equals(order.ExternalOrderId, remote.ExternalOrderId, StringComparison.Ordinal)
            || remote.LastModifiedAt >= order.LastRemoteModifiedAt)
            return 0;

        var orderChanged = false;
        var lineUpdates = 0;

        if (order.ShipmentDueAt is null && remote.ShipmentDueAt is { } dueAt)
        {
            order.ShipmentDueAt = dueAt;
            orderChanged = true;
        }

        var customer = MergeSnapshot(order.CustomerSnapshotJson, remote.CustomerSnapshotJson, RefreshableRemoteFacts);
        if (customer.Changed)
        {
            order.CustomerSnapshotJson = customer.Json!;
            orderChanged = true;
        }

        var shipmentAddress = MergeSnapshot(order.ShipmentAddressSnapshotJson, remote.ShipmentAddressSnapshotJson);
        if (shipmentAddress.Changed)
        {
            order.ShipmentAddressSnapshotJson = shipmentAddress.Json!;
            orderChanged = true;
        }

        var invoiceAddress = MergeSnapshot(order.InvoiceAddressSnapshotJson, remote.InvoiceAddressSnapshotJson);
        if (invoiceAddress.Changed)
        {
            order.InvoiceAddressSnapshotJson = invoiceAddress.Json!;
            orderChanged = true;
        }

        var remoteLines = remote.Lines
            .Where(x => !string.IsNullOrWhiteSpace(x.ExternalLineId))
            .GroupBy(x => x.ExternalLineId, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);

        foreach (var line in lines)
        {
            if (!remoteLines.TryGetValue(line.ExternalLineId, out var remoteLine)) continue;
            var changed = false;

            if (string.IsNullOrWhiteSpace(line.Barcode) && !string.IsNullOrWhiteSpace(remoteLine.Barcode))
            {
                line.Barcode = remoteLine.Barcode;
                changed = true;
            }

            if (IsFallbackTitle(line.TitleSnapshot, line.Sku)
                && !string.IsNullOrWhiteSpace(remoteLine.Title)
                && !string.Equals(remoteLine.Title.Trim(), remoteLine.Sku.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                line.TitleSnapshot = remoteLine.Title;
                changed = true;
            }

            var lineSnapshot = MergeSnapshot(line.SourceSnapshotJson, remoteLine.SourceSnapshotJson);
            if (lineSnapshot.Changed)
            {
                line.SourceSnapshotJson = lineSnapshot.Json;
                changed = true;
            }

            if (!changed) continue;
            line.Version++;
            lineUpdates++;
        }

        if (!orderChanged && lineUpdates == 0) return 0;

        order.UpdatedAt = observedAt;
        order.Version++;
        return 1 + lineUpdates;
    }

    private static bool IsFallbackTitle(string title, string sku) =>
        string.IsNullOrWhiteSpace(title)
        || string.Equals(title.Trim(), sku.Trim(), StringComparison.OrdinalIgnoreCase);

    private static (string? Json, bool Changed) MergeSnapshot(string? existingJson, string? remoteJson, IReadOnlySet<string>? refreshableFacts = null)
    {
        var target = ParseObject(existingJson);
        var remote = ParseObject(remoteJson);
        if (remote.Count == 0) return (existingJson, false);

        var original = target.ToJsonString();
        MergeMissing(target, remote, refreshableFacts ?? EmptyFactSet);
        var merged = target.ToJsonString();
        return string.Equals(original, merged, StringComparison.Ordinal)
            ? (existingJson, false)
            : (merged, true);
    }

    private static readonly IReadOnlySet<string> EmptyFactSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private static JsonObject ParseObject(string? json)
    {
        try
        {
            if (JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json) is JsonObject value) return value;
        }
        catch (System.Text.Json.JsonException) { }

        return new JsonObject();
    }

    private static void MergeMissing(JsonObject target, JsonObject remote, IReadOnlySet<string> refreshableFacts)
    {
        foreach (var (remoteKey, remoteValue) in remote.ToList())
        {
            if (IsEmpty(remoteValue)) continue;
            var targetKey = target.Select(property => property.Key)
                .FirstOrDefault(key => string.Equals(key, remoteKey, StringComparison.OrdinalIgnoreCase));
            if (targetKey is null)
            {
                target[remoteKey] = remoteValue?.DeepClone();
                continue;
            }

            var targetValue = target[targetKey];
            if (refreshableFacts.Contains(remoteKey))
            {
                target[targetKey] = remoteValue?.DeepClone();
                continue;
            }

            if (IsEmpty(targetValue))
                target[targetKey] = remoteValue?.DeepClone();
            else if (targetValue is JsonObject targetObject && remoteValue is JsonObject remoteObject)
                MergeMissing(targetObject, remoteObject, refreshableFacts);
        }
    }

    private static bool IsEmpty(JsonNode? value) => value is null
        || value is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text) && string.IsNullOrWhiteSpace(text)
        || value is JsonObject jsonObject && jsonObject.Count == 0
        || value is JsonArray jsonArray && jsonArray.Count == 0;
}
