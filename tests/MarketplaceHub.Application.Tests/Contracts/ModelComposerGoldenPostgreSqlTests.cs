using System.Text.Json;
using System.Text.RegularExpressions;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests.Contracts;

// KATALOG-14 phase D (safety net): every marketplace's create and update payload (and Trendyol's price/stock payload) for
// a seeded model is pinned in a golden file, so moving the composers onto the effective-value reader, and later turning
// the writers to the model, shows every byte that changes. Two models: "same" (three colours holding one name with
// their colour word and one description) and "differs" (two colours, the second with a name and description of its
// own, as 85 live models do). Ids and times are replaced by stable tokens; the golden is written only with
// MARKETPLACEHUB_UPDATE_GOLDENS=1 (ContractFixture).
[Collection("postgres-integration")]
public sealed partial class ModelComposerGoldenPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    private static readonly string[] Platforms = ["TRENDYOL", "HEPSIBURADA", "N11", "PAZARAMA", "PTTAVM", "SHOPIFY"];

    public static TheoryData<string> Models => new() { "same", "differs" };

    [PostgreSqlTheory]
    [MemberData(nameof(Models))]
    public async Task EveryComposerBuildsThePinnedPayload(string shape)
    {
        var seeded = await SeedAsync(fixture, shape);
        var payloads = await PayloadsAsync(fixture, seeded);
        Assert.Contains(payloads, x => x.StartsWith("TRENDYOL", StringComparison.Ordinal) && x.Contains(" publication: ", StringComparison.Ordinal) && x.Contains("\"Succeeded\":true", StringComparison.Ordinal));
        ContractFixture.MatchesGolden($"Catalog/model-composers-{shape}.golden.json", Normalize(payloads, seeded));
    }

    internal sealed record Seeded(Tenant Tenant, IReadOnlyList<Product> Cards, IReadOnlyList<PlatformConnection> Connections, DateTimeOffset Now);

    internal static async Task<Seeded> SeedAsync(PostgreSqlTenantIsolationFixture fixture, string shape)
    {
        var now = fixture.Now;
        // Ids that sort alike in .NET and PostgreSQL in the order they are made (a run-unique prefix, then a counter), so
        // the composers' ordering by id is the same on every run.
        var prefix = Guid.NewGuid().ToString("N")[..8];
        var counter = 0;
        Guid NextId() => Guid.Parse($"{prefix}-0000-7000-8000-{++counter:D12}");
        var tenant = new Tenant { Id = NextId(), Code = $"mgold-{Guid.NewGuid():N}"[..30], DisplayName = "model-golden", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var categoryId = NextId();
        var brandId = NextId();
        var fabricId = NextId();
        var cottonId = NextId();
        var differs = shape == "differs";
        var code = differs ? "MZ400" : "MZ300";
        Product Card(string colour, int minute, bool own) => new()
        {
            Id = NextId(),
            TenantId = tenant.Id,
            Title = own ? $"Kadın {colour} Kruvaze Yaka Özel Bluz" : $"Kadın {colour} Triko Bluz",
            Description = own ? $"{colour} renge özel uzun açıklama" : "Yumuşak triko bluz, uzun kollu.",
            CategoryId = categoryId,
            BrandId = brandId,
            VatRate = 10m,
            VatInclusion = "INCLUDED",
            WarrantyMonths = 24,
            DeliveryDays = 2,
            DefaultListPrice = 900m,
            DefaultSalePrice = 630m,
            Status = ProductStatus.Active,
            CreatedAt = now.AddMinutes(minute),
            UpdatedAt = now,
            Version = 1
        };
        Product[] cards = differs ? [Card("Siyah", 0, false), Card("Bordo", 1, true)] : [Card("Siyah", 0, false), Card("Bordo", 1, false), Card("Gri", 2, false)];
        string[] colours = differs ? ["Siyah", "Bordo"] : ["Siyah", "Bordo", "Gri"];
        var variants = cards.SelectMany((card, cardIndex) => new[] { "M", "L" }.Select((size, index) => new ProductVariant
        {
            Id = NextId(),
            TenantId = tenant.Id,
            ProductId = card.Id,
            SortOrder = index,
            Sku = $"{code}-{cardIndex}{size}",
            SkuNormalized = $"{code}-{cardIndex}{size}",
            Barcode = $"86900000{cardIndex}{index}11",
            BarcodeNormalized = $"86900000{cardIndex}{index}11",
            ModelCode = code,
            OptionSignature = $"Renk: {colours[cardIndex]} | Beden: {size}",
            Desi = 1m,
            CostPrice = 100m,
            DefaultListPrice = 900m,
            DefaultSalePrice = 630m,
            Status = ProductStatus.Active,
            CreatedAt = now,
            UpdatedAt = now,
            Version = 1,
        })).ToList();
        var connections = Platforms.Select(platform => new PlatformConnection { Id = NextId(), TenantId = tenant.Id, PublicId = NextId(), PlatformCode = platform, Environment = "PRODUCTION", DisplayName = platform, ExternalStoreId = $"store-{Guid.NewGuid():N}", Status = "ACTIVE", ApiVersion = "V1", SettingsJson = platform == "N11" ? """{"N11ShipmentTemplate":"Standart"}""" : "{}", Version = 1 }).ToList();
        // Pazarama's category and brand ids are GUIDs; the others take plain numbers.
        string CategoryRef(string platform) => platform == "PAZARAMA" ? "0f9b1b51-6a2f-4c55-9d4e-3a2c1e0b7a11" : "123";
        string BrandRef(string platform) => platform == "PAZARAMA" ? "5d0e8c2a-1f3b-4e6d-8a9c-7b1e2d3f4a55" : "456";

        await using var seed = fixture.CreateContext();
        seed.Tenants.Add(tenant);
        seed.Categories.Add(new Category { Id = categoryId, TenantId = tenant.Id, Name = "Bluz", NormalizedName = "BLUZ", Path = "Bluz", IsLeaf = true, IsActive = true, CreatedAt = now, UpdatedAt = now, Version = 1 });
        seed.Brands.Add(new Brand { Id = brandId, TenantId = tenant.Id, Name = "Ravencia", NormalizedName = "RAVENCIA", IsActive = true, CreatedAt = now, UpdatedAt = now, Version = 1 });
        seed.AttributeDefinitions.Add(new AttributeDefinition { Id = fabricId, TenantId = tenant.Id, Code = "KUMAS", Name = "Kumaş", DataType = AttributeDataType.SingleSelect, CreatedAt = now, UpdatedAt = now, Version = 1 });
        seed.AttributeValues.Add(new AttributeValue { Id = cottonId, TenantId = tenant.Id, AttributeId = fabricId, Value = "Pamuk", NormalizedValue = "PAMUK", Version = 1 });
        seed.PlatformConnections.AddRange(connections);
        seed.Products.AddRange(cards);
        seed.ProductVariants.AddRange(variants);
        foreach (var card in cards)
            seed.ProductAttributeAssignments.Add(new ProductAttributeAssignment { Id = NextId(), TenantId = tenant.Id, ProductId = card.Id, AttributeId = fabricId, ValueId = cottonId, Version = 1 });
        foreach (var (card, index) in cards.Select((card, index) => (card, index)))
        {
            var asset = new FileAsset { Id = NextId(), TenantId = tenant.Id, Classification = "PRODUCT_MEDIA_URL", RelativePath = $"https://cdn.example.com/{code}/{index}.jpg", MimeType = "image/jpeg", Sha256 = $"{index:D2}{new string('a', 62)}", Status = "ACTIVE", CreatedAt = now };
            seed.FileAssets.Add(asset);
            seed.ProductMedia.Add(new ProductMedia { Id = NextId(), TenantId = tenant.Id, ProductId = card.Id, FileAssetId = asset.Id, MediaRole = "ORDERED_PRIMARY", SortOrder = 0, Status = "ACTIVE" });
            seed.ProductSizeCharts.Add(new ProductSizeChart { Id = NextId(), TenantId = tenant.Id, ProductId = card.Id, MeasurementsJson = """{"columns":["Boy"],"rows":[{"size":"M","values":["70"]}]}""", UpdatedAt = now });
        }
        foreach (var connection in connections)
        {
            ReferenceSnapshot Snapshot(string type, string scope) => new() { Id = NextId(), TenantId = tenant.Id, ConnectionId = connection.Id, ResourceType = type, ScopeExternalId = scope, SourceVersion = type, ContentHash = $"hash-{type}", FetchedAt = now, IsCurrent = true, ItemCount = 1 };
            ReferenceItem Item(Guid snapshotId, string type, string externalId, string name) => new() { Id = NextId(), TenantId = tenant.Id, ConnectionId = connection.Id, SnapshotId = snapshotId, ResourceType = type, ExternalId = externalId, Name = name, NormalizedName = name.ToUpperInvariant(), Path = name, Depth = 0, IsLeaf = true, IsActive = true, PayloadHash = $"payload-{type}-{externalId}" };
            var categories = Snapshot("CATEGORIES", "");
            var brands = Snapshot("BRANDS", "");
            var categoryRef = CategoryRef(connection.PlatformCode);
            var brandRef = BrandRef(connection.PlatformCode);
            seed.ReferenceSnapshots.AddRange(categories, brands, Snapshot("CATEGORY_ATTRIBUTES", categoryRef));
            seed.ReferenceItems.AddRange(Item(categories.Id, "CATEGORIES", categoryRef, "Bluz"), Item(brands.Id, "BRANDS", brandRef, "Ravencia"));
            seed.CategoryMappings.Add(new CategoryMapping { Id = NextId(), TenantId = tenant.Id, ConnectionId = connection.Id, SnapshotId = categories.Id, LocalId = categoryId, ExternalId = categoryRef, Status = "VERIFIED", VerifiedAt = now, Version = 1 });
            seed.BrandMappings.Add(new BrandMapping { Id = NextId(), TenantId = tenant.Id, ConnectionId = connection.Id, SnapshotId = brands.Id, LocalId = brandId, ExternalId = brandRef, Status = "VERIFIED", VerifiedAt = now, Version = 1 });
            foreach (var card in cards)
            {
                var profile = new ChannelListingProfile { Id = NextId(), TenantId = tenant.Id, ConnectionId = connection.Id, ProductId = card.Id, Enabled = true, DesiredStatus = "ACTIVE", ActualStatus = "DRAFT", Version = 1 };
                seed.ChannelListingProfiles.Add(profile);
                // A publication the update composers address: every variant listed under the marketplace's own codes.
                foreach (var variant in variants.Where(x => x.ProductId == card.Id))
                    seed.ChannelListingVariants.Add(new ChannelListingVariant { Id = NextId(), TenantId = tenant.Id, ProfileId = profile.Id, VariantId = variant.Id, ExternalSku = variant.Sku, ExternalBarcode = variant.Barcode, DesiredStatus = "ACTIVE", ActualStatus = "LIVE" });
            }
            // Hepsiburada addresses a variant by its HB code, n11 by its stock code.
            if (connection.PlatformCode is "HEPSIBURADA" or "N11")
                foreach (var variant in variants)
                    seed.MarketplaceVariantLinks.Add(new MarketplaceVariantLink { Id = NextId(), TenantId = tenant.Id, ConnectionId = connection.Id, VariantId = variant.Id, ExternalId = connection.PlatformCode == "N11" ? variant.Sku : $"HB{variant.Barcode}", ExternalBarcode = variant.Barcode, ExternalSku = variant.Sku, LastSeenAt = now });
            foreach (var variant in variants)
                seed.ChannelOffers.Add(new ChannelOffer { Id = NextId(), TenantId = tenant.Id, ConnectionId = connection.Id, VariantId = variant.Id, ListPrice = 900m, SalePrice = 630m, Currency = "TRY", VatRate = 10m, VatInclusion = "INCLUDED", RoundingMode = "HALF_EVEN", Status = "ACTIVE", PriceVersion = 1, Version = 1 });
        }
        foreach (var variant in variants)
            seed.InventoryItems.Add(new InventoryItem { Id = NextId(), TenantId = tenant.Id, VariantId = variant.Id, LocationCode = "MAIN", OnHand = 5m, Available = 5m, ProjectionVersion = 1, Version = 1 });
        await seed.SaveChangesAsync();
        return new Seeded(tenant, cards, connections, now);
    }

    // Every composer of every marketplace for every colour card, in a fixed order.
    internal static async Task<List<string>> PayloadsAsync(PostgreSqlTenantIsolationFixture fixture, Seeded seeded)
    {
        await using var db = fixture.CreateContext();
        var configuration = new ConfigurationBuilder().Build();
        var payloads = new List<string>();
        var tenantId = seeded.Tenant.Id;
        void Add(string what, object result) => payloads.Add($"{what}: {JsonSerializer.Serialize(result)}");
        foreach (var connection in seeded.Connections)
            foreach (var (card, index) in seeded.Cards.Select((card, index) => (card, index)))
            {
                var key = $"{connection.PlatformCode} card{index}";
                switch (connection.PlatformCode)
                {
                    case "TRENDYOL":
                        Add($"{key} publication", await new ProductPublicationComposer(db, configuration).BuildAsync(tenantId, card.Id, connection.Id, CancellationToken.None));
                        Add($"{key} update", await new ProductUpdateComposer(db, configuration).BuildAsync(tenantId, card.Id, connection.Id, CancellationToken.None));
                        Add($"{key} price-stock", await new PriceInventoryComposer(db).BuildAsync(tenantId, connection.Id, CancellationToken.None, productId: card.Id));
                        break;
                    case "HEPSIBURADA":
                        Add($"{key} publication", await new HepsiburadaProductPublicationComposer(db, configuration).BuildAsync(tenantId, card.Id, connection.Id, CancellationToken.None));
                        Add($"{key} update", await new HepsiburadaProductUpdateComposer(db, configuration).BuildAsync(tenantId, card.Id, connection.Id, CancellationToken.None));
                        break;
                    case "N11":
                        Add($"{key} publication", await new N11ProductPublicationComposer(db, configuration).BuildAsync(tenantId, card.Id, connection.Id, CancellationToken.None));
                        Add($"{key} update", await new N11ProductUpdateComposer(db, configuration).BuildAsync(tenantId, card.Id, connection.Id, null, CancellationToken.None));
                        break;
                    case "PAZARAMA":
                        Add($"{key} publication", await new PazaramaProductPublicationComposer(db, configuration).BuildAsync(tenantId, card.Id, connection.Id, CancellationToken.None));
                        Add($"{key} update", await new PazaramaProductUpdateComposer(db).BuildAsync(tenantId, card.Id, connection.Id, CancellationToken.None));
                        break;
                    case "PTTAVM":
                        Add($"{key} publication", await new PttAvmProductPublicationComposer(db, configuration).BuildAsync(tenantId, card.Id, connection.Id, CancellationToken.None));
                        Add($"{key} update", await new PttAvmProductUpdateComposer(db, configuration).BuildAsync(tenantId, card.Id, connection.Id, [.. PttAvmProductUpdateFields.Supported.Order(StringComparer.Ordinal)], CancellationToken.None));
                        break;
                    case "SHOPIFY":
                        Add($"{key} publication", await new ShopifyProductPublicationComposer(db, configuration).BuildAsync(tenantId, card.Id, connection.Id, CancellationToken.None));
                        break;
                }
            }
        return payloads;
    }

    // Ids become tokens in order of first appearance; the seeding time and the per-run store ids become fixed words.
    internal static List<string> Normalize(IReadOnlyList<string> payloads, Seeded seeded)
    {
        var tokens = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string Token(string id)
        {
            var key = id.Replace("-", "", StringComparison.Ordinal);
            if (!tokens.TryGetValue(key, out var token)) tokens[key] = token = $"<id{tokens.Count}>";
            return token;
        }
        var stamps = new[] { seeded.Now, seeded.Now.UtcDateTime }.SelectMany(at => new[] { JsonSerializer.Serialize(at).Trim('"'), JsonSerializer.Serialize(at.ToUniversalTime()).Trim('"') }).Distinct().ToList();
        return [.. payloads.Select(payload =>
        {
            foreach (var stamp in stamps) payload = payload.Replace(stamp, "<now>", StringComparison.Ordinal);
            // A payload hash covers the per-run store id (Hepsiburada's merchant): the payload itself is pinned instead.
            payload = HashPattern().Replace(payload, "<hash>");
            // Shopify's handle carries a hash of the per-run ids.
            payload = HandlePattern().Replace(payload, "-model-<handle>");
            return GuidPattern().Replace(payload, match => Token(match.Value));
        })];
    }

    // A hash stands alone or right after an escaped quote (", whose digits are hex too).
    [GeneratedRegex("(?:(?<![0-9a-fA-F])|(?<=u0022))[0-9a-fA-F]{64}(?![0-9a-fA-F])")]
    private static partial Regex HashPattern();

    [GeneratedRegex("-model-[0-9a-f]{20}")]
    private static partial Regex HandlePattern();

    [GeneratedRegex("[0-9a-fA-F]{8}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{12}")]
    private static partial Regex GuidPattern();
}
