namespace MarketplaceHub.Application;

// The product editor's eleven required fields (owner, 10–11 Nov 2026; web required-fields.ts), held by the server for
// every card that is not a draft: a draft may be incomplete and is never sent, a card on sale or closed may not be. Ürün
// Adı, Marka, Panel Kategorisi, Model Kodu, Stok Kodu and Barkod (on every variant row), Desi (above 0 on every row),
// Garanti Süresi (0 = no warranty; empty is missing), Teslimat Süresi, Liste Fiyatı and Satış Fiyatı (above 0, a row's
// own price else the card's). The messages are the editor's. Garanti Süresi can only be told missing from what a save
// sends (the card keeps 0 and empty alike as no warranty), so a stored card counts it as given.
public static class ProductRequiredFields
{
    public const string Code = "PRODUCT_REQUIRED_FIELDS_MISSING";

    public const string Title = "title";
    public const string Brand = "brandId";
    public const string Category = "categoryId";
    public const string ModelCode = "modelCode";
    public const string Sku = "baseSku";
    public const string Barcode = "barcode";
    public const string Desi = "desi";
    public const string Warranty = "warrantyMonths";
    public const string DeliveryDays = "deliveryDays";
    public const string ListPrice = "listPrice";
    public const string SalePrice = "salePrice";

    // In the order the editor shows them.
    private static readonly (string Field, string Label)[] Fields =
    [
        (Title, "Ürün Adı"), (Brand, "Marka"), (Category, "Panel Kategorisi"), (ModelCode, "Model Kodu"), (Sku, "Stok Kodu"),
        (Barcode, "Barkod"), (Desi, "Desi"), (Warranty, "Garanti Süresi"), (DeliveryDays, "Teslimat Süresi"),
        (ListPrice, "Liste Fiyatı"), (SalePrice, "Satış Fiyatı")
    ];

    public static string Label(string field) => Fields.First(item => item.Field == field).Label;

    // One variant row as the card would hold it after the save. Prices are the row's own; null falls back to the card's.
    public sealed record Row(string? Sku, string? Barcode, string? ModelCode, decimal? Desi, decimal? ListPrice, decimal? SalePrice);

    public sealed record Card(string? Title, Guid? BrandId, Guid? CategoryId, bool WarrantyGiven, bool DeliveryDaysGiven, decimal? ListPrice, decimal? SalePrice, IReadOnlyList<Row> Rows);

    // A required field the card misses; NotPositive: a price that is there but not above 0.
    public sealed record Gap(string Field, bool NotPositive = false)
    {
        public string Label => ProductRequiredFields.Label(Field);
        public string Message => NotPositive ? $"{Label} 0'dan büyük olmalı." : $"{Label} zorunludur.";
    }

    public static IReadOnlyList<Gap> Missing(Card card)
    {
        var gaps = new Dictionary<string, Gap>(StringComparer.Ordinal);
        void Add(string field, bool notPositive = false)
        {
            // A missing value outranks one that is only not above 0.
            if (!gaps.TryGetValue(field, out var known) || known.NotPositive && !notPositive) gaps[field] = new Gap(field, notPositive);
        }
        void Price(string field, decimal? value)
        {
            if (value is not decimal amount) Add(field);
            else if (amount <= 0) Add(field, notPositive: true);
        }

        if (string.IsNullOrWhiteSpace(card.Title)) Add(Title);
        if (card.BrandId is null || card.BrandId == Guid.Empty) Add(Brand);
        if (card.CategoryId is null || card.CategoryId == Guid.Empty) Add(Category);
        if (card.Rows.Count == 0) { Add(ModelCode); Add(Sku); Add(Barcode); Add(Desi); }
        foreach (var row in card.Rows)
        {
            if (string.IsNullOrWhiteSpace(row.ModelCode)) Add(ModelCode);
            if (string.IsNullOrWhiteSpace(row.Sku)) Add(Sku);
            if (string.IsNullOrWhiteSpace(row.Barcode)) Add(Barcode);
            if (row.Desi is not > 0) Add(Desi);
        }
        if (!card.WarrantyGiven) Add(Warranty);
        if (!card.DeliveryDaysGiven) Add(DeliveryDays);
        if (card.Rows.Count == 0)
        {
            Price(ListPrice, card.ListPrice);
            Price(SalePrice, card.SalePrice);
        }
        foreach (var row in card.Rows)
        {
            Price(ListPrice, row.ListPrice ?? card.ListPrice);
            Price(SalePrice, row.SalePrice ?? card.SalePrice);
        }
        return [.. Fields.Where(item => gaps.ContainsKey(item.Field)).Select(item => gaps[item.Field])];
    }

    // "Zorunlu alanları doldurun: Marka, Desi." — the editor's line above the form.
    public static string Message(IReadOnlyList<Gap> gaps) => $"Zorunlu alanları doldurun: {Labels(gaps)}.";

    public static string Labels(IReadOnlyList<Gap> gaps) => string.Join(", ", gaps.Select(gap => gap.Label));

    // The message under each field, keyed by the editor's field names.
    public static Dictionary<string, string[]> FieldErrors(IReadOnlyList<Gap> gaps) =>
        gaps.ToDictionary(gap => gap.Field, gap => new[] { gap.Message }, StringComparer.Ordinal);

    public static ServiceResult<T> Refusal<T>(IReadOnlyList<Gap> gaps) => ServiceResult<T>.Fail(Code, Message(gaps), 422, FieldErrors(gaps));

    // What a file or bulk write may not do to a card that is not a draft: leave a required field empty.
    public static string CannotEmpty(string field) => $"{Label(field)} zorunlu alandır; taslak olmayan üründe silinemez.";
}
