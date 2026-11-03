using System.Globalization;
using System.Text.RegularExpressions;
using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Imports;

public enum ProductTransferValueKind { Empty, Set, Clear, Invalid }

// What a cell asks for: nothing (Empty), a value (Set: Amount rounded to two decimals, or Text for a text field),
// removing the stored value (Clear) or a value that cannot be used (Invalid, Error says why).
public readonly record struct ProductTransferValue(ProductTransferValueKind Kind, decimal? Amount, string? Error, string? Text = null)
{
    public static ProductTransferValue Empty => new(ProductTransferValueKind.Empty, null, null);
    public static ProductTransferValue Clear => new(ProductTransferValueKind.Clear, null, null);
    public static ProductTransferValue Set(decimal amount) => new(ProductTransferValueKind.Set, amount, null);
    public static ProductTransferValue SetText(string text) => new(ProductTransferValueKind.Set, null, null, text);
    public static ProductTransferValue Invalid(string error) => new(ProductTransferValueKind.Invalid, null, error);
}

// One store's two price cells on a row.
public sealed record ProductTransferStorePrice(Guid ConnectionId, ProductTransferValue Sale, ProductTransferValue List);

// CodeError: the row's stock code or barcode cannot be read (a formula), so the row is an error whatever it asks for.
// Barcode is the code the row is matched by; NewBarcode is the same cell read as a value to write (BARCODE field).
public sealed record ProductTransferFileRow(
    int RowNumber,
    string? Sku,
    string? Barcode,
    ProductTransferValue Cost,
    ProductTransferValue Desi,
    ProductTransferValue Stock = default,
    IReadOnlyList<ProductTransferStorePrice>? Prices = null,
    string? CodeError = null,
    ProductTransferValue Title = default,
    ProductTransferValue NewBarcode = default,
    ProductTransferValue ModelCode = default,
    ProductTransferValue PanelSale = default,
    ProductTransferValue PanelList = default,
    ProductTransferValue Vat = default)
{
    public IReadOnlyList<ProductTransferStorePrice> StorePrices => Prices ?? [];
}

// The rows of an uploaded file and the fields (ProductTransferFields) it has columns for.
public sealed record ProductTransferFile(IReadOnlyList<ProductTransferFileRow> Rows, IReadOnlyList<string> AvailableFields);

// A store's offer for a variant, as the catalog holds it now.
public sealed record ProductTransferOffer(Guid ConnectionId, Guid OfferId, long Version, decimal SalePrice, decimal ListPrice);

// A variant the uploaded rows can name, as the catalog holds it now. OnHand is null when it has no main stock record.
// PanelSalePrice / PanelListPrice are the variant's default prices, else its product's (what the panel price shows).
public sealed record ProductTransferCandidate(
    Guid VariantId,
    Guid ProductId,
    long Version,
    string Sku,
    string? Barcode,
    string ProductTitle,
    string? VariantLabel,
    decimal? CostPrice,
    decimal? Desi,
    decimal? OnHand = null,
    IReadOnlyList<ProductTransferOffer>? Offers = null,
    long ProductVersion = 1,
    string? ModelCode = null,
    decimal? PanelSalePrice = null,
    decimal? PanelListPrice = null,
    decimal? VatRate = null)
{
    public IReadOnlyList<ProductTransferOffer> StoreOffers => Offers ?? [];
}

// Reads the product export back (the .xlsx it was downloaded as, or a CSV saved from it), and the cost template of the
// earlier "Maliyet Excel". Columns are found by their header text, whatever the order: at least one of "Stok Kodu" and
// "Barkod", and at least one writable column (ProductTransferFields). Every other column is ignored.
public static partial class ProductTransferFileParser
{
    public const int MaximumRows = 20_000;
    public const decimal MaximumCost = 10_000_000m;
    public const decimal MaximumDesi = 10_000m;
    public const decimal MaximumStock = 1_000_000_000m;
    public const decimal MaximumPrice = 10_000_000m;
    public const int MaximumTitleLength = 320;
    public const int MaximumCodeLength = 160;
    public const string PanelSaleHeader = "Panel Satış Fiyatı";
    public const string PanelListHeader = "Panel Liste Fiyatı";

