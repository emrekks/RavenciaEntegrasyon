using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// The marketplace lists a connection reads on activation, on a category mapping save and in the backfill pass
// (ConnectionReferencePlan, ReferenceSnapshotBackfill), against the real schema. Each test runs inside a rolled-back
// transaction.
[Collection("postgres-integration")]
public sealed class ConnectionReferencePostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    [PostgreSqlFact]
    public async Task TheTreeReadQueuesTheMappedCategoryAndItsFieldReadFetchesTheSelectableValueListsOnce()
    {
        var (tenant, connection) = NewConnection("ref-follow");
        var ports = new ScriptedReferences();
        ports.Answer("CATEGORIES", null, [new("CATEGORIES", "123", null, "Elbise", "Elbise", 0, true, true, "{}"), new("CATEGORIES", "999", null, "Gömlek", "Gömlek", 0, true, true, "{}")]);
        ports.Answer("CATEGORY_ATTRIBUTES", "123", [
            new("CATEGORY_ATTRIBUTES", "size", "123", "Beden", "Beden", 0, true, true, "{}", true, false, false),
            new("CATEGORY_ATTRIBUTES", "note", "123", "Not", "Not", 0, true, true, "{}", false, true, false),
            new("CATEGORY_ATTRIBUTES", "fabric", "123", "Kumaş", "Kumaş", 0, true, true, "{}", false, null, false)]);
        ports.Answer("ATTRIBUTE_VALUES", "123/size", [new("ATTRIBUTE_VALUES", "s", "123/size", "S", "S", 0, false, true, "{}")]);
        ports.Refuse("ATTRIBUTE_VALUES", "123/fabric");

        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        db.Tenants.Add(tenant);
        db.PlatformConnections.Add(connection);
        var oldTree = Snapshot(tenant.Id, connection.Id, "CATEGORIES", "", fixture.Now.AddDays(-3));
        db.ReferenceSnapshots.Add(oldTree);
        db.ReferenceItems.Add(Item(tenant.Id, connection.Id, oldTree.Id, "CATEGORIES", "123", null, isLeaf: true));
        db.CategoryMappings.Add(new CategoryMapping { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, SnapshotId = oldTree.Id, LocalId = Guid.CreateVersion7(), ExternalId = "123", Status = "VERIFIED", VerifiedAt = fixture.Now.AddDays(-3), Version = 1 });
        await db.SaveChangesAsync();

        var processor = new MarketplaceJobProcessor(db, null!, ports, null!, null!, null!, null!, null!, null!, null!, null!, new ConfigurationBuilder().Build(), fixture.TimeProvider);
        var tree = await processor.ProcessAsync(tenant.Id, connection.Id, MarketplaceJobTypes.ReferenceSync,
            ConnectionReferencePlan.RootPayload(connection.Id, "CATEGORIES", followMappedCategories: true), "ref-follow", CancellationToken.None);
        Assert.True(tree.Succeeded, tree.ErrorSummary);
        var queued = await db.IntegrationJobs.AsNoTracking().SingleAsync(x => x.TenantId == tenant.Id);
        Assert.Equal(MarketplaceJobTypes.ReferenceSync, queued.JobType);
        Assert.Equal(JobStatus.Pending, queued.Status);
        Assert.Contains("\"parentExternalId\":\"123\"", queued.PayloadJson);
        Assert.True(ConnectionReferencePlan.ReadFollowUp(queued.PayloadJson).IncludeValues);

        var fields = await processor.ProcessAsync(tenant.Id, connection.Id, queued.JobType, queued.PayloadJson, "ref-follow", CancellationToken.None);
        Assert.True(fields.Succeeded, fields.ErrorSummary);
        // The free-text field "Not" was never asked for; the refused "Kumaş" list was skipped, not fatal.
        Assert.Equal(["CATEGORIES:", "CATEGORY_ATTRIBUTES:123", "ATTRIBUTE_VALUES:123/fabric", "ATTRIBUTE_VALUES:123/size"], ports.Reads);
        Assert.True(await db.ReferenceSnapshots.AnyAsync(x => x.TenantId == tenant.Id && x.ResourceType == "ATTRIBUTE_VALUES" && x.ScopeExternalId == "123/size" && x.IsCurrent));

        // The same read again within the day: the fields and the "Beden" list are fresh, only the missing list is asked for.
        ports.Reads.Clear();
        var again = await processor.ProcessAsync(tenant.Id, connection.Id, queued.JobType, queued.PayloadJson, "ref-follow", CancellationToken.None);
        Assert.True(again.Succeeded, again.ErrorSummary);
        Assert.Equal(["ATTRIBUTE_VALUES:123/fabric"], ports.Reads);
        await transaction.RollbackAsync();
    }

    [PostgreSqlFact]
    public async Task AMappingSaveAndTheBackfillQueueEachMissingListOnce()
    {
        var (tenant, connection) = NewConnection("ref-backfill");
        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        db.Tenants.Add(tenant);
        db.PlatformConnections.Add(connection);
        db.PlatformCredentials.Add(new PlatformCredential { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, CredentialType = "BASIC", ProtectedPayload = "protected", MaskedHint = "***1", CreatedAt = fixture.Now, Version = 1 });
        var tree = Snapshot(tenant.Id, connection.Id, "CATEGORIES", "", fixture.Now.AddHours(-1));
        var fields = Snapshot(tenant.Id, connection.Id, "CATEGORY_ATTRIBUTES", "123", fixture.Now.AddHours(-1));
        db.ReferenceSnapshots.AddRange(tree, fields);
        db.ReferenceItems.AddRange(
            Item(tenant.Id, connection.Id, tree.Id, "CATEGORIES", "123", null, isLeaf: true),
            Item(tenant.Id, connection.Id, tree.Id, "CATEGORIES", "124", null, isLeaf: true),
            Item(tenant.Id, connection.Id, fields.Id, "CATEGORY_ATTRIBUTES", "size", "123", allowsCustomValue: false));
        db.CategoryMappings.AddRange(
            new CategoryMapping { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, SnapshotId = tree.Id, LocalId = Guid.CreateVersion7(), ExternalId = "123", Status = "VERIFIED", VerifiedAt = fixture.Now, Version = 1 },
            new CategoryMapping { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, SnapshotId = tree.Id, LocalId = Guid.CreateVersion7(), ExternalId = "124", Status = "VERIFIED", VerifiedAt = fixture.Now, Version = 1 });
        await db.SaveChangesAsync();

        // Saving the mapping of 123: its "Beden" value list is missing, so the field read is queued once.
        Assert.True(await ConnectionReferencePlan.QueueForSavedCategoryMappingAsync(db, tenant.Id, connection.Id, "123", "ref-backfill", fixture.Now, CancellationToken.None));
        await db.SaveChangesAsync();
        Assert.False(await ConnectionReferencePlan.QueueForSavedCategoryMappingAsync(db, tenant.Id, connection.Id, "123", "ref-backfill", fixture.Now, CancellationToken.None));

        // The backfill adds the brand list never read and the fields of 124; 123 already has an open read.
        await ReferenceSnapshotBackfill.QueueMissingAsync(db, fixture.TimeProvider, new ConfigurationBuilder().Build(), CancellationToken.None);
        var jobs = await db.IntegrationJobs.AsNoTracking().Where(x => x.TenantId == tenant.Id).OrderBy(x => x.JobDedupKey).ToListAsync();
        Assert.Equal(3, jobs.Count);
        Assert.Single(jobs, x => x.PayloadJson.Contains("\"resourceType\":\"BRANDS\"") && x.JobDedupKey.StartsWith(ReferenceSnapshotBackfill.DedupPrefix));
        Assert.Single(jobs, x => x.PayloadJson.Contains("\"parentExternalId\":\"124\"") && x.JobDedupKey.StartsWith(ReferenceSnapshotBackfill.DedupPrefix));
        Assert.Single(jobs, x => x.PayloadJson.Contains("\"parentExternalId\":\"123\"") && x.JobDedupKey.StartsWith("mapping-reference:") && x.TriggerType == "user");

        // A second pass the same day adds nothing.
        await ReferenceSnapshotBackfill.QueueMissingAsync(db, fixture.TimeProvider, new ConfigurationBuilder().Build(), CancellationToken.None);
        Assert.Equal(3, await db.IntegrationJobs.AsNoTracking().CountAsync(x => x.TenantId == tenant.Id));
        await transaction.RollbackAsync();
    }

    private static (Tenant Tenant, PlatformConnection Connection) NewConnection(string code)
    {
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"{code}-{Guid.NewGuid():N}", DisplayName = code, Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, Version = 1 };
        var connection = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = "TRENDYOL", Environment = "PRODUCTION", DisplayName = "Referans testi", ExternalStoreId = $"store-{Guid.NewGuid():N}", Status = "ACTIVE", ApiVersion = "V1", SettingsJson = "{}" };
        return (tenant, connection);
    }

    private static ReferenceSnapshot Snapshot(Guid tenantId, Guid connectionId, string resourceType, string scope, DateTimeOffset fetchedAt) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        ConnectionId = connectionId,
        ResourceType = resourceType,
        ScopeExternalId = scope,
        SourceVersion = "V1",
        ContentHash = Guid.NewGuid().ToString("N"),
        FetchedAt = fetchedAt,
        IsCurrent = true,
        ItemCount = 1
    };

    private static ReferenceItem Item(Guid tenantId, Guid connectionId, Guid snapshotId, string resourceType, string externalId, string? parent, bool isLeaf = false, bool? allowsCustomValue = null) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        ConnectionId = connectionId,
        SnapshotId = snapshotId,
        ResourceType = resourceType,
        ExternalId = externalId,
        ParentExternalId = parent,
        Name = externalId,
        NormalizedName = externalId.ToUpperInvariant(),
        Path = externalId,
        IsLeaf = isLeaf,
        IsActive = true,
        AllowsCustomValue = allowsCustomValue,
        PayloadHash = Guid.NewGuid().ToString("N"),
        SortOrder = 0
    };

    private sealed class ScriptedReferences : IReferenceDataPort
    {
        private readonly Dictionary<string, IReadOnlyList<RemoteReferenceItem>> answers = [];
        private readonly HashSet<string> refused = [];
        public List<string> Reads { get; } = [];

        public void Answer(string resourceType, string? scope, IReadOnlyList<RemoteReferenceItem> items) => answers[$"{resourceType}:{scope}"] = items;
        public void Refuse(string resourceType, string scope) => refused.Add($"{resourceType}:{scope}");

        public Task<AdapterResult<AdapterPageResult<RemoteReferenceItem>>> ReadAsync(AdapterContext context, ReferenceResource resource, AdapterPageRequest page, CancellationToken cancellationToken)
        {
            var key = $"{resource.ResourceType}:{resource.ParentExternalId}";
            Reads.Add(key);
            if (refused.Contains(key))
                return Task.FromResult(AdapterResult<AdapterPageResult<RemoteReferenceItem>>.Failure(new(AdapterErrorClass.Validation, "HEPSIBURADA_REFERENCE_ATTRIBUTE_NOT_ENUM", "Bu özellik için değer listesi yok.", 400, null, null)));
            if (!answers.TryGetValue(key, out var items)) throw new InvalidOperationException($"Unexpected reference read: {key}.");
            return Task.FromResult(AdapterResult<AdapterPageResult<RemoteReferenceItem>>.Success(new(items, null, false, items.Count)));
        }
    }
}
