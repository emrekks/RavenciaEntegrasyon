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
    [InlineData("12,345", "12.35")]
    [InlineData("10000000", "10000000")]
    public void ACostIsReadInTurkishOrPlainForm(string text, string expected)
    {
        var value = ProductTransferFileParser.ParseCost(text, fromCsv: true);
        Assert.Equal(ProductTransferValueKind.Set, value.Kind);
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), value.Amount);
    }

    // K3 (owner decision 2026-11-06): a cost of 0 means "not entered", so a 0 in the file removes the stored cost like
    // "-" does and is never stored; the reports and the missing-cost template treat 0 as missing anyway.
    [Theory]
    [InlineData("0")]
    [InlineData("0,00")]
    [InlineData("0,004")]
    [InlineData("₺ 0")]
    public void AZeroCostClearsTheStoredCost(string text) =>
        Assert.Equal(ProductTransferValueKind.Clear, ProductTransferFileParser.ParseCost(text, fromCsv: true).Kind);

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
    [InlineData("9999", "9999")]
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
        var f = Candidate("F-1", "8690006", cost: 8m, desi: 3m) with { ProductDraft = true }; // only a draft may lose its desi
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

    [Fact]
    public void TheExportColumnsForTheNewFieldsAreReadBack()
    {
        var csv = "Ürün Adı;Stok Kodu;Barkod;Model Kodu;Panel Satış Fiyatı;Panel Liste Fiyatı;KDV Oranı;Satış Fiyatı\r\n" +
                  " Yeni Gömlek ;A-1;869 001;-;499,90;;%20;123\r\n" +
                  "-;A-2;=1+1;M-2;0;sil;%;\r\n";

        var file = ProductTransferFileParser.ParseCsv(new MemoryStream(Encoding.UTF8.GetBytes(csv)));

        Assert.Equal([ProductTransferFields.Title, ProductTransferFields.Barcode, ProductTransferFields.ModelCode, ProductTransferFields.PanelPrices, ProductTransferFields.Vat], file.AvailableFields);
        var first = file.Rows[0];
        Assert.Equal(("Yeni Gömlek", "869 001", ProductTransferValueKind.Clear), (first.Title.Text, first.NewBarcode.Text, first.ModelCode.Kind));
        Assert.Equal((499.90m, ProductTransferValueKind.Empty, 20m), (first.PanelSale.Amount, first.PanelList.Kind, first.Vat.Amount));
        // A title and a barcode cannot be removed, a formula-like code is refused, a panel price of 0 or "sil" and a
        // VAT without a number are errors.
        var second = file.Rows[1];
        Assert.All(new[] { second.Title, second.NewBarcode, second.PanelSale, second.PanelList, second.Vat }, value => Assert.Equal(ProductTransferValueKind.Invalid, value.Kind));
        Assert.Equal("M-2", second.ModelCode.Text);
    }

    [Fact]
    public void AStockCodeAndBarcodeFile_CanWriteTheBarcode_ButABarcodeAloneIsRefused()
    {
        var file = ProductTransferFileParser.ParseCsv(new MemoryStream(Encoding.UTF8.GetBytes("Stok Kodu;Barkod\r\nA-1;869\r\n")));
        Assert.Equal([ProductTransferFields.Barcode], file.AvailableFields);
        Assert.Throws<InvalidDataException>(() => ProductTransferFileParser.ParseCsv(new MemoryStream(Encoding.UTF8.GetBytes("Barkod\r\n869\r\n"))));
    }

    [Fact]
    public void ATitleAndAVatRateAreProductChanges_AndRowsOfOneProductMustAgree()
    {
        var productId = Guid.CreateVersion7();
        var a = Candidate("A-1", null, null, null) with { ProductId = productId, ProductTitle = "Gömlek", ProductVersion = 7, VatRate = 10m };
        var b = Candidate("A-2", null, null, null) with { ProductId = productId, ProductTitle = "Gömlek", ProductVersion = 7, VatRate = 10m };
        var other = Guid.CreateVersion7();
        var c = Candidate("C-1", null, null, null) with { ProductId = other, ProductTitle = "Pantolon", VatRate = 20m };
        var d = Candidate("C-2", null, null, null) with { ProductId = other, ProductTitle = "Pantolon", VatRate = 20m };
        var rows = new[]
        {
            new ProductTransferFileRow(2, "A-1", null, Empty, Empty, Title: Text("Yeni Gömlek"), Vat: Set(20m)),
            new ProductTransferFileRow(3, "A-2", null, Empty, Empty, Title: Text("Yeni Gömlek"), Vat: Set(20m)),
            new ProductTransferFileRow(4, "C-1", null, Empty, Empty, Title: Text("Uzun Pantolon"), Vat: Set(20m)),
            new ProductTransferFileRow(5, "C-2", null, Empty, Empty, Title: Text("Başka Pantolon"), Vat: Set(20m)),
        };

        var preview = ProductTransferImportPlanner.Plan("f.xlsx", rows, [a, b, c, d], [ProductTransferFields.Title, ProductTransferFields.Vat]);

        // No variant change: title and VAT belong to the product, written once for both of its rows.
        Assert.Empty(preview.Changes);
        Assert.Equal([new ProductTransferProductChange(productId, 7, "Yeni Gömlek", 20m)], preview.ProductChanges);
        Assert.Equal((1, 1, 2, 2), (preview.TitleUpdates, preview.VatUpdates, preview.Updates, preview.Errors));
        var row = preview.Rows.Single(item => item.RowNumber == 2);
        Assert.Equal(("Gömlek", "Yeni Gömlek", "SET", 10m, 20m, "SET"), (row.ProductTitle, row.NewTitle, row.TitleChange, row.OldVatRate, row.NewVatRate, row.VatChange));
        Assert.All(preview.Rows.Where(item => item.RowNumber >= 4), item => Assert.Contains("farklı ürün adı", item.Message));
    }

    // KATALOG-14 phase D5b review 6b: the rows of one model are compared only where the file CHANGES the field. A file
    // exported and loaded back with only the cost changed is not refused though its rows carry the sizes' differing desi;
    // one row changed alone gives its value (the other rows keep the old one, unchanged); two rows changed to two values
    // are refused.
    [Fact]
    public void AModelsRowsConflict_OnlyWhereTheFileChangesTheField()
    {
        var model = Guid.CreateVersion7();
        ProductTransferCandidate Size(string sku, decimal desi) => Candidate(sku, null, 50m, desi) with { ProductId = model, ModelCode = "MZ900", ProductTitle = "Etek", VatRate = 10m };
        var candidates = new[] { Size("E-1", 1m), Size("E-2", 2m), Size("E-3", 2m) };
        ProductTransferFileRow Line(int number, string sku, decimal cost, decimal desi) => new(number, sku, null, Set(cost), Set(desi));

        // Exported and loaded back, only the cost changed: no conflict.
        var costOnly = ProductTransferImportPlanner.Plan("f.xlsx", [Line(2, "E-1", 60m, 1m), Line(3, "E-2", 60m, 2m), Line(4, "E-3", 60m, 2m)], candidates, [ProductTransferFields.Cost, ProductTransferFields.Desi]);
        Assert.Equal(0, costOnly.Errors);
        Assert.Equal(3, costOnly.Changes.Count(change => change.SetCost));
        Assert.DoesNotContain(costOnly.Changes, change => change.SetDesi);

        // One row changed alone: its value is the change, the rows keeping the old value are unchanged.
        var oneRow = ProductTransferImportPlanner.Plan("f.xlsx", [Line(2, "E-1", 50m, 3m), Line(3, "E-2", 50m, 2m), Line(4, "E-3", 50m, 2m)], candidates, [ProductTransferFields.Cost, ProductTransferFields.Desi]);
        Assert.Equal(0, oneRow.Errors);
        var desi = Assert.Single(oneRow.Changes, change => change.SetDesi);
        Assert.Equal((candidates[0].VariantId, (decimal?)3m), (desi.VariantId, desi.Desi));

        // Two rows changed to two values: the model's rows are refused.
        var two = ProductTransferImportPlanner.Plan("f.xlsx", [Line(2, "E-1", 50m, 3m), Line(3, "E-2", 50m, 4m), Line(4, "E-3", 50m, 2m)], candidates, [ProductTransferFields.Cost, ProductTransferFields.Desi]);
        Assert.Empty(two.Changes);
        Assert.All(two.Rows, row => Assert.Contains("Aynı modelin (MZ900) satırlarında farklı desi var", row.Message));
    }

    // KATALOG-14 phase D5b (owner, 17 Nov 2026): the "Açıklama" column is the model's description. An empty cell changes
    // nothing and it cannot be removed; a formula-like value is refused; a changed one is cleaned (HTML allowlist) and
    // checked by the editor's rules. Only the rows that CHANGE it are compared: one row changed alone gives its value
    // (the rows still carrying the old text are unchanged), two rows changed to two texts refuse the model's rows.
    [Fact]
    public void TheDescription_IsTheModels_ChangedRowsAloneAreCompared_AndItIsCleanedAndChecked()
    {
        var csv = "Stok Kodu;Açıklama\r\nA-1;<p>Yeni</p>\r\nA-2;\r\nA-3;-\r\nA-4;=HYPERLINK(1)\r\n";
        var file = ProductTransferFileParser.ParseCsv(new MemoryStream(Encoding.UTF8.GetBytes(csv)));
        Assert.Equal([ProductTransferFields.Description], file.AvailableFields);
        Assert.Equal((ProductTransferValueKind.Set, "<p>Yeni</p>"), (file.Rows[0].Description.Kind, file.Rows[0].Description.Text));
        Assert.Equal(ProductTransferValueKind.Empty, file.Rows[1].Description.Kind);
        Assert.Contains("silinemez", file.Rows[2].Description.Error);
        Assert.Contains("formül", file.Rows[3].Description.Error);

        var model = Guid.CreateVersion7();
        var siyah = Guid.CreateVersion7();
        ProductTransferCandidate Size(string sku, Guid product) => Candidate(sku, null, null, null) with { ProductId = product, ModelCode = "MZ910", ProductTitle = "Etek", Description = "<p>Eski</p>" };
        var candidates = new[] { Size("S-1", siyah), Size("S-2", siyah), Size("M-1", model), Size("M-2", model) };
        ProductTransferFileRow Line(int number, string sku, string text) => new(number, sku, null, Empty, Empty, Description: Text(text));
        string[] picked = [ProductTransferFields.Description];

        // Loaded back unchanged: nothing.
        var same = ProductTransferImportPlanner.Plan("f.xlsx", [Line(2, "S-1", "<p>Eski</p>"), Line(3, "S-2", "<p>Eski</p>"), Line(4, "M-1", "<p>Eski</p>"), Line(5, "M-2", "<p>Eski</p>")], candidates, picked);
        Assert.Equal((0, 0, 4), (same.ProductChanges!.Count, same.Errors, same.Unchanged));

        // One row changed alone: its cleaned value is the change (the event handler is removed), the other rows are unchanged.
        var one = ProductTransferImportPlanner.Plan("f.xlsx", [Line(2, "S-1", "<p onclick=\"x()\">Yeni <b>metin</b></p>"), Line(3, "S-2", "<p>Eski</p>"), Line(4, "M-1", "<p>Eski</p>"), Line(5, "M-2", "<p>Eski</p>")], candidates, picked);
        Assert.Equal(0, one.Errors);
        var change = Assert.Single(one.ProductChanges!);
        Assert.Equal((siyah, "<p>Yeni <b>metin</b></p>"), (change.ProductId, change.Description));
        Assert.Equal(1, one.DescriptionUpdates);
        var view = one.Rows.Single(row => row.RowNumber == 2);
        Assert.Equal(("SET", (int?)"<p>Yeni <b>metin</b></p>".Length), (view.DescriptionChange, view.NewDescriptionLength));

        // Two rows changed to two texts: the model's rows are refused.
        var two = ProductTransferImportPlanner.Plan("f.xlsx", [Line(2, "S-1", "<p>Bir</p>"), Line(3, "S-2", "<p>Bir</p>"), Line(4, "M-1", "<p>İki</p>"), Line(5, "M-2", "<p>Eski</p>")], candidates, picked);
        Assert.Empty(two.ProductChanges!);
        Assert.All(two.Rows, row => Assert.Contains("Aynı modelin (MZ910) satırlarında farklı açıklama var", row.Message));

        // The editor's rules: an empty text after cleaning is refused.
        var empty = ProductTransferImportPlanner.Plan("f.xlsx", [Line(2, "S-1", "<script></script>")], candidates, picked);
        Assert.Equal("Açıklama boş olamaz.", Assert.Single(empty.Rows).Message);
    }

    [Fact]
    public void ABarcodeIsWrittenOnlyOnTheVariantItsStockCodeNames_AndStaysUnique()
    {
        var a = Candidate("A-1", "111", null, null);
        var b = Candidate("B-1", "222", null, null);
        var c = Candidate("C-1", "333", null, null);
        var d = Candidate("D-1", null, null, null);
        var e = Candidate("E-1", null, null, null);
        var rows = new[]
        {
            new ProductTransferFileRow(2, "A-1", "999", Empty, Empty, NewBarcode: Text("999")),  // new barcode → written
            new ProductTransferFileRow(3, "B-1", "333", Empty, Empty, NewBarcode: Text("333")),  // C-1 has it → error, not a match on C-1
            new ProductTransferFileRow(4, null, "222", Empty, Empty, NewBarcode: Text("222")),   // no stock code → error
            new ProductTransferFileRow(5, "D-1", "777", Empty, Empty, NewBarcode: Text("777")),  // two variants ask for 777 → both errors
            new ProductTransferFileRow(6, "E-1", "777", Empty, Empty, NewBarcode: Text("777")),
            new ProductTransferFileRow(7, "C-1", "333", Empty, Empty, NewBarcode: Text("333")),  // unchanged
        };

        var preview = ProductTransferImportPlanner.Plan("f.xlsx", rows, [a, b, c, d, e], [ProductTransferFields.Barcode]);

        var change = Assert.Single(preview.Changes);
        Assert.Equal((a.VariantId, "999", false, false), (change.VariantId, change.Barcode, change.SetCost, change.SetModelCode));
        Assert.Equal((1, 1, 4), (preview.BarcodeUpdates, preview.Unchanged, preview.Errors));
        Assert.Contains("C-1", preview.Rows.Single(row => row.RowNumber == 3).Message);
        Assert.Equal(b.VariantId, preview.Rows.Single(row => row.RowNumber == 3).VariantId);
        Assert.Contains("Stok Kodu", preview.Rows.Single(row => row.RowNumber == 4).Message);
        Assert.Contains("birden çok varyanta", preview.Rows.Single(row => row.RowNumber == 5).Message);

        // Not picked, the barcode cell still names the variant (as before) and nothing is written to it.
        var unpicked = ProductTransferImportPlanner.Plan("f.xlsx", [new ProductTransferFileRow(2, null, "222", Set(5m), Empty, NewBarcode: Text("222"))], [a, b]);
        Assert.Equal((b.VariantId, (string?)null), (Assert.Single(unpicked.Changes).VariantId, unpicked.Changes[0].Barcode));
    }

    [Fact]
    public void AModelCodeIsSetOrRemoved_AndAPanelPriceKeepsItsListPriceAboveTheSalePrice()
    {
        var a = Candidate("A-1", null, null, null) with { ModelCode = "M-1", PanelSalePrice = 100m, PanelListPrice = 120m };
        var b = Candidate("B-1", null, null, null) with { ModelCode = "M-1", PanelSalePrice = 100m, PanelListPrice = 120m, ProductDraft = true }; // only a draft may lose its model code
        var c = Candidate("C-1", null, null, null);
        var d = Candidate("D-1", null, null, null) with { PanelSalePrice = 100m, PanelListPrice = 120m };
        var e = Candidate("E-1", null, null, null) with { PanelSalePrice = 100m, PanelListPrice = 120m };
        var rows = new[]
        {
            new ProductTransferFileRow(2, "A-1", null, Empty, Empty, ModelCode: Text("M-2"), PanelSale: Set(110m)),
            new ProductTransferFileRow(3, "B-1", null, Empty, Empty, ModelCode: ProductTransferValue.Clear, PanelSale: Set(130m)), // 130 above the stored 120 list → error
            new ProductTransferFileRow(4, "C-1", null, Empty, Empty, PanelSale: Set(50m)),   // no list price stored → the sale price
            new ProductTransferFileRow(5, "D-1", null, Empty, Empty, PanelSale: Set(100m), PanelList: Set(120m)), // the same → unchanged
            new ProductTransferFileRow(6, "E-1", null, Empty, Empty, PanelList: Set(150m)),  // list only: the stored sale stays
        };

        var preview = ProductTransferImportPlanner.Plan("f.xlsx", rows, [a, b, c, d, e], [ProductTransferFields.ModelCode, ProductTransferFields.PanelPrices]);

        var byVariant = preview.Changes.ToDictionary(change => change.VariantId);
        Assert.Equal((true, "M-2", new ProductTransferPanelPriceChange(110m, 120m)), (byVariant[a.VariantId].SetModelCode, byVariant[a.VariantId].ModelCode, byVariant[a.VariantId].PanelPrice));
        Assert.Equal(new ProductTransferPanelPriceChange(50m, 50m), byVariant[c.VariantId].PanelPrice);
        Assert.Equal(new ProductTransferPanelPriceChange(100m, 150m), byVariant[e.VariantId].PanelPrice);
        Assert.False(byVariant.ContainsKey(b.VariantId));
        Assert.False(byVariant.ContainsKey(d.VariantId));
        Assert.Contains("küçük olamaz", preview.Rows.Single(row => row.RowNumber == 3).Message);
        Assert.Equal((3, 1, 0), (preview.PanelPriceUpdates, preview.ModelCodeUpdates, preview.ModelCodeClears));

        // Removing a model code on its own is a CLEAR.
        var clear = ProductTransferImportPlanner.Plan("f.xlsx", [new ProductTransferFileRow(2, "B-1", null, Empty, Empty, ModelCode: ProductTransferValue.Clear)], [b], [ProductTransferFields.ModelCode]);
        Assert.Equal((true, (string?)null, "CLEAR"), (clear.Changes[0].SetModelCode, clear.Changes[0].ModelCode, clear.Rows[0].ModelCodeChange));
    }

    // KATALOG-1: a model code is one value on every row of a card. A file that re-codes only some rows of a card (or names
    // two codes on one card) is refused on those rows; every row of the card re-coded together goes through.
    [Fact]
    public void AModelCode_ChangesOnEveryRowOfACardTogether()
    {
        var card = Guid.CreateVersion7();
        var s = Candidate("MZ100-S", null, null, null) with { ProductId = card, ModelCode = "MZ100" };
        var m = Candidate("MZ100-M", null, null, null) with { ProductId = card, ModelCode = "MZ100" };
        var l = Candidate("MZ100-L", null, null, null) with { ProductId = card, ModelCode = "MZ100" };
        string[] fields = [ProductTransferFields.ModelCode];

        // Only the S row takes a new code: the card would hold two codes.
        var partial = ProductTransferImportPlanner.Plan("f.xlsx", [new ProductTransferFileRow(2, "MZ100-S", null, Empty, Empty, ModelCode: Text("MZ100A"))], [s, m, l], fields);
        Assert.Empty(partial.Changes);
        Assert.Equal("ERROR", Assert.Single(partial.Rows).Outcome);
        Assert.Contains("tüm satırlarında birlikte", partial.Rows[0].Message);

        // Two codes on one card's rows.
        var split = ProductTransferImportPlanner.Plan("f.xlsx", [
            new ProductTransferFileRow(2, "MZ100-S", null, Empty, Empty, ModelCode: Text("MZ200")),
            new ProductTransferFileRow(3, "MZ100-M", null, Empty, Empty, ModelCode: Text("MZ300")),
            new ProductTransferFileRow(4, "MZ100-L", null, Empty, Empty, ModelCode: Text("MZ300"))], [s, m, l], fields);
        Assert.Empty(split.Changes);
        Assert.All(split.Rows, row => Assert.Equal("ERROR", row.Outcome));

        // Every row together (another spelling of one code is the same code).
        var together = ProductTransferImportPlanner.Plan("f.xlsx", [
            new ProductTransferFileRow(2, "MZ100-S", null, Empty, Empty, ModelCode: Text("MZ200")),
            new ProductTransferFileRow(3, "MZ100-M", null, Empty, Empty, ModelCode: Text("MZ200")),
            new ProductTransferFileRow(4, "MZ100-L", null, Empty, Empty, ModelCode: Text("mz200"))], [s, m, l], fields);
        Assert.Equal(3, together.Changes.Count);
        Assert.All(together.Rows, row => Assert.Equal("UPDATE", row.Outcome));
    }

    // KATALOG-3: the name and the VAT are the model's: rows of two colour cards of one model asking for different VATs are
    // refused; names that differ only by each card's own colour word are one name.
    [Fact]
    public void TheNameAndVat_AreCheckedAcrossTheColourCardsOfAModel()
    {
        var siyah = Candidate("MZ600-S", null, null, null) with { ModelCode = "MZ600", VariantLabel = "Renk: Siyah | Beden: S", ProductTitle = "Kadın Siyah Bluz" };
        var mavi = Candidate("MZ600-M", null, null, null) with { ModelCode = "MZ600", VariantLabel = "Renk: Mavi | Beden: S", ProductTitle = "Kadın Mavi Bluz" };
        string[] fields = [ProductTransferFields.Title, ProductTransferFields.Vat];

        var vat = ProductTransferImportPlanner.Plan("f.xlsx", [
            new ProductTransferFileRow(2, "MZ600-S", null, Empty, Empty, Vat: Set(20m)),
            new ProductTransferFileRow(3, "MZ600-M", null, Empty, Empty, Vat: Set(10m))], [siyah, mavi], fields);
        Assert.Empty(vat.ProductChanges!);
        Assert.All(vat.Rows, row => Assert.Equal("ERROR", row.Outcome));
        Assert.Contains("Aynı modelin", vat.Rows[0].Message);

        var names = ProductTransferImportPlanner.Plan("f.xlsx", [
            new ProductTransferFileRow(2, "MZ600-S", null, Empty, Empty, Title: Text("Kadın Siyah Yeni Bluz")),
            new ProductTransferFileRow(3, "MZ600-M", null, Empty, Empty, Title: Text("Kadın Mavi Yeni Bluz"))], [siyah, mavi], fields);
        Assert.Equal(2, names.ProductChanges!.Count);
        Assert.All(names.Rows, row => Assert.Equal("UPDATE", row.Outcome));
    }

    private static ProductTransferValue Empty => ProductTransferValue.Empty;
    private static ProductTransferValue Set(decimal value) => ProductTransferValue.Set(value);
    private static ProductTransferValue Text(string value) => ProductTransferValue.SetText(value);

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
        // KATALOG-14 phase D5b: the desi is the model's, so a variant without one is a variant of another model (here two
        // cards without a model code, each its own model).
        var other = new Product { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Title = "Aktarım Ürünü 2", Description = "", Status = ProductStatus.Active, CreatedAt = now, UpdatedAt = now };
        var first = Variant(tenant, product, "COST-1", "8690000100001", cost: null, desi: 2m, now);
        var second = Variant(tenant, other, "COST-2", "8690000100002", cost: 4m, desi: null, now);
        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.Add(tenant);
            seed.Products.AddRange(product, other);
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
        Assert.Equal(new ProductTransferApplyResult(2, 1, 1, 1, 0, 0, 0, 2, ModelColours: 1), applied.Value);
        await using (var check = fixture.CreateContext())
        {
            var stored = await ModelFieldCopiesTestPosition.VariantRows(check).Where(x => x.TenantId == tenant.Id).OrderBy(x => x.Sku).ToListAsync();
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
        // SIPARIS-STOK-8: an order line found too little stock earlier; the count the file sets answers its notice.
        var shortLineId = Guid.CreateVersion7();
        var shortfallMarker = new StockLedgerEntry { Id = Guid.CreateVersion7(), TenantId = tenant.Id, InventoryItemId = item.Id, MovementType = OrderInventoryStockPolicy.ShortfallMovementType, QuantityDelta = 0m, SourceType = "ORDER_LINE", SourceId = shortLineId.ToString(), SourceEventId = $"short-{shortLineId:N}", IdempotencyKey = $"short-{shortLineId:N}", OccurredAt = now, RecordedAt = now, CorrelationId = "seed" };
        var shortfallNotice = new OperationalIssue { Id = Guid.CreateVersion7(), TenantId = tenant.Id, DedupeKey = StockShortfallNotices.DedupeKey(shortLineId), Code = StockShortfallNotices.IssueCode, Summary = "stok yetersiz", Status = IssueStatus.Open, FirstSeenAt = now, LastSeenAt = now, OccurrenceCount = 1 };
        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.Add(tenant);
            seed.Products.Add(product);
            seed.ProductVariants.Add(variant);
            seed.PlatformConnections.Add(connection);
            seed.InventoryItems.Add(item);
            seed.ChannelOffers.Add(offer);
            await seed.SaveChangesAsync();
            seed.StockLedgerEntries.Add(shortfallMarker);
            seed.OperationalIssues.Add(shortfallNotice);
            await seed.SaveChangesAsync();
        }

        await using var db = fixture.CreateContext();
        var service = new ProductTransferService(db, fixture.TimeProvider);
        var export = await service.ExportAsync(tenant.Id, ProductTransferExportFilter.All, null, null, CancellationToken.None);
        var table = ImportFileReader.ReadXlsxTable(new MemoryStream(export.Value!), 64 * 1024 * 1024);
        Assert.Contains("N11 Satış Fiyatı", table.Headers);
        Assert.Equal(400m, decimal.Parse(table.Rows[0].Cells[table.Headers.ToList().IndexOf("N11 Satış Fiyatı")], System.Globalization.CultureInfo.InvariantCulture));

        var csv = "Stok Kodu;Maliyet;Stok;N11 Satış Fiyatı;N11 Liste Fiyatı\r\nSTOCK-1;9;8;450;\r\n";
        var onlyCost = await service.PreviewAsync(tenant.Id, "urunler.csv", new MemoryStream(Encoding.UTF8.GetBytes(csv)), [ProductTransferFields.Cost], CancellationToken.None);
        var change = Assert.Single(onlyCost.Value!.Changes);
        Assert.Equal((true, (ProductTransferStockChange?)null, (IReadOnlyList<ProductTransferPriceChange>?)null), (change.SetCost, change.Stock, change.Prices));

        var preview = await service.PreviewAsync(tenant.Id, "urunler.csv", new MemoryStream(Encoding.UTF8.GetBytes(csv)), [ProductTransferFields.Stock, ProductTransferFields.Prices], CancellationToken.None);
        Assert.True(preview.Succeeded, preview.Error?.Message);
        var userId = Guid.CreateVersion7();
        var applied = await service.ApplyAsync(tenant.Id, userId, new ApplyProductTransferCommand(preview.Value!.Changes), "transfer-stock", CancellationToken.None);
        Assert.True(applied.Succeeded, applied.Error?.Message);
        Assert.Equal(new ProductTransferApplyResult(1, 0, 0, 0, 0, 1, 1, 1), applied.Value);
        await using (var check = fixture.CreateContext())
        {
            var storedItem = await check.InventoryItems.AsNoTracking().SingleAsync(x => x.Id == item.Id);
            Assert.Equal((8m, 8m, 0m, 2L), (storedItem.OnHand, storedItem.Available, storedItem.Reserved, storedItem.ProjectionVersion));
            var ledger = await check.StockLedgerEntries.AsNoTracking().SingleAsync(x => x.TenantId == tenant.Id && x.MovementType != OrderInventoryStockPolicy.ShortfallMovementType);
            Assert.Equal(("MANUAL_ADJUSTMENT", 5m, "MANUAL", "Ürün dosyası içe aktarma", (Guid?)userId, "transfer-stock"), (ledger.MovementType, ledger.QuantityDelta, ledger.SourceType, ledger.SourceId, ledger.ActorUserId, ledger.CorrelationId));
            Assert.StartsWith("product-transfer:", ledger.IdempotencyKey);
            Assert.Equal(ledger.SourceEventId, ledger.IdempotencyKey);
            var notice = await check.OperationalIssues.AsNoTracking().SingleAsync(x => x.Id == shortfallNotice.Id);
            Assert.Equal(IssueStatus.Resolved, notice.Status);
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

    [PostgreSqlFact]
    public async Task ProductInformation_PanelPriceAndVat_AreWrittenToThePanelOnly()
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"transfer-info-{Guid.NewGuid():N}", DisplayName = "transfer", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        // A draft: the file removes its model code, which a card on sale or closed keeps (ProductRequiredFields).
        var product = new Product { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Title = "Eski Ad", Description = "", Status = ProductStatus.Draft, VatRate = 10m, VatInclusion = "INCLUDED", CreatedAt = now, UpdatedAt = now };
        var first = Variant(tenant, product, "INFO-1", "8690000300001", cost: null, desi: null, now);
        first.ModelCode = "MOD-1";
        first.DefaultSalePrice = 100m;
        first.DefaultListPrice = 120m;
        var second = Variant(tenant, product, "INFO-2", "8690000300002", cost: null, desi: null, now);
        var item = new InventoryItem { Id = Guid.CreateVersion7(), TenantId = tenant.Id, VariantId = first.Id, LocationCode = "MAIN", OnHand = 3m, Available = 3m };
        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.Add(tenant);
            seed.Products.Add(product);
            seed.ProductVariants.AddRange(first, second);
            seed.InventoryItems.Add(item);
            await seed.SaveChangesAsync();
        }

        await using var db = fixture.CreateContext();
        var service = new ProductTransferService(db, fixture.TimeProvider);
        string[] fields = [ProductTransferFields.Title, ProductTransferFields.Barcode, ProductTransferFields.ModelCode, ProductTransferFields.PanelPrices, ProductTransferFields.Vat];

        // The export read straight back with every new field picked proposes nothing.
        var export = await service.ExportAsync(tenant.Id, ProductTransferExportFilter.All, null, null, CancellationToken.None);
        var table = ImportFileReader.ReadXlsxTable(new MemoryStream(export.Value!), 64 * 1024 * 1024);
        Assert.Contains(ProductTransferFileParser.PanelSaleHeader, table.Headers);
        Assert.DoesNotContain("Satış Fiyatı", table.Headers);
        var roundTrip = await service.PreviewAsync(tenant.Id, "urunler.xlsx", new MemoryStream(export.Value!), [.. fields, ProductTransferFields.Stock], CancellationToken.None);
        Assert.True(roundTrip.Succeeded, roundTrip.Error?.Message);
        Assert.Equal((0, 0, 2), (roundTrip.Value!.Changes.Count, roundTrip.Value.ProductChanges!.Count, roundTrip.Value.Unchanged));

        var csv = "Stok Kodu;Ürün Adı;Barkod;Model Kodu;Panel Satış Fiyatı;Panel Liste Fiyatı;KDV Oranı\r\n" +
                  "INFO-1;Yeni Ad;8690000399999;-;110;;%20\r\n" +
                  "INFO-2;Yeni Ad;8690000300002;MOD-2;;;%20\r\n";
        var preview = await service.PreviewAsync(tenant.Id, "urunler.csv", new MemoryStream(Encoding.UTF8.GetBytes(csv)), fields, CancellationToken.None);
        Assert.True(preview.Succeeded, preview.Error?.Message);
        Assert.Equal((1, 1, 1, 1, 1, 1), (preview.Value!.TitleUpdates, preview.Value.BarcodeUpdates, preview.Value.ModelCodeUpdates, preview.Value.ModelCodeClears, preview.Value.PanelPriceUpdates, preview.Value.VatUpdates));

        var applied = await service.ApplyAsync(tenant.Id, Guid.CreateVersion7(), new ApplyProductTransferCommand(preview.Value.Changes, preview.Value.ProductChanges), "transfer-info", CancellationToken.None);
        Assert.True(applied.Succeeded, applied.Error?.Message);
        Assert.Equal(new ProductTransferApplyResult(2, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, ModelColours: 1), applied.Value);
        await using (var check = fixture.CreateContext())
        {
            var storedProduct = await ModelFieldCopiesTestPosition.Cards(check).SingleAsync(x => x.Id == product.Id);
            Assert.Equal(("Yeni Ad", 20m, "INCLUDED", 2L), (storedProduct.Title, storedProduct.VatRate, storedProduct.VatInclusion, storedProduct.Version));
            var stored = await check.ProductVariants.AsNoTracking().Where(x => x.TenantId == tenant.Id).OrderBy(x => x.Sku).ToListAsync();
            Assert.Equal(("8690000399999", "8690000399999", (string?)null, 110m, 120m), (stored[0].Barcode, stored[0].BarcodeNormalized, stored[0].ModelCode, stored[0].DefaultSalePrice, stored[0].DefaultListPrice));
            Assert.Equal(("8690000300002", "MOD-2"), (stored[1].Barcode, stored[1].ModelCode));
            // A panel write: no stock moved and nothing queued for a marketplace.
            Assert.Equal(3m, (await check.InventoryItems.AsNoTracking().SingleAsync(x => x.Id == item.Id)).OnHand);
            Assert.Equal(0, await check.StockLedgerEntries.CountAsync(x => x.TenantId == tenant.Id));
        }

        // Another variant's barcode cannot be taken.
        await using var taken = fixture.CreateContext();
        var takenService = new ProductTransferService(taken, fixture.TimeProvider);
        var steal = await takenService.PreviewAsync(tenant.Id, "urunler.csv", new MemoryStream(Encoding.UTF8.GetBytes("Stok Kodu;Barkod\r\nINFO-2;8690000399999\r\n")), [ProductTransferFields.Barcode], CancellationToken.None);
        Assert.True(steal.Succeeded, steal.Error?.Message);
        Assert.Equal((0, 1), (steal.Value!.Changes.Count, steal.Value.Errors));
        var current = await taken.ProductVariants.AsNoTracking().SingleAsync(x => x.Id == second.Id);
        var refused = await takenService.ApplyAsync(tenant.Id, Guid.CreateVersion7(), new ApplyProductTransferCommand([new ProductTransferChange(second.Id, current.Version, false, null, false, null, Barcode: "8690000399999")]), "transfer-info-2", CancellationToken.None);
        Assert.Equal(("PRODUCT_TRANSFER_BARCODE_TAKEN", 409), (refused.Error?.Code, refused.Error?.Status));
    }

    // Scan C-10 (13 Nov 2026): an Excel cell formatted as a percentage holds the fraction (%10 is 0.1); it is read as
    // 10, and only a rate the panel takes (0, 1, 10, 20) is written.
    [Theory]
    [InlineData("%10", false, 10)]
    [InlineData("10", false, 10)]
    [InlineData("0.1", false, 10)]
    [InlineData("0.2", false, 20)]
    [InlineData("0.01", false, 1)]
    [InlineData("0", false, 0)]
    [InlineData("1", false, 1)]
    [InlineData("0,1", true, 10)]
    [InlineData("%8", false, 8)]
    [InlineData("0.08", false, 8)]
    public void AVatRate_IsReadAsThePercentageExcelShows(string cell, bool fromCsv, int expected)
    {
        var value = ProductTransferFileParser.ParseVat(cell, fromCsv);
        Assert.Equal((ProductTransferValueKind.Set, (decimal?)expected), (value.Kind, value.Amount));
    }

    [Theory]
    [InlineData("0.5")]
    [InlineData("%0,1")]
    [InlineData("18")]
    [InlineData("5")]
    public void AVatRateThePanelDoesNotTake_IsAnError(string cell)
    {
        Assert.Equal(ProductTransferValueKind.Invalid, ProductTransferFileParser.ParseVat(cell, fromCsv: true).Kind);
    }

    // Scan C-9 (13 Nov 2026): a file changing a variant's model code to the code another product holds would merge the
    // two products into one family (Shopify and PTT AVM send a family as one product): the upload is refused.
    [PostgreSqlFact]
    public async Task AModelCodeAnotherProductHolds_IsRefused()
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"transfer-model-{Guid.NewGuid():N}", DisplayName = "transfer", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var shirt = new Product { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Title = "Gömlek", Description = "", Status = ProductStatus.Active, CreatedAt = now, UpdatedAt = now };
        var trousers = new Product { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Title = "Pantolon", Description = "", Status = ProductStatus.Active, CreatedAt = now, UpdatedAt = now };
        var shirtVariant = Variant(tenant, shirt, "GML-1", "8690000500001", cost: null, desi: null, now);
        shirtVariant.ModelCode = "GML-MODEL";
        var trousersVariant = Variant(tenant, trousers, "PNT-1", "8690000500002", cost: null, desi: null, now);
        trousersVariant.ModelCode = "PNT-MODEL";
        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.Add(tenant);
            seed.Products.AddRange(shirt, trousers);
            seed.ProductVariants.AddRange(shirtVariant, trousersVariant);
            await seed.SaveChangesAsync();
        }

        await using var db = fixture.CreateContext();
        var service = new ProductTransferService(db, fixture.TimeProvider);
        var refused = await service.ApplyAsync(tenant.Id, Guid.CreateVersion7(), new ApplyProductTransferCommand([new ProductTransferChange(trousersVariant.Id, trousersVariant.Version, false, null, false, null, SetModelCode: true, ModelCode: "gml model")]), "transfer-model", CancellationToken.None);
        Assert.Equal(("PRODUCT_TRANSFER_MODEL_CODE_TAKEN", 409), (refused.Error?.Code, refused.Error?.Status));
        Assert.Contains("Gömlek", refused.Error!.Message);

        // A code no other product holds is written.
        db.ChangeTracker.Clear();
        var renamed = await service.ApplyAsync(tenant.Id, Guid.CreateVersion7(), new ApplyProductTransferCommand([new ProductTransferChange(trousersVariant.Id, trousersVariant.Version, false, null, false, null, SetModelCode: true, ModelCode: "PNT-YENI")]), "transfer-model-2", CancellationToken.None);
        Assert.True(renamed.Succeeded, renamed.Error?.Message);
    }

    // Scan C-9, imports: a marketplace import may not move a variant's model code onto a card the marketplace does not
    // know (no link on the connection); a card linked on the connection is the same marketplace model's family.
    [PostgreSqlFact]
    public async Task AnImportedModelCode_IsHeldOnlyByCardsTheConnectionDoesNotKnow()
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"import-model-{Guid.NewGuid():N}", DisplayName = "import", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var connection = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = "TRENDYOL", Environment = "PRODUCTION", DisplayName = "Trendyol", ExternalStoreId = $"store-{Guid.NewGuid():N}", Status = "ACTIVE", ApiVersion = "V2", SettingsJson = "{}", Version = 1 };
        var imported = new Product { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Title = "İçe aktarılan", Description = "", Status = ProductStatus.Active, CreatedAt = now, UpdatedAt = now };
        var panelOnly = new Product { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Title = "Panel Gömleği", Description = "", Status = ProductStatus.Active, CreatedAt = now, UpdatedAt = now };
        var importedVariant = Variant(tenant, imported, "IMP-1", "8690000600001", cost: null, desi: null, now);
        importedVariant.ModelCode = "ESKI-MODEL";
        var panelVariant = Variant(tenant, panelOnly, "PNL-1", "8690000600002", cost: null, desi: null, now);
        panelVariant.ModelCode = "ORTAK-MODEL";
        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.Add(tenant);
            seed.PlatformConnections.Add(connection);
            seed.Products.AddRange(imported, panelOnly);
            seed.ProductVariants.AddRange(importedVariant, panelVariant);
            seed.MarketplaceProductLinks.Add(new MarketplaceProductLink { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, ProductId = imported.Id, ExternalId = $"content-{Guid.NewGuid():N}", SyncStatus = "SYNCED", Version = 1 });
            await seed.SaveChangesAsync();
        }

        await using (var db = fixture.CreateContext())
        {
            Assert.Equal(panelOnly.Id, (await ModelCodeOwnership.FindUnlinkedHolderAsync(db, tenant.Id, connection.Id, null, "ortak model", imported.Id, CancellationToken.None))?.ProductId);
            Assert.Null(await ModelCodeOwnership.FindUnlinkedHolderAsync(db, tenant.Id, connection.Id, null, "BAŞKA-MODEL", imported.Id, CancellationToken.None));
        }

        // Once the panel card is linked on the connection it is the same marketplace model: no conflict.
        await using (var link = fixture.CreateContext())
        {
            link.MarketplaceProductLinks.Add(new MarketplaceProductLink { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, ProductId = panelOnly.Id, ExternalId = $"content-{Guid.NewGuid():N}", SyncStatus = "SYNCED", Version = 1 });
            await link.SaveChangesAsync();
        }
        await using (var db = fixture.CreateContext())
            Assert.Null(await ModelCodeOwnership.FindUnlinkedHolderAsync(db, tenant.Id, connection.Id, null, "ORTAK-MODEL", imported.Id, CancellationToken.None));
    }

    // KATALOG-14 phase D5b (owner, 17 Nov 2026): the export carries the model's description on every row; one longer than
    // an Excel cell holds is left empty and named in the notes, and its empty cell changes nothing on the way back. A
    // description changed on one row goes to the model and every colour, and the result says how many colours.
    [PostgreSqlFact]
    public async Task TheDescription_IsExported_AndAChangeOnOneRowGoesToTheModelAndEveryColour()
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"transfer-desc-{Guid.NewGuid():N}", DisplayName = "transfer", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        Product Card(string colour, int minute, string description) => new() { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Title = $"Kadın {colour} Etek", Description = description, Status = ProductStatus.Draft, VatRate = 10m, VatInclusion = "INCLUDED", CreatedAt = now.AddMinutes(minute), UpdatedAt = now };
        var cards = new[] { Card("Siyah", 0, "<p>Eski açıklama</p>"), Card("Mavi", 1, "<p>Eski açıklama</p>"), Card("Gri", 2, "<p>Eski açıklama</p>") };
        var longCard = Card("Uzun", 3, new string('x', ProductTransferFileParser.MaximumDescriptionCell + 10));
        var variants = cards.Select((card, index) => Variant(tenant, card, $"DESC-{index}", $"869000040000{index}", cost: null, desi: 1m, now)).ToList();
        foreach (var variant in variants) variant.ModelCode = "DESC1";
        var longVariant = Variant(tenant, longCard, "LONG-1", "8690000500001", cost: null, desi: 1m, now);
        longVariant.ModelCode = "LONG1";
        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.Add(tenant);
            seed.Products.AddRange([.. cards, longCard]);
            seed.ProductVariants.AddRange([.. variants, longVariant]);
            await seed.SaveChangesAsync();
        }

        await using var db = fixture.CreateContext();
        var service = new ProductTransferService(db, fixture.TimeProvider);
        var export = await service.ExportAsync(tenant.Id, ProductTransferExportFilter.All, null, null, CancellationToken.None);
        var table = ImportFileReader.ReadXlsxTable(new MemoryStream(export.Value!), 64 * 1024 * 1024);
        var column = table.Headers.ToList().IndexOf("Açıklama");
        Assert.True(column >= 0);
        Assert.Equal(3, table.Rows.Count(row => row.Cells.Count > column && row.Cells[column] == "<p>Eski açıklama</p>"));
        var longRow = table.Rows.Single(row => row.Cells.Contains("LONG-1"));
        Assert.True(longRow.Cells.Count <= column || string.IsNullOrEmpty(longRow.Cells[column]));
        using (var zip = new System.IO.Compression.ZipArchive(new MemoryStream(export.Value!)))
        {
            var sheets = string.Concat(zip.Entries.Where(entry => entry.FullName.StartsWith("xl/", StringComparison.Ordinal) && entry.FullName.EndsWith(".xml", StringComparison.Ordinal))
                .Select(entry => new StreamReader(entry.Open()).ReadToEnd()));
            Assert.Contains("Bu Modelin Açıklaması Excel İçin Çok Uzun, Editörden Düzenleyin: LONG1", sheets);
            // The notes state the model rule: a change on one row reaches every colour of the model; no "ürünün kendisine aittir".
            Assert.Contains("bir satırdaki değişiklik modelin bütün renklerine ve bedenlerine uygulanır", sheets);
            Assert.DoesNotContain("ürünün kendisine aittir", sheets);
        }
        // Loaded straight back with the description picked: nothing changes, the long one is not deleted.
        var roundTrip = await service.PreviewAsync(tenant.Id, "urunler.xlsx", new MemoryStream(export.Value!), [ProductTransferFields.Description], CancellationToken.None);
        Assert.True(roundTrip.Succeeded, roundTrip.Error?.Message);
        Assert.Equal((0, 0), (roundTrip.Value!.ProductChanges!.Count, roundTrip.Value.Errors));

        // One row of the model changed (the others keep the old text): the model and all three colours take it.
        var csv = "Stok Kodu;Açıklama\r\nDESC-0;<p onclick='x()'>Yeni açıklama</p>\r\nDESC-1;<p>Eski açıklama</p>\r\nDESC-2;<p>Eski açıklama</p>\r\n";
        var preview = await service.PreviewAsync(tenant.Id, "urunler.csv", new MemoryStream(Encoding.UTF8.GetBytes(csv)), [ProductTransferFields.Description], CancellationToken.None);
        Assert.True(preview.Succeeded, preview.Error?.Message);
        Assert.Equal((1, 0), (preview.Value!.DescriptionUpdates, preview.Value.Errors));
        var applied = await service.ApplyAsync(tenant.Id, Guid.CreateVersion7(), new ApplyProductTransferCommand(preview.Value.Changes, preview.Value.ProductChanges), "transfer-desc", CancellationToken.None);
        Assert.True(applied.Succeeded, applied.Error?.Message);
        Assert.Equal((1, 3), (applied.Value!.DescriptionsUpdated, applied.Value.ModelColours));
        await using var check = fixture.CreateContext();
        var ids = cards.Select(card => card.Id).ToArray();
        Assert.All(await ModelFieldCopiesTestPosition.Cards(check).Where(x => ids.Contains(x.Id)).ToListAsync(), x => Assert.Equal("<p>Yeni açıklama</p>", x.Description));
        var model = await check.ProductModels.AsNoTracking().SingleAsync(x => x.TenantId == tenant.Id && x.ModelCodeKey == "DESC1");
        Assert.Equal("<p>Yeni açıklama</p>", model.Description);
        Assert.Equal(longCard.Description, (await check.Products.AsNoTracking().SingleAsync(x => x.Id == longCard.Id)).Description);

        // Two rows of the model asking for two new texts: refused, nothing written.
        var mixed = "Stok Kodu;Açıklama\r\nDESC-0;<p>Bir</p>\r\nDESC-1;<p>İki</p>\r\n";
        var refused = await service.PreviewAsync(tenant.Id, "urunler.csv", new MemoryStream(Encoding.UTF8.GetBytes(mixed)), [ProductTransferFields.Description], CancellationToken.None);
        Assert.Equal((0, 2), (refused.Value!.ProductChanges!.Count, refused.Value.Errors));
    }

    // Review (17 Nov 2026): a name or description the panel holds may start like a formula ("- %100 pamuk", "+90 …", "@…",
    // "=…") or carry a lone carriage return; the export writes it back as it is, and the file loaded back untouched
    // changes nothing (its rows, cost and stock included, are not refused). Only a changed value starting so is refused.
    [PostgreSqlFact]
    public async Task ANameOrDescriptionStartingLikeAFormula_LoadedBackUntouched_ChangesNothing()
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"transfer-formula-{Guid.NewGuid():N}", DisplayName = "transfer", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        string[] descriptions = ["- %100 pamuk, yıkanabilir", "+90 532 bilgi hattı", "@ravencia koleksiyonu", "=Yeni sezon", "Birinci satır\rİkinci satır"];
        string[] titles = ["- Kısa Etek", "+ Uzun Etek", "Etek C", "Etek D", "Etek E"];
        var products = descriptions.Select((description, index) => new Product { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Title = titles[index], Description = description, Status = ProductStatus.Draft, VatRate = 10m, VatInclusion = "INCLUDED", CreatedAt = now.AddMinutes(index), UpdatedAt = now }).ToList();
        var variants = products.Select((product, index) => Variant(tenant, product, $"FORM-{index}", $"869000060000{index}", cost: 5m, desi: 1m, now)).ToList();
        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.Add(tenant);
            seed.Products.AddRange(products);
            seed.ProductVariants.AddRange(variants);
            await seed.SaveChangesAsync();
        }

        await using var db = fixture.CreateContext();
        var service = new ProductTransferService(db, fixture.TimeProvider);
        var export = await service.ExportAsync(tenant.Id, ProductTransferExportFilter.All, null, null, CancellationToken.None);
        var roundTrip = await service.PreviewAsync(tenant.Id, "urunler.xlsx", new MemoryStream(export.Value!), [ProductTransferFields.Title, ProductTransferFields.Description, ProductTransferFields.Cost, ProductTransferFields.Desi], CancellationToken.None);
        Assert.True(roundTrip.Succeeded, roundTrip.Error?.Message);
        Assert.Equal((0, 0, 0, 5), (roundTrip.Value!.Errors, roundTrip.Value.Changes.Count, roundTrip.Value.ProductChanges!.Count, roundTrip.Value.Unchanged));

        // A changed value starting like a formula is refused; the other rows still go through.
        var csv = "Stok Kodu;Ürün Adı;Açıklama;Maliyet\r\nFORM-0;- Kısa Etek;=HYPERLINK(1);7\r\nFORM-1;+ Uzun Siyah Etek;+90 532 bilgi hattı;7\r\nFORM-2;Etek C;@ravencia koleksiyonu;7\r\n";
        var changed = await service.PreviewAsync(tenant.Id, "urunler.csv", new MemoryStream(Encoding.UTF8.GetBytes(csv)), [ProductTransferFields.Title, ProductTransferFields.Description, ProductTransferFields.Cost], CancellationToken.None);
        Assert.True(changed.Succeeded, changed.Error?.Message);
        Assert.Contains("Açıklama: formül benzeri", changed.Value!.Rows.Single(row => row.RowNumber == 2).Message);
        Assert.Contains("Ürün adı: formül benzeri", changed.Value.Rows.Single(row => row.RowNumber == 3).Message);
        Assert.Equal(("UPDATE", "SET"), (changed.Value.Rows.Single(row => row.RowNumber == 4).Outcome, changed.Value.Rows.Single(row => row.RowNumber == 4).CostChange));
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
