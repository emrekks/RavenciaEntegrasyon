using System.Globalization;

namespace MarketplaceHub.Application;

// Ürünler › Toplu İşlemler › Toplu Değer Ata (owner, 12.11.2026): the fields one bulk run may set and the value checks,
// the same as the product editor's. Model-row fields go to every colour card of the selected models, desi to every
// variant, güvenlik stoğu to every marketplace offer of every variant (where the editor keeps it).
public static class BulkProductFields
{
    public const string VatRate = "vatRate";
    public const string VatInclusion = "vatInclusion";
    public const string Desi = "desi";
    public const string WarrantyMonths = "warrantyMonths";
    public const string DeliveryDays = "deliveryDays";
    public const string Status = "status";
    public const string Brand = "brandId";
    public const string Category = "categoryId";
    public const string SafetyStock = "safetyStock";
    public const string Description = "description";

    public static readonly IReadOnlyList<string> All = [VatRate, VatInclusion, Desi, WarrantyMonths, DeliveryDays, Status, Brand, Category, SafetyStock, Description];

    // The rates the editor offers (TURKISH_VAT_RATES).
    public static readonly IReadOnlyList<decimal> VatRates = [0m, 1m, 8m, 10m, 20m];
    public const int MaxWarrantyMonths = 240;
    public const decimal MaxDesi = ProductDesi.Maximum;
    public const int MaxSafetyStock = 100_000;
    public const int MaxDescriptionLength = 60_000;

    public static string Label(string field) => field switch
    {
        VatRate => "KDV Oranı",
        VatInclusion => "KDV Dahil mi",
        Desi => "Desi",
        WarrantyMonths => "Garanti Süresi (Ay)",
        DeliveryDays => "Teslimat Süresi (Gün)",
        Status => "Satış Durumu",
        Brand => "Marka",
        Category => "Kategori",
        SafetyStock => "Güvenlik Stoğu",
        Description => "Açıklama",
        _ => field
    };

    // A checked value: Number for KDV Oranı and desi, Integer for garanti, teslimat and güvenlik stoğu, Id for marka and
    // kategori, Text for KDV Dahil mi (INCLUDED/EXCLUDED), Satış Durumu (ACTIVE/ARCHIVED) and the description.
    public sealed record Value(string Field, decimal? Number = null, int? Integer = null, Guid? Id = null, string? Text = null)
    {
        // The value as the activity log names it.
        public string Display => Field switch
        {
            VatRate => $"%{Number!.Value.ToString("0.##", CultureInfo.InvariantCulture)}",
            VatInclusion => Text == "INCLUDED" ? "Evet" : "Hayır",
            Desi => Number!.Value.ToString("0.##", CultureInfo.GetCultureInfo("tr-TR")),
            Status => Text == "ACTIVE" ? "Satışa Açık" : "Satışa Kapalı",
            Description => $"{Text!.Length.ToString(CultureInfo.InvariantCulture)} karakter",
            Brand or Category => Id!.Value.ToString("D"),
            _ => Integer!.Value.ToString(CultureInfo.InvariantCulture)
        };
    }

    public sealed record ParseResult(Value? Value, string? ErrorField, string? Error)
    {
        public static ParseResult Ok(Value value) => new(value, null, null);
        public static ParseResult Fail(string field, string message) => new(null, field, message);
    }

    public static ParseResult Parse(string? field, string? value)
    {
        var name = All.FirstOrDefault(known => string.Equals(known, field?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (name is null) return ParseResult.Fail("field", "Atanacak alan geçersiz.");
        var text = value?.Trim() ?? "";
        switch (name)
        {
            case VatRate:
                return TryNumber(text, out var rate) && VatRates.Contains(rate)
                    ? ParseResult.Ok(new(name, Number: rate))
                    : ParseResult.Fail("value", "KDV oranı %0, %1, %8, %10 veya %20 olmalıdır.");
            case VatInclusion:
                return text.ToUpperInvariant() is "INCLUDED" or "EXCLUDED"
                    ? ParseResult.Ok(new(name, Text: text.ToUpperInvariant()))
                    : ParseResult.Fail("value", "KDV dahil bilgisi Evet veya Hayır olmalıdır.");
            case Desi:
                return TryNumber(text, out var desi) && desi > 0 && desi <= MaxDesi && decimal.Round(desi, 2) == desi
                    ? ParseResult.Ok(new(name, Number: desi))
                    : ParseResult.Fail("value", $"Desi sıfırdan büyük ve en fazla {ProductDesi.MaximumText} olmalıdır (en çok iki ondalık, ör. 0,5).");
            case WarrantyMonths:
                return TryInteger(text, out var months) && months is >= 0 and <= MaxWarrantyMonths
                    ? ParseResult.Ok(new(name, Integer: months))
                    : ParseResult.Fail("value", "Garanti süresi 0-240 ay arasında tam sayı olmalıdır.");
            case DeliveryDays:
                return TryInteger(text, out var days) && ProductDeliveryDays.IsValid(days)
                    ? ParseResult.Ok(new(name, Integer: days))
                    : ParseResult.Fail("value", ProductDeliveryDays.InvalidMessage);
            case SafetyStock:
                return TryInteger(text, out var safety) && safety is >= 0 and <= MaxSafetyStock
                    ? ParseResult.Ok(new(name, Integer: safety))
                    : ParseResult.Fail("value", "Güvenlik stoğu 0 veya daha büyük bir tam sayı olmalıdır.");
            case Status:
                return text.ToUpperInvariant() is "ACTIVE" or "ARCHIVED"
                    ? ParseResult.Ok(new(name, Text: text.ToUpperInvariant()))
                    : ParseResult.Fail("value", "Satış durumu Satışa Açık veya Satışa Kapalı olmalıdır.");
            case Brand:
                return Guid.TryParse(text, out var brandId) && brandId != Guid.Empty
                    ? ParseResult.Ok(new(name, Id: brandId))
                    : ParseResult.Fail("value", "Marka seçin.");
            case Category:
                return Guid.TryParse(text, out var categoryId) && categoryId != Guid.Empty
                    ? ParseResult.Ok(new(name, Id: categoryId))
                    : ParseResult.Fail("value", "Kategori seçin.");
            default:
                if (text.Length > MaxDescriptionLength) return ParseResult.Fail("value", $"Açıklama en fazla {MaxDescriptionLength.ToString("N0", CultureInfo.GetCultureInfo("tr-TR"))} karakter olabilir.");
                return PlainText(text).Length == 0
                    ? ParseResult.Fail("value", "Açıklama boş olamaz.")
                    : ParseResult.Ok(new(name, Text: text));
        }
    }

    // A decimal comma (0,5) reads as 0.5, as the editor's fields do.
    private static bool TryNumber(string text, out decimal number) =>
        decimal.TryParse(text.Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out number);

    private static bool TryInteger(string text, out int number) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out number);

    private static string PlainText(string html) =>
        System.Text.RegularExpressions.Regex.Replace(html, "<[^>]*>", "").Replace("&nbsp;", " ", StringComparison.OrdinalIgnoreCase).Trim();
}
