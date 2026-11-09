using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// P7 (speed plan, 9 Nov 2026): the editor's category attribute requirements are cached per tenant and category in the
// API, and any committed write to the tables they are read from (an edit in the panel or a Worker sync) clears them
// through the catalog_requirements_changed notification.
public sealed class CategoryRequirementsCacheTests
{
    private static readonly IReadOnlyList<Application.CategoryAttributeRequirementView> Value = [];

    [Fact]
    public void WithoutTheListener_NothingIsKept()
    {
        var cache = new CategoryRequirementsCache(new MemoryCache(new MemoryCacheOptions { SizeLimit = 100 }));
        var tenant = Guid.NewGuid();
        var category = Guid.NewGuid();

        cache.Set(tenant, category, cache.CurrentGeneration, Value);

        Assert.False(cache.TryGet(tenant, category, out _));
    }

    [Fact]
    public void AChangeDuringTheRead_KeepsNothing_AndAChangeAfterItClearsTheEntry()
    {
        var cache = new CategoryRequirementsCache(new MemoryCache(new MemoryCacheOptions { SizeLimit = 100 }));
        cache.SetActive(true);
        var tenant = Guid.NewGuid();
        var category = Guid.NewGuid();

        var raced = cache.CurrentGeneration;
        cache.InvalidateAll();
        cache.Set(tenant, category, raced, Value);
        Assert.False(cache.TryGet(tenant, category, out _));

        cache.Set(tenant, category, cache.CurrentGeneration, Value);
        Assert.True(cache.TryGet(tenant, category, out var cached));
        Assert.Same(Value, cached);
        Assert.False(cache.TryGet(Guid.NewGuid(), category, out _));

        cache.InvalidateAll();
        Assert.False(cache.TryGet(tenant, category, out _));

        cache.Set(tenant, category, cache.CurrentGeneration, Value);
        cache.SetActive(false);
        Assert.False(cache.TryGet(tenant, category, out _));
    }
}

