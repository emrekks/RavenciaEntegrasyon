using System.Text;
using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Imports;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// K9: CSV files as Turkish users save them (Excel: ';' and Windows-1254, quoted multi-line descriptions).
public sealed class ImportFileReaderTests
{
    private static readonly Dictionary<string, string> Mappings = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Ürün"] = "title",
        ["SKU"] = "sku",
        ["Açıklama"] = "description",
        ["Stok"] = "stock"
    };

    private static List<ParsedImportRow> Read(byte[] bytes) => ImportFileReader.ReadCsv(new MemoryStream(bytes), Mappings).ToList();

    [Fact]
    public void SemicolonFile_InWindows1254_IsRead()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var bytes = Encoding.GetEncoding(1254).GetBytes("Ürün;SKU;Stok\r\nİpek Şal;ŞAL-1;5\r\n");

        var row = Assert.Single(Read(bytes));

        Assert.Empty(row.Errors);
        Assert.Equal("İpek Şal", row.Values["title"]);
        Assert.Equal("ŞAL-1", row.Values["sku"]);
        Assert.Equal("5", row.Values["stock"]);
    }

    [Fact]
    public void QuotedFieldsMaySpanLinesAndHoldTheDelimiter()
    {
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("Ürün,SKU,Açıklama\n\"Gömlek, mavi\",G-1,\"İlk satır\nikinci \"\"satır\"\"\"\nPantolon,P-1,Düz\n")).ToArray();

        var rows = Read(bytes);

        Assert.Equal(2, rows.Count);
        Assert.Equal("Gömlek, mavi", rows[0].Values["title"]);
        Assert.Equal("İlk satır\nikinci \"satır\"", rows[0].Values["description"]);
        Assert.Equal(2, rows[0].RowNumber);
        Assert.Equal(4, rows[1].RowNumber);
        Assert.Equal("P-1", rows[1].Values["sku"]);
    }

    // Turkish Excel saves 1250 formatted "#.##0" as "1.250"; read as a decimal point it became 1,25 TL and a stock of
    // "1.000" one unit. A lone dot with three digits after it is refused, as in the product file transfer.
    [Theory]
    [InlineData("1.250")]
    [InlineData("1.000")]
    [InlineData("12.345.678")]
    [InlineData("-1.250")]
    public void ThousandsOnlyNumbers_AreRejectedNotGuessed(string number)
    {
        var mappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Ürün"] = "title", ["SKU"] = "sku", ["Satış Fiyatı"] = "salePrice", ["Stok"] = "stock", ["Desi"] = "desi" };
        foreach (var column in new[] { "Satış Fiyatı", "Stok", "Desi" })
        {
            var cells = new[] { "Satış Fiyatı", "Stok", "Desi" }.Select(name => name == column ? number : "5");
            var row = Assert.Single(ImportFileReader.ReadCsv(new MemoryStream(Encoding.UTF8.GetBytes($"Ürün;SKU;Satış Fiyatı;Stok;Desi\nKazak;K-1;{string.Join(';', cells)}\n")), mappings));

            var error = Assert.Single(row.Errors);
            Assert.StartsWith($"{column}: \"{number}\" belirsiz", error, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("1.234,50", "1234.50")]
    [InlineData("1,234.50", "1234.50")]
    [InlineData("1234.5", "1234.5")]
    [InlineData("1234,5", "1234.5")]
    [InlineData("12.5", "12.5")]
    [InlineData("1250", "1250")]
    [InlineData("1,250", "1.250")]
    public void UnambiguousNumbers_AreReadAsBefore(string number, string expected)
    {
        var mappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Ürün"] = "title", ["SKU"] = "sku", ["Satış Fiyatı"] = "salePrice" };
        var row = Assert.Single(ImportFileReader.ReadCsv(new MemoryStream(Encoding.UTF8.GetBytes($"Ürün;SKU;Satış Fiyatı\nKazak;K-1;{number}\n")), mappings));

        Assert.Empty(row.Errors);
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), ImportJobProcessor.ParseDecimal(row.Values["salePrice"]));
    }

    [Fact]
    public void A_thousands_like_text_outside_the_number_columns_is_kept() =>
        Assert.Empty(Assert.Single(Read(Encoding.UTF8.GetBytes("Ürün;SKU\nKazak;1.250\n"))).Errors);

    [Fact]
    public void TabDelimitedFile_IsRecognised()
    {
        var row = Assert.Single(Read(Encoding.UTF8.GetBytes("Ürün\tSKU\nKazak\tK-1\n")));
        Assert.Equal("K-1", row.Values["sku"]);
    }

    [Fact]
    public void SignedNumbersAreKept_FormulasAreRejected()
    {
        var rows = Read(Encoding.UTF8.GetBytes("Ürün;SKU;Stok;Açıklama\nA;A-1;-5;-\nB;B-1;=1+1;x\nC;C-1;+90 532 000 00 00;@SUM(A1)\nD;D-1;-cmd|calc;y\n"));

        Assert.Empty(rows[0].Errors);
        Assert.Equal("-5", rows[0].Values["stock"]);
        Assert.Equal("-", rows[0].Values["description"]);
        Assert.Single(rows[1].Errors);
        Assert.Single(rows[2].Errors);
        Assert.Single(rows[3].Errors);
    }

    [Fact]
    public void UnclosedQuote_IsReportedWithItsLine()
    {
        var error = Assert.Throws<InvalidDataException>(() => Read(Encoding.UTF8.GetBytes("Ürün,SKU\nA,A-1\n\"B,B-1\n")));
        Assert.Contains("satır 3", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void QuoteInsideAnUnquotedField_IsALiteralCharacter()
    {
        var rows = Read(Encoding.UTF8.GetBytes("Ürün;SKU;Stok\n24\" Monitör;M-24;3\nKlavye;K-1; \"5\"\n"));

        Assert.Equal(2, rows.Count);
        Assert.Equal("24\" Monitör", rows[0].Values["title"]);
        Assert.Equal("M-24", rows[0].Values["sku"]);
        Assert.Equal("3", rows[0].Values["stock"]);
        Assert.Equal("K-1", rows[1].Values["sku"]);
        Assert.Equal("5", rows[1].Values["stock"]);
    }

    [Fact]
    public void Utf16AndUtf8ByteOrderMarks_AreDecodedAndDropped()
    {
        const string content = "Ürün\tSKU\nİpek Şal\tŞAL-1\n";
        foreach (var bytes in new[] { Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(content)).ToArray(), Encoding.BigEndianUnicode.GetPreamble().Concat(Encoding.BigEndianUnicode.GetBytes(content)).ToArray() })
        {
            var row = Assert.Single(Read(bytes));
            Assert.Equal("İpek Şal", row.Values["title"]);
            Assert.Equal("ŞAL-1", row.Values["sku"]);
        }

        // A UTF-8 BOM in front of bytes that are not valid UTF-8 (Windows-1254 body) is not left in the header.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var mixed = Encoding.UTF8.GetPreamble().Concat(Encoding.GetEncoding(1254).GetBytes("Ürün;SKU\nŞal;Ş-1\n")).ToArray();
        Assert.Equal("Ş-1", Assert.Single(Read(mixed)).Values["sku"]);
    }

    [Fact]
    public void CategoryName_MatchesMarketplacePathKeysByLeafNameOrFullPath_AndWarnsWhenAmbiguous()
    {
        var tayt = new ImportJobProcessor.ImportCategoryLookup.Leaf(Guid.NewGuid(), "Tayt", "Giyim / Alt Giyim / Tayt", "GIYIM-ALT-GIYIM-TAYT");
        var panelShirt = new ImportJobProcessor.ImportCategoryLookup.Leaf(Guid.NewGuid(), "Gömlek", "Gömlek", "GOMLEK");
        var womenSocks = new ImportJobProcessor.ImportCategoryLookup.Leaf(Guid.NewGuid(), "Çorap", "Kadın / Çorap", "KADIN-CORAP");
        var menSocks = new ImportJobProcessor.ImportCategoryLookup.Leaf(Guid.NewGuid(), "Çorap", "Erkek / Çorap", "ERKEK-CORAP");
        var names = new[] { "Tayt", "giyim / alt giyim / tayt", "Gömlek", "Çorap", "Kadın / Çorap", "Ceket" };
        var lookup = ImportJobProcessor.ImportCategoryLookup.Build([tayt, panelShirt, womenSocks, menSocks], names);

        Assert.Equal(tayt.Id, lookup.Resolve("Tayt"));
        Assert.Equal(tayt.Id, lookup.Resolve("giyim / alt giyim / tayt"));
        Assert.Equal(panelShirt.Id, lookup.Resolve("Gömlek"));
        Assert.Equal(womenSocks.Id, lookup.Resolve("Kadın / Çorap"));
        Assert.Null(lookup.Resolve("Çorap", out var ambiguous));
        Assert.Contains("birden fazla", ambiguous, StringComparison.Ordinal);
        Assert.Null(lookup.Resolve("Ceket", out var missing));
        Assert.Contains("yok", missing, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1234.5", "1234.5")]
    [InlineData("1234,5", "1234.5")]
    [InlineData("1.234,50", "1234.50")]
    [InlineData("1,234.50", "1234.50")]
    [InlineData("abc", null)]
    public void PricesAcceptTurkishAndInvariantSeparators(string value, string? expected) =>
        Assert.Equal(expected is null ? null : decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), ImportJobProcessor.ParseDecimal(value));

    [Theory]
    [InlineData("{\"DefaultVatRate\":20}", "20")]
    [InlineData("{\"DefaultVatRate\":\"10\"}", "10")]
    [InlineData("{\"DefaultVatRate\":120}", null)]
    [InlineData("{}", null)]
    [InlineData("not json", null)]
    public void ConnectionDefaultVatRate_IsReadFromSettings(string json, string? expected) =>
        Assert.Equal(expected is null ? null : decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), ConnectionDefaults.VatRate(json));
}
