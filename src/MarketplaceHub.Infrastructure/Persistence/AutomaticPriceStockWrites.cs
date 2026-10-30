using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

// The issue an automatic price/stock send leaves for an offer it cannot send (price 0, another currency, a code the
// marketplace cannot take), one per connection and variant; the next send that carries the offer closes it.
internal static class PriceInventoryOfferIssues
{
    public const string KeyPrefix = "stock-projection-offer-invalid:";

    public static string Key(Guid connectionId, Guid variantId) => $"{KeyPrefix}{connectionId:N}:{variantId:N}";

    public static string ConnectionPrefix(Guid connectionId) => $"{KeyPrefix}{connectionId:N}:";

    public static bool IsOfferProblem(string? code) =>
        code is "CHANNEL_OFFER_INVALID" or "HEPSIBURADA_LISTING_LINK_REQUIRED" or "PAZARAMA_BARCODE_INVALID";
}

// Owner decisions A and B: the gate of the automatic price/stock writes and the baseline taken when it opens.
internal static class AutomaticPriceStockWrites
{
    // The connections among the given ones whose automatic price/stock writes are open: the stored master switch on
    // and both write policies switched on (a policy without a row is off). The global switch is the worker's own
    // check; the maintenance pass clears the stored switch when it is off.
    public static async Task<HashSet<Guid>> OpenConnectionIdsAsync(AppDbContext db, Guid tenantId, IReadOnlyCollection<Guid> connectionIds, CancellationToken cancellationToken)
    {
        if (connectionIds.Count == 0) return [];
        var ids = connectionIds.Distinct().ToArray();
        var settings = await db.PlatformConnections.AsNoTracking()
            .Where(x => x.TenantId == tenantId && ids.Contains(x.Id))
            .Select(x => new { x.Id, x.SettingsJson })
            .ToListAsync(cancellationToken);
        var policies = await db.ConnectionSyncPolicies.AsNoTracking()
            .Where(x => x.TenantId == tenantId && ids.Contains(x.ConnectionId)
                && (x.ResourceType == MarketplaceExternalWritePolicies.Price || x.ResourceType == MarketplaceExternalWritePolicies.Stock))
            .Select(x => new { x.ConnectionId, x.ResourceType, x.Enabled })
            .ToListAsync(cancellationToken);
        return settings
            .Where(connection => AutomaticPriceStockWriteGate.IsOpen(
                StockProjectionOutbox.StoredExternalWritesEnabled(connection.SettingsJson),
                policies.FirstOrDefault(x => x.ConnectionId == connection.Id && x.ResourceType == MarketplaceExternalWritePolicies.Price)?.Enabled,
                policies.FirstOrDefault(x => x.ConnectionId == connection.Id && x.ResourceType == MarketplaceExternalWritePolicies.Stock)?.Enabled))
            .Select(connection => connection.Id)
            .ToHashSet();
    }

    public static async Task<bool> IsOpenAsync(AppDbContext db, Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        (await OpenConnectionIdsAsync(db, tenantId, [connectionId], cancellationToken)).Contains(connectionId);

    // B: every offer of the connection counts as sent at its current values (the price mark each platform's build
    // computes and the variant's current stock version), so the changes made while the writes were off are never sent;
    // only what changes after this moment goes out. The offers are changed in the caller's unit of work.
    public static async Task<int> MarkCurrentValuesSentAsync(AppDbContext db, Guid tenantId, Guid connectionId, string platformCode, CancellationToken cancellationToken)
    {
        var rows = await (from offer in db.ChannelOffers
                          join inventory in db.InventoryItems.AsNoTracking().Where(x => x.LocationCode == "MAIN")
                              on new { offer.TenantId, offer.VariantId } equals new { inventory.TenantId, inventory.VariantId } into inventories
                          from inventory in inventories.DefaultIfEmpty()
                          where offer.TenantId == tenantId && offer.ConnectionId == connectionId
                          select new { Offer = offer, ProjectionVersion = (long?)inventory.ProjectionVersion })
            .ToListAsync(cancellationToken);
        var changed = 0;
        foreach (var row in rows)
        {
            var priceHash = PriceInventoryComposer.CurrentPriceHash(platformCode, row.Offer);
            var stockVersion = row.ProjectionVersion ?? row.Offer.LastStockProjectionVersion;
            if (row.Offer.LastPriceHash == priceHash && row.Offer.LastStockProjectionVersion == stockVersion && row.Offer.LastSentCode is null) continue;
            row.Offer.LastPriceHash = priceHash;
            row.Offer.LastStockProjectionVersion = stockVersion;
            // The values the marketplace last confirmed are unknown from here on: the baseline is a decision not to send,
            // not a confirmation. Cleared, the first change after enabling is sent for every field it changed (Trendyol
            // compares a changed field with the confirmed value, PriceInventorySendPolicy.TrendyolFields), and a value
            // equal to an old confirmation is never taken as already held by the marketplace.
            PriceInventorySendPolicy.ClearSent(row.Offer);
            row.Offer.Version++;
            changed++;
        }
        return changed;
    }
}
