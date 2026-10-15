using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarketplaceHub.Infrastructure.Adapters.Pazarama;

// Pazarama counts its quotas per seller, and the API process and every worker call it with the
// same client, so each request takes a token from a PostgreSQL bucket shared by all of them.
// Price and stock share one bucket: the documentation asks for 10 s between "stock-price" requests.
public sealed class PazaramaRateLimitHandler(
    IOptions<PazaramaOptions> options,
    TimeProvider timeProvider,
    ISharedRateLimiter? sharedLimiter = null,
    ILogger<PazaramaRateLimitHandler>? logger = null) : DelegatingHandler
{
    public static readonly HttpRequestOptionsKey<string> AccountKeyOption = new("MarketplaceHub.Pazarama.Account");

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var bucket = BucketFor(request.RequestUri);
        if (sharedLimiter is not null && bucket is not null && request.Options.TryGetValue(AccountKeyOption, out var account) && !string.IsNullOrWhiteSpace(account))
        {
            try
            {
                await SharedRateLimits.WaitAsync(sharedLimiter, $"pazarama:{account}:{bucket}", BucketShape(options.Value, bucket), timeProvider, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A missing limiter must not stop marketplace traffic; Pazarama answers 429 when the
                // quota is really exceeded and the adapter maps that to a retry.
                logger?.LogWarning(exception, "Shared Pazarama rate limiter unavailable; sending without the shared quota");
            }
        }
        return await base.SendAsync(request, cancellationToken);
    }

    internal static string? BucketFor(Uri? uri)
    {
        var path = uri?.AbsolutePath.TrimStart('/') ?? "";
        if (path.Length == 0 || path.StartsWith("connect/", StringComparison.OrdinalIgnoreCase)) return null;
        if (path.StartsWith("product/updatePrice", StringComparison.OrdinalIgnoreCase) || path.StartsWith("product/updateStock", StringComparison.OrdinalIgnoreCase)) return "price-stock";
        if (path.Equals("product/create", StringComparison.OrdinalIgnoreCase)) return "product-create";
        if (path.StartsWith("order/", StringComparison.OrdinalIgnoreCase)) return "orders";
        return "reads";
    }

    internal static (double Capacity, double TokensPerSecond) BucketShape(PazaramaOptions options, string bucket) => bucket switch
    {
        "price-stock" => SharedRateLimits.ForSpacing(options.PriceStockSpacing),
        "product-create" => SharedRateLimits.ForSpacing(options.ProductCreateSpacing),
        "orders" => SharedRateLimits.ForWindow(options.OrderRequestsPerMinute, TimeSpan.FromMinutes(1)),
        _ => SharedRateLimits.ForWindow(options.ReadRequestsPerMinute, TimeSpan.FromMinutes(1))
    };
}
