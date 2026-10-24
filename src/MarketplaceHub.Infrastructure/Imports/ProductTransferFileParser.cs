using System.Globalization;
using System.Text.RegularExpressions;
using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Imports;

public enum ProductTransferValueKind { Empty, Set, Clear, Invalid }

// What a cell asks for: nothing (Empty), a value (Set, rounded to two decimals), removing the stored value (Clear) or
// a value that cannot be used (Invalid, Error says why).
public readonly record struct ProductTransferValue(ProductTransferValueKind Kind, decimal? Amount, string? Error)
{
    public static ProductTransferValue Empty => new(ProductTransferValueKind.Empty, null, null);
    public static ProductTransferValue Clear => new(ProductTransferValueKind.Clear, null, null);
    public static ProductTransferValue Set(decimal amount) => new(ProductTransferValueKind.Set, amount, null);
    public static ProductTransferValue Invalid(string error) => new(ProductTransferValueKind.Invalid, null, error);
}

// One store's two price cells on a row.
public sealed record ProductTransferStorePrice(Guid ConnectionId, ProductTransferValue Sale, ProductTransferValue List);

// CodeError: the row's stock code or barcode cannot be read (a formula), so the row is an error whatever it asks for.
public sealed record ProductTransferFileRow(int RowNumber, string? Sku, string? Barcode, ProductTransferValue Cost, ProductTransferValue Desi, ProductTransferValue Stock = default, IReadOnlyList<ProductTransferStorePrice>? Prices = null, string? CodeError = null)
{
    public IReadOnlyList<ProductTransferStorePrice> StorePrices => Prices ?? [];
}

// The rows of an uploaded file and the fields (ProductTransferFields) it has columns for.
public sealed record ProductTransferFile(IReadOnlyList<ProductTransferFileRow> Rows, IReadOnlyList<string> AvailableFields);

// A store's offer for a variant, as the catalog holds it now.
public sealed record ProductTransferOffer(Guid ConnectionId, Guid OfferId, long Version, decimal SalePrice, decimal ListPrice);

// A variant the uploaded rows can name, as the catalog holds it now. OnHand is null when it has no main stock record.
public sealed record ProductTransferCandidate(Guid VariantId, Guid ProductId, long Version, string Sku, string? Barcode, string ProductTitle, string? VariantLabel, decimal? CostPrice, decimal? Desi, decimal? OnHand = null, IReadOnlyList<ProductTransferOffer>? Offers = null)
{
    public IReadOnlyList<ProductTransferOffer> StoreOffers => Offers ?? [];
}

// Reads the product export back (the .xlsx it was downloaded as, or a CSV saved from it), and the cost template of the
// earlier "Maliyet Excel". Columns are found by their header text, whatever the order: at least one of "Stok Kodu" and
// "Barkod", and at least one of "Maliyet", "Desi", "Stok" and a store's price columns. Every other column is ignored.
public static partial class ProductTransferFileParser
{
    public const int MaximumRows = 20_000;
    public const decimal MaximumCost = 10_000_000m;
    public const decimal MaximumDesi = 10_000m;
    public const decimal MaximumStock = 1_000_000_000m;
    public const decimal MaximumPrice = 10_000_000m;

    private static readonly string[] SkuHeaders = ["STOKKODU", "SKU", "STOKKODUSKU", "SATICISTOKKODU", "URUNSTOKKODU"];
    private static readonly string[] BarcodeHeaders = ["BARKOD", "BARCODE", "URUNBARKODU"];
    // The old cost template had "Güncel Maliyet" (never read) beside "Yeni Maliyet"; the export has a single "Maliyet".
    private static readonly string[] NewCostHeaders = ["YENIMALIYET", "YENIMALIYETTL"];
    private static readonly string[] CostHeaders = ["MALIYET", "MALIYETTL", "BIRIMMALIYET"];
    private static readonly string[] NewDesiHeaders = ["YENIDESI"];
    private static readonly string[] DesiHeaders = ["DESI"];
    private static readonly string[] StockHeaders = ["STOK", "STOKADEDI", "STOKMIKTARI", "ELDEKISTOK"];

    // The two price columns of a store, as the export names them.
    public static (string Sale, string List) StorePriceHeaders(string storeName) => ($"{storeName} Satış Fiyatı", $"{storeName} Liste Fiyatı");

