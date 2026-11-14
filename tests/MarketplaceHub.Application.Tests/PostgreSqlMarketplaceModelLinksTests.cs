using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Model Kaydı Faz 2 (Shopify): a model's Shopify product is recorded from the variant links an import or match run saved,
// only when the product's variants are one model's and that model is not split over several Shopify products; card and
// variant links never change; cards without a model (KENE01) record nothing. The migration's backfill starts from the
// card-level product links, and the model fields the editor never saved are recomputed by the editor's rule.
[Collection("postgres-integration")]
public sealed class PostgreSqlMarketplaceModelLinksTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    private sealed record Seed(Tenant Tenant, PlatformConnection Shopify, Guid Ravencia, Guid Lavian);

    private static Seed AddSeed(AppDbContext db)
    {
        var now = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"model-links-{Guid.NewGuid():N}", DisplayName = "model links", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var shopify = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = "SHOPIFY", Environment = "PRODUCTION", DisplayName = "Shopify", ExternalStoreId = $"store-{Guid.NewGuid():N}", Status = "ACTIVE", ApiVersion = "V1", SettingsJson = "{}", Version = 1 };
        var seed = new Seed(tenant, shopify, Guid.CreateVersion7(), Guid.CreateVersion7());
        db.Tenants.Add(tenant);
        db.PlatformConnections.Add(shopify);
        db.Brands.Add(new Brand { Id = seed.Ravencia, TenantId = tenant.Id, Name = "Ravencia", NormalizedName = "RAVENCIA", IsActive = true, CreatedAt = now, UpdatedAt = now, Version = 1 });
        db.Brands.Add(new Brand { Id = seed.Lavian, TenantId = tenant.Id, Name = "Lavian", NormalizedName = "LAVIAN", IsActive = true, CreatedAt = now, UpdatedAt = now, Version = 1 });
        return seed;
    }

    // A colour card with two sizes, each size linked to the Shopify variant "<prefix>-<size>".
    private static Product AddCard(AppDbContext db, Seed seed, Guid brandId, string modelCode, string colour, string title, int minute, string? shopifyVariantPrefix)
    {
        var now = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var card = new Product { Id = Guid.CreateVersion7(), TenantId = seed.Tenant.Id, Title = title, Description = "Model açıklaması", BrandId = brandId, VatRate = 10m, VatInclusion = "INCLUDED", DeliveryDays = 1, Status = ProductStatus.Active, CreatedAt = now.AddMinutes(minute), UpdatedAt = now, Version = 1 };
        db.Products.Add(card);
        var index = 0;
        foreach (var size in new[] { "M", "L" })
        {
            var sku = $"{modelCode}-{colour}-{size}-{Guid.NewGuid():N}"[..40];
            var variant = new ProductVariant { Id = Guid.CreateVersion7(), TenantId = seed.Tenant.Id, ProductId = card.Id, Sku = sku, SkuNormalized = sku.ToUpperInvariant(), ModelCode = modelCode, OptionSignature = $"Renk: {colour} | Beden: {size}", Desi = 1m, Status = ProductStatus.Active, SortOrder = index++, CreatedAt = now, UpdatedAt = now, Version = 1 };
            db.ProductVariants.Add(variant);
            if (shopifyVariantPrefix is not null)
                db.MarketplaceVariantLinks.Add(new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = seed.Tenant.Id, ConnectionId = seed.Shopify.Id, VariantId = variant.Id, ExternalId = $"{shopifyVariantPrefix}-{colour}-{size}", Version = 1 });
        }
        return card;
    }

    private static IReadOnlyCollection<string> Variants(string prefix, params string[] colours) => [.. colours.SelectMany(colour => new[] { $"{prefix}-{colour}-M", $"{prefix}-{colour}-L" })];

    private static Task<List<MarketplaceModelLink>> ModelLinksAsync(AppDbContext db, Seed seed) =>
        db.MarketplaceModelLinks.AsNoTracking().Where(x => x.TenantId == seed.Tenant.Id).ToListAsync();

    [PostgreSqlFact]
    public async Task AShopifyProductOfOneModel_IsRecordedAsTheModelsListing_OnlyWhenUnambiguous()
    {
        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var seed = AddSeed(db);
        // MZ035YNC: two colour cards, one Shopify product.
        var siyah = AddCard(db, seed, seed.Ravencia, "MZ035YNC", "Siyah", "Kadın Siyah Bluz", 0, "gid-1");
        var mavi = AddCard(db, seed, seed.Ravencia, "MZ035YNC", "Mavi", "Kadın Mavi Bluz", 1, "gid-1");
        // KENE01: one code under two brands, no model.
        AddCard(db, seed, seed.Lavian, "KENE01", "Mavi", "Kadın Mavi Etek", 2, "gid-2");
        AddCard(db, seed, seed.Ravencia, "KENE01", "Siyah", "Kadın Siyah Etek", 3, "gid-2");
        // MZ040: listed as one Shopify product per colour.
        AddCard(db, seed, seed.Ravencia, "MZ040", "Gri", "Kadın Gri Etek", 4, "gid-3");
        AddCard(db, seed, seed.Ravencia, "MZ040", "Bej", "Kadın Bej Etek", 5, "gid-4");
        await db.SaveChangesAsync();
        var variantLinksBefore = await db.MarketplaceVariantLinks.AsNoTracking().Where(x => x.TenantId == seed.Tenant.Id).OrderBy(x => x.Id).Select(x => new { x.Id, x.VariantId, x.ExternalId, x.Version }).ToListAsync();

        var now = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await MarketplaceModelLinks.RecordAsync(db, seed.Tenant.Id, seed.Shopify.Id,
            [("7001", Variants("gid-1", "Siyah", "Mavi")), ("7002", Variants("gid-2", "Mavi", "Siyah")), ("7003", Variants("gid-3", "Gri")), ("7004", Variants("gid-4", "Bej"))],
            now, CancellationToken.None);
        var model = (await db.Products.AsNoTracking().SingleAsync(x => x.Id == siyah.Id)).FamilyId;
        Assert.Equal(model, (await db.Products.AsNoTracking().SingleAsync(x => x.Id == mavi.Id)).FamilyId);
        var link = Assert.Single(await ModelLinksAsync(db, seed));
        Assert.Equal((model, "7001", (DateTimeOffset?)now), ((Guid?)link.ModelId, link.ExternalId, link.LastSeenAt));

        // A later run that reads another product for the same model does not move the link; the same product refreshes it.
        await MarketplaceModelLinks.RecordAsync(db, seed.Tenant.Id, seed.Shopify.Id, [("7999", Variants("gid-1", "Siyah"))], now.AddHours(1), CancellationToken.None);
        await MarketplaceModelLinks.RecordAsync(db, seed.Tenant.Id, seed.Shopify.Id, [("7001", Variants("gid-1", "Siyah", "Mavi"))], now.AddHours(2), CancellationToken.None);
        link = Assert.Single(await ModelLinksAsync(db, seed));
        Assert.Equal(("7001", (DateTimeOffset?)now.AddHours(2)), (link.ExternalId, link.LastSeenAt));
        // The variant links and the absence of a card-level product link are as they were.
        Assert.Equal(variantLinksBefore, await db.MarketplaceVariantLinks.AsNoTracking().Where(x => x.TenantId == seed.Tenant.Id).OrderBy(x => x.Id).Select(x => new { x.Id, x.VariantId, x.ExternalId, x.Version }).ToListAsync());
        Assert.False(await db.MarketplaceProductLinks.AsNoTracking().AnyAsync(x => x.TenantId == seed.Tenant.Id));
        await transaction.RollbackAsync();
    }

    [PostgreSqlFact]
    public async Task TheBackfill_StartsFromASingleColourProductLink_AndRunsTwiceAlike()
    {
        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var seed = AddSeed(db);
        // One-colour model with its Shopify product link.
        var single = AddCard(db, seed, seed.Ravencia, "MZ060", "Siyah", "Kadın Siyah Elbise", 0, "gid-6");
        // A model with a product link on one card while another card holds Shopify variants of another product.
        var linked = AddCard(db, seed, seed.Ravencia, "MZ061", "Siyah", "Kadın Siyah Ceket", 1, "gid-7");
        AddCard(db, seed, seed.Ravencia, "MZ061", "Mavi", "Kadın Mavi Ceket", 2, "gid-8");
        // KENE01 card with a product link: no model, no model link.
        var kene = AddCard(db, seed, seed.Lavian, "KENE01", "Mavi", "Kadın Mavi Etek", 3, "gid-9");
        AddCard(db, seed, seed.Ravencia, "KENE01", "Siyah", "Kadın Siyah Etek", 4, null);
        foreach (var (card, external) in new[] { (single, "8001"), (linked, "8002"), (kene, "8003") })
            db.MarketplaceProductLinks.Add(new MarketplaceProductLink { Id = Guid.CreateVersion7(), TenantId = seed.Tenant.Id, ConnectionId = seed.Shopify.Id, ProductId = card.Id, ExternalId = external, SyncStatus = "SYNCED", Version = 1 });
        await db.SaveChangesAsync();

        await db.Database.ExecuteSqlRawAsync(MarketplaceModelLinks_Backfill);
        await db.Database.ExecuteSqlRawAsync(MarketplaceModelLinks_Backfill);
        var link = Assert.Single(await ModelLinksAsync(db, seed));
        Assert.Equal((await db.Products.AsNoTracking().Where(x => x.Id == single.Id).Select(x => x.FamilyId).SingleAsync(), "8001"), ((Guid?)link.ModelId, link.ExternalId));
        Assert.Equal(3, await db.MarketplaceProductLinks.AsNoTracking().CountAsync(x => x.TenantId == seed.Tenant.Id));
        await transaction.RollbackAsync();
    }

    private static string MarketplaceModelLinks_Backfill => Infrastructure.Persistence.Migrations.MarketplaceModelLinks.BackfillSql;

    [PostgreSqlFact]
    public async Task TheFieldsTheEditorNeverSaved_AreRecomputedByTheEditorsRule_SavedOnesStay()
    {
        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var seed = AddSeed(db);
        AddCard(db, seed, seed.Ravencia, "MZ070", "Gri", "Kadın Gri Bluz", 0, null);
        AddCard(db, seed, seed.Ravencia, "MZ070", "Lacivert", "Kadın Lacivert Bluz", 1, null);
        AddCard(db, seed, seed.Ravencia, "MZ071", "Siyah", "Kadın Siyah Etek", 2, null);
        await db.SaveChangesAsync();
        // What the backfill's SQL may leave (a whole-title majority) and an editor-saved model.
        await db.ProductModels.Where(x => x.TenantId == seed.Tenant.Id && x.ModelCodeKey == "MZ070").ExecuteUpdateAsync(s => s.SetProperty(x => x.Title, "Eski").SetProperty(x => x.TitleColor, (string?)null));
        var saved = DateTimeOffset.UtcNow;
        await db.ProductModels.Where(x => x.TenantId == seed.Tenant.Id && x.ModelCodeKey == "MZ071").ExecuteUpdateAsync(s => s.SetProperty(x => x.Title, "Editörün adı").SetProperty(x => x.FieldsSavedAt, saved));

        Assert.True(await ProductModelLinks.RecomputeUnsavedFieldsAsync(db, CancellationToken.None) >= 1);
        var models = await db.ProductModels.AsNoTracking().Where(x => x.TenantId == seed.Tenant.Id).ToDictionaryAsync(x => x.ModelCodeKey);
        // The oldest card's name on a tie, with its colour.
        Assert.Equal(("Kadın Gri Bluz", "Gri"), (models["MZ070"].Title, models["MZ070"].TitleColor));
        Assert.Null(models["MZ070"].FieldsSavedAt);
        Assert.Equal("Editörün adı", models["MZ071"].Title);
        // Again: nothing of this tenant to write.
        var version = models["MZ070"].Version;
        await ProductModelLinks.RecomputeUnsavedFieldsAsync(db, CancellationToken.None);
        Assert.Equal(version, (await db.ProductModels.AsNoTracking().SingleAsync(x => x.Id == models["MZ070"].Id)).Version);
        await transaction.RollbackAsync();
    }

    private static PlatformConnection AddConnection(AppDbContext db, Seed seed, string platform)
    {
        var connection = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = seed.Tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = platform, Environment = "PRODUCTION", DisplayName = platform, ExternalStoreId = $"store-{Guid.NewGuid():N}", Status = "ACTIVE", ApiVersion = "V1", SettingsJson = "{}", Version = 1 };
        db.PlatformConnections.Add(connection);
        return connection;
    }

    // The card's variant links on the Shopify connection moved to another connection (the seed links Shopify only).
    private static async Task MoveVariantLinksAsync(AppDbContext db, Seed seed, Product card, PlatformConnection connection) =>
        await db.MarketplaceVariantLinks.Where(x => x.TenantId == seed.Tenant.Id && x.ConnectionId == seed.Shopify.Id && db.ProductVariants.Any(v => v.Id == x.VariantId && v.ProductId == card.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ConnectionId, connection.Id));

    [PostgreSqlFact]
    public async Task APttAvmModelProduct_IsRecordedWithItsCode_TheReaderVerifiesIt_AndAnOrphanGoes()
    {
        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var seed = AddSeed(db);
        var ptt = AddConnection(db, seed, "PTTAVM");
        // MZ080: two colour cards, one PTT AVM product (UrunId 1523, barcode MZ080) whose card link sits on the first.
        var siyah = AddCard(db, seed, seed.Ravencia, "MZ080", "Siyah", "Kadın Siyah Bluz", 0, "ptt");
        var mavi = AddCard(db, seed, seed.Ravencia, "MZ080", "Mavi", "Kadın Mavi Bluz", 1, "ptt");
        await db.SaveChangesAsync();
        await MoveVariantLinksAsync(db, seed, siyah, ptt);
        await MoveVariantLinksAsync(db, seed, mavi, ptt);
        var snapshot = new RemoteCatalogProduct("1523", "MZ080", "Kadın Bluz", "", null, null, null, null, [],
            [.. Variants("ptt", "Siyah", "Mavi").Select(id => new RemoteCatalogVariant(id, id, id, "MZ080", new Dictionary<string, string>(), false, null, null, null, null, "TRY", "{}"))], "{}");
        var listings = MarketplaceModelLinks.ModelListings("PTTAVM", [snapshot]);
        Assert.Equal(("1523", "MZ080", 4), (listings.Single().ExternalId, listings.Single().ExternalCode, listings.Single().ExternalVariantIds.Count));
        var now = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await MarketplaceModelLinks.RecordAsync(db, seed.Tenant.Id, ptt.Id, listings, now, CancellationToken.None);
        var model = (await db.Products.AsNoTracking().SingleAsync(x => x.Id == siyah.Id)).FamilyId;
        var link = Assert.Single(await ModelLinksAsync(db, seed));
        Assert.Equal((model, "1523", "MZ080"), ((Guid?)link.ModelId, link.ExternalId, link.ExternalCode));

        // The reader takes it while a card of the model holds a variant link there; a card link to another product voids it.
        Assert.Equal("MZ080", (await MarketplaceModelLinks.ForCardAsync(db, seed.Tenant.Id, ptt.Id, model, CancellationToken.None))?.ExternalCode);
        db.MarketplaceProductLinks.Add(new MarketplaceProductLink { Id = Guid.CreateVersion7(), TenantId = seed.Tenant.Id, ConnectionId = ptt.Id, ProductId = mavi.Id, ExternalId = "9999", SyncStatus = "SYNCED", Version = 1 });
        await db.SaveChangesAsync();
        Assert.Null(await MarketplaceModelLinks.ForCardAsync(db, seed.Tenant.Id, ptt.Id, model, CancellationToken.None));
        Assert.NotNull(await MarketplaceModelLinks.ForCardAsync(db, seed.Tenant.Id, ptt.Id, model, CancellationToken.None, cardLinksAreModelLevel: false));
        await db.MarketplaceProductLinks.Where(x => x.TenantId == seed.Tenant.Id).ExecuteDeleteAsync();

        // Every variant link gone: the reconcile removes the link, which nothing holds any more.
        await db.MarketplaceVariantLinks.Where(x => x.TenantId == seed.Tenant.Id).ExecuteDeleteAsync();
        Assert.Null(await MarketplaceModelLinks.ForCardAsync(db, seed.Tenant.Id, ptt.Id, model, CancellationToken.None));
        await ProductModelLinks.ReconcileTenantAsync(db, seed.Tenant.Id, now, CancellationToken.None);
        Assert.Empty(await ModelLinksAsync(db, seed));
        await transaction.RollbackAsync();
    }

    [PostgreSqlFact]
    public async Task TheOtherMarketplacesBackfill_TakesOneModelLevelLinkPerModel_AndRunsTwiceAlike()
    {
        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var seed = AddSeed(db);
        var ptt = AddConnection(db, seed, "PTTAVM");
        var n11 = AddConnection(db, seed, "N11");
        var pazarama = AddConnection(db, seed, "PAZARAMA");
        var trendyol = AddConnection(db, seed, "TRENDYOL");
        // MZ090: two colours, the PTT AVM product link on one card; N11 main: link; Pazarama group link; Trendyol contents.
        var siyah = AddCard(db, seed, seed.Ravencia, "MZ090", "Siyah", "Kadın Siyah Bluz", 0, null);
        var mavi = AddCard(db, seed, seed.Ravencia, "MZ090", "Mavi", "Kadın Mavi Bluz", 1, null);
        // MZ091: one N11 product per SKU ("n11:"): no model id. MZ092: two PTT AVM products, one per colour.
        var single = AddCard(db, seed, seed.Ravencia, "MZ091", "Gri", "Kadın Gri Etek", 2, null);
        var gri = AddCard(db, seed, seed.Ravencia, "MZ092", "Gri", "Kadın Gri Ceket", 3, null);
        var bej = AddCard(db, seed, seed.Ravencia, "MZ092", "Bej", "Kadın Bej Ceket", 4, null);
        // MZ093: two colours, the PTT AVM link on one card while the other card holds PTT AVM variant links: left to the import.
        var lacivert = AddCard(db, seed, seed.Ravencia, "MZ093", "Lacivert", "Kadın Lacivert Bluz", 7, "p93");
        var yesil = AddCard(db, seed, seed.Ravencia, "MZ093", "Yesil", "Kadın Yeşil Bluz", 8, "p93");
        // KENE01 without a model.
        var kene = AddCard(db, seed, seed.Lavian, "KENE01", "Mavi", "Kadın Mavi Etek", 5, null);
        AddCard(db, seed, seed.Ravencia, "KENE01", "Siyah", "Kadın Siyah Etek", 6, null);
        void Link(PlatformConnection connection, Product card, string external, string? code = null) =>
            db.MarketplaceProductLinks.Add(new MarketplaceProductLink { Id = Guid.CreateVersion7(), TenantId = seed.Tenant.Id, ConnectionId = connection.Id, ProductId = card.Id, ExternalId = external, ExternalCode = code, SyncStatus = "SYNCED", Version = 1 });
        Link(ptt, siyah, "1523", "MZ090");
        Link(n11, mavi, "main:MZ090");
        Link(pazarama, siyah, "group:g-90");
        Link(trendyol, siyah, "c-1");
        Link(trendyol, mavi, "c-2");
        Link(n11, single, "n11:42");
        Link(ptt, gri, "1600", "MZ092G");
        Link(ptt, bej, "1601", "MZ092B");
        Link(ptt, lacivert, "1800", "MZ093");
        Link(ptt, kene, "1700", "KENE01");
        await db.SaveChangesAsync();
        await MoveVariantLinksAsync(db, seed, lacivert, ptt);
        await MoveVariantLinksAsync(db, seed, yesil, ptt);

        var backfill = Infrastructure.Persistence.Migrations.ModelLinksAllMarketplaces.BackfillSql;
        await db.Database.ExecuteSqlRawAsync(backfill);
        await db.Database.ExecuteSqlRawAsync(backfill);
        var model = (await db.Products.AsNoTracking().SingleAsync(x => x.Id == siyah.Id)).FamilyId;
        var links = (await ModelLinksAsync(db, seed)).Select(x => ((Guid?)x.ModelId, x.ConnectionId, x.ExternalId, x.ExternalCode)).OrderBy(x => x.ExternalId).ToList();
        Assert.Equal([(model, ptt.Id, "1523", (string?)"MZ090"), (model, pazarama.Id, "group:g-90", "g-90"), (model, n11.Id, "main:MZ090", null)], links);
        Assert.Equal(10, await db.MarketplaceProductLinks.AsNoTracking().CountAsync(x => x.TenantId == seed.Tenant.Id));
        await transaction.RollbackAsync();
    }
    [PostgreSqlFact]
    public async Task APicture_BelongsToItsCardsModel_AndFollowsTheCardToAnotherModel()
    {
        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var seed = AddSeed(db);
        var siyah = AddCard(db, seed, seed.Ravencia, "MZ100", "Siyah", "Kadın Siyah Bluz", 0, null);
        AddCard(db, seed, seed.Ravencia, "MZ100", "Mavi", "Kadın Mavi Bluz", 1, null);
        AddCard(db, seed, seed.Ravencia, "MZ101", "Gri", "Kadın Gri Bluz", 2, null);
        var asset = new FileAsset { Id = Guid.CreateVersion7(), TenantId = seed.Tenant.Id, Classification = "PRODUCT_MEDIA_URL", RelativePath = "https://cdn.example.com/mz100.jpg", MimeType = "image/remote", Sha256 = Guid.NewGuid().ToString("N"), Status = "ACTIVE", CreatedAt = DateTimeOffset.UtcNow };
        db.FileAssets.Add(asset);
        // The card and its picture in one save, as an import adds them.
        db.ProductMedia.Add(new ProductMedia { Id = Guid.CreateVersion7(), TenantId = seed.Tenant.Id, ProductId = siyah.Id, FileAssetId = asset.Id, MediaRole = "PRIMARY", SortOrder = 0, Status = "ACTIVE" });
        await db.SaveChangesAsync();
        var mz100 = await db.ProductModels.AsNoTracking().SingleAsync(x => x.TenantId == seed.Tenant.Id && x.ModelCodeKey == "MZ100");
        Assert.Equal(mz100.Id, (await db.ProductMedia.AsNoTracking().SingleAsync(x => x.ProductId == siyah.Id)).FamilyId);

        // The card is re-coded to MZ101: it and its picture move to that model.
        foreach (var variant in await db.ProductVariants.Where(x => x.ProductId == siyah.Id).ToListAsync()) variant.ModelCode = "MZ101";
        await db.SaveChangesAsync();
        var mz101 = await db.ProductModels.AsNoTracking().SingleAsync(x => x.TenantId == seed.Tenant.Id && x.ModelCodeKey == "MZ101");
        Assert.Equal(mz101.Id, (await db.ProductMedia.AsNoTracking().SingleAsync(x => x.ProductId == siyah.Id)).FamilyId);

        // The backfill gives the same value.
        await db.ProductMedia.Where(x => x.ProductId == siyah.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.FamilyId, (Guid?)null));
        await db.Database.ExecuteSqlRawAsync(Infrastructure.Persistence.Migrations.ProductMediaFamily.BackfillSql);
        Assert.Equal(mz101.Id, (await db.ProductMedia.AsNoTracking().SingleAsync(x => x.ProductId == siyah.Id)).FamilyId);
        await transaction.RollbackAsync();
    }
}
