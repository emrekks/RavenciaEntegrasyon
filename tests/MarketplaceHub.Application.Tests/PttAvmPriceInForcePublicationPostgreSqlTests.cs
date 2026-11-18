using System.Text.Json.Nodes;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Review of 18 Nov 2026 (B6): the PTT AVM publication prices a family from the price in force, the function its price-stock
// send uses (PriceInventoryComposer.PttAvmPriceInForce), not from the offers' stored prices. Panel 100 (M) and 150 (L) with
// offers stored at 300: the product goes at 100 and L carries a difference of 50 (owner: "farkı 50 olarak hesaplasın").
[Collection("postgres-integration")]
public sealed class PttAvmPriceInForcePublicationPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    [PostgreSqlFact]
    public async Task ThePublicationPricesTheFamilyFromThePriceInForce()
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"ptt-in-force-{Guid.NewGuid():N}", DisplayName = "ptt", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var categoryId = Guid.CreateVersion7();
        var brandId = Guid.CreateVersion7();
        var card = new Product { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Title = "Kadın Siyah Triko Bluz MZ420", Description = "Açıklama", CategoryId = categoryId, BrandId = brandId, VatRate = 10m, VatInclusion = "INCLUDED", WarrantyMonths = 24, DeliveryDays = 2, Status = ProductStatus.Active, CreatedAt = now, UpdatedAt = now, Version = 1 };
        var variants = new[] { ("M", 100m), ("L", 150m) }.Select((size, index) => new ProductVariant
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            ProductId = card.Id,
            SortOrder = index,
            Sku = $"MZ420-{size.Item1}",
            SkuNormalized = $"MZ420-{size.Item1}",
            Barcode = $"869420000{index}11",
            BarcodeNormalized = $"869420000{index}11",
            ModelCode = "MZ420",
            OptionSignature = $"Renk: Siyah | Beden: {size.Item1}",
            Desi = 1m,
            DefaultListPrice = size.Item2,
            DefaultSalePrice = size.Item2,
            Status = ProductStatus.Active,
            CreatedAt = now,
            UpdatedAt = now,
            Version = 1
        }).ToList();
        var connection = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = "PTTAVM", Environment = "PRODUCTION", DisplayName = "PTTAVM", ExternalStoreId = $"store-{Guid.NewGuid():N}", Status = "ACTIVE", ApiVersion = "V1", SettingsJson = "{}", Version = 1 };
        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.Add(tenant);
            seed.Categories.Add(new Category { Id = categoryId, TenantId = tenant.Id, Name = "Bluz", NormalizedName = "BLUZ", Path = "Bluz", IsLeaf = true, IsActive = true, CreatedAt = now, UpdatedAt = now, Version = 1 });
            seed.Brands.Add(new Brand { Id = brandId, TenantId = tenant.Id, Name = "Ravencia", NormalizedName = "RAVENCIA", IsActive = true, CreatedAt = now, UpdatedAt = now, Version = 1 });
            seed.PlatformConnections.Add(connection);
            seed.Products.Add(card);
            seed.ProductVariants.AddRange(variants);
            var asset = new FileAsset { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Classification = "PRODUCT_MEDIA_URL", RelativePath = "https://cdn.example.com/mz420/0.jpg", MimeType = "image/jpeg", Sha256 = Guid.NewGuid().ToString("N"), Status = "ACTIVE", CreatedAt = now };
            seed.FileAssets.Add(asset);
            seed.ProductMedia.Add(new ProductMedia { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProductId = card.Id, FileAssetId = asset.Id, MediaRole = "ORDERED_PRIMARY", SortOrder = 0, Status = "ACTIVE" });
            ReferenceSnapshot Snapshot(string type) => new() { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, ResourceType = type, ScopeExternalId = type == "CATEGORY_ATTRIBUTES" ? "123" : "", SourceVersion = type, ContentHash = $"hash-{type}", FetchedAt = now, IsCurrent = true, ItemCount = 1 };
            var categories = Snapshot("CATEGORIES");
            var brands = Snapshot("BRANDS");
            seed.ReferenceSnapshots.AddRange(categories, brands, Snapshot("CATEGORY_ATTRIBUTES"));
            seed.ReferenceItems.AddRange(
                new ReferenceItem { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, SnapshotId = categories.Id, ResourceType = "CATEGORIES", ExternalId = "123", Name = "Bluz", NormalizedName = "BLUZ", Path = "Bluz", Depth = 0, IsLeaf = true, IsActive = true, PayloadHash = "c" },
                new ReferenceItem { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, SnapshotId = brands.Id, ResourceType = "BRANDS", ExternalId = "456", Name = "Ravencia", NormalizedName = "RAVENCIA", Path = "Ravencia", Depth = 0, IsLeaf = true, IsActive = true, PayloadHash = "b" });
            seed.CategoryMappings.Add(new CategoryMapping { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, SnapshotId = categories.Id, LocalId = categoryId, ExternalId = "123", Status = "VERIFIED", VerifiedAt = now, Version = 1 });
            seed.BrandMappings.Add(new BrandMapping { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, SnapshotId = brands.Id, LocalId = brandId, ExternalId = "456", Status = "VERIFIED", VerifiedAt = now, Version = 1 });
            seed.ChannelListingProfiles.Add(new ChannelListingProfile { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, ProductId = card.Id, Enabled = true, DesiredStatus = "ACTIVE", ActualStatus = "DRAFT", Version = 1 });
            foreach (var variant in variants)
            {
                seed.ChannelOffers.Add(new ChannelOffer { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, VariantId = variant.Id, ListPrice = 300m, SalePrice = 300m, Currency = "TRY", VatRate = 10m, VatInclusion = "INCLUDED", RoundingMode = "HALF_EVEN", Status = "ACTIVE", PriceVersion = 1, Version = 1 });
                seed.InventoryItems.Add(new InventoryItem { Id = Guid.CreateVersion7(), TenantId = tenant.Id, VariantId = variant.Id, LocationCode = "MAIN", OnHand = 5m, Available = 5m, ProjectionVersion = 1, Version = 1 });
            }
            await seed.SaveChangesAsync();
        }

        await using var db = fixture.CreateContext();
        var draft = await new PttAvmProductPublicationComposer(db, new ConfigurationBuilder().Build()).BuildAsync(tenant.Id, card.Id, connection.Id, CancellationToken.None);
        Assert.True(draft.Succeeded, draft.Error?.Message);
        var payload = JsonNode.Parse(draft.Value!.PayloadJson)!.AsObject();
        Assert.Equal(100m, payload["priceWithVat"]!.GetValue<decimal>());
        var differences = payload["variants"]!.AsArray().ToDictionary(x => x!["size"]!.GetValue<string>(), x => x!["priceDifference"]?.GetValue<decimal>() ?? 0m);
        Assert.Equal((0m, 50m), (differences["M"], differences["L"]));
        // The price-stock send prices each variant with the same function.
        var cards = await PriceInventoryComposer.CardPanelPricesAsync(db, tenant.Id, [card.Id], CancellationToken.None);
        var offers = db.ChannelOffers.Where(x => x.TenantId == tenant.Id).ToList();
        Assert.Equal([100m, 150m], variants.Select(variant => PriceInventoryComposer.PttAvmPriceInForce(offers.Single(x => x.VariantId == variant.Id), variant, cards)));
    }
}