    public static ProductTransferFile ParseXlsx(Stream stream, long maximumExpandedBytes, IReadOnlyList<ProductTransferStoreView>? stores = null)
    {
        var table = ImportFileReader.ReadXlsxTable(stream, maximumExpandedBytes);
        return Parse(table.Headers, table.Rows.Select(row => (row.RowNumber, row.Cells, (IReadOnlyCollection<int>)row.FormulaColumns)), fromCsv: false, stores ?? []);
    }

    public static ProductTransferFile ParseCsv(Stream stream, IReadOnlyList<ProductTransferStoreView>? stores = null)
    {
        var text = ImportFileReader.DecodeCsv(stream);
        using var records = ImportFileReader.ParseCsvRecords(text, ImportFileReader.SniffDelimiter(text)).GetEnumerator();
        if (!records.MoveNext()) throw new InvalidDataException("Dosyada başlık satırı yok.");
        var headers = records.Current.Cells;
        return Parse(headers, Rest(records), fromCsv: true, stores ?? []);

        static IEnumerable<(int, IReadOnlyList<string>, IReadOnlyCollection<int>)> Rest(IEnumerator<(int RowNumber, List<string> Cells)> records)
        {
            while (records.MoveNext())
            {
                if (records.Current.Cells.All(string.IsNullOrWhiteSpace)) continue;
                yield return (records.Current.RowNumber, records.Current.Cells, Array.Empty<int>());
            }
        }
    }

    internal static ProductTransferFile Parse(IReadOnlyList<string> headers, IEnumerable<(int RowNumber, IReadOnlyList<string> Cells, IReadOnlyCollection<int> FormulaColumns)> rows, bool fromCsv, IReadOnlyList<ProductTransferStoreView> stores)
    {
        var keys = headers.Select(HeaderKey).ToArray();
        var sku = Column(keys, SkuHeaders);
        var barcode = Column(keys, BarcodeHeaders);
        var cost = Column(keys, NewCostHeaders);
        if (cost < 0) cost = Column(keys, CostHeaders);
        var desi = Column(keys, NewDesiHeaders);
        if (desi < 0) desi = Column(keys, DesiHeaders);
        var stock = Column(keys, StockHeaders);
        var storeColumns = stores
            .Select(store => (store.ConnectionId, Headers: StorePriceHeaders(store.Name)))
            .Select(store => (store.ConnectionId, Sale: Column(keys, [HeaderKey(store.Headers.Sale)]), List: Column(keys, [HeaderKey(store.Headers.List)])))
            .Where(store => store.Sale >= 0 || store.List >= 0)
            .ToList();
        var available = new List<string>();
        if (cost >= 0) available.Add(ProductTransferFields.Cost);
        if (desi >= 0) available.Add(ProductTransferFields.Desi);
        if (stock >= 0) available.Add(ProductTransferFields.Stock);
        if (storeColumns.Count > 0) available.Add(ProductTransferFields.Prices);
        if (available.Count == 0) throw new InvalidDataException("\"Maliyet\", \"Desi\", \"Stok\" ya da mağaza fiyatı sütunu bulunamadı; Ravencia'dan indirilen ürün dosyasını kullanın.");
        if (sku < 0 && barcode < 0) throw new InvalidDataException("\"Stok Kodu\" ya da \"Barkod\" sütunu bulunamadı; Ravencia'dan indirilen ürün dosyasını kullanın.");

        var result = new List<ProductTransferFileRow>();
        foreach (var (rowNumber, cells, formulas) in rows)
        {
            if (result.Count >= MaximumRows) throw new InvalidDataException($"Dosyada en fazla {MaximumRows:N0} satır olabilir; dosyayı bölerek yükleyin.");
            var skuText = Cell(cells, sku);
            var barcodeText = Cell(cells, barcode);
            // A formula's result is not read back (the reader never evaluates one): the seller pastes values instead.
            var codeFormula = sku >= 0 && formulas.Contains(sku) || barcode >= 0 && formulas.Contains(barcode);
            var costValue = Read(cost, ParseCost);
            var desiValue = Read(desi, ParseDesi);
            var stockValue = Read(stock, ParseStock);
            var prices = storeColumns
                .Select(store => new ProductTransferStorePrice(store.ConnectionId, Read(store.Sale, ParsePrice), Read(store.List, ParsePrice)))
                .Where(price => price.Sale.Kind != ProductTransferValueKind.Empty || price.List.Kind != ProductTransferValueKind.Empty)
                .ToList();
            if (skuText is null && barcodeText is null && costValue.Kind == ProductTransferValueKind.Empty && desiValue.Kind == ProductTransferValueKind.Empty && stockValue.Kind == ProductTransferValueKind.Empty && prices.Count == 0) continue;
            result.Add(new ProductTransferFileRow(rowNumber, skuText, barcodeText, costValue, desiValue, stockValue, prices, codeFormula ? FormulaError.Error : null));

            ProductTransferValue Read(int column, Func<string, bool, ProductTransferValue> parse) =>
                column < 0 ? ProductTransferValue.Empty
                : formulas.Contains(column) ? FormulaError
                : parse(Cell(cells, column) ?? string.Empty, fromCsv);
        }
        return new ProductTransferFile(result, available);
    }

