using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// A product matched to a marketplace listing (variant links from a product sync) gets the listing profile the panel's
// updates need, without a value of its own (owner decision 6 Nov 2026: the card wins), against PostgreSQL.
[Collection("postgres-integration")]
public sealed class ImportedListingProfilePostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>, IAsyncLifetime
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
    public async Task MatchedProduct_GetsAnEnabledImportedProfile_WithoutValues_AndListingsFromItsLinks_Idempotently()
    {
        var seeded = await SeedAsync("imported-profile", "TRENDYOL", linkId: "1489791218", linkBarcode: "RYP003T20", linkSku: "RYP003T20");
        await using (var db = fixture.CreateContext())
        {
            var productIds = await ImportedListingProfiles.ProductIdsForCodesAsync(db, seeded.Tenant.Id, seeded.Connection.Id, ["1489791218"], CancellationToken.None);
            Assert.Equal([seeded.Product.Id], productIds);
            Assert.Equal(1, await ImportedListingProfiles.EnsureAsync(db, seeded.Tenant.Id, seeded.Connection.Id, productIds, CancellationToken.None));
        }
        await using (var db = fixture.CreateContext())
        {
            var profile = await db.ChannelListingProfiles.AsNoTracking().SingleAsync(x => x.TenantId == seeded.Tenant.Id && x.ProductId == seeded.Product.Id);
            Assert.Equal(ChannelListingProfileSource.MarketplaceImport, profile.Source);
            Assert.True(profile.Enabled);
            Assert.Equal(("LIVE", "UNKNOWN"), (profile.DesiredStatus, profile.ActualStatus));
            // The card wins: no marketplace value in any override, no category or brand id.
            Assert.All(new object?[] { profile.TitleOverride, profile.DescriptionOverride, profile.ExternalCategoryId, profile.ExternalBrandId, profile.DeliveryTimeDays, profile.Origin, profile.Warranty, profile.VatOverride, profile.CargoProfile }, Assert.Null);
            var listing = await db.ChannelListingVariants.AsNoTracking().SingleAsync(x => x.TenantId == seeded.Tenant.Id && x.ProfileId == profile.Id);
            Assert.Equal((seeded.Variant.Id, "RYP003T20", "RYP003T20", "UNKNOWN"), (listing.VariantId, listing.ExternalBarcode, listing.ExternalSku, listing.ActualStatus));
            // Nothing is assumed live: no listing state is written.
            Assert.False(await db.MarketplaceListingStates.AnyAsync(x => x.TenantId == seeded.Tenant.Id));
        }
        await using (var db = fixture.CreateContext())
        {
            Assert.Equal(0, await ImportedListingProfiles.EnsureAsync(db, seeded.Tenant.Id, seeded.Connection.Id, [seeded.Product.Id], CancellationToken.None));
            Assert.Equal(1, await db.ChannelListingProfiles.CountAsync(x => x.TenantId == seeded.Tenant.Id));
            Assert.Equal(1, await db.ChannelListingVariants.CountAsync(x => x.TenantId == seeded.Tenant.Id));
        }

        // A variant linked later joins its imported profile on the next run.
        var now = DateTimeOffset.UtcNow;
        var second = new ProductVariant { Id = Guid.CreateVersion7(), TenantId = seeded.Tenant.Id, ProductId = seeded.Product.Id, Sku = "RYP003T21", SkuNormalized = "RYP003T21", Barcode = "RYP003T21", BarcodeNormalized = "RYP003T21", OptionSignature = "RYP003T21", CreatedAt = now, UpdatedAt = now };
        await using (var db = fixture.CreateContext())
        {
            db.ProductVariants.Add(second);
            db.MarketplaceVariantLinks.Add(new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = seeded.Tenant.Id, ConnectionId = seeded.Connection.Id, VariantId = second.Id, ExternalId = "1489786236", ExternalBarcode = "RYP003T21", ExternalSku = "RYP003T21" });
            await db.SaveChangesAsync();
        }
        await using (var db = fixture.CreateContext())
        {
            Assert.Equal(0, await ImportedListingProfiles.EnsureAsync(db, seeded.Tenant.Id, seeded.Connection.Id, [seeded.Product.Id], CancellationToken.None));
            Assert.Equal(2, await db.ChannelListingVariants.CountAsync(x => x.TenantId == seeded.Tenant.Id));
        }
    }

    [PostgreSqlFact]
    public async Task PanelProfile_IsLeftAsThePanelMadeIt()
    {
        var seeded = await SeedAsync("panel-profile", "N11", linkId: "RYP003T20", linkBarcode: null, linkSku: "RYP003T20");
        await using (var db = fixture.CreateContext())
        {
            db.ChannelListingProfiles.Add(new ChannelListingProfile { Id = Guid.CreateVersion7(), TenantId = seeded.Tenant.Id, ConnectionId = seeded.Connection.Id, ProductId = seeded.Product.Id, Enabled = true, DesiredStatus = "DRAFT", ActualStatus = "UNKNOWN", DeliveryTimeDays = 5 });
            await db.SaveChangesAsync();
        }
        await using (var db = fixture.CreateContext())
            Assert.Equal(0, await ImportedListingProfiles.EnsureAsync(db, seeded.Tenant.Id, seeded.Connection.Id, [seeded.Product.Id], CancellationToken.None));
        await using (var db = fixture.CreateContext())
        {
            var profile = await db.ChannelListingProfiles.AsNoTracking().SingleAsync(x => x.TenantId == seeded.Tenant.Id);
            Assert.Null(profile.Source);
            Assert.Equal(5, profile.DeliveryTimeDays);
            Assert.False(await db.ChannelListingVariants.AnyAsync(x => x.TenantId == seeded.Tenant.Id));
        }
    }

    [PostgreSqlFact]
    public async Task Shopify_AndUnlinkedProducts_GetNoProfile()
    {
        var shopify = await SeedAsync("shopify-profile", "SHOPIFY", linkId: "gid-1", linkBarcode: "869X", linkSku: "SKU-X");
        await using (var db = fixture.CreateContext())
            Assert.Equal(0, await ImportedListingProfiles.EnsureAsync(db, shopify.Tenant.Id, shopify.Connection.Id, [shopify.Product.Id], CancellationToken.None));
        var unlinked = await SeedAsync("unlinked-profile", "TRENDYOL", linkId: null, linkBarcode: null, linkSku: null);
        await using (var db = fixture.CreateContext())
        {
            Assert.Equal(0, await ImportedListingProfiles.EnsureAsync(db, unlinked.Tenant.Id, unlinked.Connection.Id, [unlinked.Product.Id], CancellationToken.None));
            Assert.False(await db.ChannelListingProfiles.AnyAsync(x => x.TenantId == shopify.Tenant.Id || x.TenantId == unlinked.Tenant.Id));
        }
    }

    [PostgreSqlFact]
    public async Task ImportedVariant_KeepsGoingByItsLink_ForPriceAndStock()
    {
        var seeded = await SeedAsync("imported-price", "TRENDYOL", linkId: "1489791218", linkBarcode: "RYP003T20", linkSku: "RYP003T20");
        string before;
        await using (var db = fixture.CreateContext())
        {
            var built = await new PriceInventoryComposer(db).BuildAsync(seeded.Tenant.Id, seeded.Connection.Id, CancellationToken.None, productId: seeded.Product.Id);
            Assert.True(built.Succeeded, built.Error?.Message);
            before = built.Value!.PayloadJson;
            await ImportedListingProfiles.EnsureAsync(db, seeded.Tenant.Id, seeded.Connection.Id, [seeded.Product.Id], CancellationToken.None);
        }
        await using (var db = fixture.CreateContext())
        {
            var built = await new PriceInventoryComposer(db).BuildAsync(seeded.Tenant.Id, seeded.Connection.Id, CancellationToken.None, productId: seeded.Product.Id);
            Assert.True(built.Succeeded, built.Error?.Message);
            Assert.Equal(before, built.Value!.PayloadJson);
            Assert.Single(built.Value.Lines);
        }
    }

    [PostgreSqlFact]
    public async Task Update_NeverMakesAMissingProfile_AndSaysWhichSyncToRun()
    {
        // A matched product without a profile stays without one: only a sync that reads the products makes it.
        var seeded = await SeedAsync("update-profile", "TRENDYOL", linkId: "1489791218", linkBarcode: "RYP003T20", linkSku: "RYP003T20");
        await using (var db = fixture.CreateContext())
        {
            var result = await NewCatalogService(db).EnqueueProductUpdateWithNoticesAsync(seeded.Tenant.Id, seeded.Product.Id, seeded.Connection.Id, $"k-{Guid.NewGuid():N}", "test", CancellationToken.None, ["TITLE"]);
            Assert.Equal("LISTING_PROFILE_REQUIRED", result.Error?.Code);
            Assert.Contains("Trendyol yayın kaydı yok", result.Error!.Message);
            Assert.Contains("Mevcut Ürünleri Güncelle", result.Error.Message);
            Assert.DoesNotContain("listing profile", result.Error.Message);
        }
        await using (var db = fixture.CreateContext())
            Assert.False(await db.ChannelListingProfiles.AnyAsync(x => x.TenantId == seeded.Tenant.Id));

        // With the profile the sync made, the update goes on to the card's own checks.
        await using (var db = fixture.CreateContext())
            await ImportedListingProfiles.EnsureAsync(db, seeded.Tenant.Id, seeded.Connection.Id, [seeded.Product.Id], CancellationToken.None);
        await using (var db = fixture.CreateContext())
        {
            var result = await NewCatalogService(db).EnqueueProductUpdateWithNoticesAsync(seeded.Tenant.Id, seeded.Product.Id, seeded.Connection.Id, $"k-{Guid.NewGuid():N}", "test", CancellationToken.None, ["TITLE"]);
            Assert.Equal("CATEGORY_MAPPING_REQUIRED", result.Error?.Code);
        }
    }

    [PostgreSqlFact]
    public async Task Update_OnAnImportedProfile_StopsWhenTheCardLacksTheDeliveryTime_NamingProductMarketplaceAndField()
    {
        var seeded = await SeedAsync("update-empty-delivery", "TRENDYOL", linkId: "1489791218", linkBarcode: "RYP003T20", linkSku: "RYP003T20");
        await using (var db = fixture.CreateContext())
        {
            await ImportedListingProfiles.EnsureAsync(db, seeded.Tenant.Id, seeded.Connection.Id, [seeded.Product.Id], CancellationToken.None);
            await db.Products.Where(x => x.Id == seeded.Product.Id).ExecuteUpdateAsync(set => set.SetProperty(x => x.DeliveryDays, (int?)null));
        }
        await using (var db = fixture.CreateContext())
        {
            var result = await NewCatalogService(db).EnqueueProductUpdateWithNoticesAsync(seeded.Tenant.Id, seeded.Product.Id, seeded.Connection.Id, $"k-{Guid.NewGuid():N}", "test", CancellationToken.None, ["DELIVERY"]);
            Assert.Equal((ImportedProfileCardFallbacks.EmptyCardFieldCode, 422), (result.Error?.Code, result.Error?.Status));
            Assert.Equal("RYP003T – Trendyol: Teslimat Süresi boş; ürün kartında doldurun.", result.Error!.Message);
            Assert.True(result.Error.FieldErrors!.ContainsKey("deliveryDays"));
            Assert.False(await db.IntegrationJobs.AnyAsync(x => x.TenantId == seeded.Tenant.Id));
        }
    }

    [PostgreSqlFact]
    public async Task N11Update_RunsOnTheImportedProfile_WithTheSamePayloadAsAPanelProfile()
    {
        var imported = await SeedAsync("n11-imported", "N11", linkId: "RYP003T20", linkBarcode: null, linkSku: "RYP003T20");
        await using (var db = fixture.CreateContext())
            await ImportedListingProfiles.EnsureAsync(db, imported.Tenant.Id, imported.Connection.Id, [imported.Product.Id], CancellationToken.None);
        string importedPayload;
        await using (var db = fixture.CreateContext())
        {
            var build = await new N11ProductUpdateComposer(db, new ConfigurationBuilder().Build()).BuildAsync(imported.Tenant.Id, imported.Product.Id, imported.Connection.Id, ["DESCRIPTION"], CancellationToken.None);
            Assert.True(build.Succeeded, build.Error?.Message);
            importedPayload = build.Value!.Publication.ApprovedContentPayloadJson;
        }

        var panel = await SeedAsync("n11-panel", "N11", linkId: "RYP003T20", linkBarcode: null, linkSku: "RYP003T20");
        await using (var db = fixture.CreateContext())
        {
            var profile = new ChannelListingProfile { Id = Guid.CreateVersion7(), TenantId = panel.Tenant.Id, ConnectionId = panel.Connection.Id, ProductId = panel.Product.Id, Enabled = true, DesiredStatus = "LIVE", ActualStatus = "LIVE" };
            db.ChannelListingProfiles.Add(profile);
            db.ChannelListingVariants.Add(new ChannelListingVariant { Id = Guid.CreateVersion7(), TenantId = panel.Tenant.Id, ProfileId = profile.Id, VariantId = panel.Variant.Id, ExternalSku = "RYP003T20", DesiredStatus = "LIVE", ActualStatus = "LIVE" });
            await db.SaveChangesAsync();
        }
        await using (var db = fixture.CreateContext())
        {
            var build = await new N11ProductUpdateComposer(db, new ConfigurationBuilder().Build()).BuildAsync(panel.Tenant.Id, panel.Product.Id, panel.Connection.Id, ["DESCRIPTION"], CancellationToken.None);
            Assert.True(build.Succeeded, build.Error?.Message);
            Assert.Equal(importedPayload, build.Value!.Publication.ApprovedContentPayloadJson);
        }
    }

    private CatalogService NewCatalogService(AppDbContext db) =>
        new(db, new CursorCodec(new EphemeralDataProtectionProvider(), TimeProvider.System),
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["FeatureFlags:ExternalWrites"] = "true" }).Build(),
            fixture.TimeProvider, new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()));

    // One card with one variant, its offer and stock, and (when linkId is given) the variant link a product sync wrote.
    private async Task<(Tenant Tenant, PlatformConnection Connection, Product Product, ProductVariant Variant)> SeedAsync(string code, string platformCode, string? linkId, string? linkBarcode, string? linkSku)
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"{code}-{Guid.NewGuid():N}", DisplayName = code, Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        CreatedTenants.Add(tenant.Id);
        var connection = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = platformCode, Environment = "PRODUCTION", DisplayName = $"{platformCode} profil testi", ExternalStoreId = $"store-{Guid.NewGuid():N}", Status = "ACTIVE", ApiVersion = "V1", SettingsJson = "{\"ExternalWritesEnabled\":true}", Version = 1 };
        var product = new Product { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Title = "Kadın Haki Kazak", Description = "Triko kazak", Status = ProductStatus.Active, DeliveryDays = 3, CreatedAt = now, UpdatedAt = now };
        var variant = new ProductVariant { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProductId = product.Id, Sku = "RYP003T20", SkuNormalized = "RYP003T20", Barcode = "RYP003T20", BarcodeNormalized = "RYP003T20", ModelCode = "RYP003T", OptionSignature = "RYP003T20", CreatedAt = now, UpdatedAt = now };
        await using var seed = fixture.CreateContext();
        seed.Tenants.Add(tenant);
        seed.PlatformConnections.Add(connection);
        seed.Products.Add(product);
        seed.ProductVariants.Add(variant);
        seed.InventoryItems.Add(new InventoryItem { Id = Guid.CreateVersion7(), TenantId = tenant.Id, VariantId = variant.Id, LocationCode = "MAIN", OnHand = 5m, Available = 5m, ProjectionVersion = 1, Version = 1 });
        seed.ChannelOffers.Add(new ChannelOffer { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, VariantId = variant.Id, ListPrice = 150m, SalePrice = 120m, Currency = "TRY", VatRate = 20m, VatInclusion = "INCLUDED", RoundingMode = "HALF_EVEN", Status = "ACTIVE", PriceVersion = 1, Version = 1 });
        if (linkId is not null)
            seed.MarketplaceVariantLinks.Add(new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, VariantId = variant.Id, ExternalId = linkId, ExternalBarcode = linkBarcode, ExternalSku = linkSku });
        await seed.SaveChangesAsync();
        return (tenant, connection, product, variant);
    }
}
