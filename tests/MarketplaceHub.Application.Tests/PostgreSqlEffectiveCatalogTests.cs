using MarketplaceHub.Application.Tests.Contracts;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// KATALOG-14 phase D1: EffectiveCatalog is the one reader of a colour card's shared values. A colour's own name or
// description (TitleOverride / DescriptionOverride) wins over the value it takes from its model, alike in SQL and in
// memory, and reaches what the marketplaces receive; without one the card reads as before (the composer goldens).
[Collection("postgres-integration")]
public sealed class PostgreSqlEffectiveCatalogTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    [PostgreSqlFact]
    public async Task AColoursOwnNameAndDescriptionWin_InSqlInMemoryAndInThePayloads()
    {
        var seeded = await ModelComposerGoldenPostgreSqlTests.SeedAsync(fixture, "same");
        var card = seeded.Cards[1];
        await using (var db = fixture.CreateContext())
        {
            // Without an override the effective card is the stored one.
            var plain = await EffectiveCatalog.Products(db).SingleAsync(x => x.TenantId == seeded.Tenant.Id && x.Id == card.Id);
            Assert.Equal(card.Title, plain.Title);
            Assert.Equal(card.Description, plain.Description);
            await db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE catalog.products SET "TitleOverride" = 'Kadın Bordo Özel Bluz', "DescriptionOverride" = 'Bordo açıklaması' WHERE "Id" = {card.Id}""");
        }

        await using (var db = fixture.CreateContext())
        {
            var sql = await EffectiveCatalog.Products(db).Where(x => x.TenantId == seeded.Tenant.Id && x.Title == "Kadın Bordo Özel Bluz").Select(x => new { x.Id, x.Description }).SingleAsync();
            Assert.Equal(card.Id, sql.Id);
            Assert.Equal("Bordo açıklaması", sql.Description);
            var stored = await db.Products.AsNoTracking().SingleAsync(x => x.Id == card.Id);
            var memory = EffectiveCatalog.Of(stored);
            Assert.Equal(("Kadın Bordo Özel Bluz", "Bordo açıklaması"), (memory.Title, memory.Description));
            Assert.Equal(card.Title, stored.Title);
            Assert.NotSame(stored, memory);
        }

        var payloads = await ModelComposerGoldenPostgreSqlTests.PayloadsAsync(fixture, seeded);
        string Payload(string key) => payloads.Single(x => x.StartsWith(key + ": ", StringComparison.Ordinal));
        // The payloads escape non-ASCII letters (Ö = Ö): compare by the escaped spelling.
        const string OwnName = "Bordo \\\\u00D6zel Bluz";
        const string OwnDescription = "Bordo a\\\\u00E7\\\\u0131klamas\\\\u0131";
        foreach (var key in new[] { "TRENDYOL card1 publication", "TRENDYOL card1 update", "HEPSIBURADA card1 publication", "HEPSIBURADA card1 update", "N11 card1 publication", "PAZARAMA card1 publication" })
        {
            Assert.Contains("\"Succeeded\":true", Payload(key), StringComparison.Ordinal);
            Assert.Contains(OwnDescription, Payload(key), StringComparison.Ordinal);
        }
        Assert.Contains(OwnDescription, Payload("N11 card1 update"), StringComparison.Ordinal);
        Assert.Contains(OwnName, Payload("TRENDYOL card1 publication"), StringComparison.Ordinal);
        Assert.Contains(OwnName, Payload("HEPSIBURADA card1 publication"), StringComparison.Ordinal);
        // PTT AVM and Shopify list a model as one product, named from its first colour.
        Assert.DoesNotContain(OwnDescription, Payload("PTTAVM card1 publication"), StringComparison.Ordinal);
        // The other colours keep their own names and descriptions.
        Assert.DoesNotContain(OwnName, Payload("TRENDYOL card0 publication"), StringComparison.Ordinal);
        Assert.DoesNotContain(OwnDescription, Payload("TRENDYOL card0 publication"), StringComparison.Ordinal);
    }
}
