using MarketplaceHub.Application.Tests.Contracts;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// KATALOG-14 phase D5a: EffectiveCatalog reads a colour's own name / description (its override) else its model's, and
// the brand, category, VAT, warranty and delivery days from the model, except that while the writers keep the card
// copies a card whose own value differs from its model's is read with its own (review, 17 Nov 2026): phase A changes no
// output. Every path that changes a card's text keeps its override in the same transaction, and the Worker's startup
// pass repairs an override the previous release left stale during a deploy.
[Collection("postgres-integration")]
public sealed class PostgreSqlEffectiveCatalogTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    // The payloads escape non-ASCII letters (ç = ç): compared by the escaped spelling.
    private const string OwnDescription = "Bordo a\\\\u00E7\\\\u0131klamas\\\\u0131";
    private const string StaleOverride = "Bordo eski override";

    [PostgreSqlFact]
    public async Task AColoursOwnText_IsKeptInTheSaveAndRead_TheRestFromTheModel()
    {
        var seeded = await ModelComposerGoldenPostgreSqlTests.SeedAsync(fixture, "same");
        var card = seeded.Cards[1];
        await using (var db = fixture.CreateContext())
        {
            // An import-like save of one colour's description: its override in the same transaction.
            (await db.Products.SingleAsync(x => x.Id == card.Id)).Description = "Bordo açıklaması";
            await db.SaveChangesAsync();
        }

        await using (var db = fixture.CreateContext())
        {
            var stored = await db.Products.AsNoTracking().SingleAsync(x => x.Id == card.Id);
            Assert.Equal("Bordo açıklaması", stored.DescriptionOverride);
            var model = await db.ProductModels.AsNoTracking().SingleAsync(x => x.Id == stored.FamilyId);
            Assert.Equal("Yumuşak triko bluz, uzun kollu.", model.Description);
            var sql = await EffectiveCatalog.Products(db).SingleAsync(x => x.Id == card.Id);
            var memory = EffectiveCatalog.Of(stored, model);
            Assert.Equal(("Bordo açıklaması", card.Title, model.VatRate, model.CategoryId), (sql.Description, sql.Title, sql.VatRate, sql.CategoryId));
            Assert.Equal((sql.Title, sql.Description, sql.BrandId, sql.WarrantyMonths, sql.DeliveryDays), (memory.Title, memory.Description, memory.BrandId, memory.WarrantyMonths, memory.DeliveryDays));
        }

        var payloads = await ModelComposerGoldenPostgreSqlTests.PayloadsAsync(fixture, seeded);
        string Payload(string key) => payloads.Single(x => x.StartsWith(key + ": ", StringComparison.Ordinal));
        Assert.Contains(OwnDescription, Payload("TRENDYOL card1 publication"), StringComparison.Ordinal);
        Assert.Contains(OwnDescription, Payload("HEPSIBURADA card1 update"), StringComparison.Ordinal);
        Assert.DoesNotContain(OwnDescription, Payload("TRENDYOL card0 publication"), StringComparison.Ordinal);
    }

    // A colour whose VAT, category, warranty or delivery days differ from its model's (an Excel row of one card, a bulk
    // edit, a race) is read with its own: the invoice's VAT, the marketplace payload and the list show it.
    [PostgreSqlFact]
    public async Task AMinorityColour_IsReadWithItsOwnValues_InTheInvoiceThePayloadAndTheList()
    {
        var seeded = await ModelComposerGoldenPostgreSqlTests.SeedAsync(fixture, "same");
        var card = seeded.Cards[0];
        await using (var db = fixture.CreateContext())
            await db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE catalog.products SET "VatRate" = 20, "DeliveryDays" = 5, "WarrantyMonths" = 12 WHERE "Id" = {card.Id}""");

        await using (var db = fixture.CreateContext())
        {
            var model = await db.ProductModels.AsNoTracking().SingleAsync(x => x.Id == db.Products.Where(p => p.Id == card.Id).Select(p => p.FamilyId).Single());
            Assert.Equal((10m, (int?)2, (int?)24), (model.VatRate, model.DeliveryDays, model.WarrantyMonths));
            var own = await EffectiveCatalog.Products(db).SingleAsync(x => x.Id == card.Id);
            Assert.Equal((20m, (int?)5, (int?)12), (own.VatRate, own.DeliveryDays, own.WarrantyMonths));
            var other = await EffectiveCatalog.Products(db).SingleAsync(x => x.Id == seeded.Cards[1].Id);
            Assert.Equal((10m, (int?)2, (int?)24), (other.VatRate, other.DeliveryDays, other.WarrantyMonths));

            // The invoice: a Trendyol line without its own VAT takes the product card's.
            var variant = await db.ProductVariants.AsNoTracking().Where(x => x.ProductId == card.Id).OrderBy(x => x.SortOrder).FirstAsync();
            var line = new OrderLine { Id = Guid.CreateVersion7(), TenantId = seeded.Tenant.Id, ExternalLineId = "l-1", Sku = variant.Sku, TitleSnapshot = "Bluz", RawStatus = "Created", VariantId = variant.Id };
            var trendyol = seeded.Connections.Single(x => x.PlatformCode == "TRENDYOL");
            var rates = await InvoiceLineVatRates.ResolveAsync(db, seeded.Tenant.Id, "TRENDYOL", trendyol.Id, [line], CancellationToken.None);
            Assert.Equal(20m, rates[line.Id]);
        }

        var payloads = await ModelComposerGoldenPostgreSqlTests.PayloadsAsync(fixture, seeded);
        var publication = payloads.Single(x => x.StartsWith("TRENDYOL card0 publication: ", StringComparison.Ordinal));
        // The serialized draft escapes the inner payload's quotes. Trendyol's VAT comes from the offer; the delivery time
        // from the card.
        Assert.Contains("deliveryDuration\\u0022:5", publication, StringComparison.Ordinal);

        // The product list: the colour's row (its family's member view) carries its own VAT.
        await using (var db = fixture.CreateContext())
        {
            var service = new CatalogService(db, new CursorCodec(new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider(), TimeProvider.System), new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), fixture.TimeProvider, new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()));
            var page = await service.ListProductsAsync(seeded.Tenant.Id, 50, null, null, null, null, null, null, CancellationToken.None);
            Assert.Contains(page.Items, item => item.Id == card.Id && item.VatRate == 20m);
        }
    }

    [PostgreSqlFact]
    public async Task AStaleOverride_LeftByThePreviousRelease_IsAlignedAtWorkerStartup()
    {
        var seeded = await ModelComposerGoldenPostgreSqlTests.SeedAsync(fixture, "same");
        var card = seeded.Cards[1];
        // The colour had an override; the previous release then wrote the card's text alone (no override, no model).
        await using (var db = fixture.CreateContext())
            await db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE catalog.products SET "Description" = 'Eski sürümün açıklaması', "DescriptionOverride" = {StaleOverride} WHERE "Id" = {card.Id}""");
        await using (var db = fixture.CreateContext())
            Assert.Equal(StaleOverride, (await EffectiveCatalog.Products(db).SingleAsync(x => x.Id == card.Id)).Description);

        await using (var db = fixture.CreateContext())
            Assert.True(await ProductModelValues.AlignStaleAsync(db, DateTimeOffset.UtcNow, CancellationToken.None) >= 1);

        await using (var db = fixture.CreateContext())
        {
            Assert.Equal("Eski sürümün açıklaması", (await EffectiveCatalog.Products(db).SingleAsync(x => x.Id == card.Id)).Description);
            // Every card of the tenant reads its own text again.
            var cards = await db.Products.AsNoTracking().Where(x => x.TenantId == seeded.Tenant.Id).ToDictionaryAsync(x => x.Id, x => (x.Title, x.Description));
            var effective = await EffectiveCatalog.Products(db).Where(x => x.TenantId == seeded.Tenant.Id).ToDictionaryAsync(x => x.Id, x => (x.Title, x.Description));
            Assert.Equal(cards, effective);
        }
        // A second pass writes nothing.
        await using (var db = fixture.CreateContext())
            Assert.Equal(0, await ProductModelValues.AlignStaleAsync(db, DateTimeOffset.UtcNow, CancellationToken.None));
    }

    // A card without a model code holds a model of its own, whose brand follows the card's (review, 17 Nov 2026).
    [PostgreSqlFact]
    public async Task ACodelessCard_HoldsItsOwnModel_WhoseBrandFollowsTheCard()
    {
        var seeded = await ModelComposerGoldenPostgreSqlTests.SeedAsync(fixture, "same");
        var now = DateTimeOffset.UtcNow;
        await using var db = fixture.CreateContext();
        var brands = await db.Brands.AsNoTracking().Where(x => x.TenantId == seeded.Tenant.Id).Select(x => x.Id).ToListAsync();
        var other = new Brand { Id = Guid.CreateVersion7(), TenantId = seeded.Tenant.Id, Name = "Lavian", NormalizedName = "LAVIAN", IsActive = true, CreatedAt = now, UpdatedAt = now, Version = 1 };
        db.Brands.Add(other);
        var card = new Product { Id = Guid.CreateVersion7(), TenantId = seeded.Tenant.Id, Title = "Kodsuz Şal", Description = "Kodsuz", BrandId = brands[0], VatRate = 10m, VatInclusion = "INCLUDED", Status = ProductStatus.Active, CreatedAt = now, UpdatedAt = now };
        db.Products.Add(card);
        db.ProductVariants.Add(new ProductVariant { Id = Guid.CreateVersion7(), TenantId = seeded.Tenant.Id, ProductId = card.Id, Sku = $"SAL-{Guid.NewGuid():N}"[..20], SkuNormalized = "SAL", OptionSignature = "SAL", CreatedAt = now, UpdatedAt = now });
        await db.SaveChangesAsync();
        var model = await db.ProductModels.AsNoTracking().SingleAsync(x => x.Id == db.Products.Where(p => p.Id == card.Id).Select(p => p.FamilyId).Single());
        Assert.Equal(($"#{card.Id:N}", brands[0]), (model.ModelCodeKey, model.BrandId));

        card.BrandId = other.Id;
        await db.SaveChangesAsync();
        Assert.Equal(other.Id, (await db.ProductModels.AsNoTracking().SingleAsync(x => x.Id == model.Id)).BrandId);
        Assert.Equal(other.Id, (await EffectiveCatalog.Products(db).SingleAsync(x => x.Id == card.Id)).BrandId);
    }
}
