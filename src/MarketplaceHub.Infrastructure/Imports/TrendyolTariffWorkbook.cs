using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using MarketplaceHub.Domain;

namespace MarketplaceHub.Infrastructure.Imports;

// Trendyol's weekly commission tariff workbook ("KomisyonTarifeleriÜrünleri", downloaded from Ürün Komisyon Tarifeleri)
// with the seller's picks written where Trendyol reads them when the file is uploaded back: the new sale price in
// "YENİ TSF (FİYAT GÜNCELLE)" and the period it is for in "Tarife Seçimi" (a list of "Trendyol Satış Fiyatı",
// "7 Günlük Fiyat", "4 Günlük Fiyat", "3 Günlük Fiyat"; the file's own "Hesaplanan Komisyon (n Gün)" formulas read the
// two cells). A row with both cells empty is left unchanged by Trendyol.
public static class TrendyolTariffWorkbook
{
    public const string SheetName = "KomisyonTarifeleriÜrünleri";
    public const string PriceHeader = "YENİ TSF (FİYAT GÜNCELLE)";
    public const string ChoiceHeader = "Tarife Seçimi";
    private const long MaximumExpandedBytes = 64 * 1024 * 1024;

    // The "Tarife Seçimi" option for a tariff period of `days` days ("3 Günlük Fiyat"), spelt as the file's own list
    // spells it when the file lists it.
    public static string ChoiceFor(int days, IReadOnlyCollection<string> options)
    {
        var wanted = $"{days.ToString(CultureInfo.InvariantCulture)} Günlük Fiyat";
        return options.FirstOrDefault(option => string.Equals(Fold(option), Fold(wanted), StringComparison.Ordinal)) ?? wanted;
    }

    public static int PeriodDays(DateTimeOffset start, DateTimeOffset end) => Math.Max(1, (int)Math.Round((end - start).TotalDays, MidpointRounding.AwayFromZero));

    // The uploaded workbook with each row of the period whose barcode has a price filled in (the price and the period's
    // option) and every other row of the period emptied in those two cells; every other cell, style, list and formula is
    // kept. Excel recalculates the file's formulas on opening. Null when the sheet has no barcode, price or choice column.
    public static (byte[] Content, int Filled)? Fill(byte[] source, string periodText, int days, IReadOnlyDictionary<string, decimal> prices)
    {
        using var input = new MemoryStream(source, writable: false);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read);
        ImportFileReader.ValidateArchive(archive, MaximumExpandedBytes);
        // The sheet the parser read (ImportFileReader.ReadXlsxTable): the first worksheet part.
        var sheetEntry = archive.Entries.Where(entry => entry.FullName.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase) && entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            .OrderBy(entry => entry.FullName, StringComparer.Ordinal).FirstOrDefault();
        if (sheetEntry is null) return null;
        var shared = SharedStrings(archive);
        var sheet = Load(sheetEntry);
        var main = sheet.Root!.Name.Namespace;
        var rows = sheet.Root.Element(main + "sheetData")?.Elements(main + "row").ToList() ?? [];
        if (rows.Count == 0) return null;

        string? barcodeColumn = null, priceColumn = null, choiceColumn = null, periodColumn = null;
        foreach (var cell in rows[0].Elements(main + "c"))
        {
            var column = ColumnOf(cell);
            var key = CommissionTariffFileParser.HeaderKey(Text(cell, main, shared));
            if (key == "BARKOD") barcodeColumn ??= column;
            else if (key.StartsWith("YENITSF", StringComparison.Ordinal)) priceColumn ??= column;
            else if (key == "TARIFESECIMI") choiceColumn ??= column;
            else if (key.StartsWith("TARIHARALIGI", StringComparison.Ordinal)) periodColumn ??= column;
        }
        if (barcodeColumn is null || priceColumn is null || choiceColumn is null) return null;
        var choice = ChoiceFor(days, ListOptions(sheet, main, choiceColumn));
        var period = Spaces(periodText);
        var byBarcode = new Dictionary<string, decimal>(prices, StringComparer.OrdinalIgnoreCase);

