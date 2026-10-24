using System.Globalization;
using System.Text.RegularExpressions;
using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Imports;

public enum ProductTransferValueKind { Empty, Set, Clear, Invalid }

// What a cost or desi cell asks for: nothing (Empty), a value (Set, rounded to two decimals), removing the stored value
// (Clear) or a value that cannot be used (Invalid, Error says why).
public readonly record struct ProductTransferValue(ProductTransferValueKind Kind, decimal? Amount, string? Error)
{
    public static ProductTransferValue Empty => new(ProductTransferValueKind.Empty, null, null);
    public static ProductTransferValue Clear => new(ProductTransferValueKind.Clear, null, null);
    public static ProductTransferValue Set(decimal amount) => new(ProductTransferValueKind.Set, amount, null);
    public static ProductTransferValue Invalid(string error) => new(ProductTransferValueKind.Invalid, null, error);
}

public sealed record ProductTransferFileRow(int RowNumber, string? Sku, string? Barcode, ProductTransferValue Cost, ProductTransferValue Desi);

// A variant the uploaded rows can name, as the catalog holds it now.
public sealed record ProductTransferCandidate(Guid VariantId, Guid ProductId, long Version, string Sku, string? Barcode, string ProductTitle, string? VariantLabel, decimal? CostPrice, decimal? Desi);

// Reads the product export back (the .xlsx it was downloaded as, or a CSV saved from it), and the cost template of the
// earlier "Maliyet Excel". Columns are found by their header text, whatever the order: at least one of "Stok Kodu" and
// "Barkod", and at least one of "Maliyet" and "Desi". Every other column (price, stock, VAT, names) is ignored.
public static partial class ProductTransferFileParser
{
    public const int MaximumRows = 20_000;
    public const decimal MaximumCost = 10_000_000m;
    public const decimal MaximumDesi = 10_000m;

    private static readonly string[] SkuHeaders = ["STOKKODU", "SKU", "STOKKODUSKU", "SATICISTOKKODU", "URUNSTOKKODU"];
    private static readonly string[] BarcodeHeaders = ["BARKOD", "BARCODE", "URUNBARKODU"];
    // The old cost template had "Güncel Maliyet" (never read) beside "Yeni Maliyet"; the export has a single "Maliyet".
    private static readonly string[] NewCostHeaders = ["YENIMALIYET", "YENIMALIYETTL"];
    private static readonly string[] CostHeaders = ["MALIYET", "MALIYETTL", "BIRIMMALIYET"];
    private static readonly string[] NewDesiHeaders = ["YENIDESI"];
    private static readonly string[] DesiHeaders = ["DESI"];

    public static IReadOnlyList<ProductTransferFileRow> ParseXlsx(Stream stream, long maximumExpandedBytes)
    {
        var table = ImportFileReader.ReadXlsxTable(stream, maximumExpandedBytes);
        return Parse(table.Headers, table.Rows.Select(row => (row.RowNumber, row.Cells, (IReadOnlyCollection<int>)row.FormulaColumns)), fromCsv: false);
    }

    public static IReadOnlyList<ProductTransferFileRow> ParseCsv(Stream stream)
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

    internal static IReadOnlyList<ProductTransferFileRow> Parse(IReadOnlyList<string> headers, IEnumerable<(int RowNumber, IReadOnlyList<string> Cells, IReadOnlyCollection<int> FormulaColumns)> rows, bool fromCsv)
    {
        var keys = headers.Select(HeaderKey).ToArray();
        var sku = Column(keys, SkuHeaders);
        var barcode = Column(keys, BarcodeHeaders);
        var cost = Column(keys, NewCostHeaders);
        if (cost < 0) cost = Column(keys, CostHeaders);
        var desi = Column(keys, NewDesiHeaders);
        if (desi < 0) desi = Column(keys, DesiHeaders);
        if (cost < 0 && desi < 0) throw new InvalidDataException("\"Maliyet\" ya da \"Desi\" sütunu bulunamadı; Ravencia'dan indirilen ürün dosyasını kullanın.");
        if (sku < 0 && barcode < 0) throw new InvalidDataException("\"Stok Kodu\" ya da \"Barkod\" sütunu bulunamadı; Ravencia'dan indirilen ürün dosyasını kullanın.");

        var result = new List<ProductTransferFileRow>();
        foreach (var (rowNumber, cells, formulas) in rows)
        {
            if (result.Count >= MaximumRows) throw new InvalidDataException($"Dosyada en fazla {MaximumRows:N0} satır olabilir; dosyayı bölerek yükleyin.");
            var skuText = Cell(cells, sku);
            var barcodeText = Cell(cells, barcode);
            // A formula's result is not read back (the reader never evaluates one): the seller pastes values instead.
            var costValue = Read(cost, ParseCost);
            var desiValue = Read(desi, ParseDesi);
            if (sku >= 0 && formulas.Contains(sku) || barcode >= 0 && formulas.Contains(barcode)) costValue = FormulaError;
            if (skuText is null && barcodeText is null && costValue.Kind == ProductTransferValueKind.Empty && desiValue.Kind == ProductTransferValueKind.Empty) continue;
            result.Add(new ProductTransferFileRow(rowNumber, skuText, barcodeText, costValue, desiValue));

            ProductTransferValue Read(int column, Func<string, bool, ProductTransferValue> parse) =>
                column < 0 ? ProductTransferValue.Empty
                : formulas.Contains(column) ? FormulaError
                : parse(Cell(cells, column) ?? string.Empty, fromCsv);
        }
        return result;
    }

