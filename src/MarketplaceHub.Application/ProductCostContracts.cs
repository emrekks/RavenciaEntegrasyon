namespace MarketplaceHub.Application;

// Product costs (ProductVariant.CostPrice, the product card's "Maliyet": what one unit costs the seller, in TL) entered
// in bulk from a spreadsheet: a template with one row per variant, then an upload that is first previewed and only
// applied when the seller confirms. Nothing is sent to a marketplace.
public sealed record ProductCostSummaryView(int VariantCount, int MissingCostCount);

// Outcome of one uploaded row: UPDATE (the cost changes), UNCHANGED, CLEAR (the cost is removed), SKIPPED (no new cost
// written), UNMATCHED (no variant has the code), AMBIGUOUS (the code names several variants) or ERROR (the value or the
// row cannot be used; Message says why).
public sealed record ProductCostImportRowView(
    int RowNumber,
    string? Sku,
    string? Barcode,
    Guid? VariantId,
    string? ProductTitle,
    string? VariantLabel,
    decimal? OldCost,
    decimal? NewCost,
    string Outcome,
    string? Message);

// A change the preview proposes, with the variant version it was read at: applying it again checks that version, so a
// cost changed meanwhile (product editor, another upload) is never overwritten. Cost null clears the cost.
public sealed record ProductCostChange(Guid VariantId, long Version, decimal? Cost);

public sealed record ProductCostImportPreview(
    string FileName,
    int TotalRows,
    int Updates,
    int Clears,
    int Unchanged,
    int Skipped,
    int Unmatched,
    int Ambiguous,
    int Errors,
    IReadOnlyList<ProductCostImportRowView> Rows,
    bool RowsTruncated,
    IReadOnlyList<ProductCostChange> Changes);

public sealed record ApplyProductCostsCommand(IReadOnlyList<ProductCostChange>? Changes);

public sealed record ProductCostApplyResult(int Updated, int Cleared, int Products);

public interface IProductCostService
{
    Task<ServiceResult<ProductCostSummaryView>> SummaryAsync(Guid tenantId, CancellationToken cancellationToken);
    Task<ServiceResult<byte[]>> TemplateAsync(Guid tenantId, bool missingOnly, Guid? brandId, Guid? categoryId, CancellationToken cancellationToken);
    Task<ServiceResult<ProductCostImportPreview>> PreviewAsync(Guid tenantId, string fileName, Stream content, CancellationToken cancellationToken);
    Task<ServiceResult<ProductCostApplyResult>> ApplyAsync(Guid tenantId, Guid userId, ApplyProductCostsCommand command, string correlationId, CancellationToken cancellationToken);
}
