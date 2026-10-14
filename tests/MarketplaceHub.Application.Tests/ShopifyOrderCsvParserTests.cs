using System.Globalization;
using System.Text;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Imports;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class ShopifyOrderCsvParserTests
{
    [Fact]
    public void Parse_GroupsShopifyLineRowsAndReadsOrderTotalsOnlyOnce()
    {
        var headers = new[]
        {
            "Name", "Created at", "Email", "Currency", "Subtotal", "Shipping", "Taxes", "Total", "Discount Amount",
            "Lineitem name", "Lineitem quantity", "Lineitem price", "Lineitem sku", "Billing Name",
            "Billing Address1", "Billing City", "Shipping Name", "Shipping Address1", "Shipping City"
        };
        var first = Row(headers.Length, (0, "#R1013"), (1, "2026-07-07 21:42:00 +0300"), (2, "buyer@example.test"), (3, "TRY"), (4, "120.00"), (5, "0.00"),
            (6, "20.00"), (7, "140.00"), (8, "10.00"), (9, "Blue dress, small"), (10, "2"), (11, "60.00"),
            (12, "SKU-1"), (13, "Ada Example"), (14, "Street, building 2"), (15, "Istanbul"), (16, "Ada Example"),
            (17, "Street, building 2"), (18, "Istanbul"));
        var second = Row(headers.Length, (0, "#R1013"), (9, "Hat \"Classic\"\nvariant"), (10, "1"), (11, "15.00"), (12, "SKU-2"));
        var csv = string.Join(',', headers) + "\r\n" + CsvRow(first) + "\r\n" + CsvRow(second) + "\r\n";

        var parsed = ShopifyOrderCsvParser.Parse(new StringReader(csv));

        var order = Assert.Single(parsed.Orders);
        Assert.Equal(2, parsed.FileRows);
        Assert.Equal(0, parsed.SkippedRows);
        Assert.Equal("#R1013", order.OrderNumber);
        Assert.Equal(DateTimeOffset.Parse("2026-07-07T21:42:00+03:00", CultureInfo.InvariantCulture), order.CreatedAt);
        Assert.Equal("TRY", order.Currency);
        Assert.Equal(140m, order.Total);
        Assert.Equal(10m, order.DiscountAmount);
        Assert.Equal("Ada Example", order.BillingAddress.Name);
        Assert.Equal("Street, building 2", order.BillingAddress.Address1);
        Assert.Equal(2, order.Lines.Count);
        Assert.Equal("Blue dress, small", order.Lines[0].Title);
        Assert.Equal("Hat \"Classic\"\nvariant", order.Lines[1].Title);
        Assert.Null(order.ValidationIssue);
    }

    [Fact]
    public void Parse_RejectsMissingRequiredColumnsAndFlagsConflictingOrderTotals()
    {
        Assert.Throws<FormatException>(() => ShopifyOrderCsvParser.Parse(new StringReader("Order,Lineitem name\r\n#1,Product\r\n")));

        const string conflicting = "Name,Currency,Total,Lineitem name\r\n#R1,TRY,10.00,Item A\r\n#R1,TRY,11.00,Item B\r\n";
        var parsed = ShopifyOrderCsvParser.Parse(new StringReader(conflicting));
        Assert.Contains("tutarsız", Assert.Single(parsed.Orders).ValidationIssue, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_RejectsMalformedQuotedCsv()
    {
        Assert.Throws<FormatException>(() => ShopifyOrderCsvParser.Parse(new StringReader("Name,Lineitem name\r\n#R1,\"not closed\r\n")));
    }

    [Fact]
    public void ImportedSnapshotSurvivesSparseRemoteSyncAndRetainsImportedLineValues()
    {
        var importedAt = DateTimeOffset.Parse("2026-09-23T12:00:00Z", CultureInfo.InvariantCulture);
        var importedCustomer = ShopifyOrderCsvSnapshotPolicy.ImportCustomer(
            "{}", "Ada Example", "buyer@example.test", null, "paid", "fulfilled", "card", 150m, 140m, 10m, "TRY", importedAt);
        var laterRemote = "{\"customerName\":null,\"email\":\"\",\"phone\":null}";
        var merged = ShopifyOrderCsvSnapshotPolicy.MergeRemoteSnapshot(laterRemote, importedCustomer);

        Assert.True(ShopifyOrderCsvSnapshotPolicy.HasImport(merged));
        Assert.Equal(150m, ShopifyOrderCsvSnapshotPolicy.ImportedAmount(merged, "grossTotal"));
        Assert.Equal(140m, ShopifyOrderCsvSnapshotPolicy.ImportedAmount(merged, "netTotal"));
        Assert.Contains("Ada Example", merged, StringComparison.Ordinal);
        Assert.Contains("buyer@example.test", merged, StringComparison.Ordinal);
        Assert.Equal((150m, 10m, 140m), ShopifyOrderCsvSnapshotPolicy.MergeRemoteAmounts(merged, 140m, 10m, 140m));
        Assert.Equal((900m, 0m, 900m), ShopifyOrderCsvSnapshotPolicy.MergeRemoteAmounts(merged, 900m, 0m, 900m));

        var importedLine = new ShopifyOrderCsvLine("SKU-1", "Blue dress", 60m);
        var source = ShopifyOrderCsvSnapshotPolicy.ImportLine("{}", importedLine, importedAt);
        var line = new OrderLine
        {
            Id = Guid.CreateVersion7(),
            TenantId = Guid.CreateVersion7(),
            OrderId = Guid.CreateVersion7(),
            ExternalLineId = "line-1",
            Sku = "SKU-1",
            TitleSnapshot = "Blue dress",
            RawStatus = "FULFILLED",
            SourceSnapshotJson = source,
            Version = 1
        };
        Assert.Equal("SKU-1", ShopifyOrderCsvSnapshotPolicy.PreserveRemoteSku("", line));
        Assert.Equal("SKU-1", ShopifyOrderCsvSnapshotPolicy.PreserveRemoteSku("SHOPIFY-LINE", line));
        Assert.Equal("Blue dress", ShopifyOrderCsvSnapshotPolicy.PreserveRemoteTitle("", line));
        Assert.Equal("Blue dress", ShopifyOrderCsvSnapshotPolicy.PreserveRemoteTitle("Shopify ürünü", line));
        Assert.Equal(60m, ShopifyOrderCsvSnapshotPolicy.ImportedAmount(source, "unitPrice"));
    }

    [Fact]
    public void NormalizeOrderNumberIgnoresHashAndWhitespace()
    {
        Assert.Equal("R1013", ShopifyOrderCsvParser.NormalizeOrderNumber(" # R10 13 "));
    }

    private static string[] Row(int length, params (int Index, string Value)[] values)
    {
        var row = Enumerable.Repeat("", length).ToArray();
        foreach (var (index, value) in values) row[index] = value;
        return row;
    }

    private static string CsvRow(IEnumerable<string> row) => string.Join(',', row.Select(value =>
        value.Contains(',') || value.Contains('"') || value.Contains('\r') || value.Contains('\n')
            ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\""
            : value));
}
