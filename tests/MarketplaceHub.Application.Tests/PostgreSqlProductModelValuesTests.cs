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

        // An import that rewrites one colour's description: most cards still hold the model's; two of three move it.
        db.ChangeTracker.Clear();
        (await db.Products.SingleAsync(x => x.Id == gri.Id)).Description = "Pazaryeri açıklaması";
        await db.SaveChangesAsync();
        Assert.Equal("Model açıklaması", (await ModelOfAsync(db, siyah.Id)).Description);
        (await db.Products.SingleAsync(x => x.Id == mavi.Id)).Description = "Pazaryeri açıklaması";
        await db.SaveChangesAsync();
        Assert.Equal("Pazaryeri açıklaması", (await ModelOfAsync(db, siyah.Id)).Description);

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

    // The backfill: the editor's majority rule (on a tie the oldest card; the desi on a tie the smaller), idempotent, one
    // warning per tenant naming the models whose cards disagree, resolved when they agree again; nothing else written.
    [PostgreSqlFact]
    public async Task TheBackfill_FollowsTheRule_IsIdempotent_AndWarnsOnlyAboutModelsWhoseCardsDisagree()
    {
        await using var db = fixture.CreateContext();
        var seed = await SeedAsync(db);
        var agreeing = AddCard(db, seed, seed.Ravencia, "AGREE1", "Siyah", "Kadın Siyah Etek", 0);
        AddCard(db, seed, seed.Ravencia, "AGREE1", "Mavi", "Kadın Mavi Etek", 1);
        var majority = AddCard(db, seed, seed.Ravencia, "MAJOR1", "Siyah", "Kadın Siyah Bluz", 2, desi: 1m, description: "Çoğunluk");
        AddCard(db, seed, seed.Ravencia, "MAJOR1", "Mavi", "Kadın Mavi Bluz", 3, desi: 2m, description: "Azınlık");
        AddCard(db, seed, seed.Ravencia, "MAJOR1", "Gri", "Kadın Gri Bluz", 4, desi: 2m, description: "Çoğunluk");
        var tie = AddCard(db, seed, seed.Ravencia, "TIE1", "Siyah", "Kadın Siyah Ceket", 5, description: "En eski");
        AddCard(db, seed, seed.Ravencia, "TIE1", "Mavi", "Kadın Mavi Ceket", 6, description: "Daha yeni");
        // KENE01: one code under two brands stays unlinked and is not a model of this warning.
        AddCard(db, seed, seed.Lavian, "KENE01", "Mavi", "Kadın Mavi Kot Etek", 7);
        AddCard(db, seed, seed.Ravencia, "KENE01", "Siyah", "Kadın Siyah Kot Etek", 8);
        await db.SaveChangesAsync();
        // A record as an older release left it: no desi, a stale description.
        await db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE catalog.product_models SET "Desi" = NULL, "Description" = 'eski' WHERE "TenantId" = {seed.Tenant.Id}""");
        var cardsBefore = await db.Products.AsNoTracking().Where(x => x.TenantId == seed.Tenant.Id).OrderBy(x => x.Id).Select(x => new { x.Id, x.Version, x.UpdatedAt, x.Description }).ToListAsync();

        var first = await ProductModelValues.RefreshTenantAsync(db, seed.Tenant.Id, DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.Equal((3, 3), (first.Models, first.Written));
        Assert.Equal((1m, "Model açıklaması"), ((await ModelOfAsync(db, agreeing.Id)).Desi, (await ModelOfAsync(db, agreeing.Id)).Description));
        // Desi 1 ×2 and 2 ×4 variants: 2; description 2 of 3.
        Assert.Equal((2m, "Çoğunluk"), ((await ModelOfAsync(db, majority.Id)).Desi, (await ModelOfAsync(db, majority.Id)).Description));
        Assert.Equal("En eski", (await ModelOfAsync(db, tie.Id)).Description);
        Assert.Equal(["MAJOR1", "TIE1"], first.Differing.Select(x => x.ModelCode).Order(StringComparer.Ordinal));
        Assert.Equal([ProductModelValues.Description, ProductModelValues.Desi], first.Differing.Single(x => x.ModelCode == "MAJOR1").Fields);
        var issue = await db.OperationalIssues.AsNoTracking().SingleAsync(x => x.TenantId == seed.Tenant.Id && x.Code == ProductModelValues.DifferCode);
        Assert.Equal(IssueStatus.Open, issue.Status);
        Assert.StartsWith("2 modelin", issue.Summary);
        Assert.Contains("MAJOR1, TIE1", issue.Summary);

        // Again: nothing to write.
        var second = await ProductModelValues.RefreshTenantAsync(db, seed.Tenant.Id, DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.Equal((3, 0), (second.Models, second.Written));
        // The cards were never written.
        Assert.Equal(JsonSerializer.Serialize(cardsBefore), JsonSerializer.Serialize(await db.Products.AsNoTracking().Where(x => x.TenantId == seed.Tenant.Id).OrderBy(x => x.Id).Select(x => new { x.Id, x.Version, x.UpdatedAt, x.Description }).ToListAsync()));

        // The owner makes the cards agree: the warning is resolved by the next run.
        await db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE catalog.products SET "Description" = 'Ortak' WHERE "TenantId" = {seed.Tenant.Id}""");
        await db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE catalog.product_variants SET "Desi" = 2 WHERE "TenantId" = {seed.Tenant.Id}""");
        var third = await ProductModelValues.RefreshTenantAsync(db, seed.Tenant.Id, DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.Empty(third.Differing);
        Assert.Equal(IssueStatus.Resolved, (await db.OperationalIssues.AsNoTracking().SingleAsync(x => x.TenantId == seed.Tenant.Id && x.Code == ProductModelValues.DifferCode)).Status);
    }

    // Phase C: the editor's GET of every card of a rich catalog is byte-identical before the backfill, after it and with
    // every model record scrambled: the record is used only where it is today's value. The reference is today's rule
    // (ProductModelLinks.SavedFieldsAsync, else ProductFamilySharedFields.ModelValues), kept here.
    [PostgreSqlFact]
    public async Task TheEditorsResponse_IsTheSame_BeforeAndAfterTheBackfill_AndWithAScrambledRecord()
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

        // Every record the editor did not save scrambled (an editor-saved record is today's rule's own source): the same.
        await db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE catalog.product_models SET "Title" = 'Bozuk', "TitleColor" = NULL, "Description" = 'Bozuk', "VatRate" = 99, "WarrantyMonths" = 99, "DeliveryDays" = 99, "Desi" = 99 WHERE "TenantId" = {seed.Tenant.Id} AND "FieldsSavedAt" IS NULL""");
        var scrambled = await ResponsesAsync();
        for (var index = 0; index < before.Count; index++) Assert.Equal(before[index], scrambled[index]);
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

    // F1: a model refresh that fails with a database error leaves the caller's tracked entities as they are: an import
    // that saves the same tracked cards again saves its next changes.
    [PostgreSqlFact]
    public async Task AFailedRefresh_LeavesTheCallersTrackedEntitiesTracked_AndTheNextSaveWritesThem()
    {
        var tap = new CommandTap(LocksAModel, () => new Npgsql.PostgresException("injected", "ERROR", "ERROR", Npgsql.PostgresErrorCodes.ForeignKeyViolation));
        await using var db = ContextWith(tap);
        var seed = await SeedAsync(db);
        var siyah = AddCard(db, seed, seed.Ravencia, "MZ300", "Siyah", "Kadın Siyah Etek", 0);
        var mavi = AddCard(db, seed, seed.Ravencia, "MZ300", "Mavi", "Kadın Mavi Etek", 1);
        await db.SaveChangesAsync();

        tap.Fail = true;
        siyah.Description = "Pazaryeri açıklaması";
        mavi.Description = "Pazaryeri açıklaması";
        await db.SaveChangesAsync();
        tap.Fail = false;
        Assert.Equal("Model açıklaması", (await ModelOfAsync(db, siyah.Id)).Description);
        Assert.Equal(EntityState.Unchanged, db.Entry(siyah).State);
        Assert.Equal(EntityState.Unchanged, db.Entry(mavi).State);

        mavi.WarrantyMonths = 12;
        await db.SaveChangesAsync();
        Assert.Equal(12, (await db.Products.AsNoTracking().SingleAsync(x => x.Id == mavi.Id)).WarrantyMonths);
        Assert.Equal("Pazaryeri açıklaması", (await ModelOfAsync(db, siyah.Id)).Description);
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
        await db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE catalog.product_models SET "Description" = 'eski' WHERE "Id" = {model.Id}""");

        await using (await LockModelAsync(model.Id))
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var outcome = await ProductModelValues.RefreshAsync(db, seed.Tenant.Id, [model.Id], DateTimeOffset.UtcNow, CancellationToken.None);
            Assert.InRange(clock.Elapsed, TimeSpan.FromSeconds(2.5), TimeSpan.FromSeconds(20));
            Assert.Equal((0, 0, 1), (outcome.Models, outcome.Written, outcome.Skipped));
        }
        Assert.Equal("eski", (await ModelOfAsync(db, siyah.Id)).Description);
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

    // F4: a save inside a caller's (here serializable) transaction does not touch the model row, so another session that
    // writes the model meanwhile neither waits for it nor makes its commit fail; the model takes the values after the
    // commit.
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

        // A transaction rolled back leaves its models to the next commit, which finds the cards as they are.
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
        Assert.Null((await ModelOfAsync(db, siyah.Id)).WarrantyMonths);
    }

    // F5: any failure of the model refresh after a save that stands (outside a caller's transaction) is recorded as the
    // PRODUCT_MODEL_LINK_FAILED issue; the save returns normally and its changes stay.
    [PostgreSqlFact]
    public async Task ARefreshThatThrowsAfterTheSave_IsRecorded_AndTheSaveStands()
    {
        var tap = new CommandTap(LocksAModel, () => new InvalidCastException("injected"));
        await using var db = ContextWith(tap);
        var seed = await SeedAsync(db);
        var siyah = AddCard(db, seed, seed.Ravencia, "MZ700", "Siyah", "Kadın Siyah Etek", 0);
        AddCard(db, seed, seed.Ravencia, "MZ700", "Mavi", "Kadın Mavi Etek", 1);
        await db.SaveChangesAsync();

        tap.Fail = true;
        siyah.WarrantyMonths = 18;
        await db.SaveChangesAsync();
        tap.Fail = false;
        Assert.Equal(18, (await db.Products.AsNoTracking().SingleAsync(x => x.Id == siyah.Id)).WarrantyMonths);
        var issue = await db.OperationalIssues.AsNoTracking().SingleAsync(x => x.TenantId == seed.Tenant.Id && x.Code == ProductModelLinks.LinkFailedCode);
        Assert.Equal(IssueStatus.Open, issue.Status);
        Assert.Contains("InvalidCastException", issue.Summary);
    }

    // F6: a run that skipped a model (locked) does not resolve the PRODUCT_MODEL_VALUES_DIFFER warning; the scan visits a
    // tenant whose warning is open though its cards are gone, and resolves it there.
    [PostgreSqlFact]
    public async Task TheWarning_IsNotResolvedByARunThatSkippedAModel_AndTheScanVisitsATenantWithOnlyAWarning()
    {
        await using var db = fixture.CreateContext();
        var seed = await SeedAsync(db);
        var siyah = AddCard(db, seed, seed.Ravencia, "MZ800", "Siyah", "Kadın Siyah Etek", 0, description: "Bir");
        AddCard(db, seed, seed.Ravencia, "MZ800", "Mavi", "Kadın Mavi Etek", 1, description: "İki");
        await db.SaveChangesAsync();
        var model = await ModelOfAsync(db, siyah.Id);
        await ProductModelValues.RefreshTenantAsync(db, seed.Tenant.Id, DateTimeOffset.UtcNow, CancellationToken.None);
        Task<IssueStatus> StatusAsync() => db.OperationalIssues.AsNoTracking().Where(x => x.TenantId == seed.Tenant.Id && x.Code == ProductModelValues.DifferCode).Select(x => x.Status).SingleAsync();
        Assert.Equal(IssueStatus.Open, await StatusAsync());

        // The owner makes the cards agree while the model is locked: the run skips it and the warning stays.
        await db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE catalog.products SET "Description" = 'Ortak' WHERE "TenantId" = {seed.Tenant.Id}""");
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
            var outcome = await ProductModelValues.RefreshTenantAsync(db, seed.Tenant.Id, DateTimeOffset.UtcNow, CancellationToken.None);
            Assert.Equal((run, 1, 0), (run, outcome.Models, outcome.Written));
            var after = await ModelOfAsync(db, siyah.Id);
            Assert.Equal(("Sahibin açıklaması", (DateTimeOffset?)saved, (DateTimeOffset?)saved), (after.Description, after.FieldsSavedAt?.ToUniversalTime(), after.AttributesSavedAt?.ToUniversalTime()));
            Assert.Equal([seed.Linen], await ModelAttributeValuesAsync(db, model.Id));
        }

        // A card changed after the save: the cards' majority is the model's again.
        await db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE catalog.products SET "UpdatedAt" = {saved.AddMinutes(1)} WHERE "Id" = {gri.Id}""");
        Assert.Equal(1, (await ProductModelValues.RefreshTenantAsync(db, seed.Tenant.Id, DateTimeOffset.UtcNow, CancellationToken.None)).Written);
        Assert.Equal("Gizli kartların açıklaması", (await ModelOfAsync(db, siyah.Id)).Description);
        Assert.Equal([seed.Cotton], await ModelAttributeValuesAsync(db, model.Id));
    }

    // KATALOG-6: a card whose link failed after its save (FamilyId left empty) is linked by the scan; the two-brand code
    // stays unlinked and reported; no card is written.
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
        Assert.Equal((3, 2), (result.UnlinkedCodedBefore, result.UnlinkedCodedAfter));
        Assert.Equal(modelId, (await db.Products.AsNoTracking().SingleAsync(x => x.Id == bordo.Id)).FamilyId);
        Assert.Null((await db.Products.AsNoTracking().SingleAsync(x => x.Id == lavian.Id)).FamilyId);
        Assert.Equal(IssueStatus.Open, (await db.OperationalIssues.AsNoTracking().SingleAsync(x => x.TenantId == seed.Tenant.Id && x.Code == ProductModelLinks.AmbiguousCode)).Status);
        Assert.Equal(versions, await db.Products.AsNoTracking().Where(x => x.TenantId == seed.Tenant.Id).ToDictionaryAsync(x => x.Id, x => x.Version));
    }
}
