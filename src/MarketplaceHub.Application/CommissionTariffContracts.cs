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
    string? ImageUrl = null,
    // The colour of the panel variant with the row's barcode (else seller SKU), from its options ("Renk: Bej"); the table
    // groups a model's rows by it.
    string? Color = null,
    // The stock the file gives (STOK); null in a tariff uploaded before it was read.
    decimal? Stock = null);

// What one unit sold at a price pays besides its commission, costed as the order profit report costs an order Trendyol
// has not settled yet: the product cost, the store's current cargo tariff, the service fee, the withholding tax (1 % of
// the VAT-free price) and the net VAT (the sale's VAT less the VAT in the cost, the commission, the cargo and the fee;
// negative when the expenses carry more). VatRate: the product's VAT rate the sale and the cost are taken at.
public sealed record UnitProfitCostsView(decimal Cost, decimal Cargo, decimal ServiceFee, decimal Stoppage, decimal NetVat, decimal VatRate);

// One band as a price the seller can pick (CommissionTariffBands.TargetPrice), its commission and the profit the price
// leaves after the commission and Costs (null without a cost, the product's VAT rate or the store's cargo tariff).
// RecommendedTier: the band that leaves the most.
public sealed record CommissionChoiceView(int Tier, decimal Price, decimal Rate, decimal Commission, decimal? Remaining, UnitProfitCostsView? Costs = null);

// The seller's picks for a tariff's Excel: a pick for every row, and row by row picks over it (a band, a price of their
// own, or neither: that row left out). The pick for every row is one band ("tier" with Tier), or the band leaving the
// most ("amount") or the largest profit on its cost ("rate"); with MinRate (per cent of the cost) or MinAmount (lira)
// a row whose pick leaves less is left out, with MaxRate or MaxAmount one whose pick leaves more.
public sealed record CommissionTariffExportRequest(string? Bulk, int? Tier, IReadOnlyList<CommissionTariffChoice>? Choices, decimal? MinRate = null, decimal? MinAmount = null,
    decimal? MaxRate = null, decimal? MaxAmount = null);

// Which rows a page lists: by the band their price is in now (1-4), whether the panel has their cost, and by the file's
// own name, barcode (or seller SKU), model code, stock range, category and brand. Sort orders the models by their name
// ("name"), their lowest current price ("price") or the most a band leaves after commission and cost ("tier1"-"tier4");
// without it the models run by name as before. ProfitTier with a least or most profit (lira) or profit on cost (per
// cent) keeps the variants whose price in that band leaves that much (Fiyat Aralığı).
public sealed record CommissionTariffRowFilter(int? Tier, bool? HasCost, string? Name = null, string? Barcode = null, string? ModelCode = null,
    decimal? MinStock = null, decimal? MaxStock = null, string? Category = null, string? Brand = null, string? Sort = null, bool Descending = false,
    int? ProfitTier = null, decimal? MinProfit = null, decimal? MaxProfit = null, decimal? MinShare = null, decimal? MaxShare = null)
{
    public bool FiltersProfit => ProfitTier is not null && (MinProfit ?? MaxProfit ?? MinShare ?? MaxShare) is not null;
}

// The categories and brands a tariff's rows carry, for the list's filters.
public sealed record CommissionTariffFacets(IReadOnlyList<string> Categories, IReadOnlyList<string> Brands);
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
    Task<ServiceResult<CommissionTariffFacets>> FacetsAsync(Guid tenantId, Guid connectionId, Guid tariffId, CancellationToken cancellationToken);
    Task<ServiceResult<CommissionTariffExportFile>> ExportAsync(Guid tenantId, Guid connectionId, Guid tariffId, CommissionTariffExportRequest request, CancellationToken cancellationToken);
}

public interface ICommissionTariffLookup
{
    // The barcode is matched first, the seller SKU when no row has the barcode. Null when no uploaded tariff covers
    // `at` for the product or its price falls in no band: the commission is then unknown, not estimated.
    Task<CommissionTariffMatch?> RateAsync(Guid tenantId, Guid connectionId, string? barcode, string? sellerSku, DateTimeOffset at, decimal basisPrice, CancellationToken cancellationToken);
}
