using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using MarketplaceHub.Infrastructure.Persistence.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Owner decisions of 6 Nov 2026 on audit 2026-10-24:
// K3: a cost of 0 means "not entered" (unknown stays unknown): it is never stored and it counts as a missing cost.
// K5: activating a product (bulk or editor) reopens only the variants archived together with it, never one the
// marketplace or the seller archived on its own before.
[Collection("postgres-integration")]
public sealed class CatalogZeroCostAndArchiveReasonPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    [PostgreSqlFact]
    public async Task BulkActivate_ReopensTheVariantsArchivedWithTheProductButNotOneArchivedOnItsOwn()
    {
        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var (tenant, product, open, ownArchived) = await SeedAsync(db, "k5-bulk", 40m);
        var service = NewCatalog(db);

        Assert.True((await service.BulkSetStatusAsync(tenant.Id, new BulkProductStatusCommand([product.Id], "ARCHIVED"), CancellationToken.None)).Succeeded);
        db.ChangeTracker.Clear();
        Assert.Equal([ProductStatus.Archived, ProductStatus.Archived], await StatusesAsync(db, open, ownArchived));

        Assert.True((await service.BulkSetStatusAsync(tenant.Id, new BulkProductStatusCommand([product.Id], "ACTIVE"), CancellationToken.None)).Succeeded);
        db.ChangeTracker.Clear();
        Assert.Equal([ProductStatus.Active, ProductStatus.Archived], await StatusesAsync(db, open, ownArchived));
        await transaction.RollbackAsync();
    }

    [PostgreSqlFact]
    public async Task EditorStatusChange_ReopensTheVariantsArchivedWithTheProductButNotOneArchivedOnItsOwn()
    {
        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var (tenant, product, open, ownArchived) = await SeedAsync(db, "k5-editor", 40m);
        var service = NewCatalog(db);

        var archived = await service.UpdateProductAsync(tenant.Id, product.Id, product.Version, new UpdateProductCommand(product.Title, product.Description, null, null, Status: "ARCHIVED"), CancellationToken.None);
        Assert.True(archived.Succeeded, archived.Error?.Message);
        db.ChangeTracker.Clear();
        Assert.Equal([ProductStatus.Archived, ProductStatus.Archived], await StatusesAsync(db, open, ownArchived));

        var activated = await service.UpdateProductAsync(tenant.Id, product.Id, archived.Value!.Version, new UpdateProductCommand(product.Title, product.Description, null, null, Status: "ACTIVE", VatRate: 10m, VatInclusion: "INCLUDED"), CancellationToken.None);
        Assert.True(activated.Succeeded, activated.Error?.Message);
        db.ChangeTracker.Clear();
        Assert.Equal([ProductStatus.Active, ProductStatus.Archived], await StatusesAsync(db, open, ownArchived));
        await transaction.RollbackAsync();
    }

    [PostgreSqlFact]
    public async Task ProductSave_WithACostOfZero_StoresNoCost()
    {
        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var (tenant, product, open, _) = await SeedAsync(db, "k3-editor", 40m);
        var service = NewCatalog(db);

        var saved = await service.UpdateProductAsync(tenant.Id, product.Id, product.Version, new UpdateProductCommand(product.Title, product.Description, null, null,
            VariantUpdates: [new UpdateVariantCommand(open.Id, open.Sku, null, null, 0, CostPrice: 0m, LoadedCostPrice: 40m)]), CancellationToken.None);
        Assert.True(saved.Succeeded, saved.Error?.Message);
        db.ChangeTracker.Clear();
        Assert.Null((await db.ProductVariants.AsNoTracking().SingleAsync(x => x.Id == open.Id)).CostPrice);

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
        var (tenant, _, _, _) = await SeedAsync(db, "k3-summary", 0m);

        var summary = await new ProductTransferService(db, fixture.TimeProvider).SummaryAsync(tenant.Id, CancellationToken.None);

        Assert.True(summary.Succeeded, summary.Error?.Message);
        Assert.Equal(1, summary.Value!.MissingCostCount);
        await transaction.RollbackAsync();
    }

    // The migration's backfill: only a variant archived at the moment its product was archived counts as archived with
    // it; then activating that product reopens it and leaves the others archived. Zero costs become empty.
    [PostgreSqlFact]
    public async Task Migration_MarksOnlyVariantsArchivedWithTheirProductAndClearsZeroCosts()
    {
        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var (tenant, _, open, ownArchived) = await SeedAsync(db, "k5-backfill", 0m);
        var archivedAt = fixture.Now.AddDays(-3);
        var archivedProduct = new Product { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Title = $"Arşivli {Guid.NewGuid():N}", Description = "Açıklama", VatRate = 10m, VatInclusion = "INCLUDED", Status = ProductStatus.Archived, ArchivedAt = archivedAt, CreatedAt = archivedAt.AddDays(-10), UpdatedAt = archivedAt, Version = 1 };
        var noTimeProduct = new Product { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Title = $"Zamansız {Guid.NewGuid():N}", Description = "Açıklama", VatRate = 10m, VatInclusion = "INCLUDED", Status = ProductStatus.Archived, CreatedAt = archivedAt, UpdatedAt = archivedAt, Version = 1 };
        ProductVariant Archived(Product product, DateTimeOffset updatedAt)
        {
            var sku = $"K5B-{Guid.NewGuid():N}"[..20].ToUpperInvariant();
            return new ProductVariant { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProductId = product.Id, Sku = sku, SkuNormalized = sku, OptionSignature = sku, Status = ProductStatus.Archived, CostPrice = 0m, CreatedAt = archivedAt.AddDays(-10), UpdatedAt = updatedAt, Version = 1 };
        }
        var withProduct = Archived(archivedProduct, archivedAt.AddMilliseconds(400));
        var before = Archived(archivedProduct, archivedAt.AddDays(-1));
        var noTime = Archived(noTimeProduct, archivedAt);
        db.Products.AddRange(archivedProduct, noTimeProduct);
        db.ProductVariants.AddRange(withProduct, before, noTime);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var scope = $"v.\"TenantId\" = '{tenant.Id}' AND v.\"Status\" = 'Archived'";
        await db.Database.ExecuteSqlRawAsync(VariantArchivedWithProductAndZeroCostsToNull.BackfillArchivedWithProduct.Replace("v.\"Status\" = 'Archived'", scope, StringComparison.Ordinal));
        Assert.Equal(0, await db.Database.ExecuteSqlRawAsync(VariantArchivedWithProductAndZeroCostsToNull.BackfillArchivedWithProduct.Replace("v.\"Status\" = 'Archived'", scope, StringComparison.Ordinal)));
        await db.Database.ExecuteSqlRawAsync(VariantArchivedWithProductAndZeroCostsToNull.ClearZeroCosts
            .Replace("WHERE \"CostPrice\" = 0", $"WHERE \"TenantId\" = '{tenant.Id}' AND \"CostPrice\" = 0", StringComparison.Ordinal)
            .Replace("WHERE \"UnitCostSnapshot\" = 0", $"WHERE \"TenantId\" = '{tenant.Id}' AND \"UnitCostSnapshot\" = 0", StringComparison.Ordinal));

        var stored = await db.ProductVariants.AsNoTracking().Where(x => x.TenantId == tenant.Id).ToDictionaryAsync(x => x.Id);
        Assert.Equal((bool?)null, stored[open.Id].ArchivedWithProduct);
        Assert.Equal((bool?)false, stored[ownArchived.Id].ArchivedWithProduct);
        Assert.Equal((bool?)true, stored[withProduct.Id].ArchivedWithProduct);
        Assert.Equal((bool?)false, stored[before.Id].ArchivedWithProduct);
        Assert.Equal((bool?)false, stored[noTime.Id].ArchivedWithProduct);
        Assert.All(stored.Values, variant => Assert.NotEqual(0m, variant.CostPrice));
        Assert.Null(stored[open.Id].CostPrice);
        Assert.Equal(25m, stored[ownArchived.Id].CostPrice);

        Assert.True((await NewCatalog(db).BulkSetStatusAsync(tenant.Id, new BulkProductStatusCommand([archivedProduct.Id], "ACTIVE"), CancellationToken.None)).Succeeded);
        db.ChangeTracker.Clear();
        Assert.Equal([ProductStatus.Active, ProductStatus.Archived], await StatusesAsync(db, withProduct, before));
        await transaction.RollbackAsync();
    }

    private async Task<(Tenant Tenant, Product Product, ProductVariant Open, ProductVariant OwnArchived)> SeedAsync(AppDbContext db, string code, decimal openCost)
    {
        var now = fixture.Now;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"{code}-{Guid.NewGuid():N}", DisplayName = code, Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var product = new Product { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Title = $"Ürün {code} {Guid.NewGuid():N}", Description = "Açıklama", VatRate = 10m, VatInclusion = "INCLUDED", Status = ProductStatus.Active, CreatedAt = now, UpdatedAt = now, Version = 1 };
        ProductVariant Variant(int order, ProductStatus status, decimal? cost)
        {
            var sku = $"{code}-{order}-{Guid.NewGuid():N}"[..24].ToUpperInvariant();
            return new ProductVariant { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProductId = product.Id, SortOrder = order, Sku = sku, SkuNormalized = sku, OptionSignature = $"Beden: {order}", Status = status, CostPrice = cost, CreatedAt = now, UpdatedAt = now, Version = 1 };
        }
        var open = Variant(0, ProductStatus.Active, openCost);
        // Archived by the marketplace import while the product stayed on sale (MarketplaceJobProcessor: remote.Archived).
        var ownArchived = Variant(1, ProductStatus.Archived, 25m);
        db.Tenants.Add(tenant); db.Products.Add(product); db.ProductVariants.AddRange(open, ownArchived);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return (tenant, product, open, ownArchived);
    }

    private static async Task<ProductStatus[]> StatusesAsync(AppDbContext db, params ProductVariant[] variants)
    {
        var ids = variants.Select(variant => variant.Id).ToArray();
        var stored = await db.ProductVariants.AsNoTracking().Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Status);
        return [.. ids.Select(id => stored[id])];
    }

    private CatalogService NewCatalog(AppDbContext db) =>
        new(db, null!, new ConfigurationBuilder().Build(), fixture.TimeProvider, new MemoryCache(new MemoryCacheOptions()));
}
