using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// KATALOG-14 D5 step B1: catalog.products.FamilyId is NOT NULL (its model key is ON DELETE RESTRICT since
// 20261127170948_ProductFamilyRestrict; before it, SET NULL ("FamilyId"), which the NOT NULL column refused). A card without a model cannot be written and a model with a card cannot be deleted; the paths that open, re-code or delete cards (and the Marka
// Listesi reset) still work because a card always has its model before it is written and leaves a model before it goes; the
// migration stops with the count, changing nothing, while a card without a model exists, and goes back and forth.
[Collection("postgres-integration")]
public sealed class ProductFamilyRequiredPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    private sealed record Seed(Tenant Tenant, Guid Ravencia, Guid Lavian, Guid CategoryId);

    private static Seed NewSeed()
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"family-{Guid.NewGuid():N}", DisplayName = "family", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        return new(tenant, Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
    }

    private static void AddSeed(AppDbContext db, Seed seed)
    {
        var now = DateTimeOffset.UtcNow;
        db.Tenants.Add(seed.Tenant);
        db.Categories.Add(new Category { Id = seed.CategoryId, TenantId = seed.Tenant.Id, Name = "Etek", NormalizedName = "ETEK", Path = "Etek", IsLeaf = true, IsActive = true, CreatedAt = now, UpdatedAt = now, Version = 1 });
        db.Brands.Add(new Brand { Id = seed.Ravencia, TenantId = seed.Tenant.Id, Name = "Ravencia", NormalizedName = "RAVENCIA", IsActive = true, CreatedAt = now, UpdatedAt = now, Version = 1 });
        db.Brands.Add(new Brand { Id = seed.Lavian, TenantId = seed.Tenant.Id, Name = "Lavian", NormalizedName = "LAVIAN", IsActive = true, CreatedAt = now, UpdatedAt = now, Version = 1 });
    }

    // A colour card with two sizes, as an import, the editor (Yeni Ürün), a copy or an Excel row opens it: one SaveChanges.
    private static Product AddCard(AppDbContext db, Seed seed, Guid? brandId, string? modelCode, string colour, string title, int minute = 0)
    {
        var now = DateTimeOffset.UtcNow;
        var card = new Product { Id = Guid.CreateVersion7(), TenantId = seed.Tenant.Id, Title = title, Description = "Model açıklaması", CategoryId = seed.CategoryId, BrandId = brandId, VatRate = 10m, VatInclusion = "INCLUDED", DeliveryDays = 1, Status = ProductStatus.Active, CreatedAt = now.AddMinutes(minute), UpdatedAt = now, Version = 1 };
        db.Products.Add(card);
        var index = 0;
        foreach (var size in new[] { "M", "L" })
        {
            var sku = $"{modelCode ?? "KODSUZ"}-{colour}-{size}-{Guid.NewGuid():N}"[..40];
            var barcode = $"869{Random.Shared.NextInt64(1_000_000_000L, 9_999_999_999L)}";
            db.ProductVariants.Add(new ProductVariant { Id = Guid.CreateVersion7(), TenantId = seed.Tenant.Id, ProductId = card.Id, Sku = sku, SkuNormalized = sku.ToUpperInvariant(), Barcode = barcode, BarcodeNormalized = barcode, ModelCode = modelCode, OptionSignature = $"Renk: {colour} | Beden: {size}", Desi = 1m, DefaultListPrice = 900m, DefaultSalePrice = 600m, Status = ProductStatus.Active, SortOrder = index++, CreatedAt = now, UpdatedAt = now, Version = 1 });
        }
        return card;
    }

    private CatalogService Service(AppDbContext db) =>
        new(db, new CursorCodec(new EphemeralDataProtectionProvider(), TimeProvider.System), new ConfigurationBuilder().Build(), fixture.TimeProvider, new MemoryCache(new MemoryCacheOptions()));

    private static async Task<Dictionary<Guid, Guid?>> LinksAsync(AppDbContext db, Guid tenantId) =>
        await db.Products.AsNoTracking().Where(x => x.TenantId == tenantId).ToDictionaryAsync(x => x.Id, x => x.FamilyId);

    private static async Task<List<ProductModel>> ModelsAsync(AppDbContext db, Guid tenantId) =>
        await db.ProductModels.AsNoTracking().Where(x => x.TenantId == tenantId).OrderBy(x => x.ModelCodeKey).ToListAsync();

    private static string OwnKey(Guid cardId) => $"{ProductModelLinks.OwnModelKeyPrefix}{cardId:N}";

    // KATALOG-14 D5 B1, before the column is NOT NULL: in this transaction only, a NULL link is refused as the NOT NULL column
    // will refuse it, so a path that writes one (even for a moment) fails here.
    private static Task RequireModelsInThisTransactionAsync(AppDbContext db) =>
        db.Database.ExecuteSqlRawAsync("""ALTER TABLE catalog.products ADD CONSTRAINT "ck_test_products_family_required" CHECK ("FamilyId" IS NOT NULL) NOT VALID""");

    // Yeni Ürün, an import, a copy and an Excel row all open cards through SaveChanges: a coded card joins or opens its
    // brand's model of the code (the own model it was inserted with goes), a card without a code keeps a model of its own,
    // and nothing else is left behind.
    [PostgreSqlFact]
    public async Task NewCards_AreWrittenWithTheirModel_CodedOnesJoinTheirCodesModel_AndNoInsertModelIsLeftBehind()
    {
        var seed = NewSeed();
        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await RequireModelsInThisTransactionAsync(db);
        AddSeed(db, seed);
        var siyah = AddCard(db, seed, seed.Ravencia, "MZ050", "Siyah", "Kadın Siyah Etek");
        var mavi = AddCard(db, seed, seed.Ravencia, "MZ050", "Mavi", "Kadın Mavi Etek");
        var lavian = AddCard(db, seed, seed.Lavian, "MZ050", "Bej", "Kadın Bej Etek");
        var codeless = AddCard(db, seed, seed.Ravencia, null, "Gri", "Kadın Gri Etek");
        await db.SaveChangesAsync();

        // The tracked cards carry the link the save left (no stale insert model).
        var links = await LinksAsync(db, seed.Tenant.Id);
        foreach (var card in new[] { siyah, mavi, lavian, codeless }) Assert.Equal(links[card.Id], card.FamilyId);
        Assert.Empty(db.ChangeTracker.Entries<ProductModel>());

        var models = await ModelsAsync(db, seed.Tenant.Id);
        Assert.Equal(3, models.Count);
        var ravencia = models.Single(x => x.ModelCodeKey == "MZ050" && x.BrandId == seed.Ravencia);
        var lavianModel = models.Single(x => x.ModelCodeKey == "MZ050" && x.BrandId == seed.Lavian);
        var own = models.Single(x => x.ModelCodeKey == OwnKey(codeless.Id));
        Assert.Equal((ravencia.Id, ravencia.Id, lavianModel.Id, own.Id), (links[siyah.Id], links[mavi.Id], links[lavian.Id], links[codeless.Id]));
        // The new model took the first card's values and the colour word of its name, as before.
        Assert.Equal(("Kadın Siyah Etek", "Siyah"), (ravencia.Title, ravencia.TitleColor));
        Assert.Equal((seed.Ravencia, "", "Kadın Gri Etek"), (own.BrandId, own.ModelCode, own.Title));

        // A later colour of the code joins the model in its own save.
        var bordo = AddCard(db, seed, seed.Ravencia, "MZ050", "Bordo", "Kadın Bordo Etek");
        await db.SaveChangesAsync();
        Assert.Equal(ravencia.Id, (await LinksAsync(db, seed.Tenant.Id))[bordo.Id]);
        Assert.Equal(3, (await ModelsAsync(db, seed.Tenant.Id)).Count);
        await transaction.RollbackAsync();
    }

    // A save that fails leaves its new cards as the caller made them: the retry writes them with a new model.
    [PostgreSqlFact]
    public async Task ASaveThatFails_IsRetried_WithoutALeftoverModel()
    {
        var seed = NewSeed();
        await using (var setup = fixture.CreateContext())
        {
            AddSeed(setup, seed);
            await setup.SaveChangesAsync();
        }
        await using var db = fixture.CreateContext();
        var card = AddCard(db, seed, seed.Ravencia, "MZ077", "Siyah", "Kadın Siyah Etek");
        var duplicateSku = db.ChangeTracker.Entries<ProductVariant>().First().Entity;
        var clash = new ProductVariant { Id = Guid.CreateVersion7(), TenantId = seed.Tenant.Id, ProductId = card.Id, Sku = duplicateSku.Sku, SkuNormalized = duplicateSku.SkuNormalized, Barcode = "8690000000001", BarcodeNormalized = "8690000000001", ModelCode = "MZ077", OptionSignature = "Renk: Siyah | Beden: S", Status = ProductStatus.Active, SortOrder = 9, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, Version = 1 };
        db.ProductVariants.Add(clash);
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Null(card.FamilyId);
        Assert.Empty(db.ChangeTracker.Entries<ProductModel>());

        db.Entry(clash).State = EntityState.Detached;
        await db.SaveChangesAsync();
        await using var check = fixture.CreateContext();
        var model = (await ModelsAsync(check, seed.Tenant.Id)).Single();
        Assert.Equal(("MZ077", model.Id), (model.ModelCodeKey, (await LinksAsync(check, seed.Tenant.Id))[card.Id]));
    }

    // A card whose code is taken away leaves its model straight for a model of its own (never a NULL link), and the model
    // keeps its other colour.
    [PostgreSqlFact]
    public async Task ACardWhoseCodeIsRemoved_MovesToAModelOfItsOwn()
    {
        var seed = NewSeed();
        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await RequireModelsInThisTransactionAsync(db);
        AddSeed(db, seed);
        var siyah = AddCard(db, seed, seed.Ravencia, "MZ050", "Siyah", "Kadın Siyah Etek");
        var mavi = AddCard(db, seed, seed.Ravencia, "MZ050", "Mavi", "Kadın Mavi Etek");
        await db.SaveChangesAsync();
        var model = (await LinksAsync(db, seed.Tenant.Id))[siyah.Id];

        foreach (var variant in db.ChangeTracker.Entries<ProductVariant>().Where(x => x.Entity.ProductId == mavi.Id)) variant.Entity.ModelCode = null;
        await db.SaveChangesAsync();

        var links = await LinksAsync(db, seed.Tenant.Id);
        var own = await db.ProductModels.AsNoTracking().SingleAsync(x => x.TenantId == seed.Tenant.Id && x.ModelCodeKey == OwnKey(mavi.Id));
        Assert.Equal((model, own.Id), (links[siyah.Id], links[mavi.Id]));
        Assert.Equal(2, (await ModelsAsync(db, seed.Tenant.Id)).Count);
        await transaction.RollbackAsync();
    }

    // Ürün silme: the cards go first, then their model (RESTRICT holds); the other brand's model of the code stays.
    [PostgreSqlFact]
    public async Task DeletingCards_DeletesTheirModelAfterThem()
    {
        var seed = NewSeed();
        Product siyah, lavian;
        await using (var db = fixture.CreateContext())
        {
            AddSeed(db, seed);
            siyah = AddCard(db, seed, seed.Ravencia, "MZ050", "Siyah", "Kadın Siyah Etek");
            lavian = AddCard(db, seed, seed.Lavian, "MZ050", "Bej", "Kadın Bej Etek");
            await db.SaveChangesAsync();
        }
        await using (var db = fixture.CreateContext())
        {
            var deleted = await Service(db).DeleteProductAsync(seed.Tenant.Id, siyah.Id, 1, CancellationToken.None);
            Assert.True(deleted.Succeeded, deleted.Error?.Message);
            var model = (await ModelsAsync(db, seed.Tenant.Id)).Single();
            Assert.Equal((seed.Lavian, model.Id), (model.BrandId, (await LinksAsync(db, seed.Tenant.Id))[lavian.Id]));
        }
    }

    private async Task ResetBrandListAsync(Seed seed)
    {
        await using var db = fixture.CreateContext();
        var service = new PanelFactoryResetService(db, fixture.TimeProvider, new NoDashboard(), NullLogger<PanelFactoryResetService>.Instance);
        var payload = JsonSerializer.Serialize(new { KeepSessionId = (Guid?)null, ActorUserId = Guid.Empty, Categories = new[] { PanelFactoryResetInventory.Brands.Key } });
        var result = await service.RunAsync(seed.Tenant.Id, Guid.CreateVersion7(), payload, CancellationToken.None);
        Assert.True(result.Succeeded, result.ErrorSummary);
        Assert.False(await db.Brands.AnyAsync(x => x.TenantId == seed.Tenant.Id));
    }

    // Review, 18 Nov 2026 (the MZ050 case): Marka Listesi alone. A code only one model holds keeps that model: it drops its
    // brand and keeps its id, name, colour word and saved fields, its cards stay on it, and a colour added afterwards takes
    // the model's name with its own colour word ("Kadın Kırmızı Etek"). A card without a code keeps its own model.
    [PostgreSqlFact]
    public async Task TheBrandListReset_KeepsAOneBrandModel_WithItsNameColourAndSavedFields()
    {
        var seed = NewSeed();
        Product siyah, mavi, yesil, bej, codeless;
        ProductModel before;
        var savedAt = new DateTimeOffset(2026, 11, 10, 9, 0, 0, TimeSpan.Zero);
        await using (var db = fixture.CreateContext())
        {
            AddSeed(db, seed);
            siyah = AddCard(db, seed, seed.Ravencia, "MZ050", "Siyah", "Kadın Siyah Etek", 0);
            mavi = AddCard(db, seed, seed.Ravencia, "MZ050", "Mavi", "Kadın Mavi Etek", 1);
            yesil = AddCard(db, seed, seed.Ravencia, "MZ050", "Yeşil", "Kadın Yeşil Etek", 2);
            bej = AddCard(db, seed, seed.Ravencia, "MZ050", "Bej", "Kadın Bej Etek", 3);
            codeless = AddCard(db, seed, seed.Ravencia, null, "Gri", "Kadın Gri Etek", 4);
            await db.SaveChangesAsync();
            await db.ProductModels.Where(x => x.TenantId == seed.Tenant.Id && x.ModelCodeKey == "MZ050").ExecuteUpdateAsync(s => s.SetProperty(x => x.FieldsSavedAt, savedAt));
            before = await db.ProductModels.AsNoTracking().SingleAsync(x => x.TenantId == seed.Tenant.Id && x.ModelCodeKey == "MZ050");
            Assert.Equal(("Kadın Siyah Etek", "Siyah"), (before.Title, before.TitleColor));
        }
        await ResetBrandListAsync(seed);
        await using (var db = fixture.CreateContext())
        {
            var links = await LinksAsync(db, seed.Tenant.Id);
            var models = await ModelsAsync(db, seed.Tenant.Id);
            Assert.Equal(2, models.Count);
            Assert.All(models, x => Assert.Null(x.BrandId));
            var after = models.Single(x => x.ModelCodeKey == "MZ050");
            Assert.Equal((before.Id, "Kadın Siyah Etek", "Siyah", (DateTimeOffset?)savedAt), (after.Id, after.Title, after.TitleColor, after.FieldsSavedAt?.ToUniversalTime()));
            Assert.All(new[] { siyah, mavi, yesil, bej }, card => Assert.Equal(before.Id, links[card.Id]));
            Assert.Equal(models.Single(x => x.ModelCodeKey == OwnKey(codeless.Id)).Id, links[codeless.Id]);

            var kirmizi = AddCard(db, seed, null, "MZ050", "Kırmızı", "Kadın Kırmızı Etek", 5);
            await db.SaveChangesAsync();
            Assert.Equal(before.Id, (await LinksAsync(db, seed.Tenant.Id))[kirmizi.Id]);
            Assert.Equal("Kadın Kırmızı Etek", (await db.Products.AsNoTracking().SingleAsync(x => x.Id == kirmizi.Id)).Title);
            var still = await db.ProductModels.AsNoTracking().SingleAsync(x => x.Id == before.Id);
            Assert.Equal(("Kadın Siyah Etek", "Siyah"), (still.Title, still.TitleColor));
        }
    }

    // A code two brands hold becomes one model without a brand, as the nightly reconcile made it before B1: opened from the
    // cards, the first brought in giving its name and colour word.
    [PostgreSqlFact]
    public async Task TheBrandListReset_GivesACodeOfTwoBrandsOneModel_FromTheFirstCardBroughtIn()
    {
        var seed = NewSeed();
        Product siyah, mavi, bej;
        await using (var db = fixture.CreateContext())
        {
            AddSeed(db, seed);
            bej = AddCard(db, seed, seed.Lavian, "MZ050", "Bej", "Kadın Bej Etek", 2);
            mavi = AddCard(db, seed, seed.Ravencia, "MZ050", "Mavi", "Kadın Mavi Etek", 1);
            siyah = AddCard(db, seed, seed.Ravencia, "MZ050", "Siyah", "Kadın Siyah Etek", 0);
            await db.SaveChangesAsync();
            Assert.Equal(2, await db.ProductModels.CountAsync(x => x.TenantId == seed.Tenant.Id && x.ModelCodeKey == "MZ050"));
        }
        await ResetBrandListAsync(seed);
        await using (var db = fixture.CreateContext())
        {
            var links = await LinksAsync(db, seed.Tenant.Id);
            var model = (await ModelsAsync(db, seed.Tenant.Id)).Single();
            Assert.Equal(("MZ050", (Guid?)null, "Kadın Siyah Etek", "Siyah"), (model.ModelCodeKey, model.BrandId, model.Title, model.TitleColor));
            Assert.Equal((model.Id, model.Id, model.Id), (links[siyah.Id], links[mavi.Id], links[bej.Id]));
        }
    }

    // Gives a card's model what the owner and the marketplaces build on it: saved fields and attributes, a size chart and a
    // marketplace model link (with the variant link that keeps it).
    private static async Task<Guid> DecorateModelAsync(AppDbContext db, Seed seed, Product card)
    {
        var modelId = (await db.Products.AsNoTracking().SingleAsync(x => x.Id == card.Id)).FamilyId!.Value;
        var connection = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = seed.Tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = "TRENDYOL", Environment = "PRODUCTION", DisplayName = "Trendyol", ExternalStoreId = $"store-{Guid.NewGuid():N}", Status = "ACTIVE", ApiVersion = "V1", SettingsJson = "{}", Version = 1 };
        db.PlatformConnections.Add(connection);
        var fabric = new AttributeDefinition { Id = Guid.CreateVersion7(), TenantId = seed.Tenant.Id, Code = $"FABRIC-{Guid.NewGuid():N}", Name = "Kumaş", DataType = AttributeDataType.Text, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, Version = 1 };
        db.AttributeDefinitions.Add(fabric);
        await db.SaveChangesAsync();
        var variant = await db.ProductVariants.AsNoTracking().Where(x => x.ProductId == card.Id).OrderBy(x => x.SortOrder).FirstAsync();
        db.MarketplaceModelLinks.Add(new MarketplaceModelLink { Id = Guid.CreateVersion7(), TenantId = seed.Tenant.Id, ConnectionId = connection.Id, ModelId = modelId, ExternalId = "TY-1", CreatedAt = DateTimeOffset.UtcNow, Version = 1 });
        db.MarketplaceVariantLinks.Add(new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = seed.Tenant.Id, ConnectionId = connection.Id, VariantId = variant.Id, ExternalId = variant.Barcode!, Version = 1 });
        db.ProductAttributeAssignments.Add(new ProductAttributeAssignment { Id = Guid.CreateVersion7(), TenantId = seed.Tenant.Id, ProductId = card.Id, AttributeId = fabric.Id, TextValue = "Pamuk", Version = 1 });
        await db.SaveChangesAsync();
        await ProductModelValues.RefreshAsync(db, seed.Tenant.Id, [modelId], DateTimeOffset.UtcNow, CancellationToken.None);
        var later = DateTimeOffset.UtcNow.AddMinutes(5);
        await db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE catalog.product_models SET "FieldsSavedAt" = {later}, "AttributesSavedAt" = {later} WHERE "Id" = {modelId}""");
        await db.Database.ExecuteSqlInterpolatedAsync($"""INSERT INTO catalog.product_model_size_charts ("Id", "TenantId", "ModelId", "MeasurementsJson", "UpdatedAt") VALUES ({Guid.CreateVersion7()}, {seed.Tenant.Id}, {modelId}, '[]', now())""");
        db.ChangeTracker.Clear();
        return modelId;
    }

    // Review round 2 (18 Nov 2026): a card without a code whose own model holds saved fields, attributes, a size chart and a
    // marketplace model link is given a code no model holds yet (the editor, an Excel row): its model takes the code (same
    // id, everything kept, as before B1) and the name and colour word the code's model opens with.
    [PostgreSqlFact]
    public async Task ASavedOwnModelGivenANewCode_IsReKeyed_KeepingEverything_WithItsColourWord()
    {
        var seed = NewSeed();
        await using var db = fixture.CreateContext();
        AddSeed(db, seed);
        var siyah = AddCard(db, seed, seed.Ravencia, null, "Siyah", "Kadın Siyah Etek");
        await db.SaveChangesAsync();
        var own = await DecorateModelAsync(db, seed, siyah);
        Assert.Null((await db.ProductModels.AsNoTracking().SingleAsync(x => x.Id == own)).TitleColor);

        foreach (var variant in await db.ProductVariants.Where(x => x.ProductId == siyah.Id).ToListAsync()) variant.ModelCode = "MZ060";
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var model = (await ModelsAsync(db, seed.Tenant.Id)).Single();
        Assert.Equal((own, "MZ060", (Guid?)seed.Ravencia, "Kadın Siyah Etek", "Siyah"), (model.Id, model.ModelCodeKey, model.BrandId, model.Title, model.TitleColor));
        Assert.NotNull(model.FieldsSavedAt);
        Assert.NotNull(model.AttributesSavedAt);
        Assert.Equal(1, await db.MarketplaceModelLinks.CountAsync(x => x.ModelId == own));
        Assert.Equal(1, await db.ProductModelSizeCharts.CountAsync(x => x.ModelId == own));
        Assert.True(await db.ProductModelAttributes.AnyAsync(x => x.ModelId == own));
        Assert.Equal(own, (await LinksAsync(db, seed.Tenant.Id))[siyah.Id]);
    }

    // The same card given a code that already has its model joins that model (the code's model wins); its own model goes.
    [PostgreSqlFact]
    public async Task ASavedOwnModelGivenACodeWithAModel_JoinsThatModel()
    {
        var seed = NewSeed();
        await using var db = fixture.CreateContext();
        AddSeed(db, seed);
        var mavi = AddCard(db, seed, seed.Ravencia, "MZ060", "Mavi", "Kadın Mavi Etek", 0);
        var siyah = AddCard(db, seed, seed.Ravencia, null, "Siyah", "Kadın Siyah Etek", 1);
        await db.SaveChangesAsync();
        var code = (await LinksAsync(db, seed.Tenant.Id))[mavi.Id];
        var own = await DecorateModelAsync(db, seed, siyah);

        foreach (var variant in await db.ProductVariants.Where(x => x.ProductId == siyah.Id).ToListAsync()) variant.ModelCode = "MZ060";
        await db.SaveChangesAsync();

        Assert.Equal(code, (await LinksAsync(db, seed.Tenant.Id))[siyah.Id]);
        Assert.False(await db.ProductModels.AnyAsync(x => x.Id == own));
        var model = (await ModelsAsync(db, seed.Tenant.Id)).Single();
        Assert.Equal((code!.Value, "Kadın Mavi Etek", "Mavi"), (model.Id, model.Title, model.TitleColor));
    }

    // New cards that bring a new code in one save open the code's model from the first card brought in (each field from
    // it, else from the oldest card that knows it): the own models the save gave them before their insert are no model to
    // re-key (SaveScope.FreshModels), so the first card's empty delivery days are taken from the next card.
    [PostgreSqlFact]
    public async Task NewCardsOfANewCode_OpenItsModelFromTheFirstCardBroughtIn_NotFromAnInsertModel()
    {
        var seed = NewSeed();
        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await RequireModelsInThisTransactionAsync(db);
        AddSeed(db, seed);
        await db.SaveChangesAsync();
        var siyah = AddCard(db, seed, seed.Ravencia, "MZ070", "Siyah", "Kadın Siyah Etek", 0);
        siyah.DeliveryDays = null;
        var mavi = AddCard(db, seed, seed.Ravencia, "MZ070", "Mavi", "Kadın Mavi Etek", 1);
        mavi.DeliveryDays = 3;
        await db.SaveChangesAsync();

        var model = (await ModelsAsync(db, seed.Tenant.Id)).Single();
        Assert.Equal(("MZ070", "Kadın Siyah Etek", "Siyah", (int?)3), (model.ModelCodeKey, model.Title, model.TitleColor, model.DeliveryDays));
        await transaction.RollbackAsync();
    }

    // B1 stage 2 (20261127111010_ProductFamilyRequired): the database itself refuses a card without a model, a link taken
    // back to NULL and the delete of a model that still has a card (the key is ON DELETE RESTRICT: refused on
    // that key, SQLSTATE 23001 restrict_violation); the card keeps its model.
    [PostgreSqlFact]
    public async Task TheDatabase_RefusesACardWithoutAModel_AndTheDeleteOfAModelThatHasACard()
    {
        var seed = NewSeed();
        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        AddSeed(db, seed);
        var card = AddCard(db, seed, seed.Ravencia, "MZ080", "Siyah", "Kadın Siyah Etek");
        await db.SaveChangesAsync();
        var model = (await LinksAsync(db, seed.Tenant.Id))[card.Id]!.Value;

        await db.Database.ExecuteSqlRawAsync("SAVEPOINT before_write");
        var insert = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO catalog.products ("Id", "TenantId", "Title", "Description", "Status", "SourcePolicyVersion", "CreatedAt", "UpdatedAt", "Version")
            VALUES ({Guid.CreateVersion7()}, {seed.Tenant.Id}, 'Modelsiz', '', 'Draft', 1, {DateTimeOffset.UtcNow}, {DateTimeOffset.UtcNow}, 1)
            """));
        Assert.Equal((Npgsql.PostgresErrorCodes.NotNullViolation, "FamilyId"), (insert.SqlState, insert.ColumnName));
        await db.Database.ExecuteSqlRawAsync("ROLLBACK TO SAVEPOINT before_write");

        var unlink = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE catalog.products SET "FamilyId" = NULL WHERE "Id" = {card.Id}"""));
        Assert.Equal(Npgsql.PostgresErrorCodes.NotNullViolation, unlink.SqlState);
        await db.Database.ExecuteSqlRawAsync("ROLLBACK TO SAVEPOINT before_write");

        var delete = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""DELETE FROM catalog.product_models WHERE "Id" = {model}"""));
        Assert.Equal((Npgsql.PostgresErrorCodes.RestrictViolation, ProductModelLinks.ProductFamilyKeyName), (delete.SqlState, delete.ConstraintName));
        await db.Database.ExecuteSqlRawAsync("ROLLBACK TO SAVEPOINT before_write");
        Assert.Equal(model, (await LinksAsync(db, seed.Tenant.Id))[card.Id]);
        await transaction.RollbackAsync();
    }

    // The owner's rule "drafts may be incomplete": Yeni Ürün saves a draft without a brand and without a model code; it is
    // written with a model of its own in the same save and stays on it.
    [PostgreSqlFact]
    public async Task ADraftWithoutBrandOrModelCode_IsSavedWithAModelOfItsOwn()
    {
        var seed = NewSeed();
        await using (var db = fixture.CreateContext())
        {
            AddSeed(db, seed);
            await db.SaveChangesAsync();
        }
        await using (var db = fixture.CreateContext())
        {
            var created = await Service(db).CreateProductAsync(seed.Tenant.Id,
                new CreateProductCommand("Taslak Etek", "", null, null, [new CreateVariantCommand($"TSL-{Guid.NewGuid():N}"[..20], null, null)], Status: "Draft"), CancellationToken.None);
            Assert.True(created.Succeeded, created.Error?.Message);
            var card = await db.Products.AsNoTracking().SingleAsync(x => x.TenantId == seed.Tenant.Id);
            Assert.Equal(ProductStatus.Draft, card.Status);
            var model = (await ModelsAsync(db, seed.Tenant.Id)).Single();
            Assert.Equal((OwnKey(card.Id), (Guid?)null, model.Id), (model.ModelCodeKey, model.BrandId, card.FamilyId!.Value));
        }
    }

    // Tam Sıfırlama: every card goes before its model, so the NOT NULL link never stops it; nothing of the tenant's catalog
    // is left.
    [PostgreSqlFact]
    public async Task TheFullReset_RemovesTheCardsBeforeTheirModels()
    {
        var seed = NewSeed();
        await using (var db = fixture.CreateContext())
        {
            AddSeed(db, seed);
            AddCard(db, seed, seed.Ravencia, "MZ090", "Siyah", "Kadın Siyah Etek");
            AddCard(db, seed, seed.Ravencia, "MZ090", "Mavi", "Kadın Mavi Etek");
            AddCard(db, seed, seed.Lavian, null, "Bej", "Kadın Bej Etek");
            await db.SaveChangesAsync();
            Assert.Equal(2, (await ModelsAsync(db, seed.Tenant.Id)).Count);
        }
        await using (var db = fixture.CreateContext())
        {
            var service = new PanelFactoryResetService(db, fixture.TimeProvider, new NoDashboard(), NullLogger<PanelFactoryResetService>.Instance);
            var payload = JsonSerializer.Serialize(new { KeepSessionId = (Guid?)null, ActorUserId = Guid.Empty, Categories = (string[]?)null });
            var result = await service.RunAsync(seed.Tenant.Id, Guid.CreateVersion7(), payload, CancellationToken.None);
            Assert.True(result.Succeeded, result.ErrorSummary);
            Assert.False(await db.Products.AnyAsync(x => x.TenantId == seed.Tenant.Id));
            Assert.Empty(await ModelsAsync(db, seed.Tenant.Id));
        }
    }

    // The migration: Down makes the column nullable again; Up stops on a card without a model with its count, changing
    // nothing (no constraint left, the row untouched), then makes the column NOT NULL once no such card is left; a second
    // run is a no-op. It never touches the model key (ON DELETE RESTRICT since 20261127170948_ProductFamilyRestrict).
    [PostgreSqlFact]
    public async Task TheMigration_StopsOnACardWithoutAModel_ChangingNothing_AndGoesBackAndForth()
    {
        var seed = NewSeed();
        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        AddSeed(db, seed);
        var card = AddCard(db, seed, seed.Ravencia, "MZ095", "Siyah", "Kadın Siyah Etek");
        await db.SaveChangesAsync();
        var model = (await LinksAsync(db, seed.Tenant.Id))[card.Id]!.Value;
        var migration = new MarketplaceHub.Infrastructure.Persistence.Migrations.ProductFamilyRequired();
        Assert.All(migration.UpOperations.Concat(migration.DownOperations), operation => Assert.IsType<Microsoft.EntityFrameworkCore.Migrations.Operations.SqlOperation>(operation));
        async Task RunAsync(IEnumerable<Microsoft.EntityFrameworkCore.Migrations.Operations.MigrationOperation> operations)
        {
            foreach (var operation in operations.Cast<Microsoft.EntityFrameworkCore.Migrations.Operations.SqlOperation>()) await db.Database.ExecuteSqlRawAsync(operation.Sql);
        }
        async Task<(bool NotNull, string OnDelete, bool Check)> SchemaAsync() =>
            (await db.Database.SqlQueryRaw<SchemaRow>("""
                SELECT a.attnotnull AS "NotNull",
                       (SELECT k.confdeltype::text FROM pg_constraint k WHERE k.conrelid = 'catalog.products'::regclass AND k.conname = 'FK_products_product_models_TenantId_FamilyId') AS "OnDelete",
                       EXISTS (SELECT 1 FROM pg_constraint k WHERE k.conrelid = 'catalog.products'::regclass AND k.conname = 'CK_products_FamilyId_NotNull') AS "Check"
                FROM pg_attribute a WHERE a.attrelid = 'catalog.products'::regclass AND a.attname = 'FamilyId'
                """).ToListAsync()).Select(x => (x.NotNull, x.OnDelete, x.Check)).Single();

        Assert.Equal((true, "r", false), await SchemaAsync());
        await RunAsync(migration.DownOperations);
        Assert.Equal((false, "r", false), await SchemaAsync());
        await db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE catalog.products SET "FamilyId" = NULL WHERE "Id" = {card.Id}""");

        await db.Database.ExecuteSqlRawAsync("SAVEPOINT before_up");
        var stopped = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => RunAsync(migration.UpOperations));
        Assert.Equal(Npgsql.PostgresErrorCodes.CheckViolation, stopped.SqlState);
        Assert.Contains("modeli olmayan 1 urun karti", stopped.MessageText, StringComparison.Ordinal);
        Assert.Contains("Hicbir sey degistirilmedi", stopped.MessageText, StringComparison.Ordinal);
        await db.Database.ExecuteSqlRawAsync("ROLLBACK TO SAVEPOINT before_up");
        Assert.Equal((false, "r", false), await SchemaAsync());
        Assert.Equal(1, (await db.Database.SqlQuery<int>($"""SELECT count(*)::int AS "Value" FROM catalog.products WHERE "Id" = {card.Id} AND "FamilyId" IS NULL""").ToListAsync()).Single());

        await db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE catalog.products SET "FamilyId" = {model} WHERE "Id" = {card.Id}""");
        await RunAsync(migration.UpOperations);
        Assert.Equal((true, "r", false), await SchemaAsync());
        await RunAsync(migration.UpOperations);
        Assert.Equal((true, "r", false), await SchemaAsync());
        await transaction.RollbackAsync();
    }

    private sealed record SchemaRow(bool NotNull, string OnDelete, bool Check);

    private sealed class NoDashboard : IDashboardReadService
    {
        public Task<DashboardBootstrapView> BootstrapAsync(Guid tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ServiceResult<DashboardPerformanceView>> PerformanceAsync(Guid tenantId, DateOnly? from, DateOnly? to, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ServiceResult<DashboardCancellationsView>> CancellationsAsync(Guid tenantId, DateOnly? day, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RebuildTenantAsync(Guid tenantId, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