    private static ProductTransferValue FormulaError => ProductTransferValue.Invalid("Formül içeren hücre okunmaz; değerleri \"Değerleri Yapıştır\" ile yazın.");

    // Cost in TL; 0 is a valid cost.
    public static ProductTransferValue ParseCost(string text, bool fromCsv) =>
        ParseNumber(text, fromCsv, "Maliyet", MaximumCost, "10.000.000 ₺", zeroAllowed: true);

    // Desi (volumetric weight); 0 is not one, as the product editor stores 0 as "no desi".
    public static ProductTransferValue ParseDesi(string text, bool fromCsv) =>
        ParseNumber(text, fromCsv, "Desi", MaximumDesi, "10.000", zeroAllowed: false);

    // On-hand stock: a whole number, 0 included; it cannot be removed.
    public static ProductTransferValue ParseStock(string text, bool fromCsv)
    {
        var value = ParseNumber(text, fromCsv, "Stok", MaximumStock, "1.000.000.000", zeroAllowed: true);
        if (value.Kind == ProductTransferValueKind.Clear) return ProductTransferValue.Invalid("Stok silinemez; stok yoksa 0 yazın.");
        if (value.Amount is decimal amount && amount != decimal.Truncate(amount)) return ProductTransferValue.Invalid("Stok tam sayı olmalıdır.");
        return value;
    }

    // A store's sale or list price in TL: above 0; it cannot be removed.
    public static ProductTransferValue ParsePrice(string text, bool fromCsv)
    {
        var value = ParseNumber(text, fromCsv, "Fiyat", MaximumPrice, "10.000.000 ₺", zeroAllowed: false);
        return value.Kind == ProductTransferValueKind.Clear ? ProductTransferValue.Invalid("Mağaza fiyatı silinemez; hücreyi boş bırakırsanız fiyat değişmez.") : value;
    }

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
        if (amount == 0 && !zeroAllowed) return ProductTransferValue.Invalid($"{label} 0'dan büyük olmalıdır.");
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

// Turns the parsed rows into the preview: which variant each row names (stock code first, then barcode), what changes
// in the fields the seller picked, and why a row is left out. Pure, so the same rules hold in tests and on the server.
public static class ProductTransferImportPlanner
{
    public const int PreviewRowLimit = 200;

    private readonly record struct FieldPlan(string? Change, decimal? Value);

    private sealed record RowPlan(FieldPlan Cost, FieldPlan Desi, FieldPlan Stock, IReadOnlyList<ProductTransferPriceView> PriceViews, IReadOnlyList<ProductTransferPriceChange> PriceChanges, string? Error)
    {
        public static readonly RowPlan None = new(default, default, default, [], [], null);
        public bool Changes => Cost.Change is not null || Desi.Change is not null || Stock.Change is not null || PriceChanges.Count > 0;
    }

    public static ProductTransferImportPreview Plan(string fileName, IReadOnlyList<ProductTransferFileRow> rows, IReadOnlyList<ProductTransferCandidate> variants, IReadOnlyCollection<string>? fields = null, IReadOnlyList<ProductTransferStoreView>? stores = null, IReadOnlyList<string>? availableFields = null)
    {
        var picked = (fields ?? [ProductTransferFields.Cost, ProductTransferFields.Desi]).ToHashSet(StringComparer.Ordinal);
        var storeNames = (stores ?? []).ToDictionary(store => store.ConnectionId, store => store.Name);
        // A field the seller did not pick is read as if its column were empty.
        var masked = rows.Select(row => row with
        {
            Cost = picked.Contains(ProductTransferFields.Cost) ? row.Cost : ProductTransferValue.Empty,
            Desi = picked.Contains(ProductTransferFields.Desi) ? row.Desi : ProductTransferValue.Empty,
            Stock = picked.Contains(ProductTransferFields.Stock) ? row.Stock : ProductTransferValue.Empty,
            Prices = picked.Contains(ProductTransferFields.Prices) ? row.StorePrices : []
        }).ToList();

        var bySku = variants.GroupBy(variant => CatalogKeyNormalizer.Key(variant.Sku)).Where(group => group.Key.Length > 0).ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var byBarcode = variants.Where(variant => !string.IsNullOrWhiteSpace(variant.Barcode)).GroupBy(variant => CatalogKeyNormalizer.Key(variant.Barcode)).Where(group => group.Key.Length > 0).ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);

