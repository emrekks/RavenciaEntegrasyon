using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

[Collection("postgres-integration")]
public sealed class CatalogStructureImportTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    [PostgreSqlFact]
    public async Task Preview_RollsBackAndApply_CreatesCategoryOptionsAndMappings()
    {
        var tenant = NewTenant("structure-apply");
        var trendyol = NewConnection(tenant, "TRENDYOL", "seller-1");
        await SeedAsync(tenant, trendyol);
        await SeedReferencesAsync(tenant, trendyol, "101", "Giyim / Bluz", "Bluz", ["Renk", "Beden", "Kumaş Tipi", "Garanti"], new() { ["Renk"] = ["Mavi", "Kırmızı", "Yeşil"], ["Beden"] = ["S", "M", "L"], ["Kumaş Tipi"] = ["Pamuk", "Keten", "Viskon"], ["Garanti"] = ["12 Ay", "24 Ay"] });
        var port = new FakeProductPort([Product("p1", "101", "Bluz", ("Renk", "Mavi"), ("Beden", "M"), ("Kumaş Tipi", "Pamuk")), Product("p2", "101", "Bluz", ("Renk", "Kırmızı"), ("Beden", "S"))]);

        try
        {
            var previewRunId = await NewRunAsync(tenant, trendyol, "PREVIEW");
            var preview = await ProcessAsync(tenant, trendyol, previewRunId, "PREVIEW", port);

            Assert.True(preview.Succeeded);
            await using (var check = fixture.CreateContext())
            {
                var run = await check.CatalogStructureRuns.AsNoTracking().SingleAsync(x => x.Id == previewRunId);
                Assert.Equal("COMPLETED", run.Status);
                var report = JsonSerializer.Deserialize<StructureReport>(run.ReportJson!, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                Assert.Equal(1, report.Totals.Categories);
                Assert.Equal(1, report.Totals.CategoriesCreated);
                Assert.Equal("CreateNew", report.Categories[0].Decision);
                Assert.Equal("Bluz", report.Categories[0].PanelName);
                // A preview must not leave anything behind in the panel catalog.
                Assert.Empty(await check.Categories.Where(x => x.TenantId == tenant.Id).ToListAsync());
                Assert.Empty(await check.CategoryMappings.Where(x => x.TenantId == tenant.Id).ToListAsync());
                Assert.Empty(await check.AttributeDefinitions.Where(x => x.TenantId == tenant.Id).ToListAsync());
            }

            var applyRunId = await NewRunAsync(tenant, trendyol, "APPLY");
            var apply = await ProcessAsync(tenant, trendyol, applyRunId, "APPLY", port);

            Assert.True(apply.Succeeded);
            await using var after = fixture.CreateContext();
            var category = await after.Categories.AsNoTracking().SingleAsync(x => x.TenantId == tenant.Id);
            Assert.Equal("Bluz", category.Name);
            Assert.Single(await after.CategoryMappings.AsNoTracking().Where(x => x.TenantId == tenant.Id).ToListAsync());
            var definitions = await after.AttributeDefinitions.AsNoTracking().Where(x => x.TenantId == tenant.Id).Select(x => x.Name).ToListAsync();
            Assert.Contains("Renk", definitions);
            Assert.Contains("Beden", definitions);
            var colorValues = await (from value in after.AttributeValues.AsNoTracking()
                                     join definition in after.AttributeDefinitions.AsNoTracking() on value.AttributeId equals definition.Id
                                     where value.TenantId == tenant.Id && definition.Name == "Renk"
                                     select value.Value).ToListAsync();
            Assert.Contains("Mavi", colorValues);
            Assert.Contains("Kırmızı", colorValues);
            // A used attribute keeps only the values products use; an unused mandatory one is not imported.
            Assert.DoesNotContain("Garanti", definitions);
            var fabricValues = await (from value in after.AttributeValues.AsNoTracking()
                                      join definition in after.AttributeDefinitions.AsNoTracking() on value.AttributeId equals definition.Id
                                      where value.TenantId == tenant.Id && definition.Name == "Kumaş Tipi"
                                      select value.Value).ToListAsync();
            Assert.Equal(["Pamuk"], fabricValues);
        }
        finally
        {
            await DeleteTenantAsync(tenant.Id);
        }
    }

    [PostgreSqlFact]
    public async Task SecondMarketplace_ReusesACategoryOnlyWhenItsAttributeSetsMatch()
    {
        var tenant = NewTenant("structure-reuse");
        var trendyol = NewConnection(tenant, "TRENDYOL", "seller-1");
        var hepsiburadaSame = NewConnection(tenant, "HEPSIBURADA", "merchant-1");
        var hepsiburadaDifferent = NewConnection(tenant, "HEPSIBURADA", "merchant-2");
        await SeedAsync(tenant, trendyol, hepsiburadaSame, hepsiburadaDifferent);
        await SeedReferencesAsync(tenant, trendyol, "101", "Giyim / Bluz", "Bluz", ["Renk", "Beden"], new() { ["Renk"] = ["Mavi"], ["Beden"] = ["M"] });
        await SeedReferencesAsync(tenant, hepsiburadaSame, "9001", "Kadın > Bluz", "Bluz", ["Renk", "Beden"], new() { ["Renk"] = ["Mavi"], ["Beden"] = ["M"] });
        await SeedReferencesAsync(tenant, hepsiburadaDifferent, "9002", "Kadın > Büyük Beden > Bluz", "Bluz", ["Renk", "Beden", "Kumaş Tipi"], new() { ["Renk"] = ["Mavi"], ["Beden"] = ["M"], ["Kumaş Tipi"] = ["Pamuk"] });

        try
        {
            await ApplyAsync(tenant, trendyol, new FakeProductPort([Product("t1", "101", "Bluz", ("Renk", "Mavi"), ("Beden", "M"))]));
            await ApplyAsync(tenant, hepsiburadaSame, new FakeProductPort([Product("h1", "9001", "Bluz", ("Renk", "Mavi"), ("Beden", "M"))]));
            await ApplyAsync(tenant, hepsiburadaDifferent, new FakeProductPort([Product("h2", "9002", "Bluz", ("Renk", "Mavi"), ("Beden", "M"), ("Kumaş Tipi", "Pamuk"))]));

            await using var db = fixture.CreateContext();
            var categories = await db.Categories.AsNoTracking().Where(x => x.TenantId == tenant.Id).OrderBy(x => x.Name).ToListAsync();
            // Same attribute set shares one panel category; a different set stays separate.
            Assert.Equal(["Bluz", "Bluz (Hepsiburada)"], categories.Select(x => x.Name).ToArray());
            var mappings = await db.CategoryMappings.AsNoTracking().Where(x => x.TenantId == tenant.Id).ToListAsync();
            Assert.Equal(3, mappings.Count);
            var shared = categories.Single(x => x.Name == "Bluz");
            Assert.Equal(2, mappings.Count(x => x.LocalId == shared.Id));
        }
        finally
        {
            await DeleteTenantAsync(tenant.Id);
        }
    }

    [PostgreSqlFact]
    public async Task TwoCategoriesWithTheSameLeafOnOneConnection_GetSeparatePanelCategories()
    {
        var tenant = NewTenant("structure-same-leaf");
        var hepsiburada = NewConnection(tenant, "HEPSIBURADA", "merchant-1");
        await SeedAsync(tenant, hepsiburada);
        await SeedReferencesAsync(tenant, hepsiburada, "9001", "Kadın > Bluz", "Bluz", ["Renk", "Beden"], new() { ["Renk"] = ["Mavi"], ["Beden"] = ["M"] });
        await SeedReferencesAsync(tenant, hepsiburada, "9002", "Kadın > Büyük Beden > Bluz", "Bluz", ["Renk", "Beden"], new() { ["Renk"] = ["Mavi"], ["Beden"] = ["M"] });

        try
        {
            await ApplyAsync(tenant, hepsiburada, new FakeProductPort([Product("h1", "9001", "Bluz", ("Renk", "Mavi"), ("Beden", "M")), Product("h2", "9002", "Bluz", ("Renk", "Mavi"), ("Beden", "M"))]));

            await using var db = fixture.CreateContext();
            var categories = await db.Categories.AsNoTracking().Where(x => x.TenantId == tenant.Id).OrderBy(x => x.Name).Select(x => x.Name).ToListAsync();
            Assert.Equal(2, categories.Count);
            Assert.Equal(2, categories.Distinct().Count());
            Assert.Equal(2, await db.CategoryMappings.AsNoTracking().CountAsync(x => x.TenantId == tenant.Id));
        }
        finally
        {
            await DeleteTenantAsync(tenant.Id);
        }
    }

    private async Task ApplyAsync(Tenant tenant, PlatformConnection connection, FakeProductPort port)
    {
        var runId = await NewRunAsync(tenant, connection, "APPLY");
        var result = await ProcessAsync(tenant, connection, runId, "APPLY", port);
        Assert.True(result.Succeeded, result.ErrorSummary);
    }

    private async Task<JobExecutionResult> ProcessAsync(Tenant tenant, PlatformConnection connection, Guid runId, string mode, FakeProductPort port)
    {
        await using var db = fixture.CreateContext();
        var processor = new MarketplaceJobProcessor(
            db, null!, null!, port, null!, null!, null!, null!, null!, null!, null!,
            new ConfigurationBuilder().Build(), fixture.TimeProvider);
        return await processor.ProcessAsync(
            tenant.Id,
            connection.Id,
            MarketplaceJobTypes.CatalogStructureImport,
            JsonSerializer.Serialize(new { connectionId = connection.Id, runId, mode }),
            $"structure-{mode}",
            CancellationToken.None);
    }

    private async Task<Guid> NewRunAsync(Tenant tenant, PlatformConnection connection, string mode)
    {
        await using var db = fixture.CreateContext();
        var run = new CatalogStructureRun { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, Mode = mode, Status = "QUEUED", CreatedAt = fixture.Now };
        db.CatalogStructureRuns.Add(run);
        await db.SaveChangesAsync();
        return run.Id;
    }

    private async Task SeedAsync(Tenant tenant, params PlatformConnection[] connections)
    {
        await using var db = fixture.CreateContext();
        db.Tenants.Add(tenant);
        db.PlatformConnections.AddRange(connections);
        await db.SaveChangesAsync();
    }

    // Reference data is seeded as a fresh cache so the processor never calls the marketplace.
    private async Task SeedReferencesAsync(Tenant tenant, PlatformConnection connection, string categoryId, string path, string name, string[] attributes, Dictionary<string, string[]> values)
    {
        await using var db = fixture.CreateContext();
        var now = DateTimeOffset.UtcNow;
        ReferenceSnapshot Snapshot(string type, string scope) => new() { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, ResourceType = type, ScopeExternalId = scope, SourceVersion = "test", ContentHash = Guid.NewGuid().ToString("N"), FetchedAt = now, IsCurrent = true };
        ReferenceItem Item(ReferenceSnapshot snapshot, string type, string id, string itemName, string itemPath, bool leaf = true, bool? required = null) => new() { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, SnapshotId = snapshot.Id, ResourceType = type, ExternalId = id, Name = itemName, NormalizedName = itemName.ToUpperInvariant(), Path = itemPath, IsLeaf = leaf, IsActive = true, IsRequired = required, PayloadHash = Guid.NewGuid().ToString("N") };

        // A connection has one current category list; extra categories join it.
        var categories = await db.ReferenceSnapshots.FirstOrDefaultAsync(x => x.TenantId == tenant.Id && x.ConnectionId == connection.Id && x.ResourceType == "CATEGORIES");
        if (categories is null) { categories = Snapshot("CATEGORIES", ""); db.ReferenceSnapshots.Add(categories); }
        var attributeSnapshot = Snapshot("CATEGORY_ATTRIBUTES", categoryId);
        db.ReferenceSnapshots.Add(attributeSnapshot);
        db.ReferenceItems.Add(Item(categories, "CATEGORIES", categoryId, name, path));
        var attributeIndex = 0;
        foreach (var attribute in attributes)
        {
            attributeIndex++;
            var attributeId = attributeIndex.ToString();
            db.ReferenceItems.Add(Item(attributeSnapshot, "CATEGORY_ATTRIBUTES", attributeId, attribute, attribute, required: true));
            var valueSnapshot = Snapshot("ATTRIBUTE_VALUES", $"{categoryId}/{attributeId}");
            db.ReferenceSnapshots.Add(valueSnapshot);
            var valueIndex = 0;
            foreach (var value in values.GetValueOrDefault(attribute) ?? [])
            {
                valueIndex++;
                db.ReferenceItems.Add(Item(valueSnapshot, "ATTRIBUTE_VALUES", $"{attributeId}-{valueIndex}", value, value));
            }
        }
        await db.SaveChangesAsync();
    }

    private static RemoteCatalogProduct Product(string id, string categoryId, string categoryName, params (string Name, string Value)[] options) =>
        new(id, null, id, "", null, null, categoryId, categoryName, [],
            [new RemoteCatalogVariant(id, id, null, null, options.ToDictionary(x => x.Name, x => x.Value), false, null, null, null, null, null, "{}")], "{}");

    private async Task DeleteTenantAsync(Guid tenantId)
    {
        await using var db = fixture.CreateContext();
        await db.IntegrationOutboxEvents.Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
        await db.AttributeValueMappings.Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
        await db.AttributeMappings.Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
        await db.CategoryMappings.Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
        await db.CategoryAttributeRequirements.Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
        await db.AttributeValues.Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
        await db.AttributeDefinitions.Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
        await db.Categories.Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
        await db.ReferenceItems.Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
        await db.ReferenceSnapshots.Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
        await db.CatalogStructureRuns.Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
        await db.OperationalIssues.Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
        await db.PlatformConnections.Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
        await db.Tenants.Where(x => x.Id == tenantId).ExecuteDeleteAsync();
    }

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

    private static PlatformConnection NewConnection(Tenant tenant, string platformCode, string storeId) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenant.Id,
        PublicId = Guid.CreateVersion7(),
        PlatformCode = platformCode,
        Environment = "STAGE",
        DisplayName = $"{platformCode} {storeId}",
        ExternalStoreId = storeId,
        Status = "ACTIVE",
        ApiVersion = "V1"
    };

    private sealed class FakeProductPort(IReadOnlyList<RemoteCatalogProduct> catalog) : IProductPort
    {
        public Task<AdapterResult<AdapterPageResult<RemoteCatalogProduct>>> ListCatalogAsync(AdapterContext context, AdapterPageRequest page, ProductReadFilter filter, CancellationToken cancellationToken) =>
            Task.FromResult(AdapterResult<AdapterPageResult<RemoteCatalogProduct>>.Success(new(catalog, null, false, catalog.Count)));

        public Task<AdapterResult<AdapterPageResult<RemoteProduct>>> ListAsync(AdapterContext context, AdapterPageRequest page, ProductReadFilter filter, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<RemoteOperationRef>> CreateAsync(AdapterContext context, ProductPublication publication, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<RemoteOperationRef>> UpdateUnapprovedAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<RemoteOperationRef>> UpdateApprovedContentAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<RemoteOperationRef>> UpdateApprovedVariantsAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<RemoteOperationRef>> UpdateApprovedDeliveryAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<RemoteOperationStatus>> GetOperationAsync(AdapterContext context, string externalOperationId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<RemotePublicationStatus>> GetPublicationStatusAsync(AdapterContext context, string barcode, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<RemoteOperationRef>> ArchiveAsync(AdapterContext context, string payloadJson, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
