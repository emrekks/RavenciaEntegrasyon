namespace MarketplaceHub.Application;

// Products exported to a spreadsheet and imported back: one row per variant, with the catalog's codes and names and the
// fields an upload can write. The seller picks which of them an upload writes; the others are ignored even when the
// file has them. An upload is first previewed and only applied when the seller confirms.
//
// Rows are matched by "Stok Kodu" (the variant SKU), else by "Barkod". The stock code is the match key and is never
// written. When the barcode itself is picked to be written, a row is matched by its stock code only (a changed barcode
// cannot find its own variant) and a row without a stock code is refused, so a barcode is only ever changed on the
// variant the row names by its stock code.
//
// Writable fields:
//  - Ürün Bilgileri: product title (Product.Title, the same on every row of a product), barcode (unique in the tenant,
//    cannot be removed) and model code ("-" removes it). Written to the panel only; the product's marketplace links are
//    marked as carrying local changes, as an editor save does, and nothing is sent.
//  - Fiyat ve Vergi: the panel price ("Panel Satış Fiyatı" / "Panel Liste Fiyatı": the variant default prices the quick
//    edit's "Panel Ana Fiyatı" writes; offers that follow the panel price are not changed) and the product card's VAT
//    rate (Product.VatRate, the same on every row of a product). Neither starts a price send.
//  - Stok ve Lojistik: stock (the main location's on-hand stock, written the way the product editor writes it, so it
//    is projected to the stores with external writes on) and desi (ProductVariant.Desi).
//  - Maliyet: ProductVariant.CostPrice.
//  - Mağaza Fiyatları: each store's offer sale and list price, queued like a panel price edit (sent only where the
//    connection's price writes are on).
// The variant, brand, category and status columns are for reading only: they are names, not values a row can set
// without a lookup, and a status change starts or stops sales.
//
// Cell semantics, unchanged for the original fields: an empty cell changes nothing; "-" or "sil" removes a removable
// value (cost, desi, model code); a value that cannot be removed (stock, prices, VAT, title, barcode) refuses "-".
public sealed record ProductTransferSummaryView(int VariantCount, int MissingCostCount, int MissingDesiCount);

// The fields an upload can write.
public static class ProductTransferFields
{
    public const string Title = "TITLE";
    public const string Barcode = "BARCODE";
    public const string ModelCode = "MODEL_CODE";
    public const string PanelPrices = "PANEL_PRICES";
    public const string Vat = "VAT";
    public const string Cost = "COST";
    public const string Desi = "DESI";
    public const string Stock = "STOCK";
    public const string Prices = "PRICES";
    public static readonly IReadOnlyList<string> All = [Title, Barcode, ModelCode, PanelPrices, Vat, Cost, Desi, Stock, Prices];
}

// What an upload does to one field of a variant: SET writes the new value, CLEAR removes the stored one.
public static class ProductTransferFieldChange
{
    public const string Set = "SET";
    public const string Clear = "CLEAR";
}

// A store whose prices the export has as two columns ("<store> Satış Fiyatı", "<store> Liste Fiyatı").
public sealed record ProductTransferStoreView(Guid ConnectionId, string Name);

// One store's price change on a row.
public sealed record ProductTransferPriceView(Guid ConnectionId, string StoreName, decimal OldSalePrice, decimal NewSalePrice, decimal OldListPrice, decimal NewListPrice);

// Outcome of one uploaded row: UPDATE (a field changes; the *Change fields and Prices say which), UNCHANGED, SKIPPED
// (nothing new written), UNMATCHED (no variant has the code), AMBIGUOUS (the code names several variants) or ERROR (a
// value or the row cannot be used; Message says why). ProductTitle is the stored title, NewTitle the file's.
public sealed record ProductTransferRowView(
    int RowNumber,
    string? Sku,
    string? Barcode,
    Guid? VariantId,
    string? ProductTitle,
    string? VariantLabel,
    decimal? OldCost,
    decimal? NewCost,
    string? CostChange,
    decimal? OldDesi,
    decimal? NewDesi,
    string? DesiChange,
    decimal? OldStock,
    decimal? NewStock,
    string? StockChange,
    IReadOnlyList<ProductTransferPriceView> Prices,
    string Outcome,
    string? Message,
    string? NewTitle = null,
    string? TitleChange = null,
    string? OldBarcode = null,
    string? NewBarcode = null,
    string? BarcodeChange = null,
    string? OldModelCode = null,
    string? NewModelCode = null,
    string? ModelCodeChange = null,
    decimal? OldPanelSalePrice = null,
    decimal? NewPanelSalePrice = null,
    decimal? OldPanelListPrice = null,
    decimal? NewPanelListPrice = null,
    string? PanelPriceChange = null,
    decimal? OldVatRate = null,
    decimal? NewVatRate = null,
    string? VatChange = null);

