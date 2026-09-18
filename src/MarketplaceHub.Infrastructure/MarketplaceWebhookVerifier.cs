using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Adapters.Shopify;
using MarketplaceHub.Infrastructure.Adapters.Trendyol;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure;

public sealed class MarketplaceWebhookVerifier(
    AppDbContext db,
    TrendyolWebhookVerifier trendyol,
    ShopifyWebhookVerifier shopify) : IWebhookVerifier
{
    public async ValueTask<AdapterResult<VerifiedWebhookEnvelope>> VerifyAsync(ReadOnlyMemory<byte> rawBody, IReadOnlyDictionary<string, string> headers, Guid connectionId, Guid subscriptionId, CancellationToken cancellationToken)
    {
        var platform = await db.PlatformConnections.AsNoTracking()
            .Where(x => x.Id == connectionId)
            .Select(x => x.PlatformCode)
            .SingleOrDefaultAsync(cancellationToken);
        return platform == "SHOPIFY"
            ? await shopify.VerifyAsync(rawBody, headers, connectionId, subscriptionId, cancellationToken)
            : await trendyol.VerifyAsync(rawBody, headers, connectionId, subscriptionId, cancellationToken);
    }
}
