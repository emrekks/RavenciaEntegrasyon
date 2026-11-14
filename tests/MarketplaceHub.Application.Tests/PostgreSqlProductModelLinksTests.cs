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

    private static Task<Dictionary<Guid, Guid?>> LinksAsync(AppDbContext db, Guid tenantId) =>
        db.Products.AsNoTracking().Where(x => x.TenantId == tenantId).ToDictionaryAsync(x => x.Id, x => x.FamilyId);

    private static Task<List<OperationalIssue>> AmbiguityIssuesAsync(AppDbContext db, Guid tenantId) =>
        db.OperationalIssues.AsNoTracking().Where(x => x.TenantId == tenantId && x.Code == ProductModelLinks.AmbiguousCode).ToListAsync();

    [PostgreSqlFact]
    public async Task AnImportFromEmpty_OpensOneModelPerBrandAndCode_LinksNewColours_AndLeavesTwoBrandCodesUnlinked()
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

        // KENE01: one code under two brands opens no model, links nothing and reports it.
        var lavian = AddCard(db, seed, seed.Lavian, "KENE01", "Mavi", "Kadın Mavi Asimetrik Kesim Kot Etek", 2);
        var ravencia = AddCard(db, seed, seed.Ravencia, "KENE01", "Mavi", "Kadın Denim Asimetrik Kesim Kot Etek", 3);
        // A Turkish-letter code is keyed like any other.
        var turkish = AddCard(db, seed, seed.Ravencia, "TYBCamKırıgı", "Beyaz", "Kadın Beyaz Bluz", 4);
        await db.SaveChangesAsync();
        var links = await LinksAsync(db, seed.Tenant.Id);
        Assert.Null(links[lavian.Id]);
        Assert.Null(links[ravencia.Id]);
        Assert.NotNull(links[turkish.Id]);
        Assert.Equal(["MZ035YNC", "TYBCAMKIRIGI"], (await db.ProductModels.AsNoTracking().Where(x => x.TenantId == seed.Tenant.Id).Select(x => x.ModelCodeKey).ToListAsync()).Order(StringComparer.Ordinal));
        var issue = Assert.Single(await AmbiguityIssuesAsync(db, seed.Tenant.Id));
        Assert.Equal((IssueStatus.Open, "product-model-ambiguous:KENE01"), (issue.Status, issue.DedupeKey));
        Assert.Contains("Lavian, Ravencia", issue.Summary);
        // Nobody's brand changed.
        Assert.Equal(seed.Lavian, (await db.Products.AsNoTracking().SingleAsync(x => x.Id == lavian.Id)).BrandId);

        // A card whose code changes leaves its model and opens its own.
        db.ChangeTracker.Clear();
        foreach (var variant in await db.ProductVariants.Where(x => x.ProductId == mavi.Id).ToListAsync()) variant.ModelCode = "MZ036";
        await db.SaveChangesAsync();
        links = await LinksAsync(db, seed.Tenant.Id);
        Assert.Equal(model.Id, links[siyah.Id]);
        Assert.NotNull(links[mavi.Id]);
        Assert.NotEqual(model.Id, links[mavi.Id]);
        await transaction.RollbackAsync();
    }

    [PostgreSqlFact]
    public async Task AnEditorSave_WritesTheModelFields_AndTodaysBrandPullThenLinksTheTwoBrandCode()
    {
        var seed = NewSeed();
        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        AddSeed(db, seed);
        var lavian = AddCard(db, seed, seed.Lavian, "KENE01", "Mavi", "Kadın Mavi Asimetrik Kesim Kot Etek", 0);
        var ravencia = AddCard(db, seed, seed.Ravencia, "KENE01", "Siyah", "Kadın Siyah Asimetrik Kesim Kot Etek", 1);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        Assert.Equal(IssueStatus.Open, Assert.Single(await AmbiguityIssuesAsync(db, seed.Tenant.Id)).Status);

        // The owner saves the Ravencia card: the brand is the model code's, so (as today, f680abdd1) the Lavian card takes
        // Ravencia and joins; now one brand holds the code, the model opens, both cards link and the report closes.
        var service = Service(db);
        var shown = (await service.GetProductAsync(seed.Tenant.Id, ravencia.Id, CancellationToken.None)).Value!;
        var saved = await service.UpdateProductAsync(seed.Tenant.Id, ravencia.Id, shown.Version, Command(shown) with { Description = "Yeni açıklama" }, CancellationToken.None);
        Assert.True(saved.Succeeded, saved.Error?.Message);
        db.ChangeTracker.Clear();
        Assert.Equal(seed.Ravencia, (await db.Products.AsNoTracking().SingleAsync(x => x.Id == lavian.Id)).BrandId);
        var model = await db.ProductModels.AsNoTracking().SingleAsync(x => x.TenantId == seed.Tenant.Id);
        var links = await LinksAsync(db, seed.Tenant.Id);
        Assert.Equal([model.Id, model.Id], [links[lavian.Id], links[ravencia.Id]]);
        Assert.Equal(IssueStatus.Resolved, Assert.Single(await AmbiguityIssuesAsync(db, seed.Tenant.Id)).Status);
        // Write-through: the model holds what the save wrote to every card.
        Assert.NotNull(model.FieldsSavedAt);
        Assert.Equal(("Kadın Siyah Asimetrik Kesim Kot Etek", "Siyah", "Yeni açıklama", seed.CategoryId, 10m, "INCLUDED"), (model.Title, model.TitleColor, model.Description, model.CategoryId, model.VatRate, model.VatInclusion));
        // As today, the card the brand pulled in takes only the brand on this save; the next save writes the rest to it.
        Assert.Equal("Model açıklaması", (await db.Products.AsNoTracking().SingleAsync(x => x.Id == lavian.Id)).Description);

        // The editor's family is the model's cards, from either row.
        Assert.Equal(2, (await service.GetProductAsync(seed.Tenant.Id, lavian.Id, CancellationToken.None)).Value!.FamilyProductIds!.Count);
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

    [PostgreSqlFact]
    public async Task DeletingCards_RemovesAModelWithoutCards_AndLinksACodeLeftUnderOneBrand()
    {
        var seed = NewSeed();
        Product single, lavian, ravencia;
        await using (var db = fixture.CreateContext())
        {
            AddSeed(db, seed);
            single = AddCard(db, seed, seed.Ravencia, "MZ050", "Siyah", "Kadın Siyah Etek", 0);
            lavian = AddCard(db, seed, seed.Lavian, "KENE01", "Mavi", "Kadın Mavi Etek", 1);
            ravencia = AddCard(db, seed, seed.Ravencia, "KENE01", "Siyah", "Kadın Siyah Kot Etek", 2);
            await db.SaveChangesAsync();
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
            Assert.Equal(IssueStatus.Resolved, Assert.Single(await AmbiguityIssuesAsync(db, seed.Tenant.Id)).Status);
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
        var ravencia = AddCard(db, seed, seed.Ravencia, "KENE01", "Siyah", "Kadın Siyah Kot Etek", 5);
        var turkish = AddCard(db, seed, seed.Ravencia, "TYBCamKırıgı", "Beyaz", "Kadın Beyaz Bluz", 6);
        var other = AddCard(db, seed, seed.Ravencia, "ŒUVRE-1", "Beyaz", "Kadın Beyaz Gömlek", 7);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var applicationLinks = await LinksAsync(db, seed.Tenant.Id);

        // As before the migration: no model, no link, no report.
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

        // The application agrees with the backfill and links the one card SQL left.
        await ProductModelLinks.ReconcileTenantAsync(db, seed.Tenant.Id, DateTimeOffset.UtcNow, CancellationToken.None);
        var reconciled = await LinksAsync(db, seed.Tenant.Id);
        Assert.All(links.Where(x => x.Key != other.Id), pair => Assert.Equal(pair.Value, reconciled[pair.Key]));
        Assert.NotNull(reconciled[other.Id]);
        Assert.Equal(applicationLinks.Keys.Order(), reconciled.Keys.Order());
        Assert.Equal(applicationLinks.Values.Count(x => x is null), reconciled.Values.Count(x => x is null));
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

    [PostgreSqlFact]
    public async Task OneModel_IsOneLinkedModelOrOneBrandAndCode()
    {
        var seed = NewSeed();
        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        AddSeed(db, seed);
        var siyah = AddCard(db, seed, seed.Ravencia, "MZ035YNC", "Siyah", "Kadın Siyah Etek", 0);
        var mavi = AddCard(db, seed, seed.Ravencia, "MZ035YNC", "Mavi", "Kadın Mavi Etek", 1);
        var otherCode = AddCard(db, seed, seed.Ravencia, "MZ036", "Mavi", "Kadın Mavi Etek", 2);
        var otherBrand = AddCard(db, seed, seed.Lavian, "MZ035YNC", "Gri", "Kadın Gri Etek", 3);
        await db.SaveChangesAsync();
        Assert.True(await ProductModelLinks.OneModelAsync(db, seed.Tenant.Id, [siyah.Id, mavi.Id], CancellationToken.None));
        Assert.False(await ProductModelLinks.OneModelAsync(db, seed.Tenant.Id, [siyah.Id, otherCode.Id], CancellationToken.None));
        Assert.False(await ProductModelLinks.OneModelAsync(db, seed.Tenant.Id, [siyah.Id, otherBrand.Id], CancellationToken.None));
        await transaction.RollbackAsync();
    }
}
