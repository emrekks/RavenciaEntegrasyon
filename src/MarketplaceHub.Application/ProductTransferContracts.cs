namespace MarketplaceHub.Application;

// Products exported to a spreadsheet and imported back: one row per variant, with the catalog's codes and names for
// reference and the fields an upload can write: cost (ProductVariant.CostPrice, the product card's "Maliyet"), desi
// (ProductVariant.Desi, which the reports use for the cargo estimate), stock (the main location's on-hand stock) and
// each store's price (its ChannelOffer's sale and list price). The seller picks which of them an upload writes; the
// others are ignored even when the file has them. An upload is first previewed and only applied when the seller
// confirms. Stock is written the way the product editor writes it, so it is projected to the stores with external
// writes on; prices are written to the offers only and start no price send of their own.
public sealed record ProductTransferSummaryView(int VariantCount, int MissingCostCount, int MissingDesiCount);

// The fields an upload can write.
public static class ProductTransferFields
{
    public const string Cost = "COST";
    public const string Desi = "DESI";
    public const string Stock = "STOCK";
    public const string Prices = "PRICES";
    public static readonly IReadOnlyList<string> All = [Cost, Desi, Stock, Prices];
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
// value or the row cannot be used; Message says why).
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
    string? Message);

// A stock change against the on-hand stock the preview read.
public sealed record ProductTransferStockChange(decimal ExpectedOnHand, decimal OnHand);

// A store price change against the offer version the preview read.
public sealed record ProductTransferPriceChange(Guid OfferId, long Version, decimal SalePrice, decimal ListPrice);

// A change the preview proposes. Cost and desi carry the variant version they were read at, stock the on-hand stock and
// a price its offer version: applying checks each, so a value changed meanwhile (product editor, another upload, an
// order) is never overwritten. Cost and desi are written only when their Set flag is true; then null clears them.
public sealed record ProductTransferChange(
    Guid VariantId,
    long Version,
    bool SetCost,
    decimal? Cost,
    bool SetDesi,
    decimal? Desi,
    ProductTransferStockChange? Stock = null,
    IReadOnlyList<ProductTransferPriceChange>? Prices = null);

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
    IReadOnlyList<ProductTransferChange> Changes);

public sealed record ApplyProductTransferCommand(IReadOnlyList<ProductTransferChange>? Changes);

public sealed record ProductTransferApplyResult(int Variants, int CostsUpdated, int CostsCleared, int DesiUpdated, int DesiCleared, int StocksUpdated, int PricesUpdated, int Products);

// Which variants an export lists: every one, or only those still missing a cost or a desi.
public enum ProductTransferExportFilter { All, MissingCost, MissingDesi }

public interface IProductTransferService
{
    Task<ServiceResult<ProductTransferSummaryView>> SummaryAsync(Guid tenantId, CancellationToken cancellationToken);
    Task<ServiceResult<byte[]>> ExportAsync(Guid tenantId, ProductTransferExportFilter filter, Guid? brandId, Guid? categoryId, CancellationToken cancellationToken);
    Task<ServiceResult<ProductTransferImportPreview>> PreviewAsync(Guid tenantId, string fileName, Stream content, IReadOnlyCollection<string> fields, CancellationToken cancellationToken);
    Task<ServiceResult<ProductTransferApplyResult>> ApplyAsync(Guid tenantId, Guid userId, ApplyProductTransferCommand command, string correlationId, CancellationToken cancellationToken);
}
