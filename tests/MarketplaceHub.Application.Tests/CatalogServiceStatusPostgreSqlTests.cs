using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// KATALOG-4: the status a product save asks for is read in any case and an unknown one is refused, never taken as a draft.
// KATALOG-11: a status-only save that opens a model leaves a colour card missing a required field closed.
[Collection("postgres-integration")]
public sealed class CatalogServiceStatusPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    private sealed record Seed(Tenant Tenant, Guid BrandId, Guid CategoryId);

    private static async Task<Seed> SeedAsync(AppDbContext db)
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"status-{Guid.NewGuid():N}", DisplayName = "status", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var seed = new Seed(tenant, Guid.CreateVersion7(), Guid.CreateVersion7());
        db.Tenants.Add(tenant);
        db.Categories.Add(new Category { Id = seed.CategoryId, TenantId = tenant.Id, Name = "Bluz", NormalizedName = "BLUZ", Path = "Bluz", IsLeaf = true, IsActive = true, CreatedAt = now, UpdatedAt = now, Version = 1 });
        db.Brands.Add(new Brand { Id = seed.BrandId, TenantId = tenant.Id, Name = "Ravencia", NormalizedName = "RAVENCIA", IsActive = true, CreatedAt = now, UpdatedAt = now, Version = 1 });
        await db.SaveChangesAsync();
        return seed;
    }

    private static Product AddCard(AppDbContext db, Seed seed, string modelCode, string colour, ProductStatus status, bool withBarcode = true)
    {
        var now = DateTimeOffset.UtcNow;
        var card = new Product { Id = Guid.CreateVersion7(), TenantId = seed.Tenant.Id, Title = $"Kadın {colour} Bluz", Description = "Açıklama", CategoryId = seed.CategoryId, BrandId = seed.BrandId, VatRate = 10m, VatInclusion = "INCLUDED", DeliveryDays = 1, DefaultListPrice = 900m, DefaultSalePrice = 600m, Status = status, CreatedAt = now, UpdatedAt = now, Version = 1 };
        db.Products.Add(card);
        var index = 0;
        foreach (var size in new[] { "S", "M" })
        {
            var sku = $"{modelCode}-{colour}-{size}-{Guid.NewGuid():N}"[..36];
            var barcode = withBarcode ? $"869{Random.Shared.NextInt64(1_000_000_000L, 9_999_999_999L)}" : null;
            db.ProductVariants.Add(new ProductVariant { Id = Guid.CreateVersion7(), TenantId = seed.Tenant.Id, ProductId = card.Id, Sku = sku, SkuNormalized = sku.ToUpperInvariant(), Barcode = barcode, BarcodeNormalized = barcode, ModelCode = modelCode, OptionSignature = $"Renk: {colour} | Beden: {size}", Desi = 1m, DefaultListPrice = 900m, DefaultSalePrice = 600m, Status = status, SortOrder = index++, CreatedAt = now, UpdatedAt = now, Version = 1 });
        }
        return card;
    }

    private CatalogService Service(AppDbContext db) =>
        new(db, new CursorCodec(new EphemeralDataProtectionProvider(), TimeProvider.System), new ConfigurationBuilder().Build(), fixture.TimeProvider, new MemoryCache(new MemoryCacheOptions()));

    private static UpdateProductCommand StatusOnly(ProductView shown, string status) => new(shown.Title, shown.Description, shown.BrandId, shown.CategoryId, Status: status);

    [PostgreSqlFact]
    public async Task AStatus_IsReadInAnyCase_AndAnUnknownOneIsRefused()
    {
        await using var db = fixture.CreateContext();
        var seed = await SeedAsync(db);
        var card = AddCard(db, seed, "ST100", "Siyah", ProductStatus.Active);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var service = Service(db);
        var shown = (await service.GetProductAsync(seed.Tenant.Id, card.Id, CancellationToken.None)).Value!;

        var unknown = await service.UpdateProductAsync(seed.Tenant.Id, card.Id, shown.Version, StatusOnly(shown, "PASSIVE"), CancellationToken.None);
        Assert.Equal(("VALIDATION_FAILED", 422), (unknown.Error!.Code, unknown.Error.Status));
        Assert.True(unknown.Error.FieldErrors!.ContainsKey("status"));

        var draft = await service.UpdateProductAsync(seed.Tenant.Id, card.Id, shown.Version, StatusOnly(shown, "draft"), CancellationToken.None);
        Assert.Equal((ProductDraftPolicy.DraftNotAllowedCode, 409), (draft.Error!.Code, draft.Error.Status));

        var lower = await service.UpdateProductAsync(seed.Tenant.Id, card.Id, shown.Version, StatusOnly(shown, "active"), CancellationToken.None);
        Assert.True(lower.Succeeded, lower.Error?.Message);
        db.ChangeTracker.Clear();
        Assert.Equal(ProductStatus.Active, (await db.Products.AsNoTracking().SingleAsync(x => x.Id == card.Id)).Status);
        Assert.All(await db.ProductVariants.AsNoTracking().Where(x => x.ProductId == card.Id).ToListAsync(), variant => Assert.Equal(ProductStatus.Active, variant.Status));

        var created = await service.CreateProductAsync(seed.Tenant.Id, new CreateProductCommand("Kadın Siyah Bluz", "Açıklama", seed.BrandId, seed.CategoryId, [new CreateVariantCommand($"YK-{Guid.NewGuid():N}"[..20], null, "YK1")], Status: "Passive"), CancellationToken.None);
        Assert.Equal(("VALIDATION_FAILED", 422), (created.Error!.Code, created.Error.Status));
    }

    [PostgreSqlFact]
    public async Task AStatusOnlySave_OpeningAModel_LeavesAnIncompleteColourClosed()
    {
        await using var db = fixture.CreateContext();
        var seed = await SeedAsync(db);
        var siyah = AddCard(db, seed, "ST200", "Siyah", ProductStatus.Archived);
        var mavi = AddCard(db, seed, "ST200", "Mavi", ProductStatus.Archived, withBarcode: false);
        var gri = AddCard(db, seed, "ST200", "Gri", ProductStatus.Archived);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var service = Service(db);
        var shown = (await service.GetProductAsync(seed.Tenant.Id, siyah.Id, CancellationToken.None)).Value!;

        var opened = await service.UpdateProductAsync(seed.Tenant.Id, siyah.Id, shown.Version, StatusOnly(shown, "ACTIVE"), CancellationToken.None);
        Assert.True(opened.Succeeded, opened.Error?.Message);
        db.ChangeTracker.Clear();
        var statuses = await db.Products.AsNoTracking().Where(x => x.TenantId == seed.Tenant.Id).ToDictionaryAsync(x => x.Id, x => x.Status);
        Assert.Equal((ProductStatus.Active, ProductStatus.Archived, ProductStatus.Active), (statuses[siyah.Id], statuses[mavi.Id], statuses[gri.Id]));
        Assert.All(await db.ProductVariants.AsNoTracking().Where(x => x.ProductId == mavi.Id).ToListAsync(), variant => Assert.Equal(ProductStatus.Archived, variant.Status));
    }
}