        var resolved = new List<(ProductTransferFileRow Row, ProductTransferCandidate? Variant, string Outcome, string? Message)>(masked.Count);
        var skipped = 0;
        foreach (var row in masked)
        {
            if (IsEmpty(row)) { skipped++; continue; }
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
                views.AddRange(items.Select(item => View(item.Row, item.Variant, item.Outcome, item.Message, RowPlan.None)));
                continue;
            }
            var pending = items.Where(item => item.Outcome == "PENDING").ToList();
            var lines = string.Join(", ", items.Select(item => item.Row.RowNumber));
            var distinct = pending.Select(item => Signature(item.Row)).Distinct(StringComparer.Ordinal).Count();
            if (items.Count > 1 && (distinct > 1 || pending.Count != items.Count))
            {
                views.AddRange(items.Select(item => View(item.Row, item.Variant, "ERROR", item.Outcome == "ERROR" ? item.Message : $"Bu varyant dosyada birden çok satırda farklı değerle var (satır {lines}).", RowPlan.None)));
                continue;
            }
            var first = pending[0];
            var variant = first.Variant!;
            var plan = PlanRow(first.Row, variant, storeNames);
            if (plan.Error is not null) views.Add(View(first.Row, variant, "ERROR", plan.Error, RowPlan.None));
            else if (!plan.Changes) views.Add(View(first.Row, variant, "UNCHANGED", null, plan));
            else
            {
                views.Add(View(first.Row, variant, "UPDATE", null, plan));
                changes.Add(new ProductTransferChange(
                    variant.VariantId,
                    variant.Version,
                    plan.Cost.Change is not null,
                    plan.Cost.Value,
                    plan.Desi.Change is not null,
                    plan.Desi.Value,
                    plan.Stock.Change is null ? null : new ProductTransferStockChange(variant.OnHand!.Value, plan.Stock.Value!.Value),
                    plan.PriceChanges.Count == 0 ? null : plan.PriceChanges));
            }
            foreach (var repeat in pending.Skip(1))
                views.Add(View(repeat.Row, variant, "SKIPPED", $"Aynı varyant {first.Row.RowNumber}. satırda aynı değerle var.", RowPlan.None));
        }

