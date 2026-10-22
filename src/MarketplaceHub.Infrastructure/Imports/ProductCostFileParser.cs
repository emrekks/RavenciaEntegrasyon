using System.Globalization;
using System.Text.RegularExpressions;
using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Imports;

public enum ProductCostValueKind { Empty, Set, Clear, Invalid }

// What the "Yeni Maliyet" cell asks for: nothing (Empty), a cost (Set, rounded to kuruş), removing the cost (Clear) or a
// value that cannot be used (Invalid, Error says why).
public readonly record struct ProductCostValue(ProductCostValueKind Kind, decimal? Amount, string? Error)
{
    public static ProductCostValue Empty => new(ProductCostValueKind.Empty, null, null);
    public static ProductCostValue Clear => new(ProductCostValueKind.Clear, null, null);
    public static ProductCostValue Set(decimal amount) => new(ProductCostValueKind.Set, amount, null);
    public static ProductCostValue Invalid(string error) => new(ProductCostValueKind.Invalid, null, error);
}

public sealed record ProductCostFileRow(int RowNumber, string? Sku, string? Barcode, ProductCostValue Value);

// A variant the uploaded rows can name, as the catalog holds it now.
public sealed record ProductCostCandidate(Guid VariantId, Guid ProductId, long Version, string Sku, string? Barcode, string ProductTitle, string? VariantLabel, decimal? CostPrice);

// Reads the cost template back (the .xlsx it was downloaded as, or a CSV saved from it). Columns are found by their header
// text, whatever the order: "Yeni Maliyet" and at least one of "Stok Kodu" and "Barkod". Every other column is ignored.
public static partial class ProductCostFileParser
{
    public const int MaximumRows = 20_000;
    public const decimal MaximumCost = 10_000_000m;

    private static readonly string[] SkuHeaders = ["STOKKODU", "SKU", "STOKKODUSKU", "SATICISTOKKODU", "URUNSTOKKODU"];
    private static readonly string[] BarcodeHeaders = ["BARKOD", "BARCODE", "URUNBARKODU"];
    private static readonly string[] NewCostHeaders = ["YENIMALIYET", "YENIMALIYETTL"];
    // A file of the seller's own may name the cost column just "Maliyet"; the template's "Güncel Maliyet" never counts.
    private static readonly string[] FallbackCostHeaders = ["MALIYET", "MALIYETTL", "BIRIMMALIYET"];

    public static IReadOnlyList<ProductCostFileRow> ParseXlsx(Stream stream, long maximumExpandedBytes)
    {
        var table = ImportFileReader.ReadXlsxTable(stream, maximumExpandedBytes);
        return Parse(table.Headers, table.Rows.Select(row => (row.RowNumber, row.Cells, (IReadOnlyCollection<int>)row.FormulaColumns)), fromCsv: false);
    }

    public static IReadOnlyList<ProductCostFileRow> ParseCsv(Stream stream)
    {
        var text = ImportFileReader.DecodeCsv(stream);
        using var records = ImportFileReader.ParseCsvRecords(text, ImportFileReader.SniffDelimiter(text)).GetEnumerator();
        if (!records.MoveNext()) throw new InvalidDataException("Dosyada başlık satırı yok.");
        var headers = records.Current.Cells;
        return Parse(headers, Rest(records), fromCsv: true);

        static IEnumerable<(int, IReadOnlyList<string>, IReadOnlyCollection<int>)> Rest(IEnumerator<(int RowNumber, List<string> Cells)> records)
        {
            while (records.MoveNext())
            {
                if (records.Current.Cells.All(string.IsNullOrWhiteSpace)) continue;
                yield return (records.Current.RowNumber, records.Current.Cells, Array.Empty<int>());
            }
        }
    }

