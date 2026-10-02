namespace MarketplaceHub.Infrastructure.Adapters.Hepsiburada;

public sealed class HepsiburadaOptions
{
    public const string SectionName = "Hepsiburada";
    // Keep non-production calls closed by default. Production compose explicitly
    // opts into the marketplace endpoints' documented HTTP Basic authentication.
    public string AuthenticationMode { get; init; } = "UNVERIFIED";
    public Uri StageOmsBaseAddress { get; init; } = new("https://oms-external-sit.hepsiburada.com/");
    public Uri StageTestOrderBaseAddress { get; init; } = new("https://oms-stub-external-sit.hepsiburada.com/");
    public Uri ProductionOmsBaseAddress { get; init; } = new("https://oms-external.hepsiburada.com/");
    public Uri StageListingBaseAddress { get; init; } = new("https://listing-external-sit.hepsiburada.com/");
    public Uri ProductionListingBaseAddress { get; init; } = new("https://listing-external.hepsiburada.com/");
    public Uri StageCatalogBaseAddress { get; init; } = new("https://mpop-sit.hepsiburada.com/product/");
    // Hepsiburada's catalog guide says production endpoints are the SIT endpoint
    // with "-sit" removed. Deployments can override this for assigned hosts.
    public Uri? ProductionCatalogBaseAddress { get; init; } = new("https://mpop.hepsiburada.com/product/");
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
    public int PageSize { get; init; } = 10;
}
