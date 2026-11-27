using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// KATALOG-8: Toplu Değer Ata and the products file read only the cards that may hold the model's key
// (ModelCodeOwnership.ProductIdsWithKeys) instead of every coded variant of the tenant. The same colour cards still
// come back: a code differing only in case or spacing, a code with a letter outside ASCII and the Turkish letters
// (keyed in memory), and never a card of another code.
[Collection("postgres-integration")]
public sealed class PostgreSqlModelCardsNarrowingTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    private sealed record Seed(Guid TenantId, Product Black, Product Lower, Product Accent, Product Other);

    private static async Task<Seed> SeedAsync(AppDbContext db)
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"model-narrow-{Guid.NewGuid():N}", DisplayName = "model narrow", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var categoryId = Guid.CreateVersion7();
        var brandId = Guid.CreateVersion7();
        db.Tenants.Add(tenant);
        db.Categories.Add(new Category { Id = categoryId, TenantId = tenant.Id, Name = "Etek", NormalizedName = "ETEK", Path = "Etek", IsLeaf = true, IsActive = true, CreatedAt = now, UpdatedAt = now, Version = 1 });
        db.Brands.Add(new Brand { Id = brandId, TenantId = tenant.Id, Name = "Ravencia", NormalizedName = "RAVENCIA", IsActive = true, CreatedAt = now, UpdatedAt = now, Version = 1 });
        var black = AddCard(db, tenant.Id, categoryId, brandId, "KENE77", "Siyah", 0);
        var lower = AddCard(db, tenant.Id, categoryId, brandId, " kene77 ", "Mavi", 1);
        // É folds to E in the catalog key, but the database does not key it: it is kept for the in-memory key check.
        var accent = AddCard(db, tenant.Id, categoryId, brandId, "KENÉ77", "Gri", 2);
        var other = AddCard(db, tenant.Id, categoryId, brandId, "KENE78", "Beyaz", 3);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return new Seed(tenant.Id, black, lower, accent, other);
    }

    private static Product AddCard(AppDbContext db, Guid tenantId, Guid categoryId, Guid brandId, string modelCode, string colour, int minute)
    {
        var now = DateTimeOffset.UtcNow;
        var card = new Product { Id = Guid.CreateVersion7(), TenantId = tenantId, Title = $"Kadın {colour} Etek", Description = "Açıklama", CategoryId = categoryId, BrandId = brandId, VatRate = 10m, VatInclusion = "INCLUDED", DeliveryDays = 1, DefaultListPrice = 900m, DefaultSalePrice = 600m, Status = ProductStatus.Active, CreatedAt = now.AddMinutes(minute), UpdatedAt = now, Version = 1 };
        db.Products.Add(card);
        var index = 0;
        foreach (var size in new[] { "M", "L" })
        {
            var sku = $"N8-{colour}-{size}-{Guid.NewGuid():N}"[..36];
            db.ProductVariants.Add(new ProductVariant { Id = Guid.CreateVersion7(), TenantId = tenantId, ProductId = card.Id, Sku = sku, SkuNormalized = sku.ToUpperInvariant(), ModelCode = modelCode, OptionSignature = $"Renk: {colour} | Beden: {size}", Desi = 1m, DefaultListPrice = 900m, DefaultSalePrice = 600m, Status = ProductStatus.Active, SortOrder = index++, CreatedAt = now, UpdatedAt = now, Version = 1 });
        }
        return card;
    }

    private static Task<Dictionary<Guid, Product>> CardsAsync(AppDbContext db, Seed seed) =>
        (ModelFieldCopiesTestPosition.CopiesWritten ? db.Products.AsNoTracking() : EffectiveCatalog.Products(db)).Where(x => x.TenantId == seed.TenantId).ToDictionaryAsync(x => x.Id);

    [PostgreSqlFact]
    public async Task TheProductsFile_WritesTheVatToTheSameColourCards()
    {
        await using var db = fixture.CreateContext();
        var seed = await SeedAsync(db);

        var applied = await new ProductTransferService(db, fixture.TimeProvider).ApplyAsync(seed.TenantId, Guid.CreateVersion7(),
            new ApplyProductTransferCommand([], [new ProductTransferProductChange(seed.Black.Id, 1, null, 20m)]), $"model-narrow-{Guid.NewGuid():N}", CancellationToken.None);
        Assert.True(applied.Succeeded, applied.Error?.Message);
        db.ChangeTracker.Clear();

        var cards = await CardsAsync(db, seed);
        Assert.Equal([20m, 20m, 20m, 10m], [cards[seed.Black.Id].VatRate, cards[seed.Lower.Id].VatRate, cards[seed.Accent.Id].VatRate, cards[seed.Other.Id].VatRate]);
        Assert.Equal(1L, cards[seed.Other.Id].Version);
    }

    [PostgreSqlFact]
    public async Task BulkField_WritesTheVatToTheSameColourCards()
    {
        await using var db = fixture.CreateContext();
        var seed = await SeedAsync(db);
        var service = new CatalogService(db, null!, new ConfigurationBuilder().Build(), fixture.TimeProvider, new MemoryCache(new MemoryCacheOptions()));

        var result = await service.BulkSetFieldAsync(seed.TenantId, new BulkProductFieldCommand([seed.Black.Id], BulkProductFields.VatRate, "20"), CancellationToken.None, Guid.CreateVersion7(), "model-narrow");
        Assert.True(result.Succeeded, result.Error?.Message);
        db.ChangeTracker.Clear();

        var cards = await CardsAsync(db, seed);
        Assert.Equal([20m, 20m, 20m, 10m], [cards[seed.Black.Id].VatRate, cards[seed.Lower.Id].VatRate, cards[seed.Accent.Id].VatRate, cards[seed.Other.Id].VatRate]);
        Assert.Equal(1L, cards[seed.Other.Id].Version);
    }
}