    private static readonly string[] TitleHeaders = ["URUNADI"];
    private static readonly string[] ModelCodeHeaders = ["MODELKODU"];
    private static readonly string[] PanelSaleHeaders = ["PANELSATISFIYATI"];
    private static readonly string[] PanelListHeaders = ["PANELLISTEFIYATI"];
    private static readonly string[] VatHeaders = ["KDVORANI", "KDV"];

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
        var title = Column(keys, TitleHeaders);
        var modelCode = Column(keys, ModelCodeHeaders);
        var panelSale = Column(keys, PanelSaleHeaders);
        var panelList = Column(keys, PanelListHeaders);
        var vat = Column(keys, VatHeaders);
        var storeColumns = stores
            .Select(store => (store.ConnectionId, Headers: StorePriceHeaders(store.Name)))
            .Select(store => (store.ConnectionId, Sale: Column(keys, [HeaderKey(store.Headers.Sale)]), List: Column(keys, [HeaderKey(store.Headers.List)])))
            .Where(store => store.Sale >= 0 || store.List >= 0)
            .ToList();
        var present = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            [ProductTransferFields.Title] = title >= 0,
            [ProductTransferFields.Barcode] = barcode >= 0,
            [ProductTransferFields.ModelCode] = modelCode >= 0,
            [ProductTransferFields.PanelPrices] = panelSale >= 0 || panelList >= 0,
            [ProductTransferFields.Vat] = vat >= 0,
            [ProductTransferFields.Stock] = stock >= 0,
            [ProductTransferFields.Desi] = desi >= 0,
            [ProductTransferFields.Cost] = cost >= 0,
            [ProductTransferFields.Prices] = storeColumns.Count > 0
        };
        var available = ProductTransferFields.All.Where(field => present[field]).ToList();
        if (sku < 0 && barcode < 0) throw new InvalidDataException("\"Stok Kodu\" ya da \"Barkod\" sütunu bulunamadı; Ravencia'dan indirilen ürün dosyasını kullanın.");
        // A barcode column is writable only beside a stock code column (a written barcode is matched by stock code).
        if (available.Count == 0 || available is [ProductTransferFields.Barcode] && sku < 0) throw new InvalidDataException("Aktarılabilecek bir sütun (ürün adı, model kodu, fiyat, KDV, stok, maliyet, desi ya da mağaza fiyatı) bulunamadı; Ravencia'dan indirilen ürün dosyasını kullanın.");

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
            var titleValue = Read(title, ParseTitle);
            var barcodeValue = Read(barcode, ParseBarcode);
            var modelCodeValue = Read(modelCode, ParseModelCode);
            var panelSaleValue = Read(panelSale, ParsePanelPrice);
            var panelListValue = Read(panelList, ParsePanelPrice);
            var vatValue = Read(vat, ParseVat);
            ProductTransferValue[] values = [costValue, desiValue, stockValue, titleValue, modelCodeValue, panelSaleValue, panelListValue, vatValue];
            if (skuText is null && barcodeText is null && values.All(value => value.Kind == ProductTransferValueKind.Empty) && prices.Count == 0) continue;
            result.Add(new ProductTransferFileRow(rowNumber, skuText, barcodeText, costValue, desiValue, stockValue, prices, codeFormula ? FormulaError.Error : null,
                titleValue, barcodeValue, modelCodeValue, panelSaleValue, panelListValue, vatValue));

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

    // The panel sale or list price in TL: above 0; it cannot be removed.
    public static ProductTransferValue ParsePanelPrice(string text, bool fromCsv)
    {
        var value = ParseNumber(text, fromCsv, "Panel fiyatı", MaximumPrice, "10.000.000 ₺", zeroAllowed: false);
        return value.Kind == ProductTransferValueKind.Clear ? ProductTransferValue.Invalid("Panel fiyatı silinemez; hücreyi boş bırakırsanız fiyat değişmez.") : value;
    }

    // The product card's VAT rate, as the export writes it ("%10") or a plain number: 0 to 100; it cannot be removed.
    public static ProductTransferValue ParseVat(string text, bool fromCsv)
    {
        var trimmed = text.Trim();
        var number = trimmed.StartsWith('%') ? trimmed[1..] : trimmed.EndsWith('%') ? trimmed[..^1] : trimmed;
        if (trimmed.Length > 0 && number.Trim().Length == 0) return ProductTransferValue.Invalid("KDV oranı için bir sayı yazın.");
        var value = ParseNumber(number, fromCsv, "KDV oranı", 100m, "100", zeroAllowed: true);
        return value.Kind == ProductTransferValueKind.Clear ? ProductTransferValue.Invalid("KDV oranı silinemez; hücreyi boş bırakırsanız değişmez.") : value;
    }

    // The product title: text up to 320 characters; it cannot be removed.
    public static ProductTransferValue ParseTitle(string text, bool fromCsv) =>
        ParseText(text, "Ürün adı", MaximumTitleLength, clearable: false, requireKey: false);

    // A barcode to write: up to 160 characters with at least one letter or digit; it cannot be removed.
    public static ProductTransferValue ParseBarcode(string text, bool fromCsv) =>
        ParseText(text, "Barkod", MaximumCodeLength, clearable: false, requireKey: true);

    // A model code: up to 160 characters; "-" or "sil" removes it.
    public static ProductTransferValue ParseModelCode(string text, bool fromCsv) =>
        ParseText(text, "Model kodu", MaximumCodeLength, clearable: true, requireKey: true);

    // Text that starts like a formula is refused in .xlsx too: the value would turn into one in a later CSV export.
    private static ProductTransferValue ParseText(string text, string label, int maximum, bool clearable, bool requireKey)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0) return ProductTransferValue.Empty;
        if (trimmed is "-" or "–" or "—" || CatalogKeyNormalizer.Key(trimmed) == "SIL")
            return clearable ? ProductTransferValue.Clear : ProductTransferValue.Invalid($"{label} silinemez; hücreyi boş bırakırsanız değişmez.");
        if (ImportFileReader.LooksLikeFormula(trimmed)) return ProductTransferValue.Invalid($"{label}: formül benzeri içerik kabul edilmez.");
        if (trimmed.Length > maximum) return ProductTransferValue.Invalid($"{label} en fazla {maximum} karakter olabilir.");
        if (requireKey && CatalogKeyNormalizer.Key(trimmed).Length == 0) return ProductTransferValue.Invalid($"{label} en az bir harf ya da rakam içermelidir.");
        return ProductTransferValue.SetText(trimmed);
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
            return ProductTransferValue.Invalid($"\"{trimmed}\" belirsiz: {AmbiguousThousandsAdvice}");
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

    // The same refusal for the CSV product import (ImportFileReader); text is the cell without spaces.
    internal static bool IsAmbiguousThousands(string text) => AmbiguousThousands().IsMatch(text);

    internal const string AmbiguousThousandsAdvice = "binlik ayıracı mı, ondalık mı? Binlik için 1234, ondalık için 1,234 yazın.";

    [GeneratedRegex(@"^-?(\d+(\.\d+)?|\.\d+)$")]
    private static partial Regex PlainNumber();

    [GeneratedRegex(@"^-?\d+(\.\d+)?[eE][-+]?\d+$")]
    private static partial Regex ExponentNumber();
}

