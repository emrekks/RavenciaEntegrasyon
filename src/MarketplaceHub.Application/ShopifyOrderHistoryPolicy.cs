namespace MarketplaceHub.Application;

public static class ShopifyOrderHistoryPolicy
{
    public const int DefaultInitialLookbackMonths = 3;
    public const int DefaultAccessibleOrderDays = 60;

    public static DateTimeOffset InitialWindowStart(DateTimeOffset now) => now.AddMonths(-DefaultInitialLookbackMonths);

    public static DateTimeOffset? ModifiedAfter(
        bool fullScan,
        bool allowBaseline,
        DateTimeOffset? lastModifiedWatermark,
        DateTimeOffset? lastSuccessAt,
        DateTimeOffset now)
    {
        if (fullScan) return null;
        if (allowBaseline) return InitialWindowStart(now);
        return lastModifiedWatermark?.AddMinutes(-10)
            ?? (lastSuccessAt is null ? InitialWindowStart(now) : null);
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
