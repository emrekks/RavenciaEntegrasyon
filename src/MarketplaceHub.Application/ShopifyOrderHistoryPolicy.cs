namespace MarketplaceHub.Application;

public static class ShopifyOrderHistoryPolicy
{
    public const int DefaultInitialLookbackMonths = 3;
    public const int DefaultAccessibleOrderDays = 60;

    public static DateTimeOffset InitialWindowStart(DateTimeOffset now) => now.AddMonths(-DefaultInitialLookbackMonths);

    public static readonly TimeSpan BaselineInterval = TimeSpan.FromDays(1);

    // The recovery run (allowBaseline) re-reads the three-month window once a day
    // (lastBaselineAt); in between it reads only what changed since its watermark.
    public static DateTimeOffset? ModifiedAfter(
        bool fullScan,
        bool allowBaseline,
        DateTimeOffset? lastModifiedWatermark,
        DateTimeOffset? lastSuccessAt,
        DateTimeOffset now,
        DateTimeOffset? lastBaselineAt = null)
    {
        if (fullScan) return null;
        if (allowBaseline && (lastBaselineAt is not { } baseline || now - baseline >= BaselineInterval || lastModifiedWatermark is null))
            return InitialWindowStart(now);
        if (allowBaseline) return lastModifiedWatermark!.Value.AddMinutes(-10);
        return lastModifiedWatermark?.AddMinutes(-10)
            ?? (lastSuccessAt is null ? InitialWindowStart(now) : null);
    }

    // A read that stops between pages resumes with the same window, so Shopify's page cursor is always used
    // with the query it was made for. The window rides in the stored cursor: "w:{fromMs}:{untilMs}:{pages}:{cursor}".
    public sealed record ResumableWindow(DateTimeOffset? From, DateTimeOffset Until, int Pages, string? Cursor);

    // Shopify pages through at most 25,000 results of one query; a read that gets near it starts a new query
    // from the latest change it has seen (the overlap is harmless: orders are upserted).
    public const int PagesBeforeRestart = 200;

    public static string Encode(ResumableWindow window) =>
        $"w:{(window.From is { } from ? from.ToUnixTimeMilliseconds() : -1)}:{window.Until.ToUnixTimeMilliseconds()}:{window.Pages}:{window.Cursor}";

    public static ResumableWindow? Decode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !value.StartsWith("w:", StringComparison.Ordinal)) return null;
        var parts = value.Split(':', 5);
        if (parts.Length != 5 || !long.TryParse(parts[1], out var from) || !long.TryParse(parts[2], out var until) || !int.TryParse(parts[3], out var pages)) return null;
        try
        {
            return new(from < 0 ? null : DateTimeOffset.FromUnixTimeMilliseconds(from), DateTimeOffset.FromUnixTimeMilliseconds(until), pages, parts[4].Length == 0 ? null : parts[4]);
        }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    public static bool RequiresAllOrdersScope(DateTimeOffset orderCreatedAt, DateTimeOffset now) =>
        orderCreatedAt < now.AddDays(-DefaultAccessibleOrderDays);

    public static int CountOrdersRequiringAllOrdersScope(IEnumerable<DateTimeOffset?> orderCreatedAt, DateTimeOffset now) =>
        orderCreatedAt.Count(createdAt => createdAt is { } value && RequiresAllOrdersScope(value, now));

    public static string HistoricalOrdersScopeNotice(int orderCount) =>
        orderCount <= 0
            ? string.Empty
            : $"{orderCount} eşleşmeyen sipariş Shopify'ın varsayılan 60 günlük sipariş aralığından eski. Bu siparişlere erişmek için uygulama tokenında read_all_orders izni bulunmalıdır. İzin yoksa Shopify'dan talep edip onaylatın, izni içeren tokenı bağlantı ayarlarına kaydedin; ardından Sipariş Senkronizasyonu > Erişilebilir tüm siparişleri tara ile tam tarama çalıştırıp CSV'yi yeniden aktarın.";
}
