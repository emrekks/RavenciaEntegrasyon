using System.Globalization;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.Hepsiburada;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace MarketplaceHub.Infrastructure.Persistence;

// A connection that lacks a marketplace list it supports (ConnectionReferencePlan) gets one read queued: the category
// tree or brand list never read, or the fields and value lists of a mapped category that has none. Run by the worker
// at startup and with the hourly defaults pass; each gap is queued at most once a day (the dedup key carries the day),
// so a list the marketplace keeps refusing costs one read a day, not one an hour. Stale lists are not this pass's
// business: the scheduled REFERENCE_DATA policy refreshes the tree, and a mapping save or an activation the rest.
public static class ReferenceSnapshotBackfill
{
    public const string DedupPrefix = "reference-backfill:";

    public sealed record Gap(string ResourceType, string? CategoryExternalId);

    public static async Task<int> QueueMissingAsync(AppDbContext db, TimeProvider timeProvider, IConfiguration configuration, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var day = now.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var connections = await db.PlatformConnections.AsNoTracking()
            .Where(x => (x.Status == "ACTIVE" || x.Status == "VERIFIED")
                && (x.PlatformCode == "TRENDYOL" || x.PlatformCode == "SHOPIFY" || x.PlatformCode == "HEPSIBURADA" || x.PlatformCode == "N11" || x.PlatformCode == "PAZARAMA" || x.PlatformCode == "PTTAVM")
                && db.PlatformCredentials.Any(credential => credential.TenantId == x.TenantId && credential.ConnectionId == x.Id && credential.RevokedAt == null))
            .Select(x => new { x.TenantId, x.Id, x.PlatformCode, x.Environment })
            .ToListAsync(cancellationToken);
        if (connections.Count == 0) return 0;
        var ids = connections.Select(x => x.Id).ToArray();
        // A first import already reads these lists.
        var bootstrapping = (await db.IntegrationJobs.AsNoTracking()
            .Where(x => x.ConnectionId != null && ids.Contains(x.ConnectionId.Value)
                && x.JobDedupKey.StartsWith(MarketplaceJobTypes.ActivationBootstrapPrefix)
                && (x.Status == JobStatus.Pending || x.Status == JobStatus.Leased || x.Status == JobStatus.RetryScheduled))
            .Select(x => x.ConnectionId!.Value)
            .Distinct()
            .ToListAsync(cancellationToken)).ToHashSet();
        var productionCatalog = configuration[$"{HepsiburadaOptions.SectionName}:ProductionCatalogBaseAddress"];

        var added = 0;
        foreach (var connection in connections.Where(x => !bootstrapping.Contains(x.Id)))
        {
            var includeCatalog = connection.PlatformCode != "HEPSIBURADA" || MarketplaceConnectionService.ShouldBootstrapHepsiburadaCatalogReferences(connection.Environment, productionCatalog);
            var rootResources = ConnectionReferencePlan.RootResources(connection.PlatformCode, includeCatalog);
            if (rootResources.Count == 0) continue;
            var currentRoots = await db.ReferenceSnapshots.AsNoTracking()
                .Where(x => x.TenantId == connection.TenantId && x.ConnectionId == connection.Id && x.ScopeExternalId == "" && x.IsCurrent)
                .Select(x => x.ResourceType)
                .Distinct()
                .ToListAsync(cancellationToken);
            var hasCategories = currentRoots.Contains("CATEGORIES");
            var mappedCategories = hasCategories && ConnectionReferencePlan.SupportsCategoryAttributes(connection.PlatformCode)
                ? await ConnectionReferencePlan.CategoriesNeedingReferencesAsync(db, connection.TenantId, connection.Id, null, null, cancellationToken)
                : [];
            var jobType = MarketplaceJobTypes.ForPlatform(connection.PlatformCode, MarketplaceJobTypes.ReferenceSync);
            foreach (var gap in Gaps(connection.PlatformCode, rootResources, currentRoots, mappedCategories))
            {
                var dedup = ConnectionReferencePlan.Truncate(gap.CategoryExternalId is null
                    ? $"{DedupPrefix}{connection.Id:N}:{gap.ResourceType.ToLowerInvariant()}:{day}"
                    : $"{DedupPrefix}{connection.Id:N}:attributes:{gap.CategoryExternalId}:{day}");
                if (await db.IntegrationJobs.AsNoTracking().AnyAsync(x => x.TenantId == connection.TenantId && x.JobType == jobType && x.JobDedupKey == dedup, cancellationToken)) continue;
                if (await ConnectionReferencePlan.OpenReadExistsAsync(db, connection.TenantId, connection.Id, jobType, gap.ResourceType, gap.CategoryExternalId, cancellationToken)) continue;
                var payload = gap.CategoryExternalId is null
                    ? ConnectionReferencePlan.RootPayload(connection.Id, gap.ResourceType, gap.ResourceType == "CATEGORIES" && ConnectionReferencePlan.SupportsCategoryAttributes(connection.PlatformCode))
                    // Without a boundary only what is missing is read: the fields when there are none, the value lists that are absent.
                    : ConnectionReferencePlan.CategoryAttributesPayload(connection.Id, gap.CategoryExternalId, null);
                db.IntegrationJobs.Add(ConnectionReferencePlan.NewJob(connection.TenantId, connection.Id, jobType, dedup, payload, $"reference-backfill-{Guid.NewGuid():N}",
                    JobPriorityPolicy.For(jobType, userInitiated: false), "system", now));
                added++;
            }
        }

        if (added == 0) return 0;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsDedupRace(exception))
        {
            // Another worker queued the same day's reads first.
            db.ChangeTracker.Clear();
            return 0;
        }
        return added;
    }

    // The tree and brand list never read, then the mapped categories whose fields or value lists are missing (only
    // once the tree is there: a field read checks its category against the current tree).
    public static IReadOnlyList<Gap> Gaps(string platformCode, IReadOnlyList<string> rootResources, IReadOnlyCollection<string> currentRootResources, IReadOnlyList<string> mappedCategoriesMissingReferences)
    {
        var gaps = rootResources.Where(resource => !currentRootResources.Contains(resource)).Select(resource => new Gap(resource, null)).ToList();
        if (currentRootResources.Contains("CATEGORIES") && ConnectionReferencePlan.SupportsCategoryAttributes(platformCode))
            gaps.AddRange(mappedCategoriesMissingReferences.Select(category => new Gap("CATEGORY_ATTRIBUTES", category)));
        return gaps;
    }

    private static bool IsDedupRace(DbUpdateException exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
            if (current is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation }) return true;
        return false;
    }
}
