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

    private sealed record Seed(Guid TenantId, Guid ConnectionId, Guid OfferId);

    private async Task<(PriceInventoryPushLine Line, string Payload)> SingleLineAsync(Seed seed)
    {
        await using var db = fixture.CreateContext();
        var build = await new PriceInventoryComposer(db).BuildAsync(seed.TenantId, seed.ConnectionId, CancellationToken.None, automatic: true);
        Assert.True(build.Succeeded, build.Error?.Message);
        return (Assert.Single(build.Value!.Lines), build.Value.PayloadJson);
    }

    // A Pazarama offer read at 330 / 300 TL with VAT 20 % included, marked at price version 1. Then either its VAT rate alone
    // was saved (10 %, version 2, the same outbound price) or its price (320 TL, version 2).
    private async Task<Seed> SeedAsync(bool vatOnlyChange, bool stockChanged, decimal? confirmed = null, bool marked = true)
    {
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
        else offer.SalePrice = 320m;
        offer.PriceVersion = 2;
        await using var db = fixture.CreateContext();
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
