using System.Data.Common;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// 18 Nov 2026: the panel switched the automatic price/stock pair with two parallel PUTs; both took the baseline over
// the same offers (a concurrency token), one failed with CONCURRENCY_CONFLICT and the pair stayed half open. The pair
// is now switched in one request and one transaction, and a save the offers alone made conflict is taken again.
[Collection("postgres-integration")]
public sealed class ExternalWriteGroupPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    [PostgreSqlFact]
    public async Task TheGroupSwitchesBothPoliciesOnTogether_AndTakesTheBaseline()
    {
        var (tenant, connection, offer) = await SeedAsync("group-on", priceVersion: 1, stockVersion: 1);

        await using var db = fixture.CreateContext();
        var result = await Service(db).UpdateExternalWriteGroupAsync(tenant.Id, connection.Id, Group(true, (MarketplaceExternalWritePolicies.Price, 1), (MarketplaceExternalWritePolicies.Stock, 1)), CancellationToken.None);

        Assert.True(result.Succeeded, result.Error?.Message);
        Assert.Equal([MarketplaceExternalWritePolicies.Price, MarketplaceExternalWritePolicies.Stock], result.Value!.Select(x => x.ResourceType));
        Assert.All(result.Value!, view => { Assert.True(view.Enabled); Assert.Equal(2, view.Version); });
        var policies = await PoliciesAsync(tenant, connection);
        Assert.All(policies, policy => { Assert.True(policy.Enabled); Assert.Equal(2, policy.Version); Assert.Equal(0, policy.IntervalSeconds); });
        var stored = await OfferAsync(offer);
        Assert.Equal(PriceInventoryComposer.CurrentPriceHash(connection.PlatformCode, stored), stored.LastPriceHash);
        Assert.Equal(5, stored.LastStockProjectionVersion);
    }

    // One stale version: nothing changes, neither the other policy nor the offers.
    [PostgreSqlFact]
    public async Task OneStaleVersion_ChangesNeitherPolicy()
    {
        var (tenant, connection, offer) = await SeedAsync("group-stale", priceVersion: 1, stockVersion: 2);

        await using var db = fixture.CreateContext();
        var result = await Service(db).UpdateExternalWriteGroupAsync(tenant.Id, connection.Id, Group(true, (MarketplaceExternalWritePolicies.Price, 1), (MarketplaceExternalWritePolicies.Stock, 1)), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("CONCURRENCY_CONFLICT", result.Error!.Code);
        Assert.Equal(412, result.Error.Status);
        var policies = await PoliciesAsync(tenant, connection);
        Assert.All(policies, policy => Assert.False(policy.Enabled));
        Assert.Equal(1, policies.Single(x => x.ResourceType == MarketplaceExternalWritePolicies.Price).Version);
        Assert.Equal(2, policies.Single(x => x.ResourceType == MarketplaceExternalWritePolicies.Stock).Version);
        var stored = await OfferAsync(offer);
        Assert.Null(stored.LastPriceHash);
        Assert.Equal(1, stored.Version);
    }

    [PostgreSqlFact]
    public async Task AnOperationsUser_CannotSwitchTheGroup()
    {
        var (tenant, connection, _) = await SeedAsync("group-denied", priceVersion: 1, stockVersion: 1);

        await using var db = fixture.CreateContext();
        var result = await Service(db).UpdateExternalWriteGroupAsync(tenant.Id, connection.Id, Group(true, (MarketplaceExternalWritePolicies.Price, 1), (MarketplaceExternalWritePolicies.Stock, 1)), CancellationToken.None, elevated: false);

        Assert.Equal("PERMISSION_DENIED", result.Error?.Code);
        Assert.All(await PoliciesAsync(tenant, connection), policy => { Assert.False(policy.Enabled); Assert.Equal(1, policy.Version); });
    }

    [PostgreSqlFact]
    public async Task TheGroupTakesOnlyWriteSwitches()
    {
        var (tenant, connection, _) = await SeedAsync("group-orders", priceVersion: 1, stockVersion: 1);

        await using var db = fixture.CreateContext();
        var result = await Service(db).UpdateExternalWriteGroupAsync(tenant.Id, connection.Id, Group(true, (MarketplaceExternalWritePolicies.Price, 1), ("ORDERS", 1)), CancellationToken.None);

        Assert.Equal("VALIDATION_FAILED", result.Error?.Code);
        Assert.All(await PoliciesAsync(tenant, connection), policy => Assert.False(policy.Enabled));
    }

    // What failed on live: another save changed the offers between the baseline's read and its save. The group (and the
    // single PUT an older panel still sends) takes the baseline again instead of failing.
    [PostgreSqlFact]
    public async Task AnOfferSavedMeanwhile_DoesNotFailTheGroup()
    {
        var (tenant, connection, offer) = await SeedAsync("group-offer-race", priceVersion: 1, stockVersion: 1);
        var interceptor = new ConcurrentOfferWrite(offer.Id);

        await using var db = ContextWith(interceptor);
        var result = await Service(db).UpdateExternalWriteGroupAsync(tenant.Id, connection.Id, Group(true, (MarketplaceExternalWritePolicies.Price, 1), (MarketplaceExternalWritePolicies.Stock, 1)), CancellationToken.None);

        Assert.True(interceptor.Fired);
        Assert.True(result.Succeeded, result.Error?.Message);
        Assert.All(await PoliciesAsync(tenant, connection), policy => { Assert.True(policy.Enabled); Assert.Equal(2, policy.Version); });
        var stored = await OfferAsync(offer);
        Assert.Equal(PriceInventoryComposer.CurrentPriceHash(connection.PlatformCode, stored), stored.LastPriceHash);
    }

    [PostgreSqlFact]
    public async Task AnOfferSavedMeanwhile_DoesNotFailTheSinglePolicyPut()
    {
        var (tenant, connection, offer) = await SeedAsync("single-offer-race", priceVersion: 1, stockVersion: 1);
        var interceptor = new ConcurrentOfferWrite(offer.Id);

        await using var db = ContextWith(interceptor);
        var result = await Service(db).UpsertSyncPolicyAsync(tenant.Id, connection.Id, MarketplaceExternalWritePolicies.Stock, 1, new(0, 0, 0, true), CancellationToken.None);

        Assert.True(interceptor.Fired);
        Assert.True(result.Succeeded, result.Error?.Message);
        var stock = (await PoliciesAsync(tenant, connection)).Single(x => x.ResourceType == MarketplaceExternalWritePolicies.Stock);
        Assert.True(stock.Enabled);
        Assert.Equal(2, stock.Version);
    }

    private static UpdateExternalWriteGroupCommand Group(bool enabled, params (string ResourceType, long Version)[] policies) =>
        new(enabled, policies.Select(x => new ExternalWriteGroupPolicy(x.ResourceType, x.Version)).ToList());

    private MarketplaceConnectionService Service(AppDbContext db)
    {
        var dataProtection = new EphemeralDataProtectionProvider();
        return new MarketplaceConnectionService(db, new CursorCodec(dataProtection, fixture.TimeProvider), dataProtection, fixture.TokenHasher, fixture.TimeProvider, WritesOn());
    }

    private static AppDbContext ContextWith(IInterceptor interceptor)
    {
        using var plain = new PostgreSqlTenantIsolationFixture().CreateContext();
        var connectionString = plain.Database.GetConnectionString();
        return new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString).AddInterceptors(interceptor).Options);
    }

    private async Task<List<ConnectionSyncPolicy>> PoliciesAsync(Tenant tenant, PlatformConnection connection)
    {
        await using var db = fixture.CreateContext();
        return await db.ConnectionSyncPolicies.AsNoTracking().Where(x => x.TenantId == tenant.Id && x.ConnectionId == connection.Id).OrderBy(x => x.ResourceType).ToListAsync();
    }

    private async Task<ChannelOffer> OfferAsync(ChannelOffer offer)
    {
        await using var db = fixture.CreateContext();
        return await db.ChannelOffers.AsNoTracking().SingleAsync(x => x.Id == offer.Id);
    }

    private async Task<(Tenant Tenant, PlatformConnection Connection, ChannelOffer Offer)> SeedAsync(string code, long priceVersion, long stockVersion)
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"{code}-{Guid.NewGuid():N}", DisplayName = code, Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var connection = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = "N11", Environment = "PRODUCTION", DisplayName = "N11 grup testi", ExternalStoreId = $"store-{Guid.NewGuid():N}", Status = "ACTIVE", ApiVersion = "V1", SettingsJson = "{\"ExternalWritesEnabled\":true}", Version = 1 };
        var product = new Product { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Title = $"Ürün {code}", Description = "Grup testi", Status = ProductStatus.Active, CreatedAt = now, UpdatedAt = now };
        var barcode = $"869{Guid.NewGuid().ToString("N")[..10]}";
        var variant = new ProductVariant { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProductId = product.Id, Sku = code, SkuNormalized = code.ToUpperInvariant(), Barcode = barcode, BarcodeNormalized = barcode.ToUpperInvariant(), OptionSignature = code, CreatedAt = now, UpdatedAt = now };
        var inventory = new InventoryItem { Id = Guid.CreateVersion7(), TenantId = tenant.Id, VariantId = variant.Id, LocationCode = "MAIN", OnHand = 7m, Available = 7m, ProjectionVersion = 5, Version = 1 };
        var offer = new ChannelOffer { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, VariantId = variant.Id, ListPrice = 150m, SalePrice = 120m, Currency = "TRY", VatRate = 20m, VatInclusion = "INCLUDED", RoundingMode = "HALF_EVEN", Status = "ACTIVE", Version = 1 };
        await using var seed = fixture.CreateContext();
        seed.Tenants.Add(tenant);
        seed.PlatformConnections.Add(connection);
        seed.ConnectionSyncPolicies.AddRange(
            new ConnectionSyncPolicy { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, ResourceType = MarketplaceExternalWritePolicies.Price, IntervalSeconds = 0, Enabled = false, Version = priceVersion },
            new ConnectionSyncPolicy { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, ResourceType = MarketplaceExternalWritePolicies.Stock, IntervalSeconds = 0, Enabled = false, Version = stockVersion });
        seed.Products.Add(product);
        seed.ProductVariants.Add(variant);
        seed.InventoryItems.Add(inventory);
        seed.ChannelOffers.Add(offer);
        await seed.SaveChangesAsync();
        return (tenant, connection, offer);
    }

    private static IConfiguration WritesOn() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["FeatureFlags:ExternalWrites"] = "true" }).Build();

    // Before the first save of the context, another session saves the offer (its version moves on), as the second
    // parallel PUT of the pair did on live.
    private sealed class ConcurrentOfferWrite(Guid offerId) : SaveChangesInterceptor
    {
        public bool Fired { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!Fired && eventData.Context is AppDbContext db && db.ChangeTracker.Entries<ChannelOffer>().Any(x => x.State == EntityState.Modified))
            {
                Fired = true;
                await using var other = new PostgreSqlTenantIsolationFixture().CreateContext();
                await other.ChannelOffers.Where(x => x.Id == offerId).ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Version, x => x.Version + 1), cancellationToken);
            }
            return result;
        }
    }
}
