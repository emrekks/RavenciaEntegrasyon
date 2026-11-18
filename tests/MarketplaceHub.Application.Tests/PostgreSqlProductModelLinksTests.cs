using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using MarketplaceHub.Infrastructure.Persistence.Migrations;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Model Kaydı Faz 1 (MODEL-KAYDI-PLANI): catalog.product_models and products.FamilyId follow the model-code rule on every
// path that opens, re-codes, re-brands or deletes a card, the migration's backfill links today's cards without changing
// them, and a model code used by two brands (KENE01) is never linked or merged, only reported.
[Collection("postgres-integration")]
public sealed class PostgreSqlProductModelLinksTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    private sealed record Seed(Tenant Tenant, Guid Ravencia, Guid Lavian, Guid CategoryId);

    private static Seed NewSeed()
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"models-{Guid.NewGuid():N}", DisplayName = "models", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
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

    // A colour card with two sizes, as an import or the editor opens it.
    private static Product AddCard(AppDbContext db, Seed seed, Guid? brandId, string modelCode, string colour, string title, int minute)
    {
        var now = DateTimeOffset.UtcNow;
        var card = new Product { Id = Guid.CreateVersion7(), TenantId = seed.Tenant.Id, Title = title, Description = "Model açıklaması", CategoryId = seed.CategoryId, BrandId = brandId, VatRate = 10m, VatInclusion = "INCLUDED", DeliveryDays = 1, Status = ProductStatus.Active, CreatedAt = now.AddMinutes(minute), UpdatedAt = now, Version = 1 };
        db.Products.Add(card);
        var index = 0;
        foreach (var size in new[] { "M", "L" })
        {
            var sku = $"{modelCode}-{colour}-{size}-{Guid.NewGuid():N}"[..40];
            var barcode = $"869{Random.Shared.NextInt64(1_000_000_000L, 9_999_999_999L)}";
            db.ProductVariants.Add(new ProductVariant { Id = Guid.CreateVersion7(), TenantId = seed.Tenant.Id, ProductId = card.Id, Sku = sku, SkuNormalized = sku.ToUpperInvariant(), Barcode = barcode, BarcodeNormalized = barcode, ModelCode = modelCode, OptionSignature = $"Renk: {colour} | Beden: {size}", Desi = 1m, DefaultListPrice = 900m, DefaultSalePrice = 600m, Status = ProductStatus.Active, SortOrder = index++, CreatedAt = now, UpdatedAt = now, Version = 1 });
        }
        return card;
    }

    private CatalogService Service(AppDbContext db) =>
        new(db, new CursorCodec(new EphemeralDataProtectionProvider(), TimeProvider.System), new ConfigurationBuilder().Build(), fixture.TimeProvider, new MemoryCache(new MemoryCacheOptions()));

    private static UpdateProductCommand Command(ProductView shown, IReadOnlyList<ProductAttributeCommand>? attributes = null) =>
        new(shown.Title, shown.Description, shown.BrandId, shown.CategoryId, WarrantyMonths: shown.WarrantyMonths ?? 0, VatRate: shown.VatRate, VatInclusion: shown.VatInclusion, DeliveryDays: shown.DeliveryDays, Attributes: attributes, FamilySharedFields: ProductFamilySharedFields.All);

    // Raw SQL: a test that makes the state before the backfill reads NULL links, which the model (FamilyId required) cannot.
    private static Task<Dictionary<Guid, Guid?>> LinksAsync(AppDbContext db, Guid tenantId) =>
        db.Database.SqlQuery<LinkRow>($"""SELECT "Id", "FamilyId" FROM catalog.products WHERE "TenantId" = {tenantId}""").ToDictionaryAsync(x => x.Id, x => x.FamilyId);

    private sealed record LinkRow(Guid Id, Guid? FamilyId);

    // A code two brands share: the card's code set by SQL, then the whole tenant's reconcile (each brand its own model).
    private static async Task SharedCodeFromBeforeAsync(AppDbContext db, Seed seed, Product card, string code)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE catalog.product_variants SET "ModelCode" = {code} WHERE "ProductId" = {card.Id}""");
        await ProductModelLinks.ReconcileTenantAsync(db, seed.Tenant.Id, DateTimeOffset.UtcNow, CancellationToken.None);
    }

    private static Task<List<OperationalIssue>> AmbiguityIssuesAsync(AppDbContext db, Guid tenantId) =>
        db.OperationalIssues.AsNoTracking().Where(x => x.TenantId == tenantId && x.Code == ProductModelLinks.AmbiguousCode).ToListAsync();

    [PostgreSqlFact]
    public async Task AnImportFromEmpty_OpensOneModelPerBrandAndCode_LinksNewColours_AndGivesEachBrandItsOwnModelOfACode()
    {
        var seed = NewSeed();
        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        AddSeed(db, seed);
        await db.SaveChangesAsync();

        // The first colour card of a model opens the model, with the card's fields and no editor save yet.
        var siyah = AddCard(db, seed, seed.Ravencia, "MZ035YNC", "Siyah", "Kadın Siyah Yırtmaçlı Etek", 0);
        await db.SaveChangesAsync();
        var model = await db.ProductModels.AsNoTracking().SingleAsync(x => x.TenantId == seed.Tenant.Id);
        Assert.Equal(("MZ035YNC", "MZ035YNC", seed.Ravencia, "Kadın Siyah Yırtmaçlı Etek", "Siyah"), (model.ModelCode, model.ModelCodeKey, model.BrandId, model.Title, model.TitleColor));
        Assert.Null(model.FieldsSavedAt);
        Assert.Equal(model.Id, (await LinksAsync(db, seed.Tenant.Id))[siyah.Id]);

        // A colour that comes in later under the same code (another spelling of it) joins the model; the cards' own
        // version and dates are not touched by the link.
        var mavi = AddCard(db, seed, seed.Ravencia, " mz035ync ", "Mavi", "Kadın Mavi Yırtmaçlı Etek", 1);
        await db.SaveChangesAsync();
        Assert.Equal(model.Id, (await LinksAsync(db, seed.Tenant.Id))[mavi.Id]);
        Assert.Equal(1L, (await db.Products.AsNoTracking().SingleAsync(x => x.Id == siyah.Id)).Version);

        // Owner (17 Nov 2026, "farklı markalar aynı model kodunu kullanabiliyor"): a model is its brand and its code. KENE01
        // of Lavian opens Lavian's model; KENE01 of Ravencia opens Ravencia's own, nothing refused or warned about.
        var lavian = AddCard(db, seed, seed.Lavian, "KENE01", "Mavi", "Kadın Mavi Asimetrik Kesim Kot Etek", 2);
        // A Turkish-letter code is keyed like any other.
        var turkish = AddCard(db, seed, seed.Ravencia, "TYBCamKırıgı", "Beyaz", "Kadın Beyaz Bluz", 4);
        await db.SaveChangesAsync();
        var ravencia = AddCard(db, seed, seed.Ravencia, "KENE01", "Mavi", "Kadın Denim Asimetrik Kesim Kot Etek", 3);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var links = await LinksAsync(db, seed.Tenant.Id);
        Assert.NotNull(links[lavian.Id]);
        Assert.NotNull(links[turkish.Id]);
        Assert.NotEqual(links[lavian.Id], links[ravencia.Id]);
        Assert.Equal(["KENE01", "KENE01", "MZ035YNC", "TYBCAMKIRIGI"], (await db.ProductModels.AsNoTracking().Where(x => x.TenantId == seed.Tenant.Id).Select(x => x.ModelCodeKey).ToListAsync()).Order(StringComparer.Ordinal));
        Assert.Equal((seed.Lavian, seed.Ravencia), ((await db.ProductModels.AsNoTracking().SingleAsync(x => x.Id == links[lavian.Id])).BrandId!.Value, (await db.ProductModels.AsNoTracking().SingleAsync(x => x.Id == links[ravencia.Id])).BrandId!.Value));
        Assert.Empty(await AmbiguityIssuesAsync(db, seed.Tenant.Id));

        // A code changed on one colour is the model's new code: every colour takes it and the model keeps its id.
        foreach (var variant in await db.ProductVariants.Where(x => x.ProductId == mavi.Id).ToListAsync()) variant.ModelCode = "MZ036";
        await db.SaveChangesAsync();
        links = await LinksAsync(db, seed.Tenant.Id);
        Assert.Equal([model.Id, model.Id], [links[siyah.Id], links[mavi.Id]]);
        var recoded = await db.ProductModels.AsNoTracking().SingleAsync(x => x.Id == model.Id);
        Assert.Equal(("MZ036", "MZ036"), (recoded.ModelCode, recoded.ModelCodeKey));
        Assert.All(await db.ProductVariants.AsNoTracking().Where(x => x.ProductId == siyah.Id).ToListAsync(), variant => Assert.Equal("MZ036", variant.ModelCode));
        // A code another model of the same brand holds is refused.
        db.ChangeTracker.Clear();
        foreach (var variant in await db.ProductVariants.Where(x => x.ProductId == mavi.Id).ToListAsync()) variant.ModelCode = "KENE01";
        await transaction.CreateSavepointAsync("code-in-use");
        Assert.Equal(ModelCodeUniquenessPolicy.ChangeConflictCode, (await Assert.ThrowsAsync<ModelRuleException>(() => db.SaveChangesAsync())).Code);
        await transaction.RollbackToSavepointAsync("code-in-use");
        await transaction.RollbackAsync();
    }

    // KATALOG-14 phase D5b (owner, 17 Nov 2026): the brand is a field of the model, so a brand changed from one colour's
    // editor reaches the model and every colour of it (no colour keeps another brand), and the model keeps its code and id.
    // The editor's own family is the model's colours only (its brand and code).
    [PostgreSqlFact]
    public async Task AnEditorBrandChange_OfOneColour_IsTheModelsBrand_OnEveryColour()
    {
        var seed = NewSeed();
        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        AddSeed(db, seed);
        var mavi = AddCard(db, seed, seed.Ravencia, "KENE01", "Mavi", "Kadın Mavi Asimetrik Kesim Kot Etek", 0);
        var denim = AddCard(db, seed, seed.Ravencia, "KENE01", "Denim Rengi", "Kadın Denim Rengi Asimetrik Kesim Kot Etek", 1);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var model = await db.ProductModels.AsNoTracking().SingleAsync(x => x.TenantId == seed.Tenant.Id);
        Assert.Equal(seed.Ravencia, model.BrandId);

        var service = Service(db);
        var shown = (await service.GetProductAsync(seed.Tenant.Id, denim.Id, CancellationToken.None)).Value!;
        Assert.Equal(new[] { mavi.Id, denim.Id }.Order(), shown.FamilyProductIds!.Order());
        var saved = await service.UpdateProductAsync(seed.Tenant.Id, denim.Id, shown.Version, Command(shown) with { BrandId = seed.Lavian, LoadedFamilyVersions = shown.FamilyVersions }, CancellationToken.None);
        Assert.True(saved.Succeeded, saved.Error?.Message);
        db.ChangeTracker.Clear();

        var after = await db.ProductModels.AsNoTracking().SingleAsync(x => x.TenantId == seed.Tenant.Id);
        Assert.Equal((model.Id, "KENE01", seed.Lavian), (after.Id, after.ModelCodeKey, after.BrandId));
        Assert.All(await db.Products.AsNoTracking().Where(x => x.TenantId == seed.Tenant.Id).ToListAsync(), card => Assert.Equal((seed.Lavian, (Guid?)model.Id), (card.BrandId, card.FamilyId)));
        Assert.Empty(await AmbiguityIssuesAsync(db, seed.Tenant.Id));
        await transaction.RollbackAsync();
    }

    [PostgreSqlFact]
    public async Task ASaveWithUnchangedNumberAttributes_LeavesTheOtherColourCardsAlone()
    {
        var seed = NewSeed();
        var now = DateTimeOffset.UtcNow;
        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        AddSeed(db, seed);
        var attribute = new AttributeDefinition { Id = Guid.CreateVersion7(), TenantId = seed.Tenant.Id, Code = "BOY", Name = "Boy", DataType = AttributeDataType.Number, IsActive = true, CreatedAt = now, UpdatedAt = now, Version = 1 };
        db.AttributeDefinitions.Add(attribute);
        var siyah = AddCard(db, seed, seed.Ravencia, "MZ040", "Siyah", "Kadın Siyah Etek", 0);
        var mavi = AddCard(db, seed, seed.Ravencia, "MZ040", "Mavi", "Kadın Mavi Etek", 1);
        foreach (var card in new[] { siyah, mavi })
            db.ProductAttributeAssignments.Add(new ProductAttributeAssignment { Id = Guid.CreateVersion7(), TenantId = seed.Tenant.Id, ProductId = card.Id, AttributeId = attribute.Id, NumberValue = 5m, SortOrder = 0 });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        // The database gives 5.0000 (numeric(19,4)) and the editor sends 5: the same value, so the Mavi card is not rewritten.
        var service = Service(db);
        var shown = (await service.GetProductAsync(seed.Tenant.Id, siyah.Id, CancellationToken.None)).Value!;
        var saved = await service.UpdateProductAsync(seed.Tenant.Id, siyah.Id, shown.Version, Command(shown, [new ProductAttributeCommand(attribute.Id, null, null, 5m, null, 0)]), CancellationToken.None);
        Assert.True(saved.Succeeded, saved.Error?.Message);
        db.ChangeTracker.Clear();
        Assert.Equal(1L, (await db.Products.AsNoTracking().SingleAsync(x => x.Id == mavi.Id)).Version);
        await transaction.RollbackAsync();
    }

    // Owner (17 Nov 2026): a brand is part of the model's key. An editor brand change that would move the model to a brand
    // already holding a model of the code is refused with a clear message: nothing is merged, no colour changes brand.
    [PostgreSqlFact]
    public async Task AnEditorBrandChange_IntoABrandThatHoldsTheCode_IsRefused_NothingIsMerged()
    {
        var seed = NewSeed();
        await using var db = fixture.CreateContext();
        AddSeed(db, seed);
        var mavi = AddCard(db, seed, seed.Ravencia, "KENE09", "Mavi", "Kadın Mavi Kot Etek", 0);
        var denim = AddCard(db, seed, seed.Ravencia, "KENE09", "Denim Rengi", "Kadın Denim Rengi Kot Etek", 1);
        var lavian = AddCard(db, seed, seed.Lavian, "KENE09", "Gri", "Kadın Gri Kot Etek", 2);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        Assert.Equal(2, await db.ProductModels.CountAsync(x => x.TenantId == seed.Tenant.Id));

        var service = Service(db);
        var shown = (await service.GetProductAsync(seed.Tenant.Id, denim.Id, CancellationToken.None)).Value!;
        Assert.Equal(new[] { mavi.Id, denim.Id }.Order(), shown.FamilyProductIds!.Order());
        var saved = await service.UpdateProductAsync(seed.Tenant.Id, denim.Id, shown.Version, Command(shown) with { BrandId = seed.Lavian, LoadedFamilyVersions = shown.FamilyVersions }, CancellationToken.None);
        Assert.False(saved.Succeeded);
        Assert.Equal(ModelCodeUniquenessPolicy.BrandCodeTakenCode, saved.Error!.Code);
        Assert.StartsWith("Bu Markada Aynı Model Kodu Zaten Var", saved.Error.Message);
        db.ChangeTracker.Clear();
        var cards = await db.Products.AsNoTracking().Where(x => x.TenantId == seed.Tenant.Id).ToDictionaryAsync(x => x.Id);
        Assert.Equal((seed.Ravencia, seed.Ravencia, seed.Lavian), (cards[mavi.Id].BrandId!.Value, cards[denim.Id].BrandId!.Value, cards[lavian.Id].BrandId!.Value));
        Assert.Equal(cards[mavi.Id].FamilyId, cards[denim.Id].FamilyId);
        Assert.NotEqual(cards[mavi.Id].FamilyId, cards[lavian.Id].FamilyId);
        Assert.Equal(2, await db.ProductModels.CountAsync(x => x.TenantId == seed.Tenant.Id));
    }

    [PostgreSqlFact]
    public async Task DeletingCards_RemovesAModelWithoutCards_AndKeepsTheOtherBrandsModelOfTheCode()
    {
        var seed = NewSeed();
        Product single, lavian, ravencia;
        await using (var db = fixture.CreateContext())
        {
            AddSeed(db, seed);
            single = AddCard(db, seed, seed.Ravencia, "MZ050", "Siyah", "Kadın Siyah Etek", 0);
            lavian = AddCard(db, seed, seed.Lavian, "KENE01", "Mavi", "Kadın Mavi Etek", 1);
            ravencia = AddCard(db, seed, seed.Ravencia, "KENE01-ESKI", "Siyah", "Kadın Siyah Kot Etek", 2);
            await db.SaveChangesAsync();
            // Owner (17 Nov 2026): the same code under two brands is two models, nothing reported.
            await SharedCodeFromBeforeAsync(db, seed, ravencia, "KENE01");
            Assert.Empty(await AmbiguityIssuesAsync(db, seed.Tenant.Id));
            Assert.Equal(2, await db.ProductModels.CountAsync(x => x.TenantId == seed.Tenant.Id && x.ModelCodeKey == "KENE01"));
        }
        await using (var db = fixture.CreateContext())
        {
            var service = Service(db);
            Assert.True((await service.DeleteProductAsync(seed.Tenant.Id, single.Id, 1, CancellationToken.None)).Succeeded);
            Assert.False(await db.ProductModels.AsNoTracking().AnyAsync(x => x.TenantId == seed.Tenant.Id && x.ModelCodeKey == "MZ050"));
            Assert.True((await service.DeleteProductAsync(seed.Tenant.Id, lavian.Id, 1, CancellationToken.None)).Succeeded);
            var model = await db.ProductModels.AsNoTracking().SingleAsync(x => x.TenantId == seed.Tenant.Id);
            Assert.Equal((seed.Ravencia, "KENE01"), (model.BrandId, model.ModelCodeKey));
            Assert.Equal(model.Id, (await LinksAsync(db, seed.Tenant.Id))[ravencia.Id]);
            Assert.Empty(await AmbiguityIssuesAsync(db, seed.Tenant.Id));
        }
    }

    [PostgreSqlFact]
    public async Task TheBackfill_LinksTodaysCards_ChangesNoCard_AndRunsTwiceAlike()
    {
        var seed = NewSeed();
        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        AddSeed(db, seed);
        var a1 = AddCard(db, seed, seed.Ravencia, "MZ056", "Gri", "Kadın Gri Bluz", 0);
        var a2 = AddCard(db, seed, seed.Ravencia, "mz056", "Lacivert", "Kadın Lacivert Bluz", 1);
        var a3 = AddCard(db, seed, seed.Ravencia, "MZ056", "Zümrüt", "Kadın Lacivert Bluz", 2);
        var none = AddCard(db, seed, null, "NOBRAND1", "Siyah", "Kadın Siyah Etek", 3);
        var lavian = AddCard(db, seed, seed.Lavian, "KENE01", "Mavi", "Kadın Mavi Etek", 4);
        var ravencia = AddCard(db, seed, seed.Ravencia, "KENE01-ESKI", "Siyah", "Kadın Siyah Kot Etek", 5);
        var turkish = AddCard(db, seed, seed.Ravencia, "TYBCamKırıgı", "Beyaz", "Kadın Beyaz Bluz", 6);
        var other = AddCard(db, seed, seed.Ravencia, "ŒUVRE-1", "Beyaz", "Kadın Beyaz Gömlek", 7);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var applicationLinks = await LinksAsync(db, seed.Tenant.Id);

        // As before the migration: no model, no link, no report, the cards' names as an older release left them (KATALOG-14
        // phase D5b gave the Zümrüt card its model's name when it was saved).
        await db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE catalog.products SET "Title" = 'Kadın Lacivert Bluz' WHERE "Id" = {a3.Id}""");
        // KENE01 under two brands, as only a release before KATALOG-14 phase D5b could leave it.
        await db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE catalog.product_variants SET "ModelCode" = 'KENE01' WHERE "ProductId" = {ravencia.Id}""");
        // KATALOG-14 D5 B1: the column is NOT NULL now; the state before the backfill is made in this transaction only.
        await db.Database.ExecuteSqlRawAsync("""ALTER TABLE catalog.products ALTER COLUMN "FamilyId" DROP NOT NULL""");
        await db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE catalog.products SET "FamilyId" = NULL WHERE "TenantId" = {seed.Tenant.Id}""");
        await db.Database.ExecuteSqlInterpolatedAsync($"""DELETE FROM catalog.product_models WHERE "TenantId" = {seed.Tenant.Id}""");
        await db.Database.ExecuteSqlInterpolatedAsync($"""DELETE FROM ops.operational_issues WHERE "TenantId" = {seed.Tenant.Id}""");
        var cardsBefore = await db.Products.AsNoTracking().Where(x => x.TenantId == seed.Tenant.Id).OrderBy(x => x.Id)
            .Select(x => new { x.Id, x.Title, x.Description, x.BrandId, x.CategoryId, x.VatRate, x.Status, x.Version, x.UpdatedAt }).ToListAsync();
        var variantsBefore = await db.ProductVariants.AsNoTracking().Where(x => x.TenantId == seed.Tenant.Id).OrderBy(x => x.Id)
            .Select(x => new { x.Id, x.ModelCode, x.Sku, x.Version, x.UpdatedAt }).ToListAsync();

        foreach (var sql in ProductModels.BackfillSql) await db.Database.ExecuteSqlRawAsync(sql);
        var models = await db.ProductModels.AsNoTracking().Where(x => x.TenantId == seed.Tenant.Id).ToListAsync();
        var links = await LinksAsync(db, seed.Tenant.Id);
        Assert.Equal(["MZ056", "NOBRAND1", "TYBCAMKIRIGI"], models.Select(x => x.ModelCodeKey).Order(StringComparer.Ordinal));
        var mz056 = models.Single(x => x.ModelCodeKey == "MZ056");
        // The majority's name (on a tie the oldest card's) and the oldest card's code; not saved by the editor yet.
        Assert.Equal(("MZ056", "Kadın Lacivert Bluz", "Lacivert", seed.Ravencia, "Model açıklaması", (int?)1), (mz056.ModelCode, mz056.Title, mz056.TitleColor, mz056.BrandId, mz056.Description, mz056.DeliveryDays));
        Assert.Null(mz056.FieldsSavedAt);
        Assert.Equal([mz056.Id, mz056.Id, mz056.Id], [links[a1.Id], links[a2.Id], links[a3.Id]]);
        Assert.NotNull(links[none.Id]);
        Assert.NotNull(links[turkish.Id]);
        Assert.Null(links[lavian.Id]);
        Assert.Null(links[ravencia.Id]);
        // A code SQL cannot key like the application stays for the application.
        Assert.Null(links[other.Id]);
        var issue = Assert.Single(await AmbiguityIssuesAsync(db, seed.Tenant.Id));
        Assert.Equal("product-model-ambiguous:KENE01", issue.DedupeKey);

        // Again: nothing new.
        foreach (var sql in ProductModels.BackfillSql) await db.Database.ExecuteSqlRawAsync(sql);
        Assert.Equal(models.Select(x => x.Id).Order(), (await db.ProductModels.AsNoTracking().Where(x => x.TenantId == seed.Tenant.Id).Select(x => x.Id).ToListAsync()).Order());
        Assert.Equal(links, await LinksAsync(db, seed.Tenant.Id));
        Assert.Single(await AmbiguityIssuesAsync(db, seed.Tenant.Id));
        // No card or variant changed.
        Assert.Equal(cardsBefore, await db.Products.AsNoTracking().Where(x => x.TenantId == seed.Tenant.Id).OrderBy(x => x.Id)
            .Select(x => new { x.Id, x.Title, x.Description, x.BrandId, x.CategoryId, x.VatRate, x.Status, x.Version, x.UpdatedAt }).ToListAsync());
        Assert.Equal(variantsBefore, await db.ProductVariants.AsNoTracking().Where(x => x.TenantId == seed.Tenant.Id).OrderBy(x => x.Id)
            .Select(x => new { x.Id, x.ModelCode, x.Sku, x.Version, x.UpdatedAt }).ToListAsync());

        // The application agrees with the backfill and links the cards SQL left: the code it cannot key, and (KATALOG-14
        // phase D5b) KENE01's model, of the oldest card's brand, with the Ravencia card on a model of its own.
        // KATALOG-14 D5 B1: since NOT NULL a card left by SQL waits on a model of its own (no NULL link any more).
        await ModelLinkTestStates.PutOnOwnModelAsync(db, seed.Tenant.Id, [.. links.Where(x => x.Value is null).Select(x => x.Key)]);
        await ProductModelLinks.ReconcileTenantAsync(db, seed.Tenant.Id, DateTimeOffset.UtcNow, CancellationToken.None);
        var reconciled = await LinksAsync(db, seed.Tenant.Id);
        Assert.All(links.Where(x => x.Key != other.Id && x.Key != lavian.Id && x.Key != ravencia.Id), pair => Assert.Equal(pair.Value, reconciled[pair.Key]));
        Assert.NotNull(reconciled[other.Id]);
        Assert.NotNull(reconciled[lavian.Id]);
        Assert.NotNull(reconciled[ravencia.Id]);
        Assert.NotEqual(reconciled[lavian.Id], reconciled[ravencia.Id]);
        Assert.Equal(applicationLinks.Keys.Order(), reconciled.Keys.Order());
        Assert.DoesNotContain(null, reconciled.Values);
        Assert.Equal(applicationLinks.Values.Count(x => x is null), reconciled.Values.Count(x => x is null));
        await transaction.RollbackAsync();
    }

    // KATALOG-14 phase D5b, deploy: the previous release, still running after the unique key's migration, opens a second
    // brand's model of a code; its insert is skipped and the card is linked to a model of its own ("#<card id>") although
    // it carries the code. The new release's startup pass (and the nightly scan) links such a card to its brand's model of
    // the code, opening it when the brand has none (owner, 17 Nov 2026: the same code under another brand is another model).
    [PostgreSqlFact]
    public async Task ACodedCardOnAModelOfItsOwn_JoinsItsBrandsModelOfTheCode_OrOpensIt()
    {
        var seed = NewSeed();
        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        AddSeed(db, seed);
        var first = AddCard(db, seed, seed.Ravencia, "KENE05", "Siyah", "Kadın Siyah Etek", 0);
        var same = AddCard(db, seed, seed.Ravencia, "TEMP-1", "Mavi", "Kadın Mavi Etek", 1);
        var other = AddCard(db, seed, seed.Lavian, "TEMP-2", "Gri", "Kadın Gri Etek", 2);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var model = await db.ProductModels.AsNoTracking().SingleAsync(x => x.TenantId == seed.Tenant.Id && x.ModelCodeKey == "KENE05");
        // As the previous release leaves them: the code on the cards, each on a model of its own.
        foreach (var card in new[] { same, other })
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE catalog.product_variants SET "ModelCode" = 'KENE05' WHERE "ProductId" = {card.Id}""");
            await ModelLinkTestStates.PutOnOwnModelAsync(db, seed.Tenant.Id, card.Id);
        }
        await db.Database.ExecuteSqlInterpolatedAsync($"""DELETE FROM catalog.product_models WHERE "TenantId" = {seed.Tenant.Id} AND "ModelCodeKey" LIKE 'TEMP%'""");
        await ProductModelLinks.EnsureOwnModelsAsync(db, seed.Tenant.Id, [same.Id, other.Id], DateTimeOffset.UtcNow, CancellationToken.None);
        var stuck = await (from card in db.Products.AsNoTracking()
                           join record in db.ProductModels.AsNoTracking() on card.FamilyId equals record.Id
                           where card.TenantId == seed.Tenant.Id && record.ModelCodeKey.StartsWith("#")
                           select card.Id).ToListAsync();
        Assert.Equal(new[] { same.Id, other.Id }.Order(), stuck.Order());

        // What the startup pass (ProductModelValues.AlignStaleAsync) and the nightly scan run for each tenant.
        await ProductModelLinks.ReconcileTenantAsync(db, seed.Tenant.Id, DateTimeOffset.UtcNow, CancellationToken.None);

        var links = await LinksAsync(db, seed.Tenant.Id);
        Assert.Equal([model.Id, model.Id], [links[first.Id], links[same.Id]]);
        Assert.NotEqual(model.Id, links[other.Id]);
        var lavianModel = await db.ProductModels.AsNoTracking().SingleAsync(x => x.Id == links[other.Id]);
        Assert.Equal(("KENE05", (Guid?)seed.Lavian), (lavianModel.ModelCodeKey, lavianModel.BrandId));
        Assert.False(await db.ProductModels.AnyAsync(x => x.TenantId == seed.Tenant.Id && x.ModelCodeKey.StartsWith("#")));
        Assert.Empty(await AmbiguityIssuesAsync(db, seed.Tenant.Id));
        await transaction.RollbackAsync();
    }

    [PostgreSqlFact]
    public async Task TheSqlKey_IsTheCatalogKey_ForAsciiAndTurkishCodes()
    {
        string[] codes = ["MZ035YNC", "mz-035 ync", "  abc__01  ", "TYBCamKırıgı…", "İĞÜŞÖÇ ığüşöç", "a/b.c-d", "--x--", "Kene01", new string('a', 200) + "-b"];
        await using var db = fixture.CreateContext();
        foreach (var code in codes)
        {
            var safeSql = "SELECT " + ProductModelLinks.SqlKeySafe("{0}::text") + " AS \"Value\"";
            var safe = await db.Database.SqlQueryRaw<bool>(safeSql, code).SingleAsync();
            if (!safe) { Assert.Contains('…', code); continue; }
            var keySql = "SELECT " + ProductModelLinks.SqlKey("{0}::text") + " AS \"Value\"";
            var key = await db.Database.SqlQueryRaw<string>(keySql, code).SingleAsync();
            Assert.Equal(CatalogKeyNormalizer.Key(code), key);
        }
    }

    // KATALOG-2: a model code corrected on every colour card of a model keeps the model (its id), so its marketplace model
    // links and saved model attributes stay; only its code and key change. KATALOG-14 phase D5b: a code corrected on one
    // colour is corrected on every colour.
    [PostgreSqlFact]
    public async Task RecodingEveryCardOfAModel_KeepsTheModelItsLinksAndAttributes()
    {
        var seed = NewSeed();
        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        AddSeed(db, seed);
        var siyah = AddCard(db, seed, seed.Ravencia, "MZ100", "Siyah", "Kadın Siyah Bluz", 0);
        var mavi = AddCard(db, seed, seed.Ravencia, "MZ100", "Mavi", "Kadın Mavi Bluz", 1);
        var connection = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = seed.Tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = "TRENDYOL", Environment = "PRODUCTION", DisplayName = "Trendyol", ExternalStoreId = $"store-{Guid.NewGuid():N}", Status = "ACTIVE", ApiVersion = "V1", SettingsJson = "{}", Version = 1 };
        db.PlatformConnections.Add(connection);
        var fabric = new AttributeDefinition { Id = Guid.CreateVersion7(), TenantId = seed.Tenant.Id, Code = $"FABRIC-{Guid.NewGuid():N}", Name = "Kumaş", DataType = AttributeDataType.Text, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, Version = 1 };
        db.AttributeDefinitions.Add(fabric);
        await db.SaveChangesAsync();
        var model = await db.ProductModels.AsNoTracking().SingleAsync(x => x.TenantId == seed.Tenant.Id);
        var siyahVariant = await db.ProductVariants.AsNoTracking().Where(x => x.ProductId == siyah.Id).OrderBy(x => x.SortOrder).FirstAsync();
        db.MarketplaceModelLinks.Add(new MarketplaceModelLink { Id = Guid.CreateVersion7(), TenantId = seed.Tenant.Id, ConnectionId = connection.Id, ModelId = model.Id, ExternalId = "TY-MZ100", CreatedAt = DateTimeOffset.UtcNow, Version = 1 });
        db.MarketplaceVariantLinks.Add(new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = seed.Tenant.Id, ConnectionId = connection.Id, VariantId = siyahVariant.Id, ExternalId = siyahVariant.Barcode!, Version = 1 });
        // The model's attribute as every save writes it (KATALOG-14 phase B): the set its colour cards hold. Inside a
        // transaction a save refreshes the model after the commit; this test's transaction never commits, so the refresh
        // is run here.
        foreach (var card in new[] { siyah, mavi })
            db.ProductAttributeAssignments.Add(new ProductAttributeAssignment { Id = Guid.CreateVersion7(), TenantId = seed.Tenant.Id, ProductId = card.Id, AttributeId = fabric.Id, TextValue = "Pamuk", Version = 1 });
        await db.SaveChangesAsync();
        await ProductModelValues.RefreshAsync(db, seed.Tenant.Id, [model.Id], DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.Equal("Pamuk", (await db.ProductModelAttributes.AsNoTracking().SingleAsync(x => x.ModelId == model.Id)).TextValue);
        db.ChangeTracker.Clear();

        // The seller corrects the code on every colour card.
        foreach (var variant in await db.ProductVariants.Where(x => x.TenantId == seed.Tenant.Id).ToListAsync()) variant.ModelCode = "MZ100-YENI";
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var recoded = await db.ProductModels.AsNoTracking().SingleAsync(x => x.TenantId == seed.Tenant.Id);
        Assert.Equal((model.Id, "MZ100-YENI", "MZ100-YENI"), (recoded.Id, recoded.ModelCode, recoded.ModelCodeKey));
        var links = await LinksAsync(db, seed.Tenant.Id);
        Assert.Equal([model.Id, model.Id], [links[siyah.Id], links[mavi.Id]]);
        Assert.Equal("TY-MZ100", (await db.MarketplaceModelLinks.AsNoTracking().SingleAsync(x => x.ModelId == model.Id)).ExternalId);
        Assert.Equal("Pamuk", (await db.ProductModelAttributes.AsNoTracking().SingleAsync(x => x.ModelId == model.Id)).TextValue);

        // KATALOG-14 phase D5b (owner, 17 Nov 2026): a code changed on one colour is the model's: the Siyah card follows
        // the Mavi card's new code and the model keeps its id, links and attributes.
        foreach (var variant in await db.ProductVariants.Where(x => x.ProductId == mavi.Id).ToListAsync()) variant.ModelCode = "MZ200";
        await db.SaveChangesAsync();
        links = await LinksAsync(db, seed.Tenant.Id);
        Assert.Equal([model.Id, model.Id], [links[siyah.Id], links[mavi.Id]]);
        Assert.Equal("MZ200", (await db.ProductModels.AsNoTracking().SingleAsync(x => x.Id == model.Id)).ModelCodeKey);
        Assert.All(await db.ProductVariants.AsNoTracking().Where(x => x.TenantId == seed.Tenant.Id).ToListAsync(), variant => Assert.Equal("MZ200", variant.ModelCode));
        Assert.Single(await db.MarketplaceModelLinks.AsNoTracking().Where(x => x.ModelId == model.Id).ToListAsync());
        await transaction.RollbackAsync();
    }

    // KATALOG-6: two reconciles of one key at once (an import and an editor save) take turns: every card ends linked to the
    // key's one model, none is left unlinked by the other's "model without cards" delete.
    [PostgreSqlFact]
    public async Task ConcurrentReconcilesOfOneKey_LeaveEveryCardLinked()
    {
        var seed = NewSeed();
        await using (var db = fixture.CreateContext())
        {
            AddSeed(db, seed);
            AddCard(db, seed, seed.Ravencia, "MZ500", "Siyah", "Kadın Siyah Etek", 0);
            AddCard(db, seed, seed.Ravencia, "MZ500", "Mavi", "Kadın Mavi Etek", 1);
            await db.SaveChangesAsync();
        }
        for (var round = 0; round < 8; round++)
        {
            await using (var reset = fixture.CreateContext())
            {
                // KATALOG-14 D5 B1: no NULL link any more; the cards wait on models of their own.
                await ModelLinkTestStates.PutOnOwnModelAsync(reset, seed.Tenant.Id, [.. await reset.Products.Where(x => x.TenantId == seed.Tenant.Id).Select(x => x.Id).ToListAsync()]);
                await reset.ProductModels.Where(x => x.TenantId == seed.Tenant.Id && !x.ModelCodeKey.StartsWith("#")).ExecuteDeleteAsync();
            }
            await using var first = fixture.CreateContext();
            await using var second = fixture.CreateContext();
            await Task.WhenAll(
                ProductModelLinks.ReconcileAsync(first, seed.Tenant.Id, ["MZ500"], DateTimeOffset.UtcNow, CancellationToken.None),
                ProductModelLinks.ReconcileAsync(second, seed.Tenant.Id, ["MZ500"], DateTimeOffset.UtcNow, CancellationToken.None));
            await using var check = fixture.CreateContext();
            var model = await check.ProductModels.AsNoTracking().SingleAsync(x => x.TenantId == seed.Tenant.Id);
            Assert.All((await LinksAsync(check, seed.Tenant.Id)).Values, familyId => Assert.Equal(model.Id, familyId));
        }
    }

    // KATALOG-6: a model link that failed after a save is recorded as one open issue per tenant, not swallowed.
    [PostgreSqlFact]
    public async Task AFailedModelLink_IsRecordedAsOneOpenIssue()
    {
        var seed = NewSeed();
        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        AddSeed(db, seed);
        await db.SaveChangesAsync();
        await ProductModelLinks.RecordLinkFailureAsync(db, seed.Tenant.Id, new InvalidOperationException("ilk"), DateTimeOffset.UtcNow);
        await ProductModelLinks.RecordLinkFailureAsync(db, seed.Tenant.Id, new InvalidOperationException("ikinci"), DateTimeOffset.UtcNow);
        var issue = await db.OperationalIssues.AsNoTracking().SingleAsync(x => x.TenantId == seed.Tenant.Id && x.Code == ProductModelLinks.LinkFailedCode);
        Assert.Equal((IssueStatus.Open, 2), (issue.Status, issue.OccurrenceCount));
        Assert.Contains("ikinci", issue.Summary);
        await transaction.RollbackAsync();
    }

    // KATALOG-7: the Kategoriler reset empties the cards' category and deletes the categories; the models stay (their
    // category emptied) with their marketplace links and saved attributes. Needs the migration's
    // ON DELETE SET NULL ("CategoryId") on FK_product_models_categories_TenantId_CategoryId.
    [PostgreSqlFact]
    public async Task ADeletedCategory_EmptiesTheModelsCategory_AndKeepsTheModelAndItsLinks()
    {
        var seed = NewSeed();
        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        AddSeed(db, seed);
        AddCard(db, seed, seed.Ravencia, "MZ300", "Siyah", "Kadın Siyah Etek", 0);
        var connection = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = seed.Tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = "TRENDYOL", Environment = "PRODUCTION", DisplayName = "Trendyol", ExternalStoreId = $"store-{Guid.NewGuid():N}", Status = "ACTIVE", ApiVersion = "V1", SettingsJson = "{}", Version = 1 };
        db.PlatformConnections.Add(connection);
        await db.SaveChangesAsync();
        var model = await db.ProductModels.AsNoTracking().SingleAsync(x => x.TenantId == seed.Tenant.Id);
        Assert.Equal(seed.CategoryId, model.CategoryId);
        db.MarketplaceModelLinks.Add(new MarketplaceModelLink { Id = Guid.CreateVersion7(), TenantId = seed.Tenant.Id, ConnectionId = connection.Id, ModelId = model.Id, ExternalId = "TY-MZ300", CreatedAt = DateTimeOffset.UtcNow, Version = 1 });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        // As PanelFactoryResetInventory's Kategoriler group: the cards let go of the category, then the categories go.
        await db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE catalog.products SET "CategoryId" = NULL WHERE "TenantId" = {seed.Tenant.Id}""");
        await db.Database.ExecuteSqlInterpolatedAsync($"""DELETE FROM catalog.categories WHERE "TenantId" = {seed.Tenant.Id}""");

        var kept = await db.ProductModels.AsNoTracking().SingleAsync(x => x.Id == model.Id);
        Assert.Equal((seed.Tenant.Id, (Guid?)null), (kept.TenantId, kept.CategoryId));
        Assert.Single(await db.MarketplaceModelLinks.AsNoTracking().Where(x => x.ModelId == model.Id).ToListAsync());
        await transaction.RollbackAsync();
    }

    // KATALOG-15: the database itself keeps a card on its own tenant's model and a card's attribute written once. Needs the
    // migration's tenant-scoped FamilyId key and the NULLS NOT DISTINCT unique index.
    [PostgreSqlFact]
    public async Task TheDatabase_RefusesAForeignTenantsModel_AndACardAttributeWrittenTwice()
    {
        var seed = NewSeed();
        var other = NewSeed();
        await using var db = fixture.CreateContext();
        AddSeed(db, seed);
        AddSeed(db, other);
        var card = AddCard(db, seed, seed.Ravencia, "MZ400", "Siyah", "Kadın Siyah Etek", 0);
        AddCard(db, other, other.Ravencia, "MZ400", "Mavi", "Kadın Mavi Etek", 0);
        var fabric = new AttributeDefinition { Id = Guid.CreateVersion7(), TenantId = seed.Tenant.Id, Code = $"FABRIC-{Guid.NewGuid():N}", Name = "Kumaş", DataType = AttributeDataType.Text, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, Version = 1 };
        db.AttributeDefinitions.Add(fabric);
        await db.SaveChangesAsync();
        var foreignModel = await db.ProductModels.AsNoTracking().Where(x => x.TenantId == other.Tenant.Id).Select(x => x.Id).SingleAsync();

        var crossTenant = await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
            db.Products.Where(x => x.Id == card.Id).ExecuteUpdateAsync(setters => setters.SetProperty(x => x.FamilyId, foreignModel)));
        Assert.Equal(Npgsql.PostgresErrorCodes.ForeignKeyViolation, crossTenant.SqlState);

        db.ProductAttributeAssignments.Add(new ProductAttributeAssignment { Id = Guid.CreateVersion7(), TenantId = seed.Tenant.Id, ProductId = card.Id, AttributeId = fabric.Id, TextValue = "Pamuk" });
        await db.SaveChangesAsync();
        db.ProductAttributeAssignments.Add(new ProductAttributeAssignment { Id = Guid.CreateVersion7(), TenantId = seed.Tenant.Id, ProductId = card.Id, AttributeId = fabric.Id, TextValue = "Keten" });
        var twice = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal(Npgsql.PostgresErrorCodes.UniqueViolation, Assert.IsType<Npgsql.PostgresException>(twice.InnerException).SqlState);
    }

    [PostgreSqlFact]
    // KATALOG-14 phase D5b: a model is its code alone, so cards of one code are one model whatever their brands say.
    public async Task OneModel_IsOneLinkedModelOrOneCode()
    {
        var seed = NewSeed();
        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        AddSeed(db, seed);
        var siyah = AddCard(db, seed, seed.Ravencia, "MZ035YNC", "Siyah", "Kadın Siyah Etek", 0);
        var mavi = AddCard(db, seed, seed.Ravencia, "MZ035YNC", "Mavi", "Kadın Mavi Etek", 1);
        var otherCode = AddCard(db, seed, seed.Ravencia, "MZ036", "Mavi", "Kadın Mavi Etek", 2);
        var otherBrand = AddCard(db, seed, seed.Lavian, "MZ035YNC-ESKI", "Gri", "Kadın Gri Etek", 3);
        await db.SaveChangesAsync();
        await SharedCodeFromBeforeAsync(db, seed, otherBrand, "MZ035YNC");
        Assert.True(await ProductModelLinks.OneModelAsync(db, seed.Tenant.Id, [siyah.Id, mavi.Id], CancellationToken.None));
        Assert.False(await ProductModelLinks.OneModelAsync(db, seed.Tenant.Id, [siyah.Id, otherCode.Id], CancellationToken.None));
        Assert.True(await ProductModelLinks.OneModelAsync(db, seed.Tenant.Id, [siyah.Id, otherBrand.Id], CancellationToken.None));
        await transaction.RollbackAsync();
    }
}
