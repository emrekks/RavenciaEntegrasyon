using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// The product editor's publish check reads one category of the list and every field's value list and value mappings
// in one request each, instead of the whole category tree and one request per field. The batch reads answer what the
// single reads answer, for the tenant's own connection only.
[Collection("postgres-integration")]
public sealed class ReferenceBatchReadPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    [PostgreSqlFact]
    public async Task BatchReads_AnswerWhatTheSingleReadsAnswer()
    {
        var seed = await SeedAsync();
        await using var db = seed.Db;
        var service = new ReferenceDataService(db, fixture.TimeProvider);
        var ct = CancellationToken.None;

        // Value lists: each field as the single read gives it; a field whose list was never read is left out.
        var references = await service.ListAttributeValueReferencesAsync(seed.Tenant.Id, seed.Connection.Id, "411", ["20", " 30 ", "40", "20"], ct);
        Assert.True(references.Succeeded, references.Error?.Message);
        Assert.Equal(["20", "30"], references.Value!.Keys.OrderBy(x => x, StringComparer.Ordinal));
        foreach (var field in new[] { "20", "30" })
        {
            var single = await service.ListAsync(seed.Tenant.Id, seed.Connection.Id, "ATTRIBUTE_VALUES", $"411/{field}", ct);
            Assert.True(single.Succeeded, single.Error?.Message);
            var batch = references.Value[field];
            Assert.Equal(single.Value!.SnapshotId, batch.SnapshotId);
            Assert.Equal(single.Value.ResourceType, batch.ResourceType);
            Assert.Equal(single.Value.FetchedAt, batch.FetchedAt);
            Assert.Equal(single.Value.Items, batch.Items);
        }
        Assert.Equal(seed.CurrentRenk.Id, references.Value["20"].SnapshotId);
        Assert.Equal(["301", "302", "303"], references.Value["20"].Items.Select(x => x.ExternalId));
        Assert.Equal("REFERENCE_SNAPSHOT_UNAVAILABLE", (await service.ListAsync(seed.Tenant.Id, seed.Connection.Id, "ATTRIBUTE_VALUES", "411/40", ct)).Error?.Code);

        // Value mappings: every asked field, an empty list when it holds none; another category's scope stays out.
        var mappings = await service.ListAttributeValueMappingsAsync(seed.Tenant.Id, seed.Connection.Id, "411", ["20", "30", "40"], ct);
        Assert.True(mappings.Succeeded, mappings.Error?.Message);
        Assert.Equal(["20", "30", "40"], mappings.Value!.Keys.OrderBy(x => x, StringComparer.Ordinal));
        foreach (var field in new[] { "20", "30", "40" })
        {
            var single = await service.ListMappingsAsync(seed.Tenant.Id, "attribute-values", seed.Connection.Id, $"411/{field}", ct);
            Assert.True(single.Succeeded, single.Error?.Message);
            Assert.Equal(single.Value!, mappings.Value[field]);
        }
        Assert.Equal(2, mappings.Value["20"].Count);
        Assert.Single(mappings.Value["30"]);
        Assert.Empty(mappings.Value["40"]);

        // One category of the list: the list's snapshot id with that row only, or no row for an id the list lacks.
        var whole = await service.ListAsync(seed.Tenant.Id, seed.Connection.Id, "CATEGORIES", null, ct);
        var one = await service.FindCategoryAsync(seed.Tenant.Id, seed.Connection.Id, "411", ct);
        Assert.True(one.Succeeded, one.Error?.Message);
        Assert.Equal(whole.Value!.SnapshotId, one.Value!.SnapshotId);
        Assert.Equal(whole.Value.Items.Where(x => x.ExternalId == "411"), one.Value.Items);
        var unknown = await service.FindCategoryAsync(seed.Tenant.Id, seed.Connection.Id, "999", ct);
        Assert.Equal(whole.Value.SnapshotId, unknown.Value!.SnapshotId);
        Assert.Empty(unknown.Value.Items);
        await seed.Transaction.RollbackAsync();
    }

    [PostgreSqlFact]
    public async Task BatchReads_StayInTheTenantAndRefuseOversizedRequests()
    {
        var seed = await SeedAsync();
        await using var db = seed.Db;
        var service = new ReferenceDataService(db, fixture.TimeProvider);
        var ct = CancellationToken.None;

        // Another tenant cannot read this tenant's connection.
        Assert.Equal("RESOURCE_NOT_FOUND", (await service.ListAttributeValueReferencesAsync(seed.OtherTenant.Id, seed.Connection.Id, "411", ["20"], ct)).Error?.Code);
        Assert.Equal("RESOURCE_NOT_FOUND", (await service.ListAttributeValueMappingsAsync(seed.OtherTenant.Id, seed.Connection.Id, "411", ["20"], ct)).Error?.Code);
        Assert.Equal("RESOURCE_NOT_FOUND", (await service.FindCategoryAsync(seed.OtherTenant.Id, seed.Connection.Id, "411", ct)).Error?.Code);

        // Its own connection shows its own rows only.
        var otherReferences = await service.ListAttributeValueReferencesAsync(seed.OtherTenant.Id, seed.OtherConnection.Id, "411", ["20"], ct);
        Assert.Equal(["901"], otherReferences.Value!["20"].Items.Select(x => x.ExternalId));
        var otherMappings = await service.ListAttributeValueMappingsAsync(seed.OtherTenant.Id, seed.OtherConnection.Id, "411", ["20"], ct);
        Assert.Equal(["901"], otherMappings.Value!["20"].Select(x => x.ExternalId));

        // Unknown fields are an empty answer, not an error; no fields at all too.
        Assert.Empty((await service.ListAttributeValueReferencesAsync(seed.Tenant.Id, seed.Connection.Id, "411", ["nope"], ct)).Value!);
        Assert.Empty((await service.ListAttributeValueMappingsAsync(seed.Tenant.Id, seed.Connection.Id, "411", [], ct)).Value!);

        // Limits.
        var tooMany = Enumerable.Range(0, ReferenceDataService.MaximumBatchAttributeIds + 1).Select(x => $"{x}").ToArray();
        Assert.Equal("VALIDATION_FAILED", (await service.ListAttributeValueReferencesAsync(seed.Tenant.Id, seed.Connection.Id, "411", tooMany, ct)).Error?.Code);
        Assert.Equal("VALIDATION_FAILED", (await service.ListAttributeValueMappingsAsync(seed.Tenant.Id, seed.Connection.Id, "411", tooMany, ct)).Error?.Code);
        Assert.Equal("VALIDATION_FAILED", (await service.ListAttributeValueMappingsAsync(seed.Tenant.Id, seed.Connection.Id, " ", ["20"], ct)).Error?.Code);
        Assert.Equal("VALIDATION_FAILED", (await service.ListAttributeValueReferencesAsync(seed.Tenant.Id, seed.Connection.Id, "411", [new string('x', 257)], ct)).Error?.Code);
        await seed.Transaction.RollbackAsync();
    }

    private sealed record Seed(AppDbContext Db, Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction Transaction, Tenant Tenant, Tenant OtherTenant, PlatformConnection Connection, PlatformConnection OtherConnection, ReferenceSnapshot CurrentRenk);

    private async Task<Seed> SeedAsync()
    {
        var tenant = NewTenant("ref-batch");
        var otherTenant = NewTenant("ref-batch-other");
        var connection = NewConnection(tenant.Id, "ref-batch");
        var otherConnection = NewConnection(otherTenant.Id, "ref-batch-other");
        var categories = NewSnapshot(tenant.Id, connection.Id, "CATEGORIES", "", fixture.Now.AddHours(-1));
        var oldRenk = NewSnapshot(tenant.Id, connection.Id, "ATTRIBUTE_VALUES", "411/20", fixture.Now.AddDays(-2), isCurrent: false);
        var renk = NewSnapshot(tenant.Id, connection.Id, "ATTRIBUTE_VALUES", "411/20", fixture.Now.AddHours(-1));
        var beden = NewSnapshot(tenant.Id, connection.Id, "ATTRIBUTE_VALUES", "411/30", fixture.Now.AddHours(-1));
        var otherCategoryRenk = NewSnapshot(tenant.Id, connection.Id, "ATTRIBUTE_VALUES", "412/20", fixture.Now.AddHours(-1));
        var otherRenk = NewSnapshot(otherTenant.Id, otherConnection.Id, "ATTRIBUTE_VALUES", "411/20", fixture.Now.AddHours(-1));

        var db = fixture.CreateContext();
        var transaction = await db.Database.BeginTransactionAsync();
        db.Tenants.AddRange(tenant, otherTenant);
        db.PlatformConnections.AddRange(connection, otherConnection);
        db.ReferenceSnapshots.AddRange(categories, oldRenk, renk, beden, otherCategoryRenk, otherRenk);
        db.ReferenceItems.AddRange(
            Item(categories, "CATEGORIES", "400", "Giyim", null, isLeaf: false),
            Item(categories, "CATEGORIES", "411", "Elbise", "400"),
            Item(categories, "CATEGORIES", "412", "Bluz", "400"),
            Item(oldRenk, "ATTRIBUTE_VALUES", "300", "Eski", "411/20"),
            Item(renk, "ATTRIBUTE_VALUES", "303", "Siyah", "411/20", sortOrder: 3),
            Item(renk, "ATTRIBUTE_VALUES", "301", "Kırmızı", "411/20", sortOrder: 1),
            Item(renk, "ATTRIBUTE_VALUES", "302", "Mavi", "411/20", sortOrder: 2, isActive: false),
            Item(beden, "ATTRIBUTE_VALUES", "501", "S", "411/30"),
            Item(otherCategoryRenk, "ATTRIBUTE_VALUES", "601", "Yeşil", "412/20"),
            Item(otherRenk, "ATTRIBUTE_VALUES", "901", "Renk B", "411/20"));

        var color = new AttributeDefinition { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Code = $"RENK-{Guid.NewGuid():N}", Name = "Renk", DataType = AttributeDataType.Text, CreatedAt = fixture.Now, UpdatedAt = fixture.Now, Version = 1 };
        var otherColor = new AttributeDefinition { Id = Guid.CreateVersion7(), TenantId = otherTenant.Id, Code = $"RENK-{Guid.NewGuid():N}", Name = "Renk", DataType = AttributeDataType.Text, CreatedAt = fixture.Now, UpdatedAt = fixture.Now, Version = 1 };
        AttributeValue Value(AttributeDefinition attribute, string value) => new() { Id = Guid.CreateVersion7(), TenantId = attribute.TenantId, AttributeId = attribute.Id, Value = value, NormalizedValue = value.ToUpperInvariant(), SortOrder = 0, IsActive = true, Version = 1 };
        var red = Value(color, "Kırmızı");
        var black = Value(color, "Siyah");
        var small = Value(color, "S");
        var otherRed = Value(otherColor, "Kırmızı");
        db.AttributeDefinitions.AddRange(color, otherColor);
        db.AttributeValues.AddRange(red, black, small, otherRed);
        AttributeValueMapping Mapping(PlatformConnection owner, ReferenceSnapshot snapshot, AttributeValue local, string scope, string externalId) => new() { Id = Guid.CreateVersion7(), TenantId = owner.TenantId, ConnectionId = owner.Id, SnapshotId = snapshot.Id, LocalId = local.Id, ScopeExternalId = scope, ExternalId = externalId, Status = "VERIFIED", VerifiedAt = fixture.Now, Version = 1 };
        db.AttributeValueMappings.AddRange(
            Mapping(connection, renk, black, "411/20", "303"),
            Mapping(connection, renk, red, "411/20", "301"),
            Mapping(connection, beden, small, "411/30", "501"),
            Mapping(connection, otherCategoryRenk, red, "412/20", "601"),
            Mapping(otherConnection, otherRenk, otherRed, "411/20", "901"));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return new(db, transaction, tenant, otherTenant, connection, otherConnection, renk);
    }

    private static ReferenceItem Item(ReferenceSnapshot snapshot, string resourceType, string externalId, string name, string? parent, bool isLeaf = true, bool isActive = true, int? sortOrder = null) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = snapshot.TenantId,
        ConnectionId = snapshot.ConnectionId,
        SnapshotId = snapshot.Id,
        ResourceType = resourceType,
        ExternalId = externalId,
        ParentExternalId = parent,
        Name = name,
        NormalizedName = name.ToUpperInvariant(),
        Path = name,
        IsLeaf = isLeaf,
        IsActive = isActive,
        PayloadHash = $"hash-{externalId}",
        SortOrder = sortOrder
    };

    private static Tenant NewTenant(string code) => new()
    {
        Id = Guid.CreateVersion7(),
        Code = $"{code}-{Guid.NewGuid():N}",
        DisplayName = code,
        Status = RecordStatus.Active,
        Timezone = "Europe/Istanbul",
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        Version = 1
    };

    private static PlatformConnection NewConnection(Guid tenantId, string store) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        PublicId = Guid.CreateVersion7(),
        PlatformCode = "TRENDYOL",
        Environment = "PRODUCTION",
        DisplayName = store,
        ExternalStoreId = $"{store}-{Guid.NewGuid():N}"[..40],
        Status = "ACTIVE",
        ApiVersion = "V1"
    };

    private static ReferenceSnapshot NewSnapshot(Guid tenantId, Guid connectionId, string resourceType, string scope, DateTimeOffset fetchedAt, bool isCurrent = true) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        ConnectionId = connectionId,
        ResourceType = resourceType,
        ScopeExternalId = scope,
        SourceVersion = "V1",
        ContentHash = $"hash-{Guid.NewGuid():N}",
        // PostgreSQL keeps microseconds; the seeded time is cut to them so it reads back equal.
        FetchedAt = new DateTimeOffset(fetchedAt.UtcTicks - fetchedAt.UtcTicks % 10, TimeSpan.Zero),
        IsCurrent = isCurrent,
        ItemCount = 1
    };
}
