using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Imports;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests.Contracts;

// KATALOG-14 phase D5 B2 (readers first): the VAT (rate and inclusion), category, warranty, delivery days, description and
// desi are the model's (step B2a added the description and the desi; the name stays each colour card's own). Before the writers stop keeping the colour cards' copy columns equal to the model, no reader may read those
// copies: each test leaves every copy deliberately stale (other values, or empty) and shows that what the owner and the
// marketplaces get is still the model's. The brand stays out: it is the card's link to its model (the reconcile reads it),
// not a copy.
[Collection("postgres-integration")]
public sealed class StaleCardCopyReadersPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    private static Task MakeCopiesStaleAsync(AppDbContext db, Guid tenantId, decimal? vatRate, string? vatInclusion, int? warranty, int? deliveryDays) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE catalog.products SET "VatRate" = {vatRate}, "VatInclusion" = {vatInclusion}, "CategoryId" = NULL, "WarrantyMonths" = {warranty}, "DeliveryDays" = {deliveryDays}
            WHERE "TenantId" = {tenantId}
            """);

    // Step B2a: every copy the writers still keep (description, VAT rate and inclusion, category, warranty, delivery days and
    // every variant's desi) poisoned with wrong values. The name is the card's own and the brand its link key: both stay.
    internal static async Task PoisonCopiesAsync(AppDbContext db, Guid tenantId)
    {
        await MakeCopiesStaleAsync(db, tenantId, 20m, "EXCLUDED", 3, 9);
        await db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE catalog.products SET "Description" = {"BAYAT KOPYA: hiçbir okuyucu bunu göstermemeli"} WHERE "TenantId" = {tenantId}""");
        await db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE catalog.product_variants SET "Desi" = {7.25m} WHERE "TenantId" = {tenantId}""");
    }

    // Step B2a (item 3): for both seeded models every composed payload of the six marketplaces (create, update, Trendyol
    // price/stock) is byte for byte the same before and after the copies are poisoned, and still the golden file.
    [PostgreSqlTheory]
    [MemberData(nameof(ModelComposerGoldenPostgreSqlTests.Models), MemberType = typeof(ModelComposerGoldenPostgreSqlTests))]
    public async Task PoisonedCopies_EveryComposedPayloadIsByteForByteTheSame(string shape)
    {
        var seeded = await ModelComposerGoldenPostgreSqlTests.SeedAsync(fixture, shape);
        var before = ModelComposerGoldenPostgreSqlTests.Normalize(await ModelComposerGoldenPostgreSqlTests.PayloadsAsync(fixture, seeded), seeded);
        await using (var db = fixture.CreateContext())
        {
            var model = await db.ProductModels.AsNoTracking().SingleAsync(x => x.TenantId == seeded.Tenant.Id);
            Assert.Equal(((decimal?)1m, (decimal?)10m, "INCLUDED", (int?)24, (int?)2), (model.Desi, model.VatRate, model.VatInclusion, model.WarrantyMonths, model.DeliveryDays));
            await PoisonCopiesAsync(db, seeded.Tenant.Id);
        }
        var after = ModelComposerGoldenPostgreSqlTests.Normalize(await ModelComposerGoldenPostgreSqlTests.PayloadsAsync(fixture, seeded), seeded);
        Assert.Equal(before, after);
        Assert.DoesNotContain(after, payload => payload.Contains("BAYAT KOPYA", StringComparison.Ordinal) || payload.Contains("7.25", StringComparison.Ordinal));
        ContractFixture.MatchesGolden($"Catalog/model-composers-{shape}.golden.json", after);
    }

    // Step B2a (poisoned-copy proof for the screens): the editor's product view of every colour, the products file (all rows
    // and the missing-desi filter) with its summary, and an order line's cost snapshot read the model, so they are the same
    // before and after the poisoning.
    [PostgreSqlFact]
    public async Task PoisonedCopies_TheProductViewsTheProductsFileAndTheOrderSnapshotAreUnchanged()
    {
        var seeded = await ModelComposerGoldenPostgreSqlTests.SeedAsync(fixture, "same");
        var before = await ReadEverythingAsync();
        await using (var db = fixture.CreateContext())
            await PoisonCopiesAsync(db, seeded.Tenant.Id);
        var after = await ReadEverythingAsync();
        Assert.Equal(before, after);
        Assert.DoesNotContain("BAYAT KOPYA", after, StringComparison.Ordinal);
        Assert.DoesNotContain("7.25", after, StringComparison.Ordinal);
        Assert.Contains("snapshot 100 1", after, StringComparison.Ordinal);

        async Task<string> ReadEverythingAsync()
        {
            await using var db = fixture.CreateContext();
            var service = new CatalogService(db, null!, new ConfigurationBuilder().Build(), fixture.TimeProvider, new MemoryCache(new MemoryCacheOptions()));
            var parts = new List<string>();
            foreach (var card in seeded.Cards)
            {
                var view = await service.GetProductAsync(seeded.Tenant.Id, card.Id, CancellationToken.None);
                Assert.True(view.Succeeded, view.Error?.Message);
                parts.Add(System.Text.Json.JsonSerializer.Serialize(view.Value));
            }
            var transfer = new ProductTransferService(db, fixture.TimeProvider);
            parts.Add(System.Text.Json.JsonSerializer.Serialize((await transfer.SummaryAsync(seeded.Tenant.Id, CancellationToken.None)).Value));
            foreach (var filter in new[] { ProductTransferExportFilter.All, ProductTransferExportFilter.MissingDesi })
            {
                var export = await transfer.ExportAsync(seeded.Tenant.Id, filter, null, null, CancellationToken.None);
                Assert.True(export.Succeeded, export.Error?.Message);
                var table = ImportFileReader.ReadXlsxTable(new MemoryStream(export.Value!), 64 * 1024 * 1024);
                parts.Add(string.Join("|", table.Headers) + "\n" + string.Join("\n", table.Rows.Select(row => string.Join("|", row.Cells))));
            }
            var variant = await db.ProductVariants.AsNoTracking().Where(x => x.ProductId == seeded.Cards[0].Id).OrderBy(x => x.SortOrder).FirstAsync();
            var line = new OrderLine { Id = Guid.CreateVersion7(), TenantId = seeded.Tenant.Id, ExternalLineId = "l", Sku = variant.Sku, TitleSnapshot = "Bluz", RawStatus = "Created", VariantId = variant.Id };
            await OrderLineCostSnapshot.TakeAsync(db, seeded.Tenant.Id, [line], CancellationToken.None);
            parts.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"snapshot {line.UnitCostSnapshot:0.##} {line.DesiSnapshot:0.##}"));
            return string.Join("\n---\n", parts);
        }
    }

    // Every marketplace's create and update payload (Trendyol, Hepsiburada, n11, Pazarama, PTT AVM, Shopify) and the
    // invoice's VAT are byte for byte what they are with the copies equal to the model (the golden file).
    [PostgreSqlFact]
    public async Task StaleCopies_EveryComposerBuildsThePinnedPayload_AndTheInvoiceTakesTheModelsVat()
    {
        var seeded = await ModelComposerGoldenPostgreSqlTests.SeedAsync(fixture, "same");
        await using (var db = fixture.CreateContext())
            await MakeCopiesStaleAsync(db, seeded.Tenant.Id, 20m, "EXCLUDED", 3, 9);

        var payloads = await ModelComposerGoldenPostgreSqlTests.PayloadsAsync(fixture, seeded);
        ContractFixture.MatchesGolden("Catalog/model-composers-same.golden.json", ModelComposerGoldenPostgreSqlTests.Normalize(payloads, seeded));

        await using (var db = fixture.CreateContext())
        {
            var trendyol = seeded.Connections.Single(x => x.PlatformCode == "TRENDYOL");
            foreach (var card in seeded.Cards)
            {
                var variant = await db.ProductVariants.AsNoTracking().Where(x => x.ProductId == card.Id).OrderBy(x => x.SortOrder).FirstAsync();
                var line = new OrderLine { Id = Guid.CreateVersion7(), TenantId = seeded.Tenant.Id, ExternalLineId = $"l-{card.Id:N}", Sku = variant.Sku, TitleSnapshot = "Bluz", RawStatus = "Created", VariantId = variant.Id };
                Assert.Equal(10m, (await InvoiceLineVatRates.ResolveAsync(db, seeded.Tenant.Id, "TRENDYOL", trendyol.Id, [line], CancellationToken.None))[line.Id]);
            }
        }
    }

    // The products file (Excel) shows the model's VAT and category on every row, and its category filter finds the model's
    // cards (the copy column is empty).
    [PostgreSqlFact]
    public async Task StaleCopies_TheProductsFileExportsTheModelsVatAndCategory()
    {
        var seeded = await ModelComposerGoldenPostgreSqlTests.SeedAsync(fixture, "same");
        await using var db = fixture.CreateContext();
        await MakeCopiesStaleAsync(db, seeded.Tenant.Id, 20m, "EXCLUDED", 3, 9);
        var model = await db.ProductModels.AsNoTracking().SingleAsync(x => x.TenantId == seeded.Tenant.Id);
        Assert.Equal((10m, true), (model.VatRate, model.CategoryId is not null));

        var service = new ProductTransferService(db, fixture.TimeProvider);
        var export = await service.ExportAsync(seeded.Tenant.Id, ProductTransferExportFilter.All, null, model.CategoryId, CancellationToken.None);
        Assert.True(export.Succeeded, export.Error?.Message);
        var table = ImportFileReader.ReadXlsxTable(new MemoryStream(export.Value!), 64 * 1024 * 1024);
        int Column(string header) => table.Headers.ToList().IndexOf(header);
        Assert.Equal(6, table.Rows.Count);
        Assert.All(table.Rows, row =>
        {
            Assert.Equal("%10", row.Cells[Column("KDV Oranı")]);
            Assert.Equal("Bluz", row.Cells[Column("Kategori")]);
            Assert.Equal("Ravencia", row.Cells[Column("Marka")]);
        });
    }

    // An editor save that leaves the VAT out keeps the model's (no "KDV Oranı Gerekli" because a copy is empty), a save
    // giving only the rate takes the model's "KDV Dahil mi", and the required warranty and delivery days are the model's.
    [PostgreSqlFact]
    public async Task StaleCopies_AnEditorSaveChecksAndKeepsTheModelsVatWarrantyAndDeliveryDays()
    {
        var seeded = await ModelComposerGoldenPostgreSqlTests.SeedAsync(fixture, "same");
        await using var db = fixture.CreateContext();
        await MakeCopiesStaleAsync(db, seeded.Tenant.Id, null, null, null, null);
        var model = await db.ProductModels.AsNoTracking().SingleAsync(x => x.TenantId == seeded.Tenant.Id);
        var card = await db.Products.AsNoTracking().SingleAsync(x => x.Id == seeded.Cards[0].Id);
        var service = new CatalogService(db, null!, new ConfigurationBuilder().Build(), fixture.TimeProvider, new MemoryCache(new MemoryCacheOptions()));

        var kept = await service.UpdateProductAsync(seeded.Tenant.Id, card.Id, card.Version, new UpdateProductCommand(card.Title, model.Description, model.BrandId, model.CategoryId), CancellationToken.None);
        Assert.True(kept.Succeeded, kept.Error?.Message);
        Assert.Equal((10m, "INCLUDED", (int?)24, (int?)2), (kept.Value!.VatRate, kept.Value.VatInclusion, kept.Value.WarrantyMonths, kept.Value.DeliveryDays));

        db.ChangeTracker.Clear();
        var rateOnly = await service.UpdateProductAsync(seeded.Tenant.Id, card.Id, kept.Value.Version, new UpdateProductCommand(card.Title, model.Description, model.BrandId, model.CategoryId, VatRate: 20m), CancellationToken.None);
        Assert.True(rateOnly.Succeeded, rateOnly.Error?.Message);
        db.ChangeTracker.Clear();
        var saved = await db.ProductModels.AsNoTracking().SingleAsync(x => x.Id == model.Id);
        Assert.Equal((20m, "INCLUDED", (int?)24, (int?)2), (saved.VatRate, saved.VatInclusion, saved.WarrantyMonths, saved.DeliveryDays));
        var read = await EffectiveCatalog.Products(db).Where(x => x.TenantId == seeded.Tenant.Id).ToListAsync();
        Assert.All(read, x => Assert.Equal((20m, "INCLUDED"), (x.VatRate, x.VatInclusion)));
    }

    // Gap 1 (Excel): a file row giving only a VAT rate keeps the model's "KDV Dahil mi", never the card copy's.
    [PostgreSqlFact]
    public async Task StaleCopies_AProductsFileVatRateKeepsTheModelsInclusion()
    {
        var seeded = await ModelComposerGoldenPostgreSqlTests.SeedAsync(fixture, "same");
        await using var db = fixture.CreateContext();
        await db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE catalog.product_models SET "VatInclusion" = 'EXCLUDED' WHERE "TenantId" = {seeded.Tenant.Id}""");
        var card = await db.Products.AsNoTracking().SingleAsync(x => x.Id == seeded.Cards[0].Id);
        // KATALOG-14 D5 step B2b: with the copies off (Catalog:MirrorModelFields false) the seeded copy is empty; the stale copy is the point only while copies are kept.
        if (ModelFieldCopiesTestPosition.CopiesWritten) Assert.Equal("INCLUDED", card.VatInclusion);

        var applied = await new ProductTransferService(db, fixture.TimeProvider).ApplyAsync(seeded.Tenant.Id, Guid.CreateVersion7(),
            new ApplyProductTransferCommand([], [new ProductTransferProductChange(card.Id, card.Version, null, 20m)]), $"stale-vat-{Guid.NewGuid():N}", CancellationToken.None);
        Assert.True(applied.Succeeded, applied.Error?.Message);
        db.ChangeTracker.Clear();
        var model = await db.ProductModels.AsNoTracking().SingleAsync(x => x.TenantId == seeded.Tenant.Id);
        Assert.Equal(((decimal?)20m, "EXCLUDED"), (model.VatRate, model.VatInclusion));
        Assert.All(await EffectiveCatalog.Products(db).Where(x => x.TenantId == seeded.Tenant.Id).ToListAsync(), x => Assert.Equal(((decimal?)20m, "EXCLUDED"), (x.VatRate, x.VatInclusion)));
    }

    // Gap 2 (new colour card): a colour added from the editor without a warranty, delivery days or VAT in the command takes
    // the model's, not the opened card's poisoned copies.
    [PostgreSqlFact]
    public async Task StaleCopies_ANewColourCardTakesTheModelsWarrantyDeliveryDaysAndVat()
    {
        var seeded = await ModelComposerGoldenPostgreSqlTests.SeedAsync(fixture, "same");
        await using var db = fixture.CreateContext();
        await PoisonCopiesAsync(db, seeded.Tenant.Id);
        var model = await db.ProductModels.AsNoTracking().SingleAsync(x => x.TenantId == seeded.Tenant.Id);
        var card = await db.Products.AsNoTracking().SingleAsync(x => x.Id == seeded.Cards[0].Id);
        var service = new CatalogService(db, null!, new ConfigurationBuilder().Build(), fixture.TimeProvider, new MemoryCache(new MemoryCacheOptions()));
        var options = new Dictionary<string, string> { ["Renk"] = "Haki", ["Beden"] = "M" };
        var saved = await service.UpdateProductAsync(seeded.Tenant.Id, card.Id, card.Version, new UpdateProductCommand(card.Title, model.Description, model.BrandId, model.CategoryId,
            VariantsToCreate: [new CreateVariantCommand("MZ300-HM", "8690000033111", "MZ300", options, Desi: 1m, DefaultListPrice: 900m, DefaultSalePrice: 630m)]), CancellationToken.None);
        Assert.True(saved.Succeeded, saved.Error?.Message);
        db.ChangeTracker.Clear();
        var cardIds = seeded.Cards.Select(x => x.Id).ToArray();
        var haki = await db.Products.AsNoTracking().SingleAsync(x => x.TenantId == seeded.Tenant.Id && !cardIds.Contains(x.Id));
        // KATALOG-14 D5 step B2b: with the copies off (Catalog:MirrorModelFields false) the new card's copy columns are not written (NULL); every reader reads the model's.
        if (ModelFieldCopiesTestPosition.CopiesWritten) Assert.Equal(((int?)24, (int?)2, (decimal?)10m, "INCLUDED"), (haki.WarrantyMonths, haki.DeliveryDays, haki.VatRate, haki.VatInclusion));
        else Assert.Equal(((int?)null, (int?)null, (decimal?)null, (string?)null), (haki.WarrantyMonths, haki.DeliveryDays, haki.VatRate, haki.VatInclusion));
        var read = await EffectiveCatalog.Products(db).SingleAsync(x => x.Id == haki.Id);
        Assert.Equal(((int?)24, (int?)2, (decimal?)10m, "INCLUDED"), (read.WarrantyMonths, read.DeliveryDays, read.VatRate, read.VatInclusion));
        var after = await db.ProductModels.AsNoTracking().SingleAsync(x => x.Id == model.Id);
        Assert.Equal(((int?)24, (int?)2, (decimal?)10m, "INCLUDED"), (after.WarrantyMonths, after.DeliveryDays, after.VatRate, after.VatInclusion));
        Assert.Equal(model.Id, haki.FamilyId);
    }

    // Gap 3 (the Worker's warranty pass): it counts by the model. Cards whose copy is empty under a model holding 24 are not
    // counted (the readers see 24); a model left empty with cards on sale is counted whatever its cards' copies hold. The
    // first pass after a start writes exactly where the previous release wrote (review L3): empty copies of cards on sale
    // take 0, a model takes 0 only when none of its cards' copies is empty.
    [PostgreSqlFact]
    public async Task StaleCopies_TheWarrantyPassJudgesByTheModel()
    {
        var seeded = await ModelComposerGoldenPostgreSqlTests.SeedAsync(fixture, "same");
        await using var db = fixture.CreateContext();
        await MakeCopiesStaleAsync(db, seeded.Tenant.Id, 10m, "INCLUDED", null, 2);
        Assert.Equal(0, await EffectiveCatalog.Products(db).CountAsync(x => x.TenantId == seeded.Tenant.Id && x.WarrantyMonths == null));
        await ProductWarranty.RunPassAsync(db, firstPassAfterStart: true, CancellationToken.None);
        Assert.Equal((int?)24, (await db.ProductModels.AsNoTracking().SingleAsync(x => x.TenantId == seeded.Tenant.Id)).WarrantyMonths);
        // KATALOG-14 D5 step B2b: with the copies off (Catalog:MirrorModelFields false) the repair is retired and writes nothing.
        Assert.All(await db.Products.AsNoTracking().Where(x => x.TenantId == seeded.Tenant.Id).ToListAsync(), x => Assert.Equal(ModelFieldCopiesTestPosition.CopiesWritten ? 0 : (int?)null, x.WarrantyMonths));

        await db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE catalog.product_models SET "WarrantyMonths" = NULL WHERE "TenantId" = {seeded.Tenant.Id}""");
        await db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE catalog.products SET "WarrantyMonths" = 12 WHERE "TenantId" = {seeded.Tenant.Id}""");
        Assert.Equal(3, await EffectiveCatalog.Products(db).CountAsync(x => x.TenantId == seeded.Tenant.Id && x.WarrantyMonths == null));
        await ProductWarranty.RunPassAsync(db, firstPassAfterStart: true, CancellationToken.None);
        Assert.Equal(ModelFieldCopiesTestPosition.CopiesWritten ? 0 : (int?)null, (await db.ProductModels.AsNoTracking().SingleAsync(x => x.TenantId == seeded.Tenant.Id)).WarrantyMonths);
    }

    // Gap 4 (saved fields): a model the owner saved after every card's last change still counts as saved when the cards'
    // category copies are stale, so the editor shows the record, and the refresh writes nothing.
    [PostgreSqlFact]
    public async Task StaleCopies_ASavedModelStaysSaved()
    {
        var seeded = await ModelComposerGoldenPostgreSqlTests.SeedAsync(fixture, "same");
        await using var db = fixture.CreateContext();
        await PoisonCopiesAsync(db, seeded.Tenant.Id);
        await db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE catalog.product_models SET "FieldsSavedAt" = {DateTimeOffset.UtcNow.AddHours(1)} WHERE "TenantId" = {seeded.Tenant.Id}""");
        var model = await db.ProductModels.AsNoTracking().SingleAsync(x => x.TenantId == seeded.Tenant.Id);
        var cards = await db.Products.AsNoTracking().Where(x => x.TenantId == seeded.Tenant.Id).ToListAsync();
        var saved = await ProductModelLinks.SavedFieldsAsync(db, seeded.Tenant.Id, model.Id, cards, "Siyah", CancellationToken.None);
        Assert.NotNull(saved);
        Assert.Equal((model.Description, model.CategoryId, model.VatRate, model.WarrantyMonths), (saved.Description, saved.CategoryId, saved.VatRate, saved.WarrantyMonths));
        await ProductModelValues.RefreshAsync(db, seeded.Tenant.Id, [model.Id], DateTimeOffset.UtcNow, CancellationToken.None);
        var after = await db.ProductModels.AsNoTracking().SingleAsync(x => x.Id == model.Id);
        Assert.Equal((model.Version, model.Description, model.CategoryId, model.VatRate, model.Desi), (after.Version, after.Description, after.CategoryId, after.VatRate, after.Desi));
    }
}
