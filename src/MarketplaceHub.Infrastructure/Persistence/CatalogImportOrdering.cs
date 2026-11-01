using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Persistence;

internal static class CatalogImportOrdering
{
    // When the marketplace created the product (Trendyol's creationDate, epoch milliseconds). A new panel product takes
    // it as its CreatedAt, so the catalog lists the products last added to the marketplace first, whatever order and
    // however many runs the import took (26 Oct 2026: each finalizing run stamped its own time, older products on top).
    internal static DateTimeOffset? RemoteCreatedAt(RemoteCatalogProduct snapshot, DateTimeOffset now)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(snapshot.RawJson);
            if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object
                || !document.RootElement.TryGetProperty("creationDate", out var value)
                || value.ValueKind != System.Text.Json.JsonValueKind.Number
                || !value.TryGetInt64(out var milliseconds))
                return null;
            var created = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
            return created.Year >= 2000 && created <= now ? created : null;
        }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    internal static string ModelKey(RemoteCatalogProduct snapshot)
    {
        if (!string.IsNullOrWhiteSpace(snapshot.ProductMainId)
            && !string.Equals(snapshot.ProductMainId.Trim(), snapshot.ExternalProductId.Trim(), StringComparison.OrdinalIgnoreCase))
            return snapshot.ProductMainId.Trim();

        var variantIdentity = snapshot.Variants
            .Select(variant => variant.ModelCode ?? variant.Barcode)
            .FirstOrDefault(identity => !string.IsNullOrWhiteSpace(identity));
        return !string.IsNullOrWhiteSpace(variantIdentity)
            && !string.Equals(variantIdentity.Trim(), snapshot.ExternalProductId.Trim(), StringComparison.OrdinalIgnoreCase)
            ? variantIdentity.Trim()
            : snapshot.ExternalProductId.Trim();
    }
}
