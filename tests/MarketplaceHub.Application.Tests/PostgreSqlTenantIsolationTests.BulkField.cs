using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Ürünler › Toplu Değer Ata (owner, 12.11.2026): one field set to one value on every colour card and variant of the
// selected models; only that field changes, nothing else of the card is checked or written.
public sealed partial class PostgreSqlTenantIsolationTests
{
    private sealed record BulkFieldCardState(decimal? VatRate, string? VatInclusion, int? WarrantyMonths, int? DeliveryDays, string Description, Guid? BrandId, Guid? CategoryId, ProductStatus Status, string Desis, string SafetyStocks);

    private static async Task<BulkFieldCardState> BulkFieldStateAsync(AppDbContext db, Guid productId)
    {
        var product = await db.Products.AsNoTracking().SingleAsync(x => x.Id == productId);
        var variants = await db.ProductVariants.AsNoTracking().Where(x => x.ProductId == productId).OrderBy(x => x.SortOrder).ToListAsync();
        var variantIds = variants.Select(x => x.Id).ToArray();
        var offers = await db.ChannelOffers.AsNoTracking().Where(x => variantIds.Contains(x.VariantId)).OrderBy(x => x.Id).Select(x => x.SafetyStock).ToListAsync();
        return new(product.VatRate, product.VatInclusion, product.WarrantyMonths, product.DeliveryDays, product.Description, product.BrandId, product.CategoryId, product.Status,
            string.Join("|", variants.Select(x => x.Desi?.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) ?? "-")),
            string.Join("|", offers.Select(x => x.ToString("0", System.Globalization.CultureInfo.InvariantCulture))));
    }

    [PostgreSqlFact]
    public async Task BulkField_WritesOnlyTheChosenFieldToEveryColourCardOfTheModel()
    {
        var tenant = NewTenant("bulk-field");
        var connection = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = "TRENDYOL", Environment = "STAGE", DisplayName = "Bulk field test", ExternalStoreId = $"store-{Guid.NewGuid():N}", Status = "ACTIVE", ApiVersion = "V2", Version = 1 };
        var brand = new Brand { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Name = "Ravencia", NormalizedName = "RAVENCIA", IsActive = true, CreatedAt = fixture.Now, UpdatedAt = fixture.Now, Version = 1 };
        var otherBrand = new Brand { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Name = "Lavian", NormalizedName = "LAVIAN", IsActive = true, CreatedAt = fixture.Now, UpdatedAt = fixture.Now, Version = 1 };
        var category = new Category { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Name = "Bluz", NormalizedName = "BLUZ", Path = "Bluz", IsLeaf = true, IsActive = true, CreatedAt = fixture.Now, UpdatedAt = fixture.Now, Version = 1 };
        var otherCategory = new Category { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Name = "Gömlek", NormalizedName = "GOMLEK", Path = "Gömlek", IsLeaf = true, IsActive = true, CreatedAt = fixture.Now, UpdatedAt = fixture.Now, Version = 1 };
        Product Card(string title, int minutes) => new() { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Title = title, Description = "<p>Eski açıklama</p>", BrandId = brand.Id, CategoryId = category.Id, VatRate = 20m, VatInclusion = "EXCLUDED", WarrantyMonths = 12, DeliveryDays = 3, Status = ProductStatus.Active, CreatedAt = fixture.Now.AddMinutes(minutes), UpdatedAt = fixture.Now, Version = 1 };
        var black = Card("Bluz Siyah", 0);
        var red = Card("Bluz Kırmızı", 1);
        var unrelated = Card("Başka model", 2);
        var variants = new List<ProductVariant>();
        var offers = new List<ChannelOffer>();
        void Sizes(Product card, string modelCode, string colour)
        {
            foreach (var (size, index) in new[] { ("S", 0), ("M", 1) })
            {
                var sku = $"{modelCode}-{colour}-{size}-{Guid.NewGuid():N}";
                var variant = new ProductVariant { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProductId = card.Id, SortOrder = index, Sku = sku, SkuNormalized = sku.ToUpperInvariant(), ModelCode = modelCode, OptionSignature = $"Renk={colour}|Beden={size}", Desi = 1m, CreatedAt = fixture.Now, UpdatedAt = fixture.Now, Version = 1 };
                variants.Add(variant);
                offers.Add(new ChannelOffer { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, VariantId = variant.Id, ListPrice = 120m, SalePrice = 100m, Currency = "TRY", VatRate = 20m, VatInclusion = "EXCLUDED", RoundingMode = "HALF_EVEN", SafetyStock = 0, Status = "ACTIVE", PriceVersion = 1, Version = 1 });
            }
        }
        Sizes(black, "BLZ01", "Siyah");
        Sizes(red, "BLZ01", "Kırmızı");
        Sizes(unrelated, "GML02", "Beyaz");
        var redLink = new MarketplaceProductLink { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, ProductId = red.Id, ExternalId = $"remote-{Guid.NewGuid():N}", SyncStatus = "SYNCED", Version = 1 };

        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        db.Tenants.Add(tenant);
        db.PlatformConnections.Add(connection);
        db.Brands.AddRange(brand, otherBrand);
        db.Categories.AddRange(category, otherCategory);
        db.Products.AddRange(black, red, unrelated);
        db.ProductVariants.AddRange(variants);
        db.ChannelOffers.AddRange(offers);
        db.MarketplaceProductLinks.Add(redLink);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var service = new CatalogService(db, null!, new ConfigurationBuilder().Build(), fixture.TimeProvider, new MemoryCache(new MemoryCacheOptions()));
        var unrelatedBefore = await BulkFieldStateAsync(db, unrelated.Id);

        // Only the black card is selected: the red colour card of the same model takes the value too.
        async Task<BulkProductFieldResult> Set(string field, string value)
        {
            var before = new[] { await BulkFieldStateAsync(db, black.Id), await BulkFieldStateAsync(db, red.Id) };
            var result = await service.BulkSetFieldAsync(tenant.Id, new BulkProductFieldCommand([black.Id], field, value), CancellationToken.None, Guid.CreateVersion7(), "bulk-field-test");
            Assert.True(result.Succeeded, result.Error?.Message);
            db.ChangeTracker.Clear();
            var after = new[] { await BulkFieldStateAsync(db, black.Id), await BulkFieldStateAsync(db, red.Id) };
            for (var index = 0; index < 2; index++)
            {
                // Every field but the chosen one is as it was.
                var expected = field switch
                {
                    BulkProductFields.WarrantyMonths => before[index] with { WarrantyMonths = after[index].WarrantyMonths },
                    BulkProductFields.DeliveryDays => before[index] with { DeliveryDays = after[index].DeliveryDays },
                    BulkProductFields.Description => before[index] with { Description = after[index].Description },
                    BulkProductFields.Category => before[index] with { CategoryId = after[index].CategoryId },
                    BulkProductFields.Brand => before[index] with { BrandId = after[index].BrandId },
                    BulkProductFields.VatRate => before[index] with { VatRate = after[index].VatRate },
                    BulkProductFields.VatInclusion => before[index] with { VatInclusion = after[index].VatInclusion },
                    BulkProductFields.Desi => before[index] with { Desis = after[index].Desis },
                    BulkProductFields.SafetyStock => before[index] with { SafetyStocks = after[index].SafetyStocks },
                    BulkProductFields.Status => before[index] with { Status = after[index].Status },
                    _ => throw new InvalidOperationException(field)
                };
                Assert.Equal(expected, after[index]);
            }
            Assert.Equal(unrelatedBefore, await BulkFieldStateAsync(db, unrelated.Id));
            return result.Value!;
        }

        var warranty = await Set(BulkProductFields.WarrantyMonths, "24");
        Assert.Equal(2, warranty.UpdatedCount);
        Assert.Equal(0, warranty.UnchangedCount);
        Assert.Equal(4, warranty.VariantCount);
        Assert.Empty(warranty.Failures);
        Assert.All(new[] { black.Id, red.Id }, id => Assert.Equal(24, db.Products.AsNoTracking().Single(x => x.Id == id).WarrantyMonths));
        var link = await db.MarketplaceProductLinks.AsNoTracking().SingleAsync(x => x.Id == redLink.Id);
        Assert.Equal("LOCAL_CHANGES_PENDING", link.SyncStatus);
        Assert.Contains("product", link.DirtyFieldsJson);
        Assert.Equal(2, await db.AuditLogs.AsNoTracking().CountAsync(x => x.TenantId == tenant.Id && x.Action == "PRODUCT_BULK_FIELD_SET"));

        var again = await Set(BulkProductFields.WarrantyMonths, "24");
        Assert.Equal(0, again.UpdatedCount);
        Assert.Equal(2, again.UnchangedCount);

        Assert.Equal(2, (await Set(BulkProductFields.DeliveryDays, "7")).UpdatedCount);
        Assert.Equal(7, (await BulkFieldStateAsync(db, red.Id)).DeliveryDays);
        await Set(BulkProductFields.Description, "<p>Yeni açıklama</p>");
        Assert.Equal("<p>Yeni açıklama</p>", (await BulkFieldStateAsync(db, red.Id)).Description);
        await Set(BulkProductFields.Category, otherCategory.Id.ToString());
        Assert.Equal(otherCategory.Id, (await BulkFieldStateAsync(db, red.Id)).CategoryId);
        await Set(BulkProductFields.VatRate, "10");
        Assert.Equal(10m, (await BulkFieldStateAsync(db, red.Id)).VatRate);
        Assert.Equal("EXCLUDED", (await BulkFieldStateAsync(db, red.Id)).VatInclusion);
        await Set(BulkProductFields.VatInclusion, "INCLUDED");
        Assert.Equal("INCLUDED", (await BulkFieldStateAsync(db, red.Id)).VatInclusion);
        await Set(BulkProductFields.Desi, "0,5");
        Assert.Equal("0.5|0.5", (await BulkFieldStateAsync(db, red.Id)).Desis);
        await Set(BulkProductFields.SafetyStock, "3");
        Assert.Equal("3|3", (await BulkFieldStateAsync(db, red.Id)).SafetyStocks);
        // The offers keep their price and VAT; nothing is queued for a marketplace.
        Assert.All(await db.ChannelOffers.AsNoTracking().Where(x => x.TenantId == tenant.Id).ToListAsync(), offer => { Assert.Equal(1, offer.PriceVersion); Assert.Equal(20m, offer.VatRate); });
        Assert.False(await db.IntegrationJobs.AsNoTracking().AnyAsync(x => x.TenantId == tenant.Id));
        await Set(BulkProductFields.Status, "ARCHIVED");
        Assert.Equal(ProductStatus.Archived, (await BulkFieldStateAsync(db, red.Id)).Status);
        Assert.All(await db.ProductVariants.AsNoTracking().Where(x => x.ProductId == red.Id).ToListAsync(), variant => Assert.Equal(ProductStatus.Archived, variant.Status));
        await Set(BulkProductFields.Brand, otherBrand.Id.ToString());
        Assert.Equal(otherBrand.Id, (await BulkFieldStateAsync(db, red.Id)).BrandId);
    }

    // RAB01 on live has no panel prices and no VAT: a bulk desi still goes through. A draft is not put on sale in bulk
    // (Toplu Satışa Aç's rule): it is named by its model code and stays a draft.
    [PostgreSqlFact]
    public async Task BulkField_SetsDesiOnACardWithoutPricesAndNamesADraftItLeaves()
    {
        var tenant = NewTenant("bulk-field-unpriced");
        var card = new Product { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Title = "Rabia elbise", Description = "", Status = ProductStatus.Draft, CreatedAt = fixture.Now, UpdatedAt = fixture.Now, Version = 4 };
        var sku = $"RAB01-{Guid.NewGuid():N}";
        var variant = new ProductVariant { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProductId = card.Id, SortOrder = 0, Sku = sku, SkuNormalized = sku.ToUpperInvariant(), ModelCode = "RAB01", OptionSignature = "Renk=Mavi", Status = ProductStatus.Draft, CreatedAt = fixture.Now, UpdatedAt = fixture.Now, Version = 2 };
        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        db.Tenants.Add(tenant);
        db.Products.Add(card);
        db.ProductVariants.Add(variant);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var service = new CatalogService(db, null!, new ConfigurationBuilder().Build(), fixture.TimeProvider, new MemoryCache(new MemoryCacheOptions()));

        var desi = await service.BulkSetFieldAsync(tenant.Id, new BulkProductFieldCommand([card.Id], "desi", "2.5"), CancellationToken.None);
        Assert.True(desi.Succeeded, desi.Error?.Message);
        Assert.Equal(1, desi.Value!.UpdatedCount);
        Assert.Equal(1, desi.Value.VariantCount);
        db.ChangeTracker.Clear();
        var stored = await db.ProductVariants.AsNoTracking().SingleAsync(x => x.Id == variant.Id);
        Assert.Equal(2.5m, stored.Desi);
        Assert.Equal(3, stored.Version);
        var storedCard = await db.Products.AsNoTracking().SingleAsync(x => x.Id == card.Id);
        Assert.Null(storedCard.VatRate);
        Assert.Null(storedCard.DefaultSalePrice);
        Assert.Equal(ProductStatus.Draft, storedCard.Status);

        var safety = await service.BulkSetFieldAsync(tenant.Id, new BulkProductFieldCommand([card.Id], "safetyStock", "2"), CancellationToken.None);
        Assert.True(safety.Succeeded, safety.Error?.Message);
        Assert.Contains(safety.Value!.Failures, failure => failure.Model == "RAB01");

        var status = await service.BulkSetFieldAsync(tenant.Id, new BulkProductFieldCommand([card.Id], "status", "ACTIVE"), CancellationToken.None);
        Assert.True(status.Succeeded, status.Error?.Message);
        Assert.Equal(0, status.Value!.UpdatedCount);
        Assert.Contains(status.Value.Failures, failure => failure.Model == "RAB01");
        db.ChangeTracker.Clear();
        Assert.Equal(ProductStatus.Draft, (await db.Products.AsNoTracking().SingleAsync(x => x.Id == card.Id)).Status);
    }

    [PostgreSqlFact]
    public async Task BulkField_RefusesAWrongValueAnInactiveReferenceAndAnotherWorkspacesCard()
    {
        var tenant = NewTenant("bulk-field-invalid");
        var otherTenant = NewTenant("bulk-field-invalid-other");
        var inactiveBrand = new Brand { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Name = "Eski", NormalizedName = "ESKI", IsActive = false, CreatedAt = fixture.Now, UpdatedAt = fixture.Now, Version = 1 };
        var branch = new Category { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Name = "Giyim", NormalizedName = "GIYIM", Path = "Giyim", IsLeaf = false, IsActive = true, CreatedAt = fixture.Now, UpdatedAt = fixture.Now, Version = 1 };
        var card = new Product { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Title = "Kart", Description = "", WarrantyMonths = 6, Status = ProductStatus.Active, CreatedAt = fixture.Now, UpdatedAt = fixture.Now, Version = 1 };
        var foreign = new Product { Id = Guid.CreateVersion7(), TenantId = otherTenant.Id, Title = "Başka kart", Description = "", WarrantyMonths = 6, Status = ProductStatus.Active, CreatedAt = fixture.Now, UpdatedAt = fixture.Now, Version = 1 };
        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        db.Tenants.AddRange(tenant, otherTenant);
        db.Brands.Add(inactiveBrand);
        db.Categories.Add(branch);
        db.Products.AddRange(card, foreign);
        await db.SaveChangesAsync();
        var service = new CatalogService(db, null!, new ConfigurationBuilder().Build(), fixture.TimeProvider, new MemoryCache(new MemoryCacheOptions()));
        async Task<ServiceError?> Error(IReadOnlyList<Guid> ids, string? field, string? value) => (await service.BulkSetFieldAsync(tenant.Id, new BulkProductFieldCommand(ids, field, value), CancellationToken.None)).Error;

        foreach (var (field, value) in new[] { ("desi", "0"), ("warrantyMonths", "-1"), ("deliveryDays", "31"), ("vatRate", "8"), ("title", "Yeni"), ("safetyStock", "-2"), ("status", "DRAFT"), ("description", " ") })
        {
            var error = await Error([card.Id], field, value);
            Assert.Equal("VALIDATION_FAILED", error?.Code);
            Assert.Equal(400, error?.Status);
        }
        Assert.Equal("BRAND_ACTIVE_REQUIRED", (await Error([card.Id], "brandId", inactiveBrand.Id.ToString()))?.Code);
        Assert.Equal("CATEGORY_LEAF_REQUIRED", (await Error([card.Id], "categoryId", branch.Id.ToString()))?.Code);
        Assert.Equal("BULK_PRODUCT_NOT_FOUND", (await Error([card.Id, foreign.Id], "warrantyMonths", "12"))?.Code);
        Assert.Equal("BULK_PRODUCT_LIMIT", (await Error([], "warrantyMonths", "12"))?.Code);
        db.ChangeTracker.Clear();
        Assert.Equal(6, (await db.Products.AsNoTracking().SingleAsync(x => x.Id == card.Id)).WarrantyMonths);
        Assert.Equal(6, (await db.Products.AsNoTracking().SingleAsync(x => x.Id == foreign.Id)).WarrantyMonths);
    }
}
