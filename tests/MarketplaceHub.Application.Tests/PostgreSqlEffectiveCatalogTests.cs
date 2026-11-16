using MarketplaceHub.Application.Tests.Contracts;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// KATALOG-14 phases D1-D4: EffectiveCatalog is the one reader of a colour card's shared values, and while the writers
// keep the card copies (dual write) the card's own text is the one source. A colour's override that does not hold the
// card's text (an older release changed the card after the deploy's refresh wrote the override) is read by no reader:
// in SQL, in memory and in every marketplace payload the card's own name and description go (review, 17 Nov 2026).
[Collection("postgres-integration")]
public sealed class PostgreSqlEffectiveCatalogTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    [PostgreSqlFact]
    public async Task AStaleOverride_IsReadByNoReader_TheCardsOwnTextGoes()
    {
        var seeded = await ModelComposerGoldenPostgreSqlTests.SeedAsync(fixture, "same");
        var card = seeded.Cards[1];
        var before = await ModelComposerGoldenPostgreSqlTests.PayloadsAsync(fixture, seeded);
        await using (var db = fixture.CreateContext())
            await db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE catalog.products SET "TitleOverride" = 'Eski Bordo Özel Bluz', "DescriptionOverride" = 'Eski bordo açıklaması' WHERE "Id" = {card.Id}""");

        await using (var db = fixture.CreateContext())
        {
            var sql = await EffectiveCatalog.Products(db).SingleAsync(x => x.TenantId == seeded.Tenant.Id && x.Id == card.Id);
            Assert.Equal((card.Title, card.Description), (sql.Title, sql.Description));
            Assert.False(await EffectiveCatalog.Products(db).AnyAsync(x => x.TenantId == seeded.Tenant.Id && x.Title == "Eski Bordo Özel Bluz"));
            var stored = await db.Products.AsNoTracking().SingleAsync(x => x.Id == card.Id);
            var memory = EffectiveCatalog.Of(stored);
            Assert.Equal((card.Title, card.Description), (memory.Title, memory.Description));
            Assert.NotSame(stored, memory);
        }

        // Every create and update payload of every marketplace is byte for byte what it was without the override.
        var after = await ModelComposerGoldenPostgreSqlTests.PayloadsAsync(fixture, seeded);
        Assert.Equal(before, after);
        Assert.Contains(after, x => x.StartsWith("TRENDYOL card1 publication: ", StringComparison.Ordinal) && x.Contains("\"Succeeded\":true", StringComparison.Ordinal));
        Assert.DoesNotContain(after, x => x.Contains("Eski", StringComparison.Ordinal));
    }
}
