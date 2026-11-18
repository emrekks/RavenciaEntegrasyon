using MarketplaceHub.Application.Tests.Contracts;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// catalog.marketplace_known_listing_values against PostgreSQL: an import records what the read carried for the variants
// it links, an accepted send overwrites with what was sent, an older read never puts an older value back, a field sent as
// null becomes unknown, a variant delete takes its row along, and the "Platformlarda Güncelle" summary reads the table
// (source and date per store), counts only the cards each store is sent, and with Desi alone compares the desi alone.
[Collection("postgres-integration")]
public sealed class MarketplaceKnownListingValuesPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    private static readonly IConfiguration Configuration = new ConfigurationBuilder().Build();

    // PostgreSQL keeps microseconds.
    private static DateTimeOffset Pg(DateTimeOffset value) => value.AddTicks(-(value.Ticks % 10));

    private sealed record Seed(ModelComposerGoldenPostgreSqlTests.Seeded Data, List<ProductVariant> Variants, PlatformConnection Trendyol, PlatformConnection Hepsiburada);

    private async Task<Seed> SeedAsync()
    {
        var data = await ModelComposerGoldenPostgreSqlTests.SeedAsync(fixture, "same");
        await using var db = fixture.CreateContext();
        var cardIds = data.Cards.Select(x => x.Id).ToArray();
        var variants = await db.ProductVariants.AsNoTracking().Where(x => cardIds.Contains(x.ProductId)).OrderBy(x => x.ProductId).ThenBy(x => x.SortOrder).ToListAsync();
        return new(data, variants, data.Connections.Single(x => x.PlatformCode == "TRENDYOL"), data.Connections.Single(x => x.PlatformCode == "HEPSIBURADA"));
    }

    private static async Task LinkAsync(AppDbContext db, Seed seed, PlatformConnection connection, IEnumerable<ProductVariant> variants)
    {
        foreach (var variant in variants)
            db.MarketplaceVariantLinks.Add(new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = seed.Data.Tenant.Id, ConnectionId = connection.Id, VariantId = variant.Id, ExternalId = variant.Barcode!, Version = 1 });
        await db.SaveChangesAsync();
    }

    private static RemoteCatalogProduct Content(string title, string? description, IEnumerable<(string Barcode, decimal? Desi)> variants) =>
        new("c1", "MZ300", title, description ?? "", null, null, null, null, [],
            [.. variants.Select(x => new RemoteCatalogVariant(x.Barcode, x.Barcode, x.Barcode, "MZ300", new Dictionary<string, string>(), false, null, null, null, null, "TRY", "{}", Desi: x.Desi))],
            description is null ? $$"""{"contentId":1,"title":"{{title}}"}""" : $$"""{"contentId":1,"title":"{{title}}","description":"{{description}}"}""");

    [PostgreSqlFact]
    public async Task AnImportRecordsWhatItRead_ASendOverwrites_AnOlderReadDoesNot_AndAVariantDeleteTakesItsRow()
    {
        var seed = await SeedAsync();
        var tenant = seed.Data.Tenant.Id;
        await using var db = fixture.CreateContext();
        var linked = seed.Variants.Take(2).ToList();
        await LinkAsync(db, seed, seed.Trendyol, linked);
        var reader = new MarketplaceKnownListingValuesReader(db);
        var readAt = Pg(seed.Data.Now.AddHours(-2));

        // The read of all six barcodes: only the two the connection links are recorded; the 0 desi of the second is not a desi.
        var snapshot = Content("Eski Bluz", "Eski açıklama", seed.Variants.Select((variant, index) => (variant.Barcode!, (decimal?)(index == 1 ? 0m : 2m))));
        await MarketplaceKnownListingValueStore.RecordImportAsync(db, tenant, seed.Trendyol.Id, "TRENDYOL", [snapshot], readAt, CancellationToken.None);
        var known = await reader.ReadAsync(tenant, seed.Trendyol.Id, [.. seed.Variants.Select(x => x.Id)], CancellationToken.None);
        Assert.Equal(linked.Select(x => x.Id).Order(), known.Keys.Order());
        var first = known[linked[0].Id];
        Assert.Equal(("Eski Bluz", "IMPORT", readAt), (first.Title!.Value, first.Title.Source, first.Title.ReadAt));
        Assert.Equal(2m, first.Desi!.Value);
        Assert.Null(known[linked[1].Id].Desi);
        // Another connection knows nothing; another platform's read records nothing.
        Assert.Empty(await reader.ReadAsync(tenant, seed.Hepsiburada.Id, [.. seed.Variants.Select(x => x.Id)], CancellationToken.None));
        await MarketplaceKnownListingValueStore.RecordImportAsync(db, tenant, seed.Trendyol.Id, "N11", [Content("N11", "N11", [(linked[0].Barcode!, 9m)])], readAt.AddHours(1), CancellationToken.None);
        Assert.Equal("Eski Bluz", (await reader.ReadAsync(tenant, seed.Trendyol.Id, [linked[0].Id], CancellationToken.None))[linked[0].Id].Title!.Value);

        // Trendyol accepts a send of the title alone: the title is the sent one, the description and desi stay as read.
        var sentAt = Pg(seed.Data.Now.AddHours(-1));
        await MarketplaceKnownListingValueStore.RecordAcceptedAsync(db, tenant, seed.Trendyol.Id, [new(linked[0].Id, Title: "Yeni Bluz")], sentAt, CancellationToken.None);
        first = (await reader.ReadAsync(tenant, seed.Trendyol.Id, [linked[0].Id], CancellationToken.None))[linked[0].Id];
        Assert.Equal(("Yeni Bluz", "SENT", sentAt), (first.Title!.Value, first.Title.Source, first.Title.ReadAt));
        Assert.Equal(("Eski açıklama", "IMPORT"), (first.Description!.Value, first.Description.Source));

        // An import that read the marketplace before the send does not put the old title back; one without a description
        // leaves the known one.
        await MarketplaceKnownListingValueStore.RecordImportAsync(db, tenant, seed.Trendyol.Id, "TRENDYOL", [Content("Eski Bluz", null, [(linked[0].Barcode!, 2m)])], readAt.AddMinutes(30), CancellationToken.None);
        first = (await reader.ReadAsync(tenant, seed.Trendyol.Id, [linked[0].Id], CancellationToken.None))[linked[0].Id];
        Assert.Equal(("Yeni Bluz", "Eski açıklama"), (first.Title!.Value, first.Description!.Value));
        // A later read is the marketplace's truth (Trendyol may still be reviewing the sent title).
        var laterRead = Pg(seed.Data.Now);
        await MarketplaceKnownListingValueStore.RecordImportAsync(db, tenant, seed.Trendyol.Id, "TRENDYOL", [Content("Eski Bluz", null, [(linked[0].Barcode!, 2m)])], laterRead, CancellationToken.None);
        first = (await reader.ReadAsync(tenant, seed.Trendyol.Id, [linked[0].Id], CancellationToken.None))[linked[0].Id];
        Assert.Equal(("Eski Bluz", "IMPORT", laterRead), (first.Title!.Value, first.Title.Source, first.Title.ReadAt));

        // A field sent empty makes it unknown.
        await MarketplaceKnownListingValueStore.RecordAcceptedAsync(db, tenant, seed.Trendyol.Id, [new(linked[0].Id, ClearDesi: true)], laterRead.AddMinutes(1), CancellationToken.None);
        Assert.Null((await reader.ReadAsync(tenant, seed.Trendyol.Id, [linked[0].Id], CancellationToken.None))[linked[0].Id].Desi);

        // The variant goes: its row goes with it. The only key of the table is to the variant and it cascades, so no
        // variant delete (old code during a deploy included) is held up by it; connection rows go by name in every
        // connection delete and reset (ConnectionDataInventory).
        var keys = await db.Database.SqlQueryRaw<string>("""
            SELECT c.confrelid::regclass::text || ':' || c.confdeltype::text AS "Value" FROM pg_constraint c
            WHERE c.conrelid = 'catalog.marketplace_known_listing_values'::regclass AND c.contype = 'f'
            """).ToListAsync();
        Assert.Equal(["catalog.product_variants:c"], keys);
    }

    [PostgreSqlFact]
    public async Task TheSummaryReadsTheTable_SaysSourceAndDate_AndCountsOnlyTheCardsEachStoreIsSent()
    {
        var seed = await SeedAsync();
        var tenant = seed.Data.Tenant.Id;
        await using var db = fixture.CreateContext();
        await LinkAsync(db, seed, seed.Trendyol, seed.Variants);
        var (black, burgundy) = (seed.Data.Cards[0].Id, seed.Data.Cards[1].Id);
        var readAt = Pg(seed.Data.Now.AddDays(-1));
        var blackVariants = seed.Variants.Where(x => x.ProductId == black).ToList();
        await MarketplaceKnownListingValueStore.RecordImportAsync(db, tenant, seed.Trendyol.Id, "TRENDYOL", [Content("Kadın Siyah Eski Bluz", null, blackVariants.Select(x => (x.Barcode!, (decimal?)1m)))], readAt, CancellationToken.None);
        var sentAt = Pg(seed.Data.Now.AddHours(-1));
        await MarketplaceKnownListingValueStore.RecordAcceptedAsync(db, tenant, seed.Trendyol.Id, [.. seed.Variants.Where(x => x.ProductId == burgundy).Select(x => new MarketplaceKnownListingValueStore.Write(x.Id, Title: "Kadın Bordo Eski Bluz"))], sentAt, CancellationToken.None);

        var service = new ProductUpdatePreviewService(db, Configuration, new MarketplaceKnownListingValuesReader(db));
        // Gönder reaches Siyah and Bordo on Trendyol (Gri is not there): the summary counts those two.
        // On Hepsiburada only Siyah goes (in the same request): its count is one colour, unknown (nothing read there yet).
        var result = await service.PreviewAsync(tenant, new([new(seed.Trendyol.Id, [black, burgundy]), new(seed.Hepsiburada.Id, [black])], [ProductUpdateFields.Title]), CancellationToken.None);
        Assert.True(result.Succeeded, result.Error?.Message);
        var hepsiburada = result.Value!.Connections.Single(x => x.ConnectionId == seed.Hepsiburada.Id);
        var hepsiburadaTitle = Assert.Single(hepsiburada.Models).Fields.Single(x => x.Field == ProductUpdateFields.Title);
        Assert.Equal((ProductUpdatePreviewStates.Unknown, 0, 0, 1), (hepsiburadaTitle.State, hepsiburadaTitle.Changed, hepsiburadaTitle.Same, hepsiburadaTitle.Unknown));
        Assert.Null(hepsiburada.KnownValuesReadAt);
        var connection = result.Value.Connections.Single(x => x.ConnectionId == seed.Trendyol.Id);
        Assert.Equal((readAt, sentAt), (connection.KnownValuesReadAt, connection.KnownValuesSentAt));
        var title = Assert.Single(connection.Models).Fields.Single(x => x.Field == ProductUpdateFields.Title);
        Assert.Equal((ProductUpdatePreviewStates.Changes, 2, 0, 0), (title.State, title.Changed, title.Same, title.Unknown));
        Assert.Equal(["Kadın Siyah Eski Bluz", "Kadın Bordo Eski Bluz"], title.Changes.Select(x => x.Before));

        // Sent to Siyah alone: Bordo's sent title is not counted, nor its date.
        var one = await service.PreviewAsync(tenant, new([new(seed.Trendyol.Id, [black])], [ProductUpdateFields.Title]), CancellationToken.None);
        var oneConnection = Assert.Single(one.Value!.Connections);
        Assert.Equal((readAt, (DateTimeOffset?)null), (oneConnection.KnownValuesReadAt, oneConnection.KnownValuesSentAt));
        Assert.Equal(1, Assert.Single(oneConnection.Models).Fields.Single(x => x.Field == ProductUpdateFields.Title).Changed);
    }

    // Review finding (d) end to end: Trendyol has approved the product (its link names a content), Desi alone is picked,
    // and the marketplace's title differs. Only the desi is compared; the title does not show up.
    [PostgreSqlFact]
    public async Task WithDesiAlonePickedOnAnApprovedTrendyolProduct_OnlyTheDesiIsCompared()
    {
        var seed = await SeedAsync();
        var tenant = seed.Data.Tenant.Id;
        await using var db = fixture.CreateContext();
        await LinkAsync(db, seed, seed.Trendyol, seed.Variants);
        var black = seed.Data.Cards[0].Id;
        db.MarketplaceProductLinks.Add(new MarketplaceProductLink { Id = Guid.CreateVersion7(), TenantId = tenant, ConnectionId = seed.Trendyol.Id, ProductId = black, ExternalId = "123456", Version = 1 });
        await db.SaveChangesAsync();
        var blackVariants = seed.Variants.Where(x => x.ProductId == black).ToList();
        await MarketplaceKnownListingValueStore.RecordImportAsync(db, tenant, seed.Trendyol.Id, "TRENDYOL", [Content("Başka Başlık", "Başka açıklama", blackVariants.Select(x => (x.Barcode!, (decimal?)7m)))], seed.Data.Now, CancellationToken.None);

        var result = await new ProductUpdatePreviewService(db, Configuration, new MarketplaceKnownListingValuesReader(db))
            .PreviewAsync(tenant, new([new(seed.Trendyol.Id, [black])], [ProductUpdateFields.Desi]), CancellationToken.None);

        Assert.True(result.Succeeded, result.Error?.Message);
        var connection = Assert.Single(result.Value!.Connections);
        Assert.Empty(connection.Problems);
        var field = Assert.Single(Assert.Single(connection.Models).Fields);
        Assert.Equal((ProductUpdateFields.Desi, ProductUpdatePreviewStates.Changes, 2), (field.Field, field.State, field.Changed));
        Assert.Empty(connection.UnknownFields);
    }

    private static async Task<int> RowsAsync(AppDbContext db, Guid connectionId) => await db.MarketplaceKnownListingValues.CountAsync(x => x.ConnectionId == connectionId);

    private async Task WriteBothStoresAsync(AppDbContext db, Seed seed)
    {
        foreach (var connection in new[] { seed.Trendyol, seed.Hepsiburada })
        {
            // The seed links its variants on Hepsiburada already (hbSku); the others are linked here.
            var linked = await db.MarketplaceVariantLinks.Where(x => x.ConnectionId == connection.Id).Select(x => x.VariantId).ToListAsync();
            await LinkAsync(db, seed, connection, seed.Variants.Where(x => !linked.Contains(x.Id)));
            await MarketplaceKnownListingValueStore.RecordAcceptedAsync(db, seed.Data.Tenant.Id, connection.Id, [.. seed.Variants.Select(x => new MarketplaceKnownListingValueStore.Write(x.Id, Title: "Bluz"))], seed.Data.Now, CancellationToken.None);
        }
        Assert.Equal((seed.Variants.Count, seed.Variants.Count), (await RowsAsync(db, seed.Trendyol.Id), await RowsAsync(db, seed.Hepsiburada.Id)));
    }

    // Independent review K1: a field sent empty keeps the send's date, so a read older than the send cannot put the old
    // value back; a later read can.
    [PostgreSqlFact]
    public async Task AFieldSentEmptyKeepsItsDate_SoAnOlderReadCannotPutTheOldValueBack()
    {
        var seed = await SeedAsync();
        var tenant = seed.Data.Tenant.Id;
        await using var db = fixture.CreateContext();
        var variant = seed.Variants[0];
        await LinkAsync(db, seed, seed.Trendyol, [variant]);
        var reader = new MarketplaceKnownListingValuesReader(db);
        var sentAt = Pg(seed.Data.Now);
        await MarketplaceKnownListingValueStore.RecordImportAsync(db, tenant, seed.Trendyol.Id, "TRENDYOL", [Content("Eski", null, [(variant.Barcode!, null)])], sentAt.AddHours(-2), CancellationToken.None);
        await MarketplaceKnownListingValueStore.RecordAcceptedAsync(db, tenant, seed.Trendyol.Id, [new(variant.Id, ClearTitle: true)], sentAt, CancellationToken.None);
        Assert.Null((await reader.ReadAsync(tenant, seed.Trendyol.Id, [variant.Id], CancellationToken.None))[variant.Id].Title);
        // A read taken between the two, processed late.
        await MarketplaceKnownListingValueStore.RecordImportAsync(db, tenant, seed.Trendyol.Id, "TRENDYOL", [Content("Eski", null, [(variant.Barcode!, null)])], sentAt.AddHours(-1), CancellationToken.None);
        Assert.Null((await reader.ReadAsync(tenant, seed.Trendyol.Id, [variant.Id], CancellationToken.None))[variant.Id].Title);
        await MarketplaceKnownListingValueStore.RecordImportAsync(db, tenant, seed.Trendyol.Id, "TRENDYOL", [Content("Yeni Okunan", null, [(variant.Barcode!, null)])], sentAt.AddHours(1), CancellationToken.None);
        Assert.Equal("Yeni Okunan", (await reader.ReadAsync(tenant, seed.Trendyol.Id, [variant.Id], CancellationToken.None))[variant.Id].Title!.Value);
    }

    // Migration review / independent review :231: a variant deleted while its values are written is skipped, not an FK
    // error that would fail the import or update step (the write locks the variant FOR KEY SHARE and waits for the delete).
    [PostgreSqlFact]
    public async Task AWriteRacingAVariantDeleteSkipsTheVariantWithoutAnError()
    {
        var seed = await SeedAsync();
        var tenant = seed.Data.Tenant.Id;
        var bare = new ProductVariant { Id = Guid.CreateVersion7(), TenantId = tenant, ProductId = seed.Data.Cards[0].Id, Sku = "MZ300-RACE", SkuNormalized = "MZ300-RACE", OptionSignature = "Beden: XL", SortOrder = 9, CreatedAt = seed.Data.Now, UpdatedAt = seed.Data.Now };
        await using (var add = fixture.CreateContext())
        {
            add.ProductVariants.Add(bare);
            await add.SaveChangesAsync();
        }
        await using var deleter = fixture.CreateContext();
        await using var transaction = await deleter.Database.BeginTransactionAsync();
        Assert.Equal(1, await deleter.ProductVariants.Where(x => x.Id == bare.Id).ExecuteDeleteAsync());
        await using var writer = fixture.CreateContext();
        var write = MarketplaceKnownListingValueStore.RecordAcceptedAsync(writer, tenant, seed.Trendyol.Id, [new(bare.Id, Title: "Bluz")], seed.Data.Now, CancellationToken.None);
        await Task.Delay(500);
        Assert.False(write.IsCompleted);
        await transaction.CommitAsync();
        await write;
        Assert.False(await writer.MarketplaceKnownListingValues.AnyAsync(x => x.VariantId == bare.Id));
    }

    // Independent review: every delete path takes the rows. The connection sweep (ConnectionLeftovers) and Bağlantıyı Sil
    // (its product purge and its connection artifacts) remove the deleted store's rows and leave the other store's.
    [PostgreSqlFact]
    public async Task TheConnectionSweepRemovesADeletedStoresRows_AndKeepsTheOthers()
    {
        var seed = await SeedAsync();
        await using var db = fixture.CreateContext();
        await WriteBothStoresAsync(db, seed);
        await db.PlatformConnections.Where(x => x.Id == seed.Hepsiburada.Id).ExecuteUpdateAsync(x => x.SetProperty(c => c.Status, ConnectionLeftovers.DeletedStatus));
        await ConnectionLeftovers.PurgeAsync(db, seed.Data.Tenant.Id, seed.Hepsiburada.Id, seed.Data.Now, CancellationToken.None);
        Assert.Equal((seed.Variants.Count, 0), (await RowsAsync(db, seed.Trendyol.Id), await RowsAsync(db, seed.Hepsiburada.Id)));
    }

    [PostgreSqlFact]
    public async Task DeletingAConnectionRemovesItsRows_AndItsPurgedProductsRowsEverywhere()
    {
        var seed = await SeedAsync();
        await using var db = fixture.CreateContext();
        await WriteBothStoresAsync(db, seed);
        // Gri is Hepsiburada's alone: Bağlantıyı Sil purges it, its variants and with them its Trendyol rows (cascade).
        var grey = seed.Data.Cards[2].Id;
        db.MarketplaceProductLinks.Add(new MarketplaceProductLink { Id = Guid.CreateVersion7(), TenantId = seed.Data.Tenant.Id, ConnectionId = seed.Hepsiburada.Id, ProductId = grey, ExternalId = "hb-grey", Version = 1 });
        await db.SaveChangesAsync();
        var hepsiburada = await db.PlatformConnections.AsNoTracking().SingleAsync(x => x.Id == seed.Hepsiburada.Id);
        var result = await new OperationalDataMaintenanceService(db, fixture.TimeProvider, new NoDashboard(), Microsoft.Extensions.Logging.Abstractions.NullLogger<OperationalDataMaintenanceService>.Instance)
            .DeleteConnectionAsync(seed.Data.Tenant.Id, Guid.CreateVersion7(), hepsiburada.Id, hepsiburada.Version, new DeleteConnectionCommand(hepsiburada.DisplayName), "corr", CancellationToken.None);
        Assert.True(result.Succeeded, result.Error?.Message);
        await using var check = fixture.CreateContext();
        Assert.Equal(0, await RowsAsync(check, seed.Hepsiburada.Id));
        Assert.False(await check.ProductVariants.AnyAsync(x => x.ProductId == grey));
        Assert.Equal(seed.Variants.Count(x => x.ProductId != grey), await RowsAsync(check, seed.Trendyol.Id));
    }

    // A card no longer on the marketplace (ResetMissingPublication): its links on the store go and its known values too.
    [PostgreSqlFact]
    public async Task UnlinkingAMissingListingTakesItsKnownValues()
    {
        var seed = await SeedAsync();
        await using var db = fixture.CreateContext();
        await WriteBothStoresAsync(db, seed);
        var black = seed.Variants.Where(x => x.ProductId == seed.Data.Cards[0].Id).Select(x => x.Id).ToArray();
        await ProductJobService.UnlinkMissingListingAsync(db, seed.Data.Tenant.Id, seed.Trendyol.Id, seed.Data.Cards[0].Id, black, CancellationToken.None);
        await db.SaveChangesAsync();
        Assert.Equal(seed.Variants.Count - black.Length, await RowsAsync(db, seed.Trendyol.Id));
        Assert.False(await db.MarketplaceKnownListingValues.AnyAsync(x => x.ConnectionId == seed.Trendyol.Id && black.Contains(x.VariantId)));
        Assert.Equal(seed.Variants.Count, await RowsAsync(db, seed.Hepsiburada.Id));
    }

    // Independent review K2: an accepted-values write that meets a lock conflict or a vanished variant is dropped and the
    // update job goes on; inside a transaction the job's own writes stay usable.
    [PostgreSqlFact]
    public async Task ADroppedAcceptedWriteLeavesTheJobsTransactionUsable()
    {
        var seed = await SeedAsync();
        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        // Another transaction holds the variant; the write gives up on the lock (55P03), as it would on a deadlock (40P01).
        await db.Database.ExecuteSqlRawAsync("""SET LOCAL lock_timeout = '50ms'""");
        await using (var holder = fixture.CreateContext())
        {
            await using var hold = await holder.Database.BeginTransactionAsync();
            await holder.Database.ExecuteSqlRawAsync("""SELECT 1 FROM catalog.product_variants WHERE "Id" = {0} FOR UPDATE""", seed.Variants[0].Id);
            var written = await MarketplaceKnownListingValueStore.TryRecordAcceptedAsync(db, seed.Data.Tenant.Id, seed.Trendyol.Id, [new(seed.Variants[0].Id, Title: "Bluz")], seed.Data.Now, CancellationToken.None);
            Assert.False(written);
            await hold.RollbackAsync();
        }
        Assert.Equal(1, await db.Database.SqlQueryRaw<int>("SELECT 1 AS \"Value\"").SingleAsync());
        Assert.True(await MarketplaceKnownListingValueStore.TryRecordAcceptedAsync(db, seed.Data.Tenant.Id, seed.Trendyol.Id, [new(seed.Variants[0].Id, Title: "Bluz")], seed.Data.Now, CancellationToken.None));
        await transaction.CommitAsync();
        Assert.Equal(1, await RowsAsync(db, seed.Trendyol.Id));
    }

    private sealed class NoDashboard : IDashboardReadService
    {
        public Task<DashboardBootstrapView> BootstrapAsync(Guid tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ServiceResult<DashboardPerformanceView>> PerformanceAsync(Guid tenantId, DateOnly? from, DateOnly? to, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ServiceResult<DashboardCancellationsView>> CancellationsAsync(Guid tenantId, DateOnly? day, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RebuildTenantAsync(Guid tenantId, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
