using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

// The reference reads that continue after their own list (ConnectionReferencePlan): a category tree read for an
// activation queues the field lists of the connection's mapped categories, and a field list read with includeValues
// reads the value lists of its selectable fields in the same run.
public sealed partial class MarketplaceJobProcessor
{
    private async Task<bool> SyncReferenceJob(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, Guid? jobId, CancellationToken cancellationToken)
    {
        var followUp = ConnectionReferencePlan.ReadFollowUp(payloadJson);
        var (resourceType, parentExternalId) = ReferenceResource(payloadJson);
        var fieldsWithValues = followUp.IncludeValues && resourceType == "CATEGORY_ATTRIBUTES" && parentExternalId is not null;
        // A field list read less than a day ago (or, without a boundary, read at all) is not read again; its value lists still are.
        var fieldsFresh = fieldsWithValues && ConnectionReferencePlan.IsFresh(await CurrentReferenceFetchedAtAsync(tenantId, connectionId, resourceType, parentExternalId!, cancellationToken), followUp.RefreshBefore);
        if (!fieldsFresh && !await SyncReferences(tenantId, connectionId, payloadJson, correlationId, cancellationToken)) return false;
        if (resourceType == "CATEGORIES" && followUp.FollowMappedCategories)
            await QueueMappedCategoryReferencesAsync(tenantId, connectionId, correlationId, jobId, cancellationToken);
        if (fieldsWithValues)
            await SyncCategoryValueListsAsync(tenantId, connectionId, parentExternalId!, followUp.RefreshBefore, correlationId, cancellationToken);
        return true;
    }

    private Task<DateTimeOffset?> CurrentReferenceFetchedAtAsync(Guid tenantId, Guid connectionId, string resourceType, string scope, CancellationToken cancellationToken) =>
        db.ReferenceSnapshots.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == resourceType && x.ScopeExternalId == scope && x.IsCurrent)
            .OrderByDescending(x => x.FetchedAt)
            .Select(x => (DateTimeOffset?)x.FetchedAt)
            .FirstOrDefaultAsync(cancellationToken);

    // One field-list job per mapped category, in the same run as the tree read (an activation's jobs keep its prefix,
    // so they count as part of the first import and keep its priority). A retry of the tree read adds none twice.
    private async Task QueueMappedCategoryReferencesAsync(Guid tenantId, Guid connectionId, string correlationId, Guid? jobId, CancellationToken cancellationToken)
    {
        var platformCode = await db.PlatformConnections.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == connectionId).Select(x => x.PlatformCode).SingleOrDefaultAsync(cancellationToken);
        if (platformCode is null || !ConnectionReferencePlan.SupportsCategoryAttributes(platformCode)) return;
        var now = timeProvider.GetUtcNow();
        var refreshBefore = now - ConnectionReferencePlan.FreshFor;
        var categories = await ConnectionReferencePlan.CategoriesNeedingReferencesAsync(db, tenantId, connectionId, null, refreshBefore, cancellationToken);
        if (categories.Count == 0) return;

        var current = jobId is { } id
            ? await db.IntegrationJobs.AsNoTracking().Where(x => x.Id == id).Select(x => new { x.JobDedupKey, x.Priority, x.TriggerType }).SingleOrDefaultAsync(cancellationToken)
            : null;
        var dedupPrefix = current?.JobDedupKey ?? $"reference-follow-up:{connectionId:N}:{now.ToUnixTimeMilliseconds()}";
        var priority = current?.Priority ?? JobPriorityPolicy.For(MarketplaceJobTypes.ReferenceSync, userInitiated: false);
        var trigger = current?.TriggerType ?? "system";
        var jobType = MarketplaceJobTypes.ForPlatform(platformCode, MarketplaceJobTypes.ReferenceSync);
        var dedups = categories.ToDictionary(category => category, category => ConnectionReferencePlan.Truncate($"{dedupPrefix}:attributes:{category}"), StringComparer.Ordinal);
        var keys = dedups.Values.ToArray();
        var existing = await db.IntegrationJobs.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.JobType == jobType && keys.Contains(x.JobDedupKey))
            .Select(x => x.JobDedupKey)
            .ToListAsync(cancellationToken);
        var added = 0;
        foreach (var category in categories)
        {
            if (existing.Contains(dedups[category], StringComparer.Ordinal)) continue;
            if (await ConnectionReferencePlan.OpenReadExistsAsync(db, tenantId, connectionId, jobType, "CATEGORY_ATTRIBUTES", category, cancellationToken)) continue;
            db.IntegrationJobs.Add(ConnectionReferencePlan.NewJob(tenantId, connectionId, jobType, dedups[category],
                ConnectionReferencePlan.CategoryAttributesPayload(connectionId, category, refreshBefore), correlationId, priority, trigger, now));
            added++;
        }
        if (added > 0) await db.SaveChangesAsync(cancellationToken);
    }

    // The value lists of a category's selectable fields, one marketplace read each (paged where the marketplace pages).
    // A list the marketplace refuses for this field (Hepsiburada: not an enum field) is skipped; a rate limit, an
    // outage or a credential problem ends the run, which comes back later and skips the lists it already has.
    private async Task SyncCategoryValueListsAsync(Guid tenantId, Guid connectionId, string categoryExternalId, DateTimeOffset? refreshBefore, string correlationId, CancellationToken cancellationToken)
    {
        var fieldSnapshotId = await db.ReferenceSnapshots.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == "CATEGORY_ATTRIBUTES" && x.ScopeExternalId == categoryExternalId && x.IsCurrent)
            .OrderByDescending(x => x.FetchedAt)
            .Select(x => (Guid?)x.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (fieldSnapshotId is null) return;
        var fields = (await db.ReferenceItems.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.SnapshotId == fieldSnapshotId && x.ResourceType == "CATEGORY_ATTRIBUTES")
                .OrderBy(x => x.SortOrder).ThenBy(x => x.ExternalId)
                .Select(x => new { x.ExternalId, x.IsActive, x.AllowsCustomValue })
                .ToListAsync(cancellationToken))
            .Where(x => ConnectionReferencePlan.NeedsValueList(x.IsActive, x.AllowsCustomValue))
            .Select(x => $"{categoryExternalId}/{x.ExternalId}")
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (fields.Count == 0) return;
        var fetched = (await db.ReferenceSnapshots.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == "ATTRIBUTE_VALUES" && x.IsCurrent && fields.Contains(x.ScopeExternalId))
                .Select(x => new { x.ScopeExternalId, x.FetchedAt })
                .ToListAsync(cancellationToken))
            .GroupBy(x => x.ScopeExternalId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Max(x => x.FetchedAt), StringComparer.Ordinal);

        foreach (var scope in fields)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ConnectionReferencePlan.IsFresh(fetched.TryGetValue(scope, out var at) ? at : null, refreshBefore)) continue;
            try
            {
                await SyncReferences(tenantId, connectionId, JsonSerializer.Serialize(new { connectionId, resourceType = "ATTRIBUTE_VALUES", parentExternalId = scope }), correlationId, cancellationToken);
            }
            catch (JobProcessingException exception) when (ConnectionReferencePlan.SkipsValueListFailure(exception.Result))
            {
                telemetryFailedCount++;
            }
            finally
            {
                // A long category keeps the change tracker small: the saved lists are not needed again in this run.
                foreach (var entry in db.ChangeTracker.Entries().Where(x => x.Entity is ReferenceItem or ReferenceSnapshot && x.State == EntityState.Unchanged).ToList())
                    entry.State = EntityState.Detached;
            }
        }
    }
}
