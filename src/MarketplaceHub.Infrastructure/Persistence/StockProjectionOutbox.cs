using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

// Queues a STOCK_PROJECTION_DISPATCH for every connection with an ACTIVE offer of a changed variant.
// The jobs are added to the caller's unit of work, so they commit together with the stock change.
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
        // A connection whose external writes are off would only finish the dispatch without sending, yet the
        // job (an effect guard) stayed in the table; it gets none. The stock reconcile scan catches it up once
        // writes are on again. The maintenance pass clears the stored switch when the global switch is off.
        var targets = StockProjectionOutboxPolicy.FanOut(changes, activeOffers
            .Where(offer => StoredExternalWritesEnabled(offer.SettingsJson))
            .Select(offer => (offer.ConnectionId, offer.VariantId)));
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