    internal static IReadOnlyList<ProductCostFileRow> Parse(IReadOnlyList<string> headers, IEnumerable<(int RowNumber, IReadOnlyList<string> Cells, IReadOnlyCollection<int> FormulaColumns)> rows, bool fromCsv)
    {
        var keys = headers.Select(HeaderKey).ToArray();
        var sku = Column(keys, SkuHeaders);
        var barcode = Column(keys, BarcodeHeaders);
        var cost = Column(keys, NewCostHeaders);
        if (cost < 0) cost = Column(keys, FallbackCostHeaders);
        if (cost < 0) throw new InvalidDataException("\"Yeni Maliyet\" sütunu bulunamadı; Ravencia'dan indirilen şablonu kullanın.");
        if (sku < 0 && barcode < 0) throw new InvalidDataException("\"Stok Kodu\" ya da \"Barkod\" sütunu bulunamadı; Ravencia'dan indirilen şablonu kullanın.");

        var result = new List<ProductCostFileRow>();
        foreach (var (rowNumber, cells, formulas) in rows)
        {
            if (result.Count >= MaximumRows) throw new InvalidDataException($"Dosyada en fazla {MaximumRows:N0} satır olabilir; dosyayı bölerek yükleyin.");
            var skuText = Cell(cells, sku);
            var barcodeText = Cell(cells, barcode);
            ProductCostValue value;
            // A formula's result is not read back (the reader never evaluates one): the seller pastes values instead.
            if (formulas.Contains(cost) || sku >= 0 && formulas.Contains(sku) || barcode >= 0 && formulas.Contains(barcode))
                value = ProductCostValue.Invalid("Formül içeren hücre okunmaz; değerleri \"Değerleri Yapıştır\" ile yazın.");
            else
                value = ParseCost(Cell(cells, cost) ?? string.Empty, fromCsv);
            if (skuText is null && barcodeText is null && value.Kind == ProductCostValueKind.Empty) continue;
            result.Add(new ProductCostFileRow(rowNumber, skuText, barcodeText, value));
        }
        return result;
    }

    // "123,45", "123.45", "1.234,56", "1234", "₺ 99,90", "99,90 TL" are costs; "-" or "sil" removes the cost; an empty cell
    // asks for nothing. "1.234" alone is refused: in Turkish it is one thousand two hundred thirty-four, elsewhere a
    // little over one, and a guess either way writes a wrong cost.
    public static ProductCostValue ParseCost(string text, bool fromCsv)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0) return ProductCostValue.Empty;
        if (trimmed is "-" or "–" or "—" || CatalogKeyNormalizer.Key(trimmed) == "SIL") return ProductCostValue.Clear;
        if (fromCsv && ImportFileReader.LooksLikeFormula(trimmed)) return ProductCostValue.Invalid("Formül benzeri içerik kabul edilmez.");

        var cleaned = trimmed.Replace("₺", string.Empty, StringComparison.Ordinal);
        if (cleaned.EndsWith("TL", StringComparison.OrdinalIgnoreCase)) cleaned = cleaned[..^2];
        cleaned = string.Concat(cleaned.Where(character => !char.IsWhiteSpace(character)));
        decimal amount;
        if (TurkishNumber().IsMatch(cleaned))
            amount = decimal.Parse(cleaned.Replace(".", string.Empty, StringComparison.Ordinal).Replace(',', '.'), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
        else if (AmbiguousThousands().IsMatch(cleaned))
            return ProductCostValue.Invalid($"\"{trimmed}\" belirsiz: binlik ayıracı mı, ondalık mı? Binlik için 1234, ondalık için 1,234 yazın.");
        else if (PlainNumber().IsMatch(cleaned))
            amount = decimal.Parse(cleaned, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
        // A number cell Excel stores in exponent form (1E-3) never comes from a CSV.
        else if (!fromCsv && ExponentNumber().IsMatch(cleaned) && decimal.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out var exponent))
            amount = exponent;
        else
            return ProductCostValue.Invalid($"\"{Short(trimmed)}\" bir tutar değil.");

        if (amount < 0) return ProductCostValue.Invalid("Maliyet negatif olamaz.");
        amount = Math.Round(amount, 2, MidpointRounding.AwayFromZero);
        if (amount > MaximumCost) return ProductCostValue.Invalid("Maliyet 10.000.000 ₺'yi aşamaz.");
        return ProductCostValue.Set(amount);
    }

    // Upper case, Turkish letters folded, only letters and digits: "Stok Kodu (SKU)" → STOKKODUSKU, "Yeni Maliyet (₺)" → YENIMALIYET.
    internal static string HeaderKey(string header) => CatalogKeyNormalizer.Key(header, 320).Replace("-", string.Empty, StringComparison.Ordinal);

    private static int Column(string[] keys, string[] names)
    {
        foreach (var name in names)
        {
            var index = Array.IndexOf(keys, name);
            if (index >= 0) return index;
        }
        return -1;
    }

    private static string? Cell(IReadOnlyList<string> cells, int column) =>
        column >= 0 && column < cells.Count && !string.IsNullOrWhiteSpace(cells[column]) ? cells[column].Trim() : null;

    private static string Short(string value) => value.Length <= 40 ? value : value[..40] + "…";

    // 1.234,56 / 1234,56 / -12,5
    [GeneratedRegex(@"^-?(\d{1,3}(\.\d{3})+|\d+),\d+$")]
    private static partial Regex TurkishNumber();

    // 1.234 / 12.345.678: dots that can only be thousands, or a decimal point with exactly three digits after it.
    [GeneratedRegex(@"^-?\d{1,3}(\.\d{3})+$")]
    private static partial Regex AmbiguousThousands();

    [GeneratedRegex(@"^-?(\d+(\.\d+)?|\.\d+)$")]
    private static partial Regex PlainNumber();

    [GeneratedRegex(@"^-?\d+(\.\d+)?[eE][-+]?\d+$")]
    private static partial Regex ExponentNumber();
}

