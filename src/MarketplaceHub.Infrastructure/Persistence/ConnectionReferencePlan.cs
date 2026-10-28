using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

// Which marketplace lists a connection keeps a local copy of (reference snapshots), and the read-only jobs that
// fetch them. The category tree (and the brand list where the marketplace has one) is read whole; field lists
// (CATEGORY_ATTRIBUTES) and their value lists (ATTRIBUTE_VALUES) only for the categories this tenant mapped on the
// connection: reading them for the whole tree would cost thousands of calls (Trendyol has ~3,900 categories).
public static class ConnectionReferencePlan
{
    // Stale after a day, like EnsureReferenceSnapshot: a newer copy is not read again.
    public static readonly TimeSpan FreshFor = TimeSpan.FromHours(24);

    // n11 has no brand list (the brand is the category field "Marka"); Hepsiburada, PTT AVM and Shopify none either.
    public static bool SupportsBrands(string platformCode) => platformCode is "TRENDYOL" or "PAZARAMA";

    // PTT AVM answers an empty field list without a call and Shopify refuses one, so nothing is queued for them.
    public static bool SupportsCategoryAttributes(string platformCode) => platformCode is "TRENDYOL" or "HEPSIBURADA" or "N11" or "PAZARAMA";

    // The lists read whole. Hepsiburada's catalog is read only where its catalog address is confirmed.
    public static IReadOnlyList<string> RootResources(string platformCode, bool includeHepsiburadaCatalog)
    {
        if (!ActiveIntegrationScope.IsMarketplace(platformCode)) return [];
        if (platformCode == "HEPSIBURADA" && !includeHepsiburadaCatalog) return [];
        return SupportsBrands(platformCode) ? ["CATEGORIES", "BRANDS"] : ["CATEGORIES"];
    }

    // A field the panel offers a value list for: the mapping page reads the list of every field that is not free
    // text (allowsCustomValue !== true), so those are the lists fetched ahead.
    public static bool NeedsValueList(bool isActive, bool? allowsCustomValue) => isActive && allowsCustomValue != true;

    // A value list the marketplace refuses for one field is skipped; a rate limit, an outage or a credential
    // problem (retry, or a connection back-off) ends the run instead.
    public static bool SkipsValueListFailure(JobExecutionResult result) => result.Kind != JobCompletionKind.Retry && !result.BackOffConnection;

    // A copy is fresh when it exists and, with a refresh boundary, was read at or after it.
    public static bool IsFresh(DateTimeOffset? fetchedAt, DateTimeOffset? refreshBefore) =>
        fetchedAt is { } at && (refreshBefore is null || at >= refreshBefore.Value);

    public static string CategoryAttributesPayload(Guid connectionId, string categoryExternalId, DateTimeOffset? refreshBefore) =>
        JsonSerializer.Serialize(new { connectionId, resourceType = "CATEGORY_ATTRIBUTES", parentExternalId = categoryExternalId, includeValues = true, refreshBefore = refreshBefore?.ToUniversalTime() });

    public static string RootPayload(Guid connectionId, string resourceType, bool followMappedCategories) => followMappedCategories
        ? JsonSerializer.Serialize(new { connectionId, resourceType, parentExternalId = (string?)null, followMappedCategories = true })
        : JsonSerializer.Serialize(new { connectionId, resourceType, parentExternalId = (string?)null });

    public sealed record FollowUp(bool FollowMappedCategories, bool IncludeValues, DateTimeOffset? RefreshBefore);

    public static FollowUp ReadFollowUp(string payloadJson)
    {
        try
        {
            using var payload = JsonDocument.Parse(payloadJson);
            var root = payload.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new(false, false, null);
            var follow = root.TryGetProperty("followMappedCategories", out var followValue) && followValue.ValueKind == JsonValueKind.True;
            var values = root.TryGetProperty("includeValues", out var valuesValue) && valuesValue.ValueKind == JsonValueKind.True;
            DateTimeOffset? refreshBefore = root.TryGetProperty("refreshBefore", out var refreshValue) && refreshValue.ValueKind == JsonValueKind.String && refreshValue.TryGetDateTimeOffset(out var parsed)
                ? parsed.ToUniversalTime()
                : null;
            return new(follow, values, refreshBefore);
        }
        catch (JsonException) { return new(false, false, null); }
    }

