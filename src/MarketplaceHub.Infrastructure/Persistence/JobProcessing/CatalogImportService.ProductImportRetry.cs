using System.Text.Json;
using System.Text.Json.Nodes;
using MarketplaceHub.Application;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

// ISLEYICI-1 retry (16 Nov 2026): the failed models of a catalog import are read again one by one
// (ProductImportRetryPolicy). Called at the end of an import, before its staging rows are deleted.
internal sealed partial class CatalogImportService
{
    // A run that is not itself a retry lists the models it could not store and queues their first try.
    private async Task NoteFailedModelsForRetryAsync(Guid tenantId, Guid connectionId, string? platformCode, Guid importJobId, string payloadJson, string correlationId, CancellationToken cancellationToken)
    {
        if (!ProductImportRetryPolicy.Supports(platformCode)) return;
        var failedSnapshots = await db.ProductImportStagingRecords.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.JobId == importJobId && x.State == "FAILED")
            .Select(x => x.SnapshotJson)
            .ToListAsync(cancellationToken);
        var keys = failedSnapshots.Select(RetryLookupKey).Where(key => key is not null).Select(key => key!).ToList();
        var cursor = await cursors.FindAsync(tenantId, connectionId, ProductImportRetryPolicy.CursorResource, cancellationToken);
        if (keys.Count == 0 && cursor?.OpaqueCursor is null) return;
        var now = timeProvider.GetUtcNow();
        var entries = ProductImportRetryPolicy.Note(ProductImportRetryPolicy.Parse(cursor?.OpaqueCursor), keys, now, out var added);
        foreach (var entry in added)
            await QueueProductImportRetryAsync(tenantId, connectionId, platformCode!, payloadJson, correlationId, entry, now, cancellationToken);
        await SaveProductImportRetriesAsync(tenantId, connectionId, cursor, entries, cancellationToken);
    }

    // A retry run takes its model off the list when it stored it, and resolves the PRODUCT_IMPORT_FAILED issues of the
    // products it stored (a catalog import does not resolve them by itself); else it queues the next try (or gives the
    // model up, its issue left open).
    private async Task CompleteProductImportRetryAsync(Guid tenantId, Guid connectionId, string? platformCode, Guid importJobId, string lookupKey, bool stored, string payloadJson, string correlationId, CancellationToken cancellationToken)
    {
        if (stored)
        {
            var storedProducts = await db.ProductImportStagingRecords.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.JobId == importJobId && x.State == "COMPLETED")
                .Select(x => x.ExternalProductId)
                .Distinct()
                .ToListAsync(cancellationToken);
            foreach (var externalProductId in storedProducts.Where(id => !string.IsNullOrWhiteSpace(id)))
                await ResolveIssue(tenantId, $"product-sync-import:{connectionId}:{externalProductId}", cancellationToken);
        }
        var cursor = await cursors.FindAsync(tenantId, connectionId, ProductImportRetryPolicy.CursorResource, cancellationToken);
        if (cursor?.OpaqueCursor is null) return;
        var now = timeProvider.GetUtcNow();
        var entries = ProductImportRetryPolicy.AfterRetry(ProductImportRetryPolicy.Parse(cursor.OpaqueCursor), lookupKey.Trim(), stored, now, out var next);
        if (next is not null && ProductImportRetryPolicy.Supports(platformCode))
            await QueueProductImportRetryAsync(tenantId, connectionId, platformCode!, payloadJson, correlationId, next, now, cancellationToken);
        await SaveProductImportRetriesAsync(tenantId, connectionId, cursor, entries, cancellationToken);
    }

    private async Task SaveProductImportRetriesAsync(Guid tenantId, Guid connectionId, Domain.SyncCursor? cursor, IReadOnlyList<ProductImportRetryPolicy.Entry> entries, CancellationToken cancellationToken)
    {
        var serialized = ProductImportRetryPolicy.Serialize(entries);
        if (cursor is null && serialized is null) return;
        cursor ??= await cursors.GetAsync(tenantId, connectionId, ProductImportRetryPolicy.CursorResource, cancellationToken);
        if (cursor.OpaqueCursor == serialized) return;
        cursor.OpaqueCursor = serialized;
        cursor.Version++;
    }

    // The single-product import of one model, due at its try's time: the same choices as the run that failed it, for this
    // product only, marked as a retry. A try is queued once (its dedup key names the try).
    private async Task QueueProductImportRetryAsync(Guid tenantId, Guid connectionId, string platformCode, string payloadJson, string correlationId, ProductImportRetryPolicy.Entry entry, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var jobType = MarketplaceJobTypes.ForPlatform(platformCode, MarketplaceJobTypes.ProductSync);
        var dedup = ProductImportRetryPolicy.JobDedupKey(connectionId, entry.LookupKey, entry.Attempts);
        if (await db.IntegrationJobs.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.JobType == jobType && x.JobDedupKey == dedup, cancellationToken)) return;
        db.IntegrationJobs.Add(IntegrationJobFactory.New(new IntegrationJobRequest(tenantId, connectionId, jobType, ProductImportRetryPayload(payloadJson, connectionId, entry.LookupKey), dedup, correlationId, entry.NextAt.ToUniversalTime())
        {
            Priority = JobPriorityPolicy.For(jobType, userInitiated: false),
            TriggerType = "system",
            CreatedAt = now
        }));
    }

    internal static string ProductImportRetryPayload(string payloadJson, Guid connectionId, string lookupKey)
    {
        JsonObject payload;
        try { payload = JsonNode.Parse(payloadJson) as JsonObject ?? []; }
        catch (JsonException) { payload = []; }
        payload["connectionId"] = connectionId;
        payload["full"] = false;
        payload["productLookup"] = lookupKey;
        payload[ProductImportRetryPolicy.PayloadFlag] = true;
        return payload.ToJsonString();
    }

    // The code the marketplace looks the model up by: its productMainId. A snapshot without one cannot be retried alone.
    internal static string? RetryLookupKey(string snapshotJson)
    {
        try
        {
            var snapshot = JsonSerializer.Deserialize<RemoteCatalogProduct>(snapshotJson);
            return string.IsNullOrWhiteSpace(snapshot?.ProductMainId) ? null : snapshot.ProductMainId.Trim();
        }
        catch (JsonException) { return null; }
    }
}
