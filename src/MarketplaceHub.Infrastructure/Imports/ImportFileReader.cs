using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace MarketplaceHub.Infrastructure.Imports;

public sealed record ParsedImportRow(int RowNumber, IReadOnlyDictionary<string, string> Values, IReadOnlyList<string> Errors);

public static class ImportFileReader
{
    // Excel in Turkish locales saves CSV as Windows-1254 with ';' between fields. The reader accepts UTF-8
    // (with or without BOM) and UTF-16 with a BOM, and falls back to Windows-1254 when the bytes are not valid UTF-8; the
    // delimiter is the one of ',', ';' or tab that the header uses most. Quoted fields may span lines.
    public static IEnumerable<ParsedImportRow> ReadCsv(Stream stream, IReadOnlyDictionary<string, string> mappings)
    {
        var text = DecodeCsv(stream);
        var records = ParseCsvRecords(text, SniffDelimiter(text)).GetEnumerator();
        if (!records.MoveNext()) throw new InvalidDataException("CSV başlık satırı yok.");
        var headers = records.Current.Cells.Select(header => header.Trim()).ToList();
        if (headers.Count == 0 || headers.Any(string.IsNullOrWhiteSpace)) throw new InvalidDataException("CSV başlıkları geçersiz.");
        while (records.MoveNext())
        {
            var (rowNumber, cells) = records.Current;
            if (cells.All(string.IsNullOrWhiteSpace)) continue;
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); var errors = new List<string>();
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

    internal static string DecodeCsv(Stream stream)
    {
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var bytes = buffer.ToArray();
        // A byte order mark names the encoding (Excel's "Unicode text" is UTF-16 LE) and is never part
        // of the first header.
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        var offset = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        try { return new UTF8Encoding(false, true).GetString(bytes, offset, bytes.Length - offset); }
        catch (DecoderFallbackException) { return Windows1254.GetString(bytes, offset, bytes.Length - offset); }
    }

    private static readonly Encoding Windows1254 = CreateWindows1254();

    private static Encoding CreateWindows1254()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1254);
    }

    internal static char SniffDelimiter(string text)
    {
        var counts = new Dictionary<char, int> { [','] = 0, [';'] = 0, ['\t'] = 0 };
        var quoted = false;
        var fieldStart = true;
        foreach (var character in text)
        {
            // As in ParseCsvRecords, only a quote that starts a field opens a quoted field.
            if (character == '"' && (quoted || fieldStart)) { quoted = !quoted; fieldStart = false; }
            else if (!quoted && character is '\r' or '\n') break;
            else if (!quoted && counts.ContainsKey(character)) { counts[character]++; fieldStart = true; }
            else if (!quoted && character != ' ') fieldStart = false;
        }
        var best = counts.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key == ',' ? 0 : pair.Key == ';' ? 1 : 2).First();
        return best.Value == 0 ? ',' : best.Key;
    }

    // One record per CSV row; a quoted field may contain the delimiter, doubled quotes and line breaks.
    // RowNumber is the line on which the record starts, so error reports point at the right line.
    // As in RFC 4180 a quote opens a quoted field only at the start of the field (leading spaces
    // allowed); inside an unquoted field (24" Monitör) it is a literal character.
    internal static IEnumerable<(int RowNumber, List<string> Cells)> ParseCsvRecords(string text, char delimiter)
    {
        var cells = new List<string>(); var current = new StringBuilder(); var quoted = false; var fieldQuoted = false; var line = 1; var recordStart = 1; var hasContent = false;
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (quoted)
            {
                if (character == '"')
                {
                    if (index + 1 < text.Length && text[index + 1] == '"') { current.Append('"'); index++; }
                    else quoted = false;
                }
                else
                {
                    if (character == '\n') line++;
                    current.Append(character);
                }
                continue;
            }
            if (character == '"' && !fieldQuoted && IsBlank(current)) { current.Clear(); quoted = true; fieldQuoted = true; hasContent = true; continue; }
            if (character == delimiter) { cells.Add(current.ToString()); current.Clear(); fieldQuoted = false; hasContent = true; continue; }
            if (character is '\r' or '\n')
            {
                if (character == '\r' && index + 1 < text.Length && text[index + 1] == '\n') index++;
                if (hasContent || current.Length > 0) { cells.Add(current.ToString()); yield return (recordStart, cells); }
                cells = []; current.Clear(); fieldQuoted = false; hasContent = false; line++; recordStart = line;
                continue;
            }
            current.Append(character); hasContent = true;
        }
        if (quoted) throw new InvalidDataException($"CSV tırnak yapısı geçersiz (satır {recordStart}).");
        if (hasContent || current.Length > 0) { cells.Add(current.ToString()); yield return (recordStart, cells); }
    }

    private static bool IsBlank(StringBuilder value)
    {
        for (var index = 0; index < value.Length; index++)
            if (value[index] is not (' ' or '\t')) return false;
        return true;
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

    // A spreadsheet runs a cell that starts with '=', '@', or '+'/'-' followed by something that is not a
    // number. Plain signed numbers ("-5", "+90 532 000 00 00", "-12,5") and a lone "-" placeholder stay
    // importable.
    internal static bool LooksLikeFormula(string value)
    {
        var trimmed = value.TrimStart();
        if (trimmed.Length == 0) return false;
        if (trimmed[0] is '=' or '@') return true;
        if (trimmed[0] is not ('+' or '-')) return false;
        var rest = trimmed[1..].Trim();
        return rest.Length > 0 && !rest.All(character => char.IsAsciiDigit(character) || character is ' ' or '.' or ',' or '(' or ')');
    }

    private sealed record XlsxCell(string Value, bool IsFormula);
}
