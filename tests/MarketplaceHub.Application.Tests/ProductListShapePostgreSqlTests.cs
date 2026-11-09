using System.Text.Json;
using MarketplaceHub.Api.Catalog;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// GET /products (OPTIMIZASYON-PLANI P3, 9 Nov 2026): a 20-family page was 2.45 MB, most of it what only the product editor
// reads. The list card keeps what the list screens show and GET /products/{id} keeps everything.
[Collection("postgres-integration")]
public sealed class ProductListShapePostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    [PostgreSqlFact]
    public async Task ListProducts_GivesTheSlimCardAndTheDetailKeepsEverything()
    {
        var now = fixture.Now;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"list-shape-{Guid.NewGuid():N}", DisplayName = "list-shape", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var connection = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = "TRENDYOL", Environment = "STAGE", DisplayName = "Trendyol list shape", ExternalStoreId = $"store-{Guid.NewGuid():N}", Status = "ACTIVE", ApiVersion = "V2", Version = 1 };
        var product = new Product { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Title = "Triko Kazak", Description = new string('x', 2000), Status = ProductStatus.Active, DefaultSalePrice = 400m, CreatedAt = now, UpdatedAt = now, Version = 1 };
        ProductVariant Variant(string size, int order)
        {
            var sku = $"LIST-{Guid.NewGuid():N}"[..20].ToUpperInvariant();
            return new ProductVariant { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProductId = product.Id, Sku = sku, SkuNormalized = sku, ModelCode = "LIST001", OptionSignature = $"Renk: Bej | Beden: {size}", SortOrder = order, CreatedAt = now, UpdatedAt = now, Version = 1 };
        }
        var small = Variant("S", 0);
        var medium = Variant("M", 1);
        var attributeId = Guid.CreateVersion7();
        var valueId = Guid.CreateVersion7();
        var colourOption = Guid.CreateVersion7();
        var bej = Guid.CreateVersion7();

        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        db.Tenants.Add(tenant);
        db.PlatformConnections.Add(connection);
        db.Products.Add(product);
        db.ProductVariants.AddRange(small, medium);
        db.AttributeDefinitions.Add(new AttributeDefinition { Id = attributeId, TenantId = tenant.Id, Code = $"MATERYAL-{Guid.NewGuid():N}", Name = "Materyal", DataType = AttributeDataType.SingleSelect, CreatedAt = now, UpdatedAt = now, Version = 1 });
        db.AttributeValues.Add(new AttributeValue { Id = valueId, TenantId = tenant.Id, AttributeId = attributeId, Value = "Yün", NormalizedValue = "YUN", SortOrder = 0, Version = 1 });
        db.ProductAttributeAssignments.Add(new ProductAttributeAssignment { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProductId = product.Id, AttributeId = attributeId, ValueId = valueId, SortOrder = 0, Version = 1 });
        db.ProductOptions.Add(new ProductOption { Id = colourOption, TenantId = tenant.Id, ProductId = product.Id, Label = "Renk", NormalizedKey = "RENK", SortOrder = 0 });
        db.ProductOptionValues.Add(new ProductOptionValue { Id = bej, TenantId = tenant.Id, OptionId = colourOption, Label = "Bej", NormalizedKey = "BEJ" });
        db.VariantOptionValues.AddRange(
            new VariantOptionValue { Id = Guid.CreateVersion7(), TenantId = tenant.Id, VariantId = small.Id, OptionId = colourOption, OptionValueId = bej },
            new VariantOptionValue { Id = Guid.CreateVersion7(), TenantId = tenant.Id, VariantId = medium.Id, OptionId = colourOption, OptionValueId = bej });
        // Both variants live on Trendyol, one closed to sale; each has an offer there.
        db.MarketplaceVariantLinks.AddRange(
            new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, VariantId = small.Id, ExternalId = small.Sku, SaleState = ListingSaleState.OnSale },
            new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, VariantId = medium.Id, ExternalId = medium.Sku, SaleState = ListingSaleState.OffSale, SaleStateReason = ListingSaleState.StockZero });
        foreach (var variant in new[] { small, medium })
            db.ChannelOffers.Add(new ChannelOffer { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, VariantId = variant.Id, ListPrice = 500m, SalePrice = 450m, Currency = "TRY", VatRate = 10m, VatInclusion = "INCLUDED", RoundingMode = "HALF_EVEN", Status = "ACTIVE", PriceVersion = 1, Version = 1 });
        string Picture(int index) => $"https://cdn.dsmcdn.com/list-shape/{index}.jpg";
        for (var index = 0; index < 3; index++)
        {
            var asset = new FileAsset { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Classification = "PRODUCT_MEDIA_URL", RelativePath = Picture(index), MimeType = "image/remote", Sha256 = Guid.NewGuid().ToString("N"), Status = "ACTIVE", CreatedAt = now };
            db.FileAssets.Add(asset);
            db.ProductMedia.Add(new ProductMedia { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProductId = product.Id, FileAssetId = asset.Id, MediaRole = index == 0 ? "PRIMARY" : "GALLERY", SortOrder = index, Status = "ACTIVE" });
            foreach (var variant in new[] { small, medium })
                db.ProductMedia.Add(new ProductMedia { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProductId = product.Id, VariantId = variant.Id, FileAssetId = asset.Id, MediaRole = index == 0 ? "PRIMARY" : "GALLERY", SortOrder = index, Status = "ACTIVE" });
        }
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var service = new CatalogService(db, null!, new ConfigurationBuilder().Build(), fixture.TimeProvider, new MemoryCache(new MemoryCacheOptions()));
        var detail = (await service.GetProductAsync(tenant.Id, product.Id, CancellationToken.None)).Value!;
        var card = Assert.Single((await service.ListProductsAsync(tenant.Id, 20, null, null, null, null, null, null, CancellationToken.None)).Items);

        // The editor's fields stay on the detail.
        Assert.Equal(product.Description, detail.Description);
        Assert.Single(detail.Attributes!);
        Assert.Single(detail.Options!);
        Assert.Equal(3, detail.MediaUrls!.Count);
        Assert.All(detail.Variants, variant => Assert.Single(variant.PlatformStatuses!));
        Assert.All(detail.Variants, variant => Assert.Equal(3, variant.MediaUrls!.Count));

        // The list card leaves them out ...
        Assert.Equal(string.Empty, card.Description);
        Assert.Null(card.Attributes);
        Assert.Null(card.Options);
        Assert.Null(card.MediaUrls);
        Assert.Null(card.SizeChartUrl);
        Assert.All(card.Variants, variant => Assert.Null(variant.PlatformStatuses));
        Assert.All(card.Variants, variant => Assert.True(variant is { OfferId: null, OfferVersion: null, PriceVersion: null, VatRate: null, VatInclusion: null, RoundingMode: null, OfferStatus: null, SafetyStock: null, InventoryVersion: null }));
        Assert.All(detail.Variants, variant => Assert.Equal((10m, "INCLUDED", "HALF_EVEN", "ACTIVE"), (variant.VatRate, variant.VatInclusion, variant.RoundingMode, variant.OfferStatus)));
        Assert.All(detail.Variants, variant => Assert.NotNull(variant.OfferId));
        Assert.All(card.Variants, variant => Assert.Equal([Picture(0)], variant.MediaUrls!));

        // ... and keeps what the list shows, equal to the detail: the platform marks with their sale state, the price,
        // stock, picture, model code, category and each variant's options, prices and versions.
        Assert.Equal(JsonSerializer.Serialize(detail.PlatformStatuses), JsonSerializer.Serialize(card.PlatformStatuses));
        var trendyol = Assert.Single(card.PlatformStatuses!);
        Assert.Equal((2, 2, 1, 1, 1), (trendyol.MatchedVariantCount, trendyol.VariantCount, trendyol.OnSaleVariantCount, trendyol.OffSaleVariantCount, trendyol.OffSaleStockZeroVariantCount));
        Assert.Equal((detail.Id, detail.Title, detail.Status, detail.Version, detail.PrimaryImageUrl, detail.TotalStock, detail.StartingPrice, detail.Currency, detail.ModelCode, detail.CategoryPath),
            (card.Id, card.Title, card.Status, card.Version, card.PrimaryImageUrl, card.TotalStock, card.StartingPrice, card.Currency, card.ModelCode, card.CategoryPath));
        Assert.Equal(Picture(0), card.PrimaryImageUrl);
        Assert.Equal(450m, card.StartingPrice);
        Assert.Equal(detail.ActivePlatforms, card.ActivePlatforms);
        foreach (var variant in card.Variants)
        {
            var full = detail.Variants.Single(x => x.Id == variant.Id);
            Assert.Equal((full.Sku, full.Barcode, full.ModelCode, full.OptionSignature, full.Status, full.Version, full.OnHand, full.ListPrice, full.SalePrice, full.Currency, full.ProductId),
                (variant.Sku, variant.Barcode, variant.ModelCode, variant.OptionSignature, variant.Status, variant.Version, variant.OnHand, variant.ListPrice, variant.SalePrice, variant.Currency, variant.ProductId));
            Assert.Equal(full.Options, variant.Options);
            Assert.Equal("Bej", variant.Options!["Renk"]);
        }

        // The answer leaves out its empty fields.
        var json = JsonSerializer.Serialize(new PageResult<ProductView>([card], null, false, 1), CatalogEndpoints.ProductListJson);
        Assert.DoesNotContain(":null", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"platformStatuses\":[{\"platform\":\"Trendyol list shape\",\"platformCode\"", json, StringComparison.Ordinal);
        Assert.Contains("\"platformStatuses\":[{\"platform\":\"Trendyol list shape\",\"status\":", json, StringComparison.Ordinal);
        Assert.Contains("\"description\":\"\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("nextCursor", json, StringComparison.Ordinal);
        Assert.Contains("\"hasMore\":false", json, StringComparison.Ordinal);
        await transaction.RollbackAsync();
    }
}
