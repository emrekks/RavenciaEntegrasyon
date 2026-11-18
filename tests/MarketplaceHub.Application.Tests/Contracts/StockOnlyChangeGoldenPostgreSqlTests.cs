using System.Text.Json.Nodes;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests.Contracts;

// Owner rule of 18 Nov 2026 ("stokla beraber fiyatı da gönderelim"): what each marketplace's request carries when only a
// variant's stock changed. The offer counts as sent at its stored price (decision B); a saved panel price 280/250 is the
// price in force. Pazarama leaves a price equal to the one it holds out (documented exception); here the panel price
// differs, so it goes. The golden pins the request body and the line, byte for byte.
[Collection("postgres-integration")]
public sealed class StockOnlyChangeGoldenPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    public static TheoryData<string> Platforms => new() { "TRENDYOL", "HEPSIBURADA", "SHOPIFY", "N11", "PAZARAMA", "PTTAVM" };

    [PostgreSqlTheory]
    [MemberData(nameof(Platforms))]
    public async Task AStockOnlyChangeCarriesThePriceInForce(string platform)
    {
        using var culture = JobGolden.InvariantCulture();
        var (tenantId, connectionId) = await SeedAsync(platform);
        await using var db = fixture.CreateContext();
        var build = await new PriceInventoryComposer(db).BuildAsync(tenantId, connectionId, CancellationToken.None, automatic: true);
        Assert.True(build.Succeeded, build.Error?.Message);
        ContractFixture.MatchesGolden($"PriceStockJobs/{platform.ToLowerInvariant()}-stock-only-change.golden.json", new
        {
            Request = JsonNode.Parse(build.Value!.PayloadJson),
            Lines = build.Value.Lines.Select(line => new { line.Barcode, line.Quantity, line.ListPrice, line.SalePrice, line.QuantityOmitted, line.PriceOmitted, line.PriceNote })
        });
    }

    private async Task<(Guid TenantId, Guid ConnectionId)> SeedAsync(string platform)
    {
        var now = DateTimeOffset.UtcNow;
        const string barcode = "8690000000017";
        const string sku = "GOLD-STOCK-M";
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"stock-only-{Guid.NewGuid():N}", DisplayName = "stock only", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var connection = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = platform, Environment = "PRODUCTION", DisplayName = platform, ExternalStoreId = "store-golden", Status = "ACTIVE", ApiVersion = "V1", SettingsJson = "{\"ExternalWritesEnabled\":true}", Version = 1 };
        var product = new Product { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Title = "Stok altın", Description = "golden", Status = ProductStatus.Active, VatRate = 20m, CreatedAt = now, UpdatedAt = now };
        var variant = new ProductVariant { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProductId = product.Id, Sku = sku, SkuNormalized = sku, Barcode = barcode, BarcodeNormalized = barcode, OptionSignature = "Beden=M", DefaultListPrice = 280m, DefaultSalePrice = 250m, CreatedAt = now, UpdatedAt = now };
        var inventory = new InventoryItem { Id = Guid.CreateVersion7(), TenantId = tenant.Id, VariantId = variant.Id, LocationCode = "MAIN", OnHand = 7m, Available = 7m, ProjectionVersion = 6, Version = 1 };
        var link = new MarketplaceVariantLink
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            ConnectionId = connection.Id,
            VariantId = variant.Id,
            Version = 1,
            ExternalId = platform switch { "HEPSIBURADA" => "HBCV0000GOLD1", "SHOPIFY" => "gid://shopify/ProductVariant/1001", "N11" => sku, _ => barcode },
            ExternalBarcode = barcode,
            ExternalSku = platform == "HEPSIBURADA" ? sku : null
        };
        var offer = new ChannelOffer { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, VariantId = variant.Id, ListPrice = 330m, SalePrice = 300m, Currency = "TRY", VatRate = 20m, VatInclusion = "INCLUDED", RoundingMode = "HALF_EVEN", Status = "ACTIVE", PriceVersion = 1, Version = 1 };
        offer.LastPriceHash = PriceInventoryComposer.CurrentPriceHash(platform, offer);
        offer.LastStockProjectionVersion = 5;
        await using var db = fixture.CreateContext();
        db.Tenants.Add(tenant);
        db.PlatformConnections.Add(connection);
        db.Products.Add(product);
        db.ProductVariants.Add(variant);
        db.InventoryItems.Add(inventory);
        db.MarketplaceVariantLinks.Add(link);
        db.ChannelOffers.Add(offer);
        await db.SaveChangesAsync();
        return (tenant.Id, connection.Id);
    }
}
