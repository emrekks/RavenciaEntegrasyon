using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace MarketplaceHub.Infrastructure.Adapters.Hepsiburada;

// Hepsiburada counts quotas per merchant account, and the API process and every worker share the
// account, so each request takes a token from a PostgreSQL bucket shared by all of them. The order
// services allow 1,000 requests a second; the panel keeps far below that. Catalog and listing
// (price and stock) calls follow their own documented minute quotas.
public sealed class HepsiburadaRateLimitHandler(
    TimeProvider timeProvider,
    ISharedRateLimiter? sharedLimiter = null,
    ILogger<HepsiburadaRateLimitHandler>? logger = null) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (sharedLimiter is not null && BucketFor(request.RequestUri) is { } bucket)
        {
            try
            {
                await SharedRateLimits.WaitAsync(sharedLimiter, $"hepsiburada:{AccountKey(request)}:{bucket.Name}", SharedRateLimits.ForWindow(bucket.LimitPerMinute, TimeSpan.FromMinutes(1)), timeProvider, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A missing limiter must not stop marketplace traffic; Hepsiburada answers 429 when the
                // quota is really exceeded and the adapter maps that to a retry.
                logger?.LogWarning(exception, "Shared Hepsiburada rate limiter unavailable; sending without the shared quota");
            }
        }
        return await base.SendAsync(request, cancellationToken);
    }

    internal static (string Name, int LimitPerMinute)? BucketFor(Uri? uri)
    {
        var host = uri?.Host ?? "";
        if (host.StartsWith("oms-", StringComparison.OrdinalIgnoreCase)) return ("oms", 600);
        if (host.StartsWith("mpop", StringComparison.OrdinalIgnoreCase)) return ("catalog", 180);
        if (host.StartsWith("listing-", StringComparison.OrdinalIgnoreCase)) return ("listing", 100);
        return null;
    }

    private static string AccountKey(HttpRequestMessage request)
    {
        var authorization = request.Headers.Authorization?.ToString();
        return string.IsNullOrWhiteSpace(authorization)
            ? "shared"
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(authorization)))[..16];
    }
}
