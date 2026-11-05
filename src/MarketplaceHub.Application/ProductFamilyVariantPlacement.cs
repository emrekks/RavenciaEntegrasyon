namespace MarketplaceHub.Application;

// Which colour card of a model family a variant created in the product editor belongs to. The editor lists every card
// of the family (one card per colour) and sends the new rows of all colours with one save on the opened card; live
// 2026-11-01 (MZ021Y-CD) 18 new rows of six other colours all landed on the opened Mavi card. A new row goes to the
// card that already holds its colour (the Renk option first, then a web colour, compared by the shared key, so
// "Mürdüm", "MURDUM" and "mürdüm" are one colour):
// - no colour on the row, or no colour on any row of the family: the opened card (the family is not split by colour);
// - one card holds the colour: that card;
// - several do (a card that already holds rows of other colours, as the Mavi card above): a card holding only that
//   colour, then the opened card, then the card with most rows of it;
// - no card holds it: the opened card when the family is that card alone; otherwise the colour has no card and the
//   save is refused (the editor has no way to open a new colour card, and a new card may not take a model code another
//   card holds), so nothing lands on a card of another colour.
// Rows being deleted in the same save count only when no remaining row holds the colour (a card whose rows are all
// replaced keeps its new ones).
public static class ProductFamilyVariantPlacement
{
    public sealed record ExistingRow(Guid ProductId, string? OptionSignature, bool Deleting = false);

    public sealed record Placement(IReadOnlyList<Guid> ProductIds, IReadOnlyList<string> ColorsWithoutCard);

    public static string? ColorOf(IReadOnlyDictionary<string, string>? options)
    {
        if (options is null) return null;
        var color = options.FirstOrDefault(pair => VariantOptionSignature.IsColorName(pair.Key) && !VariantOptionSignature.IsWebColorName(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value)).Value
            ?? options.FirstOrDefault(pair => VariantOptionSignature.IsWebColorName(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value)).Value;
        return string.IsNullOrWhiteSpace(color) ? null : color.Trim();
    }

    public static Placement Place(Guid openedProductId, IReadOnlyCollection<Guid> familyProductIds, IEnumerable<ExistingRow> existingRows, IReadOnlyList<IReadOnlyDictionary<string, string>?> newRowOptions)
    {
        var rows = existingRows.Select(row => (row.ProductId, Color: ColorKey(ProductFamilyColorOrder.ColorOf(row.OptionSignature)), row.Deleting)).ToList();
        var familySplitByColor = rows.Any(row => row.Color is not null);
        var singleCard = familyProductIds.Count(productId => productId != openedProductId) == 0;
        var productIds = new List<Guid>(newRowOptions.Count);
        var missing = new List<string>();
        foreach (var options in newRowOptions)
        {
            var color = ColorOf(options);
            var key = ColorKey(color);
            if (key is null || !familySplitByColor)
            {
                productIds.Add(openedProductId);
                continue;
            }
            var target = CardFor(openedProductId, rows.Where(row => !row.Deleting).Select(row => (row.ProductId, row.Color)).ToList(), key)
                ?? CardFor(openedProductId, rows.Select(row => (row.ProductId, row.Color)).ToList(), key);
            if (target is Guid card) productIds.Add(card);
            else
            {
                productIds.Add(openedProductId);
                if (!singleCard && !missing.Any(name => CatalogKeyNormalizer.SameKey(name, color))) missing.Add(color!);
            }
        }
        return new(productIds, missing);
    }

    private static Guid? CardFor(Guid openedProductId, IReadOnlyList<(Guid ProductId, string? Color)> rows, string key)
    {
        var cards = rows.GroupBy(row => row.ProductId)
            .Select(card => (ProductId: card.Key, Matching: card.Count(row => row.Color == key), Pure: card.All(row => row.Color == key)))
            .Where(card => card.Matching > 0)
            .ToList();
        if (cards.Count == 0) return null;
        if (cards.Count == 1) return cards[0].ProductId;
        var pool = cards.Any(card => card.Pure) ? cards.Where(card => card.Pure).ToList() : cards;
        if (pool.Any(card => card.ProductId == openedProductId)) return openedProductId;
        return pool.OrderByDescending(card => card.Matching).ThenBy(card => card.ProductId).First().ProductId;
    }

    private static string? ColorKey(string? color)
    {
        var key = CatalogKeyNormalizer.Key(color, 120);
        return key.Length == 0 ? null : key;
    }

    public static string MissingCardMessage(IReadOnlyList<string> colors) =>
        $"{string.Join(", ", colors)} {(colors.Count == 1 ? "rengi" : "renkleri")} için bu model kodunda renk kartı yok; yeni varyantlar başka renkteki açık karta eklenmez. "
        + $"{(colors.Count == 1 ? "Bu rengin" : "Bu renklerin")} satırlarını kaldırıp yalnızca kartı olan renkler için varyant oluşturun.";
}