/// Turns the parsed rows into the preview: which variant each row names (stock code first, then barcode; stock code
// only while the barcode is being written), what changes in the fields the seller picked, and why a row is left out.
// Pure, so the same rules hold in tests and on the server.
public static class ProductTransferImportPlanner
{
    public const int PreviewRowLimit = 200;

    private readonly record struct FieldPlan(string? Change, decimal? Value);
    private readonly record struct TextPlan(string? Change, string? Value);
    private readonly record struct PanelPlan(string? Change, decimal? Sale, decimal? List);

    private sealed record RowPlan(FieldPlan Cost, FieldPlan Desi, FieldPlan Stock, IReadOnlyList<ProductTransferPriceView> PriceViews, IReadOnlyList<ProductTransferPriceChange> PriceChanges, string? Error,
        TextPlan Title = default, TextPlan Barcode = default, TextPlan ModelCode = default, PanelPlan Panel = default, FieldPlan Vat = default)
    {
        public static readonly RowPlan None = new(default, default, default, [], [], null);
        public bool ChangesVariant => Cost.Change is not null || Desi.Change is not null || Stock.Change is not null || PriceChanges.Count > 0 || Barcode.Change is not null || ModelCode.Change is not null || Panel.Change is not null;
        public bool Changes => ChangesVariant || Title.Change is not null || Vat.Change is not null;
    }

