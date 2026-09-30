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
    // The catalog API publishes its SIT MPOP host. Its production host follows
    // Hepsiburada's documented MPOP convention of removing "-sit"; deployments
    // can override this when an account is assigned another host.
    public Uri? ProductionCatalogBaseAddress { get; init; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
    public int PageSize { get; init; } = 10;
}
