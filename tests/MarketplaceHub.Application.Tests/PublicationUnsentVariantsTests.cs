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
        Assert.Equal(new[] { added.Id, lost.Id }.Order(), publish.Value.Variants.Select(variant => variant.VariantId).Order());

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

    // Live 11.11.2026 (RYP006): one panel card holds six colours live on Trendyol (six contents under productMainId
    // RYP006, profile marked PRODUCT_APPROVAL_CONTENT_SPLIT) and three new Mürdüm sizes. Yayınla sends only the three
    // Mürdüm barcodes under the model code, each with its own Mürdüm pictures and the size chart last, so Trendyol opens
    // the colour as a new content of the model; the eighteen live variants are not sent again.
    [PostgreSqlFact]
    public async Task TrendyolPublish_OfAMultiColourCard_SendsOnlyTheNewColour_WithItsOwnPictures()
    {
        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var card = await Ryp006CardAsync(db, muerdumPictures: true);

        var composer = new ProductPublicationComposer(db, new ConfigurationBuilder().Build());
        var publish = await composer.BuildAsync(card.TenantId, card.ProductId, card.ConnectionId, CancellationToken.None, onlyUnsentVariants: true);
        Assert.True(publish.Succeeded, publish.Error?.Message);
        var items = JsonNode.Parse(publish.Value!.PayloadJson)!["items"]!.AsArray();
        Assert.Equal(["RYP006-19", "RYP006-20", "RYP006-21"], items.Select(item => item!["barcode"]!.GetValue<string>()).Order(StringComparer.Ordinal));
        Assert.All(items, item =>
        {
            Assert.Equal("RYP006", item!["productMainId"]!.GetValue<string>());
            Assert.Equal(["https://cdn.example.com/murdum-1.jpg", "https://cdn.example.com/murdum-2.jpg", "https://cdn.example.com/murdum-3.jpg", "https://cdn.example.com/beden-tablosu.jpg"], item["images"]!.AsArray().Select(image => image!["url"]!.GetValue<string>()));
            Assert.Contains(item["attributes"]!.AsArray(), attribute => attribute!["attributeId"]!.GetValue<long>() == 348 && attribute["customAttributeValue"]!.GetValue<string>() == "Mürdüm");
        });
        Assert.Equal(card.Muerdum.Order(), publish.Value.Variants.Select(variant => variant.VariantId).Order());

        // The whole card (an update of its content) still stops on the split contents.
        Assert.Equal("PRODUCT_APPROVAL_CONTENT_SPLIT", (await composer.BuildAsync(card.TenantId, card.ProductId, card.ConnectionId, CancellationToken.None)).Error?.Code);
        await transaction.RollbackAsync();
    }

    // A mixed family (owner, 11.11.2026: "ayrımları yapabilsin"): a new Haki size joins the Haki content, two Mürdüm
    // sizes open the Mürdüm content, a Mürdüm size without a price is left out with its reason, and the eighteen live
    // variants are not sent; the one that cannot go does not stop the others.
    [PostgreSqlFact]
    public async Task TrendyolPublish_OfAMixedFamily_SendsEachGroup_AndListsTheVariantThatCannotGo()
    {
        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var card = await Ryp006CardAsync(db, muerdumPictures: true, mixed: true);

        var publish = await new ProductPublicationComposer(db, new ConfigurationBuilder().Build()).BuildAsync(card.TenantId, card.ProductId, card.ConnectionId, CancellationToken.None, onlyUnsentVariants: true);
        Assert.True(publish.Succeeded, publish.Error?.Message);
        Assert.True(publish.Value!.AddsToListing);
        var items = JsonNode.Parse(publish.Value.PayloadJson)!["items"]!.AsArray();
        Assert.Equal(["RYP006-19", "RYP006-20", "RYP006-22"], items.Select(item => item!["barcode"]!.GetValue<string>()).Order(StringComparer.Ordinal));
        var haki = items.Single(item => item!["barcode"]!.GetValue<string>() == "RYP006-22")!;
        Assert.Equal("https://cdn.example.com/haki-1.jpg", haki["images"]!.AsArray()[0]!["url"]!.GetValue<string>());
        Assert.Contains(haki["attributes"]!.AsArray(), attribute => attribute!["attributeId"]!.GetValue<long>() == 348 && attribute["customAttributeValue"]!.GetValue<string>() == "Haki");
        var left = Assert.Single(publish.Value.SkippedVariants);
        Assert.Equal(("RYP006-21", "CHANNEL_OFFER_REQUIRED"), (left.Sku, left.Code));
        await transaction.RollbackAsync();
    }

    // A new colour without pictures of its own would open on Trendyol with the card's general gallery (every colour's
    // pictures): it is not sent.
    [PostgreSqlFact]
    public async Task TrendyolPublish_OfANewColourWithoutItsOwnPictures_IsRefused()
    {
        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var card = await Ryp006CardAsync(db, muerdumPictures: false);

        var publish = await new ProductPublicationComposer(db, new ConfigurationBuilder().Build()).BuildAsync(card.TenantId, card.ProductId, card.ConnectionId, CancellationToken.None, onlyUnsentVariants: true);
        Assert.Equal(PublicationUnsentVariants.NewColourPicturesRequiredCode, publish.Error?.Code);
        Assert.Contains("Mürdüm", publish.Error!.Message);
        await transaction.RollbackAsync();
    }

    private sealed record Ryp006Card(Guid TenantId, Guid ProductId, Guid ConnectionId, IReadOnlyList<Guid> Muerdum);

    // mixed: RYP006-21 has no Trendyol price, and a new Haki size RYP006-22 (its own Haki pictures) is added.
    private static async Task<Ryp006Card> Ryp006CardAsync(AppDbContext db, bool muerdumPictures, bool mixed = false)
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"ryp006-{Guid.NewGuid():N}", DisplayName = "ryp006", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var connection = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = "TRENDYOL", Environment = "PRODUCTION", DisplayName = "Trendyol yeni renk", ExternalStoreId = $"store-{Guid.NewGuid():N}", Status = "ACTIVE", ApiVersion = "V1", SettingsJson = "{}", Version = 1 };
        var categoryId = Guid.CreateVersion7();
        var brandId = Guid.CreateVersion7();
        var product = new Product { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Title = "Kadın Bisiklet Yaka Desenli Triko Kazak", Description = "Yeni renk testi", CategoryId = categoryId, BrandId = brandId, Status = ProductStatus.Active, CreatedAt = now, UpdatedAt = now, Version = 1 };
        ReferenceSnapshot Snapshot(string type, string scope) => new() { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, ResourceType = type, ScopeExternalId = scope, SourceVersion = type, ContentHash = $"hash-{type}", FetchedAt = now, IsCurrent = true, ItemCount = 1 };
        ReferenceItem Item(Guid snapshotId, string type, string externalId, string name) => new() { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, SnapshotId = snapshotId, ResourceType = type, ExternalId = externalId, Name = name, NormalizedName = name.ToUpperInvariant(), Path = name, Depth = 0, IsLeaf = true, IsActive = true, PayloadHash = $"payload-{type}-{externalId}" };
        var categories = Snapshot("CATEGORIES", "");
        var brands = Snapshot("BRANDS", "");
        var attributes = Snapshot("CATEGORY_ATTRIBUTES", "123");
        var renkAttribute = new AttributeDefinition { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Code = $"renk-{tenant.Id:N}", Name = "Renk", DataType = AttributeDataType.SingleSelect, CreatedAt = now, UpdatedAt = now };
        var renk = new ProductOption { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProductId = product.Id, Label = "Renk", NormalizedKey = "RENK", SortOrder = 0 };

        db.Tenants.Add(tenant);
        db.PlatformConnections.Add(connection);
        db.Categories.Add(new Category { Id = categoryId, TenantId = tenant.Id, Name = "Kazak", NormalizedName = "KAZAK", Path = "Kazak", IsLeaf = true, IsActive = true, CreatedAt = now, UpdatedAt = now, Version = 1 });
        db.Brands.Add(new Brand { Id = brandId, TenantId = tenant.Id, Name = "Ravencia", NormalizedName = "RAVENCIA", IsActive = true, CreatedAt = now, UpdatedAt = now, Version = 1 });
        db.Products.Add(product);
        db.ReferenceSnapshots.AddRange(categories, brands, attributes);
        db.ReferenceItems.AddRange(Item(categories.Id, "CATEGORIES", "123", "Kazak"), Item(brands.Id, "BRANDS", "456", "Ravencia"), Item(attributes.Id, "CATEGORY_ATTRIBUTES", "348", "Renk"));
        db.CategoryMappings.Add(new CategoryMapping { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, SnapshotId = categories.Id, LocalId = categoryId, ExternalId = "123", Status = "VERIFIED", VerifiedAt = now, Version = 1 });
        db.BrandMappings.Add(new BrandMapping { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, SnapshotId = brands.Id, LocalId = brandId, ExternalId = "456", Status = "VERIFIED", VerifiedAt = now, Version = 1 });
        db.AttributeDefinitions.Add(renkAttribute);
        db.AttributeMappings.Add(new AttributeMapping { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, SnapshotId = attributes.Id, LocalId = renkAttribute.Id, ScopeExternalId = "123", ExternalId = "348", Status = "VERIFIED", VerifiedAt = now, Version = 1 });
        // Six colours live on Trendyol, each its own content: the profile carries the split mark.
        db.ChannelListingProfiles.Add(new ChannelListingProfile { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, ProductId = product.Id, Enabled = true, DesiredStatus = "LIVE", ActualStatus = "LIVE", LastRejectionCode = ProductApprovalReconciliationPolicy.SplitApprovedContentsCode, Version = 1 });
        db.ProductOptions.Add(renk);

        FileAsset Asset(string name) => new() { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Classification = "PRODUCT_MEDIA_URL", RelativePath = $"https://cdn.example.com/{name}.jpg", MimeType = "image/jpeg", Sha256 = Guid.NewGuid().ToString("N"), Status = "ACTIVE", CreatedAt = now };
        var sizeChart = Asset("beden-tablosu");
        db.FileAssets.Add(sizeChart);
        db.ProductSizeCharts.Add(new ProductSizeChart { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProductId = product.Id, FileAssetId = sizeChart.Id, UpdatedAt = now });
        var sort = 0;
        void Picture(FileAsset asset, Guid? variantId, int order) => db.ProductMedia.Add(new ProductMedia { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProductId = product.Id, VariantId = variantId, FileAssetId = asset.Id, MediaRole = "GALLERY", SortOrder = order, Status = "ACTIVE" });

        var colours = new[] { "Haki", "Kahverengi", "Kiremit", "Lacivert", "Siyah", "Vizon", "Mürdüm" };
        var muerdum = new List<Guid>();
        ProductOptionValue? hakiValue = null;
        List<FileAsset>? hakiPictures = null;
        var number = 0;
        foreach (var (colour, colourIndex) in colours.Select((colour, index) => (colour, index)))
        {
            ProductOptionValue value = new ProductOptionValue { Id = Guid.CreateVersion7(), TenantId = tenant.Id, OptionId = renk.Id, Label = colour, NormalizedKey = colour.ToUpperInvariant(), SortOrder = colourIndex };
            db.ProductOptionValues.Add(value);
            var key = colour == "Mürdüm" ? "murdum" : colour.ToLowerInvariant();
            var pictures = Enumerable.Range(1, 3).Select(index => Asset($"{key}-{index}")).ToList();
            db.FileAssets.AddRange(pictures);
            if (colour == "Haki") (hakiValue, hakiPictures) = (value, pictures);
            // The card's strip holds every colour's pictures, then the size chart.
            foreach (var picture in pictures) Picture(picture, null, sort++);
            foreach (var size in colour == "Mürdüm" && mixed ? new[] { "L", "XL", "2XL", "3XL-HAKI" } : new[] { "L", "XL", "2XL" })
            {
                var code = $"RYP006-{++number:00}";
                var newHaki = size == "3XL-HAKI";
                if (newHaki) value = hakiValue!;
                var variant = new ProductVariant { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProductId = product.Id, Sku = code, SkuNormalized = code, Barcode = code, BarcodeNormalized = code, ModelCode = "RYP006", OptionSignature = $"Renk: {colour} | Beden: {size}", Desi = 1m, Status = ProductStatus.Active, CreatedAt = now, UpdatedAt = now, Version = 1 };
                db.ProductVariants.Add(variant);
                db.VariantOptionValues.Add(new VariantOptionValue { Id = Guid.CreateVersion7(), TenantId = tenant.Id, VariantId = variant.Id, OptionId = renk.Id, OptionValueId = value.Id });
                db.InventoryItems.Add(new InventoryItem { Id = Guid.CreateVersion7(), TenantId = tenant.Id, VariantId = variant.Id, LocationCode = "MAIN", OnHand = 5m, Available = 5m, ProjectionVersion = 1, Version = 1 });
                if (!(mixed && code == "RYP006-21")) db.ChannelOffers.Add(new ChannelOffer { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, VariantId = variant.Id, ListPrice = 900m, SalePrice = 630m, Currency = "TRY", VatRate = 10m, VatInclusion = "INCLUDED", RoundingMode = "HALF_EVEN", Status = "ACTIVE", PriceVersion = 1, Version = 1 });
                if (colour != "Mürdüm" || muerdumPictures)
                {
                    foreach (var (picture, order) in (newHaki ? hakiPictures! : pictures).Select((picture, order) => (picture, order))) Picture(picture, variant.Id, order);
                    Picture(sizeChart, variant.Id, 3);
                }
                if (newHaki) continue;
                if (colour == "Mürdüm") muerdum.Add(variant.Id);
                else db.MarketplaceVariantLinks.Add(new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, VariantId = variant.Id, ExternalId = $"{1445970000 + number}", ExternalBarcode = code });
            }
        }
        Picture(sizeChart, null, sort);
        await db.SaveChangesAsync();
        return new Ryp006Card(tenant.Id, product.Id, connection.Id, muerdum);
    }
}
