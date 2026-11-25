using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.N11;
using MarketplaceHub.Infrastructure.Adapters.Pazarama;
using MarketplaceHub.Infrastructure.Adapters.Trendyol.Mapping;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests.Contracts;

// ISLEYICI-9 stage 3d (16 Nov 2026): the safety net for moving the catalog import out of the job processor. Each
// marketplace's product contract fixture is mapped as its contract test maps it and read by a full product import job into
// an empty store; the products, models, variants, offers, inventory rows, attribute assignments, model, product and variant
// links and issues it leaves are written into a golden (no ids or times). The golden must not change while the code moves.
// The reference read answers with a refusal: the import reads only the products and maps no category or brand on its own.
[Collection("postgres-integration")]
public sealed class ProductImportGoldenPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    public static TheoryData<string> Pages => new() { "Trendyol", "N11", "Pazarama" };

    [PostgreSqlTheory]
    [MemberData(nameof(Pages))]
    public async Task AProductPageImportsTheSameCatalog(string marketplace)
    {
        var (platform, products) = MappedPage(marketplace);
        Assert.NotEmpty(products);
        var now = fixture.Now;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"pgold-{marketplace.ToLowerInvariant()}-{Guid.NewGuid():N}"[..30], DisplayName = "golden", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var connection = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = platform, Environment = "PRODUCTION", DisplayName = marketplace, ExternalStoreId = $"store-{Guid.NewGuid():N}", Status = "ACTIVE", ApiVersion = "V1", SettingsJson = "{}", Version = 1 };
        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.Add(tenant);
            seed.PlatformConnections.Add(connection);
            await seed.SaveChangesAsync();
        }

        await using (var db = fixture.CreateContext())
        {
            var processor = new MarketplaceJobProcessor(db, null!, new RefusingReferences(), new CatalogPage(products), null!, null!, null!, null!, null!, null!, null!, new ConfigurationBuilder().Build(), fixture.TimeProvider);
            var result = await processor.ProcessAsync(tenant.Id, connection.Id, MarketplaceJobTypes.ProductSync, """{"full":true,"includeArchived":true,"includeDrafts":true}""", $"product-golden-{Guid.NewGuid():N}", CancellationToken.None);
            Assert.True(result.Kind is JobCompletionKind.Succeeded or JobCompletionKind.ManualReview, $"{result.Kind} {result.ErrorCode} {result.ErrorSummary}");
        }

        await using var check = fixture.CreateContext();
        var t = tenant.Id;
        var summary = new
        {
            Products = (await check.Products.AsNoTracking().Where(x => x.TenantId == t).Select(x => new { x.Title, Status = x.Status.ToString() }).ToListAsync()).OrderBy(x => x.Title, StringComparer.Ordinal).ThenBy(x => x.Status, StringComparer.Ordinal).ToList(),
            Models = await check.ProductModels.AsNoTracking().CountAsync(x => x.TenantId == t),
            Variants = (await check.ProductVariants.AsNoTracking().Where(x => x.TenantId == t).Select(x => new { x.Sku, x.Barcode, x.ModelCode, x.OptionSignature, Status = x.Status.ToString() }).ToListAsync())
                .OrderBy(x => x.Sku, StringComparer.Ordinal).ThenBy(x => x.Barcode, StringComparer.Ordinal).ThenBy(x => x.ModelCode, StringComparer.Ordinal).ThenBy(x => x.OptionSignature, StringComparer.Ordinal).ToList(),
            Offers = (await check.ChannelOffers.AsNoTracking().Where(x => x.TenantId == t).Select(x => new { x.SalePrice, x.ListPrice, x.Currency, x.VatRate, x.Status }).ToListAsync())
                .OrderBy(x => x.SalePrice).ThenBy(x => x.ListPrice).ThenBy(x => x.Currency, StringComparer.Ordinal).ThenBy(x => x.VatRate).ThenBy(x => x.Status.ToString(), StringComparer.Ordinal).ToList(),
            Inventory = (await check.InventoryItems.AsNoTracking().Where(x => x.TenantId == t).Select(x => new { x.LocationCode, x.OnHand, x.Available }).ToListAsync())
                .OrderBy(x => x.OnHand).ThenBy(x => x.Available).ThenBy(x => x.LocationCode, StringComparer.Ordinal).ToList(),
            AttributeAssignments = await check.ProductAttributeAssignments.AsNoTracking().CountAsync(x => x.TenantId == t),
            ModelLinks = (await check.MarketplaceModelLinks.AsNoTracking().Where(x => x.TenantId == t).Select(x => x.ExternalId).ToListAsync()).Order(StringComparer.Ordinal).ToList(),
            ProductLinks = (await check.MarketplaceProductLinks.AsNoTracking().Where(x => x.TenantId == t).Select(x => new { x.ExternalId, x.ExternalCode }).ToListAsync()).OrderBy(x => x.ExternalId, StringComparer.Ordinal).ToList(),
            VariantLinks = (await check.MarketplaceVariantLinks.AsNoTracking().Where(x => x.TenantId == t).Select(x => new { x.ExternalId, x.ExternalBarcode, x.ExternalSku }).ToListAsync())
                .OrderBy(x => x.ExternalId, StringComparer.Ordinal).ThenBy(x => x.ExternalBarcode, StringComparer.Ordinal).ThenBy(x => x.ExternalSku, StringComparer.Ordinal).ToList(),
            Issues = (await check.OperationalIssues.AsNoTracking().Where(x => x.TenantId == t).Select(x => x.Code).ToListAsync()).Order(StringComparer.Ordinal).ToList()
        };
        ContractFixture.MatchesGolden($"{marketplace}/products-import.golden.json", summary);
    }

    // The same Trendyol page through the real import job: each variant the run links gets the title, description and desi
    // the page carried, dated with the read and marked IMPORT (MarketplaceKnownListingValueStore); the panel card's own
    // values are not what is recorded, and the golden summary above is untouched.
    [PostgreSqlFact]
    public async Task ATrendyolPageImportRecordsWhatTheMarketplaceGaveForTheVariantsItLinks()
    {
        var (platform, products) = MappedPage("Trendyol");
        var now = fixture.Now;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"pknown-{Guid.NewGuid():N}"[..30], DisplayName = "known", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var connection = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = platform, Environment = "PRODUCTION", DisplayName = "Trendyol", ExternalStoreId = $"store-{Guid.NewGuid():N}", Status = "ACTIVE", ApiVersion = "V1", SettingsJson = "{}", Version = 1 };
        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.Add(tenant);
            seed.PlatformConnections.Add(connection);
            await seed.SaveChangesAsync();
        }
        await using (var db = fixture.CreateContext())
        {
            var processor = new MarketplaceJobProcessor(db, null!, new RefusingReferences(), new CatalogPage(products), null!, null!, null!, null!, null!, null!, null!, new ConfigurationBuilder().Build(), fixture.TimeProvider);
            var result = await processor.ProcessAsync(tenant.Id, connection.Id, MarketplaceJobTypes.ProductSync, """{"full":true,"includeArchived":true,"includeDrafts":true}""", $"product-known-{Guid.NewGuid():N}", CancellationToken.None);
            Assert.True(result.Kind is JobCompletionKind.Succeeded or JobCompletionKind.ManualReview, $"{result.Kind} {result.ErrorCode} {result.ErrorSummary}");
        }

        await using var check = fixture.CreateContext();
        var links = await check.MarketplaceVariantLinks.AsNoTracking().Where(x => x.TenantId == tenant.Id && x.ConnectionId == connection.Id).ToListAsync();
        var known = await check.MarketplaceKnownListingValues.AsNoTracking().Where(x => x.TenantId == tenant.Id).ToListAsync();
        Assert.NotEmpty(links);
        var expected = products.SelectMany(product => MarketplaceKnownListingValueStore.ImportValues(platform, product)).ToDictionary(x => x.ExternalVariantId);
        foreach (var link in links.Where(link => expected.ContainsKey(link.ExternalId)))
        {
            var row = Assert.Single(known, x => x.VariantId == link.VariantId);
            var read = expected[link.ExternalId];
            Assert.Equal((connection.Id, read.Title, read.Description, read.Desi), (row.ConnectionId, row.Title, row.Description, row.Desi is { } desi ? decimal.Round(desi, 2) : (decimal?)null));
            Assert.Equal(read.Title is null ? null : "IMPORT", row.TitleSource);
            Assert.True(row.TitleReadAt is null || row.TitleReadAt <= fixture.TimeProvider.GetUtcNow());
        }
        Assert.Contains(known, x => x.Title is not null);
        Assert.All(known, row => Assert.Contains(links, link => link.VariantId == row.VariantId));
    }

    // Owner rule of 18 Nov 2026: a variant first read from a marketplace takes that marketplace's price as its panel price
    // (sale and list, as its offer stores them); a later import never changes a panel price already there.
    [PostgreSqlTheory]
    [MemberData(nameof(Pages))]
    public async Task ANewVariantTakesTheMarketplacePriceAsItsPanelPrice_AndALaterImportKeepsIt(string marketplace)
    {
        var (platform, products) = MappedPage(marketplace);
        var now = fixture.Now;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"ppanel-{marketplace.ToLowerInvariant()}-{Guid.NewGuid():N}"[..30], DisplayName = "panel", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var connection = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = platform, Environment = "PRODUCTION", DisplayName = marketplace, ExternalStoreId = $"store-{Guid.NewGuid():N}", Status = "ACTIVE", ApiVersion = "V1", SettingsJson = "{}", Version = 1 };
        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.Add(tenant);
            seed.PlatformConnections.Add(connection);
            await seed.SaveChangesAsync();
        }
        async Task ImportAsync(string payload = """{"full":true,"includeArchived":true,"includeDrafts":true}""")
        {
            await using var db = fixture.CreateContext();
            var processor = new MarketplaceJobProcessor(db, null!, new RefusingReferences(), new CatalogPage(products), null!, null!, null!, null!, null!, null!, null!, new ConfigurationBuilder().Build(), fixture.TimeProvider);
            var result = await processor.ProcessAsync(tenant.Id, connection.Id, MarketplaceJobTypes.ProductSync, payload, $"panel-price-{Guid.NewGuid():N}", CancellationToken.None);
            Assert.True(result.Kind is JobCompletionKind.Succeeded or JobCompletionKind.ManualReview, $"{result.Kind} {result.ErrorCode} {result.ErrorSummary}");
        }
        await ImportAsync();

        await using (var check = fixture.CreateContext())
        {
            var rows = await (from variant in check.ProductVariants.AsNoTracking()
                              join offer in check.ChannelOffers.AsNoTracking() on variant.Id equals offer.VariantId
                              where variant.TenantId == tenant.Id
                              select new { variant.Id, variant.DefaultSalePrice, variant.DefaultListPrice, offer.SalePrice, offer.ListPrice }).ToListAsync();
            Assert.NotEmpty(rows);
            Assert.Contains(rows, row => row.SalePrice > 0);
            foreach (var row in rows)
                if (row.SalePrice > 0) Assert.Equal((row.ListPrice, row.SalePrice), (row.DefaultListPrice!.Value, row.DefaultSalePrice!.Value));
                else Assert.Null(row.DefaultSalePrice);
            // Ürün Hareketleri records each panel price the import wrote.
            Assert.Equal(rows.Count(row => row.SalePrice > 0), await check.ProductChangeEntries.CountAsync(x => x.TenantId == tenant.Id && x.Source == ProductChangeSources.MarketplaceImport && x.Kind == ProductChangeKinds.DefaultPrice));
            // The owner changes a panel price; the next import leaves it.
            await check.ProductVariants.Where(x => x.Id == rows[0].Id).ExecuteUpdateAsync(set => set.SetProperty(x => x.DefaultSalePrice, 1.5m).SetProperty(x => x.DefaultListPrice, 2.5m));
        }
        // An existing variant whose panel price was cleared is not filled by a later import either (review A10).
        Guid cleared;
        await using (var check = fixture.CreateContext())
        {
            cleared = await check.ProductVariants.Where(x => x.TenantId == tenant.Id && x.DefaultSalePrice > 1.5m).Select(x => x.Id).FirstAsync();
            await check.ProductVariants.Where(x => x.Id == cleared).ExecuteUpdateAsync(set => set.SetProperty(x => x.DefaultSalePrice, (decimal?)null).SetProperty(x => x.DefaultListPrice, (decimal?)null));
        }
        await ImportAsync();
        // The paths that walk existing variants without updatePrices (existing products updated with stock only, and the
        // focused stock pull) leave every panel price as it is (review A10).
        await ImportAsync("""{"existingOnly":true,"updateExistingProducts":true,"updatePrices":false,"updateStock":true}""");
        await ImportAsync("""{"existingOnly":true,"updateExistingProducts":false,"updatePrices":false,"updateStock":true}""");
        await using var after = fixture.CreateContext();
        var kept = await after.ProductVariants.AsNoTracking().SingleAsync(x => x.TenantId == tenant.Id && x.DefaultSalePrice == 1.5m);
        Assert.Equal(2.5m, kept.DefaultListPrice);
        Assert.Null((await after.ProductVariants.AsNoTracking().SingleAsync(x => x.Id == cleared)).DefaultSalePrice);
    }

    // One rule on import (owner decisions of 18 Nov 2026): "Fiyat ve Stokları Güncelle" makes the marketplace's price the
    // panel price; the offer without Özel Fiyat stores it, whether the import updates it or opens it again, with the
    // marketplace's price as the known one; an offer with Özel Fiyat on takes the marketplace's price and leaves the panel.
    [PostgreSqlFact]
    public async Task AnImportKeepsThePanelPriceAsTheStoredPrice_AndTheMarketplacePriceAsTheKnownOne()
    {
        var (platform, products) = MappedPage("Trendyol");
        var now = fixture.Now;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"ponerule-{Guid.NewGuid():N}"[..30], DisplayName = "one rule", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var connection = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = platform, Environment = "PRODUCTION", DisplayName = "Trendyol", ExternalStoreId = $"store-{Guid.NewGuid():N}", Status = "ACTIVE", ApiVersion = "V1", SettingsJson = "{}", Version = 1 };
        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.Add(tenant);
            seed.PlatformConnections.Add(connection);
            await seed.SaveChangesAsync();
        }
        async Task ImportAsync()
        {
            await using var db = fixture.CreateContext();
            var processor = new MarketplaceJobProcessor(db, null!, new RefusingReferences(), new CatalogPage(products), null!, null!, null!, null!, null!, null!, null!, new ConfigurationBuilder().Build(), fixture.TimeProvider);
            var result = await processor.ProcessAsync(tenant.Id, connection.Id, MarketplaceJobTypes.ProductSync, """{"full":true,"includeArchived":true,"includeDrafts":true,"updatePrices":true}""", $"one-rule-{Guid.NewGuid():N}", CancellationToken.None);
            Assert.True(result.Kind is JobCompletionKind.Succeeded or JobCompletionKind.ManualReview, $"{result.Kind} {result.ErrorCode} {result.ErrorSummary}");
        }
        await ImportAsync();
        Guid followerId, customId, reopenedVariant;
        decimal followerRemote, customRemote;
        await using (var db = fixture.CreateContext())
        {
            var offers = await db.ChannelOffers.Where(x => x.TenantId == tenant.Id && x.SalePrice > 0).OrderBy(x => x.Id).Take(3).ToListAsync();
            Assert.Equal(3, offers.Count);
            (followerId, followerRemote) = (offers[0].Id, offers[0].SalePrice);
            (customId, customRemote) = (offers[1].Id, offers[1].SalePrice);
            reopenedVariant = offers[2].VariantId;
            offers[1].FollowsPanelPrice = false;
            await db.SaveChangesAsync();
            // The owner saves panel prices (5 TL above the marketplace's) and the third offer is gone (a second marketplace opens it).
            foreach (var offer in offers)
                await db.ProductVariants.Where(x => x.Id == offer.VariantId).ExecuteUpdateAsync(set => set.SetProperty(x => x.DefaultSalePrice, offer.SalePrice + 5m).SetProperty(x => x.DefaultListPrice, offer.SalePrice + 5m));
            await db.ChannelPriceHistory.Where(x => x.OfferId == offers[2].Id).ExecuteDeleteAsync();
            await db.ChannelOffers.Where(x => x.Id == offers[2].Id).ExecuteDeleteAsync();
        }
        await ImportAsync();
        await using var read = fixture.CreateContext();
        var follower = await read.ChannelOffers.AsNoTracking().SingleAsync(x => x.Id == followerId);
        Assert.Equal((followerRemote, followerRemote), (follower.SalePrice, follower.LastSentSalePrice));
        Assert.Equal(followerRemote, (await read.ProductVariants.AsNoTracking().SingleAsync(x => x.Id == follower.VariantId)).DefaultSalePrice);
        var custom = await read.ChannelOffers.AsNoTracking().SingleAsync(x => x.Id == customId);
        Assert.Equal(customRemote, custom.SalePrice);
        Assert.Equal(customRemote + 5m, (await read.ProductVariants.AsNoTracking().SingleAsync(x => x.Id == custom.VariantId)).DefaultSalePrice);
        var reopened = await read.ChannelOffers.AsNoTracking().SingleAsync(x => x.TenantId == tenant.Id && x.ConnectionId == connection.Id && x.VariantId == reopenedVariant);
        var panel = (await read.ProductVariants.AsNoTracking().SingleAsync(x => x.Id == reopenedVariant)).DefaultSalePrice;
        Assert.Equal((panel, panel), (reopened.SalePrice, reopened.LastSentSalePrice));
    }

    // Owner decision of 18 Nov 2026: "Fiyat ve Stokları Güncelle" (updatePrices, confirmed in the panel) takes the marketplace
    // price as the panel price of an existing variant too; the offers without Özel Fiyat on other marketplaces follow it,
    // keeping their previous price as the known one, with no job queued; an offer with Özel Fiyat on takes the price itself
    // and leaves the panel price. An import without updatePrices leaves the panel price.
    [PostgreSqlFact]
    public async Task UpdatePricesImport_SetsThePanelPrice_PullsTheFollowers_AndQueuesNothing()
    {
        var (platform, products) = MappedPage("Trendyol");
        var now = fixture.Now;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"pupdate-{Guid.NewGuid():N}"[..30], DisplayName = "update prices", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var connection = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = platform, Environment = "PRODUCTION", DisplayName = "Trendyol", ExternalStoreId = $"store-{Guid.NewGuid():N}", Status = "ACTIVE", ApiVersion = "V1", SettingsJson = "{}", Version = 1 };
        var n11 = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = "N11", Environment = "PRODUCTION", DisplayName = "N11", ExternalStoreId = $"store-{Guid.NewGuid():N}", Status = "ACTIVE", ApiVersion = "V1", SettingsJson = "{}", Version = 1 };
        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.Add(tenant);
            seed.PlatformConnections.AddRange(connection, n11);
            await seed.SaveChangesAsync();
        }
        async Task ImportAsync(IReadOnlyList<RemoteCatalogProduct> page, string payload)
        {
            await using var db = fixture.CreateContext();
            var processor = new MarketplaceJobProcessor(db, null!, new RefusingReferences(), new CatalogPage(page), null!, null!, null!, null!, null!, null!, null!, new ConfigurationBuilder().Build(), fixture.TimeProvider);
            var result = await processor.ProcessAsync(tenant.Id, connection.Id, MarketplaceJobTypes.ProductSync, payload, $"update-prices-{Guid.NewGuid():N}", CancellationToken.None);
            Assert.True(result.Kind is JobCompletionKind.Succeeded or JobCompletionKind.ManualReview, $"{result.Kind} {result.ErrorCode} {result.ErrorSummary}");
        }
        await ImportAsync(products, """{"full":true,"includeArchived":true,"includeDrafts":true}""");

        Guid follower, custom, n11Offer;
        decimal followerPanel, customPanel;
        await using (var db = fixture.CreateContext())
        {
            var offers = await db.ChannelOffers.Where(x => x.TenantId == tenant.Id && x.SalePrice > 0).OrderBy(x => x.Id).Take(2).ToListAsync();
            Assert.Equal(2, offers.Count);
            (follower, custom) = (offers[0].VariantId, offers[1].VariantId);
            offers[1].FollowsPanelPrice = false;
            followerPanel = (await db.ProductVariants.AsNoTracking().SingleAsync(x => x.Id == follower)).DefaultSalePrice!.Value;
            customPanel = (await db.ProductVariants.AsNoTracking().SingleAsync(x => x.Id == custom)).DefaultSalePrice!.Value;
            var n11Row = new ChannelOffer { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = n11.Id, VariantId = follower, ListPrice = followerPanel, SalePrice = followerPanel, Currency = "TRY", VatRate = 20m, VatInclusion = "INCLUDED", RoundingMode = "HALF_EVEN", Status = "ACTIVE", PriceVersion = 1, Version = 1 };
            n11Row.LastPriceHash = PriceInventoryComposer.CurrentPriceHash("N11", n11Row);
            db.ChannelOffers.Add(n11Row);
            n11Offer = n11Row.Id;
            await db.SaveChangesAsync();
        }
        var raised = products.Select(product => product with { Variants = [.. product.Variants.Select(variant => variant with { SalePrice = variant.SalePrice + 10m, ListPrice = (variant.ListPrice ?? variant.SalePrice) + 10m })] }).ToList();

        // Without updatePrices (stock only): the panel price stays.
        await ImportAsync(raised, """{"existingOnly":true,"updateExistingProducts":false,"updatePrices":false,"updateStock":true}""");
        await using (var db = fixture.CreateContext())
            Assert.Equal(followerPanel, (await db.ProductVariants.AsNoTracking().SingleAsync(x => x.Id == follower)).DefaultSalePrice);

        await using (var db = fixture.CreateContext())
            await db.IntegrationJobs.Where(x => x.TenantId == tenant.Id).ExecuteDeleteAsync();
        await ImportAsync(raised, """{"existingOnly":true,"updateExistingProducts":false,"updatePrices":true,"updateStock":false}""");

        await using var read = fixture.CreateContext();
        Assert.Equal(followerPanel + 10m, (await read.ProductVariants.AsNoTracking().SingleAsync(x => x.Id == follower)).DefaultSalePrice);
        var pulled = await read.ChannelOffers.AsNoTracking().SingleAsync(x => x.Id == n11Offer);
        Assert.Equal((followerPanel + 10m, followerPanel, 2L), (pulled.SalePrice, pulled.LastSentSalePrice, pulled.PriceVersion));
        Assert.Equal(PriceInventoryComposer.CurrentPriceHash("N11", pulled), pulled.LastPriceHash);
        Assert.Single(await read.ChannelPriceHistory.AsNoTracking().Where(x => x.OfferId == n11Offer).ToListAsync());
        // Özel Fiyat on: the offer took Trendyol's price, the panel price stayed.
        Assert.Equal(customPanel, (await read.ProductVariants.AsNoTracking().SingleAsync(x => x.Id == custom)).DefaultSalePrice);
        Assert.Equal(customPanel + 10m, (await read.ChannelOffers.AsNoTracking().SingleAsync(x => x.ConnectionId == connection.Id && x.VariantId == custom)).SalePrice);
        Assert.Contains(await read.ProductChangeEntries.AsNoTracking().Where(x => x.TenantId == tenant.Id && x.VariantId == follower && x.Kind == ProductChangeKinds.DefaultPrice).Select(x => x.Source).ToListAsync(), source => source == ProductChangeSources.MarketplaceImport);
        // No price job: the difference goes with the next stock send.
        Assert.Equal(0, await read.IntegrationJobs.CountAsync(x => x.TenantId == tenant.Id && (x.JobType == MarketplaceJobTypes.StockProjectionDispatch || x.JobType == MarketplaceJobTypes.PriceInventorySync)));
    }

    // B-6: an offer the import opens (or reprices) carries the price mark its own platform's send leaves behind. It was the
    // Trendyol-format mark on every platform, so N11 and Pazarama saw a price change that was never made and sent it.
    [PostgreSqlTheory]
    [InlineData("N11")]
    [InlineData("Pazarama")]
    public async Task ImportedOffer_CarriesItsOwnPlatformsPriceMark(string marketplace)
    {
        var (platform, products) = MappedPage(marketplace);
        var now = fixture.Now;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"pmark-{Guid.NewGuid():N}"[..30], DisplayName = "price mark", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var connection = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = platform, Environment = "PRODUCTION", DisplayName = marketplace, ExternalStoreId = $"store-{Guid.NewGuid():N}", Status = "ACTIVE", ApiVersion = "V1", SettingsJson = "{}", Version = 1 };
        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.Add(tenant);
            seed.PlatformConnections.Add(connection);
            await seed.SaveChangesAsync();
        }
        await using (var db = fixture.CreateContext())
        {
            var processor = new MarketplaceJobProcessor(db, null!, new RefusingReferences(), new CatalogPage(products), null!, null!, null!, null!, null!, null!, null!, new ConfigurationBuilder().Build(), fixture.TimeProvider);
            var result = await processor.ProcessAsync(tenant.Id, connection.Id, MarketplaceJobTypes.ProductSync, """{"full":true,"includeArchived":true,"includeDrafts":true}""", $"price-mark-{Guid.NewGuid():N}", CancellationToken.None);
            Assert.True(result.Kind is JobCompletionKind.Succeeded or JobCompletionKind.ManualReview, $"{result.Kind} {result.ErrorCode} {result.ErrorSummary}");
        }

        await using var read = fixture.CreateContext();
        var offers = await read.ChannelOffers.AsNoTracking().Where(x => x.TenantId == tenant.Id).ToListAsync();
        Assert.NotEmpty(offers);
        Assert.All(offers, offer =>
        {
            Assert.Equal(PriceInventoryComposer.CurrentPriceHash(platform, offer), offer.LastPriceHash);
            Assert.NotEqual(PriceInventoryComposer.StandardPriceHash(offer), offer.LastPriceHash);
        });
    }

    private static (string Platform, IReadOnlyList<RemoteCatalogProduct> Products) MappedPage(string marketplace)
    {
        switch (marketplace)
        {
            case "Trendyol":
                return ("TRENDYOL", TrendyolJsonMapper.CatalogProducts(ContractFixture.Read("Trendyol/products-approved.json")).Items);
            case "N11":
                {
                    using var document = ContractFixture.Json("N11/product-query-docs-sample.json");
                    return ("N11", N11ProductMapper.Page(document.RootElement).Items);
                }
            default:
                {
                    using var document = ContractFixture.Json("Pazarama/products-approved-docs-sample.json");
                    return ("PAZARAMA", PazaramaProductMapper.Page(document.RootElement, pending: false).Items);
                }
        }
    }

    // One page holding every product of the fixture; nothing is written.
    private sealed class CatalogPage(IReadOnlyList<RemoteCatalogProduct> products) : IProductPort
    {
        public Task<AdapterResult<AdapterPageResult<RemoteCatalogProduct>>> ListCatalogAsync(AdapterContext context, AdapterPageRequest page, ProductReadFilter filter, CancellationToken cancellationToken) =>
            Task.FromResult(AdapterResult<AdapterPageResult<RemoteCatalogProduct>>.Success(new(products, null, false, products.Count)));

        public Task<AdapterResult<AdapterPageResult<RemoteProduct>>> ListAsync(AdapterContext context, AdapterPageRequest page, ProductReadFilter filter, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<RemoteOperationRef>> CreateAsync(AdapterContext context, ProductPublication publication, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<RemoteOperationRef>> UpdateUnapprovedAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<RemoteOperationRef>> UpdateApprovedContentAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<RemoteOperationRef>> UpdateApprovedVariantsAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<RemoteOperationRef>> UpdateApprovedDeliveryAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<RemoteOperationStatus>> GetOperationAsync(AdapterContext context, string externalOperationId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<RemotePublicationStatus>> GetPublicationStatusAsync(AdapterContext context, string barcode, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<RemoteOperationRef>> ArchiveAsync(AdapterContext context, string payloadJson, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class RefusingReferences : IReferenceDataPort
    {
        public Task<AdapterResult<AdapterPageResult<RemoteReferenceItem>>> ReadAsync(AdapterContext context, ReferenceResource resource, AdapterPageRequest page, CancellationToken cancellationToken) =>
            Task.FromResult(AdapterResult<AdapterPageResult<RemoteReferenceItem>>.Failure(new AdapterError(AdapterErrorClass.NotSupported, "TEST_REFERENCES_REFUSED", "Referans okunmadı.", 400, null, null)));
    }
}