    // Rows of one product that ask for different product-level values (title, VAT): the rows and the message they get.
    private sealed record ProductConflicts(Dictionary<Guid, string> Title, Dictionary<Guid, string> Vat);

    public static ProductTransferImportPreview Plan(string fileName, IReadOnlyList<ProductTransferFileRow> rows, IReadOnlyList<ProductTransferCandidate> variants, IReadOnlyCollection<string>? fields = null, IReadOnlyList<ProductTransferStoreView>? stores = null, IReadOnlyList<string>? availableFields = null)
    {
        var picked = (fields ?? [ProductTransferFields.Cost, ProductTransferFields.Desi]).ToHashSet(StringComparer.Ordinal);
        var storeNames = (stores ?? []).ToDictionary(store => store.ConnectionId, store => store.Name);
        var barcodePicked = picked.Contains(ProductTransferFields.Barcode);
        // A field the seller did not pick is read as if its column were empty.
        var masked = rows.Select(row => row with
        {
            Cost = picked.Contains(ProductTransferFields.Cost) ? row.Cost : ProductTransferValue.Empty,
            Desi = picked.Contains(ProductTransferFields.Desi) ? row.Desi : ProductTransferValue.Empty,
            Stock = picked.Contains(ProductTransferFields.Stock) ? row.Stock : ProductTransferValue.Empty,
            Prices = picked.Contains(ProductTransferFields.Prices) ? row.StorePrices : [],
            Title = picked.Contains(ProductTransferFields.Title) ? row.Title : ProductTransferValue.Empty,
            NewBarcode = barcodePicked ? row.NewBarcode : ProductTransferValue.Empty,
            ModelCode = picked.Contains(ProductTransferFields.ModelCode) ? row.ModelCode : ProductTransferValue.Empty,
            PanelSale = picked.Contains(ProductTransferFields.PanelPrices) ? row.PanelSale : ProductTransferValue.Empty,
            PanelList = picked.Contains(ProductTransferFields.PanelPrices) ? row.PanelList : ProductTransferValue.Empty,
            Vat = picked.Contains(ProductTransferFields.Vat) ? row.Vat : ProductTransferValue.Empty
        }).ToList();

        var bySku = variants.GroupBy(variant => CatalogKeyNormalizer.Key(variant.Sku)).Where(group => group.Key.Length > 0).ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var byBarcode = variants.Where(variant => !string.IsNullOrWhiteSpace(variant.Barcode)).GroupBy(variant => CatalogKeyNormalizer.Key(variant.Barcode)).Where(group => group.Key.Length > 0).ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);

        var resolved = new List<(ProductTransferFileRow Row, ProductTransferCandidate? Variant, string Outcome, string? Message)>(masked.Count);
        var skipped = 0;
        foreach (var row in masked)
        {
            if (IsEmpty(row)) { skipped++; continue; }
            var (variant, outcome, message) = Match(row, bySku, byBarcode, barcodePicked);
            if (variant is not null && InvalidMessage(row) is { } error) (outcome, message) = ("ERROR", error);
            resolved.Add((row, variant, outcome ?? "PENDING", message));
        }