        var filled = 0;
        foreach (var row in rows.Skip(1))
        {
            var number = row.Attribute("r")?.Value;
            if (number is null) continue;
            var barcode = Cell(row, main, barcodeColumn) is { } barcodeCell ? Text(barcodeCell, main, shared).Trim() : string.Empty;
            if (barcode.Length == 0) continue;
            // A row of another period of the same file belongs to that period's tariff and is left as it is.
            if (periodColumn is not null && Cell(row, main, periodColumn) is { } periodCell && Spaces(Text(periodCell, main, shared)) is { Length: > 0 } rowPeriod
                && !string.Equals(rowPeriod, period, StringComparison.OrdinalIgnoreCase)) continue;
            if (byBarcode.TryGetValue(barcode, out var price))
            {
                SetNumber(CellFor(row, main, priceColumn, number), main, price);
                SetText(CellFor(row, main, choiceColumn, number), main, choice);
                filled++;
            }
            else
            {
                if (Cell(row, main, priceColumn) is { } priceCell) Clear(priceCell);
                if (Cell(row, main, choiceColumn) is { } choiceCell) Clear(choiceCell);
            }
        }

        var workbookEntry = archive.GetEntry("xl/workbook.xml");
        var workbook = workbookEntry is null ? null : Load(workbookEntry);
        if (workbook?.Root is { } root) RecalculateOnLoad(root);

