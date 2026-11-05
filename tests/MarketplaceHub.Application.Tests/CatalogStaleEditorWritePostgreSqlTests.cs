using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// A product editor opened before another change must not write its older values over the newer ones: a channel offer
// created by POST over an offer the screen did not know of (CAT-02), and a family save carrying a sibling colour
// card's row as it was loaded (CAT-03).
[Collection("postgres-integration")]
public sealed class CatalogStaleEditorWritePostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>, IAsyncLifetime
{
    private static readonly System.Collections.Concurrent.ConcurrentBag<Guid> CreatedTenants = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        var tenantIds = CreatedTenants.ToArray();
        if (tenantIds.Length == 0) return;
        await using var db = fixture.CreateContext();
        await db.JobAttempts.Where(x => tenantIds.Contains(x.TenantId)).ExecuteDeleteAsync();
        await db.IntegrationJobs.Where(x => tenantIds.Contains(x.TenantId)).ExecuteDeleteAsync();
    }

    // The editor loaded the offer at v1 (sale 120); meanwhile the price became 150 (v2). A POST with the stale 120 is
    // refused like a PATCH at v1, and the newer price stays. The same values posted again (a retried create) succeed.
    [PostgreSqlFact]
    public async Task OfferPost_OverAnExistingOfferWithOtherValues_IsRefused()
    {
        var tenant = NewTenant("cat-offer-post");
        var connection = NewConnection(tenant, "N11");
        var (product, variant) = NewVariant(tenant, $"C02-{Guid.NewGuid():N}"[..12]);
        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.Add(tenant); seed.PlatformConnections.Add(connection); seed.Products.Add(product); seed.ProductVariants.Add(variant);
            await seed.SaveChangesAsync();
        }
        await using var db = fixture.CreateContext();
        var service = new InventoryService(db, new CursorCodec(new EphemeralDataProtectionProvider(), TimeProvider.System), fixture.TimeProvider, new ConfigurationBuilder().Build());
        var user = Guid.CreateVersion7();
        var loaded = await service.UpsertOfferAsync(tenant.Id, user, new(connection.Id, variant.Id, 120m, 120m, "TRY", 20m, "INCLUDED", "HALF_EVEN", 0m, "ACTIVE", "İlk fiyat"), CancellationToken.None);
        Assert.True(loaded.Succeeded, loaded.Error?.Message);
        var newer = await service.UpdateOfferAsync(tenant.Id, user, loaded.Value!.Id, loaded.Value.Version, new(150m, 150m, "TRY", 20m, "INCLUDED", "HALF_EVEN", 0m, "ACTIVE", "Hızlı fiyat"), CancellationToken.None);
        Assert.True(newer.Succeeded, newer.Error?.Message);
        Assert.Equal(2, newer.Value!.Version);

        var stalePatch = await service.UpdateOfferAsync(tenant.Id, user, loaded.Value.Id, loaded.Value.Version, new(120m, 120m, "TRY", 20m, "INCLUDED", "HALF_EVEN", 0m, "ACTIVE", "İlk ürün fiyatı"), CancellationToken.None);
        Assert.Equal(412, stalePatch.Error?.Status);

        var stalePost = await service.UpsertOfferAsync(tenant.Id, user, new(connection.Id, variant.Id, 120m, 120m, "TRY", 20m, "INCLUDED", "HALF_EVEN", 0m, "ACTIVE", "İlk ürün fiyatı", FollowsPanelPrice: true), CancellationToken.None);
        Assert.False(stalePost.Succeeded);
        Assert.Equal(409, stalePost.Error?.Status);
        Assert.Equal("CHANNEL_OFFER_EXISTS", stalePost.Error?.Code);
        db.ChangeTracker.Clear();
        var stored = await db.ChannelOffers.AsNoTracking().SingleAsync(x => x.Id == loaded.Value.Id);
        Assert.Equal(150m, stored.SalePrice);
        Assert.Equal(2, stored.Version);

        // The offer's own values posted again: no conflict, only the "Özel Fiyat" choice is stored, at the same version.
        var samePost = await service.UpsertOfferAsync(tenant.Id, user, new(connection.Id, variant.Id, 150m, 150m, "TRY", 20m, "INCLUDED", "HALF_EVEN", 0m, "ACTIVE", "Tekrar", FollowsPanelPrice: false), CancellationToken.None);
        Assert.True(samePost.Succeeded, samePost.Error?.Message);
        db.ChangeTracker.Clear();
        stored = await db.ChannelOffers.AsNoTracking().SingleAsync(x => x.Id == loaded.Value.Id);
        Assert.Equal((150m, 2L, (bool?)false), (stored.SalePrice, stored.Version, stored.FollowsPanelPrice));
    }

    // An editor opened the Mavi card (its family carries the Bordo card's row). Meanwhile the Bordo card was saved in
    // another editor (price 100 -> 250, barcode changed). The stale Mavi editor sends Bordo's row as it loaded it, with
    // the version it loaded: the save is refused and Bordo keeps its newer values. A row sent unchanged is no conflict.
    [PostgreSqlFact]
    public async Task FamilySave_WithASiblingRowLoadedBeforeItChanged_IsRefused()
    {
        var tenant = NewTenant("cat-family-save");
        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.Add(tenant);
            await seed.SaveChangesAsync();
        }
        await using var db = fixture.CreateContext();
        var service = new CatalogService(db, null!, new ConfigurationBuilder().Build(), fixture.TimeProvider, new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()));
        var modelCode = $"AC{Guid.NewGuid():N}"[..10].ToUpperInvariant();
        static Dictionary<string, string> Options(string color, string size) => new() { ["Renk"] = color, ["Beden"] = size };
        var mavi = await service.CreateProductAsync(tenant.Id, new CreateProductCommand("Bluz Mavi", "Açıklama", null, null, [new CreateVariantCommand($"{modelCode}-M-L", $"{modelCode}M1", modelCode, Options("Mavi", "L"), DefaultListPrice: 100m, DefaultSalePrice: 100m)], VatRate: 10m, VatInclusion: "INCLUDED"), CancellationToken.None);
        Assert.True(mavi.Succeeded, mavi.Error?.Message);
        var now = DateTimeOffset.UtcNow;
        var bordoProduct = new Product { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Title = "Bluz Bordo", Description = "Açıklama", Status = ProductStatus.Active, VatRate = 10m, VatInclusion = "INCLUDED", CreatedAt = now, UpdatedAt = now };
        db.Products.Add(bordoProduct);
        db.ProductVariants.Add(new ProductVariant { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProductId = bordoProduct.Id, Sku = $"{modelCode}-B-L", SkuNormalized = $"{modelCode}-B-L", Barcode = $"{modelCode}B1", BarcodeNormalized = $"{modelCode}B1", ModelCode = modelCode, OptionSignature = "Renk: Bordo | Beden: L", DefaultListPrice = 100m, DefaultSalePrice = 100m, Status = ProductStatus.Active, CreatedAt = now, UpdatedAt = now });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var maviVariant = await db.ProductVariants.AsNoTracking().SingleAsync(x => x.ProductId == mavi.Value!.Id);
        var bordoVariant = await db.ProductVariants.AsNoTracking().SingleAsync(x => x.ProductId == bordoProduct.Id);
        var maviLoadedVersion = (await db.Products.AsNoTracking().SingleAsync(x => x.Id == mavi.Value!.Id)).Version;
        var bordoLoadedVersion = (await db.Products.AsNoTracking().SingleAsync(x => x.Id == bordoProduct.Id)).Version;
        UpdateVariantCommand Row(ProductVariant variant, string? barcode, decimal price, int sort) => new(variant.Id, variant.Sku, barcode, modelCode, sort, DefaultListPrice: price, DefaultSalePrice: price, LoadedVersion: variant.Version);

        // Another editor on the Bordo card: price 250, barcode changed.
        var bordoSave = await service.UpdateProductAsync(tenant.Id, bordoProduct.Id, bordoLoadedVersion, new UpdateProductCommand("Bluz Bordo", "Açıklama", null, null,
            VariantUpdates: [Row(bordoVariant, $"{modelCode}B2", 250m, 0), Row(maviVariant, maviVariant.Barcode, 100m, 1)]), CancellationToken.None);
        Assert.True(bordoSave.Succeeded, bordoSave.Error?.Message);
        db.ChangeTracker.Clear();
        Assert.Equal(maviLoadedVersion, (await db.Products.AsNoTracking().SingleAsync(x => x.Id == mavi.Value!.Id)).Version);

        // The stale Mavi editor saves (changes only Mavi's price) with the Bordo row as it loaded it.
        var staleMaviSave = await service.UpdateProductAsync(tenant.Id, mavi.Value!.Id, maviLoadedVersion, new UpdateProductCommand("Bluz Mavi", "Açıklama", null, null,
            VariantUpdates: [Row(maviVariant, maviVariant.Barcode, 110m, 0), Row(bordoVariant, bordoVariant.Barcode, 100m, 1)]), CancellationToken.None);
        Assert.False(staleMaviSave.Succeeded);
        Assert.Equal(409, staleMaviSave.Error?.Status);
        Assert.Equal("VARIANT_CHANGED", staleMaviSave.Error?.Code);
        db.ChangeTracker.Clear();
        var bordoNow = await db.ProductVariants.AsNoTracking().SingleAsync(x => x.Id == bordoVariant.Id);
        Assert.Equal((250m, $"{modelCode}B2"), (bordoNow.DefaultSalePrice, bordoNow.Barcode));
        Assert.Equal(100m, (await db.ProductVariants.AsNoTracking().SingleAsync(x => x.Id == maviVariant.Id)).DefaultSalePrice);

        // A Mavi editor opened after that save (the Bordo save moved Mavi's row in the colour order, a new version) whose
        // Bordo row still carries the older loaded version but already the newer values: nothing to change on Bordo, so
        // Mavi's price is saved.
        var maviReloaded = await db.ProductVariants.AsNoTracking().SingleAsync(x => x.Id == maviVariant.Id);
        var current = await service.UpdateProductAsync(tenant.Id, mavi.Value!.Id, maviLoadedVersion, new UpdateProductCommand("Bluz Mavi", "Açıklama", null, null,
            VariantUpdates: [Row(maviReloaded, maviVariant.Barcode, 110m, 1), Row(bordoVariant, $"{modelCode}B2", 250m, 0)]), CancellationToken.None);
        Assert.True(current.Succeeded, current.Error?.Message);
        db.ChangeTracker.Clear();
        Assert.Equal(110m, (await db.ProductVariants.AsNoTracking().SingleAsync(x => x.Id == maviVariant.Id)).DefaultSalePrice);
    }

    private static Tenant NewTenant(string code)
    {
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"{code}-{Guid.NewGuid():N}", DisplayName = code, Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, Version = 1 };
        CreatedTenants.Add(tenant.Id);
        return tenant;
    }

    private static PlatformConnection NewConnection(Tenant tenant, string platformCode) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenant.Id,
        PublicId = Guid.CreateVersion7(),
        PlatformCode = platformCode,
        Environment = "PRODUCTION",
        DisplayName = $"{platformCode} test",
        ExternalStoreId = $"store-{Guid.NewGuid():N}",
        Status = "ACTIVE",
        ApiVersion = "V1",
        SettingsJson = "{}"
    };

    private static (Product Product, ProductVariant Variant) NewVariant(Tenant tenant, string sku)
    {
        var now = DateTimeOffset.UtcNow;
        var product = new Product { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Title = $"Ürün {sku}", Description = "Test", Status = ProductStatus.Active, CreatedAt = now, UpdatedAt = now };
        var variant = new ProductVariant { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProductId = product.Id, Sku = sku, SkuNormalized = sku.ToUpperInvariant(), Barcode = sku + "B", BarcodeNormalized = (sku + "B").ToUpperInvariant(), ModelCode = "TEST-1", OptionSignature = sku, CreatedAt = now, UpdatedAt = now };
        return (product, variant);
    }
}
