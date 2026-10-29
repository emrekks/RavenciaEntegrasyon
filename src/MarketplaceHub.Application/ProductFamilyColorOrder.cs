using System.Globalization;

namespace MarketplaceHub.Application;

// The order of a model family's colour cards as the panel shows it: the product list's variant groups, the quick edit
// and the product editor's variant rows all list colours alphabetically (Turkish, case and accents aside), and the
// editor saves that row order as the family-wide variant SortOrder (live 2026-10-29, MZ035YNC: SortOrder 0..23 runs
// Açık Kahverengi, Bordo, Gri, Haki, Kahverengi, Lacivert, Mürdüm, Siyah). A card is placed by its colour; a card
// without a colour option comes after the coloured ones; the lowest SortOrder of its variants, then its id, settles
// what the colour leaves equal.
public static class ProductFamilyColorOrder
{
    private static readonly CompareInfo Turkish = CultureInfo.GetCultureInfo("tr-TR").CompareInfo;
    private const CompareOptions BaseLetters = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;

    public sealed record Card(Guid ProductId, string? Color, int SortOrder);

    public sealed record VariantRow(string? OptionSignature, int SortOrder);

    // A card from its variants: the colour option of its first variant that has one (a "Renk" option before a web
    // colour, as the panel picks it) and its lowest SortOrder.
    public static Card ForCard(Guid productId, IEnumerable<VariantRow> variants)
    {
        var rows = variants.ToList();
        var color = rows.OrderBy(row => row.SortOrder).Select(row => ColorOf(row.OptionSignature)).FirstOrDefault(value => value is not null);
        return new(productId, color, rows.Count == 0 ? int.MaxValue : rows.Min(row => row.SortOrder));
    }

    public static string? ColorOf(string? optionSignature)
    {
        var options = VariantOptionSignature.Parse(optionSignature);
        var color = options.FirstOrDefault(option => VariantOptionSignature.IsColorName(option.Key) && !VariantOptionSignature.IsWebColorName(option.Key)).Value
            ?? options.FirstOrDefault(option => VariantOptionSignature.IsWebColorName(option.Key)).Value;
        return string.IsNullOrWhiteSpace(color) ? null : color.Trim();
    }

    public static int Compare(Card left, Card right)
    {
        var leftColored = left.Color is not null;
        var rightColored = right.Color is not null;
        if (leftColored != rightColored) return leftColored ? -1 : 1;
        if (leftColored)
        {
            var byColor = Turkish.Compare(left.Color!.Trim(), right.Color!.Trim(), BaseLetters);
            if (byColor != 0) return byColor;
        }
        var bySortOrder = left.SortOrder.CompareTo(right.SortOrder);
        return bySortOrder != 0 ? bySortOrder : left.ProductId.CompareTo(right.ProductId);
    }

    public static IReadOnlyList<Card> Order(IEnumerable<Card> cards)
    {
        var ordered = cards.ToList();
        ordered.Sort(Compare);
        return ordered;
    }
}