        var pendingRows = resolved.Where(item => item.Outcome == "PENDING").Select(item => (item.Row, Variant: item.Variant!)).ToList();
        var conflicts = new ProductConflicts(
            Disagreements(pendingRows, row => row.Title, "ürün adı"),
            Disagreements(pendingRows, row => row.Vat, "KDV oranı"));
        // A barcode asked for by more than one variant in the file cannot be given to any of them.
        var barcodeClaims = pendingRows
            .Where(item => item.Row.NewBarcode.Kind == ProductTransferValueKind.Set && !string.Equals(item.Row.NewBarcode.Text, item.Variant.Barcode?.Trim(), StringComparison.Ordinal))
            .GroupBy(item => CatalogKeyNormalizer.Key(item.Row.NewBarcode.Text))
            .Where(group => group.Select(item => item.Variant.VariantId).Distinct().Count() > 1)
            .ToDictionary(group => group.Key, group => string.Join(", ", group.Select(item => item.Row.RowNumber)), StringComparer.Ordinal);

        // A variant named by more than one row is written only when every row asks for the same thing; otherwise none
        // of its rows is used, since which one the seller meant cannot be told.
        var views = new List<ProductTransferRowView>(resolved.Count);
        var changes = new List<ProductTransferChange>();
        var productChanges = new Dictionary<Guid, ProductTransferProductChange>();
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
            var plan = PlanRow(first.Row, variant, storeNames, byBarcode, barcodeClaims, conflicts);
            if (plan.Error is not null) views.Add(View(first.Row, variant, "ERROR", plan.Error, RowPlan.None));
            else if (!plan.Changes) views.Add(View(first.Row, variant, "UNCHANGED", null, plan));
            else
            {
                views.Add(View(first.Row, variant, "UPDATE", null, plan));
                if (plan.ChangesVariant)
                    changes.Add(new ProductTransferChange(
                        variant.VariantId,
                        variant.Version,
                        plan.Cost.Change is not null,
                        plan.Cost.Value,
                        plan.Desi.Change is not null,
                        plan.Desi.Value,
                        plan.Stock.Change is null ? null : new ProductTransferStockChange(variant.OnHand!.Value, plan.Stock.Value!.Value),
                        plan.PriceChanges.Count == 0 ? null : plan.PriceChanges,
                        plan.Barcode.Value,
                        plan.ModelCode.Change is not null,
                        plan.ModelCode.Value,
                        plan.Panel.Change is null ? null : new ProductTransferPanelPriceChange(plan.Panel.Sale!.Value, plan.Panel.List!.Value)));
                if (plan.Title.Change is not null || plan.Vat.Change is not null)
                {
                    var known = productChanges.GetValueOrDefault(variant.ProductId) ?? new ProductTransferProductChange(variant.ProductId, variant.ProductVersion, null, null);
                    productChanges[variant.ProductId] = known with { Title = plan.Title.Value ?? known.Title, VatRate = plan.Vat.Value ?? known.VatRate };
                }
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
            changes,
            productChanges.Values.ToList(),
            productChanges.Values.Count(change => change.Title is not null),
            changes.Count(change => change.Barcode is not null),
            changes.Count(change => change.SetModelCode && change.ModelCode is not null),
            changes.Count(change => change.SetModelCode && change.ModelCode is null),
            changes.Count(change => change.PanelPrice is not null),
            productChanges.Values.Count(change => change.VatRate is not null));
    }

    // The products whose rows ask for more than one value of a product-level field, with the message their rows get.
    private static Dictionary<Guid, string> Disagreements(IEnumerable<(ProductTransferFileRow Row, ProductTransferCandidate Variant)> rows, Func<ProductTransferFileRow, ProductTransferValue> value, string label) =>
        rows.Where(item => value(item.Row).Kind == ProductTransferValueKind.Set)
            .GroupBy(item => item.Variant.ProductId)
            .Where(group => group.Select(item => Key(value(item.Row))).Distinct(StringComparer.Ordinal).Count() > 1)
            .ToDictionary(group => group.Key, group => $"Aynı ürünün satırlarında farklı {label} var (satır {string.Join(", ", group.Select(item => item.Row.RowNumber))}); ürünün tüm satırlarına aynı değeri yazın.");

    private static bool IsEmpty(ProductTransferFileRow row) =>
        new[] { row.Cost, row.Desi, row.Stock, row.Title, row.NewBarcode, row.ModelCode, row.PanelSale, row.PanelList, row.Vat }.All(value => value.Kind == ProductTransferValueKind.Empty) && row.StorePrices.Count == 0;

    private static string Signature(ProductTransferFileRow row) =>
        string.Join("|", new[] { row.Cost, row.Desi, row.Stock, row.Title, row.NewBarcode, row.ModelCode, row.PanelSale, row.PanelList, row.Vat }.Select(Key).Concat(row.StorePrices.OrderBy(price => price.ConnectionId).Select(price => $"{price.ConnectionId:N}:{Key(price.Sale)}:{Key(price.List)}")));

    private static string Key(ProductTransferValue value) => $"{value.Kind}:{value.Amount?.ToString("0.##", CultureInfo.InvariantCulture)}:{value.Text}";

    private static RowPlan PlanRow(ProductTransferFileRow row, ProductTransferCandidate variant, IReadOnlyDictionary<Guid, string> storeNames, Dictionary<string, ProductTransferCandidate[]> byBarcode, Dictionary<string, string> barcodeClaims, ProductConflicts conflicts)
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

        // Product-level fields: every row of the product must ask for the same value.
        if (row.Title.Kind == ProductTransferValueKind.Set && conflicts.Title.TryGetValue(variant.ProductId, out var titleConflict)) return RowPlan.None with { Error = titleConflict };
        if (row.Vat.Kind == ProductTransferValueKind.Set && conflicts.Vat.TryGetValue(variant.ProductId, out var vatConflict)) return RowPlan.None with { Error = vatConflict };
        var title = row.Title.Kind == ProductTransferValueKind.Set && !string.Equals(row.Title.Text, variant.ProductTitle.Trim(), StringComparison.Ordinal)
            ? new TextPlan(ProductTransferFieldChange.Set, row.Title.Text) : default;
        var vat = row.Vat.Kind == ProductTransferValueKind.Set && (variant.VatRate is not decimal storedVat || Rounded(storedVat) != row.Vat.Amount)
            ? new FieldPlan(ProductTransferFieldChange.Set, row.Vat.Amount) : default;

        TextPlan barcode = default;
        if (row.NewBarcode.Kind == ProductTransferValueKind.Set && !string.Equals(row.NewBarcode.Text, variant.Barcode?.Trim(), StringComparison.Ordinal))
        {
            var key = CatalogKeyNormalizer.Key(row.NewBarcode.Text);
            if (barcodeClaims.TryGetValue(key, out var claimRows)) return RowPlan.None with { Error = $"\"{row.NewBarcode.Text}\" barkodu dosyada birden çok varyanta yazılmış (satır {claimRows})." };
            if (byBarcode.TryGetValue(key, out var holders) && holders.FirstOrDefault(holder => holder.VariantId != variant.VariantId) is { } holder)
                return RowPlan.None with { Error = $"\"{row.NewBarcode.Text}\" barkodu başka bir varyantta ({holder.Sku}) kayıtlı; bir barkod tek varyantta olabilir." };
            barcode = new TextPlan(ProductTransferFieldChange.Set, row.NewBarcode.Text);
        }
        var modelCode = row.ModelCode.Kind switch
        {
            ProductTransferValueKind.Set when !string.Equals(row.ModelCode.Text, variant.ModelCode?.Trim(), StringComparison.Ordinal) => new TextPlan(ProductTransferFieldChange.Set, row.ModelCode.Text),
            ProductTransferValueKind.Clear when !string.IsNullOrWhiteSpace(variant.ModelCode) => new TextPlan(ProductTransferFieldChange.Clear, null),
            _ => default
        };

        PanelPlan panel = default;
        if (row.PanelSale.Kind == ProductTransferValueKind.Set || row.PanelList.Kind == ProductTransferValueKind.Set)
        {
            var sale = row.PanelSale.Amount ?? (variant.PanelSalePrice is decimal storedSale ? Rounded(storedSale) : null);
            if (sale is not decimal newSale) return RowPlan.None with { Error = "Bu varyantın panel satış fiyatı yok; \"Panel Satış Fiyatı\" hücresini de doldurun." };
            // No list price stored or given: the list price is the sale price (shown in the preview before saving).
            var list = row.PanelList.Amount ?? (variant.PanelListPrice is decimal storedList ? Rounded(storedList) : newSale);
            if (list < newSale) return RowPlan.None with { Error = "Panel liste fiyatı panel satış fiyatından küçük olamaz." };
            var same = variant.PanelSalePrice is decimal currentSale && Rounded(currentSale) == newSale && variant.PanelListPrice is decimal currentList && Rounded(currentList) == list;
            if (!same) panel = new PanelPlan(ProductTransferFieldChange.Set, newSale, list);
        }
        return new RowPlan(cost, desi, stock, priceViews, priceChanges, null, title, barcode, modelCode, panel, vat);
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
        var values = new[] { row.Cost, row.Desi, row.Stock, row.Title, row.NewBarcode, row.ModelCode, row.PanelSale, row.PanelList, row.Vat }.Concat(row.StorePrices.SelectMany(price => new[] { price.Sale, price.List }));
        var errors = values.Where(value => value.Kind == ProductTransferValueKind.Invalid).Select(value => value.Error).Distinct().ToList();
        return errors.Count == 0 ? null : string.Join(" ", errors);
    }

    private static (ProductTransferCandidate? Variant, string? Outcome, string? Message) Match(ProductTransferFileRow row, Dictionary<string, ProductTransferCandidate[]> bySku, Dictionary<string, ProductTransferCandidate[]> byBarcode, bool skuOnly)
    {
        var skuKey = CatalogKeyNormalizer.Key(row.Sku);
        var barcodeKey = CatalogKeyNormalizer.Key(row.Barcode);
        if (row.CodeError is not null) return (null, "ERROR", row.CodeError);
        // The barcode is being written: it is a new value, not a code that names the variant.
        if (skuOnly)
        {
            if (skuKey.Length == 0) return (null, "ERROR", "Barkod aktarılırken satır Stok Kodu ile eşleştirilir; bu satırda stok kodu yok.");
            var matches = bySku.TryGetValue(skuKey, out var found) ? found : [];
            if (matches.Length > 1) return (null, "AMBIGUOUS", $"\"{row.Sku}\" stok kodu {matches.Length} varyantta var.");
            return matches.Length == 1 ? (matches[0], null, null) : (null, "UNMATCHED", "Stok kodu katalogda yok.");
        }
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
        message,
        row.Title.Kind == ProductTransferValueKind.Set ? row.Title.Text : null,
        plan.Title.Change,
        variant?.Barcode,
        row.NewBarcode.Kind == ProductTransferValueKind.Set ? row.NewBarcode.Text : null,
        plan.Barcode.Change,
        variant?.ModelCode,
        row.ModelCode.Kind == ProductTransferValueKind.Set ? row.ModelCode.Text : null,
        plan.ModelCode.Change,
        variant?.PanelSalePrice,
        plan.Panel.Change is null ? row.PanelSale.Amount : plan.Panel.Sale,
        variant?.PanelListPrice,
        plan.Panel.Change is null ? row.PanelList.Amount : plan.Panel.List,
        plan.Panel.Change,
        variant?.VatRate,
        row.Vat.Kind == ProductTransferValueKind.Set ? row.Vat.Amount : null,
        plan.Vat.Change);

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
