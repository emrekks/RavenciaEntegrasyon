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
}
