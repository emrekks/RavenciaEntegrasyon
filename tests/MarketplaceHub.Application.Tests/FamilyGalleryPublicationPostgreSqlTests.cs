using System.Text.Json.Nodes;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// The Trendyol payload of a model's colour cards (MZ051BYC shape, owner 11.11.2026): each colour card is composed alone,
// every item under the shared model code (productMainId) and with only its own colour's pictures, in the card's order.
// Pinned on the card shapes the editor writes colour by colour; the composer itself did not change.
[Collection("postgres-integration")]
public sealed class FamilyGalleryPublicationPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    [PostgreSqlFact]
    public async Task TrendyolPayload_OfEachColourCard_CarriesOnlyThatColoursPictures_UnderTheModelCode()
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"mz051-{Guid.NewGuid():N}", DisplayName = "mz051", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var connection = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = "TRENDYOL", Environment = "PRODUCTION", DisplayName = "Trendyol renk kartları", ExternalStoreId = $"store-{Guid.NewGuid():N}", Status = "ACTIVE", ApiVersion = "V1", SettingsJson = "{}", Version = 1 };
        var categoryId = Guid.CreateVersion7();
        var brandId = Guid.CreateVersion7();
        Product Card(string colour) => new() { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Title = $"Kadın {colour} Triko Bluz", Description = $"{colour} eski açıklama", CategoryId = categoryId, BrandId = brandId, VatRate = 10m, VatInclusion = "INCLUDED", DefaultListPrice = 900m, DefaultSalePrice = 630m, Status = ProductStatus.Active, CreatedAt = now, UpdatedAt = now, Version = 1 };
        var siyah = Card("Siyah");
        var bordo = Card("Bordo");
        ProductVariant Variant(Product card, string code) => new() { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProductId = card.Id, Sku = code, SkuNormalized = code, Barcode = code, BarcodeNormalized = code, ModelCode = "MZ051BYC", OptionSignature = $"Renk: {card.Title.Split(' ')[1]} | Beden: {code[^2..]}", Desi = 1m, Status = ProductStatus.Active, CreatedAt = now, UpdatedAt = now, Version = 1 };
        var siyahL = Variant(siyah, "MZ051BYC-31");
        var siyahXl = Variant(siyah, "MZ051BYC-32");
        var bordoL = Variant(bordo, "MZ051BYC-01");
        var bordoXl = Variant(bordo, "MZ051BYC-02");
        // A Bordo size added after the colour assignment: the editor gave it the colour's pictures as its own (inheritAssignments).
        var bordo2Xl = Variant(bordo, "MZ051BYC-03");
        ProductVariant[] variants = [siyahL, siyahXl, bordoL, bordoXl, bordo2Xl];
        ReferenceSnapshot Snapshot(string type, string scope) => new() { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, ResourceType = type, ScopeExternalId = scope, SourceVersion = type, ContentHash = $"hash-{type}", FetchedAt = now, IsCurrent = true, ItemCount = 1 };
        ReferenceItem Item(Guid snapshotId, string type, string externalId, string name) => new() { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, SnapshotId = snapshotId, ResourceType = type, ExternalId = externalId, Name = name, NormalizedName = name.ToUpperInvariant(), Path = name, Depth = 0, IsLeaf = true, IsActive = true, PayloadHash = $"payload-{type}-{externalId}" };
        var categories = Snapshot("CATEGORIES", "");
        var brands = Snapshot("BRANDS", "");
        var attributes = Snapshot("CATEGORY_ATTRIBUTES", "123");
        var assets = new Dictionary<string, FileAsset>();
        FileAsset Asset(string name)
        {
            if (assets.TryGetValue(name, out var existing)) return existing;
            return assets[name] = new FileAsset { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Classification = "PRODUCT_MEDIA_URL", RelativePath = $"https://cdn.example.com/mz051byc/{name}.jpg", MimeType = "image/jpeg", Sha256 = Guid.NewGuid().ToString("N"), Status = "ACTIVE", CreatedAt = now };
        }
        var media = new List<ProductMedia>();
        void Pictures(Product card, ProductVariant? variant, params string[] names) =>
            media.AddRange(names.Select((name, index) => new ProductMedia { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProductId = card.Id, VariantId = variant?.Id, FileAssetId = Asset(name).Id, MediaRole = index == 0 ? "ORDERED_PRIMARY" : "ORDERED_GALLERY", SortOrder = index * 50, Status = "ACTIVE" }));
        // What the editor writes, whichever colour row was opened: each card's gallery holds its own colour's pictures, the
        // XL size of each its own assignment (an imported card's first size).
        Pictures(siyah, null, "siyah-1", "siyah-2", "siyah-3");
        Pictures(siyah, siyahXl, "siyah-1", "siyah-2", "siyah-3");
        Pictures(bordo, null, "bordo-1", "bordo-2", "bordo-3");
        Pictures(bordo, bordoXl, "bordo-1", "bordo-2", "bordo-3");
        Pictures(bordo, bordo2Xl, "bordo-1", "bordo-2", "bordo-3");

        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        db.Tenants.Add(tenant);
        db.PlatformConnections.Add(connection);
        db.Categories.Add(new Category { Id = categoryId, TenantId = tenant.Id, Name = "Bluz", NormalizedName = "BLUZ", Path = "Bluz", IsLeaf = true, IsActive = true, CreatedAt = now, UpdatedAt = now, Version = 1 });
        db.Brands.Add(new Brand { Id = brandId, TenantId = tenant.Id, Name = "Ravencia", NormalizedName = "RAVENCIA", IsActive = true, CreatedAt = now, UpdatedAt = now, Version = 1 });
        db.Products.AddRange(siyah, bordo);
        db.ProductVariants.AddRange(variants);
        db.ReferenceSnapshots.AddRange(categories, brands, attributes);
        db.ReferenceItems.AddRange(Item(categories.Id, "CATEGORIES", "123", "Bluz"), Item(brands.Id, "BRANDS", "456", "Ravencia"));
        db.CategoryMappings.Add(new CategoryMapping { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, SnapshotId = categories.Id, LocalId = categoryId, ExternalId = "123", Status = "VERIFIED", VerifiedAt = now, Version = 1 });
        db.BrandMappings.Add(new BrandMapping { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, SnapshotId = brands.Id, LocalId = brandId, ExternalId = "456", Status = "VERIFIED", VerifiedAt = now, Version = 1 });
        foreach (var card in new[] { siyah, bordo })
            db.ChannelListingProfiles.Add(new ChannelListingProfile { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, ProductId = card.Id, Enabled = true, DesiredStatus = "ACTIVE", ActualStatus = "DRAFT", Version = 1 });
        db.FileAssets.AddRange(assets.Values);
        db.ProductMedia.AddRange(media);
        foreach (var variant in variants)
        {
            db.InventoryItems.Add(new InventoryItem { Id = Guid.CreateVersion7(), TenantId = tenant.Id, VariantId = variant.Id, LocationCode = "MAIN", OnHand = 5m, Available = 5m, ProjectionVersion = 1, Version = 1 });
            db.ChannelOffers.Add(new ChannelOffer { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, VariantId = variant.Id, ListPrice = 900m, SalePrice = 630m, Currency = "TRY", VatRate = 10m, VatInclusion = "INCLUDED", RoundingMode = "HALF_EVEN", Status = "ACTIVE", PriceVersion = 1, Version = 1 });
        }
        await db.SaveChangesAsync();

        var composer = new ProductPublicationComposer(db, new ConfigurationBuilder().Build());
        async Task AssertPayloads(Func<string, string> title, Func<string, string> description)
        {
            foreach (var (card, colour, barcodes) in new[] { (siyah, "Siyah", new[] { "MZ051BYC-31", "MZ051BYC-32" }), (bordo, "Bordo", new[] { "MZ051BYC-01", "MZ051BYC-02", "MZ051BYC-03" }) })
            {
                var draft = await composer.BuildAsync(tenant.Id, card.Id, connection.Id, CancellationToken.None);
                Assert.True(draft.Succeeded, draft.Error?.Message);
                var items = JsonNode.Parse(draft.Value!.PayloadJson)!["items"]!.AsArray();
                Assert.Equal(barcodes, items.Select(item => item!["barcode"]!.GetValue<string>()).Order(StringComparer.Ordinal));
                var pictures = colour.ToLowerInvariant();
                Assert.All(items, item =>
                {
                    Assert.Equal("MZ051BYC", item!["productMainId"]!.GetValue<string>());
                    Assert.Equal(title(colour), item["title"]!.GetValue<string>());
                    Assert.Equal(description(colour), item["description"]!.GetValue<string>());
                    Assert.Equal([$"https://cdn.example.com/mz051byc/{pictures}-1.jpg", $"https://cdn.example.com/mz051byc/{pictures}-2.jpg", $"https://cdn.example.com/mz051byc/{pictures}-3.jpg"],
                        item["images"]!.AsArray().Select(image => image!["url"]!.GetValue<string>()));
                });
            }
        }

        // Before: each colour card goes with its own name and description.
        await AssertPayloads(colour => $"Kadın {colour} Triko Bluz", colour => $"{colour} eski açıklama");

        // The owner edits the model's name and description from the Siyah row (FamilySharedFields): every colour card
        // takes them, each name with its own colour word; the pictures and the content split per colour stay as they were.
        var service = new CatalogService(db, null!, new ConfigurationBuilder().Build(), fixture.TimeProvider, new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()));
        var updated = await service.UpdateProductAsync(tenant.Id, siyah.Id, siyah.Version, new UpdateProductCommand("Kadın Siyah Yumuşak Triko Bluz", "Model açıklaması", brandId, categoryId, FamilySharedFields: [ProductFamilySharedFields.Title, ProductFamilySharedFields.Description]), CancellationToken.None);
        Assert.True(updated.Succeeded, updated.Error?.Message);
        Assert.Empty(updated.Value!.FamilyFieldDifferences!);
        await AssertPayloads(colour => $"Kadın {colour} Yumuşak Triko Bluz", _ => "Model açıklaması");
        await transaction.RollbackAsync();
    }

    // The model's size chart held by one colour card only (live RYP003T; the MZ051BYC Siyah dry run, owner 12.11.2026):
    // the colour card without the chart's row and without a chart of its own still sends the model's chart last from
    // every size; the card holding it sends the same list as before.
    [PostgreSqlFact]
    public async Task TrendyolPayload_OfAColourCardWithoutTheChartsRow_EndsWithTheModelsChart()
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"chart-{Guid.NewGuid():N}", DisplayName = "chart", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var connection = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = "TRENDYOL", Environment = "PRODUCTION", DisplayName = "Trendyol beden tablosu", ExternalStoreId = $"store-{Guid.NewGuid():N}", Status = "ACTIVE", ApiVersion = "V1", SettingsJson = "{}", Version = 1 };
        var categoryId = Guid.CreateVersion7();
        var brandId = Guid.CreateVersion7();
        Product Card(string colour, int minute) => new() { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Title = $"Kadın {colour} Triko Bluz", Description = $"{colour} açıklama", CategoryId = categoryId, BrandId = brandId, VatRate = 10m, VatInclusion = "INCLUDED", Status = ProductStatus.Active, CreatedAt = now.AddMinutes(minute), UpdatedAt = now, Version = 1 };
        var bordo = Card("Bordo", 0);
        var siyah = Card("Siyah", 1);
        ProductVariant Variant(Product card, string code) => new() { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProductId = card.Id, Sku = code, SkuNormalized = code, Barcode = code, BarcodeNormalized = code, ModelCode = "MZ051BYC", OptionSignature = $"Renk: {card.Title.Split(' ')[1]} | Beden: {code[^2..]}", Desi = 1m, Status = ProductStatus.Active, CreatedAt = now, UpdatedAt = now, Version = 1 };
        var bordoL = Variant(bordo, "MZ051BYC-01");
        var bordoXl = Variant(bordo, "MZ051BYC-02");
        var siyahL = Variant(siyah, "MZ051BYC-31");
        var siyahXl = Variant(siyah, "MZ051BYC-32");
        ProductVariant[] variants = [bordoL, bordoXl, siyahL, siyahXl];
        ReferenceSnapshot Snapshot(string type, string scope) => new() { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, ResourceType = type, ScopeExternalId = scope, SourceVersion = type, ContentHash = $"hash-{type}", FetchedAt = now, IsCurrent = true, ItemCount = 1 };
        ReferenceItem Item(Guid snapshotId, string type, string externalId, string name) => new() { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, SnapshotId = snapshotId, ResourceType = type, ExternalId = externalId, Name = name, NormalizedName = name.ToUpperInvariant(), Path = name, Depth = 0, IsLeaf = true, IsActive = true, PayloadHash = $"payload-{type}-{externalId}" };
        var categories = Snapshot("CATEGORIES", "");
        var brands = Snapshot("BRANDS", "");
        var attributes = Snapshot("CATEGORY_ATTRIBUTES", "123");
        var assets = new Dictionary<string, FileAsset>();
        FileAsset Asset(string name)
        {
            if (assets.TryGetValue(name, out var existing)) return existing;
            return assets[name] = new FileAsset { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Classification = "PRODUCT_MEDIA_URL", RelativePath = $"https://cdn.example.com/mz051byc/{name}.jpg", MimeType = "image/jpeg", Sha256 = Guid.NewGuid().ToString("N"), Status = "ACTIVE", CreatedAt = now };
        }
        var media = new List<ProductMedia>();
        void Pictures(Product card, ProductVariant? variant, params string[] names) =>
            media.AddRange(names.Select((name, index) => new ProductMedia { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProductId = card.Id, VariantId = variant?.Id, FileAssetId = Asset(name).Id, MediaRole = index == 0 ? "ORDERED_PRIMARY" : "ORDERED_GALLERY", SortOrder = index * 50, Status = "ACTIVE" }));
        // Bordo holds the model's chart (its gallery and its chart mark); Siyah holds neither, only XL has its own pictures.
        Pictures(bordo, null, "bordo-1", "bordo-2", "bordo-3", "beden-tablosu");
        Pictures(bordo, bordoXl, "bordo-1", "bordo-2", "bordo-3");
        Pictures(siyah, null, "siyah-1", "siyah-2", "siyah-3");
        Pictures(siyah, siyahXl, "siyah-1", "siyah-2", "siyah-3");

        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        db.Tenants.Add(tenant);
        db.PlatformConnections.Add(connection);
        db.Categories.Add(new Category { Id = categoryId, TenantId = tenant.Id, Name = "Bluz", NormalizedName = "BLUZ", Path = "Bluz", IsLeaf = true, IsActive = true, CreatedAt = now, UpdatedAt = now, Version = 1 });
        db.Brands.Add(new Brand { Id = brandId, TenantId = tenant.Id, Name = "Ravencia", NormalizedName = "RAVENCIA", IsActive = true, CreatedAt = now, UpdatedAt = now, Version = 1 });
        db.Products.AddRange(bordo, siyah);
        db.ProductVariants.AddRange(variants);
        db.ReferenceSnapshots.AddRange(categories, brands, attributes);
        db.ReferenceItems.AddRange(Item(categories.Id, "CATEGORIES", "123", "Bluz"), Item(brands.Id, "BRANDS", "456", "Ravencia"));
        db.CategoryMappings.Add(new CategoryMapping { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, SnapshotId = categories.Id, LocalId = categoryId, ExternalId = "123", Status = "VERIFIED", VerifiedAt = now, Version = 1 });
        db.BrandMappings.Add(new BrandMapping { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, SnapshotId = brands.Id, LocalId = brandId, ExternalId = "456", Status = "VERIFIED", VerifiedAt = now, Version = 1 });
        foreach (var card in new[] { bordo, siyah })
            db.ChannelListingProfiles.Add(new ChannelListingProfile { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, ProductId = card.Id, Enabled = true, DesiredStatus = "ACTIVE", ActualStatus = "DRAFT", Version = 1 });
        db.FileAssets.AddRange(assets.Values);
        db.ProductMedia.AddRange(media);
        db.ProductSizeCharts.Add(new ProductSizeChart { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProductId = bordo.Id, FileAssetId = Asset("beden-tablosu").Id, UpdatedAt = now });
        foreach (var variant in variants)
        {
            db.InventoryItems.Add(new InventoryItem { Id = Guid.CreateVersion7(), TenantId = tenant.Id, VariantId = variant.Id, LocationCode = "MAIN", OnHand = 5m, Available = 5m, ProjectionVersion = 1, Version = 1 });
            db.ChannelOffers.Add(new ChannelOffer { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, VariantId = variant.Id, ListPrice = 900m, SalePrice = 630m, Currency = "TRY", VatRate = 10m, VatInclusion = "INCLUDED", RoundingMode = "HALF_EVEN", Status = "ACTIVE", PriceVersion = 1, Version = 1 });
        }
        await db.SaveChangesAsync();

        Assert.Equal(Asset("beden-tablosu").Id, await Infrastructure.SizeCharts.SizeChartPlacement.ModelAssetIdAsync(db, tenant.Id, siyah, [siyahL, siyahXl], CancellationToken.None));

        var composer = new ProductPublicationComposer(db, new ConfigurationBuilder().Build());
        foreach (var (card, colour) in new[] { (bordo, "bordo"), (siyah, "siyah") })
        {
            var draft = await composer.BuildAsync(tenant.Id, card.Id, connection.Id, CancellationToken.None);
            Assert.True(draft.Succeeded, draft.Error?.Message);
            var items = JsonNode.Parse(draft.Value!.PayloadJson)!["items"]!.AsArray();
            Assert.Equal(2, items.Count);
            Assert.All(items, item => Assert.Equal(
                [$"https://cdn.example.com/mz051byc/{colour}-1.jpg", $"https://cdn.example.com/mz051byc/{colour}-2.jpg", $"https://cdn.example.com/mz051byc/{colour}-3.jpg", "https://cdn.example.com/mz051byc/beden-tablosu.jpg"],
                item!["images"]!.AsArray().Select(image => image!["url"]!.GetValue<string>())));
        }
        await transaction.RollbackAsync();
    }
}
