namespace MarketplaceHub.Infrastructure.Adapters.Trendyol;

// C10: Trendyol accepts at most 20,000 units of stock per barcode (price-inventory and product create). The clamped
// value is what goes into the request, so the request hash and the recorded quantity follow what Trendyol receives.
public static class TrendyolStockRules
{
    public const int MaxQuantity = 20_000;

    public static int Quantity(decimal publishable) => (int)Math.Clamp(decimal.Floor(Math.Max(0m, publishable)), 0m, MaxQuantity);

    // stok-ve-fiyat-güncelleme: an identical price-and-inventory request is refused for 15 minutes with
    // "15 dakika boyunca aynı isteği tekrarlı olarak atamazsınız!". Nothing was refused on its merits: the earlier
    // request carries the values. The text may come JSON-escaped (ı), so only its ASCII parts are matched.
    public const string RepeatRequestWaitCode = "TRENDYOL_REPEAT_REQUEST_WAIT";
    public static readonly TimeSpan RepeatRequestWait = TimeSpan.FromMinutes(15);

    public static bool IsRepeatRequestRefusal(string? text) =>
        text is not null && text.Contains("15 dakika boyunca", StringComparison.OrdinalIgnoreCase) && text.Contains("tekrarl", StringComparison.OrdinalIgnoreCase);
}
