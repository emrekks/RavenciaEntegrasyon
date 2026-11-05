using MarketplaceHub.Domain;

namespace MarketplaceHub.Application;

// What a catalog page says about the sale state of one linked variant (ListingSaleState). The link is matched to the
// page's variants by the closest code first: the marketplace's own variant id, then the barcode, then the stock code
// (Trendyol gives several variants one stockCode, so a stock code alone may name more than one). A match that names
// different states is unknown; no match leaves the stored state as it is.
public sealed record ListingSaleStateRead(string? State, string? Reason);

public static class ListingSaleStateReads
{
    public static ListingSaleStateRead? For(string externalId, string? externalBarcode, string? externalSku, IReadOnlyCollection<RemoteCatalogVariant> variants)
    {
        var id = Clean(externalId);
        var barcode = Clean(externalBarcode);
        var sku = Clean(externalSku);
        IReadOnlyList<RemoteCatalogVariant> matches = id is null ? [] : variants.Where(variant => Same(variant.ExternalVariantId, id)).ToList();
        if (matches.Count == 0)
            matches = variants.Where(variant => Clean(variant.Barcode) is { } code && (Same(code, barcode) || Same(code, id))).ToList();
        if (matches.Count == 0)
            matches = variants.Where(variant => Clean(variant.Sku) is { } code && (Same(code, sku) || Same(code, id))).ToList();
        if (matches.Count == 0) return null;
        var reads = matches.Select(variant => new ListingSaleStateRead(variant.SaleState, variant.SaleState == ListingSaleState.OffSale ? variant.SaleStateReason : null)).Distinct().ToList();
        return reads.Count == 1 ? reads[0] : new ListingSaleStateRead(null, null);
    }

    private static string? Clean(string? code) =>
        string.IsNullOrWhiteSpace(code) || MarketplaceVariantLinkHealth.IsListingPlaceholder(code) ? null : code.Trim();

    private static bool Same(string? left, string? right) =>
        left is not null && right is not null && string.Equals(left.Trim(), right, StringComparison.Ordinal);
}
