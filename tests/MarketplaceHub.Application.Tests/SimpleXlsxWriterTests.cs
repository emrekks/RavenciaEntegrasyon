using System.IO.Compression;
using MarketplaceHub.Infrastructure.Imports;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class SimpleXlsxWriterTests
{
    [Fact]
    public void A_whole_number_is_written_without_decimals_and_money_keeps_two()
    {
        var bytes = SimpleXlsxWriter.Write(
        [
            new SimpleXlsxSheet("Ürünler", [new object?[] { "Stok", "Fiyat" }, new object?[] { 2L, 599m }], [10, 10])
        ]);

        using var zip = new ZipArchive(new MemoryStream(bytes));
        var sheet = Read(zip, "xl/worksheets/sheet1.xml");
        var styles = Read(zip, "xl/styles.xml");

        Assert.Contains("<c r=\"A2\" s=\"3\"><v>2</v></c>", sheet);
        Assert.Contains("<c r=\"B2\" s=\"2\"><v>599</v></c>", sheet);
        Assert.Contains("numFmtId=\"3\"", styles);
        Assert.Contains("<cellXfs count=\"4\">", styles);
    }

    private static string Read(ZipArchive zip, string name)
    {
        using var reader = new StreamReader(zip.GetEntry(name)!.Open());
        return reader.ReadToEnd();
    }
}
