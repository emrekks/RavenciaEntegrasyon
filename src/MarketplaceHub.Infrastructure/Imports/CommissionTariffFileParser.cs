using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MarketplaceHub.Domain;

namespace MarketplaceHub.Infrastructure.Imports;

public sealed record ParsedCommissionTariff(DateTimeOffset PeriodStart, DateTimeOffset PeriodEnd, string PeriodText, IReadOnlyList<CommissionTariffRow> Rows);
public sealed record ParsedCommissionTariffFile(IReadOnlyList<ParsedCommissionTariff> Tariffs, int SkippedRows, IReadOnlyList<string> Warnings);

// Trendyol's weekly commission tariff export. Columns are found by their header text (Turkish letters, spaces, dots
// and case ignored), never by position, so a reordered or extended export still reads. A row whose numbers do not
// read is skipped with a warning, never guessed.
public static partial class CommissionTariffFileParser
{
    public static readonly TimeSpan TurkeyOffset = TimeSpan.FromHours(3);
    private const int MaximumWarnings = 20;

    private enum Column { ProductName, Barcode, SellerSku, Size, ModelCode, Tier1Min, Tier2Max, Tier2Min, Tier3Max, Tier3Min, Tier4Max, Period, Rate1, Rate2, Rate3, Rate4, BasisPrice, CurrentRate, CurrentSalePrice, TariffGroup }

    private static readonly IReadOnlyDictionary<string, Column> Headers = new Dictionary<string, Column>(StringComparer.Ordinal)
    {
        ["URUNISMI"] = Column.ProductName,
        ["URUNADI"] = Column.ProductName,
        ["BARKOD"] = Column.Barcode,
        ["SATICISTOKKODU"] = Column.SellerSku,
        ["BEDEN"] = Column.Size,
        ["MODELKODU"] = Column.ModelCode,
        ["1FIYATALTLIMIT"] = Column.Tier1Min,
        ["2FIYATUSTLIMIT"] = Column.Tier2Max,
        ["2FIYATALTLIMIT"] = Column.Tier2Min,
        ["3FIYATUSTLIMIT"] = Column.Tier3Max,
        ["3FIYATALTLIMIT"] = Column.Tier3Min,
        ["4FIYATUSTLIMIT"] = Column.Tier4Max,
        ["1KOMISYON"] = Column.Rate1,
        ["2KOMISYON"] = Column.Rate2,
        ["3KOMISYON"] = Column.Rate3,
        ["4KOMISYON"] = Column.Rate4,
        ["KOMISYONAESASFIYAT"] = Column.BasisPrice,
        ["GUNCELKOMISYON"] = Column.CurrentRate,
        ["GUNCELTSF"] = Column.CurrentSalePrice,
        ["TARIFEGRUBU"] = Column.TariffGroup
    };

