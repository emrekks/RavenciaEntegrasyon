using System.Text;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Imports;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class ProductTransferImportTests
{
    private static readonly string[] ExportHeaders = ["Ürün Adı", "Varyant", "Stok Kodu", "Barkod", "Model Kodu", "Marka", "Kategori", "Durum", "Satış Fiyatı", "Stok", "KDV Oranı", "Maliyet (₺)", "Desi"];

    [Fact]
    public void TheWrittenWorkbook_ReadsBackThroughTheImportReader()
    {
        var bytes = SimpleXlsxWriter.Write(
        [
            new SimpleXlsxSheet("Ürünler",
            [
                ExportHeaders,
                new object?[] { "Kırmızı Şık Gömlek", "Renk: Kırmızı | Beden: M", "ABC-01", "0086900001", null, "Marka & Co <1>", "Gömlek", "Satışa Açık", 499.9m, 3m, "%10", 12.5m, 2m },
                new object?[] { "=HYPERLINK(\"x\")", null, "ABC-02", null, null, null, null, "Taslak", null, 0m, null, null, 0.5m }
            ], [40, 20, 20, 20, 20, 20, 20, 12, 12, 10, 10, 12, 10]),
            new SimpleXlsxSheet("Açıklama", [new object?[] { "Not" }], [80])
        ]);

        var table = ImportFileReader.ReadXlsxTable(new MemoryStream(bytes), 64 * 1024 * 1024);

        Assert.Equal(ExportHeaders, table.Headers);
        Assert.Equal(2, table.Rows.Count);
        Assert.Equal(2, table.Rows[0].RowNumber);
        Assert.Equal("Kırmızı Şık Gömlek", table.Rows[0].Cells[0]);
        // A barcode keeps its leading zeros and text that looks like a formula stays text.
        Assert.Equal("0086900001", table.Rows[0].Cells[3]);
        Assert.Equal("Marka & Co <1>", table.Rows[0].Cells[5]);
        Assert.Equal("12.5", table.Rows[0].Cells[11]);
        Assert.Equal("=HYPERLINK(\"x\")", table.Rows[1].Cells[0]);
        Assert.Empty(table.Rows[1].FormulaColumns);
        Assert.Equal("0.5", table.Rows[1].Cells[12]);

        // Read back, only cost and desi count; the price, stock and VAT columns are ignored.
        var rows = ProductTransferFileParser.ParseXlsx(new MemoryStream(bytes), 64 * 1024 * 1024).Rows;
        Assert.Equal((12.5m, 2m), (rows[0].Cost.Amount, rows[0].Desi.Amount));
        Assert.Equal((ProductTransferValueKind.Empty, 0.5m), (rows[1].Cost.Kind, rows[1].Desi.Amount));
    }

    [Theory]
    [InlineData("123,45", "123.45")]
    [InlineData("123.45", "123.45")]
    [InlineData("1.234,56", "1234.56")]
    [InlineData("1.234.567,8", "1234567.8")]
    [InlineData("1234", "1234")]
    [InlineData("₺ 99,90", "99.90")]
    [InlineData("99,90 TL", "99.90")]
    [InlineData("0", "0")]
    [InlineData("0,004", "0")]
    [InlineData("12,345", "12.35")]
    [InlineData("10000000", "10000000")]
    public void ACostIsReadInTurkishOrPlainForm(string text, string expected)
    {
        var value = ProductTransferFileParser.ParseCost(text, fromCsv: true);
        Assert.Equal(ProductTransferValueKind.Set, value.Kind);
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), value.Amount);
    }

    [Theory]
    [InlineData("-")]
    [InlineData("sil")]
    [InlineData("SİL")]
    [InlineData(" Sil ")]
    public void ADashOrSilClearsTheValue(string text)
    {
        Assert.Equal(ProductTransferValueKind.Clear, ProductTransferFileParser.ParseCost(text, fromCsv: false).Kind);
        Assert.Equal(ProductTransferValueKind.Clear, ProductTransferFileParser.ParseDesi(text, fromCsv: false).Kind);
    }

    [Theory]
    [InlineData("1.234")]
    [InlineData("12.345.678")]
    [InlineData("-5")]
    [InlineData("-12,5")]
    [InlineData("10000000,01")]
    [InlineData("abc")]
    [InlineData("12,5,3")]
    [InlineData("=1+2")]
    [InlineData("1,234.56")]
    public void AnUnusableCostIsARowError(string text)
    {
        var value = ProductTransferFileParser.ParseCost(text, fromCsv: true);
        Assert.Equal(ProductTransferValueKind.Invalid, value.Kind);
        Assert.False(string.IsNullOrWhiteSpace(value.Error));
    }

    [Theory]
    [InlineData("2,5", "2.5")]
    [InlineData("1", "1")]
    [InlineData("10000", "10000")]
    public void ADesiIsReadLikeACost(string text, string expected) =>
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), ProductTransferFileParser.ParseDesi(text, fromCsv: true).Amount);

    [Theory]
    [InlineData("0")]
    [InlineData("0,001")]
    [InlineData("-1")]
    [InlineData("10000,5")]
    public void ADesiOfZeroOrOutOfRangeIsARowError(string text) =>
        Assert.Equal(ProductTransferValueKind.Invalid, ProductTransferFileParser.ParseDesi(text, fromCsv: true).Kind);

    [Fact]
    public void AnEmptyCellAsksForNothing() =>
        Assert.Equal(ProductTransferValueKind.Empty, ProductTransferFileParser.ParseCost("  ", fromCsv: true).Kind);

    [Fact]
    public void AnExcelNumberInExponentFormIsReadOnlyFromAWorkbook()
    {
        Assert.Equal(150m, ProductTransferFileParser.ParseCost("1.5E2", fromCsv: false).Amount);
        Assert.Equal(ProductTransferValueKind.Invalid, ProductTransferFileParser.ParseCost("1.5E2", fromCsv: true).Kind);
    }

    [Fact]
    public void ColumnsAreFoundByTheirHeaderInAnyOrder()
    {
        var csv = "Desi;Yeni Maliyet;barkod;STOK KODU;Not\r\n;45,5;869;abc_01;x\r\n;;;;\r\n3;;870;;\r\n";
        var rows = ProductTransferFileParser.ParseCsv(new MemoryStream(Encoding.UTF8.GetBytes(csv))).Rows;

        Assert.Equal(2, rows.Count);
        Assert.Equal(("abc_01", "869", 45.5m), (rows[0].Sku, rows[0].Barcode, rows[0].Cost.Amount));
        Assert.Equal(ProductTransferValueKind.Empty, rows[0].Desi.Kind);
        Assert.Equal(2, rows[0].RowNumber);
        Assert.Equal((ProductTransferValueKind.Empty, 3m), (rows[1].Cost.Kind, rows[1].Desi.Amount));
        Assert.Null(rows[1].Sku);
    }

    [Fact]
    public void TheOldCostTemplate_IsReadFromItsNewCostColumn()
    {
        var csv = "Stok Kodu;Güncel Maliyet;Yeni Maliyet\r\nA-1;5;7\r\n";
        var row = Assert.Single(ProductTransferFileParser.ParseCsv(new MemoryStream(Encoding.UTF8.GetBytes(csv))).Rows);
        Assert.Equal((7m, ProductTransferValueKind.Empty), (row.Cost.Amount, row.Desi.Kind));
    }

    [Fact]
    public void AFileWithoutAWritableOrACodeColumnIsRefused()
    {
        Assert.Throws<InvalidDataException>(() => ProductTransferFileParser.ParseCsv(new MemoryStream(Encoding.UTF8.GetBytes("Stok Kodu;Güncel Maliyet;Adet\r\nA;5;3\r\n"))));
        Assert.Throws<InvalidDataException>(() => ProductTransferFileParser.ParseCsv(new MemoryStream(Encoding.UTF8.GetBytes("Ürün Adı;Maliyet\r\nA;5\r\n"))));
    }

    [Fact]
    public void AFormulaInAValueOrCodeCellIsARowError()
    {
        var cells = new (int, IReadOnlyList<string>, IReadOnlyCollection<int>)[]
        {
            (2, new[] { "A-1", "12", "2" }, new[] { 2 }),
            (3, new[] { "A-2", "12", "2" }, new[] { 0 })
        };
        var rows = ProductTransferFileParser.Parse(["Stok Kodu", "Maliyet", "Desi"], cells, fromCsv: false, []).Rows;
        Assert.Equal((ProductTransferValueKind.Set, ProductTransferValueKind.Invalid), (rows[0].Cost.Kind, rows[0].Desi.Kind));
        Assert.NotNull(rows[1].CodeError);
    }

    [Fact]
    public void RowsAreMatchedByStockCodeThenBarcode_AndProblemsAreListedFirst()
    {
        var a = Candidate("ABC-01", "8690001", cost: 10m, desi: null);
        var b = Candidate("ABC-02", "8690002", cost: null, desi: 2m);
        var c = Candidate("DUP 1", null, cost: 5m, desi: null);
        var d = Candidate("dup_1", null, cost: 6m, desi: null);
        var e = Candidate("E-1", "8690005", cost: 7m, desi: 1m);
        var f = Candidate("F-1", "8690006", cost: 8m, desi: 3m);
        var g = Candidate("G-1", null, cost: 9.9999m, desi: 1.5m);
        var h = Candidate("H-1", null, cost: 1m, desi: 1m);
        var rows = new[]
        {
            Row(2, "abc_01", null, Set(12.5m), Set(2m)),                    // legacy form of the stock code → cost and desi update
            Row(3, "MISSING", "8690002", Set(0m), Set(2m)),                 // stock code unknown, barcode found → cost to 0, desi as stored
            Row(4, "DUP-1", null, Set(1m), Empty),                          // two variants share the key → ambiguous
            Row(5, "NOPE", "000", Set(1m), Empty),                          // nothing found → unmatched
            Row(6, "E-1", "8690006", Set(1m), Empty),                       // codes name different variants → error
            Row(7, "E-1", null, Set(7m), Set(1m)),                          // same as stored → unchanged
            Row(8, "F-1", null, ProductTransferValue.Clear, ProductTransferValue.Clear), // clear both
            Row(9, null, null, Set(3m), Empty),                             // no code → error
            Row(10, "X", null, Empty, Empty),                               // nothing asked → skipped, not listed
            Row(11, "G-1", null, Set(10m), Set(1.5m)),                      // a stored four-decimal cost rounds to the file's → unchanged
            Row(12, "H-1", null, Set(2m), ProductTransferValue.Invalid("Desi 0'dan büyük olmalıdır.")), // one bad value → the whole row is an error
        };

        var preview = ProductTransferImportPlanner.Plan("urunler.xlsx", rows, [a, b, c, d, e, f, g, h]);

        Assert.Equal((11, 3, 2, 1, 1, 1, 3), (preview.TotalRows, preview.Updates, preview.Unchanged, preview.Skipped, preview.Unmatched, preview.Ambiguous, preview.Errors));
        Assert.Equal((2, 1, 1, 1), (preview.CostUpdates, preview.CostClears, preview.DesiUpdates, preview.DesiClears));
        Assert.Equal(["ERROR", "ERROR", "ERROR", "AMBIGUOUS", "UNMATCHED", "UPDATE", "UPDATE", "UPDATE", "UNCHANGED", "UNCHANGED"], preview.Rows.Select(row => row.Outcome));
        Assert.Equal(
            new[]
            {
                new ProductTransferChange(a.VariantId, a.Version, true, 12.5m, true, 2m),
                new ProductTransferChange(b.VariantId, b.Version, true, 0m, false, null),
                new ProductTransferChange(f.VariantId, f.Version, true, null, true, null)
            }.OrderBy(change => change.VariantId),
            preview.Changes.OrderBy(change => change.VariantId));
        var update = preview.Rows.Single(row => row.RowNumber == 2);
        Assert.Equal((10m, 12.5m, "SET", (decimal?)null, 2m, "SET", a.ProductTitle), (update.OldCost, update.NewCost, update.CostChange, update.OldDesi, update.NewDesi, update.DesiChange, update.ProductTitle));
        Assert.Null(preview.Rows.Single(row => row.RowNumber == 3).DesiChange);
        Assert.Contains("Desi", preview.Rows.Single(row => row.RowNumber == 12).Message);
        Assert.False(preview.RowsTruncated);
    }

    [Fact]
    public void AVariantNamedTwice_IsWrittenOnlyWhenBothRowsAgree()
    {
        var a = Candidate("A-1", "1", cost: null, desi: null);
        var b = Candidate("B-1", "2", cost: null, desi: null);
        var rows = new[]
        {
            Row(2, "A-1", null, Set(5m), Set(2m)),
            Row(3, null, "1", Set(5.00m), Set(2.0m)),
            Row(4, "B-1", null, Set(5m), Set(2m)),
            Row(5, "B-1", null, Set(5m), Set(3m)),
        };

        var preview = ProductTransferImportPlanner.Plan("f.csv", rows, [a, b]);

        Assert.Equal([new ProductTransferChange(a.VariantId, a.Version, true, 5m, true, 2m)], preview.Changes);
        Assert.Equal("UPDATE", preview.Rows.Single(row => row.RowNumber == 2).Outcome);
        Assert.Equal("SKIPPED", preview.Rows.Single(row => row.RowNumber == 3).Outcome);
        Assert.All(preview.Rows.Where(row => row.RowNumber >= 4), row => Assert.Equal("ERROR", row.Outcome));
    }

    [Fact]
    public void ThePreviewListsAtMostTwoHundredRows_ButEveryChange()
    {
        var variants = Enumerable.Range(0, 250).Select(index => Candidate($"S-{index}", null, cost: null, desi: null)).ToArray();
        var rows = variants.Select((variant, index) => Row(index + 2, variant.Sku, null, Set(index), Empty)).ToArray();

        var preview = ProductTransferImportPlanner.Plan("f.xlsx", rows, variants);

        Assert.Equal(ProductTransferImportPlanner.PreviewRowLimit, preview.Rows.Count);
        Assert.True(preview.RowsTruncated);
        Assert.Equal(250, preview.Changes.Count);
        Assert.Equal(250, preview.Updates);
    }

    [Theory]
    [InlineData("12", "12")]
    [InlineData("0", "0")]
    [InlineData("1.250,00", "1250")]
    public void AStockIsAWholeNumber(string text, string expected) =>
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), ProductTransferFileParser.ParseStock(text, fromCsv: true).Amount);

    [Theory]
    [InlineData("2,5")]
    [InlineData("-")]
    [InlineData("-3")]
    public void AStockThatIsNotAWholeNumberOrIsRemovedIsARowError(string text) =>
        Assert.Equal(ProductTransferValueKind.Invalid, ProductTransferFileParser.ParseStock(text, fromCsv: true).Kind);

    [Theory]
    [InlineData("0")]
    [InlineData("sil")]
    public void APriceOfZeroOrARemovedPriceIsARowError(string text) =>
        Assert.Equal(ProductTransferValueKind.Invalid, ProductTransferFileParser.ParsePrice(text, fromCsv: true).Kind);

    [Fact]
    public void StoreColumnsAreFoundByTheStoreName_AndTheFileSaysWhichFieldsItHas()
    {
        var trendyol = new ProductTransferStoreView(Guid.CreateVersion7(), "Trendyol");
        var n11 = new ProductTransferStoreView(Guid.CreateVersion7(), "N11");
        var csv = "Stok Kodu;Stok;Trendyol Satış Fiyatı;Trendyol Liste Fiyatı;Pazarama Satış Fiyatı\r\nA-1;7;499,90;;100\r\n";

        var file = ProductTransferFileParser.ParseCsv(new MemoryStream(Encoding.UTF8.GetBytes(csv)), [trendyol, n11]);

        Assert.Equal([ProductTransferFields.Stock, ProductTransferFields.Prices], file.AvailableFields);
        var row = Assert.Single(file.Rows);
        Assert.Equal(7m, row.Stock.Amount);
        var price = Assert.Single(row.StorePrices);
        Assert.Equal((trendyol.ConnectionId, 499.90m, ProductTransferValueKind.Empty), (price.ConnectionId, price.Sale.Amount, price.List.Kind));
    }

    [Fact]
    public void OnlyThePickedFieldsArePlanned_AndStockAndStorePricesAreCheckedAgainstTheCatalog()
    {
        var trendyol = new ProductTransferStoreView(Guid.CreateVersion7(), "Trendyol");
        var n11 = new ProductTransferStoreView(Guid.CreateVersion7(), "N11");
        var offer = new ProductTransferOffer(trendyol.ConnectionId, Guid.CreateVersion7(), 5, 400m, 500m);
        var a = Candidate("A-1", null, cost: 10m, desi: null) with { OnHand = 3m, Offers = [offer] };
        var b = Candidate("B-1", null, cost: null, desi: null) with { OnHand = null };
        var c = Candidate("C-1", null, cost: null, desi: null) with { OnHand = 4m, Offers = [offer with { OfferId = Guid.CreateVersion7() }] };
        var rows = new[]
        {
            // Cost is in the file but not picked: it is not written.
            new ProductTransferFileRow(2, "A-1", null, Set(99m), Empty, Set(8m), [new ProductTransferStorePrice(trendyol.ConnectionId, Set(450m), Empty)]),
            new ProductTransferFileRow(3, "B-1", null, Empty, Empty, Set(1m)),                                                                   // no stock record → error
            new ProductTransferFileRow(4, "C-1", null, Empty, Empty, Set(4m), [new ProductTransferStorePrice(n11.ConnectionId, Set(10m), Empty)]), // no N11 offer → error
            new ProductTransferFileRow(5, "X-1", null, Set(5m), Set(2m)),                                                                       // only unpicked fields → skipped
        };

        var preview = ProductTransferImportPlanner.Plan("f.xlsx", rows, [a, b, c], [ProductTransferFields.Stock, ProductTransferFields.Prices], [trendyol, n11]);

        Assert.Equal([ProductTransferFields.Stock, ProductTransferFields.Prices], preview.Fields);
        Assert.Equal((1, 1, 1, 2, 1), (preview.Updates, preview.StockUpdates, preview.PriceUpdates, preview.Errors, preview.Skipped));
        var change = Assert.Single(preview.Changes);
        Assert.Equal((false, false), (change.SetCost, change.SetDesi));
        Assert.Equal(new ProductTransferStockChange(3m, 8m), change.Stock);
        Assert.Equal([new ProductTransferPriceChange(offer.OfferId, 5, 450m, 500m)], change.Prices);
        var update = preview.Rows.Single(row => row.RowNumber == 2);
        Assert.Equal(("SET", 3m, 8m, (string?)null), (update.StockChange, update.OldStock, update.NewStock, update.CostChange));
        Assert.Equal(("Trendyol", 400m, 450m), (update.Prices[0].StoreName, update.Prices[0].OldSalePrice, update.Prices[0].NewSalePrice));
        Assert.Contains("stok kaydı yok", preview.Rows.Single(row => row.RowNumber == 3).Message);
        Assert.Contains("N11", preview.Rows.Single(row => row.RowNumber == 4).Message);
    }

    [Fact]
    public void AStorePriceBelowItsListPriceIsKept_ButASalePriceAboveTheListPriceIsAnError()
    {
        var store = new ProductTransferStoreView(Guid.CreateVersion7(), "Trendyol");
        var offer = new ProductTransferOffer(store.ConnectionId, Guid.CreateVersion7(), 1, 400m, 500m);
        var a = Candidate("A-1", null, cost: null, desi: null) with { Offers = [offer] };
        var rows = new[] { new ProductTransferFileRow(2, "A-1", null, Empty, Empty, Empty, [new ProductTransferStorePrice(store.ConnectionId, Set(600m), Empty)]) };

        var preview = ProductTransferImportPlanner.Plan("f.xlsx", rows, [a], [ProductTransferFields.Prices], [store]);

        Assert.Empty(preview.Changes);
        Assert.Contains("liste fiyatı", Assert.Single(preview.Rows).Message);
    }

    private static ProductTransferValue Empty => ProductTransferValue.Empty;
    private static ProductTransferValue Set(decimal value) => ProductTransferValue.Set(value);

    private static ProductTransferCandidate Candidate(string sku, string? barcode, decimal? cost, decimal? desi) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), 3, sku, barcode, $"Ürün {sku}", "Beden: M", cost, desi);

    private static ProductTransferFileRow Row(int number, string? sku, string? barcode, ProductTransferValue cost, ProductTransferValue desi) => new(number, sku, barcode, cost, desi);
}

