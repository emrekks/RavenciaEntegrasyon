using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

// Queues a STOCK_PROJECTION_DISPATCH for every connection with an ACTIVE offer of a changed variant whose automatic
// price/stock writes are open. The jobs are added to the caller's unit of work, so they commit together with the
// stock (or price) change, and the job's creation time is the change's time (the order freshness gate, decision C).
internal static class StockProjectionOutbox
{
    public static async Task EnqueueAsync(AppDbContext db, Guid tenantId, IReadOnlyCollection<StockProjectionChange> changes, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (changes.Count == 0) return;
        var variantIds = changes.Select(change => change.VariantId).Distinct().ToArray();
        // A passive connection refuses every job, so it gets none; the reconcile scan catches it up
        // once it is active again.
        var activeOffers = await (from offer in db.ChannelOffers.AsNoTracking()
                                  join connection in db.PlatformConnections.AsNoTracking() on new { offer.TenantId, Id = offer.ConnectionId } equals new { connection.TenantId, connection.Id }
                                  where offer.TenantId == tenantId && variantIds.Contains(offer.VariantId) && offer.Status == "ACTIVE"
                                      && (connection.Status == "ACTIVE" || connection.Status == "VERIFIED")
                                  select new { offer.ConnectionId, offer.VariantId, connection.SettingsJson })
            .ToListAsync(cancellationToken);
        // Owner decision A: a connection whose master switch or automatic price/stock policies are off gets no
        // dispatch at all (it would only finish without sending). What changes meanwhile is never sent: switching
        // the writes on marks every offer as sent at its values then (decision B). The worker checks the gate again.
        var open = await AutomaticPriceStockWrites.OpenConnectionIdsAsync(db, tenantId, activeOffers.Select(offer => offer.ConnectionId).Distinct().ToArray(), cancellationToken);
        var targets = StockProjectionOutboxPolicy.FanOut(changes, activeOffers
            .Where(offer => open.Contains(offer.ConnectionId))
            .Select(offer => (offer.ConnectionId, offer.VariantId)));
        await AddDispatchesAsync(db, tenantId, targets, now, cancellationToken);
    }

    // E: a price edit of offers (from the panel or a product file) is sent like a stock change, to the offer's own
    // connection only; the dispatch sends the offer's current values and only the offers whose price changed go out.
    // priceChanged: whether an offer's save changed its price (a PRICE_EDIT send) or only its safety stock or status (a
    // stock send); null when every offer given was repriced.
    public static async Task EnqueuePriceChangesAsync(AppDbContext db, Guid tenantId, IReadOnlyCollection<ChannelOffer> offers, DateTimeOffset now, CancellationToken cancellationToken, Func<ChannelOffer, bool>? priceChanged = null)
    {
        var changed = offers.Where(offer => offer.Status == "ACTIVE").ToList();
        if (changed.Count == 0) return;
        var connectionIds = changed.Select(offer => offer.ConnectionId).Distinct().ToArray();
        var activeConnections = await db.PlatformConnections.AsNoTracking()
            .Where(x => x.TenantId == tenantId && connectionIds.Contains(x.Id) && (x.Status == "ACTIVE" || x.Status == "VERIFIED"))
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);
        var open = await AutomaticPriceStockWrites.OpenConnectionIdsAsync(db, tenantId, activeConnections, cancellationToken);
        var targets = changed
            .Where(offer => open.Contains(offer.ConnectionId))
            .Select(offer => new StockProjectionTarget(offer.ConnectionId, offer.VariantId,
                priceChanged is null || priceChanged(offer) ? $"price:{offer.Id:N}:p{offer.PriceVersion}" : $"offer:{offer.Id:N}:v{offer.Version}",
                StockProjectionOutboxPolicy.PriceChangeDedupKey(offer.ConnectionId, offer.VariantId, offer.PriceVersion, offer.Version)))
            .ToList();
        await AddDispatchesAsync(db, tenantId, targets, now, cancellationToken);
    }

    private static async Task AddDispatchesAsync(AppDbContext db, Guid tenantId, IReadOnlyList<StockProjectionTarget> targets, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (targets.Count == 0) return;
        var dedupKeys = targets.Select(target => target.DedupKey).ToArray();
        var existing = await db.IntegrationJobs.AsNoTracking()
            .Where(job => job.TenantId == tenantId && dedupKeys.Contains(job.JobDedupKey))
            .Select(job => job.JobDedupKey)
            .ToHashSetAsync(StringComparer.Ordinal, cancellationToken);
        foreach (var target in targets)
        {
            if (existing.Contains(target.DedupKey) || db.IntegrationJobs.Local.Any(job => job.TenantId == tenantId && job.JobDedupKey == target.DedupKey)) continue;
            var payload = JsonSerializer.Serialize(new { variantId = target.VariantId, sourceEventId = target.SourceEventId });
            db.IntegrationJobs.Add(new IntegrationJob
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                ConnectionId = target.ConnectionId,
                JobType = MarketplaceJobTypes.StockProjectionDispatch,
                PayloadJson = payload,
                PayloadVersion = 1,
                PayloadHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))),
                JobDedupKey = target.DedupKey,
                EffectIdempotencyKey = target.DedupKey,
                Priority = JobPriorityPolicy.For(MarketplaceJobTypes.StockProjectionDispatch, userInitiated: true),
                Status = JobStatus.Pending,
                AvailableAt = now,
                MaxAttempts = 10,
                CorrelationId = $"stock:{target.VariantId:N}",
                CreatedAt = now,
                Version = 1
            });
        }
    }

    internal static bool StoredExternalWritesEnabled(string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson)) return false;
        try
        {
            using var document = JsonDocument.Parse(settingsJson);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("ExternalWritesEnabled", out var enabled) && enabled.ValueKind == JsonValueKind.True;
        }
        catch (JsonException) { return false; }
    }
}
