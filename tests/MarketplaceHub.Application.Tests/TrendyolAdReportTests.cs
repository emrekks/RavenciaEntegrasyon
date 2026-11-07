using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Imports;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Trendyol's "Ürün Reklamları Raporum" export, as Trendyol writes it (every figure Turkish text, "-" for none), with
// made-up campaigns: each campaign read, the totals added up and the export time read from the file's name.
public sealed class TrendyolAdReportTests
{
    private static readonly string[] Headers =
    [
        "Reklam Adı", "Reklam Statüsü", "Başlangıç Tarihi", "Bitiş Tarihi", "Ürün Adedi", "Ürün ContentId Listesi", "Toplam Bütçe", "Günlük Bütçe", "Kalan Bütçe",
        "Harcanan Bütçe", "TBM Teklifi", "Gerçekleşen TBM", "Tıklanma", "Görüntülenme", "Doğrudan Satış Adedi", "Dolaylı Satış Adedi", "Toplam Satış Adedi",
        "Doğrudan Reklam Cirosu", "Dolaylı Reklam Cirosu", "Toplam Reklam Cirosu", "Harcama Getirisi"
    ];

    private static XlsxTable Table(params string[][] rows) =>
        ImportFileReader.ReadXlsxTable(new MemoryStream(SimpleXlsxWriter.Write([new SimpleXlsxSheet("Rapor", [Headers.Cast<object?>().ToList(), .. rows.Select(row => row.Cast<object?>().ToList())], Headers.Select(_ => 15d).ToList())])), 10_000_000);

    private static readonly string[] Total = ["Ürün-A", "Yayında", "2026-11-02 17:20", "-", "12", "111,222", "2.000", "-", "0", "2.000", "", "2,50", "800", "40000", "4", "6", "10", "2.400,50", "3.600,25", "6.000,75", "3,00"];
    private static readonly string[] Daily = ["Ürün-B", "Durduruldu", "2026-10-31 15:20", "2026-11-05 10:00", "5", "333", "-", "750", "120,40", "1.250,10", "1,10 - 1,30 - 1,10", "1,25", "1000", "60000", "3", "9", "12", "1.100,00", "8.900,00", "10.000,00", "8,00"];

    [Fact]
    public void Each_campaign_is_read_with_its_turkish_figures_and_dashes_stay_unknown()
    {
        var parsed = TrendyolAdReportParser.Parse(Table(Total, Daily));

        Assert.Empty(parsed.Warnings);
        Assert.Equal(2, parsed.Campaigns.Count);
        var a = parsed.Campaigns[0];
        Assert.Equal(("Ürün-A", "Yayında", 12), (a.Name, a.Status, a.ProductCount!.Value));
        Assert.Equal((2000m, (decimal?)null, 2000m), (a.TotalBudget!.Value, a.DailyBudget, a.Spent!.Value));
        Assert.Null(a.End);
        Assert.Null(a.CpcBid);
        Assert.Equal((800L, 40000L), (a.Clicks!.Value, a.Impressions!.Value));
        Assert.Equal((2400.50m, 3600.25m, 6000.75m, 3.00m), (a.DirectRevenue!.Value, a.IndirectRevenue!.Value, a.TotalRevenue!.Value, a.Roas!.Value));
        var b = parsed.Campaigns[1];
        Assert.Equal("1,10 - 1,30 - 1,10", b.CpcBid);
        Assert.Equal((750m, 120.40m, 1250.10m), (b.DailyBudget!.Value, b.RemainingBudget!.Value, b.Spent!.Value));
    }

    // Spent 3,250.10 and revenue 16,000.75 over both: ROAS 4.92 (revenue over spend, not the rows' average), a click at
    // 1.81 and 1.8 % of the impressions clicked.
    [Fact]
    public void The_totals_add_the_campaigns_up_and_the_rates_come_from_the_sums()
    {
        var totals = AdReportService.Totals(TrendyolAdReportParser.Parse(Table(Total, Daily)).Campaigns);

        Assert.Equal((3250.10m, 16000.75m, 3500.50m, 12500.25m), (totals.Spent, totals.Revenue, totals.DirectRevenue, totals.IndirectRevenue));
        Assert.Equal(4.92m, totals.Roas);
        Assert.Equal((1800L, 100000L), (totals.Clicks, totals.Impressions));
        Assert.Equal(1.81m, totals.Cpc);
        Assert.Equal(0.018m, totals.Ctr);
        Assert.Equal((22m, 7m, 15m), (totals.Sales, totals.DirectSales, totals.IndirectSales));
    }

