using System.Text.Json;

namespace MarketplaceHub.Application;

// Every code an order line names its product by. The mapper's own stock code and barcode come first; the line's
// marketplace record may carry more (Hepsiburada's merchant SKU next to its own hbSku, Trendyol's stockCode next to its
// barcode, Shopify's variant barcodes), and a seller need not have put the same one in the panel. MarketplaceVariantId
// is the marketplace's id of the listing variant, the id a connection's variant link stores (Hepsiburada's hbSku).
public sealed record OrderLineCodes(string? MarketplaceVariantId, IReadOnlyList<string> Barcodes, IReadOnlyList<string> Skus)
{
    public static readonly OrderLineCodes None = new(null, [], []);

    public bool IsEmpty => MarketplaceVariantId is null && Barcodes.Count == 0 && Skus.Count == 0;

    // The code the seller knows the line by, for the unmatched-line issue.
    public string? DisplayCode => Skus.FirstOrDefault() ?? Barcodes.FirstOrDefault() ?? MarketplaceVariantId;

    public IEnumerable<string> All() =>
        (MarketplaceVariantId is null ? Barcodes.Concat(Skus) : Barcodes.Concat(Skus).Prepend(MarketplaceVariantId))
            .Distinct(StringComparer.Ordinal);
}

// A connection's variant link and a panel variant, as the matcher sees them.
public sealed record OrderLineLinkCandidate(Guid VariantId, string ExternalId, string? ExternalBarcode, string? ExternalSku);
public sealed record OrderLinePanelCandidate(Guid VariantId, string? SkuNormalized, string? BarcodeNormalized);

public enum OrderLineMatchKind { Matched, Ambiguous, NotFound }

public readonly record struct OrderLineVariantMatch(OrderLineMatchKind Kind, Guid? VariantId)
{
    public static readonly OrderLineVariantMatch NotFound = new(OrderLineMatchKind.NotFound, null);
    public static readonly OrderLineVariantMatch Ambiguous = new(OrderLineMatchKind.Ambiguous, null);
}

// Which panel variant an order line sold. The connection's variant links are asked first (a code the link names is
// this marketplace's own binding), then the panel's barcodes and stock codes. Each step either names exactly one
// variant (a match), several (ambiguous: the line stays unmatched rather than take a guess), or none (the next step).
// A code is never stripped of what makes it unique: only leading zeros a marketplace pads a code with ("00KRL01501K04"
// for KRL01501K04 on Hepsiburada) are tried, last, and only while a letter remains.
public static class OrderLineVariantMatcher
{
    public static OrderLineCodes Codes(string? platformCode, string? sku, string? barcode, string? sourceSnapshotJson)
    {
        var barcodes = new List<string>();
        var skus = new List<string>();
        string? marketplaceVariantId = null;
        Add(barcodes, barcode);
        Add(skus, sku);
        if (!string.IsNullOrWhiteSpace(sourceSnapshotJson) && sourceSnapshotJson.TrimStart().StartsWith('{'))
        {
            try
            {
                using var document = JsonDocument.Parse(sourceSnapshotJson);
                var root = document.RootElement;
                switch (platformCode?.Trim().ToUpperInvariant())
                {
                    case "HEPSIBURADA":
                        // "sku" is Hepsiburada's own hbSku (HBCV…), the id its variant links are keyed by.
                        marketplaceVariantId = Text(root, "hbSku") ?? Text(root, "sku");
                        foreach (var name in new[] { "merchantSku", "merchantSKU", "MerchantSku" }) Add(skus, Text(root, name));
                        foreach (var name in new[] { "barcode", "productBarcode" }) Add(barcodes, Text(root, name));
                        break;
                    case "TRENDYOL":
                        Add(barcodes, Text(root, "barcode"));
                        foreach (var name in new[] { "merchantSku", "stockCode", "sku" }) Add(skus, Text(root, name));
                        break;
                    case "N11":
                        Add(barcodes, Text(root, "barcode"));
                        Add(skus, Text(root, "stockCode"));
                        break;
                    case "PAZARAMA":
                        if (root.TryGetProperty("product", out var product) && product.ValueKind == JsonValueKind.Object)
                            foreach (var name in new[] { "code", "stockCode" }) Add(skus, Text(product, name));
                        break;
                    case "PTTAVM":
                        // UrunBarkod is the product family's code, shared by every size and colour: never a variant's.
                        Add(barcodes, Text(root, "VariantBarkod"));
                        break;
                    case "SHOPIFY":
                        Add(skus, Text(root, "sku"));
                        if (root.TryGetProperty("variant", out var variant) && variant.ValueKind == JsonValueKind.Object)
                        {
                            Add(skus, Text(variant, "sku"));
                            if (variant.TryGetProperty("barcodes", out var barcodeList) && barcodeList.ValueKind == JsonValueKind.Object
                                && barcodeList.TryGetProperty("nodes", out var nodes) && nodes.ValueKind == JsonValueKind.Array)
                                foreach (var node in nodes.EnumerateArray())
                                    if (node.ValueKind == JsonValueKind.Object) Add(barcodes, Text(node, "value"));
                        }
                        break;
                }
            }
            catch (JsonException)
            {
                // A record that is not JSON adds no codes; the mapper's own still match.
            }
        }
        return new OrderLineCodes(marketplaceVariantId, barcodes, skus);
    }

