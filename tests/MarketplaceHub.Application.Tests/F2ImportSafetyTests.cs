using System.IO.Compression;
using System.Text;
using MarketplaceHub.Infrastructure.Imports;

namespace MarketplaceHub.Application.Tests;

public sealed class F2ImportSafetyTests
{
    private static readonly IReadOnlyDictionary<string, string> Mapping = new Dictionary<string, string>
    {
        ["Ürün"] = "title",
        ["SKU"] = "sku",
        ["Barkod"] = "barcode"
    };

    [Fact]
    public void Csv_reader_streams_rows_and_flags_formula_like_cells()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("Ürün,SKU,Barkod\r\nGömlek,SKU-1,123\r\n=2+2,SKU-2,456\r\n"));
        var rows = ImportFileReader.ReadCsv(stream, Mapping).ToList();
        Assert.Equal(2, rows.Count);
        Assert.Empty(rows[0].Errors);
        Assert.Contains(rows[1].Errors, error => error.Contains("formül", StringComparison.Ordinal));
    }

    [Fact]
    public void Csv_reader_rejects_malformed_quotes()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("Ürün,SKU\r\n\"Açık,SKU-1\r\n"));
        Assert.Throws<InvalidDataException>(() => ImportFileReader.ReadCsv(stream, Mapping).ToList());
    }

    [Fact]
    public void Xlsx_reader_rejects_macro_parts_before_parsing_cells()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var macro = archive.CreateEntry("xl/vbaProject.bin");
            using var writer = new StreamWriter(macro.Open()); writer.Write("unsafe");
        }
        stream.Position = 0;
        Assert.Throws<InvalidDataException>(() => ImportFileReader.ReadXlsx(stream, Mapping, 10 * 1024 * 1024).ToList());
    }

    [Theory]
    [InlineData("=2+2")]
    [InlineData("+cmd")]
    [InlineData("-1+1")]
    [InlineData("@SUM(A1)")]
    public void Error_export_neutralizes_spreadsheet_formula_prefixes(string value) =>
        Assert.StartsWith("\"'", ImportCsvSecurity.Neutralize(value), StringComparison.Ordinal);

    [Fact]
    public void Safe_xlsx_is_read_without_executing_formula_cells()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var sheet = archive.CreateEntry("xl/worksheets/sheet1.xml");
            using var writer = new StreamWriter(sheet.Open());
            writer.Write("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>" +
                "<row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>Ürün</t></is></c><c r=\"B1\" t=\"inlineStr\"><is><t>SKU</t></is></c></row>" +
                "<row r=\"2\"><c r=\"A2\" t=\"inlineStr\"><is><t>Gömlek</t></is></c><c r=\"B2\"><f>2+2</f><v>4</v></c></row>" +
                "</sheetData></worksheet>");
        }
        stream.Position = 0;
        var row = Assert.Single(ImportFileReader.ReadXlsx(stream, Mapping, 10 * 1024 * 1024));
        Assert.Contains(row.Errors, error => error.Contains("formül", StringComparison.Ordinal));
    }

    [Fact]
    public void Csv_reader_processes_the_ten_thousand_row_target_as_a_stream()
    {
        var builder = new StringBuilder("Ürün,SKU,Barkod\r\n");
        for (var index = 0; index < 10_000; index++) builder.Append("Ürün ").Append(index).Append(",SKU-").Append(index).Append(',').Append(index).Append("\r\n");
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(builder.ToString()));
        Assert.Equal(10_000, ImportFileReader.ReadCsv(stream, Mapping).Count());
    }
}
