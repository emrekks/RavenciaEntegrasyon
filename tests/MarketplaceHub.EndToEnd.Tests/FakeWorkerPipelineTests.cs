using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using MarketplaceHub.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace MarketplaceHub.EndToEnd.Tests;

public sealed class FakeWorkerPipelineTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-02T12:00:00Z");
    private PostgreSqlContainer? postgres;
    private string connectionString = string.Empty;
    private string? externalAdminConnection;
    private string? externalDatabase;

    public async ValueTask InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("MARKETPLACEHUB_TEST_POSTGRES") is { Length: > 0 } external)
        {
            externalAdminConnection = external;
            externalDatabase = $"marketplacehub_fake_e2e_{Guid.NewGuid():N}";
            await using var connection = new NpgsqlConnection(external);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE \"{externalDatabase}\"", connection);
            await command.ExecuteNonQueryAsync();
            connectionString = new NpgsqlConnectionStringBuilder(external) { Database = externalDatabase, Pooling = false }.ConnectionString;
            return;
        }

        postgres = new PostgreSqlBuilder("postgres:18.4@sha256:3a82e1f56c8f0f5616a11103ac3d47e632c3938698946a7ad26da0df1334744a").Build();
        await postgres.StartAsync();
        connectionString = postgres.GetConnectionString();
    }

    public async ValueTask DisposeAsync()
    {
        if (postgres is not null)
        {
            await postgres.DisposeAsync();
            return;
        }
        if (externalAdminConnection is null || externalDatabase is null) return;
        await using var connection = new NpgsqlConnection(externalAdminConnection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{externalDatabase}\" WITH (FORCE)", connection);
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Worker_kill_retry_with_fake_adapter_does_not_duplicate_the_order_graph()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var tenantId = Guid.Parse("55555555-5555-5555-5555-555555555555");
        var connectionId = Guid.Parse("66666666-6666-6666-6666-666666666666");
        var jobId = Guid.Parse("77777777-7777-7777-7777-777777777777");
        var clock = new MutableTimeProvider(Now);
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString).Options);
        await db.Database.MigrateAsync(cancellationToken);
        db.Tenants.Add(new Tenant { Id = tenantId, Code = "fake-e2e", DisplayName = "Fake E2E", CreatedAt = Now, UpdatedAt = Now });
        db.PlatformConnections.Add(new PlatformConnection { Id = connectionId, PublicId = Guid.Parse("88888888-8888-8888-8888-888888888888"), TenantId = tenantId, PlatformCode = "TRENDYOL", Environment = "STAGE", DisplayName = "Synthetic connection", ExternalStoreId = "synthetic-store", Status = "DRAFT", ApiVersion = "v2", Version = 1 });
        db.IntegrationJobs.Add(new IntegrationJob
        {
            Id = jobId,
            TenantId = tenantId,
            ConnectionId = connectionId,
            JobType = F3JobTypes.OrderSync,
            PayloadJson = "{}",
            PayloadVersion = 1,
            PayloadHash = "synthetic-payload-hash",
            JobDedupKey = "fake-order-sync",
            EffectIdempotencyKey = "fake-order-sync-effect",
            AvailableAt = Now,
            CorrelationId = "fake-e2e-correlation"
        });
        await db.SaveChangesAsync(cancellationToken);

        var leases = new JobLeaseService(db, new TokenHasher(Enumerable.Repeat((byte)17, 32).ToArray()), clock);
        var fake = new DeterministicFakeAdapter(FakeScenario.Success, clock);
        var processor = new F3JobProcessor(db, fake, fake, fake, fake, null!, clock);
        var firstLease = Assert.IsType<LeasedJob>(await leases.TryLeaseAsync(TimeSpan.FromMinutes(2), cancellationToken));
        Assert.True((await processor.ProcessAsync(firstLease.TenantId, firstLease.ConnectionId, firstLease.JobType, firstLease.PayloadJson, firstLease.CorrelationId, cancellationToken)).Succeeded);

        clock.Advance(TimeSpan.FromMinutes(3));
        Assert.Equal(1, await leases.ReapExpiredAsync(cancellationToken));
        var job = await db.IntegrationJobs.SingleAsync(x => x.Id == jobId, cancellationToken);
        job.AvailableAt = clock.GetUtcNow().AddSeconds(-1);
        await db.SaveChangesAsync(cancellationToken);
        var retryLease = Assert.IsType<LeasedJob>(await leases.TryLeaseAsync(TimeSpan.FromMinutes(2), cancellationToken));
        Assert.True((await processor.ProcessAsync(retryLease.TenantId, retryLease.ConnectionId, retryLease.JobType, retryLease.PayloadJson, retryLease.CorrelationId, cancellationToken)).Succeeded);
        Assert.True(await leases.CompleteAsync(retryLease.Id, retryLease.LeaseToken, JobExecutionResult.Success(), cancellationToken));

        db.ChangeTracker.Clear();
        Assert.Equal(1, await db.Orders.CountAsync(x => x.TenantId == tenantId, cancellationToken));
        Assert.Equal(1, await db.OrderLines.CountAsync(x => x.TenantId == tenantId, cancellationToken));
        Assert.Equal(1, await db.SyncCursors.CountAsync(x => x.TenantId == tenantId && x.ResourceType == "ORDERS", cancellationToken));
        Assert.Equal(2, await db.JobAttempts.CountAsync(x => x.JobId == jobId, cancellationToken));
        Assert.Equal(JobStatus.Succeeded, (await db.IntegrationJobs.SingleAsync(x => x.Id == jobId, cancellationToken)).Status);
    }

    [Fact]
    public async Task Reference_sync_retry_keeps_one_current_snapshot_and_one_item()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var tenantId = Guid.Parse("95555555-5555-5555-5555-555555555555");
        var connectionId = Guid.Parse("96666666-6666-6666-6666-666666666666");
        var clock = new MutableTimeProvider(Now);
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString).Options);
        await db.Database.MigrateAsync(cancellationToken);
        db.Tenants.Add(new Tenant { Id = tenantId, Code = "reference-e2e", DisplayName = "Reference E2E", CreatedAt = Now, UpdatedAt = Now });
        db.PlatformConnections.Add(new PlatformConnection { Id = connectionId, PublicId = Guid.NewGuid(), TenantId = tenantId, PlatformCode = "TRENDYOL", Environment = "STAGE", DisplayName = "Reference connection", ExternalStoreId = "synthetic-store", Status = "ACTIVE", ApiVersion = "v2", Version = 1 });
        db.PlatformCapabilities.Add(new PlatformCapability { Id = Guid.NewGuid(), TenantId = tenantId, ConnectionId = connectionId, Code = F3Capabilities.ReferenceRead, SupportLevel = CapabilitySupportLevel.Supported, ApiVersion = "v2", Environment = "STAGE", StoreScope = "synthetic-store", SourceUrl = "test://reference", SourceVersion = "test-v1", EvidenceNote = "Deterministic reference fixture.", VerifiedAt = Now });
        await db.SaveChangesAsync(cancellationToken);

        var fake = new DeterministicFakeAdapter(FakeScenario.Success, clock);
        var processor = new F3JobProcessor(db, fake, fake, fake, fake, null!, clock);
        Assert.True((await processor.ProcessAsync(tenantId, connectionId, F3JobTypes.ReferenceSync, "{}", "reference-first", cancellationToken)).Succeeded);
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True((await processor.ProcessAsync(tenantId, connectionId, F3JobTypes.ReferenceSync, "{}", "reference-retry", cancellationToken)).Succeeded);

        db.ChangeTracker.Clear();
        Assert.Equal(1, await db.ReferenceSnapshots.CountAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId, cancellationToken));
        Assert.Equal(1, await db.ReferenceSnapshots.CountAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.IsCurrent, cancellationToken));
        Assert.Equal(1, await db.ReferenceItems.CountAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId, cancellationToken));
        var snapshot = await db.ReferenceSnapshots.SingleAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId, cancellationToken);
        Assert.Equal("CATEGORIES", snapshot.ResourceType);
        Assert.Equal("test-v1", snapshot.SourceVersion);
        Assert.Equal(clock.GetUtcNow(), snapshot.FetchedAt);

        var localCategoryId = Guid.Parse("97777777-7777-7777-7777-777777777777");
        db.Categories.Add(new Category { Id = localCategoryId, TenantId = tenantId, Name = "Yerel Yaprak", NormalizedName = "YEREL YAPRAK", Path = "Yerel Yaprak", Depth = 0, IsLeaf = true, IsActive = true, CreatedAt = Now, UpdatedAt = Now });
        await db.SaveChangesAsync(cancellationToken);
        var referenceService = new ReferenceDataService(db, clock);
        var listed = await referenceService.ListAsync(tenantId, connectionId, "CATEGORIES", null, cancellationToken);
        Assert.True(listed.Succeeded);
        Assert.Equal(snapshot.Id, listed.Value!.SnapshotId);
        Assert.Single(listed.Value.Items);
        Assert.Null((await referenceService.GetMappingAsync(tenantId, "categories", localCategoryId, connectionId, null, cancellationToken)).Value);

        var invalid = await referenceService.UpsertMappingAsync(tenantId, "categories", localCategoryId, null, new(connectionId, snapshot.Id, "synthetic-reference", "DRAFT"), cancellationToken);
        Assert.False(invalid.Succeeded);
        Assert.Equal("MAPPING_STATUS_INVALID", invalid.Error?.Code);
        var created = await referenceService.UpsertMappingAsync(tenantId, "categories", localCategoryId, null, new(connectionId, snapshot.Id, "synthetic-reference", "VERIFIED"), cancellationToken);
        Assert.True(created.Succeeded);
        var updated = await referenceService.UpsertMappingAsync(tenantId, "categories", localCategoryId, created.Value!.Version, new(connectionId, snapshot.Id, "synthetic-reference", "VERIFIED"), cancellationToken);
        Assert.True(updated.Succeeded);
        Assert.Equal(created.Value.Id, updated.Value!.Id);
        Assert.Equal(2, updated.Value.Version);
        Assert.Equal(1, await db.CategoryMappings.CountAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.LocalId == localCategoryId, cancellationToken));

        Assert.False((await processor.ProcessAsync(tenantId, connectionId, F3JobTypes.ReferenceSync, "{\"resourceType\":\"CATEGORY_ATTRIBUTES\",\"parentExternalId\":\"unknown-category\"}", "attribute-invalid-scope", cancellationToken)).Succeeded);
        Assert.True((await processor.ProcessAsync(tenantId, connectionId, F3JobTypes.ReferenceSync, "{\"resourceType\":\"CATEGORY_ATTRIBUTES\",\"parentExternalId\":\"synthetic-reference\"}", "attribute-reference", cancellationToken)).Succeeded);
        Assert.True((await processor.ProcessAsync(tenantId, connectionId, F3JobTypes.ReferenceSync, "{\"resourceType\":\"CATEGORY_ATTRIBUTES\",\"parentExternalId\":\"synthetic-reference\"}", "attribute-reference-retry", cancellationToken)).Succeeded);
        var attributes = await referenceService.ListAsync(tenantId, connectionId, "CATEGORY_ATTRIBUTES", "synthetic-reference", cancellationToken);
        Assert.True(attributes.Succeeded);
        Assert.Single(attributes.Value!.Items);
        Assert.Equal("synthetic-reference", attributes.Value.Items[0].ParentExternalId);

        Assert.True((await processor.ProcessAsync(tenantId, connectionId, F3JobTypes.ReferenceSync, "{\"resourceType\":\"ATTRIBUTE_VALUES\",\"parentExternalId\":\"synthetic-reference/synthetic-reference\"}", "attribute-value-reference", cancellationToken)).Succeeded);
        var attributeValues = await referenceService.ListAsync(tenantId, connectionId, "ATTRIBUTE_VALUES", "synthetic-reference/synthetic-reference", cancellationToken);
        Assert.True(attributeValues.Succeeded);
        Assert.Single(attributeValues.Value!.Items);
        Assert.Equal("synthetic-reference/synthetic-reference", attributeValues.Value.Items[0].ParentExternalId);

        var localBrandId = Guid.Parse("98888888-8888-8888-8888-888888888888");
        db.Brands.Add(new Brand { Id = localBrandId, TenantId = tenantId, Name = "Synthetic Reference", NormalizedName = "SYNTHETIC REFERENCE", IsActive = true, CreatedAt = Now, UpdatedAt = Now });
        await db.SaveChangesAsync(cancellationToken);
        Assert.True((await processor.ProcessAsync(tenantId, connectionId, F3JobTypes.ReferenceSync, "{\"resourceType\":\"BRANDS\"}", "brand-reference", cancellationToken)).Succeeded);
        db.ChangeTracker.Clear();
        var brands = await referenceService.ListAsync(tenantId, connectionId, "BRANDS", null, cancellationToken);
        Assert.True(brands.Succeeded);
        Assert.Single(brands.Value!.Items);
        Assert.Equal("BRANDS", brands.Value.ResourceType);
        var productId = Guid.Parse("99999999-9999-9999-9999-999999999999");
        db.Products.Add(new Product { Id = productId, TenantId = tenantId, Title = "Mapped Product", Description = "", CategoryId = localCategoryId, BrandId = localBrandId, CreatedAt = Now, UpdatedAt = Now });
        await db.SaveChangesAsync(cancellationToken);
        var protection = new EphemeralDataProtectionProvider();
        var catalog = new CatalogService(db, new CursorCodec(protection, clock), clock);
        var missingBrand = await catalog.ValidatePublicationAsync(tenantId, productId, connectionId, cancellationToken);
        Assert.Equal("BRAND_MAPPING_REQUIRED", missingBrand.Error?.Code);
        var brandMapping = await referenceService.UpsertMappingAsync(tenantId, "brands", localBrandId, null, new(connectionId, brands.Value.SnapshotId, "synthetic-reference", "VERIFIED"), cancellationToken);
        Assert.True(brandMapping.Succeeded);
        Assert.Equal("REQUIRED_ATTRIBUTE_MAPPING_REQUIRED", (await catalog.ValidatePublicationAsync(tenantId, productId, connectionId, cancellationToken)).Error?.Code);
        var localAttributeId = Guid.Parse("91111111-1111-1111-1111-111111111111");
        db.AttributeDefinitions.Add(new AttributeDefinition { Id = localAttributeId, TenantId = tenantId, Code = "SYNTHETIC", Name = "Synthetic Reference", DataType = AttributeDataType.Text, IsActive = true, CreatedAt = Now, UpdatedAt = Now });
        await db.SaveChangesAsync(cancellationToken);
        var attributeMapping = await referenceService.UpsertMappingAsync(tenantId, "attributes", localAttributeId, null, new(connectionId, attributes.Value.SnapshotId, "synthetic-reference", "VERIFIED"), cancellationToken);
        Assert.True(attributeMapping.Succeeded);
        Assert.Equal("synthetic-reference", attributeMapping.Value!.ScopeExternalId);
        Assert.Equal("REQUIRED_ATTRIBUTE_MISSING", (await catalog.ValidatePublicationAsync(tenantId, productId, connectionId, cancellationToken)).Error?.Code);
        db.ProductAttributeAssignments.Add(new ProductAttributeAssignment { Id = Guid.NewGuid(), TenantId = tenantId, ProductId = productId, AttributeId = localAttributeId, TextValue = "custom-safe-value" });
        await db.SaveChangesAsync(cancellationToken);
        var readyMappings = await catalog.ValidatePublicationAsync(tenantId, productId, connectionId, cancellationToken);
        Assert.Equal("CAPABILITY_UNKNOWN", readyMappings.Error?.Code);
        var remoteAttribute = await db.ReferenceItems.SingleAsync(x => x.TenantId == tenantId && x.ResourceType == "CATEGORY_ATTRIBUTES", cancellationToken);
        remoteAttribute.AllowsCustomValue = false; await db.SaveChangesAsync(cancellationToken);
        Assert.Equal("ATTRIBUTE_VALUE_MAPPING_REQUIRED", (await catalog.ValidatePublicationAsync(tenantId, productId, connectionId, cancellationToken)).Error?.Code);
        remoteAttribute.AllowsCustomValue = true; await db.SaveChangesAsync(cancellationToken);
        var attributeSnapshot = await db.ReferenceSnapshots.SingleAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == "CATEGORY_ATTRIBUTES", cancellationToken);
        attributeSnapshot.IsCurrent = false; await db.SaveChangesAsync(cancellationToken);
        Assert.Equal("ATTRIBUTE_SNAPSHOT_REQUIRED", (await catalog.ValidatePublicationAsync(tenantId, productId, connectionId, cancellationToken)).Error?.Code);
        attributeSnapshot.IsCurrent = true; await db.SaveChangesAsync(cancellationToken);
        var brandSnapshot = await db.ReferenceSnapshots.SingleAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == "BRANDS", cancellationToken);
        brandSnapshot.IsCurrent = false; await db.SaveChangesAsync(cancellationToken);
        Assert.Equal("BRAND_MAPPING_REQUIRED", (await catalog.ValidatePublicationAsync(tenantId, productId, connectionId, cancellationToken)).Error?.Code);
        brandSnapshot.IsCurrent = true; await db.SaveChangesAsync(cancellationToken);
        db.ChannelListingProfiles.Add(new ChannelListingProfile { Id = Guid.NewGuid(), TenantId = tenantId, ConnectionId = connectionId, ProductId = productId, ExternalCategoryId = "wrong-category", ExternalBrandId = "synthetic-reference", DesiredStatus = "DRAFT", ActualStatus = "UNKNOWN" });
        await db.SaveChangesAsync(cancellationToken);
        var conflict = await catalog.ValidatePublicationAsync(tenantId, productId, connectionId, cancellationToken);
        Assert.Equal("LISTING_MAPPING_CONFLICT", conflict.Error?.Code);
        Assert.Equal(4, await db.ReferenceSnapshots.CountAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId, cancellationToken));
        Assert.Equal(4, await db.ReferenceSnapshots.CountAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.IsCurrent, cancellationToken));
        Assert.Equal("synthetic-reference", await db.ReferenceSnapshots.Where(x => x.ResourceType == "CATEGORY_ATTRIBUTES").Select(x => x.ScopeExternalId).SingleAsync(cancellationToken));
        Assert.Equal("synthetic-reference/synthetic-reference", await db.ReferenceSnapshots.Where(x => x.ResourceType == "ATTRIBUTE_VALUES").Select(x => x.ScopeExternalId).SingleAsync(cancellationToken));
        Assert.Equal(1, await db.BrandMappings.CountAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.LocalId == localBrandId, cancellationToken));
        Assert.Equal(1, await db.AttributeMappings.CountAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.LocalId == localAttributeId && x.ScopeExternalId == "synthetic-reference", cancellationToken));
    }

    private sealed class MutableTimeProvider(DateTimeOffset value) : TimeProvider
    {
        private DateTimeOffset current = value;
        public override DateTimeOffset GetUtcNow() => current;
        public void Advance(TimeSpan duration) => current = current.Add(duration);
    }
}
