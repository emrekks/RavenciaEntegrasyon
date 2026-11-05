using System.Text.Json.Nodes;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Yayınla on a colour card already on the marketplace sends only the variants not there yet, under the card's model code.
// Live 2026-11-05 (MZ021Y-CD on Trendyol): a card with three matched sizes and new ones was skipped whole.
public sealed class PublicationUnsentVariantsTests
{
    [Fact]
    public void Unsent_LeavesOutTheVariantsThere_AndIsNullWhenAllAre()
    {
        var ids = new[] { Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7() };
        Assert.Equal([ids[1], ids[2]], PublicationUnsentVariants.Unsent(ids, id => id, new HashSet<Guid> { ids[0] }));
        Assert.Equal(ids, PublicationUnsentVariants.Unsent(ids, id => id, new HashSet<Guid>()));
        Assert.Null(PublicationUnsentVariants.Unsent(ids, id => id, ids.ToHashSet()));
    }

    [Fact]
    public void PazaramaGroupCode_ComesFromTheCardsGroupLink()
    {
        Assert.Equal("MZ021Y-CD", PazaramaProductPublicationComposer.ExistingGroupCode(["group:MZ021Y-CD", "code:869"]));
        Assert.Null(PazaramaProductPublicationComposer.ExistingGroupCode(["code:869", null]));
        // Two groups, or one longer than Pazarama's 10 characters, say nothing sure: the model code's group is used.
        Assert.Null(PazaramaProductPublicationComposer.ExistingGroupCode(["group:A1", "group:B2"]));
        Assert.Null(PazaramaProductPublicationComposer.ExistingGroupCode(["group:MZ021Y-CD-LONGER"]));
    }
}

