using System.Text.Json;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Live 10 Nov 2026 (RYP003T): the owner set 725 on all 21 variants of a model family shown as one product (7 colour cards,
// 3 variants each) and pressed "Platformlarda Güncelle" for Trendyol; the price/stock send went for the opened card only
// (T19-T21). A send for a product covers every card of its model family on every marketplace.
[Collection("postgres-integration")]
public sealed class ModelFamilyPriceInventorySendPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>, IAsyncLifetime
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

    [PostgreSqlTheory]
    [InlineData("TRENDYOL")]
    [InlineData("HEPSIBURADA")]
    [InlineData("N11")]
    [InlineData("PAZARAMA")]
    public async Task ProductSend_CarriesEveryColourCardOfTheFamily(string platformCode)
    {
        var seed = await SeedFamilyAsync(platformCode);
        await using var db = fixture.CreateContext();

        var build = await new PriceInventoryComposer(db).BuildAsync(seed.Tenant.Id, seed.Connection.Id, CancellationToken.None, productId: seed.Opened);

        Assert.True(build.Succeeded, build.Error?.Message);
        var sent = build.Value!.Lines.Select(line => line.VariantId).ToHashSet();
        Assert.Equal(seed.FamilyVariants.ToHashSet(), sent);
        Assert.DoesNotContain(seed.DraftVariant, sent);
        Assert.DoesNotContain(seed.OtherModelVariant, sent);
    }

    [PostgreSqlFact]
    public async Task TrendyolProductSend_QueuesOneJobWithTheWholeFamily()
    {
        var seed = await SeedFamilyAsync("TRENDYOL");
        await using var db = fixture.CreateContext();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["FeatureFlags:ExternalWrites"] = "true" }).Build();
        var service = new InventoryService(db, new CursorCodec(new EphemeralDataProtectionProvider(), TimeProvider.System), fixture.TimeProvider, configuration);

        var send = await service.EnqueueProductPriceInventorySyncAsync(seed.Tenant.Id, seed.Opened, seed.Connection.Id, $"k-{Guid.NewGuid():N}", "test", CancellationToken.None);

        Assert.True(send.Succeeded, send.Error?.Message);
        var job = await db.IntegrationJobs.AsNoTracking().SingleAsync(x => x.Id == send.Value);
        var payload = JsonSerializer.Deserialize<PriceInventoryJobPayload>(job.PayloadJson)!;
        Assert.Equal(seed.FamilyVariants.Count, payload.Lines.Count);
        Assert.All(payload.Lines, line => Assert.Equal(725m, line.SalePrice));
        // The worker builds the request again at SUBMIT from the payload's product: the family must come back whole.
        var again = await new PriceInventoryComposer(db).BuildAsync(seed.Tenant.Id, seed.Connection.Id, CancellationToken.None, payload.VariantId, payload.ProductId, payload.Automatic, payload.HoldPrice, payload.HoldStock);
        Assert.Equal(seed.FamilyVariants.Count, again.Value!.Lines.Count);
    }

    // The RYP003T shape: the opened card's variants are still APPROVAL_PENDING, another card is LIVE through a listing
    // profile an import made, a third is UNKNOWN; every card has its variant links (barcode = SKU) and stock. A draft
    // card of the same model code and a card of another model code are not part of the send.
    private async Task<FamilySeed> SeedFamilyAsync(string platformCode)
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"family-{Guid.NewGuid():N}", DisplayName = "family", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        CreatedTenants.Add(tenant.Id);
        var connection = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = platformCode, Environment = "PRODUCTION", DisplayName = $"{platformCode} aile testi", ExternalStoreId = $"store-{Guid.NewGuid():N}", Status = "ACTIVE", ApiVersion = "V1", SettingsJson = "{\"ExternalWritesEnabled\":true}", Version = 1 };
        await using var db = fixture.CreateContext();
        db.Tenants.Add(tenant);
        db.PlatformConnections.Add(connection);
        var familyVariants = new List<Guid>();
        var number = 0;

        Guid Card(string state, string modelCode, ProductStatus status, bool importedProfile, List<Guid>? collect)
        {
            var product = new Product { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Title = "Kadın Kazak", Description = "Triko", Status = status, CreatedAt = now, UpdatedAt = now };
            db.Products.Add(product);
            var profile = importedProfile ? new ChannelListingProfile { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, ProductId = product.Id, Enabled = true, DesiredStatus = "LIVE", ActualStatus = "LIVE", Source = ChannelListingProfileSource.MarketplaceImport } : null;
            if (profile is not null) db.ChannelListingProfiles.Add(profile);
            for (var size = 0; size < 3; size++)
            {
                var sku = $"{modelCode}{++number:00}";
                var variant = new ProductVariant { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProductId = product.Id, Sku = sku, SkuNormalized = sku, Barcode = sku, BarcodeNormalized = sku, ModelCode = modelCode, OptionSignature = sku, SortOrder = size, CreatedAt = now, UpdatedAt = now };
                db.ProductVariants.Add(variant);
                db.InventoryItems.Add(new InventoryItem { Id = Guid.CreateVersion7(), TenantId = tenant.Id, VariantId = variant.Id, LocationCode = "MAIN", OnHand = 2m, Available = 2m, ProjectionVersion = 1, Version = 1 });
                // The imported price was 625; the editor wrote 725 on every offer (a new price version), nothing was sent yet.
                db.ChannelOffers.Add(new ChannelOffer { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, VariantId = variant.Id, ListPrice = 725m, SalePrice = 725m, Currency = "TRY", VatRate = 10m, VatInclusion = "INCLUDED", RoundingMode = "HALF_EVEN", Status = "ACTIVE", PriceVersion = 2, LastPriceHash = PriceInventoryComposer.StandardPriceHash(625m, 625m, 1), FollowsPanelPrice = true, Version = 2 });
                var externalId = platformCode == "HEPSIBURADA" ? $"HBV{number:0000000}" : sku;
                db.MarketplaceVariantLinks.Add(new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, VariantId = variant.Id, ExternalId = externalId, ExternalBarcode = sku, ExternalSku = sku });
                db.MarketplaceListingStates.Add(new MarketplaceListingState { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, VariantId = variant.Id, DesiredStatus = "LIVE", ActualStatus = state });
                if (profile is not null)
                    db.ChannelListingVariants.Add(new ChannelListingVariant { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProfileId = profile.Id, VariantId = variant.Id, ExternalBarcode = sku, ExternalSku = sku, DesiredStatus = "LIVE", ActualStatus = state });
                collect?.Add(variant.Id);
            }
            return product.Id;
        }

        var opened = Card("APPROVAL_PENDING", "RYP003T", ProductStatus.Active, importedProfile: true, familyVariants);
        Card("LIVE", "RYP003T", ProductStatus.Active, importedProfile: true, familyVariants);
        Card("LIVE", "ryp003t", ProductStatus.Active, importedProfile: true, familyVariants);
        Card("UNKNOWN", "RYP003T", ProductStatus.Active, importedProfile: true, familyVariants);
        var drafts = new List<Guid>();
        Card("LIVE", "RYP003T", ProductStatus.Draft, importedProfile: false, drafts);
        var others = new List<Guid>();
        Card("LIVE", "RYP004T", ProductStatus.Active, importedProfile: true, others);
        await db.SaveChangesAsync();
        return new(tenant, connection, opened, familyVariants, drafts[0], others[0]);
    }

    private sealed record FamilySeed(Tenant Tenant, PlatformConnection Connection, Guid Opened, List<Guid> FamilyVariants, Guid DraftVariant, Guid OtherModelVariant);
}
