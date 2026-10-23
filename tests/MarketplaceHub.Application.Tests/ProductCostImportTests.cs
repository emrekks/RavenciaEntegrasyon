using System.Text;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Imports;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class ProductCostImportTests
{
    private static readonly string[] TemplateHeaders = ["Ürün Adı", "Varyant", "Stok Kodu", "Barkod", "Model Kodu", "Marka", "Kategori", "Güncel Maliyet", "Yeni Maliyet"];

    [Fact]
    public void TheWrittenWorkbook_ReadsBackThroughTheImportReader()
    {
        var bytes = SimpleXlsxWriter.Write(
        [
            new SimpleXlsxSheet("Maliyetler",
            [
                TemplateHeaders,
                new object?[] { "Kırmızı Şık Gömlek", "Renk: Kırmızı | Beden: M", "ABC-01", "0086900001", null, "Marka & Co <1>", "Gömlek", 12.5m, null },
                new object?[] { "=HYPERLINK(\"x\")", null, "ABC-02", null, null, null, null, null, 0m }
            ], [40, 20, 20, 20, 20, 20, 20, 12, 12]),
            new SimpleXlsxSheet("Açıklama", [new object?[] { "Not" }], [80])
        ]);

        var table = ImportFileReader.ReadXlsxTable(new MemoryStream(bytes), 64 * 1024 * 1024);

        Assert.Equal(TemplateHeaders, table.Headers);
        Assert.Equal(2, table.Rows.Count);
        Assert.Equal(2, table.Rows[0].RowNumber);
        Assert.Equal("Kırmızı Şık Gömlek", table.Rows[0].Cells[0]);
        // A barcode keeps its leading zeros and text that looks like a formula stays text.
        Assert.Equal("0086900001", table.Rows[0].Cells[3]);
        Assert.Equal("Marka & Co <1>", table.Rows[0].Cells[5]);
        Assert.Equal("12.5", table.Rows[0].Cells[7]);
        Assert.Equal("=HYPERLINK(\"x\")", table.Rows[1].Cells[0]);
        Assert.Empty(table.Rows[1].FormulaColumns);
        Assert.Equal("0", table.Rows[1].Cells[8]);
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
        var value = ProductCostFileParser.ParseCost(text, fromCsv: true);
        Assert.Equal(ProductCostValueKind.Set, value.Kind);
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), value.Amount);
    }

    [Theory]
    [InlineData("-")]
    [InlineData("sil")]
    [InlineData("SİL")]
    [InlineData(" Sil ")]
    public void ADashOrSilClearsTheCost(string text) =>
        Assert.Equal(ProductCostValueKind.Clear, ProductCostFileParser.ParseCost(text, fromCsv: false).Kind);

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
        var value = ProductCostFileParser.ParseCost(text, fromCsv: true);
        Assert.Equal(ProductCostValueKind.Invalid, value.Kind);
        Assert.False(string.IsNullOrWhiteSpace(value.Error));
    }

    [Fact]
    public void AnEmptyCellAsksForNothing() =>
        Assert.Equal(ProductCostValueKind.Empty, ProductCostFileParser.ParseCost("  ", fromCsv: true).Kind);

    [Fact]
    public void AnExcelNumberInExponentFormIsReadOnlyFromAWorkbook()
    {
        Assert.Equal(150m, ProductCostFileParser.ParseCost("1.5E2", fromCsv: false).Amount);
        Assert.Equal(ProductCostValueKind.Invalid, ProductCostFileParser.ParseCost("1.5E2", fromCsv: true).Kind);
    }

    [Fact]
    public void ColumnsAreFoundByTheirHeaderInAnyOrder()
    {
        var csv = "Yeni Maliyet;barkod;STOK KODU;Not\r\n45,5;869;abc_01;x\r\n;;;\r\n;870;;\r\n";
        var rows = ProductCostFileParser.ParseCsv(new MemoryStream(Encoding.UTF8.GetBytes(csv)));

        Assert.Equal(2, rows.Count);
        Assert.Equal(("abc_01", "869", 45.5m), (rows[0].Sku, rows[0].Barcode, rows[0].Value.Amount));
        Assert.Equal(2, rows[0].RowNumber);
        Assert.Equal(ProductCostValueKind.Empty, rows[1].Value.Kind);
        Assert.Null(rows[1].Sku);
    }

    [Fact]
    public void AFileWithoutTheNewCostOrACodeColumnIsRefused()
    {
        Assert.Throws<InvalidDataException>(() => ProductCostFileParser.ParseCsv(new MemoryStream(Encoding.UTF8.GetBytes("Stok Kodu;Güncel Maliyet\r\nA;5\r\n"))));
        Assert.Throws<InvalidDataException>(() => ProductCostFileParser.ParseCsv(new MemoryStream(Encoding.UTF8.GetBytes("Ürün Adı;Yeni Maliyet\r\nA;5\r\n"))));
    }

    [Fact]
    public void AFormulaInTheCostCellIsARowError()
    {
        var cells = new (int, IReadOnlyList<string>, IReadOnlyCollection<int>)[] { (2, new[] { "A-1", "12" }, new[] { 1 }) };
        var rows = ProductCostFileParser.Parse(["Stok Kodu", "Yeni Maliyet"], cells, fromCsv: false);
        Assert.Equal(ProductCostValueKind.Invalid, Assert.Single(rows).Value.Kind);
    }

    [Fact]
    public void RowsAreMatchedByStockCodeThenBarcode_AndProblemsAreListedFirst()
    {
        var a = Candidate("ABC-01", "8690001", cost: 10m);
        var b = Candidate("ABC-02", "8690002", cost: null);
        var c = Candidate("DUP 1", null, cost: 5m);
        var d = Candidate("dup_1", null, cost: 6m);
        var e = Candidate("E-1", "8690005", cost: 7m);
        var f = Candidate("F-1", "8690006", cost: 8m);
        var rows = new[]
        {
            Row(2, "abc_01", null, ProductCostValue.Set(12.5m)),          // legacy form of the stock code → update
            Row(3, "MISSING", "8690002", ProductCostValue.Set(0m)),        // stock code unknown, barcode found → update to 0
            Row(4, "DUP-1", null, ProductCostValue.Set(1m)),               // two variants share the key → ambiguous
            Row(5, "NOPE", "000", ProductCostValue.Set(1m)),               // nothing found → unmatched
            Row(6, "E-1", "8690006", ProductCostValue.Set(1m)),            // codes name different variants → error
            Row(7, "E-1", null, ProductCostValue.Set(7m)),                 // same as stored → unchanged
            Row(8, "F-1", null, ProductCostValue.Clear),                   // clear
            Row(9, null, null, ProductCostValue.Set(3m)),                  // no code → error
            Row(10, "X", null, ProductCostValue.Empty),                    // empty value → skipped, not listed
        };

        var preview = ProductCostImportPlanner.Plan("maliyet.xlsx", rows, [a, b, c, d, e, f]);

        Assert.Equal((9, 2, 1, 1, 1, 1, 1, 2), (preview.TotalRows, preview.Updates, preview.Clears, preview.Unchanged, preview.Skipped, preview.Unmatched, preview.Ambiguous, preview.Errors));
        Assert.Equal(["ERROR", "ERROR", "AMBIGUOUS", "UNMATCHED", "UPDATE", "UPDATE", "CLEAR", "UNCHANGED"], preview.Rows.Select(row => row.Outcome));
        Assert.Equal(
            new[] { new ProductCostChange(a.VariantId, a.Version, 12.5m), new ProductCostChange(b.VariantId, b.Version, 0m), new ProductCostChange(f.VariantId, f.Version, null) }.OrderBy(change => change.VariantId),
            preview.Changes.OrderBy(change => change.VariantId));
        var update = preview.Rows.Single(row => row.RowNumber == 2);
        Assert.Equal((10m, 12.5m, a.ProductTitle), (update.OldCost, update.NewCost, update.ProductTitle));
        Assert.False(preview.RowsTruncated);
    }

    [Fact]
    public void AVariantNamedTwice_IsWrittenOnlyWhenBothRowsAgree()
    {
        var a = Candidate("A-1", "1", cost: null);
        var b = Candidate("B-1", "2", cost: null);
        var rows = new[]
        {
            Row(2, "A-1", null, ProductCostValue.Set(5m)),
            Row(3, null, "1", ProductCostValue.Set(5.00m)),
            Row(4, "B-1", null, ProductCostValue.Set(5m)),
            Row(5, "B-1", null, ProductCostValue.Set(6m)),
        };

        var preview = ProductCostImportPlanner.Plan("f.csv", rows, [a, b]);

        Assert.Equal([new ProductCostChange(a.VariantId, a.Version, 5m)], preview.Changes);
        Assert.Equal("UPDATE", preview.Rows.Single(row => row.RowNumber == 2).Outcome);
        Assert.Equal("SKIPPED", preview.Rows.Single(row => row.RowNumber == 3).Outcome);
        Assert.All(preview.Rows.Where(row => row.RowNumber >= 4), row => Assert.Equal("ERROR", row.Outcome));
    }

    [Fact]
    public void ThePreviewListsAtMostTwoHundredRows_ButEveryChange()
    {
        var variants = Enumerable.Range(0, 250).Select(index => Candidate($"S-{index}", null, cost: null)).ToArray();
        var rows = variants.Select((variant, index) => Row(index + 2, variant.Sku, null, ProductCostValue.Set(index))).ToArray();

        var preview = ProductCostImportPlanner.Plan("f.xlsx", rows, variants);

        Assert.Equal(ProductCostImportPlanner.PreviewRowLimit, preview.Rows.Count);
        Assert.True(preview.RowsTruncated);
        Assert.Equal(250, preview.Changes.Count);
        Assert.Equal(250, preview.Updates);
    }

    private static ProductCostCandidate Candidate(string sku, string? barcode, decimal? cost) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), 3, sku, barcode, $"Ürün {sku}", "Beden: M", cost);

    private static ProductCostFileRow Row(int number, string? sku, string? barcode, ProductCostValue value) => new(number, sku, barcode, value);
}