    // The codes a connection's variant links are looked up by (trimmed, upper case), the padded forms included.
    public static string[] LinkLookupCodes(IEnumerable<OrderLineCodes> lines) =>
        lines.SelectMany(line => line.All().SelectMany(code => Unpadded(code).Prepend(code)))
            .Select(LinkKey).Where(code => code.Length > 0).Distinct(StringComparer.Ordinal).ToArray();

    // The stored keys panel variants are looked up by (every stored form, CatalogKeyNormalizer.LookupKeys).
    public static string[] PanelLookupKeys(IEnumerable<OrderLineCodes> lines) =>
        lines.SelectMany(line => line.Barcodes.Concat(line.Skus).SelectMany(code => Unpadded(code).Prepend(code)))
            .SelectMany(code => CatalogKeyNormalizer.LookupKeys(code))
            .Distinct(StringComparer.Ordinal).ToArray();

    public static string LinkKey(string? code) => CatalogKeyNormalizer.Legacy(code);

    public static OrderLineVariantMatch Match(OrderLineCodes codes, IReadOnlyCollection<OrderLineLinkCandidate> links, IReadOnlyCollection<OrderLinePanelCandidate> variants)
    {
        if (codes.IsEmpty) return OrderLineVariantMatch.NotFound;
        var barcodes = codes.Barcodes.Select(LinkKey).Where(code => code.Length > 0).ToHashSet(StringComparer.Ordinal);
        var skus = codes.Skus.Select(LinkKey).Where(code => code.Length > 0).ToHashSet(StringComparer.Ordinal);
        var all = barcodes.Concat(skus).ToHashSet(StringComparer.Ordinal);
        var marketplaceId = codes.MarketplaceVariantId is null ? null : LinkKey(codes.MarketplaceVariantId);
        var steps = new IEnumerable<Guid>[]
        {
            // 1. The marketplace's own id of the listing variant (Hepsiburada hbSku).
            marketplaceId is null ? [] : links.Where(link => LinkKey(link.ExternalId) == marketplaceId).Select(link => link.VariantId),
            // 2–4. The link's barcode, then its stock code, then either one or its id under the other code.
            links.Where(link => barcodes.Contains(LinkKey(link.ExternalBarcode))).Select(link => link.VariantId),
            links.Where(link => skus.Contains(LinkKey(link.ExternalSku))).Select(link => link.VariantId),
            links.Where(link => barcodes.Contains(LinkKey(link.ExternalSku)) || skus.Contains(LinkKey(link.ExternalBarcode)) || all.Contains(LinkKey(link.ExternalId))).Select(link => link.VariantId),
            // 5–7. The panel's barcode, then its stock code, then either one under the other code.
            Panel(variants, variant => variant.BarcodeNormalized, codes.Barcodes),
            Panel(variants, variant => variant.SkuNormalized, codes.Skus),
            Panel(variants, variant => variant.SkuNormalized, codes.Barcodes).Concat(Panel(variants, variant => variant.BarcodeNormalized, codes.Skus))
        };
        // 8. A code the marketplace padded with leading zeros, one zero less at a time (so "0000077CY010" finds the
        // seller's own "0077CY010" before a "77CY010"), on the links and then on the panel.
        var lineCodes = codes.Barcodes.Concat(codes.Skus).ToList();
        var maximumPadding = lineCodes.Select(code => Unpadded(code).Count()).DefaultIfEmpty(0).Max();
        var paddedSteps = Enumerable.Range(1, maximumPadding).SelectMany(zeros =>
        {
            var forms = lineCodes.SelectMany(code => Unpadded(code).Skip(zeros - 1).Take(1)).ToList();
            var keys = forms.Select(LinkKey).ToHashSet(StringComparer.Ordinal);
            return new[]
            {
                links.Where(link => keys.Contains(LinkKey(link.ExternalBarcode)) || keys.Contains(LinkKey(link.ExternalSku)) || keys.Contains(LinkKey(link.ExternalId))).Select(link => link.VariantId),
                Panel(variants, variant => variant.BarcodeNormalized, forms).Concat(Panel(variants, variant => variant.SkuNormalized, forms))
            };
        });
        foreach (var step in steps.Concat(paddedSteps))
        {
            var found = step.Distinct().Take(2).ToList();
            if (found.Count == 1) return new OrderLineVariantMatch(OrderLineMatchKind.Matched, found[0]);
            if (found.Count > 1) return OrderLineVariantMatch.Ambiguous;
        }
        return OrderLineVariantMatch.NotFound;
    }

