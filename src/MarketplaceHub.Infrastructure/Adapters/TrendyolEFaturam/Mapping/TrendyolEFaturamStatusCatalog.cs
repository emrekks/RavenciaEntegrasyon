namespace MarketplaceHub.Infrastructure.Adapters.TrendyolEFaturam.Mapping;

public static class TrendyolEFaturamStatusCatalog
{
    public static (string CanonicalStatus, bool IsTerminal) Classify(int status) => status switch
    {
        10 or 20 or 40 or 50 or 100 or 200 => ("PENDING", false),
        30 => ("PENDING", false),
        205 => ("ACCEPTED", true),
        29 or 105 or 405 => ("REJECTED", true),
        305 => ("CANCELLED", true),
        _ => ("MANUAL_REVIEW", true)
    };
}
