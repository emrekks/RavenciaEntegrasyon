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
    // A request can name a stricter bucket than its path family (e.g. the 1/min question list).
    public static readonly HttpRequestOptionsKey<string> BucketKey = new("MarketplaceHub.N11.RateBucket");
    // SOAP calls carry the account inside the envelope, so the client names it here.
    public static readonly HttpRequestOptionsKey<string> AccountKeyOption = new("MarketplaceHub.N11.Account");
    // Invoked once the request has its quota token, right before it is sent; the caller starts the request
    // timeout there, so time spent waiting for the quota is not counted as a timeout.
    public static readonly HttpRequestOptionsKey<Action> QuotaAcquiredKey = new("MarketplaceHub.N11.QuotaAcquired");

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var bucket = request.Options.TryGetValue(BucketKey, out var namedBucket) && !string.IsNullOrWhiteSpace(namedBucket)
            ? namedBucket
            : BucketFor(request.RequestUri);
        if (sharedLimiter is not null && bucket is not null)
        {
            try
            {
                var limit = LimitPerMinute(options.Value, bucket);
                await SharedRateLimits.WaitAsync(sharedLimiter, $"n11:{AccountKey(request)}:{bucket}", BucketShape(limit), timeProvider, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A missing limiter must not stop marketplace traffic; n11 answers 429 when the quota is
                // really exceeded and the adapter maps that to a retry with Retry-After.
                logger?.LogWarning(exception, "Shared n11 rate limiter unavailable; sending without the shared quota");
            }
        }
        if (request.Options.TryGetValue(QuotaAcquiredKey, out var onQuotaAcquired)) onQuotaAcquired();
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

    // A sliding window cannot express very small quotas (1/min would allow one call per ~16 min),
    // so those are enforced as even spacing instead. n11 measures the gap on its own clock, so an
    // exact 60 s spacing is still refused now and then ("1 dakikada bir"); a quarter more is kept.
    internal static (double Capacity, double TokensPerSecond) BucketShape(int limitPerMinute) =>
        limitPerMinute <= 5
            ? SharedRateLimits.ForSpacing(TimeSpan.FromMinutes(1) * 1.25 / Math.Max(1, limitPerMinute))
            : SharedRateLimits.ForWindow(limitPerMinute, TimeSpan.FromMinutes(1));

    internal static int LimitPerMinute(N11Options options, string bucket) => bucket switch
    {
        "orders" => options.OrderRequestsPerMinute,
        "tasks" => options.TaskRequestsPerMinute,
        "products" => options.ProductRequestsPerMinute,
        "categories" => options.CategoryRequestsPerMinute,
        "questions-list" => options.QuestionListRequestsPerMinute,
        _ => options.SoapRequestsPerMinute
    };

    // REST calls carry the account in the appkey header; SOAP calls name it through AccountKeyOption.
    private static string AccountKey(HttpRequestMessage request)
    {
        var appKey = request.Options.TryGetValue(AccountKeyOption, out var soapAccount) && !string.IsNullOrWhiteSpace(soapAccount)
            ? soapAccount
            : request.Headers.TryGetValues("appkey", out var values) ? values.FirstOrDefault() : null;
        return string.IsNullOrWhiteSpace(appKey)
            ? "shared"
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(appKey)))[..16];
    }
}
