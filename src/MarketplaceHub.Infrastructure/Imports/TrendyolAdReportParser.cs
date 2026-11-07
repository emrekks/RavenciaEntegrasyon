using System.Globalization;
using System.Text.RegularExpressions;
using MarketplaceHub.Domain;

namespace MarketplaceHub.Infrastructure.Imports;

public sealed record ParsedAdReport(IReadOnlyList<AdCampaignRow> Campaigns, IReadOnlyList<string> Warnings);

// Trendyol's "Ürün Reklamları Raporum" export (Reklam Yönetimi): one row per campaign with its totals since it started.
// Columns are found by their header text. Trendyol writes every figure as Turkish text ("1.508,76", "1.000" for a
// thousand) and "-" for none; a figure that does not read stays unknown, with a warning, never guessed.
public static partial class TrendyolAdReportParser
{
    private const int MaximumWarnings = 20;
    private const int MaximumCampaigns = 5000;
    private static readonly TimeSpan TurkeyOffset = TimeSpan.FromHours(3);

    public static ParsedAdReport Parse(XlsxTable table)
    {
        var columns = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < table.Headers.Count; index++) columns.TryAdd(CommissionTariffFileParser.HeaderKey(table.Headers[index]), index);
        if (!columns.ContainsKey("REKLAMADI") || !columns.ContainsKey("HARCANANBUTCE") || !columns.ContainsKey("TOPLAMREKLAMCIROSU"))
            throw new InvalidDataException("Dosya Trendyol ürün reklamları raporu değil; Reklam Yönetimi'nden indirilen \"Ürün Reklamları Raporum\" dosyasını yükleyin.");
        if (table.Rows.Count > MaximumCampaigns) throw new InvalidDataException($"Dosyada en fazla {MaximumCampaigns.ToString(CultureInfo.InvariantCulture)} kampanya olabilir.");

        var warnings = new List<string>();
        var campaigns = new List<AdCampaignRow>();
        foreach (var row in table.Rows)
        {
            if (row.FormulaColumns.Count > 0) { Warn(row.RowNumber, "formül içeriyor; satır alınmadı."); continue; }
            string? Text(string column) => columns.TryGetValue(column, out var index) && index < row.Cells.Count && !string.IsNullOrWhiteSpace(row.Cells[index]) && row.Cells[index].Trim() != "-"
                ? row.Cells[index].Trim() : null;
            decimal? Number(string column)
            {
                if (Text(column) is not { } text) return null;
                if (TryTurkishNumber(text, out var value) && value >= 0) return value;
                Warn(row.RowNumber, $"{table.Headers[columns[column]]} değeri sayı değil ({Short(text, 40)}); bilinmiyor sayıldı.");
                return null;
            }
            long? Count(string column) => Number(column) is { } value && value == decimal.Truncate(value) && value <= long.MaxValue ? (long)value : null;

            var name = Short(Text("REKLAMADI"), 300);
            if (name is null) { Warn(row.RowNumber, "reklam adı yok; satır alınmadı."); continue; }
            campaigns.Add(new AdCampaignRow(name, Short(Text("REKLAMSTATUSU"), 60), Short(Text("BASLANGICTARIHI"), 40), Short(Text("BITISTARIHI"), 40),
                Count("URUNADEDI") is { } products && products <= int.MaxValue ? (int)products : null,
                Number("TOPLAMBUTCE"), Number("GUNLUKBUTCE"), Number("KALANBUTCE"), Number("HARCANANBUTCE"), Short(Text("TBMTEKLIFI"), 200), Number("GERCEKLESENTBM"),
                Count("TIKLANMA"), Count("GORUNTULENME"), Number("DOGRUDANSATISADEDI"), Number("DOLAYLISATISADEDI"), Number("TOPLAMSATISADEDI"),
                Number("DOGRUDANREKLAMCIROSU"), Number("DOLAYLIREKLAMCIROSU"), Number("TOPLAMREKLAMCIROSU"), Number("HARCAMAGETIRISI")));
        }
        if (campaigns.Count == 0) throw new InvalidDataException("Dosyada kampanya satırı yok.");
        return new(campaigns, warnings);

        void Warn(int rowNumber, string reason)
        {
            if (warnings.Count < MaximumWarnings) warnings.Add($"Satır {rowNumber.ToString(CultureInfo.InvariantCulture)}: {reason}");
        }
    }

    // A Turkish figure: "1.508,76" (dots group thousands, a comma marks the decimals), "1.000" a thousand, "458" plain.
    public static bool TryTurkishNumber(string text, out decimal value)
    {
        var cleaned = text.Trim().Replace("₺", string.Empty, StringComparison.Ordinal).Replace("TL", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("%", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal);
        if (cleaned.Contains(',', StringComparison.Ordinal)) cleaned = cleaned.Replace(".", string.Empty, StringComparison.Ordinal).Replace(',', '.');
        else if (ThousandsPattern().IsMatch(cleaned)) cleaned = cleaned.Replace(".", string.Empty, StringComparison.Ordinal);
        return decimal.TryParse(cleaned, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
    }

    // The export's time from its name, as Trendyol names it ("Ürün Reklamları Raporum - 07.11.2026-03.54.xlsx", Istanbul
    // local time), as a UTC instant; null when the name does not carry one.
    public static DateTimeOffset? ExportedAt(string fileName)
    {
        var match = FileNameTimePattern().Match(fileName);
        if (!match.Success) return null;
        int Part(string name) => int.Parse(match.Groups[name].Value, CultureInfo.InvariantCulture);
        var (day, month, year, hour, minute) = (Part("d"), Part("m"), Part("y"), Part("h"), Part("n"));
        if (month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month) || hour > 23 || minute > 59) return null;
        return new DateTimeOffset(year, month, day, hour, minute, 0, TurkeyOffset).ToUniversalTime();
    }

    private static string? Short(string? value, int length) => value is null ? null : value.Length <= length ? value : value[..length];

    [GeneratedRegex(@"^\d{1,3}(\.\d{3})+$", RegexOptions.CultureInvariant)]
    private static partial Regex ThousandsPattern();

    [GeneratedRegex(@"(?<d>\d{2})\.(?<m>\d{2})\.(?<y>\d{4})-(?<h>\d{2})\.(?<n>\d{2})", RegexOptions.CultureInvariant)]
    private static partial Regex FileNameTimePattern();
}
