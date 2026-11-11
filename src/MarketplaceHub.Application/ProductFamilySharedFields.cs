namespace MarketplaceHub.Application;

// A model (same brand and model code) is one product in the editor whichever colour row was opened (owner, 11.11.2026):
// its model-row fields (Sopyo's main row: name, description, category, brand, VAT, warranty, delivery days and the
// card's category attributes) are the model's, while each colour card keeps its colour, its variants' codes, stock,
// prices and pictures. A field the owner changed in the editor is written to every colour card of the model (a card
// already holding the value is left alone); a field the owner did not touch stays as each card has it, and the editor
// names the colours that differ instead of silently picking one.
public static class ProductFamilySharedFields
{
    public const string Title = "title";
    public const string Description = "description";
    public const string Brand = "brand";
    public const string Category = "category";
    public const string Vat = "vat";
    public const string Warranty = "warranty";
    public const string DeliveryDays = "deliveryDays";
    public const string Attributes = "attributes";
    // Desi lives on the variants: the editor sends a changed desi to every size of every colour (shown, not written here).
    public const string Desi = "desi";

    public static readonly IReadOnlyList<string> All = [Title, Description, Brand, Category, Vat, Warranty, DeliveryDays, Attributes];

    public static bool IsKnown(string field) => All.Contains(field, StringComparer.Ordinal);

    // A colour card's name from the model's name: when the edited name holds the opened card's colour word, each card
    // gets its own colour in that place (Kadın Bordo … → Kadın Siyah …); otherwise every card takes the name as it is.
    public static string TitleFor(string editedTitle, string? openedColor, string? cardColor)
    {
        var title = editedTitle.Trim();
        if (string.IsNullOrWhiteSpace(cardColor) || !ProductFamilyNewColorCard.HasColorWord(title, openedColor)) return title;
        return ProductFamilyNewColorCard.Title(title, openedColor, cardColor);
    }

    public sealed record CardFields(Guid ProductId, string? Color, string Title, string Description, Guid? BrandId, Guid? CategoryId, decimal? VatRate, string? VatInclusion, int? WarrantyMonths, int? DeliveryDays, IReadOnlyList<decimal?>? Desis = null);

    // The model-row fields the colour cards of a model (given in the family's card order) do not all share, with the
    // colours that differ from the value most cards hold (on a tie, the first card's). The same whichever card was opened.
    // A name that differs only by the card's own colour word is the same name.
    // brandCards: the cards of the same model code under any brand (KENE01 on live: one colour on Lavian, one on
    // Ravencia); the brand is checked over them, the other fields over the model.
    public static IReadOnlyList<ProductFamilyFieldDifferenceView> Differences(IReadOnlyList<CardFields> cards, IReadOnlyList<CardFields>? brandCards = null)
    {
        var result = new List<ProductFamilyFieldDifferenceView>();
        void Check(string field, Func<CardFields, string> keyOf, IReadOnlyList<CardFields>? over = null)
        {
            var checkedCards = over ?? cards;
            if (checkedCards.Count < 2) return;
            var keys = checkedCards.Select(keyOf).ToList();
            var common = keys.Select((key, index) => (key, index)).GroupBy(item => item.key, StringComparer.Ordinal)
                .OrderByDescending(group => group.Count()).ThenBy(group => group.Min(item => item.index)).First().Key;
            var colors = checkedCards.Where((_, index) => !string.Equals(keys[index], common, StringComparison.Ordinal))
                .Select(card => string.IsNullOrWhiteSpace(card.Color) ? card.Title.Trim() : card.Color.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (colors.Count > 0) result.Add(new ProductFamilyFieldDifferenceView(field, colors));
        }
        Check(Title, card => ProductFamilyNewColorCard.HasColorWord(card.Title.Trim(), card.Color) ? ProductFamilyNewColorCard.Title(card.Title.Trim(), card.Color, "\u0001") : card.Title.Trim());
        Check(Description, card => card.Description.Trim());
        Check(Brand, card => $"{card.BrandId}", brandCards is { Count: > 0 } ? brandCards : null);
        Check(Category, card => $"{card.CategoryId}");
        Check(Vat, card => $"{card.VatRate?.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)}|{card.VatInclusion?.ToUpperInvariant()}");
        Check(Warranty, card => $"{card.WarrantyMonths}");
        Check(DeliveryDays, card => $"{card.DeliveryDays}");
        // A card whose sizes hold different desis (or none) is a value of its own; an empty desi is a value too.
        Check(Desi, card => string.Join("|", (card.Desis ?? []).Select(desi => desi is decimal value && value > 0 ? value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) : "-").Distinct().Order(StringComparer.Ordinal)));
        return result;
    }
}

public sealed record ProductFamilyFieldDifferenceView(string Field, IReadOnlyList<string> Colors);
