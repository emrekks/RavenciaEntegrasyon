using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using MarketplaceHub.Infrastructure.Persistence.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// K3 (owner decision of 6 Nov 2026 on audit 2026-10-24): a cost of 0 means "not entered" (unknown stays unknown): it is
// never stored, it counts as a missing cost, and the zeros already stored become empty.
[Collection("postgres-integration")]
public sealed class CatalogZeroCostPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    [PostgreSqlFact]
    public async Task ProductSave_WithACostOfZero_StoresNoCost()
    {
        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var (tenant, product, variant) = await SeedAsync(db, "k3-editor", 40m);
        var service = NewCatalog(db);

        var saved = await service.UpdateProductAsync(tenant.Id, product.Id, product.Version, new UpdateProductCommand(product.Title, product.Description, null, null,
            VariantUpdates: [new UpdateVariantCommand(variant.Id, variant.Sku, null, null, 0, CostPrice: 0m, LoadedCostPrice: 40m)]), CancellationToken.None);
        Assert.True(saved.Succeeded, saved.Error?.Message);
        db.ChangeTracker.Clear();
        Assert.Null((await db.ProductVariants.AsNoTracking().SingleAsync(x => x.Id == variant.Id)).CostPrice);

        var created = await service.CreateProductAsync(tenant.Id, new CreateProductCommand($"Sıfır maliyet {Guid.NewGuid():N}", "Açıklama", null, null,
            [new CreateVariantCommand($"K3-{Guid.NewGuid():N}"[..16], null, null, CostPrice: 0m)], Status: "DRAFT"), CancellationToken.None);
        Assert.True(created.Succeeded, created.Error?.Message);
        db.ChangeTracker.Clear();
        Assert.Null((await db.ProductVariants.AsNoTracking().SingleAsync(x => x.ProductId == created.Value!.Id)).CostPrice);
        await transaction.RollbackAsync();
    }

    [PostgreSqlFact]
    public async Task CostSummary_CountsAStoredZeroCostAsMissing()
    {
        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var (tenant, _, _) = await SeedAsync(db, "k3-summary", 0m);

        var summary = await new ProductTransferService(db, fixture.TimeProvider).SummaryAsync(tenant.Id, CancellationToken.None);

        Assert.True(summary.Succeeded, summary.Error?.Message);
        Assert.Equal(1, summary.Value!.MissingCostCount);
        await transaction.RollbackAsync();
    }

    // The migration turns the stored zero costs into empty ones and leaves every other cost as it is; a second run
    // changes nothing.
    [PostgreSqlFact]
    public async Task Migration_TurnsStoredZeroCostsIntoEmptyOnes()
    {
        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var (tenant, _, zero) = await SeedAsync(db, "k3-migration", 0m);
        var (_, _, priced) = await SeedAsync(db, "k3-migration-priced", 25m, tenant);

        var scoped = ZeroCostsToNull.Clear
            .Replace("WHERE \"CostPrice\" = 0", $"WHERE \"TenantId\" = '{tenant.Id}' AND \"CostPrice\" = 0", StringComparison.Ordinal)
            .Replace("WHERE \"UnitCostSnapshot\" = 0", $"WHERE \"TenantId\" = '{tenant.Id}' AND \"UnitCostSnapshot\" = 0", StringComparison.Ordinal);
        Assert.Equal(1, await db.Database.ExecuteSqlRawAsync(scoped));
        Assert.Equal(0, await db.Database.ExecuteSqlRawAsync(scoped));

        var stored = await db.ProductVariants.AsNoTracking().Where(x => x.TenantId == tenant.Id).ToDictionaryAsync(x => x.Id, x => x.CostPrice);
        Assert.Null(stored[zero.Id]);
        Assert.Equal(25m, stored[priced.Id]);
        await transaction.RollbackAsync();
    }

    private async Task<(Tenant Tenant, Product Product, ProductVariant Variant)> SeedAsync(AppDbContext db, string code, decimal cost, Tenant? existingTenant = null)
    {
        var now = fixture.Now;
        var tenant = existingTenant ?? new Tenant { Id = Guid.CreateVersion7(), Code = $"{code}-{Guid.NewGuid():N}", DisplayName = code, Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var product = new Product { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Title = $"Ürün {code} {Guid.NewGuid():N}", Description = "Açıklama", VatRate = 10m, VatInclusion = "INCLUDED", Status = ProductStatus.Active, CreatedAt = now, UpdatedAt = now, Version = 1 };
        var sku = $"{code}-{Guid.NewGuid():N}"[..24].ToUpperInvariant();
        var variant = new ProductVariant { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProductId = product.Id, Sku = sku, SkuNormalized = sku, OptionSignature = "Beden: 0", CostPrice = cost, CreatedAt = now, UpdatedAt = now, Version = 1 };
        if (existingTenant is null) db.Tenants.Add(tenant);
        db.Products.Add(product); db.ProductVariants.Add(variant);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return (tenant, product, variant);
    }

    private CatalogService NewCatalog(AppDbContext db) =>
        new(db, null!, new ConfigurationBuilder().Build(), fixture.TimeProvider, new MemoryCache(new MemoryCacheOptions()));
}