    [Fact]
    public void A_figure_that_does_not_read_stays_unknown_with_a_warning()
    {
        var odd = (string[])Total.Clone();
        odd[9] = "çok";
        var parsed = TrendyolAdReportParser.Parse(Table(odd));

        Assert.Null(Assert.Single(parsed.Campaigns).Spent);
        Assert.Contains(parsed.Warnings, warning => warning.Contains("Harcanan Bütçe", StringComparison.Ordinal));
        // Without a spend there is no ROAS from the sums.
        Assert.Null(AdReportService.Totals(parsed.Campaigns).Roas);
    }

    // Trendyol writes its texts as shared strings with nothing between </t> and </si> (7 Nov 2026 export): each one is
    // read, the empty one included, and not shifted onto its neighbour.
    [Fact]
    public void A_workbook_of_shared_strings_reads_each_string()
    {
        using var buffer = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(buffer, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            void Entry(string name, string xml)
            {
                using var writer = new StreamWriter(archive.CreateEntry(name).Open());
                writer.Write(xml);
            }
            Entry("xl/sharedStrings.xml", """<?xml version="1.0" encoding="UTF-8"?><sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" count="5" uniqueCount="5"><si><t>Reklam Adı</t></si><si><t>Harcanan Bütçe</t></si><si><t>Toplam Reklam Cirosu</t></si><si><t></t></si><si><t>1.508,76</t></si></sst>""");
            Entry("xl/worksheets/sheet1.xml", """<?xml version="1.0" encoding="UTF-8"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData><row r="1"><c r="A1" t="s"><v>0</v></c><c r="B1" t="s"><v>1</v></c><c r="C1" t="s"><v>2</v></c></row><row r="2"><c r="A2" t="s"><v>4</v></c><c r="B2" t="s"><v>3</v></c><c r="C2" t="s"><v>4</v></c></row></sheetData></worksheet>""");
        }
        buffer.Position = 0;

        var table = ImportFileReader.ReadXlsxTable(buffer, 10_000_000);

        Assert.Equal(["Reklam Adı", "Harcanan Bütçe", "Toplam Reklam Cirosu"], table.Headers);
        Assert.Equal(["1.508,76", "", "1.508,76"], Assert.Single(table.Rows).Cells);
    }

    [Fact]
    public void Another_file_is_refused()
    {
        var other = ImportFileReader.ReadXlsxTable(new MemoryStream(SimpleXlsxWriter.Write([new SimpleXlsxSheet("Rapor", [["Barkod", "Fiyat"], ["X", "10"]], [15d, 15d])])), 10_000_000);
        Assert.Throws<InvalidDataException>(() => TrendyolAdReportParser.Parse(other));
    }

    [Theory]
    [InlineData("Ürün Reklamları Raporum - 07.11.2026-03.54.xlsx", "2026-11-07T00:54:00+00:00")]
    [InlineData("rapor.xlsx", null)]
    [InlineData("Ürün Reklamları Raporum - 31.02.2026-03.54.xlsx", null)]
    public void The_export_time_is_read_from_the_name_in_istanbul_time(string name, string? expected)
    {
        Assert.Equal(expected is null ? null : DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), TrendyolAdReportParser.ExportedAt(name));
    }

    [Theory]
    [InlineData("1.508,76", 1508.76)]
    [InlineData("1.000", 1000)]
    [InlineData("12.345.678", 12345678)]
    [InlineData("2,18", 2.18)]
    [InlineData("458", 458)]
    public void Turkish_figures_read(string text, double expected)
    {
        Assert.True(TrendyolAdReportParser.TryTurkishNumber(text, out var value));
        Assert.Equal((decimal)expected, value);
    }
}
