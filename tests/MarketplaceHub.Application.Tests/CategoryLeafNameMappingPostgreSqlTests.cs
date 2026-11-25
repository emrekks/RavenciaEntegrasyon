using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// B-5: two marketplace categories whose leaf names are the same ("Kadın / Elbise", "Çocuk / Elbise") both matched the
// panel category "Elbise". The second mapping to it broke the unique index (tenant, connection, local category, scope),
// so the import of the model failed on every run. The second one now gets the panel category of its own path.
[Collection("postgres-integration")]
public sealed class CategoryLeafNameMappingPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    [PostgreSqlTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TwoMarketplaceCategoriesWithTheSameLeafName_GetTwoPanelCategories(bool saveBetween)
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"b5-{Guid.NewGuid():N}", DisplayName = "b5", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var connection = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = "N11", Environment = "PRODUCTION", DisplayName = "b5", ExternalStoreId = $"store-{Guid.NewGuid():N}", Status = "ACTIVE", ApiVersion = "V1", SettingsJson = "{}", Version = 1 };
        var snapshot = new ReferenceSnapshot { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, ResourceType = "CATEGORIES", SourceVersion = "test", ContentHash = "test", FetchedAt = now, IsCurrent = true };
        var panel = new Category { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Name = "Elbise", NormalizedName = CatalogAttributeService.NormalizeCatalogKey("Elbise", 160), Path = "Elbise", Depth = 0, IsLeaf = true, IsActive = true, CreatedAt = now, UpdatedAt = now, Version = 1 };
        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.Add(tenant);
            seed.PlatformConnections.Add(connection);
            seed.ReferenceSnapshots.Add(snapshot);
            seed.Categories.Add(panel);
            await seed.SaveChangesAsync();
        }

        await using var db = fixture.CreateContext();
        var service = new CatalogAttributeService(db, TimeProvider.System, null!, null!);
        var women = await service.EnsureMappedCategory(tenant.Id, connection.Id, snapshot, Remote(tenant.Id, connection.Id, snapshot.Id, "100", "Kadın / Elbise"), CancellationToken.None);
        if (saveBetween) await db.SaveChangesAsync();
        var children = await service.EnsureMappedCategory(tenant.Id, connection.Id, snapshot, Remote(tenant.Id, connection.Id, snapshot.Id, "200", "Çocuk / Elbise"), CancellationToken.None);
        await db.SaveChangesAsync();

        Assert.Equal(panel.Id, women.Id);
        Assert.NotEqual(women.Id, children.Id);
        Assert.Equal("Elbise", children.Name);
        await using var check = fixture.CreateContext();
        var mappings = await check.CategoryMappings.AsNoTracking().Where(x => x.TenantId == tenant.Id).OrderBy(x => x.ExternalId).ToListAsync();
        Assert.Equal([panel.Id, children.Id], mappings.Select(x => x.LocalId));

        // A later run finds each one's own mapping again; nothing new is opened.
        await using var again = fixture.CreateContext();
        var rerun = new CatalogAttributeService(again, TimeProvider.System, null!, null!);
        Assert.Equal(children.Id, (await rerun.EnsureMappedCategory(tenant.Id, connection.Id, snapshot, Remote(tenant.Id, connection.Id, snapshot.Id, "200", "Çocuk / Elbise"), CancellationToken.None)).Id);
        await again.SaveChangesAsync();
        Assert.Equal(2, await check.Categories.CountAsync(x => x.TenantId == tenant.Id));
    }

    private static ReferenceItem Remote(Guid tenantId, Guid connectionId, Guid snapshotId, string id, string path) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        ConnectionId = connectionId,
        SnapshotId = snapshotId,
        ResourceType = "CATEGORIES",
        ExternalId = id,
        Name = "Elbise",
        NormalizedName = CatalogAttributeService.NormalizeCatalogKey("Elbise", 160),
        Path = path,
        Depth = 1,
        IsLeaf = true,
        IsActive = true,
        PayloadHash = "h"
    };
}