// A stock change against the on-hand stock the preview read.
public sealed record ProductTransferStockChange(decimal ExpectedOnHand, decimal OnHand);

// A store price change against the offer version the preview read.
public sealed record ProductTransferPriceChange(Guid OfferId, long Version, decimal SalePrice, decimal ListPrice);

// The panel price (variant default sale and list price) a change writes.
public sealed record ProductTransferPanelPriceChange(decimal SalePrice, decimal ListPrice);

// A change the preview proposes. Cost, desi, barcode, model code and panel price carry the variant version they were
// read at, stock the on-hand stock and a price its offer version: applying checks each, so a value changed meanwhile
// (product editor, another upload, an order) is never overwritten. Cost, desi and model code are written only when
// their Set flag is true; then null clears them. A barcode is written when Barcode is not null.
public sealed record ProductTransferChange(
    Guid VariantId,
    long Version,
    bool SetCost,
    decimal? Cost,
    bool SetDesi,
    decimal? Desi,
    ProductTransferStockChange? Stock = null,
    IReadOnlyList<ProductTransferPriceChange>? Prices = null,
    string? Barcode = null,
    bool SetModelCode = false,
    string? ModelCode = null,
    ProductTransferPanelPriceChange? PanelPrice = null)
{
    public bool WritesVariant() => SetCost || SetDesi || Barcode is not null || SetModelCode || PanelPrice is not null;
}

// A product-level change (title, VAT rate) against the product version the preview read. Null leaves a field alone.
public sealed record ProductTransferProductChange(Guid ProductId, long Version, string? Title, decimal? VatRate);

public sealed record ProductTransferImportPreview(
    string FileName,
    IReadOnlyList<string> AvailableFields,
    IReadOnlyList<string> Fields,
    IReadOnlyList<ProductTransferStoreView> Stores,
    int TotalRows,
    int Updates,
    int CostUpdates,
    int CostClears,
    int DesiUpdates,
    int DesiClears,
    int StockUpdates,
    int PriceUpdates,
    int Unchanged,
    int Skipped,
    int Unmatched,
    int Ambiguous,
    int Errors,
    IReadOnlyList<ProductTransferRowView> Rows,
    bool RowsTruncated,
    IReadOnlyList<ProductTransferChange> Changes,
    IReadOnlyList<ProductTransferProductChange>? ProductChanges = null,
    int TitleUpdates = 0,
    int BarcodeUpdates = 0,
    int ModelCodeUpdates = 0,
    int ModelCodeClears = 0,
    int PanelPriceUpdates = 0,
    int VatUpdates = 0);

public sealed record ApplyProductTransferCommand(IReadOnlyList<ProductTransferChange>? Changes, IReadOnlyList<ProductTransferProductChange>? ProductChanges = null);

public sealed record ProductTransferApplyResult(
    int Variants,
    int CostsUpdated,
    int CostsCleared,
    int DesiUpdated,
    int DesiCleared,
    int StocksUpdated,
    int PricesUpdated,
    int Products,
    int TitlesUpdated = 0,
    int BarcodesUpdated = 0,
    int ModelCodesUpdated = 0,
    int ModelCodesCleared = 0,
    int PanelPricesUpdated = 0,
    int VatUpdated = 0);

// Which variants an export lists: every one, or only those still missing a cost or a desi.
public enum ProductTransferExportFilter { All, MissingCost, MissingDesi }

public interface IProductTransferService
{
    Task<ServiceResult<ProductTransferSummaryView>> SummaryAsync(Guid tenantId, CancellationToken cancellationToken);
    Task<ServiceResult<byte[]>> ExportAsync(Guid tenantId, ProductTransferExportFilter filter, Guid? brandId, Guid? categoryId, CancellationToken cancellationToken);
    Task<ServiceResult<ProductTransferImportPreview>> PreviewAsync(Guid tenantId, string fileName, Stream content, IReadOnlyCollection<string> fields, CancellationToken cancellationToken);
    Task<ServiceResult<ProductTransferApplyResult>> ApplyAsync(Guid tenantId, Guid userId, ApplyProductTransferCommand command, string correlationId, CancellationToken cancellationToken);
}