// Turns the parsed rows into the preview: which variant each row names (stock code first, then barcode), what changes,
// and why a row is left out. Pure, so the same rules hold in tests and on the server.
public static class ProductCostImportPlanner
{
    public const int PreviewRowLimit = 200;

    public static ProductCostImportPreview Plan(string fileName, IReadOnlyList<ProductCostFileRow> rows, IReadOnlyList<ProductCostCandidate> variants)
    {
        var bySku = variants.GroupBy(variant => CatalogKeyNormalizer.Key(variant.Sku)).Where(group => group.Key.Length > 0).ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var byBarcode = variants.Where(variant => !string.IsNullOrWhiteSpace(variant.Barcode)).GroupBy(variant => CatalogKeyNormalizer.Key(variant.Barcode)).Where(group => group.Key.Length > 0).ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);

        var resolved = new List<(ProductCostFileRow Row, ProductCostCandidate? Variant, string Outcome, string? Message)>(rows.Count);
        var skipped = 0;
        foreach (var row in rows)
        {
            if (row.Value.Kind == ProductCostValueKind.Empty) { skipped++; continue; }
            var (variant, outcome, message) = Match(row, bySku, byBarcode);
            if (variant is not null && row.Value.Kind == ProductCostValueKind.Invalid) (outcome, message) = ("ERROR", row.Value.Error);
            resolved.Add((row, variant, outcome ?? "PENDING", message));
        }

        // A variant named by more than one row is written only when every row asks for the same thing; otherwise none
        // of its rows is used, since which one the seller meant cannot be told.
        var views = new List<ProductCostImportRowView>(resolved.Count);
        var changes = new List<ProductCostChange>();
        foreach (var group in resolved.GroupBy(item => item.Variant?.VariantId ?? Guid.Empty))
        {
            var items = group.ToList();
            if (group.Key == Guid.Empty || items.All(item => item.Outcome != "PENDING"))
            {
                views.AddRange(items.Select(item => View(item.Row, item.Variant, item.Outcome, item.Message)));
                continue;
            }
            var pending = items.Where(item => item.Outcome == "PENDING").ToList();
            var lines = string.Join(", ", items.Select(item => item.Row.RowNumber));
            var distinct = pending.Select(item => item.Row.Value).Distinct().Count();
            if (items.Count > 1 && (distinct > 1 || pending.Count != items.Count))
            {
                views.AddRange(items.Select(item => View(item.Row, item.Variant, "ERROR", item.Outcome == "ERROR" ? item.Message : $"Bu varyant dosyada birden çok satırda farklı değerle var (satır {lines}).")));
                continue;
            }
            var first = pending[0];
            var variant = first.Variant!;
            var value = first.Row.Value;
            if (value.Kind == ProductCostValueKind.Clear)
            {
                if (variant.CostPrice is null) views.Add(View(first.Row, variant, "UNCHANGED", "Maliyet zaten boş."));
                else { views.Add(View(first.Row, variant, "CLEAR", null)); changes.Add(new ProductCostChange(variant.VariantId, variant.Version, null)); }
            }
            else if (variant.CostPrice == value.Amount) views.Add(View(first.Row, variant, "UNCHANGED", null));
            else { views.Add(View(first.Row, variant, "UPDATE", null)); changes.Add(new ProductCostChange(variant.VariantId, variant.Version, value.Amount)); }
            foreach (var repeat in pending.Skip(1))
                views.Add(View(repeat.Row, variant, "SKIPPED", $"Aynı varyant {first.Row.RowNumber}. satırda aynı değerle var."));
        }