    // The mapped categories of a connection (one per remote category) whose fields or value lists are missing or,
    // with a refresh boundary, older than it.
    public static async Task<IReadOnlyList<string>> CategoriesNeedingReferencesAsync(AppDbContext db, Guid tenantId, Guid connectionId, IReadOnlyCollection<string>? onlyCategories, DateTimeOffset? refreshBefore, CancellationToken cancellationToken)
    {
        var mappedQuery = db.CategoryMappings.AsNoTracking().Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.Status == "VERIFIED");
        if (onlyCategories is not null) mappedQuery = mappedQuery.Where(x => onlyCategories.Contains(x.ExternalId));
        var mapped = (await mappedQuery.Select(x => x.ExternalId).Distinct().ToListAsync(cancellationToken))
            .Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
        if (mapped.Count == 0) return [];
        // A field read is refused for a category that is no longer an active leaf of the current tree; none is queued.
        mapped = (await db.ReferenceItems.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == "CATEGORIES" && x.IsLeaf && x.IsActive && mapped.Contains(x.ExternalId)
                    && db.ReferenceSnapshots.Any(snapshot => snapshot.TenantId == tenantId && snapshot.Id == x.SnapshotId && snapshot.IsCurrent && snapshot.ScopeExternalId == ""))
                .Select(x => x.ExternalId)
                .Distinct()
                .ToListAsync(cancellationToken))
            .Order(StringComparer.Ordinal).ToList();
        if (mapped.Count == 0) return [];

        var attributeSnapshots = await db.ReferenceSnapshots.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == "CATEGORY_ATTRIBUTES" && x.IsCurrent && mapped.Contains(x.ScopeExternalId))
            .Select(x => new { x.Id, x.ScopeExternalId, x.FetchedAt })
            .ToListAsync(cancellationToken);
        var freshAttributeSnapshots = attributeSnapshots
            .GroupBy(x => x.ScopeExternalId, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(x => x.FetchedAt).First())
            .Where(x => IsFresh(x.FetchedAt, refreshBefore))
            .ToDictionary(x => x.ScopeExternalId, x => x.Id, StringComparer.Ordinal);
        var snapshotIds = freshAttributeSnapshots.Values.ToArray();
        var valueFields = await db.ReferenceItems.AsNoTracking()
            .Where(x => x.TenantId == tenantId && snapshotIds.Contains(x.SnapshotId) && x.ResourceType == "CATEGORY_ATTRIBUTES" && x.IsActive && x.AllowsCustomValue != true)
            .Select(x => new { x.ParentExternalId, x.ExternalId })
            .ToListAsync(cancellationToken);
        var valueScopes = valueFields.Where(x => x.ParentExternalId is not null).Select(x => $"{x.ParentExternalId}/{x.ExternalId}").Distinct(StringComparer.Ordinal).ToArray();
        var valueSnapshots = await db.ReferenceSnapshots.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == "ATTRIBUTE_VALUES" && x.IsCurrent && valueScopes.Contains(x.ScopeExternalId))
            .Select(x => new { x.ScopeExternalId, x.FetchedAt })
            .ToListAsync(cancellationToken);
        var freshValueScopes = valueSnapshots.Where(x => IsFresh(x.FetchedAt, refreshBefore)).Select(x => x.ScopeExternalId).ToHashSet(StringComparer.Ordinal);
        var categoriesMissingValues = valueScopes.Where(scope => !freshValueScopes.Contains(scope)).Select(scope => scope[..scope.IndexOf('/', StringComparison.Ordinal)]).ToHashSet(StringComparer.Ordinal);
        return mapped.Where(category => !freshAttributeSnapshots.ContainsKey(category) || categoriesMissingValues.Contains(category)).ToList();
    }

    // Whether a reference read of the connection for this resource (and scope) is already waiting or running.
    public static Task<bool> OpenReadExistsAsync(AppDbContext db, Guid tenantId, Guid connectionId, string jobType, string resourceType, string? parentExternalId, CancellationToken cancellationToken)
    {
        var resourceFragment = $"\"resourceType\":\"{resourceType}\"";
        var query = db.IntegrationJobs.AsNoTracking().Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.JobType == jobType
            && (x.Status == JobStatus.Pending || x.Status == JobStatus.Leased || x.Status == JobStatus.RetryScheduled)
            && x.PayloadJson.Contains(resourceFragment));
        if (parentExternalId is not null)
        {
            var parentFragment = $"\"parentExternalId\":{JsonSerializer.Serialize(parentExternalId)}";
            query = query.Where(x => x.PayloadJson.Contains(parentFragment));
        }
        return query.AnyAsync(cancellationToken);
    }

    public static IntegrationJob NewJob(Guid tenantId, Guid connectionId, string jobType, string dedup, string payload, string correlationId, int priority, string triggerType, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        ConnectionId = connectionId,
        JobType = jobType,
        PayloadJson = payload,
        PayloadVersion = 1,
        PayloadHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))),
        JobDedupKey = dedup,
        EffectIdempotencyKey = dedup,
        Priority = priority,
        TriggerType = triggerType,
        AvailableAt = now,
        CreatedAt = now,
        CorrelationId = correlationId,
        Version = 1
    };

    // A category mapping saved on the panel: its fields and their value lists are read in the background unless a
    // copy younger than a day is already there. Not saved here; the caller saves with its own changes.
    public static async Task<bool> QueueForSavedCategoryMappingAsync(AppDbContext db, Guid tenantId, Guid connectionId, string categoryExternalId, string correlationId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var connection = await db.PlatformConnections.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.Id == connectionId)
            .Select(x => new { x.PlatformCode, x.Status })
            .SingleOrDefaultAsync(cancellationToken);
        if (connection is null || connection.Status is not ("ACTIVE" or "VERIFIED") || !SupportsCategoryAttributes(connection.PlatformCode)) return false;
        var refreshBefore = now - FreshFor;
        if ((await CategoriesNeedingReferencesAsync(db, tenantId, connectionId, [categoryExternalId], refreshBefore, cancellationToken)).Count == 0) return false;
        var jobType = MarketplaceJobTypes.ForPlatform(connection.PlatformCode, MarketplaceJobTypes.ReferenceSync);
        if (await OpenReadExistsAsync(db, tenantId, connectionId, jobType, "CATEGORY_ATTRIBUTES", categoryExternalId, cancellationToken)) return false;
        var dedup = Truncate($"mapping-reference:{connectionId:N}:{categoryExternalId}:{now.ToUnixTimeMilliseconds()}");
        db.IntegrationJobs.Add(NewJob(tenantId, connectionId, jobType, dedup, CategoryAttributesPayload(connectionId, categoryExternalId, refreshBefore), correlationId,
            JobPriorityPolicy.ForUser(jobType), "user", now));
        return true;
    }

    internal static string Truncate(string dedup) => dedup.Length <= 256 ? dedup : dedup[..192] + ":" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dedup)))[..32];
}