// The service against the real schema: export, preview and apply, and a preview that went stale.
[Collection("postgres-integration")]
public sealed class ProductTransferPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    [PostgreSqlFact]
    public async Task APreviewedFile_IsAppliedOnce_AndAStalePreviewWritesNothing()
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"transfer-{Guid.NewGuid():N}", DisplayName = "transfer", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var product = new Product { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Title = "Aktarım Ürünü", Description = "", Status = ProductStatus.Active, CreatedAt = now, UpdatedAt = now };
        var first = Variant(tenant, product, "COST-1", "8690000100001", cost: null, desi: 2m, now);
        var second = Variant(tenant, product, "COST-2", "8690000100002", cost: 4m, desi: null, now);
        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.Add(tenant);
            seed.Products.Add(product);
            seed.ProductVariants.AddRange(first, second);
            await seed.SaveChangesAsync();
        }

        await using var db = fixture.CreateContext();
        var service = new ProductTransferService(db, fixture.TimeProvider);
        var summary = await service.SummaryAsync(tenant.Id, CancellationToken.None);
        Assert.Equal(new ProductTransferSummaryView(2, 1, 1), summary.Value);
        var missingCost = await service.ExportAsync(tenant.Id, ProductTransferExportFilter.MissingCost, null, null, CancellationToken.None);
        Assert.Equal("COST-1", Assert.Single(ImportFileReader.ReadXlsxTable(new MemoryStream(missingCost.Value!), 64 * 1024 * 1024).Rows).Cells[2]);

        // The whole export read straight back proposes nothing.
        var export = await service.ExportAsync(tenant.Id, ProductTransferExportFilter.All, null, null, CancellationToken.None);
        var roundTrip = await service.PreviewAsync(tenant.Id, "urunler.xlsx", new MemoryStream(export.Value!), [ProductTransferFields.Cost, ProductTransferFields.Desi], CancellationToken.None);
        Assert.True(roundTrip.Succeeded, roundTrip.Error?.Message);
        Assert.Equal((0, 2), (roundTrip.Value!.Changes.Count, roundTrip.Value.Unchanged));

        var csv = "Stok Kodu;Barkod;Maliyet;Desi\r\ncost_1;;1.234,50;\r\n;8690000100002;sil;3,5\r\n";
        var preview = await service.PreviewAsync(tenant.Id, "urunler.csv", new MemoryStream(Encoding.UTF8.GetBytes(csv)), [ProductTransferFields.Cost, ProductTransferFields.Desi], CancellationToken.None);
        Assert.True(preview.Succeeded, preview.Error?.Message);
        Assert.Equal((2, 1, 1, 1), (preview.Value!.Updates, preview.Value.CostUpdates, preview.Value.CostClears, preview.Value.DesiUpdates));

        var applied = await service.ApplyAsync(tenant.Id, Guid.CreateVersion7(), new ApplyProductTransferCommand(preview.Value.Changes), "transfer-test", CancellationToken.None);
        Assert.True(applied.Succeeded, applied.Error?.Message);
        Assert.Equal(new ProductTransferApplyResult(2, 1, 1, 1, 0, 0, 0, 1), applied.Value);
        await using (var check = fixture.CreateContext())
        {
            var stored = await check.ProductVariants.AsNoTracking().Where(x => x.TenantId == tenant.Id).OrderBy(x => x.Sku).ToListAsync();
            Assert.Equal(new decimal?[] { 1234.50m, null }, stored.Select(x => x.CostPrice));
            Assert.Equal(new decimal?[] { 2m, 3.5m }, stored.Select(x => x.Desi));
            Assert.Equal(1, await check.AuditLogs.CountAsync(x => x.TenantId == tenant.Id && x.Action == "PRODUCT_TRANSFER_IMPORTED"));
            Assert.Equal(1, (await check.Products.AsNoTracking().SingleAsync(x => x.Id == product.Id)).Version);
        }

        // The same changes again: every variant already holds its values, so nothing is written.
        await using var again = fixture.CreateContext();
        var repeat = await new ProductTransferService(again, fixture.TimeProvider).ApplyAsync(tenant.Id, Guid.CreateVersion7(), new ApplyProductTransferCommand(preview.Value.Changes), "transfer-test-2", CancellationToken.None);
        Assert.Equal(new ProductTransferApplyResult(0, 0, 0, 0, 0, 0, 0, 0), repeat.Value);

        // A preview read before another change is refused whole.
        await using var stale = fixture.CreateContext();
        var current = await stale.ProductVariants.AsNoTracking().SingleAsync(x => x.Id == second.Id);
        // A change that is still current listed before a stale one: the refusal changes neither, not even in memory
        // (the idempotency middleware saves the context after a 409).
        var staleResult = await new ProductTransferService(stale, fixture.TimeProvider).ApplyAsync(tenant.Id, Guid.CreateVersion7(),
            new ApplyProductTransferCommand([new ProductTransferChange(second.Id, current.Version, true, 7m, false, null), new ProductTransferChange(first.Id, first.Version, false, null, true, 9m)]), "transfer-test-3", CancellationToken.None);
        Assert.Equal(409, staleResult.Error?.Status);
        Assert.False(stale.ChangeTracker.HasChanges());
    }

    [PostgreSqlFact]
    public async Task StockAndStorePrices_AreWrittenOnlyWhenPicked_WithALedgerEntryAndPriceHistory()
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"transfer-stock-{Guid.NewGuid():N}", DisplayName = "transfer", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var product = new Product { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Title = "Aktarım Ürünü", Description = "", Status = ProductStatus.Active, CreatedAt = now, UpdatedAt = now };
        var variant = Variant(tenant, product, "STOCK-1", "8690000200001", cost: 5m, desi: 1m, now);
        var connection = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = "N11", Environment = "STAGE", DisplayName = "N11", ExternalStoreId = $"s-{Guid.NewGuid():N}"[..12], Status = "VERIFIED", ApiVersion = "V1" };
        var item = new InventoryItem { Id = Guid.CreateVersion7(), TenantId = tenant.Id, VariantId = variant.Id, LocationCode = "MAIN", OnHand = 3m, Available = 3m };
        var offer = new ChannelOffer { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, VariantId = variant.Id, ListPrice = 500m, SalePrice = 400m, Currency = "TRY", VatRate = 10m, VatInclusion = "INCLUDED", RoundingMode = "HALF_EVEN", Status = "ACTIVE" };
        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.Add(tenant);
            seed.Products.Add(product);
            seed.ProductVariants.Add(variant);
            seed.PlatformConnections.Add(connection);
            seed.InventoryItems.Add(item);
            seed.ChannelOffers.Add(offer);
            await seed.SaveChangesAsync();
        }

        await using var db = fixture.CreateContext();
        var service = new ProductTransferService(db, fixture.TimeProvider);
        var export = await service.ExportAsync(tenant.Id, ProductTransferExportFilter.All, null, null, CancellationToken.None);
        var table = ImportFileReader.ReadXlsxTable(new MemoryStream(export.Value!), 64 * 1024 * 1024);
        Assert.Contains("N11 Satış Fiyatı", table.Headers);
        Assert.Equal("400", table.Rows[0].Cells[table.Headers.ToList().IndexOf("N11 Satış Fiyatı")]);

        var csv = "Stok Kodu;Maliyet;Stok;N11 Satış Fiyatı;N11 Liste Fiyatı\r\nSTOCK-1;9;8;450;\r\n";
        var onlyCost = await service.PreviewAsync(tenant.Id, "urunler.csv", new MemoryStream(Encoding.UTF8.GetBytes(csv)), [ProductTransferFields.Cost], CancellationToken.None);
        var change = Assert.Single(onlyCost.Value!.Changes);
        Assert.Equal((true, (ProductTransferStockChange?)null, (IReadOnlyList<ProductTransferPriceChange>?)null), (change.SetCost, change.Stock, change.Prices));

        var preview = await service.PreviewAsync(tenant.Id, "urunler.csv", new MemoryStream(Encoding.UTF8.GetBytes(csv)), [ProductTransferFields.Stock, ProductTransferFields.Prices], CancellationToken.None);
        Assert.True(preview.Succeeded, preview.Error?.Message);
        var applied = await service.ApplyAsync(tenant.Id, Guid.CreateVersion7(), new ApplyProductTransferCommand(preview.Value!.Changes), "transfer-stock", CancellationToken.None);
        Assert.True(applied.Succeeded, applied.Error?.Message);
        Assert.Equal(new ProductTransferApplyResult(1, 0, 0, 0, 0, 1, 1, 1), applied.Value);
        await using (var check = fixture.CreateContext())
        {
            var storedItem = await check.InventoryItems.AsNoTracking().SingleAsync(x => x.Id == item.Id);
            Assert.Equal((8m, 8m, 2L), (storedItem.OnHand, storedItem.Available, storedItem.ProjectionVersion));
            var ledger = await check.StockLedgerEntries.AsNoTracking().SingleAsync(x => x.TenantId == tenant.Id);
            Assert.Equal(("MANUAL_ADJUSTMENT", 5m), (ledger.MovementType, ledger.QuantityDelta));
            var storedOffer = await check.ChannelOffers.AsNoTracking().SingleAsync(x => x.Id == offer.Id);
            Assert.Equal((450m, 500m, 2L), (storedOffer.SalePrice, storedOffer.ListPrice, storedOffer.PriceVersion));
            Assert.Equal(1, await check.ChannelPriceHistory.CountAsync(x => x.OfferId == offer.Id));
            Assert.Equal(5m, (await check.ProductVariants.AsNoTracking().SingleAsync(x => x.Id == variant.Id)).CostPrice);
        }

        // An order moved the stock after the preview: the stale preview writes nothing.
        await using var moved = fixture.CreateContext();
        await moved.InventoryItems.Where(x => x.Id == item.Id).ExecuteUpdateAsync(set => set.SetProperty(x => x.OnHand, 6m).SetProperty(x => x.Available, 6m).SetProperty(x => x.Version, x => x.Version + 1));
        var stale = await new ProductTransferService(moved, fixture.TimeProvider).ApplyAsync(tenant.Id, Guid.CreateVersion7(),
            new ApplyProductTransferCommand([new ProductTransferChange(variant.Id, variant.Version, false, null, false, null, new ProductTransferStockChange(8m, 10m))]), "transfer-stock-2", CancellationToken.None);
        Assert.Equal(409, stale.Error?.Status);
    }

    private static ProductVariant Variant(Tenant tenant, Product product, string sku, string barcode, decimal? cost, decimal? desi, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenant.Id,
        ProductId = product.Id,
        Sku = sku,
        SkuNormalized = sku,
        Barcode = barcode,
        BarcodeNormalized = barcode,
        OptionSignature = "",
        Status = ProductStatus.Active,
        CostPrice = cost,
        Desi = desi,
        CreatedAt = now,
        UpdatedAt = now
    };
}
