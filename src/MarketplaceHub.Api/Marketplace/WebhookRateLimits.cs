using System.Threading.RateLimiting;
using MarketplaceHub.Api.Security;

namespace MarketplaceHub.Api.Marketplace;

// Webhook deliveries pass two limits. Marketplaces deliver from a few shared addresses, so the
// connection limit is generous and a campaign burst is not answered with 429 (Hepsiburada blacklists a
// slow or failing feed). The connection limit is taken in the endpoint, once the route gate has admitted
// the address (WebhookConnectionQuota): the connection's public id is no secret (every panel role reads
// it, and it sits in the address the marketplace was given), so a limit taken before the gate let anyone
// use up a connection's allowance with made-up route tokens and have the real deliveries refused
// (security review 2026-10-29 A-2). Before the endpoint, the per-network limit stops a sender that changes
// the id or the token (an IPv6 sender is counted per /64, see ClientNetwork). One marketplace address
// delivers for every store (and tenant) connected here, so the address limit is several connections'
// worth: it must never be the one that refuses a real burst, only a guessing sender. Memory is guarded
// separately: at most ConcurrentDeliveries deliveries are read at once (each may buffer up to 10 MiB);
// the rest wait in a queue long enough for a campaign burst (the limiter runs before any body is read,
// so a waiting delivery holds only its connection).
public static class WebhookRateLimits
{
    public const int PerConnectionPerMinute = 1200;
    public const int PerAddressPerMinute = 4 * PerConnectionPerMinute;
    public const int ConcurrentDeliveries = 32;
    public const int QueuedDeliveries = 256;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    public static PartitionedRateLimiter<HttpContext> Create() => PerAddress();

    public static PartitionedRateLimiter<HttpContext> Concurrency() => PartitionedRateLimiter.Create<HttpContext, string>(context =>
        ConnectionSegment(context) is null
            ? RateLimitPartition.GetNoLimiter(string.Empty)
            : RateLimitPartition.GetConcurrencyLimiter("webhook-deliveries", _ => new ConcurrencyLimiterOptions { PermitLimit = ConcurrentDeliveries, QueueLimit = QueuedDeliveries, QueueProcessingOrder = QueueProcessingOrder.OldestFirst }));

    private static PartitionedRateLimiter<HttpContext> PerAddress() => PartitionedRateLimiter.Create<HttpContext, string>(context =>
        ConnectionSegment(context) is null
            ? RateLimitPartition.GetNoLimiter(string.Empty)
            : RateLimitPartition.GetFixedWindowLimiter($"address:{ClientNetwork.PartitionKey(context)}", _ => Options(PerAddressPerMinute)));

    // /api/v1/hooks/{connectionPublicId}/...; null for every other request.
    internal static string? ConnectionSegment(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/api/v1/hooks", StringComparison.OrdinalIgnoreCase, out var rest)) return null;
        var segment = (rest.Value ?? string.Empty).TrimStart('/').Split('/', 2)[0];
        return segment.Length == 0 ? null : segment.ToLowerInvariant();
    }

    internal static FixedWindowRateLimiterOptions Options(int permits) => new() { PermitLimit = permits, Window = Window, QueueLimit = 0 };
}

// The per-connection allowance (PerConnectionPerMinute, whatever address delivers), taken only by the
// deliveries whose route token the gate admitted. One instance for the application.
public sealed class WebhookConnectionQuota : IDisposable
{
    private readonly PartitionedRateLimiter<Guid> limiter = PartitionedRateLimiter.Create<Guid, Guid>(connection =>
        RateLimitPartition.GetFixedWindowLimiter(connection, _ => WebhookRateLimits.Options(WebhookRateLimits.PerConnectionPerMinute)));

    public bool TryTake(Guid connectionPublicId)
    {
        using var lease = limiter.AttemptAcquire(connectionPublicId);
        return lease.IsAcquired;
    }

    public void Dispose() => limiter.Dispose();
}