[Collection("postgres-integration")]
public sealed class PublicationUnsentVariantsPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    [PostgreSqlFact]
    public async Task TrendyolPublish_OfAMatchedCard_SendsOnlyItsNewBarcodes_UnderTheCardsProductMainId()
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"unsent-{Guid.NewGuid():N}", DisplayName = "unsent", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var connection = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = "TRENDYOL", Environment = "PRODUCTION", DisplayName = "Trendyol eksik varyant", ExternalStoreId = $"store-{Guid.NewGuid():N}", Status = "ACTIVE", ApiVersion = "V1", SettingsJson = "{}", Version = 1 };
        var categoryId = Guid.CreateVersion7();
        var brandId = Guid.CreateVersion7();
        var product = new Product { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Title = "Penye Bluz", Description = "Eksik varyant testi", CategoryId = categoryId, BrandId = brandId, Status = ProductStatus.Active, CreatedAt = now, UpdatedAt = now, Version = 1 };
        ProductVariant Variant(string code) => new() { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProductId = product.Id, Sku = code, SkuNormalized = code, Barcode = code, BarcodeNormalized = code, ModelCode = "MZ021Y-CD", OptionSignature = code, Desi = 1m, Status = ProductStatus.Active, CreatedAt = now, UpdatedAt = now, Version = 1 };
        var matched = Variant("MZ021Y-CD13");
        var lost = Variant("MZ021Y-CD14");
        var added = Variant("MZ021Y-CD-25");
        var variants = new[] { matched, lost, added };
        ReferenceSnapshot Snapshot(string type, string scope) => new() { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, ResourceType = type, ScopeExternalId = scope, SourceVersion = type, ContentHash = $"hash-{type}", FetchedAt = now, IsCurrent = true, ItemCount = 1 };
        ReferenceItem Item(Guid snapshotId, string type, string externalId, string name) => new() { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, SnapshotId = snapshotId, ResourceType = type, ExternalId = externalId, Name = name, NormalizedName = name.ToUpperInvariant(), Path = name, Depth = 0, IsLeaf = true, IsActive = true, PayloadHash = $"payload-{type}-{externalId}" };
        var categories = Snapshot("CATEGORIES", "");
        var brands = Snapshot("BRANDS", "");
        var attributes = Snapshot("CATEGORY_ATTRIBUTES", "123");
        var asset = new FileAsset { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Classification = "PRODUCT_MEDIA_URL", RelativePath = "https://cdn.example.com/mz021-mavi.jpg", MimeType = "image/jpeg", Sha256 = Guid.NewGuid().ToString("N"), Status = "ACTIVE", CreatedAt = now };

        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        db.Tenants.Add(tenant);
        db.PlatformConnections.Add(connection);
        db.Categories.Add(new Category { Id = categoryId, TenantId = tenant.Id, Name = "Bluz", NormalizedName = "BLUZ", Path = "Bluz", IsLeaf = true, IsActive = true, CreatedAt = now, UpdatedAt = now, Version = 1 });
        db.Brands.Add(new Brand { Id = brandId, TenantId = tenant.Id, Name = "Ravencia", NormalizedName = "RAVENCIA", IsActive = true, CreatedAt = now, UpdatedAt = now, Version = 1 });
        db.Products.Add(product);
        db.ProductVariants.AddRange(variants);
        db.ReferenceSnapshots.AddRange(categories, brands, attributes);
        db.ReferenceItems.AddRange(Item(categories.Id, "CATEGORIES", "123", "Bluz"), Item(brands.Id, "BRANDS", "456", "Ravencia"));
        db.CategoryMappings.Add(new CategoryMapping { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, SnapshotId = categories.Id, LocalId = categoryId, ExternalId = "123", Status = "VERIFIED", VerifiedAt = now, Version = 1 });
        db.BrandMappings.Add(new BrandMapping { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, SnapshotId = brands.Id, LocalId = brandId, ExternalId = "456", Status = "VERIFIED", VerifiedAt = now, Version = 1 });
        db.ChannelListingProfiles.Add(new ChannelListingProfile { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, ProductId = product.Id, Enabled = true, DesiredStatus = "ACTIVE", ActualStatus = "DRAFT", Version = 1 });
        db.FileAssets.Add(asset);
        db.ProductMedia.Add(new ProductMedia { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProductId = product.Id, FileAssetId = asset.Id, MediaRole = "GALLERY", SortOrder = 0, Status = "ACTIVE" });
        foreach (var variant in variants)
        {
            db.InventoryItems.Add(new InventoryItem { Id = Guid.CreateVersion7(), TenantId = tenant.Id, VariantId = variant.Id, LocationCode = "MAIN", OnHand = 5m, Available = 5m, ProjectionVersion = 1, Version = 1 });
            db.ChannelOffers.Add(new ChannelOffer { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, VariantId = variant.Id, ListPrice = 900m, SalePrice = 630m, Currency = "TRY", VatRate = 10m, VatInclusion = "INCLUDED", RoundingMode = "HALF_EVEN", Status = "ACTIVE", PriceVersion = 1, Version = 1 });
        }
        // Matched by an import (Trendyol knows it); the second was lost there (NOT_FOUND) and goes again; the third is new.
        db.MarketplaceVariantLinks.AddRange(
            new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, VariantId = matched.Id, ExternalId = "1445970692", ExternalBarcode = matched.Barcode },
            new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, VariantId = lost.Id, ExternalId = "1445971349", ExternalBarcode = lost.Barcode, HealthStatus = MarketplaceVariantLinkHealth.NotFound });
        await db.SaveChangesAsync();

        var composer = new ProductPublicationComposer(db, new ConfigurationBuilder().Build());
        var publish = await composer.BuildAsync(tenant.Id, product.Id, connection.Id, CancellationToken.None, onlyUnsentVariants: true);
        Assert.True(publish.Succeeded, publish.Error?.Message);
        var items = JsonNode.Parse(publish.Value!.PayloadJson)!["items"]!.AsArray();
        Assert.Equal(["MZ021Y-CD-25", "MZ021Y-CD14"], items.Select(item => item!["barcode"]!.GetValue<string>()).Order(StringComparer.Ordinal));
        Assert.All(items, item => Assert.Equal("MZ021Y-CD", item!["productMainId"]!.GetValue<string>()));
        Assert.Equal([added.Id, lost.Id], publish.Value.Variants.Select(variant => variant.VariantId).Order());

        // The update composers build from the whole card.
        var whole = await composer.BuildAsync(tenant.Id, product.Id, connection.Id, CancellationToken.None);
        Assert.Equal(3, JsonNode.Parse(whole.Value!.PayloadJson)!["items"]!.AsArray().Count);

        // Every variant there: nothing to send.
        db.MarketplaceVariantLinks.Add(new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, VariantId = added.Id, ExternalId = "1727129699", ExternalBarcode = added.Barcode });
        var lostLink = db.MarketplaceVariantLinks.Local.Single(link => link.VariantId == lost.Id);
        lostLink.HealthStatus = null;
        await db.SaveChangesAsync();
        Assert.Equal("PRODUCT_ALREADY_LIVE", (await composer.BuildAsync(tenant.Id, product.Id, connection.Id, CancellationToken.None, onlyUnsentVariants: true)).Error?.Code);

        await transaction.RollbackAsync();
    }
}
