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
// - no card holds it: the opened card when the family is that card alone; otherwise the colour has no card yet
//   (ColorsWithoutCard, and the row's entry in RowColorsWithoutCard): the save opens a new colour card for it in the
//   family (ProductFamilyNewColorCard; live 2026-11-06, MZ028IH: Zümrüt added to a family of nine colour cards was
//   refused), so nothing lands on a card of another colour.
// Rows being deleted in the same save count only when no remaining row holds the colour (a card whose rows are all
// replaced keeps its new ones).
public static class ProductFamilyVariantPlacement
{
    public sealed record ExistingRow(Guid ProductId, string? OptionSignature, bool Deleting = false);

    // ProductIds: the card of each new row (the opened card for a row whose colour has no card yet).
    // RowColorsWithoutCard: per row, the colour (spelled as in ColorsWithoutCard) that still needs its own card, or null.
    public sealed record Placement(IReadOnlyList<Guid> ProductIds, IReadOnlyList<string> ColorsWithoutCard, IReadOnlyList<string?> RowColorsWithoutCard);

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
        var rowMissing = new List<string?>(newRowOptions.Count);
        foreach (var options in newRowOptions)
        {
            var color = ColorOf(options);
            var key = ColorKey(color);
            if (key is null || !familySplitByColor)
            {
                productIds.Add(openedProductId);
                rowMissing.Add(null);
                continue;
            }
            var target = CardFor(openedProductId, rows.Where(row => !row.Deleting).Select(row => (row.ProductId, row.Color)).ToList(), key)
                ?? CardFor(openedProductId, rows.Select(row => (row.ProductId, row.Color)).ToList(), key);
            productIds.Add(target ?? openedProductId);
            if (target is not null || singleCard)
            {
                rowMissing.Add(null);
                continue;
            }
            var spelled = missing.FirstOrDefault(name => CatalogKeyNormalizer.SameKey(name, color));
            if (spelled is null)
            {
                spelled = color!;
                missing.Add(spelled);
            }
            rowMissing.Add(spelled);
        }
        return new(productIds, missing, rowMissing);
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

    // The option set of a row as one comparable key (names and values folded, order aside), so a new row that repeats a
    // row the family already holds is found whatever its spelling ("Renk: SİYAH | Beden: L" = Beden L, Renk siyah).
    public static string? OptionKey(IEnumerable<KeyValuePair<string, string>>? options)
    {
        var pairs = (options ?? [])
            .Select(pair => (Name: CatalogKeyNormalizer.Key(pair.Key, 320).Replace("-", "", StringComparison.Ordinal), Value: CatalogKeyNormalizer.Key(pair.Value, 320)))
            .Where(pair => pair.Name.Length > 0 && pair.Value.Length > 0)
            .Select(pair => $"{pair.Name}={pair.Value}")
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
        return pairs.Count == 0 ? null : string.Join("|", pairs);
    }

    public static string? OptionKey(string? optionSignature) => OptionKey(VariantOptionSignature.Parse(optionSignature));

    public static string DuplicateRowMessage(IReadOnlyList<string> labels) =>
        $"{string.Join(", ", labels)} {(labels.Count == 1 ? "varyantı" : "varyantları")} bu model kodunda zaten var; aynı seçenekler ikinci kez eklenmez. "
        + "Sayfayı yenileyip yalnızca eksik seçenekleri ekleyin.";

    public static string NewCardModelCodeMessage(string? modelCode) =>
        $"Yeni renk kartının varyantları ailenin model kodunu ({modelCode}) taşımalıdır; model kodu farklı satırlar başka bir ürün olur.";
}
