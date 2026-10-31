using System.IO.Compression;
using System.Text;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Imports;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class CommissionTariffTests
{
    // The tariff table groups a model's rows by the panel variant's colour, read from its option signature.
    [Theory]
    [InlineData("Renk: Bej | Beden: L | Desen: Çiçekli", "Bej")]
    [InlineData("Beden: XL | renk:  Mürdüm ", "Mürdüm")]
    [InlineData("Beden: XL", null)]
    [InlineData(null, null)]
    public void A_variants_colour_comes_from_its_options(string? signature, string? expected) =>
        Assert.Equal(expected, MarketplaceHub.Infrastructure.Persistence.CommissionTariffService.ColorOf(signature));

    // The bands of a row in Trendyol's weekly tariff export (22 Ekim 08.00-29 Ekim 07.59).
    private static CommissionTariffRow SampleRow() => new()
    {
        Barcode = "RYP00101",
        Tier1Min = 470.64m,
        Tier2Max = 470.63m,
        Tier2Min = 435.98m,
        Tier3Max = 435.97m,
        Tier3Min = 388.64m,
        Tier4Max = 388.63m,
        Rate1 = 21.5m,
        Rate2 = 20.6m,
        Rate3 = 19.4m,
        Rate4 = 17.5m
    };

    [Theory]
    [InlineData("519.75", 1, "21.5")]
    [InlineData("470.64", 1, "21.5")]
    [InlineData("470.63", 2, "20.6")]
    [InlineData("435.98", 2, "20.6")]
    [InlineData("435.97", 3, "19.4")]
    [InlineData("388.64", 3, "19.4")]
    [InlineData("388.63", 4, "17.5")]
    [InlineData("10", 4, "17.5")]
    [InlineData("470.634", 2, "20.6")]
    [InlineData("470.635", 1, "21.5")]
    public void ABasisPriceFallsInTheBandItsRoundedPriceReaches(string price, int tier, string rate)
    {
        var band = CommissionTariffBands.BandFor(SampleRow(), decimal.Parse(price, System.Globalization.CultureInfo.InvariantCulture));
        Assert.NotNull(band);
        Assert.Equal(tier, band.Tier);
        Assert.Equal(decimal.Parse(rate, System.Globalization.CultureInfo.InvariantCulture), band.Rate);
    }

    [Fact]
    public void AMissingBandIsNeverGuessedAndTheNextLowerBandIsTheNearestCheaperRate()
    {
        var twoBands = new CommissionTariffRow { Barcode = "X", Tier1Min = 500m, Rate1 = 20m, Tier4Max = 399.99m, Rate4 = 15m };
        Assert.Equal(1, CommissionTariffBands.BandFor(twoBands, 600m)?.Tier);
        Assert.Equal(4, CommissionTariffBands.BandFor(twoBands, 300m)?.Tier);
        // 400-499.99 is in no band of this row: the rate is unknown.
        Assert.Null(CommissionTariffBands.BandFor(twoBands, 450m));
        Assert.Null(CommissionTariffBands.BandFor(new CommissionTariffRow { Barcode = "Y" }, 100m));

        var next = CommissionTariffBands.NextLowerBand(SampleRow(), 519.75m);
        Assert.Equal(2, next?.Tier);
        Assert.Equal(470.63m, next?.Max);
        Assert.Null(CommissionTariffBands.NextLowerBand(SampleRow(), 300m));
    }

    [Fact]
    public void EachBandIsPricedAtTheHighestPriceThatEarnsItAndTheBandLeavingTheMostIsRecommended()
    {
        // At 519,75 the product is already in band 1: picking it keeps the price; the others go to their upper limit.
        var row = SampleRow();
        row.BasisPrice = 519.75m;
        // Cost 160, VAT 10 %, the store's cargo tariff 98,34.
        var facts = new Infrastructure.Persistence.UnitProfitFacts(160m, 10m, 98.34m);
        var choices = Infrastructure.Persistence.CommissionTariffChoices.Of(row, facts);
        Assert.Equal([1, 2, 3, 4], choices.Select(x => x.Tier));
        Assert.Equal([519.75m, 470.63m, 435.97m, 388.63m], choices.Select(x => x.Price));
        // 470,63 × %20,6 = 96,95 of commission; 160 cost, 98,34 cargo, 13,19 service fee, 4,28 withholding tax and a net
        // VAT of 42,78 − 14,55 − 16,16 − 16,39 − 2,20 = −6,52 leave 104,39.
        Assert.Equal(96.95m, choices[1].Commission);
        Assert.Equal(104.39m, choices[1].Remaining);
        Assert.Equal((160m, 98.34m, 13.19m, 4.28m, -6.52m), (choices[1].Costs!.Cost, choices[1].Costs!.Cargo, choices[1].Costs!.ServiceFee, choices[1].Costs!.Stoppage, choices[1].Costs!.NetVat));
        Assert.Equal(1, Infrastructure.Persistence.CommissionTariffChoices.Recommended(choices));

        // Below band 1 the open band starts at its lower limit; 470,64 at %21,5 leaves less than 470,63 at %20,6.
        row.BasisPrice = 450m;
        choices = Infrastructure.Persistence.CommissionTariffChoices.Of(row, facts);
        Assert.Equal(470.64m, choices[0].Price);
        // Melontik's example of the same product: 470,64 at %21,5 leaves 100,86 (it shows 100,85, its commission VAT
        // 16,865 rounded down) after a net VAT of −7,22.
        Assert.Equal((101.19m, 100.86m, -7.22m), (choices[0].Commission, choices[0].Remaining!.Value, choices[0].Costs!.NetVat));
        Assert.Equal(2, Infrastructure.Persistence.CommissionTariffChoices.Recommended(choices));

        // Without a cost, a VAT rate or a cargo tariff the profit is not known, so nothing is recommended; a price of the
        // seller's own falls in its band.
        var none = Infrastructure.Persistence.UnitProfitFacts.None;
        Assert.Null(Infrastructure.Persistence.CommissionTariffChoices.Recommended(Infrastructure.Persistence.CommissionTariffChoices.Of(row, none)));
        Assert.Null(Infrastructure.Persistence.CommissionTariffChoices.Recommended(Infrastructure.Persistence.CommissionTariffChoices.Of(row, facts with { Cargo = null })));
        Assert.Equal(3, Infrastructure.Persistence.CommissionTariffChoices.PricedAt(row, 400m, none)?.Tier);
    }

    [Fact]
    public void APickForEveryRowTakesABandOrTheBestByAmountOrByShareAndLeavesOutWhatFallsShortOfItsCondition()
    {
        // Over a cost of 100 the highest price leaves the most money and, the cost being the same in every band, the most
        // on the cost too (band 1: 190,80).
        var row = SampleRow();
        row.BasisPrice = 519.75m;
        var choices = Infrastructure.Persistence.CommissionTariffChoices.Of(row, new(100m, 10m, 98.34m));
        Assert.Equal(1, Infrastructure.Persistence.CommissionTariffChoices.Pick(choices, "amount", null, null, null)?.Tier);
        Assert.Equal(1, Infrastructure.Persistence.CommissionTariffChoices.Pick(choices, "rate", null, null, null)?.Tier);
        // Band 2 leaves 158,93, %158,93 of the cost: kept over 150 lira, left out over %160.
        Assert.Equal((158.93m, 158.93m), (choices[1].Remaining!.Value, Infrastructure.Persistence.CommissionTariffChoices.OnCost(choices[1])!.Value));
        Assert.Equal(2, Infrastructure.Persistence.CommissionTariffChoices.Pick(choices, "tier", 2, null, 150m)?.Tier);
        Assert.Null(Infrastructure.Persistence.CommissionTariffChoices.Pick(choices, "tier", 2, 160m, null));
        // A condition needs the profit: without it the row is left out rather than picked blind.
        var none = Infrastructure.Persistence.UnitProfitFacts.None;
        Assert.Null(Infrastructure.Persistence.CommissionTariffChoices.Pick(Infrastructure.Persistence.CommissionTariffChoices.Of(row, none), "tier", 2, 10m, null));
        Assert.Equal(2, Infrastructure.Persistence.CommissionTariffChoices.Pick(Infrastructure.Persistence.CommissionTariffChoices.Of(row, none), "tier", 2, null, null)?.Tier);
    }

    [Fact]
    public void ThePeriodTextIsReadInTurkeyTimeWithTheNearestYearAndAnExclusiveEnd()
    {
        var now = new DateTimeOffset(2026, 10, 21, 18, 7, 55, TimeSpan.Zero);
        Assert.True(CommissionTariffFileParser.TryParsePeriod("22 Ekim 08.00-29 Ekim 07.59", now, out var start, out var end));
        Assert.Equal(new DateTimeOffset(2026, 10, 22, 8, 0, 0, TimeSpan.FromHours(3)), start);
        Assert.Equal(new DateTimeOffset(2026, 10, 29, 8, 0, 0, TimeSpan.FromHours(3)), end);

        // Across New Year: the end falls in the next year.
        Assert.True(CommissionTariffFileParser.TryParsePeriod("29 Aralık 08.00 - 5 Ocak 07.59", new DateTimeOffset(2026, 12, 28, 9, 0, 0, TimeSpan.Zero), out start, out end));
        Assert.Equal(2026, start.Year);
        Assert.Equal(new DateTimeOffset(2027, 1, 5, 8, 0, 0, TimeSpan.FromHours(3)), end);
        // Uploaded in early January for the week that began in December.
        Assert.True(CommissionTariffFileParser.TryParsePeriod("29 Aralık 08.00-5 Ocak 07.59", new DateTimeOffset(2027, 1, 2, 9, 0, 0, TimeSpan.Zero), out start, out _));
        Assert.Equal(2026, start.Year);

        Assert.True(CommissionTariffFileParser.TryParsePeriod("3 ŞUBAT 08:00-10 şubat 07:59", now, out start, out _));
        Assert.Equal(2, start.Month);
        Assert.False(CommissionTariffFileParser.TryParsePeriod("22 Ekim", now, out _, out _));
        Assert.False(CommissionTariffFileParser.TryParsePeriod("22 Foo 08.00-29 Ekim 07.59", now, out _, out _));
        Assert.False(CommissionTariffFileParser.TryParsePeriod("22 Ekim 25.00-29 Ekim 07.59", now, out _, out _));
    }

    [Theory]
    [InlineData("470.64", "470.64")]
    [InlineData("470,64", "470.64")]
    [InlineData("1.234,56", "1234.56")]
    [InlineData("21.5", "21.5")]
    [InlineData("%20", "20")]
    [InlineData("519.75 ₺", "519.75")]
    public void NumbersReadInTheExportAndTurkishForm(string text, string expected)
    {
        Assert.True(CommissionTariffFileParser.TryParseNumber(text, out var value));
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), value);
    }

    [Fact]
    public void AnExportIsReadByItsHeadersAndBadRowsAreSkippedWithAReason()
    {
        string[] headers = ["ÜRÜN İSMİ", "BARKOD", "SATICI STOK KODU", "BEDEN", "MODEL KODU", "KATEGORİ", "MARKA", "STOK", "1.Fiyat Alt Limit", "2.Fiyat Üst Limiti", "2.Fiyat Alt Limit", "3.Fiyat Üst Limiti", "3.Fiyat Alt Limit", "4.Fiyat Üst Limiti", "Tarih aralığı (7 Gün)", "1.KOMİSYON", "2.KOMİSYON", "3.KOMİSYON", "4.KOMİSYON", "KOMİSYONA ESAS FİYAT", "GÜNCEL KOMİSYON", "GÜNCEL TSF", "TARİFE GRUBU"];
        var bytes = Xlsx(headers,
        [
            ["Bluz", "RYP00101", "RYP00101", "M", "RYP001", "Bluz", "Ravencia", "14.0", "470.64", "470.63", "435.98", "435.97", "388.64", "388.63", "22 Ekim 08.00-29 Ekim 07.59", "21.5", "20.6", "19.4", "17.5", "519.75", "21.5", "525.0", "g1"],
            ["Bluz", "", "RYP00102", "L", "RYP001", "Bluz", "Ravencia", "1", "470.64", "", "", "", "", "", "22 Ekim 08.00-29 Ekim 07.59", "21.5", "", "", "", "", "", "", ""],
            ["Bluz", "RYP00103", "RYP00103", "XL", "RYP001", "Bluz", "Ravencia", "1", "abc", "", "", "", "", "", "22 Ekim 08.00-29 Ekim 07.59", "21.5", "", "", "", "", "", "", ""],
            ["Bluz", "RYP00101", "RYP00101", "M", "RYP001", "Bluz", "Ravencia", "1", "470.64", "", "", "", "", "", "22 Ekim 08.00-29 Ekim 07.59", "21.5", "", "", "", "", "", "", ""]
        ]);
        var table = ImportFileReader.ReadXlsxTable(new MemoryStream(bytes), 10_000_000);
        var parsed = CommissionTariffFileParser.Parse(table, new DateTimeOffset(2026, 10, 21, 18, 0, 0, TimeSpan.Zero));

        var tariff = Assert.Single(parsed.Tariffs);
        var row = Assert.Single(tariff.Rows);
        Assert.Equal("RYP00101", row.Barcode);
        Assert.Equal(470.64m, row.Tier1Min);
        Assert.Equal(17.5m, row.Rate4);
        Assert.Equal(519.75m, row.BasisPrice);
        Assert.Equal(525.0m, row.CurrentSalePrice);
        Assert.Equal(("Bluz", "Ravencia", 14.0m), (row.Category, row.Brand, row.Stock!.Value));
        Assert.Equal(3, parsed.SkippedRows);
        Assert.Contains(parsed.Warnings, warning => warning.Contains("barkod yok", StringComparison.Ordinal));
        Assert.Contains(parsed.Warnings, warning => warning.Contains("1.Fiyat Alt Limit değeri sayı değil", StringComparison.Ordinal));
        Assert.Contains(parsed.Warnings, warning => warning.Contains("birden çok kez", StringComparison.Ordinal));
    }

    // Trendyol's export of 31 Oct 2026: the band limits once, then "Tarih aralığı (3 Gün)" with its four rates and
    // "Tarih aralığı (4 Gün)" with four more. Each period is a tariff of its own with its own rates.
    [Fact]
    public void ATwoPeriodExportIsReadAsATariffForEachPeriodWithItsOwnRates()
    {
        var bytes = TrendyolTariffWorkbookTests.Workbook(("Bluz M", "RYP00101", TrendyolTariffWorkbookTests.Period3, null), ("Etek", "ETK001", TrendyolTariffWorkbookTests.Period3, null));
        var parsed = CommissionTariffFileParser.Parse(ImportFileReader.ReadXlsxTable(new MemoryStream(bytes), 10_000_000), TrendyolTariffWorkbookTests.Now);

        Assert.Equal(0, parsed.SkippedRows);
        Assert.Equal(2, parsed.Tariffs.Count);
        var (three, four) = (parsed.Tariffs[0], parsed.Tariffs[1]);
        Assert.Equal(("3 Gün", TrendyolTariffWorkbookTests.Period3), (three.Label, three.PeriodText));
        Assert.Equal(("4 Gün", TrendyolTariffWorkbookTests.Period4), (four.Label, four.PeriodText));
        var istanbul = TimeSpan.FromHours(3);
        Assert.Equal((new DateTimeOffset(2026, 10, 29, 8, 0, 0, istanbul), new DateTimeOffset(2026, 11, 1, 8, 0, 0, istanbul)), (three.PeriodStart, three.PeriodEnd));
        Assert.Equal((new DateTimeOffset(2026, 11, 1, 8, 0, 0, istanbul), new DateTimeOffset(2026, 11, 5, 8, 0, 0, istanbul)), (four.PeriodStart, four.PeriodEnd));
        Assert.Equal(TimeSpan.Zero, four.PeriodStart.Offset);
        var row3 = three.Rows.Single(x => x.Barcode == "RYP00101");
        var row4 = four.Rows.Single(x => x.Barcode == "RYP00101");
        Assert.NotEqual(row3.Id, row4.Id);
        // The limits are the file's one set; the rates are each period's.
        Assert.Equal((464.63m, 464.62m, 432.26m, 400.1m), (row4.Tier1Min!.Value, row4.Tier2Max!.Value, row4.Tier3Max!.Value, row4.Tier4Max!.Value));
        Assert.Equal((row3.Tier1Min, row3.Tier4Max), (row4.Tier1Min, row4.Tier4Max));
        Assert.Equal((21.5m, 20.6m, 19.6m, 18.3m), (row3.Rate1!.Value, row3.Rate2!.Value, row3.Rate3!.Value, row3.Rate4!.Value));
        Assert.Equal((21.5m, 15.8m, 14.4m, 12.8m), (row4.Rate1!.Value, row4.Rate2!.Value, row4.Rate3!.Value, row4.Rate4!.Value));
        // At 464,62 the 4-day period's 2. band is %15,8, the 3-day's %20,6.
        Assert.Equal(15.8m, CommissionTariffBands.BandFor(row4, 464.62m)?.Rate);
        Assert.Equal(20.6m, CommissionTariffBands.BandFor(row3, 464.62m)?.Rate);
        Assert.Equal(519.75m, row4.BasisPrice);
    }

    // The reports' fallback commission reads the period a sale was made in: the 3-day rate before 1 Nov 08.00, the 4-day
    // rate from then on.
    [Fact]
    public void AReportLineTakesTheRateOfThePeriodItWasSoldIn()
    {
        var bytes = TrendyolTariffWorkbookTests.Workbook(("Bluz M", "RYP00101", TrendyolTariffWorkbookTests.Period3, null));
        var parsed = CommissionTariffFileParser.Parse(ImportFileReader.ReadXlsxTable(new MemoryStream(bytes), 10_000_000), TrendyolTariffWorkbookTests.Now);
        var connection = Guid.NewGuid();
        var uploaded = TrendyolTariffWorkbookTests.Now;
        var tariffs = new Infrastructure.Persistence.ReportCommissionTariffs(parsed.Tariffs.SelectMany(period =>
        {
            var tariff = new CommissionTariff { Id = Guid.NewGuid(), ConnectionId = connection, PlatformCode = "TRENDYOL", PeriodStart = period.PeriodStart, PeriodEnd = period.PeriodEnd, PeriodText = period.PeriodText, PeriodLabel = period.Label, FileName = "930112.xlsx", FileHash = "H", UploadedAt = uploaded };
            return period.Rows.Select(row => (tariff, row));
        }));
        var istanbul = TimeSpan.FromHours(3);
        Assert.Equal(20.6m, tariffs.Rate(connection, "RYP00101", null, new DateTimeOffset(2026, 11, 1, 7, 59, 0, istanbul).ToUniversalTime(), 450m));
        Assert.Equal(15.8m, tariffs.Rate(connection, "RYP00101", null, new DateTimeOffset(2026, 11, 1, 8, 0, 0, istanbul).ToUniversalTime(), 450m));
        Assert.Equal(12.8m, tariffs.Rate(connection, "RYP00101", null, new DateTimeOffset(2026, 11, 4, 20, 0, 0, istanbul).ToUniversalTime(), 399m));
        Assert.Null(tariffs.Rate(connection, "RYP00101", null, new DateTimeOffset(2026, 11, 5, 8, 0, 0, istanbul).ToUniversalTime(), 399m));
    }

    // A row whose second period is empty or does not read is kept in the period that reads, with a warning for the other;
    // a row neither period reads is skipped.
    [Fact]
    public void ARowIsKeptInThePeriodsThatRead()
    {
        string[] headers = ["BARKOD", "1.Fiyat Alt Limit", "4.Fiyat Üst Limiti", "Tarih aralığı (3 Gün)", "1.KOMİSYON", "4.KOMİSYON", "Tarih aralığı (4 Gün)", "1.KOMİSYON", "4.KOMİSYON"];
        var bytes = Xlsx(headers,
        [
            ["A1", "500", "400", "29 Ekim 08.00-1 Kasım 07.59", "21.5", "18", "1 Kasım 08.00-5 Kasım 07.59", "21.5", "12"],
            ["A2", "500", "400", "29 Ekim 08.00-1 Kasım 07.59", "21.5", "18", "", "", ""],
            ["A3", "500", "400", "29 Ekim 08.00-1 Kasım 07.59", "21.5", "18", "1 Kasım 08.00-5 Kasım 07.59", "abc", "12"],
            ["A4", "500", "400", "", "21.5", "18", "1 Kasım 08.00-5 Kasım 07.59", "21.5", "12"],
            ["A5", "500", "400", "", "", "", "", "", ""]
        ]);
        var parsed = CommissionTariffFileParser.Parse(ImportFileReader.ReadXlsxTable(new MemoryStream(bytes), 10_000_000), TrendyolTariffWorkbookTests.Now);

        Assert.Equal(["A1", "A2", "A3"], parsed.Tariffs[0].Rows.Select(x => x.Barcode));
        Assert.Equal(["A1", "A4"], parsed.Tariffs[1].Rows.Select(x => x.Barcode));
        Assert.Equal(12m, parsed.Tariffs[1].Rows[0].Rate4);
        Assert.Equal(1, parsed.SkippedRows);
        Assert.Contains(parsed.Warnings, warning => warning.StartsWith("Satır 4:", StringComparison.Ordinal) && warning.Contains("(4 Gün)", StringComparison.Ordinal));
        Assert.Contains(parsed.Warnings, warning => warning.StartsWith("Satır 5:", StringComparison.Ordinal) && warning.Contains("(3 Gün)", StringComparison.Ordinal));
    }

    [Fact]
    public void AFileWithoutTheTariffColumnsIsRefusedInTurkish()
    {
        var bytes = Xlsx(["Ürün", "Fiyat"], [["a", "1"]]);
        var table = ImportFileReader.ReadXlsxTable(new MemoryStream(bytes), 10_000_000);
        var error = Assert.Throws<InvalidDataException>(() => CommissionTariffFileParser.Parse(table, DateTimeOffset.UtcNow));
        Assert.Contains("BARKOD", error.Message, StringComparison.Ordinal);
    }

    // A minimal workbook whose cells are inline strings, as Trendyol's export writes them.
    internal static byte[] Xlsx(string[] headers, string[][] rows)
    {
        static string Column(int index) { var name = string.Empty; index++; while (index > 0) { var remainder = (index - 1) % 26; name = (char)('A' + remainder) + name; index = (index - 1) / 26; } return name; }
        static string Row(int number, string[] cells) => $"<row r=\"{number}\">" + string.Concat(cells.Select((value, index) => value.Length == 0 ? string.Empty : $"<c r=\"{Column(index)}{number}\" t=\"inlineStr\"><is><t>{System.Security.SecurityElement.Escape(value)}</t></is></c>")) + "</row>";
        var sheet = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");
        sheet.Append(Row(1, headers));
        for (var index = 0; index < rows.Length; index++) sheet.Append(Row(index + 2, rows[index]));
        sheet.Append("</sheetData></worksheet>");
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(archive.CreateEntry("xl/worksheets/sheet1.xml").Open(), new UTF8Encoding(false));
            writer.Write(sheet.ToString());
        }
        return buffer.ToArray();
    }
}
