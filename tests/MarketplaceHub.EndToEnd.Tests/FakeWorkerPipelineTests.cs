using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using MarketplaceHub.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
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
        var brandMapping = await referenceService.UpsertMappingAsync(tenantId, "brands", localBrandId, null, new(connectionId, brands.Value.SnapshotId, "synthetic-reference", "VERIFIED"), cancellationToken);
        Assert.True(brandMapping.Succeeded);
        var localAttributeId = Guid.Parse("91111111-1111-1111-1111-111111111111");
        db.AttributeDefinitions.Add(new AttributeDefinition { Id = localAttributeId, TenantId = tenantId, Code = "SYNTHETIC", Name = "Synthetic Reference", DataType = AttributeDataType.Text, IsActive = true, CreatedAt = Now, UpdatedAt = Now });
        await db.SaveChangesAsync(cancellationToken);
        var attributeMapping = await referenceService.UpsertMappingAsync(tenantId, "attributes", localAttributeId, null, new(connectionId, attributes.Value.SnapshotId, "synthetic-reference", "VERIFIED"), cancellationToken);
        Assert.True(attributeMapping.Succeeded);
        Assert.Equal("synthetic-reference", attributeMapping.Value!.ScopeExternalId);
        Assert.Equal(4, await db.ReferenceSnapshots.CountAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId, cancellationToken));
        Assert.Equal(4, await db.ReferenceSnapshots.CountAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.IsCurrent, cancellationToken));
        Assert.Equal("synthetic-reference", await db.ReferenceSnapshots.Where(x => x.ResourceType == "CATEGORY_ATTRIBUTES").Select(x => x.ScopeExternalId).SingleAsync(cancellationToken));
        Assert.Equal("synthetic-reference/synthetic-reference", await db.ReferenceSnapshots.Where(x => x.ResourceType == "ATTRIBUTE_VALUES").Select(x => x.ScopeExternalId).SingleAsync(cancellationToken));
        Assert.Equal(1, await db.BrandMappings.CountAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.LocalId == localBrandId, cancellationToken));
        Assert.Equal(1, await db.AttributeMappings.CountAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.LocalId == localAttributeId && x.ScopeExternalId == "synthetic-reference", cancellationToken));
    }


    [Fact]
    public async Task Product_create_job_submits_polls_and_persists_row_results()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var tenantId = Guid.Parse("a5555555-5555-5555-5555-555555555555");
        var connectionId = Guid.Parse("a6666666-6666-6666-6666-666666666666");
        var categoryId = Guid.Parse("a7777777-7777-7777-7777-777777777777");
        var brandId = Guid.Parse("a8888888-8888-8888-8888-888888888888");
        var attributeId = Guid.Parse("a9999999-9999-9999-9999-999999999999");
        var productId = Guid.Parse("abbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var variantId = Guid.Parse("accccccc-cccc-cccc-cccc-cccccccccccc");
        var profileId = Guid.Parse("addddddd-dddd-dddd-dddd-dddddddddddd");
        var categorySnapshotId = Guid.Parse("a1111111-1111-1111-1111-111111111111");
        var brandSnapshotId = Guid.Parse("a2222222-2222-2222-2222-222222222222");
        var attributeSnapshotId = Guid.Parse("a3333333-3333-3333-3333-333333333333");
        var clock = new MutableTimeProvider(Now);

        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString).Options);
        await db.Database.MigrateAsync(cancellationToken);
        db.Tenants.Add(new Tenant { Id = tenantId, Code = "product-create-e2e", DisplayName = "Product Create E2E", CreatedAt = Now, UpdatedAt = Now });
        db.PlatformConnections.Add(new PlatformConnection { Id = connectionId, PublicId = Guid.NewGuid(), TenantId = tenantId, PlatformCode = "TRENDYOL", Environment = "STAGE", DisplayName = "Product create connection", ExternalStoreId = "synthetic-store", Status = "ACTIVE", ApiVersion = "v2", SettingsJson = "{\"ExternalWritesEnabled\":true}", Version = 1 });
        db.PlatformCapabilities.Add(new PlatformCapability { Id = Guid.NewGuid(), TenantId = tenantId, ConnectionId = connectionId, Code = F3Capabilities.ProductWrite, SupportLevel = CapabilitySupportLevel.Supported, ApiVersion = "v2", Environment = "STAGE", StoreScope = "synthetic-store", SourceUrl = "test://product-create", SourceVersion = "test-v1", EvidenceNote = "Deterministic Stage contract fixture.", VerifiedAt = Now });
        db.Categories.Add(new Category { Id = categoryId, TenantId = tenantId, Name = "Bluz", NormalizedName = "BLUZ", Path = "Kadın / Bluz", Depth = 1, IsLeaf = true, IsActive = true, CreatedAt = Now, UpdatedAt = Now });
        db.Brands.Add(new Brand { Id = brandId, TenantId = tenantId, Name = "Ravencia", NormalizedName = "RAVENCIA", IsActive = true, CreatedAt = Now, UpdatedAt = Now });
        db.AttributeDefinitions.Add(new AttributeDefinition { Id = attributeId, TenantId = tenantId, Code = "KUMAS", Name = "Kumaş", DataType = AttributeDataType.Text, IsActive = true, CreatedAt = Now, UpdatedAt = Now });
        db.Products.Add(new Product { Id = productId, TenantId = tenantId, Title = "Kadın Desenli Likralı Bluz", Description = "Yumuşak dokulu günlük kadın bluzu.", CategoryId = categoryId, BrandId = brandId, CreatedAt = Now, UpdatedAt = Now });
        db.ProductVariants.Add(new ProductVariant { Id = variantId, TenantId = tenantId, ProductId = productId, Sku = "RV-BLZ-001-M", SkuNormalized = "RV-BLZ-001-M", Barcode = "8690000000001", BarcodeNormalized = "8690000000001", ModelCode = "RV-BLZ-001", OptionSignature = "BEDEN=M", CreatedAt = Now, UpdatedAt = Now });
        db.ProductAttributeAssignments.Add(new ProductAttributeAssignment { Id = Guid.NewGuid(), TenantId = tenantId, ProductId = productId, AttributeId = attributeId, TextValue = "Viskon", SortOrder = 0 });
        db.ChannelListingProfiles.Add(new ChannelListingProfile { Id = profileId, TenantId = tenantId, ConnectionId = connectionId, ProductId = productId, DeliveryTimeDays = 2, Origin = "TR", Enabled = true, DesiredStatus = "DRAFT", ActualStatus = "UNKNOWN", Version = 1 });
        db.ChannelOffers.Add(new ChannelOffer { Id = Guid.NewGuid(), TenantId = tenantId, ConnectionId = connectionId, VariantId = variantId, ListPrice = 499.90m, SalePrice = 399.90m, Currency = "TRY", VatRate = 20, VatInclusion = "INCLUDED", RoundingMode = "HALF_UP", SafetyStock = 2, Status = "ACTIVE", Version = 1 });
        db.InventoryItems.Add(new InventoryItem { Id = Guid.NewGuid(), TenantId = tenantId, VariantId = variantId, LocationCode = "MAIN", OnHand = 12, Reserved = 0, Available = 12, Version = 1 });
        var assetId = Guid.NewGuid();
        db.FileAssets.Add(new FileAsset { Id = assetId, TenantId = tenantId, Classification = "PRODUCT_MEDIA_URL", RelativePath = "https://cdn.example.test/products/rv-blz-001.jpg", OriginalNameSafe = "rv-blz-001.jpg", MimeType = "image/jpeg", SizeBytes = 0, Sha256 = new string('A', 64), Status = "ACTIVE", CreatedAt = Now });
        db.ProductMedia.Add(new ProductMedia { Id = Guid.NewGuid(), TenantId = tenantId, ProductId = productId, FileAssetId = assetId, MediaRole = "MAIN", SortOrder = 0, AltText = "Kadın desenli bluz", Status = "ACTIVE" });

        db.ReferenceSnapshots.AddRange(
            new ReferenceSnapshot { Id = categorySnapshotId, TenantId = tenantId, ConnectionId = connectionId, ResourceType = "CATEGORIES", ScopeExternalId = "", SourceVersion = "test-v1", ContentHash = "category-hash", FetchedAt = Now, IsCurrent = true, ItemCount = 1 },
            new ReferenceSnapshot { Id = brandSnapshotId, TenantId = tenantId, ConnectionId = connectionId, ResourceType = "BRANDS", ScopeExternalId = "", SourceVersion = "test-v1", ContentHash = "brand-hash", FetchedAt = Now, IsCurrent = true, ItemCount = 1 },
            new ReferenceSnapshot { Id = attributeSnapshotId, TenantId = tenantId, ConnectionId = connectionId, ResourceType = "CATEGORY_ATTRIBUTES", ScopeExternalId = "100", SourceVersion = "test-v1", ContentHash = "attribute-hash", FetchedAt = Now, IsCurrent = true, ItemCount = 1 });
        db.ReferenceItems.AddRange(
            new ReferenceItem { Id = Guid.NewGuid(), TenantId = tenantId, ConnectionId = connectionId, SnapshotId = categorySnapshotId, ResourceType = "CATEGORIES", ExternalId = "100", Name = "Bluz", NormalizedName = "BLUZ", Path = "Kadın / Bluz", Depth = 1, IsLeaf = true, IsActive = true, PayloadHash = "category-item" },
            new ReferenceItem { Id = Guid.NewGuid(), TenantId = tenantId, ConnectionId = connectionId, SnapshotId = brandSnapshotId, ResourceType = "BRANDS", ExternalId = "200", Name = "Ravencia", NormalizedName = "RAVENCIA", Path = "Ravencia", Depth = 0, IsLeaf = true, IsActive = true, PayloadHash = "brand-item" },
            new ReferenceItem { Id = Guid.NewGuid(), TenantId = tenantId, ConnectionId = connectionId, SnapshotId = attributeSnapshotId, ResourceType = "CATEGORY_ATTRIBUTES", ExternalId = "300", ParentExternalId = "100", Name = "Kumaş", NormalizedName = "KUMAŞ", Path = "Kumaş", Depth = 0, IsLeaf = true, IsActive = true, IsRequired = true, AllowsCustomValue = true, AllowsMultipleValues = false, PayloadHash = "attribute-item" });
        db.CategoryMappings.Add(new CategoryMapping { Id = Guid.NewGuid(), TenantId = tenantId, ConnectionId = connectionId, SnapshotId = categorySnapshotId, LocalId = categoryId, ExternalId = "100", Status = "VERIFIED", VerifiedAt = Now });
        db.BrandMappings.Add(new BrandMapping { Id = Guid.NewGuid(), TenantId = tenantId, ConnectionId = connectionId, SnapshotId = brandSnapshotId, LocalId = brandId, ExternalId = "200", Status = "VERIFIED", VerifiedAt = Now });
        db.AttributeMappings.Add(new AttributeMapping { Id = Guid.NewGuid(), TenantId = tenantId, ConnectionId = connectionId, SnapshotId = attributeSnapshotId, LocalId = attributeId, ScopeExternalId = "100", ExternalId = "300", Status = "VERIFIED", VerifiedAt = Now });
        await db.SaveChangesAsync(cancellationToken);

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["FeatureFlags:ExternalWrites"] = "true" }).Build();
        var catalog = new CatalogService(db, new CursorCodec(new EphemeralDataProtectionProvider(), clock), configuration, clock);
        var queued = await catalog.EnqueuePublicationAsync(tenantId, productId, connectionId, "publish-once", "product-create-e2e", cancellationToken);
        Assert.True(queued.Succeeded);
        var replay = await catalog.EnqueuePublicationAsync(tenantId, productId, connectionId, "publish-replay", "product-create-e2e-replay", cancellationToken);
        Assert.Equal(queued.Value, replay.Value);

        var job = await db.IntegrationJobs.SingleAsync(x => x.Id == queued.Value, cancellationToken);
        var envelope = JsonSerializer.Deserialize<ProductPublicationJobPayload>(job.PayloadJson);
        Assert.NotNull(envelope);
        using (var document = JsonDocument.Parse(envelope!.PayloadJson))
        {
            var item = Assert.Single(document.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal("8690000000001", item.GetProperty("barcode").GetString());
            Assert.Equal(10, item.GetProperty("quantity").GetInt32());
            Assert.Equal(100, item.GetProperty("categoryId").GetInt64());
            Assert.Equal(200, item.GetProperty("brandId").GetInt64());
            Assert.Equal(300, Assert.Single(item.GetProperty("attributes").EnumerateArray()).GetProperty("attributeId").GetInt64());
            Assert.False(item.TryGetProperty("deliveryDuration", out _));
            Assert.Equal(2, item.GetProperty("deliveryOption").GetProperty("deliveryDuration").GetInt32());
        }

        var fake = new DeterministicFakeAdapter(FakeScenario.Success, clock, true);
        var processor = new F3JobProcessor(db, fake, fake, fake, fake, fake, clock);
        var submitted = await processor.ProcessAsync(tenantId, connectionId, job.JobType, job.PayloadJson, job.CorrelationId, cancellationToken);
        Assert.Equal(JobCompletionKind.Retry, submitted.Kind);
        Assert.Equal("PRODUCT_BATCH_PENDING", submitted.ErrorCode);

        db.ChangeTracker.Clear();
        job = await db.IntegrationJobs.SingleAsync(x => x.Id == queued.Value, cancellationToken);
        var completed = await processor.ProcessAsync(tenantId, connectionId, job.JobType, job.PayloadJson, job.CorrelationId, cancellationToken);
        Assert.True(completed.Succeeded);

        db.ChangeTracker.Clear();
        var status = await catalog.GetPublicationStatusAsync(tenantId, productId, connectionId, cancellationToken);
        Assert.True(status.Succeeded);
        Assert.Equal("APPROVAL_PENDING", status.Value!.ActualStatus);
        Assert.Equal("CREATE_ACCEPTED", Assert.Single(status.Value.Lines).ActualStatus);
        Assert.Equal("CREATE_ACCEPTED", (await db.MarketplaceListingStates.SingleAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.VariantId == variantId, cancellationToken)).ActualStatus);
        Assert.Equal(1, fake.ExternalEffectCount);
        Assert.Equal(1, await db.ExternalEffectRecords.CountAsync(x => x.TenantId == tenantId && x.EffectType == F3JobTypes.ProductCreate, cancellationToken));
        Assert.Equal(1, await db.IntegrationJobs.CountAsync(x => x.TenantId == tenantId && x.JobType == F3JobTypes.ProductCreate, cancellationToken));
        var terminalJob = await db.IntegrationJobs.SingleAsync(x => x.Id == queued.Value, cancellationToken);
        terminalJob.Status = JobStatus.Succeeded;
        terminalJob.CompletedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        db.ChangeTracker.Clear();
        var completedReplay = await catalog.EnqueuePublicationAsync(tenantId, productId, connectionId, "publish-after-completion", "product-create-e2e-completed-replay", cancellationToken);
        Assert.Equal(queued.Value, completedReplay.Value);
    }

    [Fact]
    public async Task Product_create_partial_batch_is_persisted_per_variant_without_full_success()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var tenantId = Guid.Parse("b5555555-5555-5555-5555-555555555555");
        var connectionId = Guid.Parse("b6666666-6666-6666-6666-666666666666");
        var productId = Guid.Parse("b7777777-7777-7777-7777-777777777777");
        var firstVariantId = Guid.Parse("b8888888-8888-8888-8888-888888888888");
        var secondVariantId = Guid.Parse("b9999999-9999-9999-9999-999999999999");
        var profileId = Guid.Parse("baaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var jobId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var clock = new MutableTimeProvider(Now);
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString).Options);
        await db.Database.MigrateAsync(cancellationToken);
        db.Tenants.Add(new Tenant { Id = tenantId, Code = "product-partial-e2e", DisplayName = "Product Partial E2E", CreatedAt = Now, UpdatedAt = Now });
        db.PlatformConnections.Add(new PlatformConnection { Id = connectionId, PublicId = Guid.NewGuid(), TenantId = tenantId, PlatformCode = "TRENDYOL", Environment = "STAGE", DisplayName = "Partial connection", ExternalStoreId = "synthetic-store", Status = "ACTIVE", ApiVersion = "v2", Version = 1 });
        db.Products.Add(new Product { Id = productId, TenantId = tenantId, Title = "Partial Product", Description = "Partial batch fixture", CreatedAt = Now, UpdatedAt = Now });
        db.ProductVariants.AddRange(
            new ProductVariant { Id = firstVariantId, TenantId = tenantId, ProductId = productId, Sku = "PARTIAL-1", SkuNormalized = "PARTIAL-1", Barcode = "8690000000101", BarcodeNormalized = "8690000000101", ModelCode = "PARTIAL", OptionSignature = "SIZE=M", CreatedAt = Now, UpdatedAt = Now },
            new ProductVariant { Id = secondVariantId, TenantId = tenantId, ProductId = productId, Sku = "PARTIAL-2", SkuNormalized = "PARTIAL-2", Barcode = "8690000000102", BarcodeNormalized = "8690000000102", ModelCode = "PARTIAL", OptionSignature = "SIZE=L", CreatedAt = Now, UpdatedAt = Now });
        db.ChannelListingProfiles.Add(new ChannelListingProfile { Id = profileId, TenantId = tenantId, ConnectionId = connectionId, ProductId = productId, Enabled = true, DesiredStatus = "CREATE_REQUESTED", ActualStatus = "QUEUED", Version = 1 });
        db.ChannelListingVariants.AddRange(
            new ChannelListingVariant { Id = Guid.NewGuid(), TenantId = tenantId, ProfileId = profileId, VariantId = firstVariantId, ExternalSku = "PARTIAL-1", ExternalBarcode = "8690000000101", DesiredStatus = "CREATE", ActualStatus = "QUEUED" },
            new ChannelListingVariant { Id = Guid.NewGuid(), TenantId = tenantId, ProfileId = profileId, VariantId = secondVariantId, ExternalSku = "PARTIAL-2", ExternalBarcode = "8690000000102", DesiredStatus = "CREATE", ActualStatus = "QUEUED" });
        db.MarketplaceListingStates.AddRange(
            new MarketplaceListingState { Id = Guid.NewGuid(), TenantId = tenantId, ConnectionId = connectionId, VariantId = firstVariantId, DesiredStatus = "CREATE", ActualStatus = "QUEUED", PayloadHash = "partial-hash", Version = 1 },
            new MarketplaceListingState { Id = Guid.NewGuid(), TenantId = tenantId, ConnectionId = connectionId, VariantId = secondVariantId, DesiredStatus = "CREATE", ActualStatus = "QUEUED", PayloadHash = "partial-hash", Version = 1 });
        var publicationJson = "{\"items\":[{\"barcode\":\"8690000000101\"},{\"barcode\":\"8690000000102\"}]}";
        var payload = JsonSerializer.Serialize(new ProductPublicationJobPayload(jobId, productId, profileId, "SUBMIT", "partial-hash", publicationJson, null, null));
        db.IntegrationJobs.Add(new IntegrationJob { Id = jobId, TenantId = tenantId, ConnectionId = connectionId, JobType = F3JobTypes.ProductCreate, PayloadJson = payload, PayloadVersion = 1, PayloadHash = "partial-envelope", JobDedupKey = "partial-product-create", EffectIdempotencyKey = "partial-product-create-effect", Status = JobStatus.Pending, AvailableAt = Now, MaxAttempts = 10, CorrelationId = "product-partial-e2e", CreatedAt = Now, Version = 1 });
        await db.SaveChangesAsync(cancellationToken);

        var fake = new DeterministicFakeAdapter(FakeScenario.Partial, clock, true);
        var processor = new F3JobProcessor(db, fake, fake, fake, fake, fake, clock);
        var submitted = await processor.ProcessAsync(tenantId, connectionId, F3JobTypes.ProductCreate, payload, "product-partial-submit", cancellationToken);
        Assert.Equal(JobCompletionKind.Retry, submitted.Kind);
        db.ChangeTracker.Clear();
        var updatedJob = await db.IntegrationJobs.SingleAsync(x => x.Id == jobId, cancellationToken);
        var completed = await processor.ProcessAsync(tenantId, connectionId, F3JobTypes.ProductCreate, updatedJob.PayloadJson, "product-partial-poll", cancellationToken);
        Assert.Equal(JobCompletionKind.Blocked, completed.Kind);
        Assert.Equal("PRODUCT_BATCH_PARTIAL_FAILURE", completed.ErrorCode);

        db.ChangeTracker.Clear();
        var profile = await db.ChannelListingProfiles.SingleAsync(x => x.Id == profileId, cancellationToken);
        var lines = await db.ChannelListingVariants.Where(x => x.ProfileId == profileId).OrderBy(x => x.ExternalBarcode).ToListAsync(cancellationToken);
        Assert.Equal("PARTIAL_FAILURE", profile.ActualStatus);
        Assert.Equal("CREATE_ACCEPTED", lines[0].ActualStatus);
        Assert.Equal("CREATE_REJECTED", lines[1].ActualStatus);
        Assert.Equal("FAKE_PARTIAL_REJECTION", lines[1].RejectionCode);
        Assert.Equal(1, fake.ExternalEffectCount);
    }

    private sealed class MutableTimeProvider(DateTimeOffset value) : TimeProvider
    {
        private DateTimeOffset current = value;
        public override DateTimeOffset GetUtcNow() => current;
        public void Advance(TimeSpan duration) => current = current.Add(duration);
    }
}
