namespace MarketplaceHub.Domain;

// A marketplace's promotion offer as the seller uploaded it (Trendyol's "Plus Komisyon Tarifesi", "Avantajlı Ürün
// Etiketi" and "Flaş Ürünler" exports): the latest file of each kind per connection, its rows kept as JSON. Nothing is
// sent to the marketplace; the panel only works out what each offer leaves after commission and cost.
public sealed class PromotionOfferFile
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ConnectionId { get; set; }
    public required string PlatformCode { get; set; }
    // PromotionOfferKinds: PLUS, ADVANTAGE or FLASH.
    public required string Kind { get; set; }
    public required string FileName { get; set; }
    public required string FileHash { get; set; }
    public string? PeriodText { get; set; }
    public DateTimeOffset UploadedAt { get; set; }
    public Guid? UploadedBy { get; set; }
    public int RowCount { get; set; }
    public required string RowsJson { get; set; }
}

public static class PromotionOfferKinds
{
    public const string Plus = "PLUS";
    public const string Advantage = "ADVANTAGE";
    public const string Flash = "FLASH";
    public static readonly IReadOnlyList<string> All = [Plus, Advantage, Flash];
}

// One product of a Plus offer: its price and commission now, and the Plus price ceiling with the commission Trendyol
// offers at it for the period.
public sealed record PlusOfferRow(string Barcode, string? SellerSku, string? ModelCode, string? ProductName, string? Size, decimal? Stock,
    decimal? SalePrice, decimal? BasisPrice, decimal? CurrentRate, decimal? PlusPriceLimit, decimal? PlusRate, string? Promotions, string? Category = null, string? Brand = null);

// One product of the advantage label offer: the price ceiling of each star (1 to 3) and the price the customer sees now.
public sealed record AdvantageOfferRow(string Barcode, string? ModelCode, string? ProductName, string? Size, decimal? Stock, bool? InTariff,
    decimal? Star1Max, decimal? Star1Min, decimal? Star2Max, decimal? Star2Min, decimal? Star3Max, decimal? CustomerPrice, decimal? SalePrice, string? Promotions,
    string? Category = null, string? Brand = null);

// One model (colour) of the flash offer: its barcodes, its price and commission now, and the 24-hour and 3-hour flash
// prices with their times (Istanbul local time as the file writes it, "26/10/2026 00:00").
public sealed record FlashOfferRow(string? ModelCode, IReadOnlyList<string> Barcodes, string? ProductName, decimal? Stock, decimal? CurrentPrice, decimal? CustomerPrice,
    decimal? CurrentRate, decimal? Price24h, string? Start24h, string? End24h, decimal? Price3h, string? Start3h, string? End3h, bool? InTariff, bool? Campaign, string? Promotions,
    string? Category = null, string? Brand = null);