    // The panel variants whose stored key is one of the codes. A stored key may still be in a legacy form ("ABC_01"
    // next to "ABC-01") until the variant is saved again; a row stored under the shared key wins over a legacy row.
    private static IEnumerable<Guid> Panel(IReadOnlyCollection<OrderLinePanelCandidate> variants, Func<OrderLinePanelCandidate, string?> stored, IReadOnlyList<string> codes)
    {
        if (codes.Count == 0) return [];
        var hits = variants.Where(variant => codes.Any(code => CatalogKeyNormalizer.SameKey(code, stored(variant)))).ToList();
        var shared = hits.Where(variant => stored(variant) is { Length: > 0 } key && string.Equals(key, CatalogKeyNormalizer.Key(key), StringComparison.Ordinal)).ToList();
        return (shared.Count > 0 ? shared : hits).Select(variant => variant.VariantId);
    }

    // The forms of a code without the leading zeros a marketplace may have padded it with, longest first; only codes
    // with a letter (a bare number's leading zeros may be its own) and only while a letter remains.
    public static IEnumerable<string> Unpadded(string code)
    {
        var trimmed = code.Trim();
        if (!trimmed.Any(char.IsLetter)) yield break;
        for (var index = 1; index < trimmed.Length && trimmed[index - 1] == '0'; index++)
            yield return trimmed[index..];
    }

    private static void Add(List<string> codes, string? code)
    {
        var trimmed = code?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > 256) return;
        if (!codes.Contains(trimmed, StringComparer.Ordinal)) codes.Add(trimmed);
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null
            }
            : null;
}

// What the panel's stock does when a line finds its variant only after it was imported (the variant or its link
// was created later). The units of an order that had already shipped, been delivered, returned or cancelled by then
// left (or never left) the shelf while the panel did not track them, so the panel's stock count of the variant did
// not hold them: the line adopts what it commits as its stock baseline and takes nothing from stock. A line of an
// order still open takes its units like any new order (OrderInventoryStockPolicy), so a sale awaiting shipment is
// held once the panel can name the variant.
public static class OrderLineLateMatchStockPolicy
{
    public static bool OrderHasSettled(string? derivedStatus) =>
        derivedStatus?.Trim().ToUpperInvariant() is "SHIPPED" or "DELIVERED" or "UNDELIVERED" or "RETURN_IN_TRANSIT" or "RETURNED" or "CANCELLED";

    public static decimal AdoptedBaseline(decimal orderedQuantity, decimal cancelledQuantity, decimal shippedQuantity, decimal deliveredQuantity)
    {
        var ordered = Math.Max(0m, orderedQuantity);
        var shipped = Math.Min(ordered, Math.Max(0m, Math.Max(shippedQuantity, deliveredQuantity)));
        return OrderInventoryStockPolicy.TargetCommittedQuantity(ordered, Math.Max(0m, cancelledQuantity), shipped);
    }
}
