namespace MarketplaceHub.Infrastructure.Adapters.Hepsiburada;

public sealed class HepsiburadaOptions
{
    public const string SectionName = "Hepsiburada";
    // The current official docs warn that authentication has changed. Keep outbound
    // calls disabled until the account's SIT credential pairing is confirmed.
    public string AuthenticationMode { get; init; } = "UNVERIFIED";
    public Uri StageOmsBaseAddress { get; init; } = new("https://oms-external-sit.hepsiburada.com/");
    public Uri ProductionOmsBaseAddress { get; init; } = new("https://oms-external.hepsiburada.com/");
    public Uri StageListingBaseAddress { get; init; } = new("https://listing-external-sit.hepsiburada.com/");
    public Uri ProductionListingBaseAddress { get; init; } = new("https://listing-external.hepsiburada.com/");
    public Uri StageCatalogBaseAddress { get; init; } = new("https://mpop-sit.hepsiburada.com/product/");
    // The official catalog reference currently documents the SIT base URL only.
    // Configure production explicitly after Hepsiburada confirms the account endpoint.
    public Uri? ProductionCatalogBaseAddress { get; init; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
    public int PageSize { get; init; } = 10;
}
