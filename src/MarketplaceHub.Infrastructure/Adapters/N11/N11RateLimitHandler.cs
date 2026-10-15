using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarketplaceHub.Infrastructure.Adapters.N11;

// n11 counts its quotas per API account, and the API process and every worker call it with the
// same keys, so each request takes a token from a PostgreSQL bucket shared by all of them.
// Buckets follow docs/N11-ENTEGRASYON-PLANI.md §5.6; only the order quota is documented by n11.
public sealed class N11RateLimitHandler(
    IOptions<N11Options> options,
    TimeProvider timeProvider,
    ISharedRateLimiter? sharedLimiter = null,
    ILogger<N11RateLimitHandler>? logger = null) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var bucket = BucketFor(request.RequestUri);
        if (sharedLimiter is not null && bucket is not null)
        {
            try
            {
                var limit = LimitPerMinute(options.Value, bucket);
                await SharedRateLimits.WaitAsync(sharedLimiter, $"n11:{AccountKey(request)}:{bucket}", SharedRateLimits.ForWindow(limit, TimeSpan.FromMinutes(1)), timeProvider, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A missing limiter must not stop marketplace traffic; n11 answers 429 when the quota is
                // really exceeded and the adapter maps that to a retry with Retry-After.
                logger?.LogWarning(exception, "Shared n11 rate limiter unavailable; sending without the shared quota");
            }
        }
        return await base.SendAsync(request, cancellationToken);
    }

    internal static string? BucketFor(Uri? uri)
    {
        var path = uri?.AbsolutePath.TrimStart('/') ?? "";
        if (path.StartsWith("rest/", StringComparison.OrdinalIgnoreCase)) return "orders";
        if (path.StartsWith("ms/product/task", StringComparison.OrdinalIgnoreCase)) return "tasks";
        if (path.StartsWith("ms/", StringComparison.OrdinalIgnoreCase)) return "products";
        if (path.StartsWith("cdn/", StringComparison.OrdinalIgnoreCase)) return "categories";
        if (path.StartsWith("ws/", StringComparison.OrdinalIgnoreCase)) return "soap";
        return null;
    }

    internal static int LimitPerMinute(N11Options options, string bucket) => bucket switch
    {
        "orders" => options.OrderRequestsPerMinute,
        "tasks" => options.TaskRequestsPerMinute,
        "products" => options.ProductRequestsPerMinute,
        "categories" => options.CategoryRequestsPerMinute,
        _ => options.SoapRequestsPerMinute
    };

    // REST calls carry the account in the appkey header; SOAP calls carry it in the envelope,
    // so they share one bucket per host.
    private static string AccountKey(HttpRequestMessage request)
    {
        var appKey = request.Headers.TryGetValues("appkey", out var values) ? values.FirstOrDefault() : null;
        return string.IsNullOrWhiteSpace(appKey)
            ? "shared"
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(appKey)))[..16];
    }
}
