using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Imports;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests.Contracts;

// KATALOG-14 phase D5 B2 (readers first): the VAT (rate and inclusion), category, warranty and delivery days are the
// model's. Before the writers stop keeping the colour cards' copy columns equal to the model, no reader may read those
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
}
