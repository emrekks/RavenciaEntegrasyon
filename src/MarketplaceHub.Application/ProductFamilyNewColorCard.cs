using System.Globalization;
using System.Text;

namespace MarketplaceHub.Application;

// A colour added in the product editor to a model family of several colour cards gets its own card in the same save
// (ProductFamilyVariantPlacement): the opened card's product fields, its title with the opened card's colour word
// swapped for the new colour (live MZ028IH: "Kadın Siyah V Yaka Düğmeli Anne Triko Hırka" → "Kadın Zümrüt V Yaka
// Düğmeli Anne Triko Hırka"), and no Trendyol Web Color override (the opened card's would name another colour).
public static class ProductFamilyNewColorCard
{
    // The opened card's colour word, as a whole word (case, accents and the Turkish dotted/dotless i aside), becomes the
    // new colour; a title without it gets the new colour appended, so two cards of the family never share one title.
    public static string Title(string openedTitle, string? openedColor, string newColor)
    {
        var title = openedTitle.Trim();
        var color = newColor.Trim();
        var foldedTitle = Fold(title);
        var opened = Fold(openedColor?.Trim() ?? "");
        if (opened.Length > 0)
        {
            var search = 0;
            while (search <= foldedTitle.Length - opened.Length)
            {
                var index = foldedTitle.IndexOf(opened, search, StringComparison.Ordinal);
                if (index < 0) break;
                var end = index + opened.Length;
                if ((index == 0 || !char.IsLetterOrDigit(title[index - 1])) && (end == title.Length || !char.IsLetterOrDigit(title[end])))
                    return string.Concat(title.AsSpan(0, index), color, title.AsSpan(end));
                search = index + 1;
            }
        }
        return foldedTitle.Contains(Fold(color), StringComparison.Ordinal) ? title : $"{title} {color}";
    }

    // One folded character per character, so an index in the folded text is the same index in the original.
    private static string Fold(string value)
    {
        var folded = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (character is 'ı' or 'i' or 'İ' or 'I') { folded.Append('I'); continue; }
            var decomposed = character.ToString().Normalize(NormalizationForm.FormD);
            folded.Append(char.ToUpperInvariant(decomposed[0]));
        }
        return folded.ToString();
    }
}
