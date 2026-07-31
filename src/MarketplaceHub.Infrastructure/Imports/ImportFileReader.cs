using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace MarketplaceHub.Infrastructure.Imports;

public sealed record ParsedImportRow(int RowNumber, IReadOnlyDictionary<string, string> Values, IReadOnlyList<string> Errors);

public static class ImportFileReader
{
    public static IEnumerable<ParsedImportRow> ReadCsv(Stream stream, IReadOnlyDictionary<string, string> mappings)
    {
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), true, 81920, leaveOpen: true);
        var headerLine = reader.ReadLine() ?? throw new InvalidDataException("CSV başlık satırı yok.");
        var headers = ParseCsvLine(headerLine); if (headers.Count == 0 || headers.Any(string.IsNullOrWhiteSpace)) throw new InvalidDataException("CSV başlıkları geçersiz.");
        var rowNumber = 1;
        while (reader.ReadLine() is { } line)
        {
            rowNumber++; var cells = ParseCsvLine(line); var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); var errors = new List<string>();
            for (var index = 0; index < headers.Count; index++)
            {
                var source = headers[index]; if (!mappings.TryGetValue(source, out var target)) continue; var value = index < cells.Count ? cells[index].Trim() : string.Empty;
                if (LooksLikeFormula(value)) errors.Add($"{source}: formül benzeri içerik reddedildi."); else values[target] = value;
            }
            ValidateRequired(values, errors); yield return new ParsedImportRow(rowNumber, values, errors);
        }
    }

    public static IEnumerable<ParsedImportRow> ReadXlsx(Stream stream, IReadOnlyDictionary<string, string> mappings, long maximumBytes)
    {
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true); ValidateArchive(archive, maximumBytes);
        var sharedStrings = ReadSharedStrings(archive); var sheet = archive.Entries.Where(entry => entry.FullName.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase) && entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)).OrderBy(entry => entry.FullName, StringComparer.Ordinal).FirstOrDefault() ?? throw new InvalidDataException("XLSX worksheet bulunamadı.");
        using var source = sheet.Open(); using var reader = XmlReader.Create(source, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true });
        List<string>? headers = null; var rowNumber = 0;
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "row") continue; rowNumber = int.TryParse(reader.GetAttribute("r"), NumberStyles.None, CultureInfo.InvariantCulture, out var parsedRow) ? parsedRow : rowNumber + 1; var cells = ReadXlsxRow(reader.ReadSubtree(), sharedStrings);
            if (headers is null) { headers = Dense(cells); if (headers.Count == 0 || headers.Any(string.IsNullOrWhiteSpace)) throw new InvalidDataException("XLSX başlıkları geçersiz."); continue; }
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); var errors = new List<string>();
            foreach (var (index, cell) in cells)
            {
                if (cell.IsFormula) { errors.Add($"{headers.ElementAtOrDefault(index) ?? index.ToString(CultureInfo.InvariantCulture)}: formül reddedildi."); continue; }
                if (index < headers.Count && mappings.TryGetValue(headers[index], out var target)) values[target] = cell.Value.Trim();
            }
            ValidateRequired(values, errors); yield return new ParsedImportRow(rowNumber, values, errors);
        }
    }

    private static void ValidateArchive(ZipArchive archive, long maximumBytes)
    {
        long expanded = 0;
        foreach (var entry in archive.Entries)
        {
            var path = entry.FullName.Replace('\\', '/'); if (path.Contains("../", StringComparison.Ordinal) || path.StartsWith("/", StringComparison.Ordinal)) throw new InvalidDataException("XLSX güvenli olmayan arşiv yolu içeriyor.");
            if (path.Contains("vbaProject", StringComparison.OrdinalIgnoreCase) || path.Contains("macrosheet", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Makrolu XLSX kabul edilmez.");
            if (path.StartsWith("xl/externalLinks/", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Dış bağlantılı XLSX kabul edilmez.");
            expanded = checked(expanded + entry.Length); if (entry.Length > maximumBytes || expanded > maximumBytes) throw new InvalidDataException("XLSX açılmış içerik boyutu üst sınırı aşıyor.");
        }
    }

    private static List<string> ReadSharedStrings(ZipArchive archive)
    {
        var entry = archive.GetEntry("xl/sharedStrings.xml"); if (entry is null) return [];
        var result = new List<string>(); using var source = entry.Open(); using var reader = XmlReader.Create(source, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }); var current = new StringBuilder();
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "si") current.Clear();
            else if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "t") current.Append(reader.ReadElementContentAsString());
            else if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "si") result.Add(current.ToString());
        }
        return result;
    }

    private static Dictionary<int, XlsxCell> ReadXlsxRow(XmlReader reader, IReadOnlyList<string> sharedStrings)
    {
        var result = new Dictionary<int, XlsxCell>();
        using (reader)
        {
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "c") continue; var reference = reader.GetAttribute("r") ?? string.Empty; var type = reader.GetAttribute("t"); var index = ColumnIndex(reference); var value = string.Empty; var formula = false;
                using var cellReader = reader.ReadSubtree(); while (cellReader.Read()) { if (cellReader.NodeType == XmlNodeType.Element && cellReader.LocalName == "f") { formula = true; cellReader.Skip(); } else if (cellReader.NodeType == XmlNodeType.Element && cellReader.LocalName is "v" or "t") value = cellReader.ReadElementContentAsString(); }
                if (type == "s" && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var sharedIndex) && sharedIndex >= 0 && sharedIndex < sharedStrings.Count) value = sharedStrings[sharedIndex]; result[index] = new XlsxCell(value, formula);
            }
        }
        return result;
    }

    private static int ColumnIndex(string reference) { var index = 0; foreach (var character in reference.TakeWhile(char.IsLetter)) index = checked(index * 26 + char.ToUpperInvariant(character) - 'A' + 1); return Math.Max(0, index - 1); }
    private static List<string> Dense(Dictionary<int, XlsxCell> cells) { var result = new List<string>(); for (var index = 0; index <= cells.Keys.DefaultIfEmpty(-1).Max(); index++) result.Add(cells.TryGetValue(index, out var cell) ? cell.Value.Trim() : string.Empty); return result; }
    private static void ValidateRequired(IReadOnlyDictionary<string, string> values, ICollection<string> errors) { if (!values.TryGetValue("title", out var title) || string.IsNullOrWhiteSpace(title)) errors.Add("title: zorunlu"); if (!values.TryGetValue("sku", out var sku) || string.IsNullOrWhiteSpace(sku)) errors.Add("sku: zorunlu"); }
    private static bool LooksLikeFormula(string value) => value.TrimStart().StartsWith('=') || value.TrimStart().StartsWith('+') || value.TrimStart().StartsWith('-') || value.TrimStart().StartsWith('@');

    private static List<string> ParseCsvLine(string line)
    {
        var result = new List<string>(); var current = new StringBuilder(); var quoted = false;
        for (var index = 0; index < line.Length; index++) { var character = line[index]; if (character == '"') { if (quoted && index + 1 < line.Length && line[index + 1] == '"') { current.Append('"'); index++; } else quoted = !quoted; } else if (character == ',' && !quoted) { result.Add(current.ToString()); current.Clear(); } else current.Append(character); }
        if (quoted) throw new InvalidDataException("CSV tırnak yapısı geçersiz."); result.Add(current.ToString()); return result;
    }

    private sealed record XlsxCell(string Value, bool IsFormula);
}
