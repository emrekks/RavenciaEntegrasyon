using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace MarketplaceHub.Infrastructure.Imports;

// A worksheet to write: the first row is the header (bold, frozen). A cell is a string (written as an inline string, so
// text that starts with '=' stays text and is never run as a formula), a decimal (a number shown with two decimals) or
// null (left empty). Widths are in Excel character units, one per column.
public sealed record SimpleXlsxSheet(string Name, IReadOnlyList<IReadOnlyList<object?>> Rows, IReadOnlyList<double> ColumnWidths);

// The smallest workbook Excel, LibreOffice and ImportFileReader open: one part per sheet, a style sheet with a bold
// header and a money number format, and no shared strings. Enough for templates the seller fills in and uploads back.
public static class SimpleXlsxWriter
{
    private const string MainNamespace = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string RelationshipNamespace = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const int MaximumCellText = 32_767;
    private const int HeaderStyle = 1;
    private const int MoneyStyle = 2;

    public static byte[] Write(IReadOnlyList<SimpleXlsxSheet> sheets)
    {
        if (sheets.Count == 0) throw new ArgumentException("En az bir sayfa gerekir.", nameof(sheets));
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            Entry(archive, "[Content_Types].xml", writer =>
            {
                writer.WriteStartElement("Types", "http://schemas.openxmlformats.org/package/2006/content-types");
                Default(writer, "rels", "application/vnd.openxmlformats-package.relationships+xml");
                Default(writer, "xml", "application/xml");
                Override(writer, "/xl/workbook.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml");
                Override(writer, "/xl/styles.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml");
                for (var index = 1; index <= sheets.Count; index++)
                    Override(writer, $"/xl/worksheets/sheet{index}.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml");
                writer.WriteEndElement();
            });
            Entry(archive, "_rels/.rels", writer =>
            {
                writer.WriteStartElement("Relationships", "http://schemas.openxmlformats.org/package/2006/relationships");
                Relationship(writer, "rId1", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument", "xl/workbook.xml");
                writer.WriteEndElement();
            });
            Entry(archive, "xl/workbook.xml", writer =>
            {
                writer.WriteStartElement("workbook", MainNamespace);
                writer.WriteAttributeString("xmlns", "r", null, RelationshipNamespace);
                writer.WriteStartElement("sheets", MainNamespace);
                var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (var index = 0; index < sheets.Count; index++)
                {
                    writer.WriteStartElement("sheet", MainNamespace);
                    writer.WriteAttributeString("name", SheetName(sheets[index].Name, index, usedNames));
                    writer.WriteAttributeString("sheetId", (index + 1).ToString(CultureInfo.InvariantCulture));
                    writer.WriteAttributeString("id", RelationshipNamespace, $"rId{index + 1}");
                    writer.WriteEndElement();
                }
                writer.WriteEndElement();
                writer.WriteEndElement();
            });
            Entry(archive, "xl/_rels/workbook.xml.rels", writer =>
            {
                writer.WriteStartElement("Relationships", "http://schemas.openxmlformats.org/package/2006/relationships");
                for (var index = 1; index <= sheets.Count; index++)
                    Relationship(writer, $"rId{index}", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet", $"worksheets/sheet{index}.xml");
                Relationship(writer, $"rId{sheets.Count + 1}", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles", "styles.xml");
                writer.WriteEndElement();
            });
            Entry(archive, "xl/styles.xml", WriteStyles);
            for (var index = 0; index < sheets.Count; index++)
            {
                var sheet = sheets[index];
                Entry(archive, $"xl/worksheets/sheet{index + 1}.xml", writer => WriteSheet(writer, sheet));
            }
        }
        return buffer.ToArray();
    }

    private static void WriteSheet(XmlWriter writer, SimpleXlsxSheet sheet)
    {
        writer.WriteStartElement("worksheet", MainNamespace);
        if (sheet.Rows.Count > 1)
        {
            writer.WriteStartElement("sheetViews", MainNamespace);
            writer.WriteStartElement("sheetView", MainNamespace);
            writer.WriteAttributeString("workbookViewId", "0");
            writer.WriteStartElement("pane", MainNamespace);
            writer.WriteAttributeString("ySplit", "1");
            writer.WriteAttributeString("topLeftCell", "A2");
            writer.WriteAttributeString("activePane", "bottomLeft");
            writer.WriteAttributeString("state", "frozen");
            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteEndElement();
        }
        if (sheet.ColumnWidths.Count > 0)
        {
            writer.WriteStartElement("cols", MainNamespace);
            for (var column = 0; column < sheet.ColumnWidths.Count; column++)
            {
                writer.WriteStartElement("col", MainNamespace);
                var number = (column + 1).ToString(CultureInfo.InvariantCulture);
                writer.WriteAttributeString("min", number);
                writer.WriteAttributeString("max", number);
                writer.WriteAttributeString("width", Math.Clamp(sheet.ColumnWidths[column], 4, 120).ToString("0.##", CultureInfo.InvariantCulture));
                writer.WriteAttributeString("customWidth", "1");
                writer.WriteEndElement();
            }
            writer.WriteEndElement();
        }
        writer.WriteStartElement("sheetData", MainNamespace);
        for (var rowIndex = 0; rowIndex < sheet.Rows.Count; rowIndex++)
        {
            var row = sheet.Rows[rowIndex];
            var rowNumber = (rowIndex + 1).ToString(CultureInfo.InvariantCulture);
            writer.WriteStartElement("row", MainNamespace);
            writer.WriteAttributeString("r", rowNumber);
            for (var column = 0; column < row.Count; column++)
            {
                var value = row[column];
                if (value is null) continue;
                writer.WriteStartElement("c", MainNamespace);
                writer.WriteAttributeString("r", ColumnName(column) + rowNumber);
                switch (value)
                {
                    case decimal number:
                        writer.WriteAttributeString("s", (rowIndex == 0 ? HeaderStyle : MoneyStyle).ToString(CultureInfo.InvariantCulture));
                        writer.WriteElementString("v", MainNamespace, number.ToString(CultureInfo.InvariantCulture));
                        break;
                    default:
                        if (rowIndex == 0) writer.WriteAttributeString("s", HeaderStyle.ToString(CultureInfo.InvariantCulture));
                        writer.WriteAttributeString("t", "inlineStr");
                        writer.WriteStartElement("is", MainNamespace);
                        writer.WriteStartElement("t", MainNamespace);
                        var text = CellText(Convert.ToString(value, CultureInfo.InvariantCulture));
                        if (text.Length > 0 && (char.IsWhiteSpace(text[0]) || char.IsWhiteSpace(text[^1]))) writer.WriteAttributeString("xml", "space", null, "preserve");
                        writer.WriteString(text);
                        writer.WriteEndElement();
                        writer.WriteEndElement();
                        break;
                }
                writer.WriteEndElement();
            }
            writer.WriteEndElement();
        }
        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static void WriteStyles(XmlWriter writer)
    {
        writer.WriteStartElement("styleSheet", MainNamespace);
        writer.WriteStartElement("fonts", MainNamespace);
        writer.WriteAttributeString("count", "2");
        Font(writer, bold: false);
        Font(writer, bold: true);
        writer.WriteEndElement();
        writer.WriteStartElement("fills", MainNamespace);
        writer.WriteAttributeString("count", "2");
        foreach (var pattern in new[] { "none", "gray125" })
        {
            writer.WriteStartElement("fill", MainNamespace);
            writer.WriteStartElement("patternFill", MainNamespace);
            writer.WriteAttributeString("patternType", pattern);
            writer.WriteEndElement();
            writer.WriteEndElement();
        }
        writer.WriteEndElement();
        writer.WriteStartElement("borders", MainNamespace);
        writer.WriteAttributeString("count", "1");
        writer.WriteStartElement("border", MainNamespace);
        foreach (var side in new[] { "left", "right", "top", "bottom", "diagonal" }) { writer.WriteStartElement(side, MainNamespace); writer.WriteEndElement(); }
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteStartElement("cellStyleXfs", MainNamespace);
        writer.WriteAttributeString("count", "1");
        CellFormat(writer, numberFormat: 0, font: 0, inStyleList: false);
        writer.WriteEndElement();
        // 0: default, 1: bold header, 2: "#,##0.00" (built-in format 4, shown with the reader's own separators).
        writer.WriteStartElement("cellXfs", MainNamespace);
        writer.WriteAttributeString("count", "3");
        CellFormat(writer, numberFormat: 0, font: 0, inStyleList: true);
        CellFormat(writer, numberFormat: 0, font: 1, inStyleList: true);
        CellFormat(writer, numberFormat: 4, font: 0, inStyleList: true);
        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static void Font(XmlWriter writer, bool bold)
    {
        writer.WriteStartElement("font", MainNamespace);
        if (bold) { writer.WriteStartElement("b", MainNamespace); writer.WriteEndElement(); }
        writer.WriteStartElement("sz", MainNamespace); writer.WriteAttributeString("val", "11"); writer.WriteEndElement();
        writer.WriteStartElement("name", MainNamespace); writer.WriteAttributeString("val", "Calibri"); writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static void CellFormat(XmlWriter writer, int numberFormat, int font, bool inStyleList)
    {
        writer.WriteStartElement("xf", MainNamespace);
        writer.WriteAttributeString("numFmtId", numberFormat.ToString(CultureInfo.InvariantCulture));
        writer.WriteAttributeString("fontId", font.ToString(CultureInfo.InvariantCulture));
        writer.WriteAttributeString("fillId", "0");
        writer.WriteAttributeString("borderId", "0");
        if (inStyleList)
        {
            writer.WriteAttributeString("xfId", "0");
            if (numberFormat != 0) writer.WriteAttributeString("applyNumberFormat", "1");
            if (font != 0) writer.WriteAttributeString("applyFont", "1");
        }
        writer.WriteEndElement();
    }

    private static void Entry(ZipArchive archive, string name, Action<XmlWriter> write)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = false });
        writer.WriteStartDocument(true);
        write(writer);
        writer.WriteEndDocument();
    }

    private static void Default(XmlWriter writer, string extension, string contentType)
    {
        writer.WriteStartElement("Default", "http://schemas.openxmlformats.org/package/2006/content-types");
        writer.WriteAttributeString("Extension", extension);
        writer.WriteAttributeString("ContentType", contentType);
        writer.WriteEndElement();
    }

    private static void Override(XmlWriter writer, string part, string contentType)
    {
        writer.WriteStartElement("Override", "http://schemas.openxmlformats.org/package/2006/content-types");
        writer.WriteAttributeString("PartName", part);
        writer.WriteAttributeString("ContentType", contentType);
        writer.WriteEndElement();
    }

    private static void Relationship(XmlWriter writer, string id, string type, string target)
    {
        writer.WriteStartElement("Relationship", "http://schemas.openxmlformats.org/package/2006/relationships");
        writer.WriteAttributeString("Id", id);
        writer.WriteAttributeString("Type", type);
        writer.WriteAttributeString("Target", target);
        writer.WriteEndElement();
    }

    // Excel refuses a sheet name over 31 characters, with []:*?/\ in it, or used twice.
    private static string SheetName(string name, int index, HashSet<string> used)
    {
        var cleaned = new string(name.Where(character => character is not ('[' or ']' or ':' or '*' or '?' or '/' or '\\') && !char.IsControl(character)).ToArray()).Trim().Trim('\'');
        if (cleaned.Length > 31) cleaned = cleaned[..31];
        if (cleaned.Length == 0 || !used.Add(cleaned)) { cleaned = $"Sayfa{index + 1}"; used.Add(cleaned); }
        return cleaned;
    }

    // Characters XML cannot carry (control characters from marketplace titles) are dropped; Excel's cell limit applies.
    private static string CellText(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var builder = new StringBuilder(Math.Min(value.Length, MaximumCellText));
        for (var index = 0; index < value.Length && builder.Length < MaximumCellText; index++)
        {
            var character = value[index];
            if (char.IsHighSurrogate(character) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]))
            {
                if (builder.Length + 2 > MaximumCellText) break;
                builder.Append(character).Append(value[++index]);
            }
            else if (XmlConvert.IsXmlChar(character)) builder.Append(character);
        }
        return builder.ToString();
    }

    internal static string ColumnName(int column)
    {
        var name = string.Empty;
        for (var index = column + 1; index > 0; index = (index - 1) / 26) name = (char)('A' + (index - 1) % 26) + name;
        return name;
    }
}
