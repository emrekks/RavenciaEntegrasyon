namespace MarketplaceHub.Application;

// Marketplace commission tariffs the seller uploads (Trendyol's weekly "komisyon tarifesi" export): the price bands and
// the commission rate of each band for a period. They tell which rate a sale pays when the order itself does not say,
// and which price earns a lower rate. Nothing is sent to the marketplace.
public static class CommissionTariffPlatforms
{
    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "TRENDYOL" };
    public static bool Supports(string? platformCode) => platformCode is not null && Supported.Contains(platformCode);
}

public sealed record CommissionTariffView(
    Guid Id,
    string PlatformCode,
    DateTimeOffset PeriodStart,
    DateTimeOffset PeriodEnd,
    string PeriodText,
    string FileName,
    DateTimeOffset UploadedAt,
    int RowCount,
    int MatchedRowCount,
    bool IsCurrent);

public sealed record CommissionTariffUploadResult(IReadOnlyList<CommissionTariffView> Tariffs, int ImportedRows, int SkippedRows, bool AlreadyUploaded, IReadOnlyList<string> Warnings);

public sealed record CommissionBandView(int Tier, decimal? Min, decimal? Max, decimal Rate);

// CurrentTier / CurrentBandRate: the band the row's commission-basis price falls in by the tariff's own limits (null when
// it falls in none). NextLowerBand: the nearest band with a lower rate and the highest price that still earns it.
public sealed record CommissionTariffRowView(
    Guid Id,
    string Barcode,
    string? SellerSku,
    string? ModelCode,
    string? ProductName,
    string? Size,
    decimal? BasisPrice,
    decimal? CurrentSalePrice,
    decimal? CurrentRate,
    int? CurrentTier,
    decimal? CurrentBandRate,
    IReadOnlyList<CommissionBandView> Bands,
    CommissionBandView? NextLowerBand,
    bool MatchesPanel,
    decimal? CostPrice = null,
    IReadOnlyList<CommissionChoiceView>? Choices = null,
    int? RecommendedTier = null,
    string? ImageUrl = null);

// One band as a price the seller can pick (CommissionTariffBands.TargetPrice), its commission and what is left of the
// price after the commission and the product's cost (null without a cost; cargo, service fee and withholding tax are not
// per product and are not taken off). RecommendedTier: the band that leaves the most.
public sealed record CommissionChoiceView(int Tier, decimal Price, decimal Rate, decimal Commission, decimal? Remaining);

// The seller's picks for a tariff's Excel: a pick for every row, and row by row picks over it (a band, a price of their
// own, or neither: that row left out). The pick for every row is one band ("tier" with Tier), or the band leaving the
// most ("amount") or the largest share of the price ("rate"); with MinRate (per cent of the price) or MinAmount (lira)
// a row whose pick leaves less is left out.
public sealed record CommissionTariffExportRequest(string? Bulk, int? Tier, IReadOnlyList<CommissionTariffChoice>? Choices, decimal? MinRate = null, decimal? MinAmount = null);

// Which rows a page lists: by the band their price is in now (1-4) and whether the panel has their cost.
public sealed record CommissionTariffRowFilter(int? Tier, bool? HasCost);
public sealed record CommissionTariffChoice(Guid RowId, int? Tier, decimal? Price);
public sealed record CommissionTariffExportFile(byte[] Content, string FileName, int Rows);

// A page of models: Items are every variant of the page's models; TotalCount and PageSize count models.
public sealed record CommissionTariffRowPage(IReadOnlyList<CommissionTariffRowView> Items, int TotalCount, int Page, int PageSize);

// The rate a sale pays by the tariff in force at `At`, from the band its commission-basis price falls in.
public sealed record CommissionTariffMatch(decimal Rate, int Tier, Guid TariffId, DateTimeOffset PeriodStart, DateTimeOffset PeriodEnd);

public interface ICommissionTariffService
{
    Task<ServiceResult<CommissionTariffUploadResult>> UploadAsync(Guid tenantId, Guid? userId, Guid connectionId, string fileName, Stream content, string correlationId, CancellationToken cancellationToken);
    Task<ServiceResult<IReadOnlyList<CommissionTariffView>>> ListAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken);
    Task<ServiceResult<CommissionTariffRowPage>> RowsAsync(Guid tenantId, Guid connectionId, Guid tariffId, string? search, int page, int pageSize, CommissionTariffRowFilter filter, CancellationToken cancellationToken);
    Task<ServiceResult<bool>> DeleteAsync(Guid tenantId, Guid? userId, Guid connectionId, Guid tariffId, string correlationId, CancellationToken cancellationToken);
    Task<ServiceResult<CommissionTariffExportFile>> ExportAsync(Guid tenantId, Guid connectionId, Guid tariffId, CommissionTariffExportRequest request, CancellationToken cancellationToken);
}

public interface ICommissionTariffLookup
{
    // The barcode is matched first, the seller SKU when no row has the barcode. Null when no uploaded tariff covers
    // `at` for the product or its price falls in no band: the commission is then unknown, not estimated.
    Task<CommissionTariffMatch?> RateAsync(Guid tenantId, Guid connectionId, string? barcode, string? sellerSku, DateTimeOffset at, decimal basisPrice, CancellationToken cancellationToken);
}
