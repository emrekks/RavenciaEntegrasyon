using System.Text.Json;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// KATALOG-14 phase B: what the marketplaces receive does not depend on the model record. Every publication, update and
// price/stock composer of every marketplace builds a seeded three-colour model byte for byte the same with the record as
// the write-through left it, with every model column, attribute and chart scrambled, and after the backfill rewrote it.
// The write-through and the backfill write only the record (never a card or variant), so the payloads before this
// release and after it are the same.
[Collection("postgres-integration")]
public sealed class ModelRecordComposerEqualityPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    private static readonly string[] Platforms = ["TRENDYOL", "HEPSIBURADA", "N11", "PAZARAMA", "PTTAVM", "SHOPIFY"];

    [PostgreSqlFact]
    public async Task EveryComposer_BuildsTheSamePayload_WhateverTheModelRecordHolds()
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"composer-eq-{Guid.NewGuid():N}", DisplayName = "composer-eq", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var categoryId = Guid.CreateVersion7();
        var brandId = Guid.CreateVersion7();
        var fabricId = Guid.CreateVersion7();
        var cottonId = Guid.CreateVersion7();
        Product Card(string colour, int minute) => new() { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Title = $"Kadın {colour} Triko Bluz", Description = $"{colour} açıklama", CategoryId = categoryId, BrandId = brandId, VatRate = 10m, VatInclusion = "INCLUDED", WarrantyMonths = 24, DeliveryDays = 2, DefaultListPrice = 900m, DefaultSalePrice = 630m, Status = ProductStatus.Active, CreatedAt = now.AddMinutes(minute), UpdatedAt = now, Version = 1 };
        Product[] cards = [Card("Siyah", 0), Card("Bordo", 1), Card("Gri", 2)];
        var variants = cards.SelectMany((card, cardIndex) => new[] { "M", "L" }.Select((size, index) => new ProductVariant
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            ProductId = card.Id,
            SortOrder = index,
            Sku = $"MZ300-{cardIndex}{size}",
            SkuNormalized = $"MZ300-{cardIndex}{size}",
            Barcode = $"86900000{cardIndex}{index}11",
            BarcodeNormalized = $"86900000{cardIndex}{index}11",
            ModelCode = "MZ300",
            OptionSignature = $"Renk: {card.Title.Split(' ')[1]} | Beden: {size}",
            Desi = cardIndex == 2 ? 2m : 1m,
            CostPrice = 100m,
            DefaultListPrice = 900m,
            DefaultSalePrice = 630m,
            Status = ProductStatus.Active,
            CreatedAt = now,
            UpdatedAt = now,
            Version = 1,
        })).ToList();
        var connections = Platforms.Select(code => new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = code, Environment = "PRODUCTION", DisplayName = code, ExternalStoreId = $"store-{Guid.NewGuid():N}", Status = "ACTIVE", ApiVersion = "V1", SettingsJson = "{}", Version = 1 }).ToList();

        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.Add(tenant);
            seed.Categories.Add(new Category { Id = categoryId, TenantId = tenant.Id, Name = "Bluz", NormalizedName = "BLUZ", Path = "Bluz", IsLeaf = true, IsActive = true, CreatedAt = now, UpdatedAt = now, Version = 1 });
            seed.Brands.Add(new Brand { Id = brandId, TenantId = tenant.Id, Name = "Ravencia", NormalizedName = "RAVENCIA", IsActive = true, CreatedAt = now, UpdatedAt = now, Version = 1 });
            seed.AttributeDefinitions.Add(new AttributeDefinition { Id = fabricId, TenantId = tenant.Id, Code = "KUMAS", Name = "Kumaş", DataType = AttributeDataType.SingleSelect, CreatedAt = now, UpdatedAt = now, Version = 1 });
            seed.AttributeValues.Add(new AttributeValue { Id = cottonId, TenantId = tenant.Id, AttributeId = fabricId, Value = "Pamuk", NormalizedValue = "PAMUK", Version = 1 });
            seed.PlatformConnections.AddRange(connections);
            seed.Products.AddRange(cards);
            seed.ProductVariants.AddRange(variants);
            foreach (var card in cards)
                seed.ProductAttributeAssignments.Add(new ProductAttributeAssignment { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProductId = card.Id, AttributeId = fabricId, ValueId = cottonId, Version = 1 });
            foreach (var (card, index) in cards.Select((card, index) => (card, index)))
            {
                var asset = new FileAsset { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Classification = "PRODUCT_MEDIA_URL", RelativePath = $"https://cdn.example.com/mz300/{index}.jpg", MimeType = "image/jpeg", Sha256 = Guid.NewGuid().ToString("N"), Status = "ACTIVE", CreatedAt = now };
                seed.FileAssets.Add(asset);
                seed.ProductMedia.Add(new ProductMedia { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProductId = card.Id, FileAssetId = asset.Id, MediaRole = "ORDERED_PRIMARY", SortOrder = 0, Status = "ACTIVE" });
                seed.ProductSizeCharts.Add(new ProductSizeChart { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProductId = card.Id, MeasurementsJson = """{"columns":["Boy"],"rows":[{"size":"M","values":["70"]}]}""", UpdatedAt = now });
            }
            foreach (var connection in connections)
            {
                ReferenceSnapshot Snapshot(string type, string scope) => new() { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, ResourceType = type, ScopeExternalId = scope, SourceVersion = type, ContentHash = $"hash-{type}", FetchedAt = now, IsCurrent = true, ItemCount = 1 };
                ReferenceItem Item(Guid snapshotId, string type, string externalId, string name) => new() { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, SnapshotId = snapshotId, ResourceType = type, ExternalId = externalId, Name = name, NormalizedName = name.ToUpperInvariant(), Path = name, Depth = 0, IsLeaf = true, IsActive = true, PayloadHash = $"payload-{type}-{externalId}" };
                var categories = Snapshot("CATEGORIES", "");
                var brands = Snapshot("BRANDS", "");
                seed.ReferenceSnapshots.AddRange(categories, brands, Snapshot("CATEGORY_ATTRIBUTES", "123"));
                seed.ReferenceItems.AddRange(Item(categories.Id, "CATEGORIES", "123", "Bluz"), Item(brands.Id, "BRANDS", "456", "Ravencia"));
                seed.CategoryMappings.Add(new CategoryMapping { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, SnapshotId = categories.Id, LocalId = categoryId, ExternalId = "123", Status = "VERIFIED", VerifiedAt = now, Version = 1 });
                seed.BrandMappings.Add(new BrandMapping { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, SnapshotId = brands.Id, LocalId = brandId, ExternalId = "456", Status = "VERIFIED", VerifiedAt = now, Version = 1 });
                foreach (var card in cards)
                    seed.ChannelListingProfiles.Add(new ChannelListingProfile { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, ProductId = card.Id, Enabled = true, DesiredStatus = "ACTIVE", ActualStatus = "DRAFT", Version = 1 });
                foreach (var variant in variants)
                    seed.ChannelOffers.Add(new ChannelOffer { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, VariantId = variant.Id, ListPrice = 900m, SalePrice = 630m, Currency = "TRY", VatRate = 10m, VatInclusion = "INCLUDED", RoundingMode = "HALF_EVEN", Status = "ACTIVE", PriceVersion = 1, Version = 1 });
            }
            foreach (var variant in variants)
                seed.InventoryItems.Add(new InventoryItem { Id = Guid.CreateVersion7(), TenantId = tenant.Id, VariantId = variant.Id, LocationCode = "MAIN", OnHand = 5m, Available = 5m, ProjectionVersion = 1, Version = 1 });
            await seed.SaveChangesAsync();
        }

        async Task<List<string>> PayloadsAsync()
        {
            await using var db = fixture.CreateContext();
            var configuration = new ConfigurationBuilder().Build();
            var payloads = new List<string>();
            void Add(string what, object result) => payloads.Add($"{what}: {JsonSerializer.Serialize(result)}");
            foreach (var connection in connections)
            {
                foreach (var card in cards)
                {
                    var key = $"{connection.PlatformCode} {card.Title}";
                    switch (connection.PlatformCode)
                    {
                        case "TRENDYOL":
                            Add($"{key} publication", await new ProductPublicationComposer(db, configuration).BuildAsync(tenant.Id, card.Id, connection.Id, CancellationToken.None));
                            Add($"{key} update", await new ProductUpdateComposer(db, configuration).BuildAsync(tenant.Id, card.Id, connection.Id, CancellationToken.None));
                            Add($"{key} price-stock", await new PriceInventoryComposer(db).BuildAsync(tenant.Id, connection.Id, CancellationToken.None, productId: card.Id));
                            break;
                        case "HEPSIBURADA":
                            Add($"{key} publication", await new HepsiburadaProductPublicationComposer(db, configuration).BuildAsync(tenant.Id, card.Id, connection.Id, CancellationToken.None));
                            Add($"{key} update", await new HepsiburadaProductUpdateComposer(db, configuration).BuildAsync(tenant.Id, card.Id, connection.Id, CancellationToken.None));
                            break;
                        case "N11":
                            Add($"{key} publication", await new N11ProductPublicationComposer(db, configuration).BuildAsync(tenant.Id, card.Id, connection.Id, CancellationToken.None));
                            Add($"{key} update", await new N11ProductUpdateComposer(db, configuration).BuildAsync(tenant.Id, card.Id, connection.Id, null, CancellationToken.None));
                            break;
                        case "PAZARAMA":
                            Add($"{key} publication", await new PazaramaProductPublicationComposer(db, configuration).BuildAsync(tenant.Id, card.Id, connection.Id, CancellationToken.None));
                            Add($"{key} update", await new PazaramaProductUpdateComposer(db).BuildAsync(tenant.Id, card.Id, connection.Id, CancellationToken.None));
                            break;
                        case "PTTAVM":
                            Add($"{key} publication", await new PttAvmProductPublicationComposer(db, configuration).BuildAsync(tenant.Id, card.Id, connection.Id, CancellationToken.None));
                            Add($"{key} update", await new PttAvmProductUpdateComposer(db, configuration).BuildAsync(tenant.Id, card.Id, connection.Id, [.. ProductUpdateFields.Known.Order(StringComparer.Ordinal)], CancellationToken.None));
                            break;
                        case "SHOPIFY":
                            Add($"{key} publication", await new ShopifyProductPublicationComposer(db, configuration).BuildAsync(tenant.Id, card.Id, connection.Id, CancellationToken.None));
                            break;
                    }
                }
            }
            return payloads;
        }

        var written = await PayloadsAsync();
        // Not vacuous: the Trendyol payload of every colour is built (the others build or refuse alike each time).
        Assert.Equal(3, written.Count(x => x.StartsWith("TRENDYOL", StringComparison.Ordinal) && x.Contains(" publication: ", StringComparison.Ordinal) && x.Contains("\"Succeeded\":true", StringComparison.Ordinal)));
        await using (var db = fixture.CreateContext())
        {
            var model = await db.ProductModels.AsNoTracking().SingleAsync(x => x.TenantId == tenant.Id);
            Assert.Equal(1m, model.Desi);
            Assert.True(await db.ProductModelSizeCharts.AsNoTracking().AnyAsync(x => x.ModelId == model.Id));
            Assert.True(await db.ProductModelAttributes.AsNoTracking().AnyAsync(x => x.ModelId == model.Id));

            await db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE catalog.product_models SET "Title" = 'Bozuk', "TitleColor" = NULL, "Description" = 'Bozuk', "VatRate" = 1, "VatInclusion" = 'EXCLUDED', "WarrantyMonths" = 1, "DeliveryDays" = 9, "Desi" = 77 WHERE "TenantId" = {tenant.Id}""");
            await db.Database.ExecuteSqlInterpolatedAsync($"""DELETE FROM catalog.product_model_attributes WHERE "TenantId" = {tenant.Id}""");
            await db.Database.ExecuteSqlInterpolatedAsync($$"""UPDATE catalog.product_model_size_charts SET "MeasurementsJson" = '{"columns":["X"],"rows":[]}' WHERE "TenantId" = {{tenant.Id}}""");
        }
        var scrambled = await PayloadsAsync();
        for (var index = 0; index < written.Count; index++) Assert.Equal(written[index], scrambled[index]);

        await using (var db = fixture.CreateContext())
            Assert.Equal(1, (await ProductModelValues.RefreshTenantAsync(db, tenant.Id, DateTimeOffset.UtcNow, CancellationToken.None)).Written);
        var backfilled = await PayloadsAsync();
        for (var index = 0; index < written.Count; index++) Assert.Equal(written[index], backfilled[index]);
    }
}
