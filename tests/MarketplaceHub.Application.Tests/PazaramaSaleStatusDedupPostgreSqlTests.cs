using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// K1 (scan 15 Nov 2026): a Pazarama open/close-for-sale request is saved under "{dedup}:{jobId}", but the open-job check
// looked for the bare dedup key, so it never matched and every request opened a new job. The same barcodes asked again
// while the first job waits get that job back; once it has ended, a new one is opened.
[Collection("postgres-integration")]
public sealed class PazaramaSaleStatusDedupPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    [PostgreSqlFact]
    public async Task ASecondRequestForTheSameBarcodesReturnsTheOpenJob()
    {
        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var now = fixture.Now;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"pzr-sale-{Guid.NewGuid():N}", DisplayName = "pzr-sale", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var connection = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = "PAZARAMA", Environment = "PRODUCTION", DisplayName = "Pazarama", ExternalStoreId = $"store-{Guid.NewGuid():N}", Status = "ACTIVE", ApiVersion = "V1", SettingsJson = """{"ExternalWritesEnabled":true}""" };
        var product = new Product { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Title = "Ürün", Description = "", Status = ProductStatus.Active, CreatedAt = now, UpdatedAt = now };
        var variant = new ProductVariant { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProductId = product.Id, Sku = "PZ-1", SkuNormalized = "PZ-1", ModelCode = "PZ-1", OptionSignature = "PZ-1", CreatedAt = now, UpdatedAt = now };
        db.AddRange(tenant, connection, product, variant,
            new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, VariantId = variant.Id, ExternalId = "8690000000017" });
        await db.SaveChangesAsync();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["FeatureFlags:ExternalWrites"] = "true" }).Build();
        var service = new CatalogService(db, null!, configuration, fixture.TimeProvider, new MemoryCache(new MemoryCacheOptions()));

        var first = await service.EnqueuePazaramaSaleStatusAsync(tenant.Id, connection.Id, ["8690000000017"], close: true, "key-1", "corr-1", CancellationToken.None);
        var second = await service.EnqueuePazaramaSaleStatusAsync(tenant.Id, connection.Id, ["8690000000017"], close: true, "key-2", "corr-2", CancellationToken.None);

        Assert.True(first.Succeeded, first.Error?.Message);
        Assert.True(second.Succeeded, second.Error?.Message);
        Assert.Equal(first.Value, second.Value);
        Assert.Equal(1, await db.IntegrationJobs.CountAsync(x => x.TenantId == tenant.Id && x.JobType == MarketplaceJobTypes.ProductArchive));

        // The opposite request (open for sale) is another job; so is the same request once the first has ended.
        var open = await service.EnqueuePazaramaSaleStatusAsync(tenant.Id, connection.Id, ["8690000000017"], close: false, "key-3", "corr-3", CancellationToken.None);
        Assert.NotEqual(first.Value, open.Value);
        await db.IntegrationJobs.Where(x => x.Id == first.Value).ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Status, JobStatus.Succeeded));
        var again = await service.EnqueuePazaramaSaleStatusAsync(tenant.Id, connection.Id, ["8690000000017"], close: true, "key-4", "corr-4", CancellationToken.None);
        Assert.NotEqual(first.Value, again.Value);

        await transaction.RollbackAsync();
    }
}
