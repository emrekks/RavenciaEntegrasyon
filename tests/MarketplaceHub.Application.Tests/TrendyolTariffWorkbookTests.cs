using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Imports;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// The Excel of the seller's picks is Trendyol's own tariff workbook with the picked price in "YENİ TSF (FİYAT GÜNCELLE)"
// and the period in "Tarife Seçimi", as Trendyol's export of 31 Oct 2026 (KomisyonTarifeleriÜrünleri, a 3- and a 4-day
// period) lays them out.
public sealed class TrendyolTariffWorkbookTests
{
    internal const string Period3 = "29 Ekim 08.00-1 Kasım 07.59";
    internal const string Period4 = "1 Kasım 08.00-5 Kasım 07.59";
    internal static readonly DateTimeOffset Now = new(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Picked_rows_get_their_price_and_period_and_every_other_cell_stays()
    {
        var source = Workbook(("Bluz M", "RYP00101", Period3, null), ("Bluz XL", "RYP00103", Period3, "999"), ("Etek", "ETK001", Period3, null), ("Bluz M", "RYP00101", Period4, "777"));
        var filled = TrendyolTariffWorkbook.Fill(source, Period3, 3, new Dictionary<string, decimal> { ["ryp00101"] = 400.1m, ["ETK001"] = 299.999m });

        Assert.NotNull(filled);
        Assert.Equal(2, filled.Value.Filled);
        var table = ImportFileReader.ReadXlsxTable(new MemoryStream(filled.Value.Content), 10_000_000);
        var price = table.Headers.ToList().IndexOf("YENİ TSF (FİYAT GÜNCELLE)");
        var choice = table.Headers.ToList().IndexOf("Tarife Seçimi");
        Assert.Equal((4, 19, 21), (table.Rows.Count, price, choice));
        string Cell(int row, int column) => column < table.Rows[row].Cells.Count ? table.Rows[row].Cells[column] : string.Empty;
        Assert.Equal(("400.10", "3 Günlük Fiyat"), (Cell(0, price), Cell(0, choice)));
        // A row of the period without a pick is emptied there (no change on Trendyol), a stale price included.
        Assert.Equal((string.Empty, string.Empty), (Cell(1, price), Cell(1, choice)));
        Assert.Equal(("300.00", "3 Günlük Fiyat"), (Cell(2, price), Cell(2, choice)));
        // A row of the file's other period is not this tariff's and is left as it was.
        Assert.Equal("777", Cell(3, price));
        Assert.Equal(Period3, Cell(0, table.Headers.ToList().IndexOf("Tarih aralığı (3 Gün)")));

        var sheet = Part(filled.Value.Content, "xl/worksheets/sheet1.xml");
        Assert.Contains("<c r=\"T2\" s=\"62\"><v>400.10</v></c>", sheet, StringComparison.Ordinal);
        Assert.Contains("<c r=\"V2\" s=\"68\" t=\"inlineStr\"><is><t>3 Günlük Fiyat</t></is></c>", sheet, StringComparison.Ordinal);
        Assert.Contains("<c r=\"T3\" s=\"62\" />", sheet, StringComparison.Ordinal);
        Assert.Contains("<f>IF(OR(T2=\"\",V2=\"\"),\"-\",J2)</f>", sheet, StringComparison.Ordinal);
        Assert.Contains("<dataValidations count=\"2\">", sheet, StringComparison.Ordinal);
        Assert.Contains("fullCalcOnLoad=\"1\"", Part(filled.Value.Content, "xl/workbook.xml"), StringComparison.Ordinal);
        Assert.Equal(Part(source, "xl/styles.xml"), Part(filled.Value.Content, "xl/styles.xml"));
        Assert.Equal(Part(source, "xl/sharedStrings.xml"), Part(filled.Value.Content, "xl/sharedStrings.xml"));

        // The file still reads as the same tariff.
        var parsed = CommissionTariffFileParser.Parse(table, Now);
        Assert.Equal(3, parsed.Tariffs.Single(x => x.PeriodText == Period3).Rows.Count);
    }

    [Fact]
    public void A_cell_the_row_lacks_is_made_in_its_place()
    {
        var source = Workbook(("Bluz M", "RYP00101", Period3, null));
        // The same workbook with the row's price and choice cells left out altogether.
        var rewritten = Rewrite(source, "xl/worksheets/sheet1.xml", xml => xml.Replace("<c r=\"T2\" s=\"62\"/>", string.Empty, StringComparison.Ordinal).Replace("<c r=\"V2\" s=\"68\"/>", string.Empty, StringComparison.Ordinal));
        Assert.DoesNotContain("r=\"T2\"", Part(rewritten, "xl/worksheets/sheet1.xml"), StringComparison.Ordinal);

        var filled = TrendyolTariffWorkbook.Fill(rewritten, Period3, 3, new Dictionary<string, decimal> { ["RYP00101"] = 450m })!.Value;
        var sheet = Part(filled.Content, "xl/worksheets/sheet1.xml");
        Assert.True(sheet.IndexOf("r=\"S2\"", StringComparison.Ordinal) < sheet.IndexOf("r=\"T2\"", StringComparison.Ordinal));
        Assert.True(sheet.IndexOf("r=\"T2\"", StringComparison.Ordinal) < sheet.IndexOf("r=\"U2\"", StringComparison.Ordinal));
        Assert.True(sheet.IndexOf("r=\"U2\"", StringComparison.Ordinal) < sheet.IndexOf("r=\"V2\"", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(3, "3 Günlük Fiyat")]
    [InlineData(4, "4 Günlük Fiyat")]
    [InlineData(7, "7 Günlük Fiyat")]
    public void The_period_option_is_the_files_own(int days, string expected)
    {
        Assert.Equal(expected, TrendyolTariffWorkbook.ChoiceFor(days, ["Trendyol Satış Fiyatı", "7 Günlük Fiyat", "4 Günlük Fiyat", "3 Günlük Fiyat"]));
        Assert.Equal(expected, TrendyolTariffWorkbook.ChoiceFor(days, []));
        Assert.Equal(3, TrendyolTariffWorkbook.PeriodDays(new DateTimeOffset(2026, 10, 29, 5, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 1, 5, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void Without_the_uploaded_file_the_workbook_is_written_again_in_trendyols_columns()
    {
        var row = new CommissionTariffRow { Id = Guid.NewGuid(), Barcode = "RYP00101", ProductName = "Bluz", Tier1Min = 464.63m, Tier2Max = 464.62m, Tier2Min = 432.27m, Tier3Max = 432.26m, Tier3Min = 400.11m, Tier4Max = 400.10m, Rate1 = 21.5m, Rate2 = 20.6m, Rate3 = 19.6m, Rate4 = 18.3m, BasisPrice = 519.75m, TariffGroup = "g1" };
        var other = new CommissionTariffRow { Id = Guid.NewGuid(), Barcode = "ETK001", ProductName = "Etek", Tier1Min = 300m, Rate1 = 20m };
        var written = TrendyolTariffWorkbook.Write(3, [row, other], Period3, new Dictionary<Guid, (decimal, decimal)> { [row.Id] = (400.1m, 18.3m) });

        Assert.Equal(1, written.Filled);
        Assert.Contains("name=\"KomisyonTarifeleriÜrünleri\"", Part(written.Content, "xl/workbook.xml"), StringComparison.Ordinal);
        var table = ImportFileReader.ReadXlsxTable(new MemoryStream(written.Content), 10_000_000);
        Assert.Equal(["ÜRÜN İSMİ", "BARKOD", "SATICI STOK KODU", "BEDEN", "MODEL KODU", "KATEGORİ", "MARKA", "STOK", "1.Fiyat Alt Limit", "2.Fiyat Üst Limiti", "2.Fiyat Alt Limit",
            "3.Fiyat Üst Limiti", "3.Fiyat Alt Limit", "4.Fiyat Üst Limiti", "Tarih aralığı (3 Gün)", "1.KOMİSYON", "2.KOMİSYON", "3.KOMİSYON", "4.KOMİSYON", "KOMİSYONA ESAS FİYAT",
            "GÜNCEL KOMİSYON", "GÜNCEL TSF", "Müşterinin Gördüğü Güncel Fiyat", "Plus Müşterinin Gördüğü Fiyat", "Güncel Uygulanan Promosyonlar", "YENİ TSF (FİYAT GÜNCELLE)",
            "Hesaplanan Komisyon (3 Gün)", "Tarife Seçimi", "FIRST EXTERNAL ID", "SECOND EXTERNAL ID", "FULL EXTERNAL ID", "TARİFE GRUBU"], table.Headers);
        var picked = table.Rows.Single(x => x.Cells[1] == "RYP00101").Cells;
        Assert.Equal(("400.1", "18.3", "3 Günlük Fiyat"), (picked[25], picked[26], picked[27]));
        Assert.True(table.Rows.Single(x => x.Cells[1] == "ETK001").Cells.Count <= 25 || table.Rows.Single(x => x.Cells[1] == "ETK001").Cells[25].Length == 0);
        var parsed = Assert.Single(CommissionTariffFileParser.Parse(table, Now).Tariffs);
        Assert.Equal(2, parsed.Rows.Count);
        Assert.Equal(18.3m, parsed.Rows.Single(x => x.Barcode == "RYP00101").Rate4);
    }

    // A workbook laid out as Trendyol's (inline strings, a stale choice as a shared string, styled empty price and choice cells, a formula reading them, the
    // choice list as a data validation), each row (name, barcode, period, a price already typed in).
    internal static byte[] Workbook(params (string Name, string Barcode, string Period, string? NewPrice)[] rows)
    {
        string[] headers = ["ÜRÜN İSMİ", "BARKOD", "1.Fiyat Alt Limit", "2.Fiyat Üst Limiti", "2.Fiyat Alt Limit", "3.Fiyat Üst Limiti", "3.Fiyat Alt Limit", "4.Fiyat Üst Limiti",
            "Tarih aralığı (3 Gün)", "1.KOMİSYON", "2.KOMİSYON", "3.KOMİSYON", "4.KOMİSYON", "Tarih aralığı (4 Gün)", "1.KOMİSYON", "2.KOMİSYON", "3.KOMİSYON", "4.KOMİSYON",
            "KOMİSYONA ESAS FİYAT", "YENİ TSF (FİYAT GÜNCELLE)", "Hesaplanan Komisyon (3 Gün)", "Tarife Seçimi", "TARİFE GRUBU"];
        var strings = new List<string>();
        int Shared(string text) { var index = strings.IndexOf(text); if (index < 0) { strings.Add(text); index = strings.Count - 1; } return index; }
        static string Column(int index) => index < 26 ? ((char)('A' + index)).ToString() : "A" + (char)('A' + index - 26);
        var sheet = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
        sheet.Append("<sheetViews><sheetView workbookViewId=\"0\"><pane xSplit=\"2.0\" ySplit=\"1.0\" state=\"frozen\" topLeftCell=\"C2\" activePane=\"bottomRight\"/></sheetView></sheetViews><sheetData>");
        sheet.Append("<row r=\"1\">").Append(string.Concat(headers.Select((header, index) => $"<c r=\"{Column(index)}1\" s=\"{index + 2}\" t=\"inlineStr\"><is><t>{SecurityElement.Escape(header)}</t></is></c>"))).Append("</row>");
        for (var index = 0; index < rows.Length; index++)
        {
            var (name, barcode, period, price) = rows[index];
            var r = (index + 2).ToString(CultureInfo.InvariantCulture);
            string Text(string column, string value) => $"<c r=\"{column}{r}\" s=\"80\" t=\"inlineStr\"><is><t>{SecurityElement.Escape(value)}</t></is></c>";
            string Number(string column, string value) => $"<c r=\"{column}{r}\" s=\"94\"><v>{value}</v></c>";
            sheet.Append($"<row r=\"{r}\" customHeight=\"true\" ht=\"24.0\">")
                .Append(Text("A", name)).Append(Text("B", barcode))
                .Append(Number("C", "464.63")).Append(Number("D", "464.62")).Append(Number("E", "432.27")).Append(Number("F", "432.26")).Append(Number("G", "400.11")).Append(Number("H", "400.1"))
                .Append(Text("I", period)).Append(Number("J", "21.5")).Append(Number("K", "20.6")).Append(Number("L", "19.6")).Append(Number("M", "18.3"))
                .Append(Text("N", Period4)).Append(Number("O", "21.5")).Append(Number("P", "15.8")).Append(Number("Q", "14.4")).Append(Number("R", "12.8"))
                .Append(Number("S", "519.75"))
                .Append(price is null ? $"<c r=\"T{r}\" s=\"62\"/>" : $"<c r=\"T{r}\" s=\"62\"><v>{price}</v></c>")
                .Append($"<c r=\"U{r}\" s=\"64\"><f>IF(OR(T{r}=\"\",V{r}=\"\"),\"-\",J{r})</f><v>0.0</v></c>")
                .Append(price is null ? $"<c r=\"V{r}\" s=\"68\"/>" : $"<c r=\"V{r}\" s=\"68\" t=\"s\"><v>{Shared("7 Günlük Fiyat")}</v></c>")
                .Append(Text("W", "ea1e09b0"))
                .Append("</row>");
        }
        sheet.Append("</sheetData><dataValidations count=\"2\">");
        sheet.Append("<dataValidation type=\"decimal\" operator=\"between\" sqref=\"T2:T1048576\" allowBlank=\"true\" errorStyle=\"stop\"><formula1>0</formula1><formula2>3.4028235E38</formula2></dataValidation>");
        sheet.Append($"<dataValidation type=\"list\" sqref=\"V2:V{rows.Length + 1}\" allowBlank=\"true\" errorStyle=\"stop\"><formula1>\"Trendyol Satış Fiyatı,7 Günlük Fiyat,4 Günlük Fiyat,3 Günlük Fiyat,\"</formula1></dataValidation>");
        sheet.Append("</dataValidations></worksheet>");

        var shared = new StringBuilder($"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><sst xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" count=\"{strings.Count}\" uniqueCount=\"{strings.Count}\">");
        foreach (var text in strings) shared.Append("<si><t>").Append(SecurityElement.Escape(text)).Append("</t></si>");
        shared.Append("</sst>");
        var parts = new Dictionary<string, string>
        {
            ["[Content_Types].xml"] = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/><Override PartName=\"/xl/sharedStrings.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml\"/><Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/></Types>",
            ["_rels/.rels"] = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>",
            ["xl/workbook.xml"] = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><workbookPr date1904=\"false\"/><bookViews><workbookView activeTab=\"0\"/></bookViews><sheets><sheet name=\"KomisyonTarifeleriÜrünleri\" r:id=\"rId3\" sheetId=\"1\"/></sheets><definedNames/></workbook>",
            ["xl/_rels/workbook.xml.rels"] = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Target=\"sharedStrings.xml\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/sharedStrings\"/><Relationship Id=\"rId2\" Target=\"styles.xml\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\"/><Relationship Id=\"rId3\" Target=\"worksheets/sheet1.xml\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\"/></Relationships>",
            ["xl/styles.xml"] = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><fonts count=\"1\"><font/></fonts><fills count=\"1\"><fill/></fills><borders count=\"1\"><border/></borders><cellXfs count=\"1\"><xf/></cellXfs></styleSheet>",
            ["xl/sharedStrings.xml"] = shared.ToString(),
            ["xl/worksheets/sheet1.xml"] = sheet.ToString()
        };
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, content) in parts)
            {
                using var writer = new StreamWriter(archive.CreateEntry(name).Open(), new UTF8Encoding(false));
                writer.Write(content);
            }
        return buffer.ToArray();
    }

    internal static string Part(byte[] workbook, string name)
    {
        using var archive = new ZipArchive(new MemoryStream(workbook), ZipArchiveMode.Read);
        using var reader = new StreamReader(archive.GetEntry(name)!.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static byte[] Rewrite(byte[] workbook, string name, Func<string, string> change)
    {
        using var source = new ZipArchive(new MemoryStream(workbook), ZipArchiveMode.Read);
        using var buffer = new MemoryStream();
        using (var target = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var entry in source.Entries)
            {
                using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
                var text = reader.ReadToEnd();
                using var writer = new StreamWriter(target.CreateEntry(entry.FullName).Open(), new UTF8Encoding(false));
                writer.Write(entry.FullName == name ? change(text) : text);
            }
        return buffer.ToArray();
    }
}