    private static ProductTransferValue FormulaError => ProductTransferValue.Invalid("Formül içeren hücre okunmaz; değerleri \"Değerleri Yapıştır\" ile yazın.");

    // Cost in TL; 0 is a valid cost.
    public static ProductTransferValue ParseCost(string text, bool fromCsv) =>
        ParseNumber(text, fromCsv, "Maliyet", MaximumCost, "10.000.000 ₺", zeroAllowed: true);

    // Desi (volumetric weight); 0 is not one, as the product editor stores 0 as "no desi".
    public static ProductTransferValue ParseDesi(string text, bool fromCsv) =>
        ParseNumber(text, fromCsv, "Desi", MaximumDesi, "10.000", zeroAllowed: false);

    // "123,45", "123.45", "1.234,56", "1234", "₺ 99,90", "99,90 TL" are numbers; "-" or "sil" removes the value; an empty
    // cell asks for nothing. "1.234" alone is refused: in Turkish it is one thousand two hundred thirty-four, elsewhere a
    // little over one, and a guess either way writes a wrong value.
    private static ProductTransferValue ParseNumber(string text, bool fromCsv, string label, decimal maximum, string maximumText, bool zeroAllowed)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0) return ProductTransferValue.Empty;
        if (trimmed is "-" or "–" or "—" || CatalogKeyNormalizer.Key(trimmed) == "SIL") return ProductTransferValue.Clear;
        if (fromCsv && ImportFileReader.LooksLikeFormula(trimmed)) return ProductTransferValue.Invalid("Formül benzeri içerik kabul edilmez.");

        var cleaned = trimmed.Replace("₺", string.Empty, StringComparison.Ordinal);
        if (cleaned.EndsWith("TL", StringComparison.OrdinalIgnoreCase)) cleaned = cleaned[..^2];
        cleaned = string.Concat(cleaned.Where(character => !char.IsWhiteSpace(character)));
        decimal amount;
        if (TurkishNumber().IsMatch(cleaned))
            amount = decimal.Parse(cleaned.Replace(".", string.Empty, StringComparison.Ordinal).Replace(',', '.'), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
        else if (AmbiguousThousands().IsMatch(cleaned))
            return ProductTransferValue.Invalid($"\"{trimmed}\" belirsiz: binlik ayıracı mı, ondalık mı? Binlik için 1234, ondalık için 1,234 yazın.");
        else if (PlainNumber().IsMatch(cleaned))
            amount = decimal.Parse(cleaned, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
        // A number cell Excel stores in exponent form (1E-3) never comes from a CSV.
        else if (!fromCsv && ExponentNumber().IsMatch(cleaned) && decimal.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out var exponent))
            amount = exponent;
        else
            return ProductTransferValue.Invalid($"{label} için \"{Short(trimmed)}\" bir sayı değil.");

        if (amount < 0) return ProductTransferValue.Invalid($"{label} negatif olamaz.");
        amount = Math.Round(amount, 2, MidpointRounding.AwayFromZero);
        if (amount > maximum) return ProductTransferValue.Invalid($"{label} {maximumText} değerini aşamaz.");
        if (amount == 0 && !zeroAllowed) return ProductTransferValue.Invalid($"{label} 0'dan büyük olmalıdır; silmek için - yazın.");
        return ProductTransferValue.Set(amount);
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
public static class ProductTransferImportPlanner
{
    public const int PreviewRowLimit = 200;

    private readonly record struct FieldPlan(string? Change, decimal? Value);

    public static ProductTransferImportPreview Plan(string fileName, IReadOnlyList<ProductTransferFileRow> rows, IReadOnlyList<ProductTransferCandidate> variants)
    {
        var bySku = variants.GroupBy(variant => CatalogKeyNormalizer.Key(variant.Sku)).Where(group => group.Key.Length > 0).ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var byBarcode = variants.Where(variant => !string.IsNullOrWhiteSpace(variant.Barcode)).GroupBy(variant => CatalogKeyNormalizer.Key(variant.Barcode)).Where(group => group.Key.Length > 0).ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);

        var resolved = new List<(ProductTransferFileRow Row, ProductTransferCandidate? Variant, string Outcome, string? Message)>(rows.Count);
        var skipped = 0;
        foreach (var row in rows)
        {
            if (row.Cost.Kind == ProductTransferValueKind.Empty && row.Desi.Kind == ProductTransferValueKind.Empty) { skipped++; continue; }
            var (variant, outcome, message) = Match(row, bySku, byBarcode);
            if (variant is not null && InvalidMessage(row) is { } error) (outcome, message) = ("ERROR", error);
            resolved.Add((row, variant, outcome ?? "PENDING", message));
        }

        // A variant named by more than one row is written only when every row asks for the same thing; otherwise none
        // of its rows is used, since which one the seller meant cannot be told.
        var views = new List<ProductTransferRowView>(resolved.Count);
        var changes = new List<ProductTransferChange>();
        foreach (var group in resolved.GroupBy(item => item.Variant?.VariantId ?? Guid.Empty))
        {
            var items = group.ToList();
            if (group.Key == Guid.Empty || items.All(item => item.Outcome != "PENDING"))
            {
                views.AddRange(items.Select(item => View(item.Row, item.Variant, item.Outcome, item.Message, default, default)));
                continue;
            }
            var pending = items.Where(item => item.Outcome == "PENDING").ToList();
            var lines = string.Join(", ", items.Select(item => item.Row.RowNumber));
            var distinct = pending.Select(item => (item.Row.Cost, item.Row.Desi)).Distinct().Count();
            if (items.Count > 1 && (distinct > 1 || pending.Count != items.Count))
            {
                views.AddRange(items.Select(item => View(item.Row, item.Variant, "ERROR", item.Outcome == "ERROR" ? item.Message : $"Bu varyant dosyada birden çok satırda farklı değerle var (satır {lines}).", default, default)));
                continue;
            }
            var first = pending[0];
            var variant = first.Variant!;
            var cost = PlanField(first.Row.Cost, variant.CostPrice);
            var desi = PlanField(first.Row.Desi, variant.Desi);
            if (cost.Change is null && desi.Change is null) views.Add(View(first.Row, variant, "UNCHANGED", null, cost, desi));
            else
            {
                views.Add(View(first.Row, variant, "UPDATE", null, cost, desi));
                changes.Add(new ProductTransferChange(variant.VariantId, variant.Version, cost.Change is not null, cost.Value, desi.Change is not null, desi.Value));
            }
            foreach (var repeat in pending.Skip(1))
                views.Add(View(repeat.Row, variant, "SKIPPED", $"Aynı varyant {first.Row.RowNumber}. satırda aynı değerle var.", default, default));
        }

        int Count(string outcome) => views.Count(view => view.Outcome == outcome);
        var ordered = views.OrderBy(view => OutcomeRank(view.Outcome)).ThenBy(view => view.RowNumber).ToList();
        return new ProductTransferImportPreview(
            fileName,
            rows.Count,
            Count("UPDATE"),
            views.Count(view => view.CostChange == ProductTransferFieldChange.Set),
            views.Count(view => view.CostChange == ProductTransferFieldChange.Clear),
            views.Count(view => view.DesiChange == ProductTransferFieldChange.Set),
            views.Count(view => view.DesiChange == ProductTransferFieldChange.Clear),
            Count("UNCHANGED"),
            skipped + Count("SKIPPED"),
            Count("UNMATCHED"),
            Count("AMBIGUOUS"),
            Count("ERROR"),
            ordered.Take(PreviewRowLimit).ToList(),
            ordered.Count > PreviewRowLimit,
            changes);
    }

    // A stored value with more than two decimals (the product editor keeps four) is the same as the file's value when it
    // rounds to it: the export writes it rounded, and reading it back must not propose a change.
    private static FieldPlan PlanField(ProductTransferValue value, decimal? stored) => value.Kind switch
    {
        ProductTransferValueKind.Set when stored is not decimal current || Math.Round(current, 2, MidpointRounding.AwayFromZero) != value.Amount => new(ProductTransferFieldChange.Set, value.Amount),
        ProductTransferValueKind.Clear when stored is not null => new(ProductTransferFieldChange.Clear, null),
        _ => default
    };

    private static string? InvalidMessage(ProductTransferFileRow row)
    {
        var errors = new[] { row.Cost, row.Desi }.Where(value => value.Kind == ProductTransferValueKind.Invalid).Select(value => value.Error).Distinct().ToList();
        return errors.Count == 0 ? null : string.Join(" ", errors);
    }

    private static (ProductTransferCandidate? Variant, string? Outcome, string? Message) Match(ProductTransferFileRow row, Dictionary<string, ProductTransferCandidate[]> bySku, Dictionary<string, ProductTransferCandidate[]> byBarcode)
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

    private static ProductTransferRowView View(ProductTransferFileRow row, ProductTransferCandidate? variant, string outcome, string? message, FieldPlan cost, FieldPlan desi) => new(
        row.RowNumber,
        row.Sku,
        row.Barcode,
        variant?.VariantId,
        variant?.ProductTitle,
        variant?.VariantLabel,
        variant?.CostPrice,
        row.Cost.Kind == ProductTransferValueKind.Set ? row.Cost.Amount : null,
        cost.Change,
        variant?.Desi,
        row.Desi.Kind == ProductTransferValueKind.Set ? row.Desi.Amount : null,
        desi.Change,
        outcome,
        message);

    // Problems first so the seller sees them within the shown rows, then what will be written.
    private static int OutcomeRank(string outcome) => outcome switch
    {
        "ERROR" => 0,
        "AMBIGUOUS" => 1,
        "UNMATCHED" => 2,
        "UPDATE" => 3,
        "UNCHANGED" => 4,
        _ => 5
    };
}
