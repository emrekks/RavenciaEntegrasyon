namespace MarketplaceHub.Application;

// A model (same brand and model code) is one product in the editor whichever colour row was opened (owner, 11.11.2026):
// its model-row fields (Temel Ürün Bilgileri: name, description, category, brand, VAT, warranty, delivery days and the
// card's category attributes) are the model's, while each colour card keeps its colour, its variants' codes, stock,
// prices, marketplace prices and pictures. The fields are one value for the whole model (owner, 14.11.2026: "Bunlar
// ortak değerlerdir, her varyantında eşit olmalı"): the editor shows the model's value (ModelValues) and every save
// writes all of them to every colour card, so cards that drifted apart become equal again.
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

    public sealed record CardFields(Guid ProductId, string? Color, string Title, string Description, Guid? BrandId, Guid? CategoryId, decimal? VatRate, string? VatInclusion, int? WarrantyMonths, int? DeliveryDays);

    public sealed record ModelFields(string Title, string Description, Guid? BrandId, Guid? CategoryId, decimal? VatRate, string? VatInclusion, int? WarrantyMonths, int? DeliveryDays);

    // The value of each field most of the colour cards hold, on a tie the first card's (cards in the family's order,
    // oldest first): the same whichever colour row was opened. A name is compared without the colour word it holds (its
    // own colour, or another colour of the model left there by a copy: live MZ056's Gri card read "Kadın Zümrüt …") and
    // is given with the opened card's colour in that place.
    public static ModelFields ModelValues(IReadOnlyList<CardFields> cards, string? openedColor)
    {
        if (cards.Count == 0) throw new ArgumentException("A model has at least one colour card.", nameof(cards));
        var colors = cards.Select(card => card.Color?.Trim()).OfType<string>().Where(color => color.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        string? ColorWordOf(CardFields card)
        {
            var title = card.Title.Trim();
            return ProductFamilyNewColorCard.HasColorWord(title, card.Color) ? card.Color!.Trim() : colors.FirstOrDefault(color => ProductFamilyNewColorCard.HasColorWord(title, color));
        }
        string TitleKey(CardFields card) => ColorWordOf(card) is { } word ? ProductFamilyNewColorCard.Title(card.Title.Trim(), word, "\u0001") : card.Title.Trim();
        CardFields Most(Func<CardFields, string> keyOf) => cards.Select((card, index) => (card, key: keyOf(card), index))
            .GroupBy(item => item.key, StringComparer.Ordinal)
            .OrderByDescending(group => group.Count()).ThenBy(group => group.Min(item => item.index))
            .First().OrderBy(item => item.index).First().card;

        var titleCard = Most(TitleKey);
        var title = ColorWordOf(titleCard) is { } titleWord && !string.IsNullOrWhiteSpace(openedColor)
            ? ProductFamilyNewColorCard.Title(titleCard.Title.Trim(), titleWord, openedColor.Trim())
            : titleCard.Title.Trim();
        var vatCard = Most(card => $"{card.VatRate?.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)}|{card.VatInclusion?.ToUpperInvariant()}");
        // Garanti Süresi: an empty one (a draft saved with it emptied, 15 Nov 2026) is "not filled", not a value of its
        // own; the model holds the value most filled cards hold, and is empty only while no card holds one.
        var filledWarranty = cards.Where(card => card.WarrantyMonths is not null).ToList();
        var warranty = filledWarranty.Count == 0 ? null : filledWarranty
            .Select((card, index) => (card.WarrantyMonths, index)).GroupBy(item => item.WarrantyMonths)
            .OrderByDescending(group => group.Count()).ThenBy(group => group.Min(item => item.index)).First().Key;
        return new ModelFields(
            title,
            Most(card => card.Description.Trim()).Description,
            Most(card => $"{card.BrandId}").BrandId,
            Most(card => $"{card.CategoryId}").CategoryId,
            vatCard.VatRate, vatCard.VatInclusion,
            warranty,
            Most(card => $"{card.DeliveryDays}").DeliveryDays);
    }

    // KATALOG-14 phase D5b (owner, 17 Nov 2026: "isimleri ilk çekilen gibi yapsın", "KDV ve desi de öyle olsun"): the values
    // a model opened for its colours takes (a save, a marketplace import) are the first card's (the oldest, the first one
    // brought in), field by field; where that card does not know a value (no VAT, category, warranty, delivery days or
    // brand; an empty name or description) the oldest card that knows it gives it, and it stays unknown only when no colour
    // knows it. Nothing is voted on. The other colours take the model's (the name with their colour word, TitleFor). The
    // editor's ModelValues stays as it is.
    public static ModelFields NewModelValues(IReadOnlyList<CardFields> cards)
    {
        if (cards.Count == 0) throw new ArgumentException("A model has at least one colour card.", nameof(cards));
        CardFields? First(Func<CardFields, bool> known) => cards.FirstOrDefault(known);
        var vat = First(card => card.VatRate is not null);
        return new ModelFields(
            (First(card => !string.IsNullOrWhiteSpace(card.Title)) ?? cards[0]).Title.Trim(),
            First(card => !string.IsNullOrWhiteSpace(card.Description))?.Description ?? cards[0].Description,
            First(card => card.BrandId is not null)?.BrandId,
            First(card => card.CategoryId is not null)?.CategoryId,
            vat?.VatRate, vat?.VatInclusion,
            First(card => card.WarrantyMonths is not null)?.WarrantyMonths,
            First(card => card.DeliveryDays is not null)?.DeliveryDays);
    }

    // The card (index in the family's order) whose attribute set most cards hold, on a tie the first: the model's attributes.
    public static int ModelAttributeCard(IReadOnlyList<string> attributeKeys) => attributeKeys.Select((key, index) => (key, index))
        .GroupBy(item => item.key, StringComparer.Ordinal)
        .OrderByDescending(group => group.Count()).ThenBy(group => group.Min(item => item.index))
        .First().Min(item => item.index);
}
