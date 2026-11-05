using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace MarketplaceHub.Infrastructure.Adapters.Hepsiburada;

// Hepsiburada's quotas are shared by the API process and every worker, so each request takes a token from a
// PostgreSQL bucket shared by all of them. The order services allow 1,000 requests a second; the panel keeps far
// below that. Catalog and listing (price and stock) calls follow their own documented minute quotas.
//
// Y3: the catalog (mpop) quota is documented per IP ("180 istek/1 dakika" for each IP, Katalog Ürün Giriş Önemli
// Bilgiler), not per merchant. Every Hepsiburada account the panel serves leaves through the same egress IP (one
// server), so they share one catalog bucket. The other buckets stay per account.
public sealed class HepsiburadaRateLimitHandler(
    TimeProvider timeProvider,
    ISharedRateLimiter? sharedLimiter = null,
    ILogger<HepsiburadaRateLimitHandler>? logger = null) : DelegatingHandler
{
    // Invoked once the request has its quota token, right before it is sent; the caller starts the request
    // timeout there, so time spent waiting for the quota is not counted as a timeout.
    public static readonly HttpRequestOptionsKey<Action> QuotaAcquiredKey = new("MarketplaceHub.Hepsiburada.QuotaAcquired");

    internal const string CatalogBucket = "catalog";
    internal const string EgressScope = "egress";
    private const int CatalogLimitPerMinute = 180;

    // D17: when the shared limiter cannot be reached, a catalog call does not go out unthrottled. This process spaces
    // its catalog calls to a third of the per-IP quota, so the API and the workers together stay under it.
    internal static readonly TimeSpan CatalogFallbackSpacing = TimeSpan.FromMinutes(1) / (CatalogLimitPerMinute / 3);
    private static readonly Lock FallbackLock = new();
    private static DateTimeOffset nextFallbackCatalogSlot = DateTimeOffset.MinValue;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (sharedLimiter is not null && BucketFor(request.RequestUri) is { } bucket)
        {
            var limited = false;
            try
            {
                await SharedRateLimits.WaitAsync(sharedLimiter, QuotaKey(request, bucket.Name), SharedRateLimits.ForWindow(bucket.LimitPerMinute, TimeSpan.FromMinutes(1)), timeProvider, cancellationToken);
                limited = true;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Other buckets: a missing limiter must not stop marketplace traffic; Hepsiburada answers 429 when the
                // quota is really exceeded and the adapter maps that to a retry. The catalog bucket waits below.
                logger?.LogWarning(exception, "Shared Hepsiburada rate limiter unavailable for the {Bucket} bucket", bucket.Name);
            }
            if (!limited && bucket.Name == CatalogBucket)
            {
                var wait = TakeFallbackCatalogSlot(timeProvider.GetUtcNow());
                if (wait > TimeSpan.Zero) await Task.Delay(wait, timeProvider, cancellationToken);
            }
        }
        // The circuit's half-open probe is claimed only now that the token is held (G11).
        if (MarketplaceCircuitBreakerHandler.PassProbeGate(request) is { } refused) return refused;
        if (request.Options.TryGetValue(QuotaAcquiredKey, out var onQuotaAcquired)) onQuotaAcquired();
        return await base.SendAsync(request, cancellationToken);
    }

    // How long this call waits for its place in the in-process catalog spacing (one call per CatalogFallbackSpacing).
    internal static TimeSpan TakeFallbackCatalogSlot(DateTimeOffset now)
    {
        lock (FallbackLock)
        {
            var slot = nextFallbackCatalogSlot > now ? nextFallbackCatalogSlot : now;
            nextFallbackCatalogSlot = slot + CatalogFallbackSpacing;
            return slot - now;
        }
    }

    internal static (string Name, int LimitPerMinute)? BucketFor(Uri? uri)
    {
        var host = uri?.Host ?? "";
        if (host.StartsWith("oms-", StringComparison.OrdinalIgnoreCase)) return ("oms", 600);
        if (host.StartsWith("mpop", StringComparison.OrdinalIgnoreCase)) return (CatalogBucket, CatalogLimitPerMinute);
        if (host.StartsWith("listing-", StringComparison.OrdinalIgnoreCase)) return ("listing", 100);
        // C18: the ask-to-seller (questions) host had no bucket at all. Its quota is not documented, so a
        // conservative 60 a minute is shared by the API and every worker.
        if (host.StartsWith("api-asktoseller", StringComparison.OrdinalIgnoreCase)) return ("asktoseller", 60);
        // The finance (muhasebe) host documents no quota either: the same cautious bucket.
        if (host.StartsWith("mpfinance", StringComparison.OrdinalIgnoreCase)) return ("finance", 60);
        return null;
    }

    // The catalog bucket is one per egress IP (shared by every account); the others are one per account.
    internal static string QuotaKey(HttpRequestMessage request, string bucket) =>
        bucket == CatalogBucket ? $"hepsiburada:{EgressScope}:{bucket}" : $"hepsiburada:{AccountKey(request)}:{bucket}";

    private static string AccountKey(HttpRequestMessage request)
    {
        var authorization = request.Headers.Authorization?.ToString();
        return string.IsNullOrWhiteSpace(authorization)
            ? "shared"
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(authorization)))[..16];
    }
}
