using MarketplaceHub.Infrastructure.Adapters.Hepsiburada;
using MarketplaceHub.Infrastructure.Adapters.N11;
using MarketplaceHub.Infrastructure.Adapters.Pazarama;
using MarketplaceHub.Infrastructure.Adapters.PttAvm;
using MarketplaceHub.Infrastructure.Adapters.Trendyol;

namespace MarketplaceHub.Infrastructure.Jobs;

// Queue phase 0 (27 Nov 2026): what a shared quota bucket key is (marketplace, bucket, the panel's name for it) and the limit
// the code keeps for it per minute, read from the same constants and options the limiters use. A key the code does not know
// is shown by its own name with no limit; nothing is invented. Shopify has no shared bucket (it answers THROTTLED itself),
// so no Shopify key exists.
public sealed class QuotaBucketCatalog(TrendyolOptions trendyol, N11Options n11, PazaramaOptions pazarama, PttAvmOptions pttAvm)
{
    public sealed record Bucket(string PlatformCode, string Code, string Label, double? LimitPerMinute, bool Shared = false, bool BaseLimit = false);

    public Bucket? Describe(string key)
    {
        var parts = key.Split(':');
        if (parts.Length < 2) return null;
        return parts[0] switch
        {
            "trendyol" => Trendyol(key, parts),
            "hepsiburada" => Hepsiburada(parts),
            "n11" when parts.Length >= 3 => N11(parts[^1]),
            "pazarama" when parts.Length >= 3 => Pazarama(parts[^1]),
            "pttavm" when parts.Length >= 3 => PttAvm(parts[^1]),
            _ => null
        };
    }

    private static double PerMinute(int limit, TimeSpan interval) => interval <= TimeSpan.Zero ? limit : Math.Round(limit * 60 / interval.TotalSeconds, 1);
    private static double? Spacing(TimeSpan spacing) => spacing <= TimeSpan.Zero ? null : Math.Round(60 / spacing.TotalSeconds, 1);

    // trendyol:orders:{host}:{seller}:fg|bg|spacing|spacing:start, trendyol:finance:{host}:{seller},
    // trendyol:claim-actions:{host}:{seller}, trendyol:global:{host}:{User-Agent}:{endpoint family}.
    private Bucket? Trendyol(string key, string[] parts)
    {
        switch (parts[1])
        {
            case "orders" when parts.Length >= 5:
                {
                    // The order quota the code starts from (TrendyolOptions.OrderRequestsPerInterval, split by the foreground
                    // share); the worker may learn a higher tier from Trendyol's answers, which the api does not know.
                    var (foreground, background) = TrendyolResilienceHandler.OrderQuotaSplit(trendyol);
                    var tail = string.Join(':', parts[4..]);
                    return tail switch
                    {
                        // BaseLimit: the panel labels the number "Taban Sınır" (queue phase 1A, item 6).
                        "fg" => new("TRENDYOL", "TRENDYOL:ORDERS_FG", "Sipariş Okuma · Ön Plan", PerMinute(foreground, trendyol.OrderRequestInterval), BaseLimit: true),
                        "bg" => new("TRENDYOL", "TRENDYOL:ORDERS_BG", "Sipariş Okuma · Arka Plan", PerMinute(background, trendyol.OrderRequestInterval), BaseLimit: true),
                        "spacing" => new("TRENDYOL", "TRENDYOL:ORDER_PAGE_SPACING", "Sipariş Akışı Sayfa Aralığı", Spacing(trendyol.OrderRequestMinimumInterval)),
                        "spacing:start" => new("TRENDYOL", "TRENDYOL:ORDER_QUERY_SPACING", "Sipariş Akışı Sorgu Aralığı", Spacing(trendyol.OrderStreamStartMinimumInterval)),
                        _ => new("TRENDYOL", $"TRENDYOL:ORDERS:{tail.ToUpperInvariant()}", $"Sipariş · {tail}", null)
                    };
                }
            case "finance":
                return new("TRENDYOL", "TRENDYOL:FINANCE", "Finans", PerMinute(Math.Max(1, trendyol.FinanceRequestsPerInterval), GroupInterval));
            case "claim-actions":
                return new("TRENDYOL", "TRENDYOL:CLAIM_ACTIONS", "İade Onay ve Ret", PerMinute(Math.Max(1, trendyol.ClaimActionRequestsPerInterval), GroupInterval));
            case "global" when parts.Length >= 5:
                {
                    // trendyol:global:{host}:{User-Agent}:{family}; the family may hold colons ("order:claims").
                    var family = string.Join(':', parts[4..]);
                    return new("TRENDYOL", $"TRENDYOL:GLOBAL:{family.ToUpperInvariant()}", $"Genel Limit · {family}", PerMinute(trendyol.RequestsPerInterval, trendyol.RequestInterval));
                }
            default:
                return new("TRENDYOL", $"TRENDYOL:{key[(parts[0].Length + 1)..].ToUpperInvariant()}", key[(parts[0].Length + 1)..], null);
        }
    }