[Collection("postgres-integration")]
public sealed class CategoryRequirementsCachePostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    [PostgreSqlFact]
    public async Task Requirements_AreServedFromTheCache_UntilAnEditOrAnotherProcessWritesTheCatalog()
    {
        var cache = new CategoryRequirementsCache(new MemoryCache(new MemoryCacheOptions { SizeLimit = 100 }));
        var services = new ServiceCollection().AddScoped(_ => fixture.CreateContext()).BuildServiceProvider();
        var listener = new CategoryRequirementsChangeListener(services.GetRequiredService<IServiceScopeFactory>(), cache, NullLogger<CategoryRequirementsChangeListener>.Instance);
        var (tenant, category, attribute, value) = await SeedAsync();
        try
        {
            await listener.StartAsync(CancellationToken.None);
            await WaitUntilAsync(() => cache.IsActive);
            await using var db = fixture.CreateContext();
            var service = new CatalogService(db, null!, new ConfigurationBuilder().Build(), fixture.TimeProvider, new MemoryCache(new MemoryCacheOptions()), cache);

            // A second read is the same list, not a new database read. Other tests may commit catalog rows meanwhile
            // (each clears the cache), so a few tries are allowed.
            var cachedTwice = false;
            for (var attempt = 0; attempt < 5 && !cachedTwice; attempt++)
            {
                var first = await service.GetRequirementsAsync(tenant.Id, category.Id, CancellationToken.None);
                var second = await service.GetRequirementsAsync(tenant.Id, category.Id, CancellationToken.None);
                Assert.True(first.Succeeded, first.Error?.Message);
                cachedTwice = ReferenceEquals(first.Value, second.Value);
            }
            Assert.True(cachedTwice);
            Assert.Equal("Kırmızı", Assert.Single(Assert.Single((await service.GetRequirementsAsync(tenant.Id, category.Id, CancellationToken.None)).Value!).Attribute.Values).Value);
            // Another tenant never reads this tenant's entry.
            Assert.False((await service.GetRequirementsAsync(Guid.CreateVersion7(), category.Id, CancellationToken.None)).Succeeded);

            // Another process (the Worker's attribute sync) renames the value and commits.
            await using (var worker = fixture.CreateContext())
                await worker.AttributeValues.Where(x => x.Id == value.Id).ExecuteUpdateAsync(x => x.SetProperty(v => v.Value, "Bordo"));
            await WaitUntilAsync(() => !cache.TryGet(tenant.Id, category.Id, out _));
            Assert.Equal("Bordo", Assert.Single(Assert.Single((await service.GetRequirementsAsync(tenant.Id, category.Id, CancellationToken.None)).Value!).Attribute.Values).Value);

            // The panel's own edit of the requirements.
            Assert.True(Assert.Single((await service.GetRequirementsAsync(tenant.Id, category.Id, CancellationToken.None)).Value!).IsRequired);
            var version = await db.Categories.AsNoTracking().Where(x => x.Id == category.Id).Select(x => x.Version).SingleAsync();
            var replaced = await service.ReplaceRequirementsAsync(tenant.Id, category.Id, version, [new Application.AttributeRequirementCommand(attribute.Id, false, true, 0)], CancellationToken.None);
            Assert.True(replaced.Succeeded, replaced.Error?.Message);
            await WaitUntilAsync(() => !cache.TryGet(tenant.Id, category.Id, out _));
            Assert.False(Assert.Single((await service.GetRequirementsAsync(tenant.Id, category.Id, CancellationToken.None)).Value!).IsRequired);
        }
        finally
        {
            await listener.StopAsync(CancellationToken.None);
            listener.Dispose();
            await services.DisposeAsync();
            await using var cleanup = fixture.CreateContext();
            await cleanup.CategoryAttributeRequirements.Where(x => x.TenantId == tenant.Id).ExecuteDeleteAsync();
            await cleanup.AttributeValues.Where(x => x.TenantId == tenant.Id).ExecuteDeleteAsync();
            await cleanup.AttributeDefinitions.Where(x => x.TenantId == tenant.Id).ExecuteDeleteAsync();
            await cleanup.Categories.Where(x => x.TenantId == tenant.Id).ExecuteDeleteAsync();
            await cleanup.Tenants.Where(x => x.Id == tenant.Id).ExecuteDeleteAsync();
        }
        Assert.False(cache.IsActive);
    }

    private async Task<(Tenant, Category, AttributeDefinition, AttributeValue)> SeedAsync()
    {
        var now = fixture.Now;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"requirements-cache-{Guid.NewGuid():N}", DisplayName = "requirements-cache", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var category = new Category { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Name = "Elbise", NormalizedName = "elbise", Path = "Elbise", CreatedAt = now, UpdatedAt = now };
        var attribute = new AttributeDefinition { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Code = "renk", Name = "Renk", DataType = AttributeDataType.SingleSelect, CreatedAt = now, UpdatedAt = now };
        var value = new AttributeValue { Id = Guid.CreateVersion7(), TenantId = tenant.Id, AttributeId = attribute.Id, Value = "Kırmızı", NormalizedValue = "kirmizi" };
        await using var db = fixture.CreateContext();
        db.Tenants.Add(tenant);
        db.Categories.Add(category);
        db.AttributeDefinitions.Add(attribute);
        db.AttributeValues.Add(value);
        db.CategoryAttributeRequirements.Add(new CategoryAttributeRequirement { Id = Guid.CreateVersion7(), TenantId = tenant.Id, CategoryId = category.Id, AttributeId = attribute.Id, IsRequired = true, IsPanelScoped = true, Role = "ATTRIBUTE", Version = 1 });
        await db.SaveChangesAsync();
        return (tenant, category, attribute, value);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The condition was not met within 10 seconds.");
            await Task.Delay(25);
        }
    }
}
