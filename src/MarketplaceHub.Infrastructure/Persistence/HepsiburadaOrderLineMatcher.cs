using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;

namespace MarketplaceHub.Infrastructure.Persistence;

internal static class HepsiburadaOrderLineMatcher
{
    public static bool CanAddMissingLine(RemoteOrderLine remoteLine, IReadOnlyCollection<OrderLine> existingLines, IReadOnlyCollection<RemoteOrderLine> remoteLines)
    {
        if (FindExistingLine(remoteLine, existingLines, remoteLines) is not null) return false;
        var identities = IdentityAliases(remoteLine.ExternalLineId, remoteLine.SourceSnapshotJson);
        var products = ProductAliases(remoteLine);
        if (existingLines.Any(line => IdentityAliases(line.ExternalLineId, line.SourceSnapshotJson).Overlaps(identities)))
            return false;
        // The same SKU may occur on different order lines. Add the missing line
        // only if every local product match is already accounted for by another
        // uniquely matched remote line; otherwise a claim alias is ambiguous.
        return existingLines.Where(line => ProductAliases(line).Overlaps(products))
            .All(line => remoteLines.Any(other => !string.Equals(other.ExternalLineId, remoteLine.ExternalLineId, StringComparison.Ordinal)
                && ReferenceEquals(FindExistingLine(other, existingLines, remoteLines), line)));
    }

    public static OrderLine? FindExistingLine(
        RemoteOrderLine remoteLine,
        IReadOnlyCollection<OrderLine> existingLines,
        IReadOnlyCollection<RemoteOrderLine> remoteLines)
    {
        var exactIdMatches = existingLines
            .Where(line => string.Equals(line.ExternalLineId, remoteLine.ExternalLineId, StringComparison.Ordinal))
            .DistinctBy(line => line.Id)
            .ToArray();
        var exactRemoteIdMatches = remoteLines
            .Where(line => string.Equals(line.ExternalLineId, remoteLine.ExternalLineId, StringComparison.Ordinal))
            .DistinctBy(line => line.ExternalLineId)
            .Take(2)
            .Count();
        if (exactIdMatches.Length == 1 && exactRemoteIdMatches == 1) return exactIdMatches[0];

        var remoteLineIds = IdentityAliases(remoteLine.ExternalLineId, remoteLine.SourceSnapshotJson);
        var remoteIdentityMatches = remoteLines
            .Where(line => IdentityAliases(line.ExternalLineId, line.SourceSnapshotJson).Overlaps(remoteLineIds))
            .DistinctBy(line => line.ExternalLineId)
            .Take(2)
            .Count();
        if (remoteIdentityMatches > 1) return null;
        var identityMatches = existingLines
            .Where(line => IdentityAliases(line.ExternalLineId, line.SourceSnapshotJson).Overlaps(remoteLineIds))
            .DistinctBy(line => line.Id)
            .ToArray();
        if (identityMatches.Length == 1) return identityMatches[0];
        if (identityMatches.Length > 1) return null;

        var localProductIndex = Index(existingLines, ProductAliases);
        var remoteProductIndex = Index(remoteLines, ProductAliases);
        var safeAliases = ProductAliases(remoteLine)
            .Where(alias => localProductIndex.TryGetValue(alias, out var locals) && locals.Count == 1
                && remoteProductIndex.TryGetValue(alias, out var remotes) && remotes.Count == 1)
            .ToArray();
        var productMatches = safeAliases
            .Select(alias => localProductIndex[alias][0])
            .DistinctBy(line => line.Id)
            .ToArray();
        return productMatches.Length == 1 ? productMatches[0] : null;
    }

    private static HashSet<string> IdentityAliases(string? externalLineId, string? snapshotJson)
    {
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddAlias(aliases, "id", externalLineId);
        AddSnapshotAliases(aliases, snapshotJson,
            ["lineItemId", "orderLineId", "orderItemId", "externalLineId"], "id");
        return aliases;
    }

    private static HashSet<string> ProductAliases(OrderLine line)
    {
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddAlias(aliases, "sku", line.Sku);
        AddAlias(aliases, "barcode", line.Barcode);
        AddSnapshotAliases(aliases, line.SourceSnapshotJson, ["sku", "merchantSku", "sellerSku", "hbSku"], "sku");
        AddSnapshotAliases(aliases, line.SourceSnapshotJson, ["barcode", "productBarcode", "gtin"], "barcode");
        return aliases;
    }

    private static HashSet<string> ProductAliases(RemoteOrderLine line)
    {
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddAlias(aliases, "sku", line.Sku);
        AddAlias(aliases, "barcode", line.Barcode);
        AddSnapshotAliases(aliases, line.SourceSnapshotJson, ["sku", "merchantSku", "sellerSku", "hbSku"], "sku");
        AddSnapshotAliases(aliases, line.SourceSnapshotJson, ["barcode", "productBarcode", "gtin"], "barcode");
        return aliases;
    }

    private static Dictionary<string, List<T>> Index<T>(IEnumerable<T> rows, Func<T, HashSet<string>> aliases)
    {
        var index = new Dictionary<string, List<T>>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        foreach (var alias in aliases(row))
        {
            if (!index.TryGetValue(alias, out var matches)) index[alias] = matches = [];
            matches.Add(row);
        }

        return index;
    }

    private static void AddSnapshotAliases(HashSet<string> aliases, string? snapshotJson, IReadOnlyCollection<string> propertyNames, string kind)
    {
        if (string.IsNullOrWhiteSpace(snapshotJson)) return;
        try
        {
            using var document = JsonDocument.Parse(snapshotJson);
            AddSnapshotAliases(aliases, document.RootElement, propertyNames, kind);
        }
        catch (JsonException) { }
    }

    private static void AddSnapshotAliases(HashSet<string> aliases, JsonElement element, IReadOnlyCollection<string> propertyNames, string kind)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (propertyNames.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
                    AddAlias(aliases, kind, property.Value.ToString());
                AddSnapshotAliases(aliases, property.Value, propertyNames, kind);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) AddSnapshotAliases(aliases, item, propertyNames, kind);
        }
    }

    private static void AddAlias(HashSet<string> aliases, string kind, string? value)
    {
        var normalized = value?.Trim();
        if (!string.IsNullOrWhiteSpace(normalized)) aliases.Add($"{kind}:{normalized}");
    }
}