        int Count(string outcome) => views.Count(view => view.Outcome == outcome);
        var ordered = views.OrderBy(view => OutcomeRank(view.Outcome)).ThenBy(view => view.RowNumber).ToList();
        return new ProductCostImportPreview(
            fileName,
            rows.Count,
            Count("UPDATE"),
            Count("CLEAR"),
            Count("UNCHANGED"),
            skipped + Count("SKIPPED"),
            Count("UNMATCHED"),
            Count("AMBIGUOUS"),
            Count("ERROR"),
            ordered.Take(PreviewRowLimit).ToList(),
            ordered.Count > PreviewRowLimit,
            changes);
    }

    private static (ProductCostCandidate? Variant, string? Outcome, string? Message) Match(ProductCostFileRow row, Dictionary<string, ProductCostCandidate[]> bySku, Dictionary<string, ProductCostCandidate[]> byBarcode)
    {
        var skuKey = CatalogKeyNormalizer.Key(row.Sku);
        var barcodeKey = CatalogKeyNormalizer.Key(row.Barcode);
        if (skuKey.Length == 0 && barcodeKey.Length == 0) return (null, "ERROR", "Stok kodu ya da barkod yok.");
        var skuMatches = skuKey.Length > 0 && bySku.TryGetValue(skuKey, out var foundBySku) ? foundBySku : [];
        var barcodeMatches = barcodeKey.Length > 0 && byBarcode.TryGetValue(barcodeKey, out var foundByBarcode) ? foundByBarcode : [];
        if (skuMatches.Length > 1) return (null, "AMBIGUOUS", $"\"{row.Sku}\" stok kodu {skuMatches.Length} varyantta var.");
        if (skuMatches.Length == 1)
        {
            // Both codes given and both found, but on different variants: the row cannot say which one it means.
            if (barcodeMatches.Length == 1 && barcodeMatches[0].VariantId != skuMatches[0].VariantId)
                return (null, "ERROR", "Stok kodu ile barkod farklı varyantları gösteriyor.");
            return (skuMatches[0], null, null);
        }
        if (barcodeMatches.Length > 1) return (null, "AMBIGUOUS", $"\"{row.Barcode}\" barkodu {barcodeMatches.Length} varyantta var.");
        if (barcodeMatches.Length == 1) return (barcodeMatches[0], null, null);
        return (null, "UNMATCHED", skuKey.Length > 0 && barcodeKey.Length > 0 ? "Stok kodu ve barkod katalogda yok." : skuKey.Length > 0 ? "Stok kodu katalogda yok." : "Barkod katalogda yok.");
    }

    private static ProductCostImportRowView View(ProductCostFileRow row, ProductCostCandidate? variant, string outcome, string? message) => new(
        row.RowNumber,
        row.Sku,
        row.Barcode,
        variant?.VariantId,
        variant?.ProductTitle,
        variant?.VariantLabel,
        variant?.CostPrice,
        row.Value.Kind == ProductCostValueKind.Set ? row.Value.Amount : null,
        outcome,
        message);

    // Problems first so the seller sees them within the shown rows, then what will be written.
    private static int OutcomeRank(string outcome) => outcome switch
    {
        "ERROR" => 0,
        "AMBIGUOUS" => 1,
        "UNMATCHED" => 2,
        "UPDATE" => 3,
        "CLEAR" => 4,
        "UNCHANGED" => 5,
        _ => 6
    };
}
