using MarketplaceHub.Application.Tests.Contracts;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// The "Platformlarda Güncelle" summary against PostgreSQL, with the update composers of a seeded three-colour model and a
// fake source of marketplace values: what differs is counted per colour (title, description) and per size (desi), a text
// that differs only by whitespace or markup is the same, a value the marketplace source does not know is unknown, the
// marketplaces without a compared field list theirs as unknown, the cap holds, and nothing is queued or written.
[Collection("postgres-integration")]
public sealed class ProductUpdatePreviewPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    private sealed class FakeKnownValues : IMarketplaceKnownListingValues
    {
        public Dictionary<(Guid Connection, Guid Variant), KnownListingValues> Values { get; } = [];

        public Task<IReadOnlyDictionary<Guid, KnownListingValues>> ReadAsync(Guid tenantId, Guid connectionId, IReadOnlyCollection<Guid> variantIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, KnownListingValues>>(Values.Where(pair => pair.Key.Connection == connectionId && variantIds.Contains(pair.Key.Variant)).ToDictionary(pair => pair.Key.Variant, pair => pair.Value));
    }

    private sealed record Seed(ModelComposerGoldenPostgreSqlTests.Seeded Data, Dictionary<Guid, List<ProductVariant>> Variants, PlatformConnection Trendyol, PlatformConnection Hepsiburada, PlatformConnection N11);

    // The golden model's three colours (two sizes each); saving them links them to one model record (MZ300), so the
    // summary shows them as one model.
    private async Task<Seed> SeedAsync()
    {
        var data = await ModelComposerGoldenPostgreSqlTests.SeedAsync(fixture, "same");
        await using var db = fixture.CreateContext();
        var cardIds = data.Cards.Select(x => x.Id).ToArray();
        Assert.Single((await db.Products.AsNoTracking().Where(x => cardIds.Contains(x.Id)).Select(x => x.FamilyId).ToListAsync()).Distinct().OfType<Guid>());
        var variants = (await db.ProductVariants.AsNoTracking().Where(x => cardIds.Contains(x.ProductId)).OrderBy(x => x.SortOrder).ToListAsync())
            .GroupBy(x => x.ProductId).ToDictionary(group => group.Key, group => group.ToList());
        PlatformConnection Of(string code) => data.Connections.Single(x => x.PlatformCode == code);
        return new(data, variants, Of("TRENDYOL"), Of("HEPSIBURADA"), Of("N11"));
    }

    private static readonly IConfiguration Configuration = new ConfigurationBuilder().Build();

    private static KnownListingValues Known(string? title, string? description, decimal? desi, DateTimeOffset at, string source = MarketplaceKnownListingValueStore.ImportSource) =>
        new(title is null ? null : new(title, source, at), description is null ? null : new(description, source, at), desi is null ? null : new(desi.Value, source, at));

    // Every card on every store, as the window builds it when each card is on each store.
    private static ProductUpdatePreviewRequest Request(IReadOnlyList<Guid> cards, IReadOnlyList<Guid> connections, IReadOnlyList<string>? fields) =>
        new([.. connections.Select(connection => new ProductUpdatePreviewTarget(connection, cards))], fields);

    // The values the update composer sends for each variant on a connection: the summary must compare exactly these.
    private static async Task<Dictionary<Guid, ProductUpdatePreviewService.SentValues>> SentAsync(AppDbContext db, Seed seed, PlatformConnection connection)
    {
        var sent = new Dictionary<Guid, ProductUpdatePreviewService.SentValues>();
        foreach (var card in seed.Data.Cards)
        {
            var draft = await new ProductUpdateComposer(db, Configuration).BuildAsync(seed.Data.Tenant.Id, card.Id, connection.Id, CancellationToken.None);
            Assert.True(draft.Succeeded, draft.Error?.Message);
            foreach (var (variantId, values) in ProductUpdatePreviewService.SentValuesOf(draft.Value!)) sent[variantId] = values;
        }
        return sent;
    }

    [PostgreSqlFact]
    public async Task Trendyol_CountsChangedColoursAndSizes_TreatsReformattedTextAsSame_AndUnknownAsUnknown()
    {
        var seed = await SeedAsync();
        var known = new FakeKnownValues();
        var readAt = seed.Data.Now.AddHours(-3);
        await using var db = fixture.CreateContext();
        var sent = await SentAsync(db, seed, seed.Trendyol);
        var (black, burgundy, grey) = (seed.Data.Cards[0].Id, seed.Data.Cards[1].Id, seed.Data.Cards[2].Id);
        // Siyah: another title on the marketplace and a desi of 3 on its L size; Bordo: the same texts written with other
        // whitespace and markup; Gri: nothing known.
        foreach (var variant in seed.Variants[black])
            known.Values[(seed.Trendyol.Id, variant.Id)] = Known("Kadın Siyah Eski Bluz", sent[variant.Id].Description, variant.SortOrder == 1 ? 3m : 1m, readAt);
        foreach (var variant in seed.Variants[burgundy])
            known.Values[(seed.Trendyol.Id, variant.Id)] = Known($"  {sent[variant.Id].Title!.Replace(" ", "  ", StringComparison.Ordinal)} ", $"\n{sent[variant.Id].Description!.Replace(",", ",\r\n", StringComparison.Ordinal)}  ", 1.00m, readAt.AddHours(1));
        var jobsBefore = await db.IntegrationJobs.CountAsync(x => x.TenantId == seed.Data.Tenant.Id);

        var result = await new ProductUpdatePreviewService(db, Configuration, known).PreviewAsync(seed.Data.Tenant.Id, Request([black, burgundy, grey], [seed.Trendyol.Id], [ProductUpdateFields.Title, ProductUpdateFields.Desi]), CancellationToken.None);

        Assert.True(result.Succeeded, result.Error?.Message);
        var connection = Assert.Single(result.Value!.Connections);
        Assert.Empty(connection.Problems);
        Assert.Equal(readAt, connection.KnownValuesReadAt);
        var model = Assert.Single(connection.Models);
        Assert.Equal("MZ300", model.Label);
        var title = model.Fields.Single(x => x.Field == ProductUpdateFields.Title);
        Assert.Equal((ProductUpdatePreviewStates.Changes, ProductUpdatePreviewStates.ColorUnit, 1, 1, 1), (title.State, title.Unit, title.Changed, title.Same, title.Unknown));
        var change = Assert.Single(title.Changes);
        Assert.Equal(("Siyah", "Kadın Siyah Eski Bluz", sent[seed.Variants[black][0].Id].Title), (change.Label, change.Before, change.After));
        var desi = model.Fields.Single(x => x.Field == ProductUpdateFields.Desi);
        Assert.Equal((ProductUpdatePreviewStates.Changes, ProductUpdatePreviewStates.VariantUnit, 1, 3, 2), (desi.State, desi.Unit, desi.Changed, desi.Same, desi.Unknown));
        Assert.Equal(("MZ300-0L", "3", "1"), (desi.Changes[0].Label, desi.Changes[0].Before, desi.Changes[0].After));
        // Trendyol has not approved the product: its full update carries the description too although it was not
        // picked, so it is compared (and none differs: Gri is unknown, so the field is unknown, not "the same").
        var description = model.Fields.Single(x => x.Field == ProductUpdateFields.Description);
        Assert.Equal((ProductUpdatePreviewStates.Unknown, 0, 2, 1), (description.State, description.Changed, description.Same, description.Unknown));
        Assert.Empty(connection.UnknownFields);

        // Read only: no job and no listing state changed.
        Assert.Equal(jobsBefore, await db.IntegrationJobs.CountAsync(x => x.TenantId == seed.Data.Tenant.Id));
        Assert.All(await db.ChannelListingProfiles.AsNoTracking().Where(x => x.TenantId == seed.Data.Tenant.Id).ToListAsync(), profile => Assert.Equal(1, profile.Version));
    }

    [PostgreSqlFact]
    public async Task Hepsiburada_ComparesItsWholePackage_N11ListsItsFieldsAsUnknown_AndTheDefaultSourceKnowsNothing()
    {
        var seed = await SeedAsync();
        var known = new FakeKnownValues();
        await using var db = fixture.CreateContext();
        var sent = await SentAsync(db, seed, seed.Hepsiburada);
        foreach (var variant in seed.Variants.Values.SelectMany(x => x))
            known.Values[(seed.Hepsiburada.Id, variant.Id)] = Known(
                variant.ProductId == seed.Data.Cards[2].Id ? "Kadın Gri Eski Bluz" : sent[variant.Id].Title,
                sent[variant.Id].Description!.Replace(" ", "  ", StringComparison.Ordinal).Replace(",", ",\n", StringComparison.Ordinal),
                1m, seed.Data.Now);
        var ids = seed.Data.Cards.Select(x => x.Id).ToList();

        var result = await new ProductUpdatePreviewService(db, Configuration, known).PreviewAsync(seed.Data.Tenant.Id, Request(ids, [seed.Hepsiburada.Id, seed.N11.Id], [ProductUpdateFields.Description]), CancellationToken.None);

        Assert.True(result.Succeeded, result.Error?.Message);
        var hepsiburada = result.Value!.Connections.Single(x => x.PlatformCode == "HEPSIBURADA");
        var model = Assert.Single(hepsiburada.Models);
        var description = model.Fields.Single(x => x.Field == ProductUpdateFields.Description);
        Assert.Equal((ProductUpdatePreviewStates.Same, 0, 3, 0), (description.State, description.Changed, description.Same, description.Unknown));
        // Hepsiburada takes its whole product package with the description: the title goes too, and Gri's differs.
        var title = model.Fields.Single(x => x.Field == ProductUpdateFields.Title);
        Assert.Equal((ProductUpdatePreviewStates.Changes, 1), (title.State, title.Changed));
        Assert.Equal(("Gri", "Kadın Gri Eski Bluz"), (title.Changes[0].Label, title.Changes[0].Before));
        Assert.Equal(ProductUpdatePreviewStates.Same, model.Fields.Single(x => x.Field == ProductUpdateFields.Desi).State);

        var n11 = result.Value.Connections.Single(x => x.PlatformCode == "N11");
        Assert.Empty(n11.Models);
        Assert.Equal([ProductUpdateFields.Description], n11.UnknownFields);
        Assert.Null(n11.KnownValuesReadAt);

        // The production source before any read or send: every compared field is unknown, never "the same".
        var unknown = await new ProductUpdatePreviewService(db, Configuration, new MarketplaceKnownListingValuesReader(db)).PreviewAsync(seed.Data.Tenant.Id, Request(ids, [seed.Hepsiburada.Id], [ProductUpdateFields.Description]), CancellationToken.None);
        Assert.All(Assert.Single(Assert.Single(unknown.Value!.Connections).Models).Fields, field => Assert.Equal((ProductUpdatePreviewStates.Unknown, 0, 0), (field.State, field.Changed, field.Same)));
    }

    [PostgreSqlFact]
    public async Task TheCapAndAnUnknownFieldAreRefused_AndAnotherTenantsCardsAreNotRead()
    {
        var seed = await SeedAsync();
        await using var db = fixture.CreateContext();
        var service = new ProductUpdatePreviewService(db, Configuration, new FakeKnownValues());
        var tooMany = Enumerable.Range(0, ProductUpdatePreviewPolicy.MaxProducts + 1).Select(_ => Guid.NewGuid()).ToList();
        var capped = await service.PreviewAsync(seed.Data.Tenant.Id, Request(tooMany, [seed.Trendyol.Id], [ProductUpdateFields.Title]), CancellationToken.None);
        Assert.Equal(("PRODUCT_UPDATE_PREVIEW_TOO_MANY", 422), (capped.Error?.Code, capped.Error?.Status));
        Assert.Equal("PRODUCT_UPDATE_FIELD_UNKNOWN", (await service.PreviewAsync(seed.Data.Tenant.Id, Request([seed.Data.Cards[0].Id], [seed.Trendyol.Id], ["PRICE"]), CancellationToken.None)).Error?.Code);
        Assert.Equal("VALIDATION_FAILED", (await service.PreviewAsync(seed.Data.Tenant.Id, Request([], [seed.Trendyol.Id], null), CancellationToken.None)).Error?.Code);

        var other = await ModelComposerGoldenPostgreSqlTests.SeedAsync(fixture, "same");
        var foreign = await service.PreviewAsync(other.Tenant.Id, Request([seed.Data.Cards[0].Id], [seed.Trendyol.Id], [ProductUpdateFields.Title]), CancellationToken.None);
        Assert.True(foreign.Succeeded);
        Assert.Empty(foreign.Value!.Connections);
    }
}
