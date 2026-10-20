using System.Threading.RateLimiting;
using MarketplaceHub.Api.Security;

namespace MarketplaceHub.Api.Marketplace;

// Webhook deliveries pass two limits in a chain. Marketplaces deliver from a few shared addresses, so
// the connection limit is generous and a campaign burst is not answered with 429 (Hepsiburada
// blacklists a slow or failing feed). The connection id comes from the URL, though, so on its own it
// let anyone send unlimited requests by changing the id; the per-network limit closes that (an IPv6
// sender is counted per /64, see ClientNetwork). Separately, at most ConcurrentDeliveries deliveries are
// read at once (each may buffer up to 10 MiB); the rest wait in a short queue.
public static class WebhookRateLimits
{
    public const int PerAddressPerMinute = 600;
    public const int PerConnectionPerMinute = 1200;
    public const int ConcurrentDeliveries = 32;
    public const int QueuedDeliveries = 64;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    public static PartitionedRateLimiter<HttpContext> Create() => PartitionedRateLimiter.CreateChained(PerAddress(), PerConnection());

    public static PartitionedRateLimiter<HttpContext> Concurrency() => PartitionedRateLimiter.Create<HttpContext, string>(context =>
        ConnectionSegment(context) is null
            ? RateLimitPartition.GetNoLimiter(string.Empty)
            : RateLimitPartition.GetConcurrencyLimiter("webhook-deliveries", _ => new ConcurrencyLimiterOptions { PermitLimit = ConcurrentDeliveries, QueueLimit = QueuedDeliveries, QueueProcessingOrder = QueueProcessingOrder.OldestFirst }));

    private static PartitionedRateLimiter<HttpContext> PerAddress() => PartitionedRateLimiter.Create<HttpContext, string>(context =>
        ConnectionSegment(context) is null
            ? RateLimitPartition.GetNoLimiter(string.Empty)
            : RateLimitPartition.GetFixedWindowLimiter($"address:{ClientNetwork.PartitionKey(context)}", _ => Options(PerAddressPerMinute)));

    private static PartitionedRateLimiter<HttpContext> PerConnection() => PartitionedRateLimiter.Create<HttpContext, string>(context =>
        ConnectionSegment(context) is not { } connection
            ? RateLimitPartition.GetNoLimiter(string.Empty)
            : RateLimitPartition.GetFixedWindowLimiter($"connection:{connection}", _ => Options(PerConnectionPerMinute)));

    // /api/v1/hooks/{connectionPublicId}/...; null for every other request.
    internal static string? ConnectionSegment(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/api/v1/hooks", StringComparison.OrdinalIgnoreCase, out var rest)) return null;
        var segment = (rest.Value ?? string.Empty).TrimStart('/').Split('/', 2)[0];
        return segment.Length == 0 ? null : segment.ToLowerInvariant();
    }

    private static FixedWindowRateLimiterOptions Options(int permits) => new() { PermitLimit = permits, Window = Window, QueueLimit = 0 };
}
