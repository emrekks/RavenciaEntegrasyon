using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Owner rule of 18 Nov 2026: "teklif kaydı yoksa panel fiyatı ve stoğuyla gönder; Özel Fiyat açıksa kendi fiyatıyla".
// A linked variant without an offer gets one at the panel price when the owner switches the writes on (or opens them
// from Entegrasyonlar), counted as sent: nothing goes out then; only a later stock or panel price change does.
[Collection("postgres-integration")]
public sealed class PanelPriceOffersPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    private static readonly Guid Owner = Guid.CreateVersion7();

    [PostgreSqlFact]
    public async Task SwitchingTheWritesOn_OpensOnlyLinkedPricedOffers_AndSendsNothing()
    {
        var seed = await SeedAsync("TRENDYOL");

        await using (var db = fixture.CreateContext())
        {
            var result = await Connections(db).UpdateExternalWriteGroupAsync(seed.Tenant.Id, seed.Connection.Id, Group(true), CancellationToken.None, actorUserId: Owner);
            Assert.True(result.Succeeded, result.Error?.Message);
        }

        await using var read = fixture.CreateContext();
        var offers = await read.ChannelOffers.AsNoTracking().Where(x => x.TenantId == seed.Tenant.Id).ToListAsync();
        // Opened: the priced one only. The size without a panel price, with 0, with a list price below the sale price, without
        // VAT, the unlinked one and the archived one get none; the one with an offer keeps it as it was.
        var opened = Assert.Single(offers, x => x.VariantId == seed.Priced.Id);
        Assert.Equal((true, "ACTIVE", 250m, 200m, 20m, "TRY", 1m), (opened.FollowsPanelPrice, opened.Status, opened.ListPrice, opened.SalePrice, opened.VatRate, opened.Currency, opened.SafetyStock));
        Assert.Equal(PriceInventoryComposer.CurrentPriceHash("TRENDYOL", opened), opened.LastPriceHash);
        Assert.Equal(5, opened.LastStockProjectionVersion);
        Assert.Null(opened.LastSentCode);
        Assert.DoesNotContain(offers, x => x.VariantId == seed.NoPanelPrice.Id || x.VariantId == seed.ZeroPanelPrice.Id || x.VariantId == seed.ListBelowSale.Id
            || x.VariantId == seed.NoVat.Id || x.VariantId == seed.Unlinked.Id || x.VariantId == seed.Archived.Id);
        var existing = Assert.Single(offers, x => x.VariantId == seed.WithOffer.Id);
        Assert.Equal((null, 330m, 300m), (existing.FollowsPanelPrice, existing.ListPrice, existing.SalePrice));
        Assert.Single(await read.ChannelPriceHistory.AsNoTracking().Where(x => x.OfferId == opened.Id).ToListAsync());
        var audit = Assert.Single(await read.AuditLogs.AsNoTracking().Where(x => x.TenantId == seed.Tenant.Id && x.Action == PanelPriceOffers.AuditAction).ToListAsync());
        Assert.Equal(Owner, audit.ActorUserId);
        Assert.Contains("1 teklif açıldı", audit.Reason);
        Assert.Contains("panel fiyatı olmayan 3", audit.Reason);
        Assert.Contains("KDV'si olmayan 1", audit.Reason);
        // Nothing is queued and nothing is due: a build of the connection has no line.
        Assert.Equal(0, await read.IntegrationJobs.CountAsync(x => x.TenantId == seed.Tenant.Id));
        var build = await new PriceInventoryComposer(read).BuildAsync(seed.Tenant.Id, seed.Connection.Id, CancellationToken.None, automatic: true);
        Assert.False(build.Succeeded && build.Value!.Lines.Count > 0);
    }

    [PostgreSqlFact]
    public async Task AfterOpening_AStockChangeSendsThatVariantOnly_PanelStockLessSafetyStock_WithThePriceInForce()
    {
        var seed = await SeedAsync("TRENDYOL");
        await using (var db = fixture.CreateContext())
            Assert.True((await Connections(db).UpdateExternalWriteGroupAsync(seed.Tenant.Id, seed.Connection.Id, Group(true), CancellationToken.None, actorUserId: Owner)).Succeeded);

        await using (var db = fixture.CreateContext())
        {
            var result = await Inventory(db).SetOnHandAsync(seed.Tenant.Id, Owner, seed.Priced.Id, new StockSetCommand(9m, 7m, "Sayım", $"panel-offer-{Guid.NewGuid():N}"), $"panel-offer-{Guid.NewGuid():N}", "panel-offer", CancellationToken.None);
            Assert.True(result.Succeeded, result.Error?.Message);
        }

        await using var read = fixture.CreateContext();
        var job = Assert.Single(await read.IntegrationJobs.AsNoTracking().Where(x => x.TenantId == seed.Tenant.Id).ToListAsync());
        Assert.Equal((MarketplaceJobTypes.StockProjectionDispatch, seed.Connection.Id), (job.JobType, job.ConnectionId));
        Assert.Contains(seed.Priced.Id.ToString(), job.PayloadJson);
        var build = await new PriceInventoryComposer(read).BuildAsync(seed.Tenant.Id, seed.Connection.Id, CancellationToken.None, automatic: true);
        Assert.True(build.Succeeded, build.Error?.Message);
        var line = Assert.Single(build.Value!.Lines);
        Assert.Equal(seed.Priced.Id, line.VariantId);
        // 9 on hand less the connection's safety stock of 1, with the price in force (the saved panel price 250/200):
        // owner rule of 18 Nov 2026, the price goes with every stock send.
        Assert.Equal((8m, false, false, 250m, 200m), (line.Quantity, line.QuantityOmitted, line.PriceOmitted, line.ListPrice, line.SalePrice));
    }

    // A panel price the owner changes (here from the product file) reaches the offers that follow it: Özel Fiyat off, or
    // a choice never stored on a variant whose panel price was saved. Özel Fiyat on keeps its price; a choice never stored
    // on a variant that had no panel price of its own keeps it too (the first panel price reprices nothing at once). Only
    // the connection whose writes are open gets a send.
    [PostgreSqlFact]
    public async Task APanelPriceChange_RepricesTheFollowers_KeepsOzelFiyat_AndSendsOnlyWhereWritesAreOpen()
    {
        var seed = await SeedAsync("TRENDYOL");
        var closed = await AddConnectionAsync(seed.Tenant, "N11", writesOpen: false);
        var custom = await AddConnectionAsync(seed.Tenant, "HEPSIBURADA", writesOpen: false);
        Guid follower, unstored, ozelFiyat, closedFollower, firstPanelOffer;
        await using (var db = fixture.CreateContext())
        {
            follower = AddOffer(db, seed.Tenant, seed.Connection, seed.Priced, 250m, 200m, true);
            closedFollower = AddOffer(db, seed.Tenant, closed, seed.Priced, 260m, 210m, null);
            ozelFiyat = AddOffer(db, seed.Tenant, custom, seed.Priced, 199m, 199m, false);
            unstored = seed.WithOffer.Id;
            firstPanelOffer = AddOffer(db, seed.Tenant, closed, seed.NoPanelPrice, 150m, 140m, null);
            await db.SaveChangesAsync();
            await db.ConnectionSyncPolicies.Where(x => x.ConnectionId == seed.Connection.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Enabled, true));
        }

        await using (var db = fixture.CreateContext())
        {
            var priced = await db.ProductVariants.AsNoTracking().SingleAsync(x => x.Id == seed.Priced.Id);
            var unpriced = await db.ProductVariants.AsNoTracking().SingleAsync(x => x.Id == seed.NoPanelPrice.Id);
            var withOffer = await db.ProductVariants.AsNoTracking().SingleAsync(x => x.Id == seed.WithOffer.Id);
            var result = await new ProductTransferService(db, fixture.TimeProvider).ApplyAsync(seed.Tenant.Id, Owner, new ApplyProductTransferCommand([
                new ProductTransferChange(priced.Id, priced.Version, false, null, false, null, PanelPrice: new ProductTransferPanelPriceChange(220m, 260m)),
                new ProductTransferChange(withOffer.Id, withOffer.Version, false, null, false, null, PanelPrice: new ProductTransferPanelPriceChange(320m, 350m)),
                new ProductTransferChange(unpriced.Id, unpriced.Version, false, null, false, null, PanelPrice: new ProductTransferPanelPriceChange(155m, 165m))]), $"panel-follow-{Guid.NewGuid():N}", CancellationToken.None);
            Assert.True(result.Succeeded, result.Error?.Message);
        }

        await using var read = fixture.CreateContext();
        var offers = await read.ChannelOffers.AsNoTracking().Where(x => x.TenantId == seed.Tenant.Id).ToDictionaryAsync(x => x.Id);
        Assert.Equal((260m, 220m, 2L), (offers[follower].ListPrice, offers[follower].SalePrice, offers[follower].PriceVersion));
        Assert.Equal((260m, 220m), (offers[closedFollower].ListPrice, offers[closedFollower].SalePrice));
        Assert.Equal((199m, 199m, 1L), (offers[ozelFiyat].ListPrice, offers[ozelFiyat].SalePrice, offers[ozelFiyat].PriceVersion));
        var withOfferOffer = offers.Values.Single(x => x.VariantId == unstored);
        Assert.Equal((350m, 320m), (withOfferOffer.ListPrice, withOfferOffer.SalePrice));
        // One rule (owner decision of 18 Nov 2026): the first panel price saved pulls a choice never stored too.
        Assert.Equal((165m, 155m, 2L), (offers[firstPanelOffer].ListPrice, offers[firstPanelOffer].SalePrice, offers[firstPanelOffer].PriceVersion));
        var jobs = await read.IntegrationJobs.AsNoTracking().Where(x => x.TenantId == seed.Tenant.Id).ToListAsync();
        Assert.All(jobs, job => Assert.Equal(seed.Connection.Id, job.ConnectionId));
        Assert.Equal(2, jobs.Count(x => x.JobType == MarketplaceJobTypes.StockProjectionDispatch));
    }

    // A stock change or the switch itself never reprices an offer: an offer whose choice was never stored keeps the price
    // the marketplace gave it even when the panel price differs.
    [PostgreSqlFact]
    public async Task TheSwitchAndAStockChange_NeverRepriceAnExistingOffer()
    {
        var seed = await SeedAsync("TRENDYOL");
        await using (var db = fixture.CreateContext())
            Assert.True((await Connections(db).UpdateExternalWriteGroupAsync(seed.Tenant.Id, seed.Connection.Id, Group(true), CancellationToken.None)).Succeeded);
        await using (var db = fixture.CreateContext())
            Assert.True((await Inventory(db).SetOnHandAsync(seed.Tenant.Id, Owner, seed.WithOffer.Id, new StockSetCommand(3m, 7m, "Sayım", $"no-reprice-{Guid.NewGuid():N}"), $"no-reprice-{Guid.NewGuid():N}", "no-reprice", CancellationToken.None)).Succeeded);

        await using var read = fixture.CreateContext();
        var offer = await read.ChannelOffers.AsNoTracking().SingleAsync(x => x.VariantId == seed.WithOffer.Id);
        Assert.Equal((330m, 300m, 1L, (bool?)null), (offer.ListPrice, offer.SalePrice, offer.PriceVersion, offer.FollowsPanelPrice));
    }

    [PostgreSqlFact]
    public async Task TwoOpeningsAtOnce_OpenEachOfferOnce()
    {
        var seed = await SeedAsync("HEPSIBURADA");

        async Task<ServiceResult<PanelPriceOffersOpened>> OpenAsync()
        {
            await using var db = fixture.CreateContext();
            return await Inventory(db).OpenPanelPriceOffersAsync(seed.Tenant.Id, Owner, seed.Connection.Id, CancellationToken.None);
        }
        var results = await Task.WhenAll(OpenAsync(), OpenAsync(), OpenAsync());

        Assert.All(results, result => Assert.True(result.Succeeded, result.Error?.Message));
        await using var read = fixture.CreateContext();
        var offer = Assert.Single(await read.ChannelOffers.AsNoTracking().Where(x => x.TenantId == seed.Tenant.Id && x.VariantId == seed.Priced.Id).ToListAsync());
        // Hepsiburada takes the sale price only: list = sale.
        Assert.Equal((200m, 200m, true), (offer.ListPrice, offer.SalePrice, offer.FollowsPanelPrice));
        Assert.Equal(1, results.Sum(result => result.Value!.Opened));
        // A closed connection: opened, nothing queued.
        Assert.Equal(0, await read.IntegrationJobs.CountAsync(x => x.TenantId == seed.Tenant.Id));
        // Opening again opens nothing more.
        await using var again = fixture.CreateContext();
        Assert.Equal(0, (await Inventory(again).OpenPanelPriceOffersAsync(seed.Tenant.Id, Owner, seed.Connection.Id, CancellationToken.None)).Value!.Opened);
    }

    // Deterministic race: another request opens the same offer between this one's read and its save. The unique index
    // refuses the second insert; the opening reads the offer as there and saves the rest instead of failing.
    [PostgreSqlFact]
    public async Task AnOfferOpenedMeanwhile_IsReadAsThere_NotDoubled()
    {
        var seed = await SeedAsync("HEPSIBURADA");
        var interceptor = new ConcurrentOfferOpen(seed.Tenant.Id, seed.Connection.Id, seed.Priced.Id);
        var connectionString = fixture.CreateContext().Database.GetConnectionString();
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString).AddInterceptors(interceptor).Options);

        var result = await Inventory(db).OpenPanelPriceOffersAsync(seed.Tenant.Id, Owner, seed.Connection.Id, CancellationToken.None);

        Assert.True(interceptor.Fired);
        Assert.True(result.Succeeded, result.Error?.Message);
        Assert.Equal(0, result.Value!.Opened);
        await using var read = fixture.CreateContext();
        Assert.Single(await read.ChannelOffers.AsNoTracking().Where(x => x.TenantId == seed.Tenant.Id && x.VariantId == seed.Priced.Id).ToListAsync());
    }

    private sealed class ConcurrentOfferOpen(Guid tenantId, Guid connectionId, Guid variantId) : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
    {
        public bool Fired { get; private set; }

        public override async ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> SavingChangesAsync(Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData eventData, Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!Fired && eventData.Context is AppDbContext db && db.ChangeTracker.Entries<ChannelOffer>().Any(x => x.State == EntityState.Added))
            {
                Fired = true;
                await using var other = new PostgreSqlTenantIsolationFixture().CreateContext();
                other.ChannelOffers.Add(new ChannelOffer { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = connectionId, VariantId = variantId, ListPrice = 200m, SalePrice = 200m, Currency = "TRY", VatRate = 20m, VatInclusion = "INCLUDED", RoundingMode = "HALF_EVEN", Status = "ACTIVE", FollowsPanelPrice = true, PriceVersion = 1, Version = 1 });
                await other.SaveChangesAsync(cancellationToken);
            }
            return result;
        }
    }

    [PostgreSqlFact]
    public async Task PttAvm_OpensOffersToo_SinceThePriceGoesWithEveryStockSend()
    {
        var seed = await SeedAsync("PTTAVM");
        await using (var db = fixture.CreateContext())
            Assert.True((await Connections(db).UpdateExternalWriteGroupAsync(seed.Tenant.Id, seed.Connection.Id, Group(true), CancellationToken.None)).Succeeded);
        await using var read = fixture.CreateContext();
        var opened = Assert.Single(await read.ChannelOffers.AsNoTracking().Where(x => x.TenantId == seed.Tenant.Id && x.VariantId == seed.Priced.Id).ToListAsync());
        // PTT AVM takes the sale price only: list = sale.
        Assert.Equal((200m, 200m, true), (opened.ListPrice, opened.SalePrice, opened.FollowsPanelPrice));
        Assert.Equal(0, await read.IntegrationJobs.CountAsync(x => x.TenantId == seed.Tenant.Id));
        var preview = (await Inventory(read).PanelPriceOfferPreviewAsync(seed.Tenant.Id, seed.Connection.Id, CancellationToken.None)).Value!;
        Assert.Equal((true, 0, 4), (preview.OpensOffers, preview.Openable, preview.LinkedWithoutOffer));
    }

    [PostgreSqlFact]
    public async Task ThePreviewCountsPerConnection_AndStaysInItsTenant()
    {
        var seed = await SeedAsync("SHOPIFY");
        var other = await SeedAsync("SHOPIFY");

        await using var db = fixture.CreateContext();
        var preview = (await Inventory(db).PanelPriceOfferPreviewAsync(seed.Tenant.Id, seed.Connection.Id, CancellationToken.None)).Value!;
        Assert.Equal((true, 5, 1, 3, 1), (preview.OpensOffers, preview.LinkedWithoutOffer, preview.Openable, preview.MissingPanelPrice, preview.MissingVat));
        // The offer with no stored choice on a variant with a saved panel price (300 vs panel 310 / list 340): it follows
        // the panel and holds another price now.
        Assert.Equal((1, 1, 0), (preview.Following, preview.FollowingAtOtherPrice, preview.CustomPrice));
        Assert.Equal(300m, Assert.Single(preview.Differences).SalePrice);
        // Its price in force (310) differs from the stored one: it goes with the first stock send after the writes open.
        Assert.Equal(1, preview.PriceChangesOnFirstStockSend);
        // Another tenant's connection is not found, and opening from this tenant leaves the other untouched.
        Assert.Equal(404, (await Inventory(db).PanelPriceOfferPreviewAsync(seed.Tenant.Id, other.Connection.Id, CancellationToken.None)).Error?.Status);
        Assert.Equal(404, (await Inventory(db).OpenPanelPriceOffersAsync(seed.Tenant.Id, Owner, other.Connection.Id, CancellationToken.None)).Error?.Status);
        Assert.Equal(1, (await Inventory(db).OpenPanelPriceOffersAsync(seed.Tenant.Id, Owner, seed.Connection.Id, CancellationToken.None)).Value!.Opened);
        await using var read = fixture.CreateContext();
        Assert.Equal(1, await read.ChannelOffers.CountAsync(x => x.TenantId == other.Tenant.Id));
    }

    // Owner rule of 18 Nov 2026: the one-off fill of panel prices from the marketplace each variant was first read from is
    // previewed read only: variants without a saved panel price, the oldest offer's price, 0 left empty, nothing written.
    [PostgreSqlFact]
    public async Task ThePanelPriceFillPreview_ReadsTheOldestOffer_AndWritesNothing()
    {
        var seed = await SeedAsync("TRENDYOL");
        var later = await AddConnectionAsync(seed.Tenant, "N11", writesOpen: false);
        await using (var db = fixture.CreateContext())
        {
            // NOPANEL: first read from Trendyol (its oldest offer, 120), N11 later (999). ZERO: a 0 TL first offer.
            AddOffer(db, seed.Tenant, seed.Connection, seed.NoPanelPrice, 130m, 120m, null);
            // Offer ids are time ordered to the millisecond; the later offer is created a few milliseconds after.
            await Task.Delay(5);
            AddOffer(db, seed.Tenant, seed.Connection, seed.ZeroPanelPrice, 0m, 0m, null);
            await db.SaveChangesAsync();
            AddOffer(db, seed.Tenant, later, seed.NoPanelPrice, 999m, 999m, null);
            await db.SaveChangesAsync();
        }
        await using var read = fixture.CreateContext();
        var preview = await Inventory(read).PanelPriceFillPreviewAsync(seed.Tenant.Id, CancellationToken.None);
        // Without a saved panel price: NOPANEL, ZERO (0 is no price). Every other seeded variant has one.
        Assert.Equal((2, 1, 1, 0), (preview.WithoutPanelPrice, preview.Fillable, preview.ZeroPrice, preview.WithoutOffer));
        var row = Assert.Single(preview.Sample);
        Assert.Equal((seed.NoPanelPrice.Id, "TRENDYOL", 130m, 120m), (row.VariantId, row.PlatformCode, row.ListPrice, row.SalePrice));
        Assert.Equal(1, preview.FillableBySource["TRENDYOL"]);
        // The N11 offer (999) the fill would pull to the new panel price (130/120); the Trendyol one already holds it.
        var change = Assert.Single(preview.OfferChanges);
        Assert.Equal(("N11", 1, 879m, 879m, 879m), (change.PlatformCode, change.Offers, change.TotalDifference, change.MedianDifference, change.LargestDifference));
        Assert.Null((await read.ProductVariants.AsNoTracking().SingleAsync(x => x.Id == seed.NoPanelPrice.Id)).DefaultSalePrice);
    }

    // Owner decision of 18 Nov 2026: the fill writes only the empty panel prices, pulls the offers with Özel Fiyat off to
    // them (price history, send marks moved), sends nothing and queues nothing; the counted preview must still hold; a
    // second run fills nothing; another tenant is untouched. The price reaches the marketplace with the next stock send.
    [PostgreSqlFact]
    public async Task ThePanelPriceFill_WritesOnlyEmptyPanelPrices_PullsTheFollowers_AndSendsNothing()
    {
        var seed = await SeedAsync("TRENDYOL");
        var other = await SeedAsync("TRENDYOL");
        var n11 = await AddConnectionAsync(seed.Tenant, "N11", writesOpen: true);
        Guid trendyolOffer, n11Offer, customOffer;
        await using (var db = fixture.CreateContext())
        {
            trendyolOffer = AddOffer(db, seed.Tenant, seed.Connection, seed.NoPanelPrice, 130m, 120m, null);
            // Offer ids are time ordered to the millisecond; the later offer is created a few milliseconds after.
            await Task.Delay(5);
            await db.SaveChangesAsync();
            n11Offer = AddOffer(db, seed.Tenant, n11, seed.NoPanelPrice, 999m, 999m, null);
            db.MarketplaceVariantLinks.Add(new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = seed.Tenant.Id, ConnectionId = n11.Id, VariantId = seed.NoPanelPrice.Id, ExternalId = $"N11-{Guid.NewGuid():N}", Version = 1 });
            customOffer = AddOffer(db, seed.Tenant, n11, seed.ZeroPanelPrice, 500m, 500m, false);
            AddOffer(db, other.Tenant, other.Connection, other.NoPanelPrice, 130m, 120m, null);
            await db.SaveChangesAsync();
            // Both connections' writes are open and every offer counts as sent at its values (decision B).
            await db.ConnectionSyncPolicies.Where(x => x.ConnectionId == seed.Connection.Id).ExecuteUpdateAsync(set => set.SetProperty(x => x.Enabled, true));
            foreach (var offer in await db.ChannelOffers.Where(x => x.TenantId == seed.Tenant.Id).ToListAsync())
            {
                offer.LastPriceHash = PriceInventoryComposer.CurrentPriceHash(offer.ConnectionId == n11.Id ? "N11" : "TRENDYOL", offer);
                offer.LastStockProjectionVersion = 5;
            }
            await db.SaveChangesAsync();
        }

        await using (var db = fixture.CreateContext())
        {
            var stale = await Inventory(db).ApplyPanelPriceFillAsync(seed.Tenant.Id, Owner, new PanelPriceFillCommand(7), "fill-stale", CancellationToken.None);
            Assert.Equal(("PANEL_PRICE_FILL_CHANGED", 409), (stale.Error?.Code, stale.Error?.Status));
        }
        await using (var db = fixture.CreateContext())
            Assert.Null((await db.ProductVariants.AsNoTracking().SingleAsync(x => x.Id == seed.NoPanelPrice.Id)).DefaultSalePrice);

        int expected;
        await using (var db = fixture.CreateContext())
            expected = (await Inventory(db).PanelPriceFillPreviewAsync(seed.Tenant.Id, CancellationToken.None)).Fillable;
        // NOPANEL from Trendyol (its oldest offer, 120) and ZERO from its only offer, the N11 one with Özel Fiyat (500).
        Assert.Equal(2, expected);
        await using (var db = fixture.CreateContext())
        {
            var applied = await Inventory(db).ApplyPanelPriceFillAsync(seed.Tenant.Id, Owner, new PanelPriceFillCommand(expected), "fill", CancellationToken.None);
            Assert.True(applied.Succeeded, applied.Error?.Message);
            Assert.Equal((2, 1), (applied.Value!.Filled, applied.Value.OffersRepriced));
        }

        await using (var read = fixture.CreateContext())
        {
            var variant = await read.ProductVariants.AsNoTracking().SingleAsync(x => x.Id == seed.NoPanelPrice.Id);
            Assert.Equal((130m, 120m), (variant.DefaultListPrice, variant.DefaultSalePrice));
            // A variant with a panel price already is untouched.
            Assert.Equal(200m, (await read.ProductVariants.AsNoTracking().SingleAsync(x => x.Id == seed.Priced.Id)).DefaultSalePrice);
            var offers = await read.ChannelOffers.AsNoTracking().Where(x => x.TenantId == seed.Tenant.Id).ToDictionaryAsync(x => x.Id);
            Assert.Equal((120m, 1L), (offers[trendyolOffer].SalePrice, offers[trendyolOffer].PriceVersion));
            Assert.Equal((130m, 120m, 2L), (offers[n11Offer].ListPrice, offers[n11Offer].SalePrice, offers[n11Offer].PriceVersion));
            Assert.Equal(PriceInventoryComposer.CurrentPriceHash("N11", offers[n11Offer]), offers[n11Offer].LastPriceHash);
            Assert.Equal((500m, 1L), (offers[customOffer].SalePrice, offers[customOffer].PriceVersion));
            Assert.Single(await read.ChannelPriceHistory.AsNoTracking().Where(x => x.OfferId == n11Offer).ToListAsync());
            Assert.Equal(0, await read.IntegrationJobs.CountAsync(x => x.TenantId == seed.Tenant.Id));
            foreach (var connectionId in new[] { seed.Connection.Id, n11.Id })
            {
                var build = await new PriceInventoryComposer(read).BuildAsync(seed.Tenant.Id, connectionId, CancellationToken.None, automatic: true);
                Assert.False(build.Succeeded && build.Value!.Lines.Count > 0);
            }
            Assert.Single(await read.AuditLogs.AsNoTracking().Where(x => x.TenantId == seed.Tenant.Id && x.Action == "PANEL_PRICES_FILLED_FROM_FIRST_MARKETPLACE" && x.ActorUserId == Owner).ToListAsync());
            Assert.Null((await read.ProductVariants.AsNoTracking().SingleAsync(x => x.Id == other.NoPanelPrice.Id)).DefaultSalePrice);
        }
        await using (var db = fixture.CreateContext())
        {
            var again = await Inventory(db).ApplyPanelPriceFillAsync(seed.Tenant.Id, Owner, new PanelPriceFillCommand(0), "fill-again", CancellationToken.None);
            Assert.Equal(0, again.Value!.Filled);
        }
        // The next stock send of the variant carries the new panel price (n11 sends stock and price together).
        await using (var db = fixture.CreateContext())
            await db.InventoryItems.Where(x => x.VariantId == seed.NoPanelPrice.Id).ExecuteUpdateAsync(set => set.SetProperty(x => x.ProjectionVersion, 6));
        await using (var read = fixture.CreateContext())
        {
            var build = await new PriceInventoryComposer(read).BuildAsync(seed.Tenant.Id, n11.Id, CancellationToken.None, automatic: true);
            var line = Assert.Single(build.Value!.Lines);
            Assert.Equal((false, 120m, 130m), (line.PriceOmitted, line.SalePrice, line.ListPrice));
        }
    }

    // Second review of 18 Nov 2026: Trendyol 120, Pazarama 999, both marked as sent. The fill pulls Pazarama to 120 and
    // keeps 999 as the price Pazarama is known to hold, so the next stock send carries 120 (Pazarama refuses only an equal
    // price), once: after it is confirmed, an unchanged price stays out. The send log shows 999 as the previous price.
    [PostgreSqlFact]
    public async Task AfterTheFill_PazaramasNextStockSendCarriesThePanelPriceOnce()
    {
        var seed = await SeedAsync("TRENDYOL");
        var pazarama = await AddConnectionAsync(seed.Tenant, "PAZARAMA", writesOpen: false);
        Guid pazaramaOffer;
        await using (var db = fixture.CreateContext())
        {
            AddOffer(db, seed.Tenant, seed.Connection, seed.NoPanelPrice, 120m, 120m, null);
            await Task.Delay(5);
            pazaramaOffer = AddOffer(db, seed.Tenant, pazarama, seed.NoPanelPrice, 999m, 999m, null);
            db.MarketplaceVariantLinks.Add(new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = seed.Tenant.Id, ConnectionId = pazarama.Id, VariantId = seed.NoPanelPrice.Id, ExternalId = "8690000000999", Version = 1 });
            await db.SaveChangesAsync();
            foreach (var offer in await db.ChannelOffers.Where(x => x.TenantId == seed.Tenant.Id).ToListAsync())
            {
                offer.LastPriceHash = PriceInventoryComposer.CurrentPriceHash(offer.ConnectionId == pazarama.Id ? "PAZARAMA" : "TRENDYOL", offer);
                offer.LastStockProjectionVersion = 5;
            }
            await db.SaveChangesAsync();
        }
        await using (var db = fixture.CreateContext())
        {
            var preview = await Inventory(db).PanelPriceFillPreviewAsync(seed.Tenant.Id, CancellationToken.None);
            Assert.True((await Inventory(db).ApplyPanelPriceFillAsync(seed.Tenant.Id, Owner, new PanelPriceFillCommand(preview.Fillable), "fill", CancellationToken.None)).Succeeded);
        }
        // The stock changed while the writes were off; the owner opens them after the fill: the baseline (which marks the
        // offer anew) keeps the known price. Then the stock changes again.
        await using (var db = fixture.CreateContext())
            await db.InventoryItems.Where(x => x.VariantId == seed.NoPanelPrice.Id).ExecuteUpdateAsync(set => set.SetProperty(x => x.ProjectionVersion, 6));
        await using (var db = fixture.CreateContext())
            Assert.True((await Connections(db).UpdateExternalWriteGroupAsync(seed.Tenant.Id, pazarama.Id, Group(true), CancellationToken.None)).Succeeded);
        await using (var db = fixture.CreateContext())
            await db.InventoryItems.Where(x => x.VariantId == seed.NoPanelPrice.Id).ExecuteUpdateAsync(set => set.SetProperty(x => x.ProjectionVersion, 7));

        PriceInventoryPushLine line;
        await using (var db = fixture.CreateContext())
        {
            var offer = await db.ChannelOffers.AsNoTracking().SingleAsync(x => x.Id == pazaramaOffer);
            Assert.Equal((120m, 999m), (offer.SalePrice, offer.LastSentSalePrice));
            var build = await new PriceInventoryComposer(db).BuildAsync(seed.Tenant.Id, pazarama.Id, CancellationToken.None, automatic: true);
            line = Assert.Single(build.Value!.Lines);
            Assert.Equal((false, 120m), (line.PriceOmitted, line.SalePrice));
            Assert.Contains("\"salePrice\":120", build.Value.PayloadJson);
            // The send log's previous price is the one Pazarama held.
            var jobId = Guid.CreateVersion7();
            var job = new IntegrationJob { Id = jobId, TenantId = seed.Tenant.Id, ConnectionId = pazarama.Id, JobType = MarketplaceJobTypes.PriceInventorySync, PayloadJson = "{}", PayloadVersion = 1, PayloadHash = "h", JobDedupKey = $"log:{jobId:N}", EffectIdempotencyKey = $"log:{jobId:N}", Status = JobStatus.Leased, AvailableAt = DateTimeOffset.UtcNow, CorrelationId = "log", CreatedAt = DateTimeOffset.UtcNow, Version = 1 };
            var payload = new PriceInventoryJobPayload(jobId, pazarama.Id, "SUBMIT", "h", build.Value.PayloadJson, build.Value.Lines, null, null);
            await PriceStockSendLogRecorder.AddAsync(db, seed.Tenant.Id, pazarama.Id, "PAZARAMA", job, payload, "batch", DateTimeOffset.UtcNow, PriceStockSendResults.Submitted, null, null, CancellationToken.None);
            var row = Assert.Single(db.PriceStockSendLogs.Local);
            Assert.Equal((999m, 120m), (row.PreviousSalePrice, row.NewSalePrice));
        }
        // Pazarama confirms the line; the next stock change sends no price again.
        await using (var db = fixture.CreateContext())
        {
            var offer = await db.ChannelOffers.SingleAsync(x => x.Id == pazaramaOffer);
            offer.LastPriceHash = line.PriceHash;
            offer.LastStockProjectionVersion = line.ProjectionVersion;
            PriceInventorySendPolicy.RecordSent(offer, line, DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
            await db.InventoryItems.Where(x => x.VariantId == seed.NoPanelPrice.Id).ExecuteUpdateAsync(set => set.SetProperty(x => x.ProjectionVersion, 8));
        }
        await using (var db = fixture.CreateContext())
        {
            var build = await new PriceInventoryComposer(db).BuildAsync(seed.Tenant.Id, pazarama.Id, CancellationToken.None, automatic: true);
            Assert.True(Assert.Single(build.Value!.Lines).PriceOmitted);
        }
    }

    // The fill's second line (review A6): a panel price saved between the plan and the write is never overwritten.
    [PostgreSqlFact]
    public async Task ThePanelPriceFill_SkipsAPanelPriceSavedAfterItsPlan()
    {
        var seed = await SeedAsync("TRENDYOL");
        await using (var db = fixture.CreateContext())
        {
            AddOffer(db, seed.Tenant, seed.Connection, seed.NoPanelPrice, 130m, 120m, null);
            await db.SaveChangesAsync();
        }
        var interceptor = new PanelPriceSavedMeanwhile(seed.NoPanelPrice.Id);
        var connectionString = fixture.CreateContext().Database.GetConnectionString();
        await using (var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString).AddInterceptors(interceptor).Options))
        {
            var result = await Inventory(db).ApplyPanelPriceFillAsync(seed.Tenant.Id, Owner, new PanelPriceFillCommand(1), "fill-race", CancellationToken.None);
            Assert.True(interceptor.Fired);
            Assert.Equal(0, result.Value!.Filled);
        }
        await using var read = fixture.CreateContext();
        Assert.Equal(777m, (await read.ProductVariants.AsNoTracking().SingleAsync(x => x.Id == seed.NoPanelPrice.Id)).DefaultSalePrice);
    }

    // A worker saving an offer meanwhile breaks the serializable fill (40001): the fill runs again, checks the previewed
    // count again and writes once.
    [PostgreSqlFact]
    public async Task ThePanelPriceFill_RunsAgainAfterASerializationFailure()
    {
        var seed = await SeedAsync("TRENDYOL");
        await using (var db = fixture.CreateContext())
        {
            AddOffer(db, seed.Tenant, seed.Connection, seed.NoPanelPrice, 130m, 120m, null);
            await db.SaveChangesAsync();
        }
        var interceptor = new SerializationFailureOnce();
        var connectionString = fixture.CreateContext().Database.GetConnectionString();
        await using (var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString).AddInterceptors(interceptor).Options))
        {
            var result = await Inventory(db).ApplyPanelPriceFillAsync(seed.Tenant.Id, Owner, new PanelPriceFillCommand(1), "fill-retry", CancellationToken.None);
            Assert.True(result.Succeeded, result.Error?.Message);
            Assert.Equal(1, result.Value!.Filled);
        }
        // The first attempt failed at its first save; the second saved twice (the panel prices, then the offers pulled).
        Assert.Equal(3, interceptor.Saves);
        await using var read = fixture.CreateContext();
        Assert.Equal(120m, (await read.ProductVariants.AsNoTracking().SingleAsync(x => x.Id == seed.NoPanelPrice.Id)).DefaultSalePrice);
        Assert.Single(await read.AuditLogs.AsNoTracking().Where(x => x.TenantId == seed.Tenant.Id && x.Action == "PANEL_PRICES_FILLED_FROM_FIRST_MARKETPLACE").ToListAsync());
    }

    private sealed class SerializationFailureOnce : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
    {
        public int Saves { get; private set; }

        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> SavingChangesAsync(Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData eventData, Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Saves++;
            if (Saves == 1) throw new Npgsql.PostgresException("could not serialize access", "ERROR", "ERROR", Npgsql.PostgresErrorCodes.SerializationFailure);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class PanelPriceSavedMeanwhile(Guid variantId) : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        public bool Fired { get; private set; }

        public override async ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(System.Data.Common.DbCommand command, Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData, Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (!Fired && command.CommandText.Contains(InventoryService.PanelPriceFillLoadTag, StringComparison.Ordinal))
            {
                Fired = true;
                await using var update = command.Connection!.CreateCommand();
                update.Transaction = command.Transaction;
                update.CommandText = $"UPDATE catalog.product_variants SET \"DefaultSalePrice\" = 777, \"DefaultListPrice\" = 777 WHERE \"Id\" = '{variantId}'";
                await update.ExecuteNonQueryAsync(cancellationToken);
            }
            return result;
        }
    }

    private sealed record Seed(Tenant Tenant, PlatformConnection Connection, ProductVariant Priced, ProductVariant NoPanelPrice, ProductVariant ZeroPanelPrice, ProductVariant ListBelowSale, ProductVariant NoVat, ProductVariant Unlinked, ProductVariant Archived, ProductVariant WithOffer);

    private async Task<Seed> SeedAsync(string platformCode)
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"panel-offer-{Guid.NewGuid():N}", DisplayName = "panel offer", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        await using var db = fixture.CreateContext();
        db.Tenants.Add(tenant);
        var connection = Connection(db, tenant, platformCode, writesOpen: false);
        db.ConnectionInventoryPolicies.Add(new ConnectionInventoryPolicy { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, AuthorityMode = "PANEL", ReservationMode = "NONE", ReserveOnStatuses = "", ReleaseOnStatuses = "", DefaultSafetyStock = 1m, Version = 1 });
        ProductVariant Variant(string code, decimal? sale, decimal? list, decimal? vat = 20m, bool link = true, ProductStatus status = ProductStatus.Active)
        {
            var product = new Product { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Title = $"Ürün {code}", Description = "Panel fiyatı testi", Status = ProductStatus.Active, VatRate = vat, CreatedAt = now, UpdatedAt = now };
            var barcode = $"869{Guid.NewGuid().ToString("N")[..10]}";
            var variant = new ProductVariant { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProductId = product.Id, Sku = code, SkuNormalized = code.ToUpperInvariant(), Barcode = barcode, BarcodeNormalized = barcode.ToUpperInvariant(), OptionSignature = code, Status = status, DefaultSalePrice = sale, DefaultListPrice = list, CreatedAt = now, UpdatedAt = now };
            db.Products.Add(product);
            db.ProductVariants.Add(variant);
            db.InventoryItems.Add(new InventoryItem { Id = Guid.CreateVersion7(), TenantId = tenant.Id, VariantId = variant.Id, LocationCode = "MAIN", OnHand = 7m, Available = 7m, ProjectionVersion = 5, Version = 1 });
            if (link) db.MarketplaceVariantLinks.Add(new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, VariantId = variant.Id, ExternalId = barcode, ExternalBarcode = barcode, ExternalSku = code, Version = 1 });
            return variant;
        }
        var seed = new Seed(tenant, connection,
            Variant("PRICED", 200m, 250m),
            Variant("NOPANEL", null, null),
            Variant("ZERO", 0m, 0m),
            Variant("LISTBELOW", 200m, 150m),
            Variant("NOVAT", 200m, 250m, vat: null),
            Variant("UNLINKED", 200m, 250m, link: false),
            Variant("ARCHIVED", 200m, 250m, status: ProductStatus.Archived),
            Variant("WITHOFFER", 310m, 340m));
        AddOffer(db, tenant, connection, seed.WithOffer, 330m, 300m, null);
        await db.SaveChangesAsync();
        return seed;
    }

    private async Task<PlatformConnection> AddConnectionAsync(Tenant tenant, string platformCode, bool writesOpen)
    {
        await using var db = fixture.CreateContext();
        var connection = Connection(db, tenant, platformCode, writesOpen);
        await db.SaveChangesAsync();
        return connection;
    }

    private static PlatformConnection Connection(AppDbContext db, Tenant tenant, string platformCode, bool writesOpen)
    {
        var connection = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = platformCode, Environment = "PRODUCTION", DisplayName = $"{platformCode} panel fiyatı", ExternalStoreId = $"store-{Guid.NewGuid():N}", Status = "ACTIVE", ApiVersion = "V1", SettingsJson = "{\"ExternalWritesEnabled\":true}", Version = 1 };
        db.PlatformConnections.Add(connection);
        db.ConnectionSyncPolicies.AddRange(
            new ConnectionSyncPolicy { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, ResourceType = MarketplaceExternalWritePolicies.Price, IntervalSeconds = 0, Enabled = writesOpen, Version = 1 },
            new ConnectionSyncPolicy { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, ResourceType = MarketplaceExternalWritePolicies.Stock, IntervalSeconds = 0, Enabled = writesOpen, Version = 1 });
        return connection;
    }

    private static Guid AddOffer(AppDbContext db, Tenant tenant, PlatformConnection connection, ProductVariant variant, decimal list, decimal sale, bool? follows)
    {
        var offer = new ChannelOffer { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, VariantId = variant.Id, ListPrice = list, SalePrice = sale, Currency = "TRY", VatRate = 20m, VatInclusion = "INCLUDED", RoundingMode = "HALF_EVEN", Status = "ACTIVE", FollowsPanelPrice = follows, PriceVersion = 1, Version = 1 };
        db.ChannelOffers.Add(offer);
        return offer.Id;
    }

    private static UpdateExternalWriteGroupCommand Group(bool enabled) =>
        new(enabled, [new ExternalWriteGroupPolicy(MarketplaceExternalWritePolicies.Price, 1), new ExternalWriteGroupPolicy(MarketplaceExternalWritePolicies.Stock, 1)]);

    private MarketplaceConnectionService Connections(AppDbContext db)
    {
        var dataProtection = new EphemeralDataProtectionProvider();
        return new MarketplaceConnectionService(db, new CursorCodec(dataProtection, fixture.TimeProvider), dataProtection, fixture.TokenHasher, fixture.TimeProvider, WritesOn());
    }

    private InventoryService Inventory(AppDbContext db) =>
        new(db, new CursorCodec(new EphemeralDataProtectionProvider(), TimeProvider.System), fixture.TimeProvider, WritesOn());

    private static IConfiguration WritesOn() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["FeatureFlags:ExternalWrites"] = "true" }).Build();
}
