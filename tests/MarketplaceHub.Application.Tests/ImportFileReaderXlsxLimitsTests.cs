using System.IO.Compression;
using System.Text;
using MarketplaceHub.Infrastructure.Imports;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// SEC-01: a cell reference past Excel's last column (XFD), a malformed one, or a file with more rows, columns or cells
// than the reader's caps is refused with InvalidDataException (the upload answers 422) instead of being expanded into
// memory or failing with an OverflowException (500).
public sealed class ImportFileReaderXlsxLimitsTests
{
    private const long MaximumBytes = 64 * 1024 * 1024;

    private static string Column(int zeroBasedIndex)
    {
        var n = zeroBasedIndex + 1; var text = string.Empty;
        while (n > 0) { n--; text = (char)('A' + n % 26) + text; n /= 26; }
        return text;
    }

    private static MemoryStream Workbook(string sheetRows)
    {
        var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("xl/worksheets/sheet1.xml");
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write($"<?xml version=\"1.0\" encoding=\"UTF-8\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>{sheetRows}</sheetData></worksheet>");
        }
        buffer.Position = 0;
        return buffer;
    }

    private static string Cell(string reference, string text) => $"<c r=\"{reference}\" t=\"inlineStr\"><is><t>{text}</t></is></c>";

    private static string WithFarCell(string reference) => $"<row r=\"1\">{Cell("A1", "SKU")}</row><row r=\"2\">{Cell("A2", "X-1")}{Cell(reference, "far")}</row>";

    [Theory]
    [InlineData(16_384)] // XFE: one past Excel's last column
    [InlineData(200_000)]
    [InlineData(2_000_000)]
    public void A_cell_past_XFD_is_refused_without_expanding_the_row(int columnIndex)
    {
        Assert.Equal("XFD", Column(16_383));
        using var file = Workbook(WithFarCell(Column(columnIndex) + "2"));
        var before = GC.GetAllocatedBytesForCurrentThread();
        var exception = Assert.Throws<InvalidDataException>(() => ImportFileReader.ReadXlsxTable(file, MaximumBytes));
        Assert.Contains(Column(columnIndex) + "2", exception.Message, StringComparison.Ordinal);
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 4_000_000, "the far column was not materialised");
    }

    [Fact]
    public void A_column_within_Excel_but_past_the_column_cap_is_refused()
    {
        using var file = Workbook(WithFarCell(Column(ImportFileReader.MaximumColumns) + "2"));
        Assert.Throws<InvalidDataException>(() => ImportFileReader.ReadXlsxTable(file, MaximumBytes));
        using var lastAllowed = Workbook(WithFarCell(Column(ImportFileReader.MaximumColumns - 1) + "2"));
        var table = ImportFileReader.ReadXlsxTable(lastAllowed, MaximumBytes);
        Assert.Equal(ImportFileReader.MaximumColumns, table.Rows[0].Cells.Count);
        Assert.Equal("far", table.Rows[0].Cells[^1]);
    }

    [Theory]
    [InlineData("ZZZZZZZ2")] // overflowed the old parser (OverflowException, a 500)
    [InlineData("EZZZZZZ2")] // the largest seven-letter column that did not overflow (~1.87e9 cells in a dense row)
    [InlineData("AAAA2")]
    [InlineData("2")]
    [InlineData("A")]
    [InlineData("A0")]
    [InlineData("A1048577")]
    [InlineData("A-1")]
    [InlineData("Ä2")]
    public void A_malformed_or_out_of_sheet_reference_is_InvalidDataException_on_every_reader(string reference)
    {
        using var table = Workbook(WithFarCell(reference));
        Assert.Throws<InvalidDataException>(() => ImportFileReader.ReadXlsxTable(table, MaximumBytes));

        // The import reader (header and data rows) and the product-transfer parser, whose service callers map
        // InvalidDataException to 422.
        using var header = Workbook($"<row r=\"1\">{Cell(reference, "SKU")}</row>");
        Assert.Throws<InvalidDataException>(() => ImportFileReader.ReadXlsx(header, new Dictionary<string, string>(), MaximumBytes).ToList());
        using var data = Workbook($"<row r=\"1\">{Cell("A1", "SKU")}{Cell("B1", "title")}</row><row r=\"2\">{Cell(reference, "x")}</row>");
        Assert.Throws<InvalidDataException>(() => ImportFileReader.ReadXlsx(data, new Dictionary<string, string> { ["SKU"] = "sku", ["title"] = "title" }, MaximumBytes).ToList());
        using var transfer = Workbook(WithFarCell(reference));
        Assert.Throws<InvalidDataException>(() => ProductTransferFileParser.ParseXlsx(transfer, MaximumBytes));
    }

    [Fact]
    public void A_row_number_outside_the_sheet_is_refused()
    {
        using var file = Workbook($"<row r=\"1\">{Cell("A1", "SKU")}</row><row r=\"99999999999\">{Cell("A2", "x")}</row>");
        Assert.Throws<InvalidDataException>(() => ImportFileReader.ReadXlsxTable(file, MaximumBytes));
    }

    [Fact]
    public void More_rows_than_the_cap_are_refused()
    {
        var rows = new StringBuilder($"<row r=\"1\">{Cell("A1", "SKU")}</row>");
        for (var row = 2; row <= ImportFileReader.MaximumRows + 1; row++) rows.Append("<row r=\"").Append(row).Append("\"/>");
        using var file = Workbook(rows.ToString());
        var exception = Assert.Throws<InvalidDataException>(() => ImportFileReader.ReadXlsxTable(file, MaximumBytes));
        Assert.Contains("satır", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void More_cells_than_the_cap_are_refused_even_when_each_row_is_within_the_column_cap()
    {
        // Every row reaches the last allowed column with a single cell: the dense rows, not the file, would be large.
        var rowsNeeded = (int)(ImportFileReader.MaximumCells / ImportFileReader.MaximumColumns) + 2;
        Assert.True(rowsNeeded < ImportFileReader.MaximumRows);
        var last = Column(ImportFileReader.MaximumColumns - 1);
        var rows = new StringBuilder($"<row r=\"1\">{Cell("A1", "SKU")}</row>");
        for (var row = 2; row <= rowsNeeded + 1; row++) rows.Append("<row r=\"").Append(row).Append("\"><c r=\"").Append(last).Append(row).Append("\"><v>1</v></c></row>");
        using var file = Workbook(rows.ToString());
        var exception = Assert.Throws<InvalidDataException>(() => ImportFileReader.ReadXlsxTable(file, MaximumBytes));
        Assert.Contains("hücre", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_cell_without_a_reference_sits_after_the_one_before_it()
    {
        using var file = Workbook($"<row r=\"1\">{Cell("A1", "SKU")}<c t=\"inlineStr\"><is><t>title</t></is></c></row><row r=\"2\">{Cell("A2", "X-1")}<c t=\"inlineStr\"><is><t>Lamba</t></is></c></row>");
        var table = ImportFileReader.ReadXlsxTable(file, MaximumBytes);
        Assert.Equal(["SKU", "title"], table.Headers);
        Assert.Equal(["X-1", "Lamba"], table.Rows[0].Cells);
    }

    [Fact]
    public void Lowercase_and_three_letter_references_within_XFD_still_read()
    {
        using var file = Workbook($"<row r=\"1\">{Cell("a1", "SKU")}{Cell("b1", "title")}</row><row r=\"2\">{Cell("a2", "X-1")}{Cell("b2", "Lamba")}</row>");
        var table = ImportFileReader.ReadXlsxTable(file, MaximumBytes);
        Assert.Equal(["SKU", "title"], table.Headers);
        Assert.Equal(["X-1", "Lamba"], table.Rows[0].Cells);
        Assert.Equal(16_383, ImportFileReader.ColumnIndex("XFD1"));
        Assert.Equal(0, ImportFileReader.ColumnIndex("A1048576"));
    }
}
