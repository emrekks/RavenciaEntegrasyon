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
    bool MatchesPanel);

public sealed record CommissionTariffRowPage(IReadOnlyList<CommissionTariffRowView> Items, int TotalCount, int Page, int PageSize);

// The rate a sale pays by the tariff in force at `At`, from the band its commission-basis price falls in.
public sealed record CommissionTariffMatch(decimal Rate, int Tier, Guid TariffId, DateTimeOffset PeriodStart, DateTimeOffset PeriodEnd);

public interface ICommissionTariffService
{
    Task<ServiceResult<CommissionTariffUploadResult>> UploadAsync(Guid tenantId, Guid? userId, Guid connectionId, string fileName, Stream content, string correlationId, CancellationToken cancellationToken);
    Task<ServiceResult<IReadOnlyList<CommissionTariffView>>> ListAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken);
    Task<ServiceResult<CommissionTariffRowPage>> RowsAsync(Guid tenantId, Guid connectionId, Guid tariffId, string? search, int page, int pageSize, CancellationToken cancellationToken);
    Task<ServiceResult<bool>> DeleteAsync(Guid tenantId, Guid? userId, Guid connectionId, Guid tariffId, string correlationId, CancellationToken cancellationToken);
}

public interface ICommissionTariffLookup
{
    // The barcode is matched first, the seller SKU when no row has the barcode. Null when no uploaded tariff covers
    // `at` for the product or its price falls in no band: the commission is then unknown, not estimated.
    Task<CommissionTariffMatch?> RateAsync(Guid tenantId, Guid connectionId, string? barcode, string? sellerSku, DateTimeOffset at, decimal basisPrice, CancellationToken cancellationToken);
}
