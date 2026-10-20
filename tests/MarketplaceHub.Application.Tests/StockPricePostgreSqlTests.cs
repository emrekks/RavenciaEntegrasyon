using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Stock, price and publishing behaviour that needs the real schema (audit 2026-10-19, section 4.3).
[Collection("postgres-integration")]
public sealed class StockPricePostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    // K1: a manual adjustment reaches every connection with an ACTIVE offer for the variant.
    [PostgreSqlFact]
    public async Task ManualStockAdjustment_QueuesADispatchForEveryConnectionSellingTheVariant()
    {
        var tenant = NewTenant("stock-fan-out");
        var trendyol = NewConnection(tenant, "TRENDYOL");
        var n11 = NewConnection(tenant, "N11");
        var pazarama = NewConnection(tenant, "PAZARAMA");
        var passive = NewConnection(tenant, "HEPSIBURADA");
        passive.Status = "PASSIVE";
        var (product, variant) = NewVariant(tenant, "FAN-1", "8690000000011");
        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.Add(tenant);
            seed.PlatformConnections.AddRange(trendyol, n11, pazarama, passive);
            seed.Products.Add(product);
            seed.ProductVariants.Add(variant);
            seed.InventoryItems.Add(NewInventory(tenant, variant, 5m));
            seed.ChannelOffers.AddRange(NewOffer(tenant, trendyol, variant), NewOffer(tenant, n11, variant), NewOffer(tenant, pazarama, variant, status: "INACTIVE"), NewOffer(tenant, passive, variant));
            await seed.SaveChangesAsync();
        }

        await using var db = fixture.CreateContext();
        var service = new InventoryService(db, new CursorCodec(new EphemeralDataProtectionProvider(), TimeProvider.System), fixture.TimeProvider, new ConfigurationBuilder().Build());
        var result = await service.AdjustAsync(tenant.Id, Guid.CreateVersion7(), variant.Id, new StockAdjustmentCommand(3m, "Sayım farkı", "count-1"), $"adjust-{Guid.NewGuid():N}", "stock-fan-out", CancellationToken.None);

        Assert.True(result.Succeeded, result.Error?.Message);
        var dispatchConnections = await db.IntegrationJobs.AsNoTracking()
            .Where(x => x.TenantId == tenant.Id && x.JobType == MarketplaceJobTypes.StockProjectionDispatch)
            .Select(x => x.ConnectionId)
            .ToListAsync();
        Assert.Equal(new Guid?[] { trendyol.Id, n11.Id }.OrderBy(x => x), dispatchConnections.OrderBy(x => x));
    }

    // K2: a candidate whose offer cannot be sent no longer stops the reconcile scan.
    [PostgreSqlFact]
    public async Task StockReconcile_ContinuesPastACandidateWithAnInvalidOffer()
    {
        var tenant = NewTenant("stock-reconcile-continue");
        var n11 = NewConnection(tenant, "N11", writesEnabled: true);
        var (product, invalid) = NewVariant(tenant, "REC-1", "8690000000028");
        var valid = NewVariant(tenant, "REC-2", "8690000000035", product.Id).Variant;
        var now = fixture.Now;
        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.Add(tenant);
            seed.PlatformConnections.Add(n11);
            seed.Products.Add(product);
            seed.ProductVariants.AddRange(invalid, valid);
            var invalidInventory = NewInventory(tenant, invalid, 4m);
            invalidInventory.ReconciledAt = now.AddHours(-10);
            var validInventory = NewInventory(tenant, valid, 6m);
            validInventory.ReconciledAt = now.AddHours(-5);
            seed.InventoryItems.AddRange(invalidInventory, validInventory);
            seed.ChannelOffers.AddRange(NewOffer(tenant, n11, invalid, currency: "USD"), NewOffer(tenant, n11, valid));
            seed.MarketplaceVariantLinks.AddRange(
                new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = n11.Id, VariantId = invalid.Id, ExternalId = "REC-1" },
                new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = n11.Id, VariantId = valid.Id, ExternalId = "REC-2" });
            await seed.SaveChangesAsync();
        }

        await using var db = fixture.CreateContext();
        var processor = new MarketplaceJobProcessor(db, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, WritesOnConfiguration(), fixture.TimeProvider);
        var result = await processor.ProcessAsync(tenant.Id, n11.Id, MarketplaceJobTypes.StockReconciliation, "{\"lookbackHours\":1}", "stock-reconcile-continue", CancellationToken.None);

        Assert.True(result.Succeeded, result.ErrorSummary);
        var pushes = await db.IntegrationJobs.AsNoTracking().Where(x => x.TenantId == tenant.Id && x.JobType == MarketplaceJobTypes.PriceInventorySync).Select(x => x.PayloadJson).ToListAsync();
        Assert.Single(pushes);
        Assert.Contains(valid.Id.ToString(), pushes[0], StringComparison.OrdinalIgnoreCase);
        Assert.True(await db.OperationalIssues.AsNoTracking().AnyAsync(x => x.TenantId == tenant.Id && x.Code == "CHANNEL_OFFER_INVALID"));
    }

    private static IConfiguration WritesOnConfiguration() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["FeatureFlags:ExternalWrites"] = "true" }).Build();

    private static Tenant NewTenant(string code) => new()
    {
        Id = Guid.CreateVersion7(),
        Code = $"{code}-{Guid.NewGuid():N}",
        DisplayName = code,
        Status = RecordStatus.Active,
        Timezone = "Europe/Istanbul",
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        Version = 1
    };

    private static PlatformConnection NewConnection(Tenant tenant, string platformCode, bool writesEnabled = false) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenant.Id,
        PublicId = Guid.CreateVersion7(),
        PlatformCode = platformCode,
        Environment = "PRODUCTION",
        DisplayName = $"{platformCode} stok testi",
        ExternalStoreId = $"store-{Guid.NewGuid():N}",
        Status = "ACTIVE",
        ApiVersion = "V1",
        SettingsJson = writesEnabled ? "{\"ExternalWritesEnabled\":true}" : "{}"
    };

    private static (Product Product, ProductVariant Variant) NewVariant(Tenant tenant, string sku, string barcode, Guid? productId = null)
    {
        var now = DateTimeOffset.UtcNow;
        var product = new Product { Id = productId ?? Guid.CreateVersion7(), TenantId = tenant.Id, Title = $"Ürün {sku}", Description = "Stok testi", Status = ProductStatus.Active, CreatedAt = now, UpdatedAt = now };
        var variant = new ProductVariant
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            ProductId = product.Id,
            Sku = sku,
            SkuNormalized = sku.ToUpperInvariant(),
            Barcode = barcode,
            BarcodeNormalized = barcode.ToUpperInvariant(),
            ModelCode = "MODEL-1",
            OptionSignature = sku,
            CreatedAt = now,
            UpdatedAt = now
        };
        return (product, variant);
    }

    private static InventoryItem NewInventory(Tenant tenant, ProductVariant variant, decimal onHand) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenant.Id,
        VariantId = variant.Id,
        LocationCode = "MAIN",
        OnHand = onHand,
        Available = onHand
    };

    private static ChannelOffer NewOffer(Tenant tenant, PlatformConnection connection, ProductVariant variant, string status = "ACTIVE", string currency = "TRY") => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenant.Id,
        ConnectionId = connection.Id,
        VariantId = variant.Id,
        ListPrice = 150m,
        SalePrice = 120m,
        Currency = currency,
        VatRate = 20m,
        VatInclusion = "INCLUDED",
        RoundingMode = "HALF_EVEN",
        Status = status
    };
}
