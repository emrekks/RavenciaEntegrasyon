namespace MarketplaceHub.Application;

// Products exported to a spreadsheet and imported back: one row per variant, with the catalog's codes, names, price,
// stock and VAT for reference and the two fields the upload can change, cost (ProductVariant.CostPrice, the product
// card's "Maliyet": what one unit costs the seller, in TL) and desi (ProductVariant.Desi, which the reports use for the
// cargo estimate). An upload is first previewed and only applied when the seller confirms. Price and stock are not
// imported: a stock change is projected to the marketplaces, and nothing here is sent to a marketplace.
public sealed record ProductTransferSummaryView(int VariantCount, int MissingCostCount, int MissingDesiCount);

// What an upload does to one field of a variant: SET writes the new value, CLEAR removes the stored one.
public static class ProductTransferFieldChange
{
    public const string Set = "SET";
    public const string Clear = "CLEAR";
}

// Outcome of one uploaded row: UPDATE (cost or desi changes; CostChange and DesiChange say which), UNCHANGED, SKIPPED
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
    string Outcome,
    string? Message);

// A change the preview proposes, with the variant version it was read at: applying it again checks that version, so a
// value changed meanwhile (product editor, another upload) is never overwritten. A field is written only when its Set
// flag is true; then null clears it.
public sealed record ProductTransferChange(Guid VariantId, long Version, bool SetCost, decimal? Cost, bool SetDesi, decimal? Desi);

public sealed record ProductTransferImportPreview(
    string FileName,
    int TotalRows,
    int Updates,
    int CostUpdates,
    int CostClears,
    int DesiUpdates,
    int DesiClears,
    int Unchanged,
    int Skipped,
    int Unmatched,
    int Ambiguous,
    int Errors,
    IReadOnlyList<ProductTransferRowView> Rows,
    bool RowsTruncated,
    IReadOnlyList<ProductTransferChange> Changes);

public sealed record ApplyProductTransferCommand(IReadOnlyList<ProductTransferChange>? Changes);

public sealed record ProductTransferApplyResult(int Variants, int CostsUpdated, int CostsCleared, int DesiUpdated, int DesiCleared, int Products);

// Which variants an export lists: every one, or only those still missing a cost or a desi.
public enum ProductTransferExportFilter { All, MissingCost, MissingDesi }

public interface IProductTransferService
{
    Task<ServiceResult<ProductTransferSummaryView>> SummaryAsync(Guid tenantId, CancellationToken cancellationToken);
    Task<ServiceResult<byte[]>> ExportAsync(Guid tenantId, ProductTransferExportFilter filter, Guid? brandId, Guid? categoryId, CancellationToken cancellationToken);
    Task<ServiceResult<ProductTransferImportPreview>> PreviewAsync(Guid tenantId, string fileName, Stream content, CancellationToken cancellationToken);
    Task<ServiceResult<ProductTransferApplyResult>> ApplyAsync(Guid tenantId, Guid userId, ApplyProductTransferCommand command, string correlationId, CancellationToken cancellationToken);
}