    public static ParsedCommissionTariffFile Parse(XlsxTable table, DateTimeOffset now)
    {
        var columns = new Dictionary<Column, int>();
        for (var index = 0; index < table.Headers.Count; index++)
        {
            var key = HeaderKey(table.Headers[index]);
            if (key.StartsWith("TARIHARALIGI", StringComparison.Ordinal)) columns.TryAdd(Column.Period, index);
            else if (Headers.TryGetValue(key, out var column)) columns.TryAdd(column, index);
        }
        var missing = new List<string>();
        if (!columns.ContainsKey(Column.Barcode)) missing.Add("BARKOD");
        if (!columns.ContainsKey(Column.Period)) missing.Add("Tarih aralığı");
        if (!columns.ContainsKey(Column.Tier1Min) || !columns.ContainsKey(Column.Rate1)) missing.Add("1.Fiyat Alt Limit / 1.KOMİSYON");
        if (missing.Count > 0)
            throw new InvalidDataException($"Dosya komisyon tarifesi biçiminde değil; eksik sütun: {string.Join(", ", missing)}.");

        var readColumns = columns.Values.ToHashSet();
        var warnings = new List<string>();
        var skipped = 0;
        var periods = new Dictionary<(DateTimeOffset Start, DateTimeOffset End), (string Text, List<CommissionTariffRow> Rows, HashSet<string> Barcodes)>();
        foreach (var row in table.Rows)
        {
            string? Text(Column column) => columns.TryGetValue(column, out var index) && index < row.Cells.Count && !string.IsNullOrWhiteSpace(row.Cells[index]) ? row.Cells[index].Trim() : null;
            void Skip(string reason) { skipped++; if (warnings.Count < MaximumWarnings) warnings.Add($"Satır {row.RowNumber.ToString(CultureInfo.InvariantCulture)}: {reason}"); }

            // The export computes its own "Hesaplanan Komisyon" with a formula; only a formula in a column read here
            // makes the row unreadable (formulas are never evaluated).
            if (row.FormulaColumns.Any(readColumns.Contains)) { Skip("okunan bir sütunda formül var; satır okunmadı."); continue; }
            var barcode = Text(Column.Barcode);
            if (barcode is null) { Skip("barkod yok."); continue; }
            var periodText = Text(Column.Period);
            if (periodText is null || !TryParsePeriod(periodText, now, out var start, out var end)) { Skip($"tarih aralığı okunamadı ({periodText ?? "boş"})."); continue; }

            var numbers = new Dictionary<Column, decimal?>();
            var unreadable = false;
            foreach (var column in new[] { Column.Tier1Min, Column.Tier2Max, Column.Tier2Min, Column.Tier3Max, Column.Tier3Min, Column.Tier4Max, Column.Rate1, Column.Rate2, Column.Rate3, Column.Rate4, Column.BasisPrice, Column.CurrentRate, Column.CurrentSalePrice })
            {
                var text = Text(column);
                if (text is null) { numbers[column] = null; continue; }
                if (!TryParseNumber(text, out var number) || number < 0) { unreadable = true; Skip($"{table.Headers[columns[column]]} değeri sayı değil ({text})."); break; }
                numbers[column] = number;
            }
            if (unreadable) continue;
            if (numbers[Column.Rate1] is null || numbers[Column.Tier1Min] is null) { Skip("1. fiyat bandı ya da komisyonu yok."); continue; }
            if (new[] { Column.Rate1, Column.Rate2, Column.Rate3, Column.Rate4 }.Any(column => numbers[column] is > 100)) { Skip("komisyon oranı %100'ü aşıyor."); continue; }

            if (!periods.TryGetValue((start, end), out var period))
                periods[(start, end)] = period = (periodText, [], new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            if (!period.Barcodes.Add(barcode)) { Skip($"{barcode} barkodu dosyada birden çok kez var; ilki alındı."); continue; }
            period.Rows.Add(new CommissionTariffRow
            {
                Id = Guid.CreateVersion7(),
                Barcode = Short(barcode, 160)!,
                SellerSku = Short(Text(Column.SellerSku), 160),
                ModelCode = Short(Text(Column.ModelCode), 160),
                ProductName = Short(Text(Column.ProductName), 500),
                Size = Short(Text(Column.Size), 80),
                Tier1Min = numbers[Column.Tier1Min],
                Tier2Max = numbers[Column.Tier2Max],
                Tier2Min = numbers[Column.Tier2Min],
                Tier3Max = numbers[Column.Tier3Max],
                Tier3Min = numbers[Column.Tier3Min],
                Tier4Max = numbers[Column.Tier4Max],
                Rate1 = numbers[Column.Rate1],
                Rate2 = numbers[Column.Rate2],
                Rate3 = numbers[Column.Rate3],
                Rate4 = numbers[Column.Rate4],
                BasisPrice = numbers[Column.BasisPrice],
                CurrentRate = numbers[Column.CurrentRate],
                CurrentSalePrice = numbers[Column.CurrentSalePrice],
                TariffGroup = Short(Text(Column.TariffGroup), 80)
            });
        }
        if (periods.Count == 0)
            throw new InvalidDataException(warnings.Count > 0 ? $"Dosyada okunabilen tarife satırı yok. {warnings[0]}" : "Dosyada tarife satırı yok.");
        return new(periods.Select(pair => new ParsedCommissionTariff(pair.Key.Start, pair.Key.End, pair.Value.Text, pair.Value.Rows)).OrderBy(tariff => tariff.PeriodStart).ToList(), skipped, warnings);
    }

    // "22 Ekim 08.00-29 Ekim 07.59" in Turkey's time. The file names no year: the year that puts the start nearest to
    // `now` is taken, and an end before the start is in the next year (a period across New Year). The end minute is
    // included, so the stored end is the next minute (exclusive).
    public static bool TryParsePeriod(string text, DateTimeOffset now, out DateTimeOffset start, out DateTimeOffset end)
    {
        start = end = default;
        var match = PeriodPattern().Match(text);
        if (!match.Success) return false;
        if (!TryMonth(match.Groups["m1"].Value, out var startMonth) || !TryMonth(match.Groups["m2"].Value, out var endMonth)) return false;
        var day1 = int.Parse(match.Groups["d1"].Value, CultureInfo.InvariantCulture); var day2 = int.Parse(match.Groups["d2"].Value, CultureInfo.InvariantCulture);
        var hour1 = int.Parse(match.Groups["h1"].Value, CultureInfo.InvariantCulture); var hour2 = int.Parse(match.Groups["h2"].Value, CultureInfo.InvariantCulture);
        var minute1 = int.Parse(match.Groups["n1"].Value, CultureInfo.InvariantCulture); var minute2 = int.Parse(match.Groups["n2"].Value, CultureInfo.InvariantCulture);
        if (hour1 > 23 || hour2 > 23 || minute1 > 59 || minute2 > 59) return false;
        var localNow = now.ToOffset(TurkeyOffset);
        DateTimeOffset? best = null;
        foreach (var year in new[] { localNow.Year - 1, localNow.Year, localNow.Year + 1 })
        {
            if (day1 > DateTime.DaysInMonth(year, startMonth)) continue;
            var candidate = new DateTimeOffset(year, startMonth, day1, hour1, minute1, 0, TurkeyOffset);
            if (best is null || (candidate - localNow).Duration() < (best.Value - localNow).Duration()) best = candidate;
        }
        if (best is null) return false;
        var endYear = best.Value.Year;
        if (endMonth < startMonth || endMonth == startMonth && day2 < day1) endYear++;
        if (day2 > DateTime.DaysInMonth(endYear, endMonth)) return false;
        var endInclusive = new DateTimeOffset(endYear, endMonth, day2, hour2, minute2, 0, TurkeyOffset);
        if (endInclusive <= best.Value) return false;
        start = best.Value;
        end = endInclusive.AddMinutes(1);
        return true;
    }

    // Spreadsheet numbers come as "470.64"; a Turkish "470,64" or "1.234,56" is read too.
    public static bool TryParseNumber(string text, out decimal value)
    {
        var cleaned = text.Trim().Replace("₺", string.Empty, StringComparison.Ordinal).Replace("%", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal);
        if (cleaned.Contains(',', StringComparison.Ordinal))
            cleaned = cleaned.Contains('.', StringComparison.Ordinal) ? cleaned.Replace(".", string.Empty, StringComparison.Ordinal).Replace(',', '.') : cleaned.Replace(',', '.');
        return decimal.TryParse(cleaned, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out value);
    }

    internal static string HeaderKey(string header)
    {
        var builder = new StringBuilder(header.Length);
        foreach (var character in Fold(header))
            if (char.IsAsciiLetterOrDigit(character)) builder.Append(character);
        var key = builder.ToString();
        // "Üst Limiti" and "Üst Limit" are the same column.
        return key.EndsWith("LIMITI", StringComparison.Ordinal) ? key[..^1] : key;
    }

    private static bool TryMonth(string name, out int month)
    {
        month = Array.IndexOf(MonthKeys, HeaderKey(name)) + 1;
        if (month == 0) month = Array.FindIndex(MonthKeys, key => key.StartsWith(HeaderKey(name), StringComparison.Ordinal) && HeaderKey(name).Length >= 3) + 1;
        return month > 0;
    }

    private static readonly string[] MonthKeys = ["OCAK", "SUBAT", "MART", "NISAN", "MAYIS", "HAZIRAN", "TEMMUZ", "AGUSTOS", "EYLUL", "EKIM", "KASIM", "ARALIK"];

    private static string Fold(string value) => value.Trim()
        .Replace('İ', 'I').Replace('ı', 'I').Replace('Ş', 'S').Replace('ş', 'S').Replace('Ğ', 'G').Replace('ğ', 'G')
        .Replace('Ü', 'U').Replace('ü', 'U').Replace('Ö', 'O').Replace('ö', 'O').Replace('Ç', 'C').Replace('ç', 'C')
        .ToUpperInvariant();

    private static string? Short(string? value, int length) => value is null ? null : value.Length <= length ? value : value[..length];

    [GeneratedRegex(@"(?<d1>\d{1,2})\s+(?<m1>\p{L}+)\s+(?<h1>\d{1,2})[.:](?<n1>\d{2})\s*[-–—]\s*(?<d2>\d{1,2})\s+(?<m2>\p{L}+)\s+(?<h2>\d{1,2})[.:](?<n2>\d{2})", RegexOptions.CultureInvariant)]
    private static partial Regex PeriodPattern();
}