        int Count(string outcome) => views.Count(view => view.Outcome == outcome);
        var ordered = views.OrderBy(view => OutcomeRank(view.Outcome)).ThenBy(view => view.RowNumber).ToList();
        return new ProductTransferImportPreview(
            fileName,
            availableFields ?? ProductTransferFields.All,
            ProductTransferFields.All.Where(picked.Contains).ToList(),
            stores ?? [],
            rows.Count,
            Count("UPDATE"),
            views.Count(view => view.CostChange == ProductTransferFieldChange.Set),
            views.Count(view => view.CostChange == ProductTransferFieldChange.Clear),
            views.Count(view => view.DesiChange == ProductTransferFieldChange.Set),
            views.Count(view => view.DesiChange == ProductTransferFieldChange.Clear),
            views.Count(view => view.StockChange == ProductTransferFieldChange.Set),
            views.Sum(view => view.Prices.Count),
            Count("UNCHANGED"),
            skipped + Count("SKIPPED"),
            Count("UNMATCHED"),
            Count("AMBIGUOUS"),
            Count("ERROR"),
            ordered.Take(PreviewRowLimit).ToList(),
            ordered.Count > PreviewRowLimit,
            changes);
    }

    private static bool IsEmpty(ProductTransferFileRow row) =>
        row.Cost.Kind == ProductTransferValueKind.Empty && row.Desi.Kind == ProductTransferValueKind.Empty && row.Stock.Kind == ProductTransferValueKind.Empty && row.StorePrices.Count == 0;

    private static string Signature(ProductTransferFileRow row) =>
        string.Join("|", new[] { row.Cost, row.Desi, row.Stock }.Select(Key).Concat(row.StorePrices.OrderBy(price => price.ConnectionId).Select(price => $"{price.ConnectionId:N}:{Key(price.Sale)}:{Key(price.List)}")));

    private static string Key(ProductTransferValue value) => $"{value.Kind}:{value.Amount?.ToString("0.##", CultureInfo.InvariantCulture)}";

    private static RowPlan PlanRow(ProductTransferFileRow row, ProductTransferCandidate variant, IReadOnlyDictionary<Guid, string> storeNames)
    {
        var cost = PlanField(row.Cost, variant.CostPrice);
        var desi = PlanField(row.Desi, variant.Desi);
        FieldPlan stock = default;
        if (row.Stock.Kind == ProductTransferValueKind.Set)
        {
            if (variant.OnHand is not decimal onHand) return RowPlan.None with { Error = "Bu varyantın stok kaydı yok; stoğu ürün sayfasından girin." };
            if (onHand != row.Stock.Amount) stock = new(ProductTransferFieldChange.Set, row.Stock.Amount);
        }
        var priceViews = new List<ProductTransferPriceView>();
        var priceChanges = new List<ProductTransferPriceChange>();
        foreach (var price in row.StorePrices)
        {
            var store = storeNames.GetValueOrDefault(price.ConnectionId) ?? "Mağaza";
            var offer = variant.StoreOffers.FirstOrDefault(item => item.ConnectionId == price.ConnectionId);
            if (offer is null) return RowPlan.None with { Error = $"{store} mağazasında bu varyantın fiyat kaydı yok; fiyatı ürün sayfasından ekleyin." };
            var sale = price.Sale.Amount ?? offer.SalePrice;
            var list = price.List.Amount ?? offer.ListPrice;
            if (list < sale) return RowPlan.None with { Error = $"{store}: liste fiyatı satış fiyatından küçük olamaz." };
            if (Rounded(offer.SalePrice) == sale && Rounded(offer.ListPrice) == list) continue;
            priceViews.Add(new ProductTransferPriceView(price.ConnectionId, store, offer.SalePrice, sale, offer.ListPrice, list));
            priceChanges.Add(new ProductTransferPriceChange(offer.OfferId, offer.Version, sale, list));
        }
        return new RowPlan(cost, desi, stock, priceViews, priceChanges, null);
    }

    // A stored value with more than two decimals (the product editor keeps four) is the same as the file's value when it
    // rounds to it: the export writes it rounded, and reading it back must not propose a change.
    private static FieldPlan PlanField(ProductTransferValue value, decimal? stored) => value.Kind switch
    {
        ProductTransferValueKind.Set when stored is not decimal current || Rounded(current) != value.Amount => new(ProductTransferFieldChange.Set, value.Amount),
        ProductTransferValueKind.Clear when stored is not null => new(ProductTransferFieldChange.Clear, null),
        _ => default
    };

    private static decimal Rounded(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    private static string? InvalidMessage(ProductTransferFileRow row)
    {
        var values = new[] { row.Cost, row.Desi, row.Stock }.Concat(row.StorePrices.SelectMany(price => new[] { price.Sale, price.List }));
        var errors = values.Where(value => value.Kind == ProductTransferValueKind.Invalid).Select(value => value.Error).Distinct().ToList();
        return errors.Count == 0 ? null : string.Join(" ", errors);
    }

    private static (ProductTransferCandidate? Variant, string? Outcome, string? Message) Match(ProductTransferFileRow row, Dictionary<string, ProductTransferCandidate[]> bySku, Dictionary<string, ProductTransferCandidate[]> byBarcode)
    {
        var skuKey = CatalogKeyNormalizer.Key(row.Sku);
        var barcodeKey = CatalogKeyNormalizer.Key(row.Barcode);
        if (row.CodeError is not null) return (null, "ERROR", row.CodeError);
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

    private static ProductTransferRowView View(ProductTransferFileRow row, ProductTransferCandidate? variant, string outcome, string? message, RowPlan plan) => new(
        row.RowNumber,
        row.Sku,
        row.Barcode,
        variant?.VariantId,
        variant?.ProductTitle,
        variant?.VariantLabel,
        variant?.CostPrice,
        row.Cost.Kind == ProductTransferValueKind.Set ? row.Cost.Amount : null,
        plan.Cost.Change,
        variant?.Desi,
        row.Desi.Kind == ProductTransferValueKind.Set ? row.Desi.Amount : null,
        plan.Desi.Change,
        variant?.OnHand,
        row.Stock.Kind == ProductTransferValueKind.Set ? row.Stock.Amount : null,
        plan.Stock.Change,
        plan.PriceViews,
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
