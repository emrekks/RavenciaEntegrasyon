using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace MarketplaceHub.Api.Marketplace;

// Answers whether a webhook address belongs to a connection that may receive deliveries, before the
// body is read: a request for a random connection id is refused with 404 without buffering up to
// 10 MiB first. MarketplaceWebhookService checks the same again, with the route token and signature.
public interface IWebhookRouteGate
{
    ValueTask<bool> IsKnownAsync(Guid connectionPublicId, CancellationToken cancellationToken);
}

public sealed class WebhookRouteGate(AppDbContext db, IMemoryCache cache) : IWebhookRouteGate
{
    // Known addresses are remembered briefly so a delivery burst costs one lookup; unknown ones are
    // not remembered, so random ids cannot fill the cache.
    private static readonly TimeSpan KnownFor = TimeSpan.FromMinutes(1);

    public async ValueTask<bool> IsKnownAsync(Guid connectionPublicId, CancellationToken cancellationToken)
    {
        var key = (nameof(WebhookRouteGate), connectionPublicId);
        if (cache.TryGetValue(key, out _)) return true;
        var known = await db.PlatformConnections.AsNoTracking().AnyAsync(x => x.PublicId == connectionPublicId
            && (x.PlatformCode == "TRENDYOL" || x.PlatformCode == "SHOPIFY" || x.PlatformCode == "HEPSIBURADA")
            && (x.Status == "ACTIVE" || x.Status == "VERIFIED"), cancellationToken);
        if (known) cache.Set(key, true, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = KnownFor, Size = 1 });
        return known;
    }
}