// The service against the real schema: template, preview and apply, and a preview that went stale.
[Collection("postgres-integration")]
public sealed class ProductCostPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    [PostgreSqlFact]
    public async Task APreviewedFile_IsAppliedOnce_AndAStalePreviewWritesNothing()
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"cost-{Guid.NewGuid():N}", DisplayName = "cost", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var product = new Product { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Title = "Maliyet Ürünü", Description = "", Status = ProductStatus.Active, CreatedAt = now, UpdatedAt = now };
        var first = Variant(tenant, product, "COST-1", "8690000100001", cost: null, now);
        var second = Variant(tenant, product, "COST-2", "8690000100002", cost: 4m, now);
        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.Add(tenant);
            seed.Products.Add(product);
            seed.ProductVariants.AddRange(first, second);
            await seed.SaveChangesAsync();
        }

        await using var db = fixture.CreateContext();
        var service = new ProductCostService(db, fixture.TimeProvider);
        var summary = await service.SummaryAsync(tenant.Id, CancellationToken.None);
        Assert.Equal(new ProductCostSummaryView(2, 1), summary.Value);
        var template = await service.TemplateAsync(tenant.Id, missingOnly: true, null, null, CancellationToken.None);
        var templateRows = ImportFileReader.ReadXlsxTable(new MemoryStream(template.Value!), 64 * 1024 * 1024).Rows;
        Assert.Equal("COST-1", Assert.Single(templateRows).Cells[2]);

        var csv = "Stok Kodu;Barkod;Yeni Maliyet\r\ncost_1;;1.234,50\r\n;8690000100002;sil\r\n";
        var preview = await service.PreviewAsync(tenant.Id, "maliyet.csv", new MemoryStream(Encoding.UTF8.GetBytes(csv)), CancellationToken.None);
        Assert.True(preview.Succeeded, preview.Error?.Message);
        Assert.Equal((1, 1), (preview.Value!.Updates, preview.Value.Clears));

        var applied = await service.ApplyAsync(tenant.Id, Guid.CreateVersion7(), new ApplyProductCostsCommand(preview.Value.Changes), "cost-test", CancellationToken.None);
        Assert.True(applied.Succeeded, applied.Error?.Message);
        Assert.Equal(new ProductCostApplyResult(1, 1, 1), applied.Value);
        await using (var check = fixture.CreateContext())
        {
            var stored = await check.ProductVariants.AsNoTracking().Where(x => x.TenantId == tenant.Id).OrderBy(x => x.Sku).ToListAsync();
            Assert.Equal(new decimal?[] { 1234.50m, null }, stored.Select(x => x.CostPrice));
            Assert.Equal(1, await check.AuditLogs.CountAsync(x => x.TenantId == tenant.Id && x.Action == "PRODUCT_COSTS_IMPORTED"));
            Assert.Equal(1, (await check.Products.AsNoTracking().SingleAsync(x => x.Id == product.Id)).Version);
        }

        // The same changes again: every cost already holds its value, so nothing is written.
        await using var again = fixture.CreateContext();
        var repeat = await new ProductCostService(again, fixture.TimeProvider).ApplyAsync(tenant.Id, Guid.CreateVersion7(), new ApplyProductCostsCommand(preview.Value.Changes), "cost-test-2", CancellationToken.None);
        Assert.Equal(new ProductCostApplyResult(0, 0, 0), repeat.Value);

        // A preview read before another change is refused whole.
        await using var stale = fixture.CreateContext();
        var current = await stale.ProductVariants.AsNoTracking().SingleAsync(x => x.Id == second.Id);
        // A change that is still current listed before a stale one: the refusal changes neither, not even in memory
        // (the idempotency middleware saves the context after a 409).
        var staleResult = await new ProductCostService(stale, fixture.TimeProvider).ApplyAsync(tenant.Id, Guid.CreateVersion7(),
            new ApplyProductCostsCommand([new ProductCostChange(second.Id, current.Version, 7m), new ProductCostChange(first.Id, first.Version, 99m)]), "cost-test-3", CancellationToken.None);
        Assert.Equal(409, staleResult.Error?.Status);
        Assert.False(stale.ChangeTracker.HasChanges());
    }

    private static ProductVariant Variant(Tenant tenant, Product product, string sku, string barcode, decimal? cost, DateTimeOffset now) => new()
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
        CreatedAt = now,
        UpdatedAt = now
    };
}
