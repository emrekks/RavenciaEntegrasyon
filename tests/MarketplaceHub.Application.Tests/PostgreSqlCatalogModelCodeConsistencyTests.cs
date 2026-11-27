using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// KATALOG-1: the model code is the model's, one value on every row of a card. The editor's save, a new card and the
// products file each refuse a change that would leave a card's rows on different codes; every row changed together goes.
[Collection("postgres-integration")]
public sealed class PostgreSqlCatalogModelCodeConsistencyTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    private sealed record Seed(Tenant Tenant, Guid BrandId, Guid CategoryId);

    private static async Task<Seed> SeedAsync(AppDbContext db)
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"model-code-{Guid.NewGuid():N}", DisplayName = "model code", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var seed = new Seed(tenant, Guid.CreateVersion7(), Guid.CreateVersion7());
        db.Tenants.Add(tenant);
        db.Categories.Add(new Category { Id = seed.CategoryId, TenantId = tenant.Id, Name = "Bluz", NormalizedName = "BLUZ", Path = "Bluz", IsLeaf = true, IsActive = true, CreatedAt = now, UpdatedAt = now, Version = 1 });
        db.Brands.Add(new Brand { Id = seed.BrandId, TenantId = tenant.Id, Name = "Ravencia", NormalizedName = "RAVENCIA", IsActive = true, CreatedAt = now, UpdatedAt = now, Version = 1 });
        await db.SaveChangesAsync();
        return seed;
    }

    private static Product AddCard(AppDbContext db, Seed seed, string modelCode, string colour)
    {
        var now = DateTimeOffset.UtcNow;
        var card = new Product { Id = Guid.CreateVersion7(), TenantId = seed.Tenant.Id, Title = $"Kadın {colour} Bluz", Description = "Açıklama", CategoryId = seed.CategoryId, BrandId = seed.BrandId, VatRate = 10m, VatInclusion = "INCLUDED", DeliveryDays = 1, DefaultListPrice = 900m, DefaultSalePrice = 600m, Status = ProductStatus.Active, CreatedAt = now, UpdatedAt = now, Version = 1 };
        db.Products.Add(card);
        var index = 0;
        foreach (var size in new[] { "S", "M", "L" })
        {
            var sku = $"{modelCode}-{colour}-{size}-{Guid.NewGuid():N}"[..36];
            var barcode = $"869{Random.Shared.NextInt64(1_000_000_000L, 9_999_999_999L)}";
            db.ProductVariants.Add(new ProductVariant { Id = Guid.CreateVersion7(), TenantId = seed.Tenant.Id, ProductId = card.Id, Sku = sku, SkuNormalized = sku.ToUpperInvariant(), Barcode = barcode, BarcodeNormalized = barcode, ModelCode = modelCode, OptionSignature = $"Renk: {colour} | Beden: {size}", Desi = 1m, DefaultListPrice = 900m, DefaultSalePrice = 600m, Status = ProductStatus.Active, SortOrder = index++, CreatedAt = now, UpdatedAt = now, Version = 1 });
        }
        return card;
    }

    private CatalogService Service(AppDbContext db) =>
        new(db, new CursorCodec(new EphemeralDataProtectionProvider(), TimeProvider.System), new ConfigurationBuilder().Build(), fixture.TimeProvider, new MemoryCache(new MemoryCacheOptions()));

    private static UpdateProductCommand Save(ProductView shown, Func<ProductVariantView, int, string?> code) =>
        new(shown.Title, shown.Description, shown.BrandId, shown.CategoryId, WarrantyMonths: shown.WarrantyMonths ?? 0, VatRate: shown.VatRate, VatInclusion: shown.VatInclusion, DeliveryDays: shown.DeliveryDays,
            VariantUpdates: [.. shown.Variants.Select((variant, index) => new UpdateVariantCommand(variant.Id, variant.Sku, variant.Barcode, code(variant, index), index, LoadedVersion: variant.Version))]);

    private static Task<List<string?>> CodesAsync(AppDbContext db, Guid productId) =>
        db.ProductVariants.AsNoTracking().Where(x => x.ProductId == productId).OrderBy(x => x.SortOrder).Select(x => x.ModelCode).ToListAsync();

    [PostgreSqlFact]
    public async Task AnEditorSave_RecodingOneRowOnly_IsRefusedWhole_AndEveryRowTogetherGoesThrough()
    {
        await using var db = fixture.CreateContext();
        var seed = await SeedAsync(db);
        var siyah = AddCard(db, seed, "MZ100", "Siyah");
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var service = Service(db);
        var shown = (await service.GetProductAsync(seed.Tenant.Id, siyah.Id, CancellationToken.None)).Value!;

        var refused = await service.UpdateProductAsync(seed.Tenant.Id, siyah.Id, shown.Version, Save(shown, (variant, index) => index == 0 ? "MZ100A" : variant.ModelCode), CancellationToken.None);
        Assert.False(refused.Succeeded);
        Assert.Equal((CatalogService.ModelCodeMixedCode, 422), (refused.Error!.Code, refused.Error.Status));
        db.ChangeTracker.Clear();
        Assert.Equal(["MZ100", "MZ100", "MZ100"], await CodesAsync(db, siyah.Id));

        var saved = await service.UpdateProductAsync(seed.Tenant.Id, siyah.Id, shown.Version, Save(shown, (_, _) => "MZ100A"), CancellationToken.None);
        Assert.True(saved.Succeeded, saved.Error?.Message);
        db.ChangeTracker.Clear();
        Assert.Equal(["MZ100A", "MZ100A", "MZ100A"], await CodesAsync(db, siyah.Id));
    }

    [PostgreSqlFact]
    public async Task ANewCard_WithTwoModelCodes_IsRefused()
    {
        await using var db = fixture.CreateContext();
        var seed = await SeedAsync(db);
        var service = Service(db);
        var refused = await service.CreateProductAsync(seed.Tenant.Id, new CreateProductCommand("Kadın Siyah Bluz", "Açıklama", seed.BrandId, seed.CategoryId,
            [new CreateVariantCommand($"YK1-S-{Guid.NewGuid():N}"[..20], null, "YK1"), new CreateVariantCommand($"YK2-M-{Guid.NewGuid():N}"[..20], null, "YK2")], Status: "DRAFT"), CancellationToken.None);
        Assert.Equal((CatalogService.ModelCodeMixedCode, 422), (refused.Error!.Code, refused.Error.Status));
        Assert.False(await db.Products.AnyAsync(x => x.TenantId == seed.Tenant.Id));
    }

    // KATALOG-3: the name and the VAT the products file writes on one colour card go to every colour card of the model
    // (each with its own colour word), so the editor's next save does not take them back; another brand's card (another
    // model: KATALOG-14 phase D5b, a code is one model) is not touched.
    [PostgreSqlFact]
    public async Task AProductsFile_NameAndVatOnOneColour_GoToEveryColourOfTheModel()
    {
        await using var db = fixture.CreateContext();
        var seed = await SeedAsync(db);
        var siyah = AddCard(db, seed, "MZ600", "Siyah");
        var mavi = AddCard(db, seed, "MZ600", "Mavi");
        var otherBrand = Guid.CreateVersion7();
        db.Brands.Add(new Brand { Id = otherBrand, TenantId = seed.Tenant.Id, Name = "Lavian", NormalizedName = "LAVIAN", IsActive = true, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, Version = 1 });
        var lavian = AddCard(db, seed, "MZ601", "Gri");
        lavian.BrandId = otherBrand;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var service = new ProductTransferService(db, fixture.TimeProvider);
        var applied = await service.ApplyAsync(seed.Tenant.Id, Guid.CreateVersion7(), new ApplyProductTransferCommand([], [new ProductTransferProductChange(siyah.Id, 1, "Kadın Siyah Yırtmaçlı Bluz", 20m)]), "transfer-model-fields", CancellationToken.None);
        Assert.True(applied.Succeeded, applied.Error?.Message);
        db.ChangeTracker.Clear();
        var cards = await EffectiveCatalog.Products(db).Where(x => x.TenantId == seed.Tenant.Id).ToDictionaryAsync(x => x.Id);
        Assert.Equal(("Kadın Siyah Yırtmaçlı Bluz", 20m), (cards[siyah.Id].Title, cards[siyah.Id].VatRate));
        Assert.Equal(("Kadın Mavi Yırtmaçlı Bluz", 20m, "INCLUDED"), (cards[mavi.Id].Title, cards[mavi.Id].VatRate, cards[mavi.Id].VatInclusion));
        Assert.Equal(("Kadın Gri Bluz", 10m), (cards[lavian.Id].Title, cards[lavian.Id].VatRate));
    }

    [PostgreSqlFact]
    public async Task AProductsFile_RecodingOneRowOfACard_IsRefusedOnApply()
    {
        await using var db = fixture.CreateContext();
        var seed = await SeedAsync(db);
        var siyah = AddCard(db, seed, "MZ100", "Siyah");
        await db.SaveChangesAsync();
        var first = await db.ProductVariants.AsNoTracking().Where(x => x.ProductId == siyah.Id).OrderBy(x => x.SortOrder).FirstAsync();
        var service = new ProductTransferService(db, fixture.TimeProvider);
        var refused = await service.ApplyAsync(seed.Tenant.Id, Guid.CreateVersion7(), new ApplyProductTransferCommand([new ProductTransferChange(first.Id, first.Version, false, null, false, null, SetModelCode: true, ModelCode: "MZ100A")]), "transfer-mixed", CancellationToken.None);
        Assert.Equal((CatalogService.ModelCodeMixedCode, 422), (refused.Error!.Code, refused.Error.Status));
        db.ChangeTracker.Clear();
        Assert.Equal(["MZ100", "MZ100", "MZ100"], await CodesAsync(db, siyah.Id));
    }
}
