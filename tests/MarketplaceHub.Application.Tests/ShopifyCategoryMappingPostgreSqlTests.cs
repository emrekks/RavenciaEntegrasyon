using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Owner decision 14 Nov 2026: a Shopify publication follows the category mapping like every other marketplace. Without a
// mapping of the panel category to one of the store's product types it is refused (and nothing is mapped on the way);
// with one, the mapped product type is what goes out, not the panel category name. On Eşleştirmeler a type the store
// does not hold yet can be typed, and "Birebir Eşle" maps unmapped categories to the store types of the same name.
// Product import still maps categories by itself (CatalogStructureImportTests).
[Collection("postgres-integration")]
public sealed class ShopifyCategoryMappingPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    [PostgreSqlFact]
    public async Task Publication_IsRefusedWithoutAMapping_AndSendsTheMappedProductType()
    {
        var seed = await SeedAsync();
        await using var db = seed.Db;
        var composer = new ShopifyProductPublicationComposer(db, new ConfigurationBuilder().Build());

        var refused = await composer.BuildAsync(seed.Tenant.Id, seed.Product.Id, seed.Connection.Id, CancellationToken.None);
        Assert.False(refused.Succeeded);
        Assert.Equal("CATEGORY_MAPPING_REQUIRED", refused.Error?.Code);
        Assert.Contains("Eşleştirmeler", refused.Error!.Message, StringComparison.Ordinal);
        Assert.Equal(0, await db.CategoryMappings.CountAsync(x => x.TenantId == seed.Tenant.Id));

        // The panel category is Bluz; the store's type it is mapped to is "Kadın Bluz" and that is what is sent.
        db.CategoryMappings.Add(new CategoryMapping { Id = Guid.CreateVersion7(), TenantId = seed.Tenant.Id, ConnectionId = seed.Connection.Id, SnapshotId = seed.Categories.Id, LocalId = seed.CategoryId, ExternalId = "Kadın Bluz", Status = "VERIFIED", VerifiedAt = fixture.Now, Version = 1 });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var built = await composer.BuildAsync(seed.Tenant.Id, seed.Product.Id, seed.Connection.Id, CancellationToken.None);
        Assert.True(built.Succeeded, built.Error?.Message);
        using var payload = JsonDocument.Parse(built.Value!.PayloadJson);
        Assert.Equal("Kadın Bluz", payload.RootElement.GetProperty("productType").GetString());
        await seed.Transaction.RollbackAsync();
    }

    [PostgreSqlFact]
    public async Task ATypedProductType_JoinsTheList_AndAKnownOneKeepsTheStoreSpelling()
    {
        var seed = await SeedAsync();
        await using var db = seed.Db;
        var service = new ReferenceDataService(db, fixture.TimeProvider);

        var typed = await service.UpsertMappingAsync(seed.Tenant.Id, "categories", seed.CategoryId, null, new(seed.Connection.Id, seed.Categories.Id, "  Yaz Bluzu ", "VERIFIED"), CancellationToken.None);
        Assert.True(typed.Succeeded, typed.Error?.Message);
        Assert.Equal("Yaz Bluzu", typed.Value!.ExternalId);
        var entry = await db.ReferenceItems.AsNoTracking().SingleAsync(x => x.SnapshotId == seed.Categories.Id && x.ExternalId == "Yaz Bluzu");
        Assert.True(entry.IsActive && entry.IsLeaf && entry.ParentExternalId is null);
        Assert.Equal("Yaz Bluzu", await ShopifyProductPublicationComposer.MappedProductTypeAsync(db, seed.Tenant.Id, seed.Connection.Id, seed.CategoryId, CancellationToken.None));

        // "kadın bluz" is a type the store already holds: the mapping takes the store's spelling, no new entry.
        db.ChangeTracker.Clear();
        var known = await service.UpsertMappingAsync(seed.Tenant.Id, "categories", seed.OtherCategoryId, null, new(seed.Connection.Id, seed.Categories.Id, "kadın bluz", "VERIFIED"), CancellationToken.None);
        Assert.True(known.Succeeded, known.Error?.Message);
        Assert.Equal("Kadın Bluz", known.Value!.ExternalId);
        Assert.Equal(3, await db.ReferenceItems.CountAsync(x => x.SnapshotId == seed.Categories.Id));

        var empty = await service.UpsertMappingAsync(seed.Tenant.Id, "categories", seed.OtherCategoryId, known.Value.Version, new(seed.Connection.Id, seed.Categories.Id, "   ", "VERIFIED"), CancellationToken.None);
        Assert.Equal("SHOPIFY_PRODUCT_TYPE_INVALID", empty.Error?.Code);
        await seed.Transaction.RollbackAsync();
    }

    [PostgreSqlFact]
    public async Task ExactMatch_MapsSameNamedCategoriesOnly_AndNeverChangesAMapping()
    {
        var seed = await SeedAsync();
        await using var db = seed.Db;
        var service = new ReferenceDataService(db, fixture.TimeProvider);
        var elbise = Guid.CreateVersion7();
        var etek = Guid.CreateVersion7();
        db.Categories.AddRange(Category(seed.Tenant.Id, elbise, "elbise"), Category(seed.Tenant.Id, etek, "Etek"));
        db.ReferenceItems.Add(Item(seed.Categories, "Elbise"));
        // Bluz is already mapped (to another type): it stays as it is.
        db.CategoryMappings.Add(new CategoryMapping { Id = Guid.CreateVersion7(), TenantId = seed.Tenant.Id, ConnectionId = seed.Connection.Id, SnapshotId = seed.Categories.Id, LocalId = seed.CategoryId, ExternalId = "Kadın Bluz", Status = "VERIFIED", VerifiedAt = fixture.Now, Version = 1 });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var preview = await service.ExactMatchShopifyCategoriesAsync(seed.Tenant.Id, seed.Connection.Id, false, CancellationToken.None);
        Assert.True(preview.Succeeded, preview.Error?.Message);
        // elbise ↔ Elbise; Etek and Anne Bluz have no type of that name.
        Assert.Equal((1, 2, 0, false), (preview.Value!.MatchCount, preview.Value.WithoutTypeCount, preview.Value.AmbiguousCount, preview.Value.Applied));
        Assert.Equal(1, await db.CategoryMappings.CountAsync(x => x.TenantId == seed.Tenant.Id));

        var applied = await service.ExactMatchShopifyCategoriesAsync(seed.Tenant.Id, seed.Connection.Id, true, CancellationToken.None);
        Assert.True(applied.Succeeded && applied.Value!.Applied, applied.Error?.Message);
        var mappings = await db.CategoryMappings.AsNoTracking().Where(x => x.TenantId == seed.Tenant.Id).ToDictionaryAsync(x => x.LocalId, x => x.ExternalId);
        Assert.Equal(new Dictionary<Guid, string> { [seed.CategoryId] = "Kadın Bluz", [elbise] = "Elbise" }, mappings);
        Assert.Equal(3, await db.ReferenceItems.CountAsync(x => x.SnapshotId == seed.Categories.Id));

        Assert.Equal(0, (await service.ExactMatchShopifyCategoriesAsync(seed.Tenant.Id, seed.Connection.Id, true, CancellationToken.None)).Value!.MatchCount);
        await seed.Transaction.RollbackAsync();
    }

    private sealed record Seed(AppDbContext Db, Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction Transaction, Tenant Tenant, PlatformConnection Connection, Product Product, Guid CategoryId, Guid OtherCategoryId, ReferenceSnapshot Categories);

    private async Task<Seed> SeedAsync()
    {
        var now = fixture.Now;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"shopify-type-{Guid.NewGuid():N}", DisplayName = "shopify-type", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var connection = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = "SHOPIFY", Environment = "PRODUCTION", DisplayName = "Shopify", ExternalStoreId = $"store-{Guid.NewGuid():N}", Status = "ACTIVE", ApiVersion = "2026-10", SettingsJson = "{}", Version = 1 };
        var categoryId = Guid.CreateVersion7();
        var otherCategoryId = Guid.CreateVersion7();
        var product = new Product { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Title = "Kadın Triko Bluz", Description = "Triko bluz", CategoryId = categoryId, VatRate = 10m, VatInclusion = "INCLUDED", DefaultListPrice = 900m, DefaultSalePrice = 630m, Status = ProductStatus.Active, CreatedAt = now, UpdatedAt = now, Version = 1 };
        var variant = new ProductVariant { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProductId = product.Id, Sku = "TB-1", SkuNormalized = "TB-1", Barcode = "TB1", BarcodeNormalized = "TB1", OptionSignature = "Beden: M", Desi = 1m, Status = ProductStatus.Active, CreatedAt = now, UpdatedAt = now, Version = 1 };
        var categories = new ReferenceSnapshot { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, ResourceType = "CATEGORIES", ScopeExternalId = "", SourceVersion = "2026-10", ContentHash = $"hash-{Guid.NewGuid():N}", FetchedAt = now, IsCurrent = true, ItemCount = 2 };
        var asset = new FileAsset { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Classification = "PRODUCT_MEDIA_URL", RelativePath = "https://cdn.example.com/tb/1.jpg", MimeType = "image/jpeg", Sha256 = Guid.NewGuid().ToString("N"), Status = "ACTIVE", CreatedAt = now };

        var db = fixture.CreateContext();
        var transaction = await db.Database.BeginTransactionAsync();
        db.Tenants.Add(tenant);
        db.PlatformConnections.Add(connection);
        db.Categories.AddRange(Category(tenant.Id, categoryId, "Bluz"), Category(tenant.Id, otherCategoryId, "Anne Bluz"));
        db.Products.Add(product);
        db.ProductVariants.Add(variant);
        db.ReferenceSnapshots.Add(categories);
        db.ReferenceItems.AddRange(Item(categories, "Kadın Bluz"), Item(categories, "Tunik"));
        db.ChannelListingProfiles.Add(new ChannelListingProfile { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, ProductId = product.Id, Enabled = true, DesiredStatus = "ACTIVE", ActualStatus = "DRAFT", Version = 1 });
        db.FileAssets.Add(asset);
        db.ProductMedia.Add(new ProductMedia { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProductId = product.Id, FileAssetId = asset.Id, MediaRole = "ORDERED_PRIMARY", SortOrder = 0, Status = "ACTIVE" });
        db.InventoryItems.Add(new InventoryItem { Id = Guid.CreateVersion7(), TenantId = tenant.Id, VariantId = variant.Id, LocationCode = "MAIN", OnHand = 5m, Available = 5m, ProjectionVersion = 1, Version = 1 });
        db.ChannelOffers.Add(new ChannelOffer { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, VariantId = variant.Id, ListPrice = 900m, SalePrice = 630m, Currency = "TRY", VatRate = 10m, VatInclusion = "INCLUDED", RoundingMode = "HALF_EVEN", Status = "ACTIVE", PriceVersion = 1, Version = 1 });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return new(db, transaction, tenant, connection, product, categoryId, otherCategoryId, categories);
    }

    private Category Category(Guid tenantId, Guid id, string name) => new() { Id = id, TenantId = tenantId, Name = name, NormalizedName = name.ToUpperInvariant(), Path = name, IsLeaf = true, IsActive = true, CreatedAt = fixture.Now, UpdatedAt = fixture.Now, Version = 1 };

    private static ReferenceItem Item(ReferenceSnapshot snapshot, string productType) => new() { Id = Guid.CreateVersion7(), TenantId = snapshot.TenantId, ConnectionId = snapshot.ConnectionId, SnapshotId = snapshot.Id, ResourceType = "CATEGORIES", ExternalId = productType, Name = productType, NormalizedName = productType.ToUpperInvariant(), Path = productType, Depth = 0, IsLeaf = true, IsActive = true, PayloadHash = $"payload-{productType}" };
}
