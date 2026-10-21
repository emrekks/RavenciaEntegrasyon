using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarketplaceHub.Infrastructure.Adapters.PttAvm;

// PTT AVM documents no quotas, so the starting buckets are cautious (docs/PTTAVM-ENTEGRASYON-PLANI.md §5.4).
// The API process and every worker call it with the same client, so each request takes a token from a
// PostgreSQL bucket shared by all of them, per API user. Every SOAP call goes to the same address, so the
// bucket is chosen by the operation name the client puts on the request.
public sealed class PttAvmRateLimitHandler(
    IOptions<PttAvmOptions> options,
    TimeProvider timeProvider,
    ISharedRateLimiter? sharedLimiter = null,
    ILogger<PttAvmRateLimitHandler>? logger = null) : DelegatingHandler
{
    public static readonly HttpRequestOptionsKey<string> AccountKeyOption = new("MarketplaceHub.PttAvm.Account");
    public static readonly HttpRequestOptionsKey<string> OperationKey = new("MarketplaceHub.PttAvm.Operation");
    // Invoked once the request has its quota token, right before it is sent; the caller starts the request
    // timeout there, so time spent waiting for the quota is not counted as a timeout.
    public static readonly HttpRequestOptionsKey<Action> QuotaAcquiredKey = new("MarketplaceHub.PttAvm.QuotaAcquired");

    // The API user behind the credentials, without the user name itself: one user belongs to one store.
    public static string AccountKey(string username) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(username.Trim().ToLowerInvariant())))[..16];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var bucket = BucketFor(request.Options.TryGetValue(OperationKey, out var operation) ? operation : null);
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

    internal static string BucketFor(string? operation)
    {
        if (string.IsNullOrWhiteSpace(operation)) return "reads";
        if (operation.StartsWith("Stok", StringComparison.Ordinal) && operation.Contains("Guncelle", StringComparison.Ordinal)
            || operation.StartsWith("UpdateProduct", StringComparison.Ordinal))
            return "product-write";
        if (operation.StartsWith("Siparis", StringComparison.Ordinal) || operation is "KargoBilgiListesi" or "SaveInvoince") return "orders";
        return "reads";
    }

    internal static (double Capacity, double TokensPerSecond) BucketShape(PttAvmOptions options, string bucket) => bucket switch
    {
        "product-write" => SharedRateLimits.ForSpacing(options.ProductWriteSpacing),
        "orders" => SharedRateLimits.ForWindow(options.OrderRequestsPerMinute, TimeSpan.FromMinutes(1)),
        _ => SharedRateLimits.ForWindow(options.ReadRequestsPerMinute, TimeSpan.FromMinutes(1))
    };
}
