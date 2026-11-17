using MarketplaceHub.Application.Tests.Contracts;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// KATALOG-14 phase D5a: EffectiveCatalog reads the target rule: a colour's own name / description (its override) else
// its model's, and the brand, category, VAT, warranty and delivery days from the model. Every path that changes a card's
// text keeps its override in the same transaction, and the Worker's startup pass repairs a card the previous release
// changed during a deploy (review, 17 Nov 2026): whatever the path, the readers and the marketplaces get the card's text.
[Collection("postgres-integration")]
public sealed class PostgreSqlEffectiveCatalogTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    // The payloads escape non-ASCII letters (ç = ç): compared by the escaped spelling.
    private const string OwnDescription = "Bordo a\\\\u00E7\\\\u0131klamas\\\\u0131";
    private const string StaleDescription = "Eski s\\\\u00FCr\\\\u00FCm\\\\u00FCn a\\\\u00E7\\\\u0131klamas\\\\u0131";

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
            // The model's value reaches the brand, category, VAT, warranty and delivery days of every colour.
            await db.ProductModels.Where(x => x.Id == model.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.DeliveryDays, 5));
            Assert.Equal(5, (await EffectiveCatalog.Products(db).SingleAsync(x => x.Id == seeded.Cards[0].Id)).DeliveryDays);
            await db.ProductModels.Where(x => x.Id == model.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.DeliveryDays, 2));
        }

        var payloads = await ModelComposerGoldenPostgreSqlTests.PayloadsAsync(fixture, seeded);
        string Payload(string key) => payloads.Single(x => x.StartsWith(key + ": ", StringComparison.Ordinal));
        Assert.Contains(OwnDescription, Payload("TRENDYOL card1 publication"), StringComparison.Ordinal);
        Assert.Contains(OwnDescription, Payload("HEPSIBURADA card1 update"), StringComparison.Ordinal);
        Assert.DoesNotContain(OwnDescription, Payload("TRENDYOL card0 publication"), StringComparison.Ordinal);
    }

    [PostgreSqlFact]
    public async Task ACardThePreviousReleaseChangedDuringADeploy_IsAlignedAtWorkerStartup()
    {
        var seeded = await ModelComposerGoldenPostgreSqlTests.SeedAsync(fixture, "same");
        var card = seeded.Cards[1];
        // The previous release writes the card alone (no override, no model): the reader would give the model's text.
        await using (var db = fixture.CreateContext())
            await db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE catalog.products SET "Description" = 'Eski sürümün açıklaması' WHERE "Id" = {card.Id}""");
        await using (var db = fixture.CreateContext())
            Assert.NotEqual("Eski sürümün açıklaması", (await EffectiveCatalog.Products(db).SingleAsync(x => x.Id == card.Id)).Description);

        await using (var db = fixture.CreateContext())
            Assert.True(await ProductModelValues.AlignStaleAsync(db, DateTimeOffset.UtcNow, CancellationToken.None) >= 1);

        await using (var db = fixture.CreateContext())
        {
            Assert.Equal("Eski sürümün açıklaması", (await EffectiveCatalog.Products(db).SingleAsync(x => x.Id == card.Id)).Description);
            // Every card of the tenant reads its own text again.
            var cards = await db.Products.AsNoTracking().Where(x => x.TenantId == seeded.Tenant.Id).ToDictionaryAsync(x => x.Id, x => (x.Title, x.Description));
            var effective = await EffectiveCatalog.Products(db).Where(x => x.TenantId == seeded.Tenant.Id).ToDictionaryAsync(x => x.Id, x => (x.Title, x.Description));
            Assert.Equal(cards, effective);
            // A second pass has nothing to do for this tenant.
            Assert.DoesNotContain(seeded.Tenant.Id, await db.Products.AsNoTracking().Where(x => x.TenantId == seeded.Tenant.Id && x.DescriptionOverride != null && x.DescriptionOverride != x.Description).Select(x => x.TenantId).ToListAsync());
        }
        var payloads = await ModelComposerGoldenPostgreSqlTests.PayloadsAsync(fixture, seeded);
        Assert.Contains(StaleDescription, payloads.Single(x => x.StartsWith("TRENDYOL card1 publication: ", StringComparison.Ordinal)), StringComparison.Ordinal);
    }
}
