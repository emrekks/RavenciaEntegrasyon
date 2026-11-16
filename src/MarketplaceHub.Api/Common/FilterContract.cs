namespace MarketplaceHub.Api.Common;

// API-GUVENLIK-8 b: one name per filter. A platform filter is `platforms` (a comma list of platform codes) and a period
// is `dateFrom` / `dateTo` (a day yyyy-MM-dd where the endpoint reads days, an ISO-8601 moment where it reads moments).
// The endpoints that spelled them otherwise take the unified names too (EndpointHelpers.Unified: the unified name wins
// when both come); the old names keep working for browser tabs on an older bundle and the document marks them
// deprecated with their replacement. ApiDeprecatedParametersTests lists every deprecated parameter for its removal.
public static class FilterContract
{
    private static readonly Dictionary<string, string> PlatformCodes = new(StringComparer.Ordinal) { ["platformCodes"] = "platforms" };
    private static readonly Dictionary<string, string> Period = new(StringComparer.Ordinal) { ["from"] = "dateFrom", ["to"] = "dateTo" };
    private static readonly Dictionary<string, string> PeriodAndPlatforms = new(StringComparer.Ordinal) { ["from"] = "dateFrom", ["to"] = "dateTo", ["platformCodes"] = "platforms" };
    private static readonly Dictionary<string, string> Platform = new(StringComparer.Ordinal) { ["platform"] = "platforms" };
    private static readonly Dictionary<string, string> Since = new(StringComparer.Ordinal) { ["since"] = "dateFrom" };

    // Old name → unified name, by the operation's document route.
    private static readonly Dictionary<string, IReadOnlyDictionary<string, string>> Renamed = new(StringComparer.Ordinal)
    {
        ["/api/v1/dashboard/performance"] = Period,
        ["/api/v1/invoice-workspace/page"] = PeriodAndPlatforms,
        ["/api/v1/jobs"] = Since,
        ["/api/v1/jobs/counts"] = Since,
        ["/api/v1/jobs/page"] = Since,
        ["/api/v1/notifications"] = Period,
        ["/api/v1/price-stock-send-log"] = Period,
        ["/api/v1/price-stock-send-log/counts"] = Period,
        ["/api/v1/product-activity"] = Period,
        ["/api/v1/product-activity/counts"] = Period,
        ["/api/v1/reports/order-profitability"] = PeriodAndPlatforms,
        ["/api/v1/reports/payouts"] = PlatformCodes,
        ["/api/v1/reports/payouts/lines"] = PlatformCodes,
        ["/api/v1/reports/profit-rate"] = PlatformCodes,
        ["/api/v1/reports/sales"] = PeriodAndPlatforms,
        ["/api/v1/reports/stock"] = PeriodAndPlatforms,
        // Round 8: these filter by one platform; platforms takes a single code there (several are refused).
        ["/api/v1/products"] = Platform,
        ["/api/v1/questions"] = Platform,
    };

    /// <summary>The operations that renamed a filter, with each old name's unified replacement.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> RenamedFilters => Renamed;

    /// <summary>The unified name of a deprecated filter of the operation, or null.</summary>
    public static string? ReplacementOf(string path, string parameter) =>
        Renamed.TryGetValue(path, out var names) && names.TryGetValue(parameter, out var replacement) ? replacement : null;
}
