using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarketplaceHub.Infrastructure.Adapters.PttAvm;

// PTT AVM documents no quotas, so the starting buckets are cautious (docs/PTTAVM-ENTEGRASYON-PLANI.md §5.4).
// The API process and every worker call it with the same client, so each request takes a token from a
// PostgreSQL bucket shared by all of them, per store.
public sealed class PttAvmRateLimitHandler(
    IOptions<PttAvmOptions> options,
    TimeProvider timeProvider,
    ISharedRateLimiter? sharedLimiter = null,
    ILogger<PttAvmRateLimitHandler>? logger = null) : DelegatingHandler
{
    public static readonly HttpRequestOptionsKey<string> AccountKeyOption = new("MarketplaceHub.PttAvm.Account");
    // Invoked once the request has its quota token, right before it is sent; the caller starts the request
    // timeout there, so time spent waiting for the quota is not counted as a timeout.
    public static readonly HttpRequestOptionsKey<Action> QuotaAcquiredKey = new("MarketplaceHub.PttAvm.QuotaAcquired");

    // The store behind the credentials, without the token itself. The Access Token is issued per integrator
    // and store, while the API Key may be shared by every store of one integrator.
    public static string StoreKey(string accessToken) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(accessToken.Trim())))[..16];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var bucket = BucketFor(request.RequestUri);
        if (sharedLimiter is not null && request.Options.TryGetValue(AccountKeyOption, out var account) && !string.IsNullOrWhiteSpace(account))
        {
            try
            {
                await SharedRateLimits.WaitAsync(sharedLimiter, $"pttavm:{account}:{bucket}", BucketShape(options.Value, bucket), timeProvider, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A missing limiter must not stop marketplace traffic; PTT AVM answers 429 when a quota
                // really exists and the adapter maps that to a retry.
                logger?.LogWarning(exception, "Shared PTT AVM rate limiter unavailable; sending without the shared quota");
            }
        }
        // The circuit's half-open probe is claimed only now that the token is held (G11).
        if (MarketplaceCircuitBreakerHandler.PassProbeGate(request) is { } refused) return refused;
        if (request.Options.TryGetValue(QuotaAcquiredKey, out var onQuotaAcquired)) onQuotaAcquired();
        return await base.SendAsync(request, cancellationToken);
    }

    internal static string BucketFor(Uri? uri)
    {
        if (uri is null) return "reads";
        if (uri.Host.StartsWith("shipment.", StringComparison.OrdinalIgnoreCase)) return "shipment";
        var path = uri.AbsolutePath.TrimStart('/');
        if (path.StartsWith("api/v1/", StringComparison.OrdinalIgnoreCase)) path = path["api/v1/".Length..];
        if (path.Equals("products/upsert", StringComparison.OrdinalIgnoreCase) || path.Equals("products/stock-prices", StringComparison.OrdinalIgnoreCase)) return "product-write";
        if (path.StartsWith("orders/", StringComparison.OrdinalIgnoreCase)) return "orders";
        return "reads";
    }

    internal static (double Capacity, double TokensPerSecond) BucketShape(PttAvmOptions options, string bucket) => bucket switch
    {
        "product-write" => SharedRateLimits.ForSpacing(options.ProductWriteSpacing),
        "orders" => SharedRateLimits.ForWindow(options.OrderRequestsPerMinute, TimeSpan.FromMinutes(1)),
        "shipment" => SharedRateLimits.ForWindow(options.ShipmentRequestsPerMinute, TimeSpan.FromMinutes(1)),
        _ => SharedRateLimits.ForWindow(options.ReadRequestsPerMinute, TimeSpan.FromMinutes(1))
    };
}
