using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Imports;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Temel Ürün Bilgileri are the model's (owner, 14.11.2026: "Bunlar ortak değerlerdir, her varyantında eşit olmalı. Sadece
// alttaki varyantlar kısmındaki değerler birbirinden farklı"). An MZ056-shaped model whose colour cards drifted apart:
// the editor opens with one value from every colour row, a save (the editor names every model field) makes every card
// equal even when the owner changed nothing, and the variants' codes, prices and marketplace prices stay their own.
[Collection("postgres-integration")]
public sealed class FamilyModelFieldsPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    private const string Name = "Dik Çizgi Desenli Anne Penye Bluz";

    [PostgreSqlFact]
    public async Task ASave_FromAnyColourRow_MakesTheModelFieldsEqual_AndLeavesEveryVariantsOwnValues()
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"mz056-{Guid.NewGuid():N}", DisplayName = "mz056", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var connection = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = "TRENDYOL", Environment = "PRODUCTION", DisplayName = "Trendyol model alanları", ExternalStoreId = $"store-{Guid.NewGuid():N}", Status = "ACTIVE", ApiVersion = "V1", SettingsJson = "{}", Version = 1 };
        var categoryId = Guid.CreateVersion7();
        var brandId = Guid.CreateVersion7();
        Product Card(string colour, string title, string description, int? warranty, int minute) => new() { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Title = title, Description = description, CategoryId = categoryId, BrandId = brandId, VatRate = 10m, VatInclusion = "INCLUDED", WarrantyMonths = warranty, DeliveryDays = 1, Status = ProductStatus.Active, CreatedAt = now.AddMinutes(minute), UpdatedAt = now, Version = 1 };
        // The Gri card holds the Zümrüt card's name (a copy), Lacivert a name without colour, a description and a warranty of its own
        // (no warranty is stored as null).
        var gri = Card("Gri", $"Kadın Zümrüt {Name}", "Model açıklaması", null, 0);
        var lacivert = Card("Lacivert", $"Kadın {Name}", "Lacivert eski açıklama", 24, 1);
        var zumrut = Card("Zümrüt", $"Kadın Zümrüt {Name}", "Model açıklaması", null, 2);
        var cards = new[] { (gri, "Gri"), (lacivert, "Lacivert"), (zumrut, "Zümrüt") };
        var variants = new List<ProductVariant>();
        var offers = new List<ChannelOffer>();
        var index = 0;
        foreach (var (card, colour) in cards)
            foreach (var size in new[] { "L", "XL" })
            {
                index++;
                var code = $"MZ056-{index:00}";
                var variant = new ProductVariant { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProductId = card.Id, Sku = code, SkuNormalized = code, Barcode = $"86900{index:00}", BarcodeNormalized = $"86900{index:00}", ModelCode = "MZ056", OptionSignature = $"Renk: {colour} | Beden: {size}", Desi = 1m, DefaultListPrice = 900m + index, DefaultSalePrice = 600m + index, Status = ProductStatus.Active, SortOrder = size == "L" ? 0 : 1, CreatedAt = now, UpdatedAt = now, Version = 1 };
                variants.Add(variant);
                // Each variant's own marketplace price ("Özel Fiyat"), apart from its panel price.
                offers.Add(new ChannelOffer { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, VariantId = variant.Id, ListPrice = 1000m + index, SalePrice = 700m + index, Currency = "TRY", VatRate = 10m, VatInclusion = "INCLUDED", RoundingMode = "HALF_EVEN", Status = "ACTIVE", FollowsPanelPrice = false, PriceVersion = 1, Version = 1 });
            }

        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        db.Tenants.Add(tenant);
        db.PlatformConnections.Add(connection);
        db.Categories.Add(new Category { Id = categoryId, TenantId = tenant.Id, Name = "Bluz", NormalizedName = "BLUZ", Path = "Bluz", IsLeaf = true, IsActive = true, CreatedAt = now, UpdatedAt = now, Version = 1 });
        db.Brands.Add(new Brand { Id = brandId, TenantId = tenant.Id, Name = "Ravencia", NormalizedName = "RAVENCIA", IsActive = true, CreatedAt = now, UpdatedAt = now, Version = 1 });
        db.Products.AddRange(gri, lacivert, zumrut);
        db.ProductVariants.AddRange(variants);
        foreach (var variant in variants)
            db.InventoryItems.Add(new InventoryItem { Id = Guid.CreateVersion7(), TenantId = tenant.Id, VariantId = variant.Id, LocationCode = "MAIN", OnHand = 5m, Available = 5m, ProjectionVersion = 1, Version = 1 });
        db.ChannelOffers.AddRange(offers);
        await db.SaveChangesAsync();
        var variantsBefore = variants.Select(variant => (variant.Id, variant.ProductId, variant.Sku, variant.Barcode, variant.OptionSignature, variant.DefaultListPrice, variant.DefaultSalePrice)).ToList();
        var offersBefore = offers.Select(offer => (offer.VariantId, offer.ListPrice, offer.SalePrice, offer.FollowsPanelPrice, offer.PriceVersion)).ToList();
        db.ChangeTracker.Clear();

        var service = new CatalogService(db, new CursorCodec(new EphemeralDataProtectionProvider(), TimeProvider.System), new ConfigurationBuilder().Build(), fixture.TimeProvider, new MemoryCache(new MemoryCacheOptions()));

        // The editor opens with one value per model field from every colour row: what most cards hold, the name in the
        // opened card's colour.
        foreach (var (card, colour) in cards)
        {
            var opened = (await service.GetProductAsync(tenant.Id, card.Id, CancellationToken.None)).Value!;
            Assert.Equal($"Kadın {colour} {Name}", opened.Title);
            Assert.Equal("Model açıklaması", opened.Description);
            Assert.Null(opened.WarrantyMonths);
        }

        // Kaydet from the Lacivert row with the fields as the editor showed them (the owner touched nothing): every card
        // takes the model's values.
        var shown = (await service.GetProductAsync(tenant.Id, lacivert.Id, CancellationToken.None)).Value!;
        var saved = await service.UpdateProductAsync(tenant.Id, lacivert.Id, shown.Version, Command(shown, shown.Title), CancellationToken.None);
        Assert.True(saved.Succeeded, saved.Error?.Message);
        db.ChangeTracker.Clear();
        await AssertCards(colour => $"Kadın {colour} {Name}");

        // A changed name from the Gri row: every card takes it, each with its own colour word.
        shown = (await service.GetProductAsync(tenant.Id, gri.Id, CancellationToken.None)).Value!;
        saved = await service.UpdateProductAsync(tenant.Id, gri.Id, shown.Version, Command(shown, $"Kadın Gri Yeni {Name}"), CancellationToken.None);
        Assert.True(saved.Succeeded, saved.Error?.Message);
        db.ChangeTracker.Clear();
        await AssertCards(colour => $"Kadın {colour} Yeni {Name}");
        await transaction.RollbackAsync();

        async Task AssertCards(Func<string, string> title)
        {
            var stored = await db.Products.AsNoTracking().Where(product => product.TenantId == tenant.Id).ToListAsync();
            foreach (var (card, colour) in cards)
            {
                var product = stored.Single(item => item.Id == card.Id);
                Assert.Equal(title(colour), product.Title);
                Assert.Equal("Model açıklaması", product.Description);
                Assert.Null(product.WarrantyMonths);
                Assert.Equal((10m, "INCLUDED"), (product.VatRate, product.VatInclusion));
            }
            // The variant rows stay each variant's own: codes, barcodes, colour/size and prices, and every marketplace price.
            var storedVariants = await db.ProductVariants.AsNoTracking().Where(variant => variant.TenantId == tenant.Id).ToListAsync();
            Assert.Equal(variantsBefore, variantsBefore.Select(before => storedVariants.Single(variant => variant.Id == before.Id)).Select(variant => (variant.Id, variant.ProductId, variant.Sku, variant.Barcode, variant.OptionSignature, variant.DefaultListPrice, variant.DefaultSalePrice)).ToList());
            var storedOffers = await db.ChannelOffers.AsNoTracking().Where(offer => offer.TenantId == tenant.Id).ToListAsync();
            Assert.Equal(offersBefore, offersBefore.Select(before => storedOffers.Single(offer => offer.VariantId == before.VariantId)).Select(offer => (offer.VariantId, offer.ListPrice, offer.SalePrice, offer.FollowsPanelPrice, offer.PriceVersion)).ToList());
        }
    }

    private static UpdateProductCommand Command(ProductView shown, string title) =>
        new(title, shown.Description, shown.BrandId, shown.CategoryId, WarrantyMonths: shown.WarrantyMonths, VatRate: shown.VatRate, VatInclusion: shown.VatInclusion, DeliveryDays: shown.DeliveryDays, FamilySharedFields: ProductFamilySharedFields.All);
}
