using System.Text.Json;
using System.Text.Json.Nodes;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// "Platformlarda Güncelle" with only the price or only the stock picked, against PostgreSQL: the part not picked is left
// out of the request and keeps its mark, so a later send still carries it.
[Collection("postgres-integration")]
public sealed class PlatformUpdateFieldSelectionPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>, IAsyncLifetime
{
    private static readonly System.Collections.Concurrent.ConcurrentBag<Guid> CreatedTenants = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        var tenantIds = CreatedTenants.ToArray();
        if (tenantIds.Length == 0) return;
        await using var db = fixture.CreateContext();
        await db.IntegrationJobs.Where(x => tenantIds.Contains(x.TenantId)).ExecuteDeleteAsync();
    }

    [PostgreSqlFact]
    public async Task PriceOnly_LeavesTheStockOutAndUnsettled()
    {
        var (tenant, connection, product, offer) = await SeedTrendyolOfferAsync("price-only");

        await using (var db = fixture.CreateContext())
        {
            var priceOnly = await new PriceInventoryComposer(db).BuildAsync(tenant.Id, connection.Id, CancellationToken.None, productId: product.Id, holdStock: true);
            Assert.True(priceOnly.Succeeded, priceOnly.Error?.Message);
            var line = Assert.Single(priceOnly.Value!.Lines);
            Assert.True(line.QuantityOmitted);
            Assert.True(line.StockHeld);
            Assert.False(line.PriceOmitted);
            var item = JsonNode.Parse(priceOnly.Value.PayloadJson)!["items"]![0]!.AsObject();
            Assert.False(item.ContainsKey("quantity"));
            Assert.Equal(120m, item["salePrice"]!.GetValue<decimal>());

            var stockOnly = await new PriceInventoryComposer(db).BuildAsync(tenant.Id, connection.Id, CancellationToken.None, productId: product.Id, holdPrice: true);
            var stockLine = Assert.Single(stockOnly.Value!.Lines);
            Assert.True(stockLine.PriceHeld);
            Assert.False(stockLine.QuantityOmitted);
            Assert.False(JsonNode.Parse(stockOnly.Value.PayloadJson)!["items"]![0]!.AsObject().ContainsKey("salePrice"));

            Assert.Equal("PRICE_INVENTORY_FIELDS_REQUIRED", (await new PriceInventoryComposer(db).BuildAsync(tenant.Id, connection.Id, CancellationToken.None, productId: product.Id, holdPrice: true, holdStock: true)).Error?.Code);
        }

        // The price was sent; only the stock still differs. A price-only send has nothing to carry and must not settle the
        // stock: the next full send still carries it.
        await using (var db = fixture.CreateContext())
        {
            var stored = await db.ChannelOffers.SingleAsync(x => x.Id == offer.Id);
            stored.LastPriceHash = PriceInventoryComposer.StandardPriceHash(stored);
            stored.LastSentCode = "869TEST0001";
            stored.LastSentSalePrice = 120m;
            stored.LastSentListPrice = 150m;
            stored.Version++;
            await db.SaveChangesAsync();
        }
        await using (var db = fixture.CreateContext())
        {
            Assert.Equal("NO_EXTERNAL_CHANGES", (await new PriceInventoryComposer(db).BuildAsync(tenant.Id, connection.Id, CancellationToken.None, productId: product.Id, holdStock: true)).Error?.Code);
            Assert.Null((await db.ChannelOffers.AsNoTracking().SingleAsync(x => x.Id == offer.Id)).LastStockProjectionVersion);
            var full = await new PriceInventoryComposer(db).BuildAsync(tenant.Id, connection.Id, CancellationToken.None, productId: product.Id);
            var line = Assert.Single(full.Value!.Lines);
            Assert.False(line.QuantityOmitted);
            Assert.True(line.PriceOmitted);
            Assert.False(line.StockHeld);
        }
    }

    [PostgreSqlFact]
    public async Task ProductSend_CarriesThePickToTheJob_AndKeepsItsOwnDedupKey()
    {
        var (tenant, connection, product, _) = await SeedTrendyolOfferAsync("pick-job");
        await using var db = fixture.CreateContext();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["FeatureFlags:ExternalWrites"] = "true" }).Build();
        var service = new InventoryService(db, new CursorCodec(new EphemeralDataProtectionProvider(), TimeProvider.System), fixture.TimeProvider, configuration);

        var priceOnly = await service.EnqueueProductPriceInventorySyncAsync(tenant.Id, product.Id, connection.Id, $"k-{Guid.NewGuid():N}", "test", CancellationToken.None, price: true, stock: false);
        Assert.True(priceOnly.Succeeded, priceOnly.Error?.Message);
        var full = await service.EnqueueProductPriceInventorySyncAsync(tenant.Id, product.Id, connection.Id, $"k-{Guid.NewGuid():N}", "test", CancellationToken.None);
        Assert.True(full.Succeeded, full.Error?.Message);
        Assert.NotEqual(priceOnly.Value, full.Value);

        var jobs = await db.IntegrationJobs.AsNoTracking().Where(x => x.TenantId == tenant.Id).ToDictionaryAsync(x => x.Id);
        var pricePayload = JsonSerializer.Deserialize<PriceInventoryJobPayload>(jobs[priceOnly.Value].PayloadJson)!;
        Assert.True(pricePayload.HoldStock);
        Assert.False(pricePayload.HoldPrice);
        Assert.EndsWith(":price", jobs[priceOnly.Value].JobDedupKey);
        var fullPayload = JsonSerializer.Deserialize<PriceInventoryJobPayload>(jobs[full.Value].PayloadJson)!;
        Assert.False(fullPayload.HoldStock || fullPayload.HoldPrice);
        Assert.False(jobs[full.Value].JobDedupKey.EndsWith(":price", StringComparison.Ordinal));

        Assert.Equal("PRICE_INVENTORY_FIELDS_REQUIRED", (await service.EnqueueProductPriceInventorySyncAsync(tenant.Id, product.Id, connection.Id, $"k-{Guid.NewGuid():N}", "test", CancellationToken.None, price: false, stock: false)).Error?.Code);
    }

    [PostgreSqlFact]
    public async Task WarrantyAlone_SendsNothingToTrendyol_NotTheWholeCard()
    {
        var (tenant, connection, product, _) = await SeedTrendyolOfferAsync("warranty-only");
        await using var db = fixture.CreateContext();
        var sections = ProductUpdateFields.Sections(["WARRANTY"]);
        Assert.True(sections.Succeeded, sections.Error?.Message);
        var build = await new ProductUpdateComposer(db, new ConfigurationBuilder().Build()).BuildAsync(tenant.Id, product.Id, connection.Id, CancellationToken.None, sections.Value);
        Assert.Equal("PRODUCT_UPDATE_FIELDS_UNSUPPORTED", build.Error?.Code);
    }

    // A Trendyol offer read in by an import (a variant link with the barcode), whose price and stock were never sent.
    private async Task<(Tenant Tenant, PlatformConnection Connection, Product Product, ChannelOffer Offer)> SeedTrendyolOfferAsync(string code)
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"{code}-{Guid.NewGuid():N}", DisplayName = code, Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        CreatedTenants.Add(tenant.Id);
        var connection = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = "TRENDYOL", Environment = "PRODUCTION", DisplayName = "Trendyol seçim testi", ExternalStoreId = $"store-{Guid.NewGuid():N}", Status = "ACTIVE", ApiVersion = "V1", SettingsJson = "{\"ExternalWritesEnabled\":true}", Version = 1 };
        var product = new Product { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Title = "Seçim ürünü", Description = "Seçim testi", Status = ProductStatus.Active, CreatedAt = now, UpdatedAt = now };
        var variant = new ProductVariant { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProductId = product.Id, Sku = "PICK-1", SkuNormalized = "PICK-1", Barcode = "869TEST0001", BarcodeNormalized = "869TEST0001", OptionSignature = "PICK-1", CreatedAt = now, UpdatedAt = now };
        var inventory = new InventoryItem { Id = Guid.CreateVersion7(), TenantId = tenant.Id, VariantId = variant.Id, LocationCode = "MAIN", OnHand = 5m, Available = 5m, ProjectionVersion = 1, Version = 1 };
        var offer = new ChannelOffer { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, VariantId = variant.Id, ListPrice = 150m, SalePrice = 120m, Currency = "TRY", VatRate = 20m, VatInclusion = "INCLUDED", RoundingMode = "HALF_EVEN", Status = "ACTIVE", PriceVersion = 1, Version = 1 };
        await using var seed = fixture.CreateContext();
        seed.Tenants.Add(tenant);
        seed.PlatformConnections.Add(connection);
        seed.Products.Add(product);
        seed.ProductVariants.Add(variant);
        seed.InventoryItems.Add(inventory);
        seed.ChannelOffers.Add(offer);
        seed.MarketplaceVariantLinks.Add(new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, VariantId = variant.Id, ExternalId = "869TEST0001", ExternalBarcode = "869TEST0001" });
        await seed.SaveChangesAsync();
        return (tenant, connection, product, offer);
    }
}
