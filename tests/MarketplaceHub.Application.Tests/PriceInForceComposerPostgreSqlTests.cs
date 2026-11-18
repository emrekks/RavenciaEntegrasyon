using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Owner rule of 18 Nov 2026: "Stokla beraber fiyatı da gönderelim; orada fark etmeden farklı fiyat olmasın." Every stock
// send carries the price in force on every marketplace: Özel Fiyat on → the offer's own price; else the saved panel price;
// without a saved panel price → the offer's stored price. Without a price in force the stock goes alone with a note where
// the marketplace takes it (PTT AVM does not: the line is refused). Only the stock changed in every case here (the offer
// counts as sent at its stored price, decision B).
[Collection("postgres-integration")]
public sealed class PriceInForceComposerPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    public static TheoryData<string> Platforms => new() { "TRENDYOL", "HEPSIBURADA", "SHOPIFY", "N11", "PAZARAMA", "PTTAVM" };

    [PostgreSqlTheory]
    [MemberData(nameof(Platforms))]
    public async Task WithoutASavedPanelPrice_TheStockGoesWithTheOffersStoredPrice(string platform)
    {
        var seed = await SeedAsync(platform, panel: null, follows: null);
        var line = await SingleLineAsync(seed);
        // Pazarama refuses a price equal to the one it holds (the offer's stored price was read from it): the documented
        // exception, the stock goes alone without a note.
        Assert.Equal((7m, false, platform == "PAZARAMA", 300m), (line.Quantity, line.QuantityOmitted, line.PriceOmitted, line.SalePrice));
        Assert.Null(line.PriceNote);
    }

    [PostgreSqlTheory]
    [MemberData(nameof(Platforms))]
    public async Task WithASavedPanelPrice_TheStockGoesWithThePanelPrice(string platform)
    {
        var seed = await SeedAsync(platform, panel: (280m, 250m), follows: null);
        var (line, payload) = await SingleLineWithPayloadAsync(seed);
        Assert.Equal((7m, false, false, 250m), (line.Quantity, line.QuantityOmitted, line.PriceOmitted, line.SalePrice));
        // Sale-price-only marketplaces keep list = sale.
        Assert.Equal(platform is "HEPSIBURADA" or "PTTAVM" ? 250m : 280m, line.ListPrice);
        Assert.Contains("250", payload);
        Assert.DoesNotContain("300", payload);
    }

    [PostgreSqlTheory]
    [MemberData(nameof(Platforms))]
    public async Task OzelFiyatOn_TheStockGoesWithTheOffersOwnPrice(string platform)
    {
        var seed = await SeedAsync(platform, panel: (280m, 250m), follows: false);
        var (line, payload) = await SingleLineWithPayloadAsync(seed);
        // Pazarama: the own price equals the stored (and held) one, so it stays out (documented exception).
        Assert.Equal((platform == "PAZARAMA", 300m), (line.PriceOmitted, line.SalePrice));
        if (platform != "PAZARAMA") Assert.Contains("300", payload);
        Assert.DoesNotContain("250", payload);
    }

    // A saved panel price that cannot be sent (list below sale): the stock goes alone with a note; PTT AVM takes no stock
    // without a price, so its line is refused and counted.
    [PostgreSqlTheory]
    [MemberData(nameof(Platforms))]
    public async Task APanelPriceThatCannotBeSent_SendsTheStockAlone_OrRefusesWherePriceIsRequired(string platform)
    {
        var seed = await SeedAsync(platform, panel: (200m, 250m), follows: null);
        await using var db = fixture.CreateContext();
        var composer = new PriceInventoryComposer(db);
        var build = await composer.BuildAsync(seed.TenantId, seed.ConnectionId, CancellationToken.None, automatic: true);
        // PTT AVM takes no stock without a price; Trendyol's 0 TL rule, judged on the price in force, keeps such a line out.
        if (platform is "PTTAVM" or "TRENDYOL")
        {
            Assert.False(build.Succeeded && build.Value!.Lines.Count > 0);
            Assert.Equal("CHANNEL_OFFER_INVALID", Assert.Single(composer.InvalidOffers).Code);
            return;
        }
        Assert.True(build.Succeeded, build.Error?.Message);
        var line = Assert.Single(build.Value!.Lines);
        Assert.Equal((7m, false, true), (line.Quantity, line.QuantityOmitted, line.PriceOmitted));
        Assert.Equal(PanelPriceOfferPolicy.PriceNotSentNote, line.PriceNote);
        Assert.DoesNotContain("250", build.Value.PayloadJson);
    }

    // Decision B: an offer marked as sent at its values, with no stock change, sends nothing, whatever the panel price.
    [PostgreSqlTheory]
    [MemberData(nameof(Platforms))]
    public async Task NothingChanged_NothingGoes_EvenWithADifferentPanelPrice(string platform)
    {
        var seed = await SeedAsync(platform, panel: (280m, 250m), follows: null, stockChanged: false);
        await using var db = fixture.CreateContext();
        var build = await new PriceInventoryComposer(db).BuildAsync(seed.TenantId, seed.ConnectionId, CancellationToken.None, automatic: true);
        Assert.False(build.Succeeded && build.Value!.Lines.Count > 0);
    }

    // Owner decision of 18 Nov 2026: Trendyol's 0 TL offers stay without a price, judged on the price in force: an offer
    // stored at 0 goes at the saved panel price; without one it stays out (no stock either), as before.
    [PostgreSqlFact]
    public async Task TrendyolZeroTlOffer_GoesAtASavedPanelPrice_ElseStaysOut()
    {
        foreach (var (panel, sends) in new[] { ((decimal List, decimal Sale)?)(280m, 250m), null }.Select(panel => (panel, panel is not null)))
        {
            var seed = await SeedAsync("TRENDYOL", panel, follows: null, storedSale: 0m);
            await using var db = fixture.CreateContext();
            var composer = new PriceInventoryComposer(db);
            var build = await composer.BuildAsync(seed.TenantId, seed.ConnectionId, CancellationToken.None, automatic: true);
            if (sends)
            {
                var line = Assert.Single(build.Value!.Lines);
                Assert.Equal((false, 250m), (line.PriceOmitted, line.SalePrice));
            }
            else
            {
                Assert.False(build.Succeeded && build.Value!.Lines.Count > 0);
                Assert.Equal("CHANNEL_OFFER_INVALID", Assert.Single(composer.InvalidOffers).Code);
            }
        }
    }

    private sealed record Seed(Guid TenantId, Guid ConnectionId, Guid VariantId);

    private async Task<PriceInventoryPushLine> SingleLineAsync(Seed seed) => (await SingleLineWithPayloadAsync(seed)).Line;

    private async Task<(PriceInventoryPushLine Line, string Payload)> SingleLineWithPayloadAsync(Seed seed)
    {
        await using var db = fixture.CreateContext();
        var build = await new PriceInventoryComposer(db).BuildAsync(seed.TenantId, seed.ConnectionId, CancellationToken.None, automatic: true);
        Assert.True(build.Succeeded, build.Error?.Message);
        var line = Assert.Single(build.Value!.Lines);
        Assert.Equal(seed.VariantId, line.VariantId);
        return (line, build.Value.PayloadJson);
    }

    private async Task<Seed> SeedAsync(string platform, (decimal List, decimal Sale)? panel, bool? follows, bool stockChanged = true, decimal storedSale = 300m)
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"in-force-{Guid.NewGuid():N}", DisplayName = "price in force", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var connection = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = platform, Environment = "PRODUCTION", DisplayName = platform, ExternalStoreId = "store-in-force", Status = "ACTIVE", ApiVersion = "V1", SettingsJson = "{\"ExternalWritesEnabled\":true}", Version = 1 };
        var product = new Product { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Title = "Fiyat stok", Description = "geçerli fiyat", Status = ProductStatus.Active, VatRate = 20m, CreatedAt = now, UpdatedAt = now };
        // Codes drawn from digits that cannot spell a price checked in the payload (no 0, 2, 3 or 5).
        static string Digits(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => "146789"[Random.Shared.Next(6)]));
        var barcode = $"869{Digits(10)}";
        var sku = $"SKU-{Digits(12)}";
        var variant = new ProductVariant { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProductId = product.Id, Sku = sku, SkuNormalized = sku.ToUpperInvariant(), Barcode = barcode, BarcodeNormalized = barcode, OptionSignature = "Beden=M", DefaultListPrice = panel?.List, DefaultSalePrice = panel?.Sale, CreatedAt = now, UpdatedAt = now };
        var inventory = new InventoryItem { Id = Guid.CreateVersion7(), TenantId = tenant.Id, VariantId = variant.Id, LocationCode = "MAIN", OnHand = 7m, Available = 7m, ProjectionVersion = 6, Version = 1 };
        var link = new MarketplaceVariantLink
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            ConnectionId = connection.Id,
            VariantId = variant.Id,
            Version = 1,
            ExternalId = platform switch { "HEPSIBURADA" => "HBCV0000ABC1", "SHOPIFY" => "gid://shopify/ProductVariant/1001", "N11" => sku, _ => barcode },
            ExternalBarcode = barcode,
            ExternalSku = platform == "HEPSIBURADA" ? sku : null
        };
        var offer = new ChannelOffer { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, VariantId = variant.Id, ListPrice = storedSale == 0m ? 0m : 330m, SalePrice = storedSale, Currency = "TRY", VatRate = 20m, VatInclusion = "INCLUDED", RoundingMode = "HALF_EVEN", Status = "ACTIVE", FollowsPanelPrice = follows, PriceVersion = 1, Version = 1 };
        // Decision B: the offer counts as sent at its stored price; the stock changed since (or not).
        offer.LastPriceHash = PriceInventoryComposer.CurrentPriceHash(platform, offer);
        offer.LastStockProjectionVersion = stockChanged ? 5 : 6;
        await using var db = fixture.CreateContext();
        db.Tenants.Add(tenant);
        db.PlatformConnections.Add(connection);
        db.Products.Add(product);
        db.ProductVariants.Add(variant);
        db.InventoryItems.Add(inventory);
        db.MarketplaceVariantLinks.Add(link);
        db.ChannelOffers.Add(offer);
        await db.SaveChangesAsync();
        return new(tenant.Id, connection.Id, variant.Id);
    }
}
