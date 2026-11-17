using System.Text.Json;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Ai;
using MarketplaceHub.Infrastructure.Persistence;
using MarketplaceHub.Infrastructure.SizeCharts;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// KATALOG-14 phases B and C, KATALOG-6 (scan), KATALOG-13 (model chart): the model record holds its cards' shared values
// after every write path and after the backfill, without changing a card, an editor response or a marketplace payload.
[Collection("postgres-integration")]
public sealed class PostgreSqlProductModelValuesTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    private sealed record Seed(Tenant Tenant, Guid Ravencia, Guid Lavian, Guid CategoryId, Guid FabricId, Guid Cotton, Guid Linen);

    private static async Task<Seed> SeedAsync(AppDbContext db)
    {
        var now = DateTimeOffset.UtcNow;
        var seed = new Seed(new Tenant { Id = Guid.CreateVersion7(), Code = $"values-{Guid.NewGuid():N}", DisplayName = "values", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 },
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        db.Tenants.Add(seed.Tenant);
        db.Categories.Add(new Category { Id = seed.CategoryId, TenantId = seed.Tenant.Id, Name = "Etek", NormalizedName = "ETEK", Path = "Etek", IsLeaf = true, IsActive = true, CreatedAt = now, UpdatedAt = now, Version = 1 });
        db.Brands.Add(new Brand { Id = seed.Ravencia, TenantId = seed.Tenant.Id, Name = "Ravencia", NormalizedName = "RAVENCIA", IsActive = true, CreatedAt = now, UpdatedAt = now, Version = 1 });
        db.Brands.Add(new Brand { Id = seed.Lavian, TenantId = seed.Tenant.Id, Name = "Lavian", NormalizedName = "LAVIAN", IsActive = true, CreatedAt = now, UpdatedAt = now, Version = 1 });
        db.AttributeDefinitions.Add(new AttributeDefinition { Id = seed.FabricId, TenantId = seed.Tenant.Id, Code = "KUMAS", Name = "Kumaş", DataType = AttributeDataType.SingleSelect, CreatedAt = now, UpdatedAt = now, Version = 1 });
        db.AttributeValues.Add(new AttributeValue { Id = seed.Cotton, TenantId = seed.Tenant.Id, AttributeId = seed.FabricId, Value = "Pamuk", NormalizedValue = "PAMUK", Version = 1 });
        db.AttributeValues.Add(new AttributeValue { Id = seed.Linen, TenantId = seed.Tenant.Id, AttributeId = seed.FabricId, Value = "Keten", NormalizedValue = "KETEN", Version = 1 });
        await db.SaveChangesAsync();
        return seed;
    }

    // A colour card with two sizes; status, desi and description per card.
    private static Product AddCard(AppDbContext db, Seed seed, Guid? brandId, string? modelCode, string colour, string title, int minute, decimal? desi = 1m, string description = "Model açıklaması", ProductStatus status = ProductStatus.Active, decimal vat = 10m)
    {
        var now = DateTimeOffset.UtcNow;
        var card = new Product { Id = Guid.CreateVersion7(), TenantId = seed.Tenant.Id, Title = title, Description = description, CategoryId = seed.CategoryId, BrandId = brandId, VatRate = vat, VatInclusion = "INCLUDED", DeliveryDays = 1, Status = status, CreatedAt = now.AddMinutes(minute), UpdatedAt = now, Version = 1 };
        db.Products.Add(card);
        var index = 0;
        foreach (var size in new[] { "M", "L" })
        {
            var sku = $"{modelCode ?? "X"}-{colour}-{size}-{Guid.NewGuid():N}"[..40];
            var barcode = $"869{Random.Shared.NextInt64(1_000_000_000L, 9_999_999_999L)}";
            db.ProductVariants.Add(new ProductVariant { Id = Guid.CreateVersion7(), TenantId = seed.Tenant.Id, ProductId = card.Id, Sku = sku, SkuNormalized = sku.ToUpperInvariant(), Barcode = barcode, BarcodeNormalized = barcode, ModelCode = modelCode, OptionSignature = $"Renk: {colour} | Beden: {size}", Desi = desi, DefaultListPrice = 900m, DefaultSalePrice = 600m, Status = status, SortOrder = index++, CreatedAt = now, UpdatedAt = now, Version = 1 });
        }
        return card;
    }

    private CatalogService Service(AppDbContext db) =>
        new(db, new CursorCodec(new EphemeralDataProtectionProvider(), TimeProvider.System), new ConfigurationBuilder().Build(), fixture.TimeProvider, new MemoryCache(new MemoryCacheOptions()));

    private static Task<ProductModel> ModelOfAsync(AppDbContext db, Guid productId) =>
        (from product in db.Products.AsNoTracking() join model in db.ProductModels.AsNoTracking() on product.FamilyId equals model.Id where product.Id == productId select model).SingleAsync();

    private static async Task<List<string>> DescriptionsAsync(AppDbContext db, params Guid[] cardIds)
    {
        var rows = await db.Products.AsNoTracking().Where(x => cardIds.Contains(x.Id)).Select(x => new { x.Id, x.Description }).ToListAsync();
        return [.. cardIds.Select(id => rows.Single(x => x.Id == id).Description)];
    }

    private static async Task<List<string>> TitlesAsync(AppDbContext db, params Guid[] cardIds)
    {
        var rows = await db.Products.AsNoTracking().Where(x => cardIds.Contains(x.Id)).Select(x => new { x.Id, x.Title }).ToListAsync();
        return [.. cardIds.Select(id => rows.Single(x => x.Id == id).Title)];
    }

    private static async Task<List<Guid?>> ModelAttributeValuesAsync(AppDbContext db, Guid modelId) =>
        await db.ProductModelAttributes.AsNoTracking().Where(x => x.ModelId == modelId).OrderBy(x => x.SortOrder).Select(x => x.ValueId).ToListAsync();

    private static ProductSizeChart Chart(Seed seed, Guid productId, string boy) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = seed.Tenant.Id,
        ProductId = productId,
        UpdatedAt = DateTimeOffset.UtcNow,
        MeasurementsJson = $$"""{"columns":["Boy"],"rows":[{"size":"M","values":["{{boy}}"]},{"size":"L","values":["{{boy}}"]}]}""",
    };

    // Every EF path writes through, with no transaction of the caller (each model in its own locked transaction) and
    // inside one (the save's transaction): the bulk field set, an import-like change of the cards, card attributes, the
    // editor's save, a card's size chart; a card without a chart of its own opens with the model's.
    [PostgreSqlFact]
    public async Task EveryWritePath_LeavesTheModelRecordHoldingItsCardsValues()
    {
        await using var db = fixture.CreateContext();
        var seed = await SeedAsync(db);
        var siyah = AddCard(db, seed, seed.Ravencia, "MZ100", "Siyah", "Kadın Siyah Etek", 0, desi: 1m);
        var mavi = AddCard(db, seed, seed.Ravencia, "MZ100", "Mavi", "Kadın Mavi Etek", 1, desi: 1m);
        var gri = AddCard(db, seed, seed.Ravencia, "MZ100", "Gri", "Kadın Gri Etek", 2, desi: null);
        await db.SaveChangesAsync();
        var model = await ModelOfAsync(db, siyah.Id);
        Assert.Equal(1m, model.Desi);
        Assert.Equal("Model açıklaması", model.Description);
        var service = Service(db);

        // Toplu Değer Ata: desi and VAT go to every colour and to the model.
        Assert.True((await service.BulkSetFieldAsync(seed.Tenant.Id, new BulkProductFieldCommand([siyah.Id], BulkProductFields.Desi, "3"), CancellationToken.None)).Succeeded);
        Assert.True((await service.BulkSetFieldAsync(seed.Tenant.Id, new BulkProductFieldCommand([mavi.Id], BulkProductFields.VatRate, "20"), CancellationToken.None)).Succeeded);
        model = await ModelOfAsync(db, siyah.Id);
        Assert.Equal((3m, 20m), (model.Desi, model.VatRate));

        // KATALOG-14 phase D5b: a save that rewrites one colour's description gives it to the model and every colour.
        db.ChangeTracker.Clear();
        (await db.Products.SingleAsync(x => x.Id == gri.Id)).Description = "Yeni açıklama";
        await db.SaveChangesAsync();
        Assert.Equal("Yeni açıklama", (await ModelOfAsync(db, siyah.Id)).Description);
        Assert.Equal(["Yeni açıklama", "Yeni açıklama", "Yeni açıklama"], await DescriptionsAsync(db, siyah.Id, mavi.Id, gri.Id));
        // A marketplace import never moves the model: the colour takes the model's text back.
        db.ChangeTracker.Clear();
        db.MarketplaceCatalogImport = true;
        (await db.Products.SingleAsync(x => x.Id == mavi.Id)).Description = "Pazaryeri açıklaması";
        await db.SaveChangesAsync();
        db.MarketplaceCatalogImport = false;
        Assert.Equal("Yeni açıklama", (await ModelOfAsync(db, siyah.Id)).Description);
        Assert.Equal(["Yeni açıklama", "Yeni açıklama", "Yeni açıklama"], await DescriptionsAsync(db, siyah.Id, mavi.Id, gri.Id));

        // Card attributes: the set most cards hold.
        foreach (var card in new[] { siyah, mavi })
            db.ProductAttributeAssignments.Add(new ProductAttributeAssignment { Id = Guid.CreateVersion7(), TenantId = seed.Tenant.Id, ProductId = card.Id, AttributeId = seed.FabricId, ValueId = seed.Cotton, Version = 1 });
        db.ProductAttributeAssignments.Add(new ProductAttributeAssignment { Id = Guid.CreateVersion7(), TenantId = seed.Tenant.Id, ProductId = gri.Id, AttributeId = seed.FabricId, ValueId = seed.Linen, Version = 1 });
        await db.SaveChangesAsync();
        Assert.Equal([seed.Cotton], await ModelAttributeValuesAsync(db, model.Id));

        // The editor's save (inside the save's transaction) writes every colour and the model.
        db.ChangeTracker.Clear();
        var shown = (await service.GetProductAsync(seed.Tenant.Id, gri.Id, CancellationToken.None)).Value!;
        var saved = await service.UpdateProductAsync(seed.Tenant.Id, gri.Id, shown.Version, new UpdateProductCommand(shown.Title, "Editör açıklaması", shown.BrandId, shown.CategoryId, WarrantyMonths: 24, VatRate: shown.VatRate, VatInclusion: shown.VatInclusion, DeliveryDays: 3, FamilySharedFields: ProductFamilySharedFields.All.Where(x => x != ProductFamilySharedFields.Attributes).ToList()), CancellationToken.None);
        Assert.True(saved.Succeeded, saved.Error?.Message);
        model = await ModelOfAsync(db, siyah.Id);
        Assert.Equal(("Editör açıklaması", (int?)24, (int?)3), (model.Description, model.WarrantyMonths, model.DeliveryDays));
        Assert.Equal(["Editör açıklaması", "Editör açıklaması", "Editör açıklaması"], await DescriptionsAsync(db, siyah.Id, mavi.Id, gri.Id));

        // KATALOG-13: the chart most charted colours hold is the model's; a colour without one opens with it.
        db.ChangeTracker.Clear();
        db.ProductSizeCharts.AddRange(Chart(seed, siyah.Id, "70"), Chart(seed, mavi.Id, "70"));
        await db.SaveChangesAsync();
        var modelChart = await db.ProductModelSizeCharts.AsNoTracking().SingleAsync(x => x.ModelId == model.Id);
        Assert.Contains("70", modelChart.MeasurementsJson);
        var sizeCharts = new SizeChartService(db, new ClaudeService(new AiSettingsService(db, new EphemeralDataProtectionProvider(), TimeProvider.System), null!, db, TimeProvider.System), null!, TimeProvider.System);
        var griChart = await sizeCharts.GetAsync(seed.Tenant.Id, gri.Id, CancellationToken.None);
        Assert.Equal([70m, 70m], griChart!.Measurements!.Rows.Select(row => row.Values[0]!.Value));
        Assert.False(await db.ProductSizeCharts.AsNoTracking().AnyAsync(x => x.ProductId == gri.Id));
        // Its own chart wins over the model's.
        db.ProductSizeCharts.Add(Chart(seed, gri.Id, "72"));
        await db.SaveChangesAsync();
        Assert.Equal([72m, 72m], (await sizeCharts.GetAsync(seed.Tenant.Id, gri.Id, CancellationToken.None))!.Measurements!.Rows.Select(row => row.Values[0]!.Value));
    }

    // KATALOG-14 phase D5b: the refresh votes on none of the eight model fields (the model is their source): a model keeps
    // its name and description though its colours' copies say otherwise, only a model without a desi yet takes what most
    // of its variants hold (the variants keep their own); a colour's VAT changed before its model was gets the model's
    // back (CloseGapsAsync); idempotent; no card's Version or UpdatedAt is written and the readers read the copies.
    [PostgreSqlFact]
    public async Task TheBackfill_VotesOnNoModelField_IsIdempotent_AndClosesAnOlderCardsGap()
    {
        await using var db = fixture.CreateContext();
        var seed = await SeedAsync(db);
        var agreeing = AddCard(db, seed, seed.Ravencia, "AGREE1", "Siyah", "Kadın Siyah Etek", 0);
        var agreeingMavi = AddCard(db, seed, seed.Ravencia, "AGREE1", "Mavi", "Kadın Mavi Etek", 1);
        var majority = AddCard(db, seed, seed.Ravencia, "MAJOR1", "Siyah", "Kadın Siyah Bluz", 2);
        var majorityMavi = AddCard(db, seed, seed.Ravencia, "MAJOR1", "Mavi", "Kadın Mavi Bluz", 3);
        var majorityGri = AddCard(db, seed, seed.Ravencia, "MAJOR1", "Gri", "Kadın Gri Bluz", 4);
        await db.SaveChangesAsync();
        // A record as an older release left it: no desi, a stale description; MAJOR1's variants with desi 1 ×2 and 2 ×4;
        // AGREE1's Mavi card holding another VAT, written before its model was.
        await db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE catalog.product_models SET "Desi" = NULL, "Description" = 'eski', "UpdatedAt" = now() WHERE "TenantId" = {seed.Tenant.Id}""");
        await db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE catalog.product_variants SET "Desi" = CASE WHEN "ProductId" = {majority.Id} THEN 1 ELSE 2 END WHERE "ProductId" IN ({majority.Id}, {majorityMavi.Id}, {majorityGri.Id})""");
        await db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE catalog.products SET "VatRate" = 20, "UpdatedAt" = now() - interval '1 hour' WHERE "Id" = {agreeingMavi.Id}""");
        var cardsBefore = await db.Products.AsNoTracking().Where(x => x.TenantId == seed.Tenant.Id).OrderBy(x => x.Id).Select(x => new { x.Id, x.Version, x.UpdatedAt, x.Title, x.Description }).ToListAsync();
        var variantsBefore = await db.ProductVariants.AsNoTracking().Where(x => x.TenantId == seed.Tenant.Id).OrderBy(x => x.Id).Select(x => new { x.Id, x.Desi }).ToListAsync();

        var first = await ProductModelValues.RefreshTenantAsync(db, seed.Tenant.Id, DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.Equal((2, 2), (first.Models, first.Written));
        Assert.Equal((1m, "eski"), ((await ModelOfAsync(db, agreeing.Id)).Desi, (await ModelOfAsync(db, agreeing.Id)).Description));
        Assert.Equal((2m, "eski"), ((await ModelOfAsync(db, majority.Id)).Desi, (await ModelOfAsync(db, majority.Id)).Description));
        Assert.Equal((10m, 10m), ((await ModelOfAsync(db, agreeing.Id)).VatRate, (await db.Products.AsNoTracking().SingleAsync(x => x.Id == agreeingMavi.Id)).VatRate));
        Assert.Empty(first.Differing);

        // Again: nothing to write.
        var second = await ProductModelValues.RefreshTenantAsync(db, seed.Tenant.Id, DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.Equal((2, 0), (second.Models, second.Written));
        // No card's text, Version or UpdatedAt, no variant's desi was written; every reader reads the copies.
        Assert.Equal(JsonSerializer.Serialize(cardsBefore), JsonSerializer.Serialize(await db.Products.AsNoTracking().Where(x => x.TenantId == seed.Tenant.Id).OrderBy(x => x.Id).Select(x => new { x.Id, x.Version, x.UpdatedAt, x.Title, x.Description }).ToListAsync()));
        Assert.Equal(JsonSerializer.Serialize(variantsBefore), JsonSerializer.Serialize(await db.ProductVariants.AsNoTracking().Where(x => x.TenantId == seed.Tenant.Id).OrderBy(x => x.Id).Select(x => new { x.Id, x.Desi }).ToListAsync()));
        Assert.Equal(cardsBefore.Select(x => (x.Title, x.Description)), (await EffectiveCatalog.Products(db).Where(x => x.TenantId == seed.Tenant.Id).OrderBy(x => x.Id).Select(x => new { x.Title, x.Description }).ToListAsync()).Select(x => (x.Title, x.Description)));
    }

    // Phase C: the editor's GET of every card of a rich catalog is byte-identical before the backfill and after it. The
    // reference is today's rule (ProductModelLinks.SavedFieldsAsync, else ProductFamilySharedFields.ModelValues), kept here.
    [PostgreSqlFact]
    public async Task TheEditorsResponse_IsTheSame_BeforeAndAfterTheBackfill_AndShowsTheModelsFields()
    {
        await using var db = fixture.CreateContext();
        var seed = await SeedAsync(db);
        var cards = new List<Product>
        {
            AddCard(db, seed, seed.Ravencia, "AGREE1", "Siyah", "Kadın Siyah Etek", 0),
            AddCard(db, seed, seed.Ravencia, "AGREE1", "Mavi", "Kadın Mavi Etek", 1),
            AddCard(db, seed, seed.Ravencia, "DIFF1", "Siyah", "Kadın Siyah Bluz", 2, description: "Bir"),
            AddCard(db, seed, seed.Ravencia, "DIFF1", "Mavi", "Kadın Mavi Bluz Uzun", 3, description: "İki", vat: 20m),
            AddCard(db, seed, seed.Ravencia, "DIFF1", "Gri", "Kadın Gri Bluz", 4, description: "Bir"),
            AddCard(db, seed, seed.Lavian, "KENE01", "Mavi", "Kadın Mavi Kot Etek", 5),
            AddCard(db, seed, seed.Ravencia, "KENE01", "Siyah", "Kadın Denim Kot Etek", 6),
            AddCard(db, seed, seed.Ravencia, null, "Beyaz", "Kodsuz Gömlek", 7),
            AddCard(db, seed, seed.Ravencia, "ARCH1", "Siyah", "Kadın Siyah Ceket", 8, status: ProductStatus.Archived),
            AddCard(db, seed, seed.Ravencia, "ARCH1", "Mavi", "Kadın Mavi Ceket", 9),
            AddCard(db, seed, seed.Ravencia, "DRAFT1", "Siyah", "Kadın Siyah Şort", 10, status: ProductStatus.Draft, desi: null),
            AddCard(db, seed, seed.Ravencia, "SAVED1", "Siyah", "Kadın Siyah Elbise", 11),
            AddCard(db, seed, seed.Ravencia, "SAVED1", "Bordo", "Kadın Bordo Elbise", 12),
        };
        await db.SaveChangesAsync();
        var service = Service(db);
        // SAVED1 saved from the editor (FieldsSavedAt, the record read by today's rule).
        var savedShown = (await service.GetProductAsync(seed.Tenant.Id, cards[^1].Id, CancellationToken.None)).Value!;
        Assert.True((await service.UpdateProductAsync(seed.Tenant.Id, cards[^1].Id, savedShown.Version, new UpdateProductCommand(savedShown.Title, "Kaydedilmiş", savedShown.BrandId, savedShown.CategoryId, VatRate: savedShown.VatRate, VatInclusion: savedShown.VatInclusion, DeliveryDays: savedShown.DeliveryDays, FamilySharedFields: ProductFamilySharedFields.All.Where(x => x != ProductFamilySharedFields.Attributes).ToList()), CancellationToken.None)).Succeeded);
        db.ChangeTracker.Clear();

        async Task<List<string>> ResponsesAsync()
        {
            var responses = new List<string>();
            foreach (var card in cards)
            {
                db.ChangeTracker.Clear();
                var result = await service.GetProductAsync(seed.Tenant.Id, card.Id, CancellationToken.None);
                if (result.Value is not { } view)
                {
                    responses.Add($"{card.Title}: {result.Error?.Code}");
                    continue;
                }
                responses.Add(JsonSerializer.Serialize(view));
                // Today's rule, kept here: the fields the response carries.
                var reference = await TodaysFieldsAsync(db, seed.Tenant.Id, card.Id, view);
                Assert.Equal(reference, (view.Title, view.Description, view.BrandId, view.CategoryId, view.VatRate, view.VatInclusion, view.WarrantyMonths, view.DeliveryDays));
            }
            return responses;
        }

        var before = await ResponsesAsync();
        await ProductModelValues.RefreshTenantAsync(db, seed.Tenant.Id, DateTimeOffset.UtcNow, CancellationToken.None);
        var after = await ResponsesAsync();
        for (var index = 0; index < before.Count; index++) Assert.Equal(before[index], after[index]);
        // The record is read where it holds the cards' one value.
        var agreeing = await db.Products.AsNoTracking().Where(x => x.Id == cards[0].Id || x.Id == cards[1].Id).ToListAsync();
        var shown = (await service.GetProductAsync(seed.Tenant.Id, cards[0].Id, CancellationToken.None)).Value!;
        var fields = agreeing.OrderBy(x => x.CreatedAt).Select(x => new ProductFamilySharedFields.CardFields(x.Id, x.Id == cards[0].Id ? "Siyah" : "Mavi", x.Title, x.Description, x.BrandId, x.CategoryId, x.VatRate, x.VatInclusion, x.WarrantyMonths, x.DeliveryDays)).ToList();
        Assert.NotNull(await ProductModelValues.RecordFieldsAsync(db, seed.Tenant.Id, agreeing[0].FamilyId, agreeing, fields, "Siyah", ProductFamilySharedFields.ModelValues(fields, "Siyah"), CancellationToken.None));
        Assert.Equal("Kadın Siyah Etek", shown.Title);

        // KATALOG-14 phase D5b: the model is the source of the VAT, warranty and delivery days, so a scrambled record (here
        // every record the editor did not save) is what the editor shows; the name and description are read from the
        // colours' copies, which only the model's own writes change (ModelMirror).
        await db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE catalog.product_models SET "Title" = 'Bozuk', "TitleColor" = NULL, "Description" = 'Bozuk', "VatRate" = 99, "WarrantyMonths" = 99, "DeliveryDays" = 99, "Desi" = 99 WHERE "TenantId" = {seed.Tenant.Id} AND "FieldsSavedAt" IS NULL""");
        var scrambledModels = await db.ProductModels.AsNoTracking().Where(x => x.TenantId == seed.Tenant.Id && x.FieldsSavedAt == null).Select(x => x.Id).ToListAsync();
        foreach (var card in cards)
        {
            db.ChangeTracker.Clear();
            if ((await service.GetProductAsync(seed.Tenant.Id, card.Id, CancellationToken.None)).Value is not { } view) continue;
            var expected = JsonSerializer.Deserialize<ProductView>(after[cards.IndexOf(card)])!;
            var modelId = (await db.Products.AsNoTracking().SingleAsync(x => x.Id == card.Id)).FamilyId!.Value;
            var scrambled = scrambledModels.Contains(modelId);
            Assert.Equal((expected.Title, expected.Description), (view.Title, view.Description));
            Assert.Equal(scrambled ? (99m, 99, 99) : (expected.VatRate, expected.WarrantyMonths, expected.DeliveryDays), (view.VatRate, view.WarrantyMonths, view.DeliveryDays));
        }
    }

    private static async Task<(string, string, Guid?, Guid?, decimal?, string?, int?, int?)> TodaysFieldsAsync(AppDbContext db, Guid tenantId, Guid productId, ProductView view)
    {
        var family = await db.Products.AsNoTracking().Where(x => view.FamilyProductIds!.Contains(x.Id)).ToListAsync();
        var ordered = family.OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).ToList();
        var signatures = await db.ProductVariants.AsNoTracking().Where(x => view.FamilyProductIds!.Contains(x.ProductId)).OrderBy(x => x.SortOrder).ThenBy(x => x.Id).ToListAsync();
        string? ColorOf(Guid id) => signatures.Where(x => x.ProductId == id).Select(x => ProductFamilyColorOrder.ColorOf(x.OptionSignature)).FirstOrDefault(color => !string.IsNullOrWhiteSpace(color));
        var cardFields = ordered.Select(x => new ProductFamilySharedFields.CardFields(x.Id, ColorOf(x.Id), x.Title, x.Description, x.BrandId, x.CategoryId, x.VatRate, x.VatInclusion, x.WarrantyMonths, x.DeliveryDays)).ToList();
        var opened = family.Single(x => x.Id == productId);
        var fields = await ProductModelLinks.SavedFieldsAsync(db, tenantId, opened.FamilyId, family, ColorOf(productId), CancellationToken.None)
            ?? ProductFamilySharedFields.ModelValues(cardFields, ColorOf(productId));
        return (fields.Title, fields.Description, fields.BrandId, fields.CategoryId, fields.VatRate, fields.VatInclusion, fields.WarrantyMonths, fields.DeliveryDays);
    }

    // A command interceptor for the review's failure cases: commands whose text matches fail with the given exception
    // while Fail is set, and the matching commands are counted.
    private sealed class CommandTap(Func<string, bool> matches, Func<Exception>? failure = null) : DbCommandInterceptor
    {
        public bool Fail { get; set; }
        public int Count { get; private set; }

        private void Check(System.Data.Common.DbCommand command)
        {
            if (!matches(command.CommandText)) return;
            Count++;
            if (Fail && failure is not null) throw failure();
        }

        public override InterceptionResult<System.Data.Common.DbDataReader> ReaderExecuting(System.Data.Common.DbCommand command, CommandEventData eventData, InterceptionResult<System.Data.Common.DbDataReader> result) { Check(command); return result; }
        public override ValueTask<InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(System.Data.Common.DbCommand command, CommandEventData eventData, InterceptionResult<System.Data.Common.DbDataReader> result, CancellationToken cancellationToken = default) { Check(command); return ValueTask.FromResult(result); }
        public override InterceptionResult<int> NonQueryExecuting(System.Data.Common.DbCommand command, CommandEventData eventData, InterceptionResult<int> result) { Check(command); return result; }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(System.Data.Common.DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) { Check(command); return ValueTask.FromResult(result); }
    }

    private AppDbContext ContextWith(IInterceptor interceptor)
    {
        using var plain = fixture.CreateContext();
        return new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(plain.Database.GetConnectionString()).AddInterceptors(interceptor).Options);
    }

    // The refresh's lock of one model row (ProductModelValues.RefreshAsync, outside a caller's transaction).
    private static bool LocksAModel(string sql) => sql.Contains("FROM catalog.product_models", StringComparison.Ordinal) && sql.Contains("FOR UPDATE", StringComparison.Ordinal);

    // F1, KATALOG-14 phase D5a: the model's values are written in the save's own transaction. A model write that fails
    // with a database error fails the save and rolls it back whole (no card without its model's values); the caller's
    // tracked entities keep their changes, so an import that saves again writes them and the model.
    private static bool WritesAModel(string sql) => sql.Contains("UPDATE catalog.product_models", StringComparison.Ordinal);

    [PostgreSqlFact]
    public async Task AFailedRefresh_LeavesTheCallersTrackedEntitiesTracked_AndTheNextSaveWritesThem()
    {
        var tap = new CommandTap(WritesAModel, () => new Npgsql.PostgresException("injected", "ERROR", "ERROR", Npgsql.PostgresErrorCodes.ForeignKeyViolation));
        await using var db = ContextWith(tap);
        var seed = await SeedAsync(db);
        var siyah = AddCard(db, seed, seed.Ravencia, "MZ300", "Siyah", "Kadın Siyah Etek", 0);
        var mavi = AddCard(db, seed, seed.Ravencia, "MZ300", "Mavi", "Kadın Mavi Etek", 1);
        await db.SaveChangesAsync();

        tap.Fail = true;
        siyah.Description = "Pazaryeri açıklaması";
        mavi.Description = "Pazaryeri açıklaması";
        await Assert.ThrowsAnyAsync<Exception>(() => db.SaveChangesAsync());
        tap.Fail = false;
        Assert.Equal("Model açıklaması", (await ModelOfAsync(db, siyah.Id)).Description);
        Assert.Equal("Model açıklaması", (await db.Products.AsNoTracking().SingleAsync(x => x.Id == siyah.Id)).Description);
        Assert.Equal(EntityState.Modified, db.Entry(siyah).State);
        Assert.Equal(EntityState.Modified, db.Entry(mavi).State);

        mavi.WarrantyMonths = 12;
        await db.SaveChangesAsync();
        Assert.Equal(12, (await db.Products.AsNoTracking().SingleAsync(x => x.Id == mavi.Id)).WarrantyMonths);
        Assert.Equal("Pazaryeri açıklaması", (await db.Products.AsNoTracking().SingleAsync(x => x.Id == siyah.Id)).Description);
        Assert.Equal("Pazaryeri açıklaması", (await ModelOfAsync(db, siyah.Id)).Description);
        Assert.Equal(EntityState.Unchanged, db.Entry(siyah).State);
    }

    // Another session holding the model row locked (an editor save of the model) until it is disposed.
    private async Task<AppDbContext> LockModelAsync(Guid modelId)
    {
        var locker = fixture.CreateContext();
        await locker.Database.BeginTransactionAsync();
        await locker.Database.ExecuteSqlInterpolatedAsync($"""SELECT 1 FROM catalog.product_models WHERE "Id" = {modelId} FOR UPDATE""");
        return locker;
    }

    // F3: the refresh's own transaction waits a few seconds for a model a save holds, then skips it for the next run; a
    // refresh inside a caller's transaction leaves that transaction's lock wait as it is.
    [PostgreSqlFact]
    public async Task TheRefresh_WaitsBriefly_ForALockedModel_AndSkipsIt()
    {
        await using var db = fixture.CreateContext();
        var seed = await SeedAsync(db);
        var siyah = AddCard(db, seed, seed.Ravencia, "MZ500", "Siyah", "Kadın Siyah Etek", 0);
        AddCard(db, seed, seed.Ravencia, "MZ500", "Mavi", "Kadın Mavi Etek", 1);
        await db.SaveChangesAsync();
        var model = await ModelOfAsync(db, siyah.Id);
        // KATALOG-14 phase D5b: the refresh writes only a missing desi of the eight model fields.
        await db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE catalog.product_models SET "Desi" = NULL WHERE "Id" = {model.Id}""");

        await using (await LockModelAsync(model.Id))
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var outcome = await ProductModelValues.RefreshAsync(db, seed.Tenant.Id, [model.Id], DateTimeOffset.UtcNow, CancellationToken.None);
            Assert.InRange(clock.Elapsed, TimeSpan.FromSeconds(2.5), TimeSpan.FromSeconds(20));
            Assert.Equal((0, 0, 1), (outcome.Models, outcome.Written, outcome.Skipped));
        }
        Assert.Null((await ModelOfAsync(db, siyah.Id)).Desi);
        Assert.Equal((1, 1, 0), await RefreshAsync());

        await using (await db.Database.BeginTransactionAsync())
        {
            await ProductModelValues.RefreshAsync(db, seed.Tenant.Id, [model.Id], DateTimeOffset.UtcNow, CancellationToken.None);
            Assert.Equal("0", await db.Database.SqlQueryRaw<string>("""SELECT current_setting('lock_timeout') AS "Value" """).SingleAsync());
        }

        async Task<(int, int, int)> RefreshAsync()
        {
            var outcome = await ProductModelValues.RefreshAsync(db, seed.Tenant.Id, [model.Id], DateTimeOffset.UtcNow, CancellationToken.None);
            return (outcome.Models, outcome.Written, outcome.Skipped);
        }
    }

    // F4: a save inside a caller's serializable transaction (the editor's save, the delete) does not touch the model row,
    // so another session that writes the model meanwhile neither waits for it nor makes its commit fail; the model takes
    // the values after the commit (KATALOG-14 phase D5a keeps this for serializable callers; review, 17 Nov 2026).
    // Meanwhile the readers read the cards, whose values win where they differ from the model.
    [PostgreSqlFact]
    public async Task ASaveInsideATransaction_LeavesTheModelRowAlone_AndRefreshesItAfterTheCommit()
    {
        await using var db = fixture.CreateContext();
        var seed = await SeedAsync(db);
        var siyah = AddCard(db, seed, seed.Ravencia, "MZ600", "Siyah", "Kadın Siyah Etek", 0);
        var mavi = AddCard(db, seed, seed.Ravencia, "MZ600", "Mavi", "Kadın Mavi Etek", 1);
        await db.SaveChangesAsync();
        var model = await ModelOfAsync(db, siyah.Id);

        await using (var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable))
        {
            siyah.Description = "Editör açıklaması";
            mavi.Description = "Editör açıklaması";
            await db.SaveChangesAsync();
            // The readers already give the saved text (the card's, which differs from the model's).
            Assert.Equal("Editör açıklaması", (await EffectiveCatalog.Products(db).SingleAsync(x => x.Id == siyah.Id)).Description);
            // Another session (the scan, a worker's save) writes the model row meanwhile, without waiting.
            await using (var other = fixture.CreateContext())
            {
                await other.Database.OpenConnectionAsync();
                await other.Database.ExecuteSqlRawAsync("SET lock_timeout = '1s'");
                await other.Database.ExecuteSqlInterpolatedAsync($"""UPDATE catalog.product_models SET "UpdatedAt" = now() WHERE "Id" = {model.Id}""");
            }
            await transaction.CommitAsync();
        }
        Assert.Equal("Editör açıklaması", (await ModelOfAsync(db, siyah.Id)).Description);

        // A read-committed transaction writes the model in it; rolled back, the model stays as it was.
        await using (await db.Database.BeginTransactionAsync())
        {
            siyah.WarrantyMonths = 6;
            await db.SaveChangesAsync();
        }
        db.ChangeTracker.Clear();
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await transaction.CommitAsync();
        }
        Assert.Equal(0, (await ModelOfAsync(db, siyah.Id)).WarrantyMonths);
    }

    // KATALOG-14 phase D5a (review, 17 Nov 2026): the model rows are locked before the cards are read in a save's
    // transaction, so two saves of sibling colours take turns: the second reads the first's committed colour and the model
    // takes the value both now hold.
    [PostgreSqlFact]
    public async Task TwoSavesOfSiblingColours_TakeTurnsOnTheModel_AndItTakesTheirOneValue()
    {
        await using var db = fixture.CreateContext();
        var seed = await SeedAsync(db);
        var siyah = AddCard(db, seed, seed.Ravencia, "MZ650", "Siyah", "Kadın Siyah Etek", 0);
        var mavi = AddCard(db, seed, seed.Ravencia, "MZ650", "Mavi", "Kadın Mavi Etek", 1);
        await db.SaveChangesAsync();

        await using var first = fixture.CreateContext();
        await using var second = fixture.CreateContext();
        await using var transaction = await first.Database.BeginTransactionAsync();
        (await first.Products.SingleAsync(x => x.Id == siyah.Id)).Description = "Yeni açıklama";
        await first.SaveChangesAsync();
        // The second save of the other colour waits for the first's model lock.
        (await second.Products.SingleAsync(x => x.Id == mavi.Id)).Description = "Yeni açıklama";
        var waiting = second.SaveChangesAsync();
        await Task.Delay(500);
        Assert.False(waiting.IsCompleted);
        await transaction.CommitAsync();
        await waiting;

        Assert.Equal("Yeni açıklama", (await ModelOfAsync(db, siyah.Id)).Description);
        Assert.Equal(["Yeni açıklama", "Yeni açıklama"], await DescriptionsAsync(db, siyah.Id, mavi.Id));
    }

    // F5, KATALOG-14 phase D5a: any failure of the model write in the save's own transaction (not a database error alone)
    // fails the save: nothing of it stands, no card is left read with another value than its model gives.
    [PostgreSqlFact]
    public async Task ARefreshThatThrowsInTheSave_FailsTheSave_AndNothingStands()
    {
        var tap = new CommandTap(WritesAModel, () => new InvalidCastException("injected"));
        await using var db = ContextWith(tap);
        var seed = await SeedAsync(db);
        var siyah = AddCard(db, seed, seed.Ravencia, "MZ700", "Siyah", "Kadın Siyah Etek", 0);
        var mavi = AddCard(db, seed, seed.Ravencia, "MZ700", "Mavi", "Kadın Mavi Etek", 1);
        await db.SaveChangesAsync();

        tap.Fail = true;
        siyah.WarrantyMonths = 18;
        mavi.WarrantyMonths = 18;
        await Assert.ThrowsAnyAsync<InvalidCastException>(() => db.SaveChangesAsync());
        tap.Fail = false;
        Assert.Equal(0, (await db.Products.AsNoTracking().SingleAsync(x => x.Id == siyah.Id)).WarrantyMonths);
        Assert.Equal(0, (await ModelOfAsync(db, siyah.Id)).WarrantyMonths);
        Assert.False(await db.OperationalIssues.AsNoTracking().AnyAsync(x => x.TenantId == seed.Tenant.Id && x.Code == ProductModelLinks.LinkFailedCode));
    }

    // F6: a run that skipped a model (locked) does not resolve the PRODUCT_MODEL_VALUES_DIFFER warning; the scan visits a
    // tenant whose warning is open though its cards are gone, and resolves it there.
    [PostgreSqlFact]
    public async Task TheWarning_IsNotResolvedByARunThatSkippedAModel_AndTheScanVisitsATenantWithOnlyAWarning()
    {
        await using var db = fixture.CreateContext();
        var seed = await SeedAsync(db);
        // KATALOG-14 phase D5b: what is left for the tenant's warning is a difference in the category attributes (the
        // eight model fields are the model's, so their copies never differ for long).
        var siyah = AddCard(db, seed, seed.Ravencia, "MZ800", "Siyah", "Kadın Siyah Etek", 0);
        var mavi = AddCard(db, seed, seed.Ravencia, "MZ800", "Mavi", "Kadın Mavi Etek", 1);
        db.ProductAttributeAssignments.Add(new ProductAttributeAssignment { Id = Guid.CreateVersion7(), TenantId = seed.Tenant.Id, ProductId = siyah.Id, AttributeId = seed.FabricId, ValueId = seed.Cotton, Version = 1 });
        db.ProductAttributeAssignments.Add(new ProductAttributeAssignment { Id = Guid.CreateVersion7(), TenantId = seed.Tenant.Id, ProductId = mavi.Id, AttributeId = seed.FabricId, ValueId = seed.Linen, Version = 1 });
        await db.SaveChangesAsync();
        var model = await ModelOfAsync(db, siyah.Id);
        await ProductModelValues.RefreshTenantAsync(db, seed.Tenant.Id, DateTimeOffset.UtcNow, CancellationToken.None);
        Task<IssueStatus> StatusAsync() => db.OperationalIssues.AsNoTracking().Where(x => x.TenantId == seed.Tenant.Id && x.DedupeKey == ProductModelValues.DifferDedupeKey).Select(x => x.Status).SingleAsync();
        Assert.Equal(IssueStatus.Open, await StatusAsync());

        // The owner makes the cards agree while the model is locked: the run skips it and the warning stays.
        await db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE catalog.product_attribute_assignments SET "ValueId" = {seed.Cotton} WHERE "TenantId" = {seed.Tenant.Id}""");
        await using (await LockModelAsync(model.Id))
        {
            var outcome = await ProductModelValues.RefreshTenantAsync(db, seed.Tenant.Id, DateTimeOffset.UtcNow, CancellationToken.None);
            Assert.Equal((0, 1), (outcome.Differing.Count, outcome.Skipped));
        }
        Assert.Equal(IssueStatus.Open, await StatusAsync());
        Assert.Empty((await ProductModelValues.RefreshTenantAsync(db, seed.Tenant.Id, DateTimeOffset.UtcNow, CancellationToken.None)).Differing);
        Assert.Equal(IssueStatus.Resolved, await StatusAsync());

        // A tenant whose cards are all gone with its warning still open is visited by the scan.
        var empty = new Tenant { Id = Guid.CreateVersion7(), Code = $"values-{Guid.NewGuid():N}", DisplayName = "values", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, Version = 1 };
        db.Tenants.Add(empty);
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO ops.operational_issues ("Id", "TenantId", "DedupeKey", "Code", "Summary", "Status", "FirstSeenAt", "LastSeenAt", "OccurrenceCount")
            VALUES ({Guid.CreateVersion7()}, {empty.Id}, {ProductModelValues.DifferDedupeKey}, {ProductModelValues.DifferCode}, 'eski', 'Open', now(), now(), 1)
            """);
        Assert.Contains(empty.Id, await ProductModelScan.TenantsAsync(db, CancellationToken.None));
        await ProductModelScan.RunTenantAsync(db, empty.Id, DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.Equal(IssueStatus.Resolved, await db.OperationalIssues.AsNoTracking().Where(x => x.TenantId == empty.Id).Select(x => x.Status).SingleAsync());
    }

    // F8: the model's size chart is written only when it differs: a second run issues no write for it, neither for a
    // model with a chart nor for one without.
    [PostgreSqlFact]
    public async Task ASecondRun_IssuesNoSizeChartWrite()
    {
        var tap = new CommandTap(sql => sql.Contains("product_model_size_charts", StringComparison.Ordinal) && (sql.Contains("INSERT", StringComparison.Ordinal) || sql.Contains("DELETE", StringComparison.Ordinal)));
        await using var db = ContextWith(tap);
        var seed = await SeedAsync(db);
        var siyah = AddCard(db, seed, seed.Ravencia, "MZ900", "Siyah", "Kadın Siyah Etek", 0);
        var mavi = AddCard(db, seed, seed.Ravencia, "MZ900", "Mavi", "Kadın Mavi Etek", 1);
        AddCard(db, seed, seed.Ravencia, "MZ901", "Siyah", "Kadın Siyah Bluz", 2);
        await db.SaveChangesAsync();
        db.ProductSizeCharts.AddRange(Chart(seed, siyah.Id, "70"), Chart(seed, mavi.Id, "70"));
        await db.SaveChangesAsync();
        Assert.True(await db.ProductModelSizeCharts.AsNoTracking().AnyAsync(x => x.TenantId == seed.Tenant.Id));

        var before = tap.Count;
        var outcome = await ProductModelValues.RefreshTenantAsync(db, seed.Tenant.Id, DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.Equal((2, 0), (outcome.Models, outcome.Written));
        Assert.Equal(before, tap.Count);
    }

    // F2: fields and attributes the owner saved in the editor after the last change of every card stay the owner's, even
    // when most cards (here cards the editor does not show) hold another value; the stamps stay; a second run writes
    // nothing either.
    [PostgreSqlFact]
    public async Task OwnerSavedValues_AreKept_AgainstAHiddenCardMajority()
    {
        await using var db = fixture.CreateContext();
        var seed = await SeedAsync(db);
        var siyah = AddCard(db, seed, seed.Ravencia, "MZ400", "Siyah", "Kadın Siyah Etek", 0);
        var mavi = AddCard(db, seed, seed.Ravencia, "MZ400", "Mavi", "Kadın Mavi Etek", 1);
        var gri = AddCard(db, seed, seed.Ravencia, "MZ400", "Gri", "Kadın Gri Etek", 2);
        foreach (var card in new[] { siyah, mavi, gri })
            db.ProductAttributeAssignments.Add(new ProductAttributeAssignment { Id = Guid.CreateVersion7(), TenantId = seed.Tenant.Id, ProductId = card.Id, AttributeId = seed.FabricId, ValueId = seed.Cotton, Version = 1 });
        await db.SaveChangesAsync();
        var model = await ModelOfAsync(db, siyah.Id);
        var cardsChanged = new DateTimeOffset(DateTimeOffset.UtcNow.UtcTicks / 10 * 10, TimeSpan.Zero).AddHours(-1);
        var saved = cardsChanged.AddMinutes(1);
        // The owner saved "Sahibin açıklaması" and Keten; two cards linked without a change of their own hold another value.
        await db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE catalog.products SET "Description" = CASE WHEN "Id" = {siyah.Id} THEN 'Sahibin açıklaması' ELSE 'Gizli kartların açıklaması' END, "UpdatedAt" = {cardsChanged} WHERE "TenantId" = {seed.Tenant.Id}""");
        await db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE catalog.product_models SET "Description" = 'Sahibin açıklaması', "FieldsSavedAt" = {saved}, "AttributesSavedAt" = {saved} WHERE "Id" = {model.Id}""");
        await db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE catalog.product_model_attributes SET "ValueId" = {seed.Linen} WHERE "ModelId" = {model.Id}""");

        foreach (var run in new[] { 1, 2 })
        {
            // KATALOG-14 phase D5b: the refresh votes on no model field, so neither run writes.
            var outcome = await ProductModelValues.RefreshTenantAsync(db, seed.Tenant.Id, DateTimeOffset.UtcNow, CancellationToken.None);
            Assert.Equal((run, 1, 0), (run, outcome.Models, outcome.Written));
            var after = await ModelOfAsync(db, siyah.Id);
            Assert.Equal(("Sahibin açıklaması", (DateTimeOffset?)saved, (DateTimeOffset?)saved), (after.Description, after.FieldsSavedAt?.ToUniversalTime(), after.AttributesSavedAt?.ToUniversalTime()));
            Assert.Equal([seed.Linen], await ModelAttributeValuesAsync(db, model.Id));
            Assert.Equal(["Sahibin açıklaması", "Gizli kartların açıklaması", "Gizli kartların açıklaması"], await DescriptionsAsync(db, siyah.Id, mavi.Id, gri.Id));
        }

        // A card changed after the save: the attributes are the cards' majority again; the description is not voted on
        // (phases D2, D5b) and stays the model's.
        await db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE catalog.products SET "UpdatedAt" = {saved.AddMinutes(1)} WHERE "Id" = {gri.Id}""");
        Assert.Equal(1, (await ProductModelValues.RefreshTenantAsync(db, seed.Tenant.Id, DateTimeOffset.UtcNow, CancellationToken.None)).Written);
        Assert.Equal("Sahibin açıklaması", (await ModelOfAsync(db, siyah.Id)).Description);
        Assert.Equal([seed.Cotton], await ModelAttributeValuesAsync(db, model.Id));
    }

    // KATALOG-6: a card left without its model (FamilyId emptied) is linked by the scan; KATALOG-14 phase D5a: the
    // two-brand code's cards each hold a model of their own brand (never merged), the code stays reported; no card's
    // own columns are written.
    [PostgreSqlFact]
    public async Task TheScan_RelinksACardWhoseLinkFailed_AndLeavesTheTwoBrandCodeAlone()
    {
        await using var db = fixture.CreateContext();
        var seed = await SeedAsync(db);
        var siyah = AddCard(db, seed, seed.Ravencia, "MZ200", "Siyah", "Kadın Siyah Etek", 0);
        var bordo = AddCard(db, seed, seed.Ravencia, "MZ200", "Bordo", "Kadın Bordo Etek", 1);
        var lavian = AddCard(db, seed, seed.Lavian, "KENE01", "Mavi", "Kadın Mavi Kot Etek", 2);
        AddCard(db, seed, seed.Ravencia, "KENE01", "Siyah", "Kadın Siyah Kot Etek", 3);
        await db.SaveChangesAsync();
        var modelId = (await db.Products.AsNoTracking().SingleAsync(x => x.Id == siyah.Id)).FamilyId;
        await db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE catalog.products SET "FamilyId" = NULL WHERE "Id" = {bordo.Id}""");
        var versions = await db.Products.AsNoTracking().Where(x => x.TenantId == seed.Tenant.Id).ToDictionaryAsync(x => x.Id, x => x.Version);

        var result = await ProductModelScan.RunTenantAsync(db, seed.Tenant.Id, DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.Equal((1, 0), (result.UnlinkedCodedBefore, result.UnlinkedCodedAfter));
        Assert.Equal(modelId, (await db.Products.AsNoTracking().SingleAsync(x => x.Id == bordo.Id)).FamilyId);
        var lavianModel = (await db.Products.AsNoTracking().SingleAsync(x => x.Id == lavian.Id)).FamilyId;
        Assert.Equal(seed.Lavian, (await db.ProductModels.AsNoTracking().SingleAsync(x => x.Id == lavianModel)).BrandId);
        Assert.Equal(IssueStatus.Open, (await db.OperationalIssues.AsNoTracking().SingleAsync(x => x.TenantId == seed.Tenant.Id && x.Code == ProductModelLinks.AmbiguousCode)).Status);
        Assert.Equal(versions, await db.Products.AsNoTracking().Where(x => x.TenantId == seed.Tenant.Id).ToDictionaryAsync(x => x.Id, x => x.Version));
    }

    // KATALOG-14 phase D5b: new colour cards in one save open a model with the oldest colour's values (most colours, on a
    // tie the oldest: the editor's rule) and a colour that came with another name takes the model's, with a warning naming
    // the model; a marketplace import that rewrites one colour's description never moves the model (the colour takes the
    // model's back, warned); Toplu Değer Ata on one colour gives the value to the model and every colour.
    [PostgreSqlFact]
    public async Task AnImportAndABulkEdit_LeaveNoColourWithAValueOfItsOwn()
    {
        await using var db = fixture.CreateContext();
        var seed = await SeedAsync(db);
        var siyah = AddCard(db, seed, seed.Ravencia, "IMP1", "Siyah", "Kadın Siyah Midi Etek", 0, description: "Ortak açıklama");
        var mavi = AddCard(db, seed, seed.Ravencia, "IMP1", "Mavi", "Kadın Mavi Midi Etek", 1, description: "Ortak açıklama");
        var gri = AddCard(db, seed, seed.Ravencia, "IMP1", "Gri", "Gri Pileli Etek Kampanya", 2, description: "Ortak açıklama");
        await db.SaveChangesAsync();
        var model = await ModelOfAsync(db, siyah.Id);
        Assert.Equal(("Kadın Siyah Midi Etek", "Siyah", "Ortak açıklama"), (model.Title, model.TitleColor, model.Description));
        Assert.Equal(["Kadın Siyah Midi Etek", "Kadın Mavi Midi Etek", "Kadın Gri Midi Etek"], await TitlesAsync(db, siyah.Id, mavi.Id, gri.Id));
        Task<OperationalIssue> WarningAsync() => db.OperationalIssues.AsNoTracking().SingleAsync(x => x.TenantId == seed.Tenant.Id && x.DedupeKey == ModelMirror.DifferDedupePrefix + model.Id.ToString("N"));
        Assert.Contains("Gri Pileli Etek Kampanya", (await WarningAsync()).Summary);

        // A Pazaryeri import rewrites one colour's description: the model keeps its own and so does the colour.
        db.ChangeTracker.Clear();
        db.MarketplaceCatalogImport = true;
        (await db.Products.SingleAsync(x => x.Id == mavi.Id)).Description = "Mavi rengin açıklaması";
        await db.SaveChangesAsync();
        db.MarketplaceCatalogImport = false;
        Assert.Equal("Ortak açıklama", (await ModelOfAsync(db, siyah.Id)).Description);
        Assert.Equal(["Ortak açıklama", "Ortak açıklama", "Ortak açıklama"], await DescriptionsAsync(db, siyah.Id, mavi.Id, gri.Id));
        var warning = await WarningAsync();
        Assert.Equal(IssueStatus.Open, warning.Status);
        Assert.Contains("pazaryerinden gelen", warning.Summary);
        Assert.Contains("Mavi rengin açıklaması", warning.Summary);

        // Toplu Değer Ata writes the whole model (every colour of the selected card) and resolves the warning.
        Assert.True((await Service(db).BulkSetFieldAsync(seed.Tenant.Id, new BulkProductFieldCommand([mavi.Id], BulkProductFields.Description, "Toplu açıklama"), CancellationToken.None)).Succeeded);
        Assert.Equal("Toplu açıklama", (await ModelOfAsync(db, siyah.Id)).Description);
        Assert.Equal(["Toplu açıklama", "Toplu açıklama", "Toplu açıklama"], await DescriptionsAsync(db, siyah.Id, mavi.Id, gri.Id));
        Assert.Equal(IssueStatus.Resolved, (await WarningAsync()).Status);
    }

    // KATALOG-14 phase D3: a lookup by model code (ownership, family scope, Excel, import matching) finds a linked card by
    // its model's key, the one place the code lives, and a card linked to no model (a code two brands share) by its own.
    [PostgreSqlFact]
    public async Task ALookupByModelCode_ReadsTheModelForALinkedCard_AndTheVariantsForAnUnlinkedOne()
    {
        await using var db = fixture.CreateContext();
        var seed = await SeedAsync(db);
        var linked = AddCard(db, seed, seed.Ravencia, "MZ500", "Siyah", "Kadın Siyah Etek", 0);
        AddCard(db, seed, seed.Lavian, "KENE02", "Mavi", "Kadın Mavi Kot Etek", 1);
        var unlinked = AddCard(db, seed, seed.Ravencia, "KENE02", "Siyah", "Kadın Siyah Kot Etek", 2);
        await db.SaveChangesAsync();
        Assert.NotNull((await db.Products.AsNoTracking().SingleAsync(x => x.Id == linked.Id)).FamilyId);
        // KATALOG-14 phase D5a: the code under two brands: a model of its own brand; the card left without one (its link
        // still to come) is found by its variants.
        Assert.NotNull((await db.Products.AsNoTracking().SingleAsync(x => x.Id == unlinked.Id)).FamilyId);
        await db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE catalog.products SET "FamilyId" = NULL WHERE "Id" = {unlinked.Id}""");

        Assert.Equal(linked.Id, (await ModelCodeOwnership.FindAsync(db, seed.Tenant.Id, ["mz500"], CancellationToken.None))?.ProductId);
        Assert.Contains(unlinked.Id, await ModelCodeOwnership.ProductIdsWithKeys(db, seed.Tenant.Id, ["KENE02"]).ToListAsync());
        // The model's key decides for a linked card: with the model moved to another key the card is not found by the old.
        await db.ProductModels.Where(x => x.TenantId == seed.Tenant.Id && x.ModelCodeKey == "MZ500").ExecuteUpdateAsync(s => s.SetProperty(x => x.ModelCodeKey, "MZ501"));
        Assert.Null(await ModelCodeOwnership.FindAsync(db, seed.Tenant.Id, ["MZ500"], CancellationToken.None));
        Assert.Equal([linked.Id], await ModelCodeOwnership.ProductIdsWithKeys(db, seed.Tenant.Id, ["MZ501"]).ToListAsync());
    }

    // KATALOG-13 / KATALOG-14 phase D4: one size chart per model. The refresh gives the model the picture every colour's
    // chart is (else the oldest colour's); a colour without a chart of its own sends the model's; marking a new picture on
    // a colour that follows the model moves the model's and every following colour's, while a colour with a chart of
    // its own (the exception) keeps it.
    [PostgreSqlFact]
    public async Task TheModelHoldsOneSizeChart_AndACorrectionReachesEveryColourWithoutItsOwn()
    {
        await using var db = fixture.CreateContext();
        var seed = await SeedAsync(db);
        var siyah = AddCard(db, seed, seed.Ravencia, "CHT1", "Siyah", "Kadın Siyah Etek", 0);
        var mavi = AddCard(db, seed, seed.Ravencia, "CHT1", "Mavi", "Kadın Mavi Etek", 1);
        var gri = AddCard(db, seed, seed.Ravencia, "CHT1", "Gri", "Kadın Gri Etek", 2);
        var bordo = AddCard(db, seed, seed.Ravencia, "CHT1", "Bordo", "Kadın Bordo Etek", 3);
        FileAsset Asset(string name) => new() { Id = Guid.CreateVersion7(), TenantId = seed.Tenant.Id, Classification = "PRODUCT_MEDIA_URL", RelativePath = $"https://cdn.example.com/{name}.jpg", MimeType = "image/jpeg", Sha256 = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"), Status = "ACTIVE", CreatedAt = DateTimeOffset.UtcNow };
        var shared = Asset("ortak-tablo");
        var own = Asset("bordo-tablo");
        var corrected = Asset("duzeltilmis-tablo");
        db.FileAssets.AddRange(shared, own, corrected);
        ProductMedia Media(Product card, FileAsset asset, int sort) => new() { Id = Guid.CreateVersion7(), TenantId = seed.Tenant.Id, ProductId = card.Id, FileAssetId = asset.Id, MediaRole = "ORDERED_PRIMARY", SortOrder = sort, Status = "ACTIVE" };
        db.ProductMedia.AddRange(Media(siyah, shared, 0), Media(mavi, shared, 0), Media(bordo, own, 0), Media(siyah, corrected, 1));
        // Siyah and Mavi hold the shared chart, Bordo a chart of its own, Gri none.
        var siyahChart = Chart(seed, siyah.Id, "70");
        siyahChart.FileAssetId = shared.Id;
        var maviChart = Chart(seed, mavi.Id, "70");
        maviChart.FileAssetId = shared.Id;
        var bordoChart = Chart(seed, bordo.Id, "75");
        bordoChart.FileAssetId = own.Id;
        db.ProductSizeCharts.AddRange(siyahChart, maviChart, bordoChart);
        await db.SaveChangesAsync();
        var model = await ModelOfAsync(db, siyah.Id);
        await db.ProductModelSizeCharts.Where(x => x.ModelId == model.Id).ExecuteDeleteAsync();

        // The deploy's refresh: the colours disagree, so the oldest colour's picture and the measurements most hold.
        await ProductModelValues.RefreshTenantAsync(db, seed.Tenant.Id, DateTimeOffset.UtcNow, CancellationToken.None);
        var row = await db.ProductModelSizeCharts.AsNoTracking().SingleAsync(x => x.ModelId == model.Id);
        Assert.Equal(shared.Id, row.FileAssetId);
        Assert.Contains("70", row.MeasurementsJson);
        // A second run writes nothing.
        Assert.Equal(0, (await ProductModelValues.RefreshTenantAsync(db, seed.Tenant.Id, DateTimeOffset.UtcNow, CancellationToken.None)).Written);
        var griCard = await db.Products.AsNoTracking().SingleAsync(x => x.Id == gri.Id);
        var griVariants = await db.ProductVariants.AsNoTracking().Where(x => x.ProductId == gri.Id).ToListAsync();
        Assert.Equal(shared.Id, await SizeChartPlacement.ModelAssetIdAsync(db, seed.Tenant.Id, griCard, griVariants, CancellationToken.None));

        // The owner marks a corrected picture on Siyah (it follows the model): the model and Mavi take it, Gri sends it,
        // Bordo keeps its own.
        db.ChangeTracker.Clear();
        var sizeCharts = new SizeChartService(db, new ClaudeService(new AiSettingsService(db, new EphemeralDataProtectionProvider(), TimeProvider.System), null!, db, TimeProvider.System), null!, TimeProvider.System);
        await sizeCharts.SetAsync(seed.Tenant.Id, Guid.Empty, siyah.Id, corrected.RelativePath, CancellationToken.None);
        Assert.Equal(corrected.Id, (await db.ProductModelSizeCharts.AsNoTracking().SingleAsync(x => x.ModelId == model.Id)).FileAssetId);
        var rows = await db.ProductSizeCharts.AsNoTracking().Where(x => x.TenantId == seed.Tenant.Id).ToDictionaryAsync(x => x.ProductId, x => x.FileAssetId);
        Assert.Equal((corrected.Id, corrected.Id, own.Id), (rows[siyah.Id], rows[mavi.Id], rows[bordo.Id]));
        Assert.Equal(corrected.Id, await SizeChartPlacement.ModelAssetIdAsync(db, seed.Tenant.Id, griCard, griVariants, CancellationToken.None));
        var maviCard = await db.Products.AsNoTracking().SingleAsync(x => x.Id == mavi.Id);
        Assert.Equal(corrected.Id, await SizeChartPlacement.ModelAssetIdAsync(db, seed.Tenant.Id, maviCard, await db.ProductVariants.AsNoTracking().Where(x => x.ProductId == mavi.Id).ToListAsync(), CancellationToken.None));
        var bordoCard = await db.Products.AsNoTracking().SingleAsync(x => x.Id == bordo.Id);
        Assert.Equal(own.Id, await SizeChartPlacement.ModelAssetIdAsync(db, seed.Tenant.Id, bordoCard, await db.ProductVariants.AsNoTracking().Where(x => x.ProductId == bordo.Id).ToListAsync(), CancellationToken.None));
        // The colour without a chart of its own opens with the model's picture and measurements.
        var griView = await sizeCharts.GetAsync(seed.Tenant.Id, gri.Id, CancellationToken.None);
        Assert.Contains("duzeltilmis-tablo", griView!.SizeChartUrl);
        Assert.Equal([70m, 70m], griView.Measurements!.Rows.Select(x => x.Values[0]!.Value));

        // The corrected picture is removed from every colour: a colour without a chart of its own no longer sends it, at
        // once (the model row is read by Shown's rule) and after the refresh, which drops it from the model (review, 17 Nov
        // 2026). Gri then goes by the family rule (the oldest colour still showing a chart: Bordo's own).
        await db.ProductMedia.Where(x => x.TenantId == seed.Tenant.Id && x.FileAssetId == corrected.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "ARCHIVED"));
        Assert.Null(await SizeChartPlacement.ModelRowAssetIdAsync(db, seed.Tenant.Id, model.Id, CancellationToken.None));
        Assert.Equal(own.Id, await SizeChartPlacement.ModelAssetIdAsync(db, seed.Tenant.Id, griCard, griVariants, CancellationToken.None));
        await ProductModelValues.RefreshTenantAsync(db, seed.Tenant.Id, DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.NotEqual(corrected.Id, (await db.ProductModelSizeCharts.AsNoTracking().SingleAsync(x => x.ModelId == model.Id)).FileAssetId);
        Assert.Equal(own.Id, await SizeChartPlacement.ModelAssetIdAsync(db, seed.Tenant.Id, griCard, griVariants, CancellationToken.None));

        // With no colour showing any chart the model sends none.
        await db.ProductMedia.Where(x => x.TenantId == seed.Tenant.Id && x.FileAssetId == own.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "ARCHIVED"));
        Assert.Null(await SizeChartPlacement.ModelAssetIdAsync(db, seed.Tenant.Id, griCard, griVariants, CancellationToken.None));
        await ProductModelValues.RefreshTenantAsync(db, seed.Tenant.Id, DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.Null((await db.ProductModelSizeCharts.AsNoTracking().SingleAsync(x => x.ModelId == model.Id)).FileAssetId);
    }

    // KATALOG-14 phase D2: the refresh of a model whose colours already hold their overrides writes nothing, so a
    // serializable editor save of the model running beside the nightly scan meets no new write to its cards or model.
    [PostgreSqlFact]
    public async Task ASteadyRefresh_WritesNoCardOrModel_BesideASerializableSave()
    {
        await using var db = fixture.CreateContext();
        var seed = await SeedAsync(db);
        var siyah = AddCard(db, seed, seed.Ravencia, "SER1", "Siyah", "Kadın Siyah Etek", 0, description: "Bir");
        AddCard(db, seed, seed.Ravencia, "SER1", "Mavi", "Kadın Mavi Etek", 1, description: "İki");
        await db.SaveChangesAsync();
        await ProductModelValues.RefreshTenantAsync(db, seed.Tenant.Id, DateTimeOffset.UtcNow, CancellationToken.None);

        await using var editor = fixture.CreateContext();
        await using var transaction = await editor.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        var family = await editor.Products.Where(x => x.TenantId == seed.Tenant.Id).ToListAsync();
        await editor.ProductModels.Where(x => x.TenantId == seed.Tenant.Id).ToListAsync();
        await using (var scan = fixture.CreateContext())
            Assert.Equal(0, (await ProductModelValues.RefreshTenantAsync(scan, seed.Tenant.Id, DateTimeOffset.UtcNow, CancellationToken.None)).Written);
        family.Single(x => x.Id == siyah.Id).DeliveryDays = 2;
        foreach (var card in family) card.Version++;
        await editor.SaveChangesAsync();
        await transaction.CommitAsync();
    }
}