    private TimeSpan GroupInterval => trendyol.GroupRequestInterval <= TimeSpan.Zero ? TimeSpan.FromMinutes(1) : trendyol.GroupRequestInterval;

    // hepsiburada:{account}:{oms|listing|asktoseller|finance}, hepsiburada:egress:catalog (one per server IP, every account).
    private static Bucket Hepsiburada(string[] parts)
    {
        var name = parts[^1];
        var shared = parts.Length >= 3 && parts[1] == HepsiburadaRateLimitHandler.EgressScope;
        int? limit = HepsiburadaRateLimitHandler.LimitPerMinute(name);
        var label = name switch
        {
            "oms" => "Sipariş (OMS)",
            "listing" => "Listeleme · Fiyat ve Stok",
            "asktoseller" => "Müşteri Soruları",
            "finance" => "Finans",
            HepsiburadaRateLimitHandler.CatalogBucket => "Katalog · Sunucu IP'si Ortak",
            _ => name
        };
        return new("HEPSIBURADA", $"HEPSIBURADA:{name.ToUpperInvariant()}", label, limit, shared);
    }

    private Bucket N11(string name) => new("N11", $"N11:{name.ToUpperInvariant()}", name switch
    {
        "orders" => "Sipariş",
        "products" => "Ürün",
        "tasks" => "Ürün Görevleri",
        "categories" => "Kategori",
        "questions-list" => "Soru Listesi",
        "soap" => "SOAP Servisleri",
        _ => name
    }, name is "orders" or "products" or "tasks" or "categories" or "questions-list" or "soap" ? N11RateLimitHandler.LimitPerMinute(n11, name) : null);

    private Bucket Pazarama(string name) => name switch
    {
        "orders" => new("PAZARAMA", "PAZARAMA:ORDERS", "Sipariş", pazarama.OrderRequestsPerMinute),
        "reads" => new("PAZARAMA", "PAZARAMA:READS", "Okuma", pazarama.ReadRequestsPerMinute),
        "price-stock" => new("PAZARAMA", "PAZARAMA:PRICE_STOCK", "Fiyat ve Stok Yazma", Spacing(pazarama.PriceStockSpacing)),
        "product-create" => new("PAZARAMA", "PAZARAMA:PRODUCT_CREATE", "Ürün Oluşturma", Spacing(pazarama.ProductCreateSpacing)),
        _ => new("PAZARAMA", $"PAZARAMA:{name.ToUpperInvariant()}", name, null)
    };

    private Bucket PttAvm(string name) => name switch
    {
        "orders" => new("PTTAVM", "PTTAVM:ORDERS", "Sipariş", pttAvm.OrderRequestsPerMinute),
        "reads" => new("PTTAVM", "PTTAVM:READS", "Okuma", pttAvm.ReadRequestsPerMinute),
        "product-write" => new("PTTAVM", "PTTAVM:PRODUCT_WRITE", "Ürün ve Stok Yazma", Spacing(pttAvm.ProductWriteSpacing)),
        _ => new("PTTAVM", $"PTTAVM:{name.ToUpperInvariant()}", name, null)
    };
}