        using var output = new MemoryStream();
        using (var target = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in archive.Entries)
            {
                var copy = target.CreateEntry(entry.FullName, CompressionLevel.Optimal);
                copy.LastWriteTime = entry.LastWriteTime;
                using var destination = copy.Open();
                if (entry == sheetEntry) Save(sheet, destination);
                else if (entry == workbookEntry && workbook is not null) Save(workbook, destination);
                else { using var from = entry.Open(); from.CopyTo(destination); }
            }
        }
        return (output.ToArray(), filled);
    }

    // The workbook written again from a tariff's stored rows when its uploaded file is not kept: Trendyol's columns in
    // its order for a one-period tariff, the picks in the price and choice columns. The file's display-only columns the
    // panel never stored (the customer's price, Plus price, promotions, external ids) are left empty.
    public static (byte[] Content, int Filled) Write(int days, IReadOnlyList<CommissionTariffRow> rows, string periodText, IReadOnlyDictionary<Guid, (decimal Price, decimal Rate)> picks)
    {
        var choice = ChoiceFor(days, []);
        var span = $"({days.ToString(CultureInfo.InvariantCulture)} Gün)";
        var sheet = new List<IReadOnlyList<object?>>
        {
            new object?[] { "ÜRÜN İSMİ", "BARKOD", "SATICI STOK KODU", "BEDEN", "MODEL KODU", "KATEGORİ", "MARKA", "STOK", "1.Fiyat Alt Limit", "2.Fiyat Üst Limiti", "2.Fiyat Alt Limit",
                "3.Fiyat Üst Limiti", "3.Fiyat Alt Limit", "4.Fiyat Üst Limiti", $"Tarih aralığı {span}", "1.KOMİSYON", "2.KOMİSYON", "3.KOMİSYON", "4.KOMİSYON", "KOMİSYONA ESAS FİYAT",
                "GÜNCEL KOMİSYON", "GÜNCEL TSF", "Müşterinin Gördüğü Güncel Fiyat", "Plus Müşterinin Gördüğü Fiyat", "Güncel Uygulanan Promosyonlar", PriceHeader, $"Hesaplanan Komisyon {span}",
                ChoiceHeader, "FIRST EXTERNAL ID", "SECOND EXTERNAL ID", "FULL EXTERNAL ID", "TARİFE GRUBU" }
        };
        var filled = 0;
        foreach (var row in rows)
        {
            var picked = picks.TryGetValue(row.Id, out var pick);
            if (picked) filled++;
            sheet.Add(new object?[]
            {
                row.ProductName, row.Barcode, row.SellerSku, row.Size, row.ModelCode, row.Category, row.Brand, row.Stock, row.Tier1Min, row.Tier2Max, row.Tier2Min, row.Tier3Max, row.Tier3Min, row.Tier4Max,
                periodText, row.Rate1, row.Rate2, row.Rate3, row.Rate4, row.BasisPrice, row.CurrentRate, row.CurrentSalePrice, null, null, null,
                picked ? decimal.Round(pick.Price, 2, MidpointRounding.AwayFromZero) : null, picked ? pick.Rate : null, picked ? choice : null, null, null, null, row.TariffGroup
            });
        }
        double[] widths = [48, 18, 18, 10, 16, 16, 14, 10, 14, 14, 14, 14, 14, 14, 30, 12, 12, 12, 12, 16, 14, 12, 16, 16, 24, 20, 20, 18, 14, 14, 14, 34];
        return (SimpleXlsxWriter.Write([new SimpleXlsxSheet(SheetName, sheet, widths)]), filled);
    }

    private static XDocument Load(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
    }

    private static void Save(XDocument document, Stream destination)
    {
        using var writer = XmlWriter.Create(destination, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = false });
        document.Save(writer);
    }

    private static List<string> SharedStrings(ZipArchive archive)
    {
        if (archive.GetEntry("xl/sharedStrings.xml") is not { } entry) return [];
        var document = Load(entry);
        var main = document.Root!.Name.Namespace;
        // A rich text's runs joined; its phonetic hints (rPh) are not part of the text.
        return document.Root.Elements(main + "si").Select(item => string.Concat(item.Descendants(main + "t").Where(t => t.Parent?.Name != main + "rPh").Select(t => t.Value))).ToList();
    }

    private static string Text(XElement cell, XNamespace main, IReadOnlyList<string> shared)
    {
        var type = cell.Attribute("t")?.Value;
        if (type == "inlineStr") return string.Concat(cell.Element(main + "is")?.Descendants(main + "t").Select(t => t.Value) ?? []);
        var value = cell.Element(main + "v")?.Value ?? string.Empty;
        return type == "s" && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index < shared.Count ? shared[index] : value;
    }

    // The options of the list a data validation offers in a column ("Trendyol Satış Fiyatı,7 Günlük Fiyat,…").
    private static List<string> ListOptions(XDocument sheet, XNamespace main, string column)
    {
        var validations = sheet.Root!.Element(main + "dataValidations")?.Elements(main + "dataValidation") ?? [];
        foreach (var validation in validations)
        {
            if (validation.Attribute("type")?.Value != "list") continue;
            var ranges = (validation.Attribute("sqref")?.Value ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (!ranges.Any(range => range.Split(':').Select(part => new string(part.TakeWhile(char.IsLetter).ToArray())).Contains(column, StringComparer.OrdinalIgnoreCase))) continue;
            var formula = validation.Element(main + "formula1")?.Value.Trim() ?? string.Empty;
            if (formula.Length < 2 || formula[0] != '"' || formula[^1] != '"') continue;
            return formula[1..^1].Split(',').Select(option => option.Trim()).Where(option => option.Length > 0).ToList();
        }
        return [];
    }

    private static string ColumnOf(XElement cell) => new((cell.Attribute("r")?.Value ?? string.Empty).TakeWhile(char.IsLetter).Select(char.ToUpperInvariant).ToArray());

    private static int ColumnNumber(string column) => column.Aggregate(0, (sum, letter) => sum * 26 + letter - 'A' + 1);

    private static XElement? Cell(XElement row, XNamespace main, string column) => row.Elements(main + "c").FirstOrDefault(cell => ColumnOf(cell) == column);

    // The row's cell in a column, made in its place among the row's cells when the row has none.
    private static XElement CellFor(XElement row, XNamespace main, string column, string rowNumber)
    {
        if (Cell(row, main, column) is { } existing) return existing;
        var cell = new XElement(main + "c", new XAttribute("r", column + rowNumber));
        var after = row.Elements(main + "c").FirstOrDefault(other => ColumnNumber(ColumnOf(other)) > ColumnNumber(column));
        if (after is null) row.Add(cell); else after.AddBeforeSelf(cell);
        return cell;
    }

    // A cell emptied of its value, its style kept.
    private static void Clear(XElement cell)
    {
        cell.Attribute("t")?.Remove();
        cell.RemoveNodes();
    }

    private static void SetNumber(XElement cell, XNamespace main, decimal value)
    {
        Clear(cell);
        cell.Add(new XElement(main + "v", decimal.Round(value, 2, MidpointRounding.AwayFromZero).ToString("0.00", CultureInfo.InvariantCulture)));
    }

    private static void SetText(XElement cell, XNamespace main, string value)
    {
        Clear(cell);
        cell.SetAttributeValue("t", "inlineStr");
        cell.Add(new XElement(main + "is", new XElement(main + "t", value)));
    }

    // Excel works the file's formulas ("Hesaplanan Komisyon") out again on opening, so they show the filled prices.
    private static void RecalculateOnLoad(XElement workbook)
    {
        var main = workbook.Name.Namespace;
        var calc = workbook.Element(main + "calcPr");
        if (calc is null)
        {
            calc = new XElement(main + "calcPr");
            // calcPr follows sheets and the optional parts the schema puts after them.
            var before = workbook.Elements().LastOrDefault(element => element.Name.LocalName is "sheets" or "functionGroups" or "externalReferences" or "definedNames");
            if (before is null) return;
            before.AddAfterSelf(calc);
        }
        calc.SetAttributeValue("fullCalcOnLoad", "1");
    }

    private static string Spaces(string value) => string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string Fold(string value) => CommissionTariffFileParser.HeaderKey(value);
}
