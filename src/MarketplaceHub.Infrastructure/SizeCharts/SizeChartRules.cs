using System.Globalization;

namespace MarketplaceHub.Infrastructure.SizeCharts;

public sealed record SizeChartRowInput(string? Size, IReadOnlyList<decimal?>? Values);
public sealed record SizeChartInput(IReadOnlyList<string?>? Columns, IReadOnlyList<SizeChartRowInput?>? Rows);

// The measurements as the chart shows them: column names, and per size the values as text (Turkish decimal comma).
public sealed record SizeChartRow(string Size, IReadOnlyList<string> Values);
public sealed record SizeChartTable(IReadOnlyList<string> Columns, IReadOnlyList<SizeChartRow> Rows);

// The measurement grid the editor sends: 1-8 named columns (cm), one row per size, every cell a number above 0 and
// at most 500 with at most one decimal. Values are never rounded or changed: a value with more decimals is refused.
public static class SizeChartRules
{
    public const int MaxColumns = 8;
    public const int MaxRows = 30;
    public const int MaxColumnName = 24;
    public const int MaxSizeName = 40;
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");

    public static bool IsSizeOption(string? label) =>
        string.Equals(label?.Trim().ToLower(Turkish), "beden", StringComparison.Ordinal);

    public static (SizeChartTable? Table, string? Error) Validate(SizeChartInput? input)
    {
        var columns = (input?.Columns ?? []).Select(name => name?.Trim() ?? "").ToList();
        if (columns.Count is 0 or > MaxColumns) return (null, $"1-{MaxColumns} ölçü kolonu girin.");
        if (columns.Any(name => name.Length == 0)) return (null, "Kolon adı boş olamaz.");
        if (columns.Any(name => name.Length > MaxColumnName)) return (null, $"Kolon adı en fazla {MaxColumnName} karakter olabilir.");
        if (columns.Any(name => name.Any(char.IsControl))) return (null, "Kolon adında görünmeyen karakter olamaz.");
        if (columns.Select(name => name.ToLower(Turkish)).Distinct(StringComparer.Ordinal).Count() != columns.Count) return (null, "Kolon adları birbirinden farklı olmalı.");

        var rows = input?.Rows ?? [];
        if (rows.Count is 0 or > MaxRows) return (null, $"1-{MaxRows} beden satırı gerekli.");
        var table = new List<SizeChartRow>(rows.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var size = row?.Size?.Trim() ?? "";
            if (size.Length == 0) return (null, "Beden adı boş olamaz.");
            if (size.Length > MaxSizeName || size.Any(char.IsControl)) return (null, $"'{size}' beden adı geçersiz.");
            if (!seen.Add(size.ToLower(Turkish))) return (null, $"'{size}' bedeni iki kez yazılmış.");
            var values = row!.Values ?? [];
            if (values.Count != columns.Count) return (null, $"'{size}' satırında her kolon için bir ölçü girin.");
            var texts = new List<string>(values.Count);
            for (var index = 0; index < values.Count; index++)
            {
                if (values[index] is not { } value) return (null, $"'{size}' · {columns[index]}: ölçü boş olamaz.");
                if (value <= 0 || value > 500) return (null, $"'{size}' · {columns[index]}: ölçü 0'dan büyük ve en fazla 500 cm olmalı.");
                if (decimal.Round(value, 1) != value) return (null, $"'{size}' · {columns[index]}: ölçü en fazla bir ondalık basamak içerebilir.");
                texts.Add(Format(value));
            }
            table.Add(new SizeChartRow(size, texts));
        }
        return (new SizeChartTable(columns, table), null);
    }

    // 64 → "64", 66.5 → "66,5", 70.0 → "70".
    public static string Format(decimal value) => value.ToString("0.#", Turkish);

    // The values the editor gets back to prefill the dialog.
    public static decimal Parse(string text) => decimal.Parse(text, NumberStyles.Number, Turkish);
}
