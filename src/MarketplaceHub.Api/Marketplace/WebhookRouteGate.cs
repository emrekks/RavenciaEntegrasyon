using MarketplaceHub.Infrastructure.Persistence;
using MarketplaceHub.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace MarketplaceHub.Api.Marketplace;

// Answers whether a webhook address belongs to an active subscription of a connection that may receive
// deliveries, before the body is read: a request with a random connection id, or with a known connection
// id and a guessed route token, is refused with 404 without buffering up to 10 MiB first.
// MarketplaceWebhookService checks the same again, with the signature or credentials.
public interface IWebhookRouteGate
{
    ValueTask<bool> IsKnownAsync(Guid connectionPublicId, string routeToken, CancellationToken cancellationToken);
}

public sealed class WebhookRouteGate(AppDbContext db, IMemoryCache cache, TokenHasher hasher) : IWebhookRouteGate
{
    // Known addresses are remembered briefly so a delivery burst costs one lookup; unknown ones are
    // not remembered, so random ids or tokens cannot fill the cache.
    private static readonly TimeSpan KnownFor = TimeSpan.FromMinutes(1);
    private const int MaximumRouteTokenLength = 128;

    public async ValueTask<bool> IsKnownAsync(Guid connectionPublicId, string routeToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(routeToken) || routeToken.Length > MaximumRouteTokenLength) return false;
        var tokenHash = hasher.Hash(routeToken);
        var key = (nameof(WebhookRouteGate), connectionPublicId, tokenHash);
        if (cache.TryGetValue(key, out _)) return true;
        var known = await (from connection in db.PlatformConnections.AsNoTracking()
                           join subscription in db.WebhookSubscriptions.AsNoTracking() on new { connection.TenantId, ConnectionId = connection.Id } equals new { subscription.TenantId, subscription.ConnectionId }
                           where connection.PublicId == connectionPublicId
                               && (connection.PlatformCode == "TRENDYOL" || connection.PlatformCode == "SHOPIFY" || connection.PlatformCode == "HEPSIBURADA")
                               && (connection.Status == "ACTIVE" || connection.Status == "VERIFIED")
                               && subscription.Status == "ACTIVE" && subscription.RouteTokenHash == tokenHash
                           select subscription.Id).AnyAsync(cancellationToken);
        if (known) cache.Set(key, true, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = KnownFor, Size = 1 });
        return known;
    }
}
