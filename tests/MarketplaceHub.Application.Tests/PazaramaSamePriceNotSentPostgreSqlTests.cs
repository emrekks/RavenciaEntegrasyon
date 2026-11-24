using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.Pazarama;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// O2 (scan of 23 Nov 2026): Pazarama refuses a price equal to the one it holds ("… önceki değerleriyle aynı olamaz"), and a
// new price version alone (a VAT rate saved on a VAT-inclusive offer: the same outbound price) sent it again. The price
// goes only when it differs from the price Pazarama holds as far as the panel knows; a real price change still goes.
[Collection("postgres-integration")]
public sealed class PazaramaSamePriceNotSentPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    // A VAT rate saved alone: no stock change, so nothing goes, and the price mark moves on so the offer is not judged
    // again every round.
    [PostgreSqlFact]
    public async Task AVatOnlyPriceVersion_SendsNothing_AndSettlesTheMark()
    {
        var seed = await SeedAsync(vatOnlyChange: true, stockChanged: false);
        await using var db = fixture.CreateContext();
        var build = await new PriceInventoryComposer(db).BuildAsync(seed.TenantId, seed.ConnectionId, CancellationToken.None, automatic: true);
        Assert.False(build.Succeeded && build.Value!.Lines.Count > 0);
        await using var read = fixture.CreateContext();
        var offer = await read.ChannelOffers.AsNoTracking().SingleAsync(x => x.Id == seed.OfferId);
        Assert.Equal(PazaramaPriceInventoryRules.PriceHash(330m, 300m, 2), offer.LastPriceHash);
    }

    // The same with a stock change: the stock goes alone.
    [PostgreSqlFact]
    public async Task AVatOnlyPriceVersion_WithAStockChange_SendsTheStockAlone()
    {
        var seed = await SeedAsync(vatOnlyChange: true, stockChanged: true);
        var (line, payload) = await SingleLineAsync(seed);
        Assert.Equal((false, true), (line.QuantityOmitted, line.PriceOmitted));
        Assert.DoesNotContain("salePrice", payload);
    }

    // Pazarama confirmed this barcode's price before: the same price is not sent again either.
    [PostgreSqlFact]
    public async Task AVatOnlyPriceVersion_WithTheSamePriceConfirmed_SendsTheStockAlone()
    {
        var seed = await SeedAsync(vatOnlyChange: true, stockChanged: true, confirmed: 300m);
        var (line, _) = await SingleLineAsync(seed);
        Assert.True(line.PriceOmitted);
    }

    // A price Pazarama confirmed that differs from the one in force still goes, with a new price version.
    [PostgreSqlFact]
    public async Task AVatOnlyPriceVersion_WithADifferentPriceConfirmed_SendsThePrice()
    {
        var seed = await SeedAsync(vatOnlyChange: true, stockChanged: false, confirmed: 280m);
        var (line, payload) = await SingleLineAsync(seed);
        Assert.Equal((true, false, 300m), (line.QuantityOmitted, line.PriceOmitted, line.SalePrice));
        Assert.Contains("300", payload);
    }

    // A real change of the offer's price still goes, as before.
    [PostgreSqlFact]
    public async Task ARealPriceChange_SendsThePrice()
    {
        var seed = await SeedAsync(vatOnlyChange: false, stockChanged: false);
        var (line, payload) = await SingleLineAsync(seed);
        Assert.Equal((false, 320m), (line.PriceOmitted, line.SalePrice));
        Assert.Contains("320", payload);
    }

    // No price mark at all: what Pazarama holds is not known, so the price goes, as before.
    [PostgreSqlFact]
    public async Task WithoutAPriceMark_ThePriceGoes()
    {
        var seed = await SeedAsync(vatOnlyChange: true, stockChanged: false, marked: false);
        var (line, _) = await SingleLineAsync(seed);
        Assert.False(line.PriceOmitted);
    }

    // Review of 24 Nov 2026: Pazarama confirmed 300 TL (mark at version 1); the owner typed 30 TL (version 2) and sent it, its
    // batch result still out; then back to 300 TL (version 3). Pazarama may hold 30 TL once that result lands, so the
    // 300 TL is not taken as held: it goes, and the mark is not moved on without a send.
    [PostgreSqlFact]
    public async Task ABackAndForthPrice_WithAnotherPriceInBetween_SendsThePriceAgain()
    {
        var seed = await SeedAsync(vatOnlyChange: true, stockChanged: false, confirmed: 300m, salesByVersion: [300m, 30m, 300m]);
        var (line, payload) = await SingleLineAsync(seed);
        Assert.Equal((true, false, 300m, 3L), (line.QuantityOmitted, line.PriceOmitted, line.SalePrice, line.PriceVersion));
        Assert.Contains("300", payload);
        await using var read = fixture.CreateContext();
        var offer = await read.ChannelOffers.AsNoTracking().SingleAsync(x => x.Id == seed.OfferId);
        Assert.Equal(PazaramaPriceInventoryRules.PriceHash(330m, 300m, 1), offer.LastPriceHash);
    }

    // The same without a confirmed value (the stored price the mark names): the price goes too.
    [PostgreSqlFact]
    public async Task ABackAndForthPrice_WithoutAConfirmation_SendsThePriceAgain()
    {
        var seed = await SeedAsync(vatOnlyChange: true, stockChanged: false, salesByVersion: [300m, 30m, 300m]);
        var (line, _) = await SingleLineAsync(seed);
        Assert.Equal((false, 300m), (line.PriceOmitted, line.SalePrice));
    }

    // The history cannot prove the price stayed (a version missing from it): unknown, so the price goes.
    [PostgreSqlFact]
    public async Task AVatOnlyPriceVersion_MissingFromTheHistory_SendsThePrice()
    {
        var seed = await SeedAsync(vatOnlyChange: true, stockChanged: false, confirmed: 300m, missingHistoryVersion: 2);
        var (line, _) = await SingleLineAsync(seed);
        Assert.False(line.PriceOmitted);
    }

    // Several VAT-only versions after the mark keep the price known: still nothing goes.
    [PostgreSqlFact]
    public async Task SeveralVatOnlyPriceVersions_SendNothing()
    {
        var seed = await SeedAsync(vatOnlyChange: true, stockChanged: false, confirmed: 300m, salesByVersion: [300m, 300m, 300m, 300m]);
        await using var db = fixture.CreateContext();
        var build = await new PriceInventoryComposer(db).BuildAsync(seed.TenantId, seed.ConnectionId, CancellationToken.None, automatic: true);
        Assert.False(build.Succeeded && build.Value!.Lines.Count > 0);
    }

    private sealed record Seed(Guid TenantId, Guid ConnectionId, Guid OfferId);

    private async Task<(PriceInventoryPushLine Line, string Payload)> SingleLineAsync(Seed seed)
    {
        await using var db = fixture.CreateContext();
        var build = await new PriceInventoryComposer(db).BuildAsync(seed.TenantId, seed.ConnectionId, CancellationToken.None, automatic: true);
        Assert.True(build.Succeeded, build.Error?.Message);
        return (Assert.Single(build.Value!.Lines), build.Value.PayloadJson);
    }

    // A Pazarama offer read at 330 / 300 TL with VAT 20 % included, marked at price version 1. Then either its VAT rate alone
    // was saved (10 %, version 2, the same outbound price) or its price (320 TL, version 2). salesByVersion gives the sale
    // price of every version instead (the last is the current one); the price history records each version, but
    // missingHistoryVersion.
    private async Task<Seed> SeedAsync(bool vatOnlyChange, bool stockChanged, decimal? confirmed = null, bool marked = true, decimal[]? salesByVersion = null, long? missingHistoryVersion = null)
    {
        salesByVersion ??= vatOnlyChange ? [300m, 300m] : [300m, 320m];
        var now = DateTimeOffset.UtcNow;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"pz-same-{Guid.NewGuid():N}", DisplayName = "pazarama same price", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var connection = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = "PAZARAMA", Environment = "PRODUCTION", DisplayName = "Pazarama", ExternalStoreId = "store-pz", Status = "ACTIVE", ApiVersion = "V1", SettingsJson = "{\"ExternalWritesEnabled\":true}", Version = 1 };
        var product = new Product { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Title = "Fiyat", Description = "aynı fiyat", Status = ProductStatus.Active, VatRate = 20m, CreatedAt = now, UpdatedAt = now };
        static string Digits(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => "146789"[Random.Shared.Next(6)]));
        var barcode = $"869{Digits(10)}";
        var sku = $"SKU-{Digits(12)}";
        var variant = new ProductVariant { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProductId = product.Id, Sku = sku, SkuNormalized = sku.ToUpperInvariant(), Barcode = barcode, BarcodeNormalized = barcode, OptionSignature = "Beden=M", CreatedAt = now, UpdatedAt = now };
        var inventory = new InventoryItem { Id = Guid.CreateVersion7(), TenantId = tenant.Id, VariantId = variant.Id, LocationCode = "MAIN", OnHand = 7m, Available = 7m, ProjectionVersion = 6, Version = 1 };
        var link = new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, VariantId = variant.Id, Version = 1, ExternalId = barcode, ExternalBarcode = barcode };
        var offer = new ChannelOffer { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, VariantId = variant.Id, ListPrice = 330m, SalePrice = 300m, Currency = "TRY", VatRate = 20m, VatInclusion = "INCLUDED", RoundingMode = "HALF_EVEN", Status = "ACTIVE", PriceVersion = 1, Version = 1 };
        offer.LastPriceHash = marked ? PriceInventoryComposer.CurrentPriceHash("PAZARAMA", offer) : null;
        offer.LastStockProjectionVersion = stockChanged ? 5 : 6;
        if (confirmed is { } price) (offer.LastSentCode, offer.LastSentSalePrice, offer.LastSentListPrice, offer.LastSentQuantity) = (barcode, price, 330m, 7m);
        if (vatOnlyChange) offer.VatRate = 10m;
        offer.SalePrice = salesByVersion[^1];
        offer.PriceVersion = salesByVersion.Length;
        await using var db = fixture.CreateContext();
        for (var version = 1; version <= salesByVersion.Length; version++)
            if (version != missingHistoryVersion)
                db.ChannelPriceHistory.Add(new ChannelPriceHistory { Id = Guid.CreateVersion7(), TenantId = tenant.Id, OfferId = offer.Id, PriceVersion = version, ListPrice = 330m, SalePrice = salesByVersion[version - 1], Currency = "TRY", Reason = "test", ActorSource = "SYSTEM:TEST", EffectiveAt = now });
        db.Tenants.Add(tenant);
        db.PlatformConnections.Add(connection);
        db.Products.Add(product);
        db.ProductVariants.Add(variant);
        db.InventoryItems.Add(inventory);
        db.MarketplaceVariantLinks.Add(link);
        db.ChannelOffers.Add(offer);
        await db.SaveChangesAsync();
        return new(tenant.Id, connection.Id, offer.Id);
    }
}
