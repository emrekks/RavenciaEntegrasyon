using MarketplaceHub.Domain;

namespace MarketplaceHub.Application;

// The nightly link verification (LISTING_VERIFY): once a day every connection's whole marketplace catalog is read and
// only the linked codes are checked, inside the Istanbul night window 03:00-06:00. A day the window was missed (host
// down, connection failing, an import holding the catalog lock) runs at the first chance afterwards; no day is skipped.
public static class ListingVerificationPolicy
{
    public const string ResourceType = "LISTING_VERIFY";
    // The run's own position (page cursor, start time, completed runs) beside the telemetry cursor of the same flow.
    public const string StateCursorResource = "LISTING_VERIFY_STATE";
    public const int DefaultIntervalSeconds = 86_400;
    // Spreads the connections over the first half of the window.
    public const int DefaultJitterSeconds = 5_400;
    public static readonly TimeSpan IstanbulOffset = TimeSpan.FromHours(3);
    public static readonly TimeSpan WindowStart = TimeSpan.FromHours(3);
    public static readonly TimeSpan WindowEnd = TimeSpan.FromHours(6);
    // A run that was queued but did not complete (a failing connection) is queued again an hour later at the earliest.
    public static readonly TimeSpan RetrySpacing = TimeSpan.FromHours(1);

    // n11, Pazarama and PTT AVM link by the panel's codes, which their listings need not carry: each connection's first
    // two completed runs only record what they would mark.
    public const int ObserveOnlyRuns = 2;

    // Single-variant lookups: at most this many per connection and run, one per variant within LookupSpacing.
    public const int MaxLookupsPerRun = 300;
    public static readonly TimeSpan LookupSpacing = TimeSpan.FromHours(6);
    // A lookup answer that runs over this many pages or variants did not apply the code filter: it says nothing.
    public const int MaxLookupPages = 5;
    public const int MaxLookupVariants = 50;

    public static bool Supports(string? platformCode) => platformCode is "TRENDYOL" or "HEPSIBURADA" or "SHOPIFY" or "N11" or "PAZARAMA" or "PTTAVM";

    // The 03:00 Istanbul that opened the current daily cycle: today's once it has passed, else yesterday's.
    public static DateTimeOffset CycleStart(DateTimeOffset now)
    {
        var local = now.ToOffset(IstanbulOffset);
        var todayStart = new DateTimeOffset(local.Year, local.Month, local.Day, 0, 0, 0, IstanbulOffset) + WindowStart;
        return (now >= todayStart ? todayStart : todayStart.AddDays(-1)).ToUniversalTime();
    }

    // Due once per cycle: from the window's start (plus the connection's jitter) until a run completes in the cycle.
    // A connection never queued waits for the window itself; one whose run did not complete is queued again at the
    // first chance after RetrySpacing, inside the window or not, so a missed night is caught up instead of skipped.
    public static bool IsDue(DateTimeOffset now, DateTimeOffset? lastCompletedAt, DateTimeOffset? lastQueuedAt, int jitterSeconds)
    {
        var cycleStart = CycleStart(now);
        if (lastCompletedAt is { } completed && completed >= cycleStart) return false;
        if (lastQueuedAt is { } queued && now - queued < RetrySpacing) return false;
        var windowLength = WindowEnd - WindowStart;
        var start = cycleStart + TimeSpan.FromSeconds(Math.Clamp(jitterSeconds, 0, (int)windowLength.TotalSeconds / 2));
        if (now < start) return false;
        if (lastCompletedAt is null && lastQueuedAt is null) return now < cycleStart + windowLength;
        return true;
    }

    public static bool ObservesOnly(string? platformCode, int completedRuns) =>
        platformCode is "N11" or "PAZARAMA" or "PTTAVM" && completedRuns < ObserveOnlyRuns;

    // PTT AVM's product list names product rows, not the variants of a model (GetProductsWithVariants carries no codes),
    // so a scan that misses a variant code proves nothing there: its unseen codes go to the single-code check only.
    public static bool ScanListsVariantCodes(string? platformCode) => platformCode != "PTTAVM";

    public static bool LookupDue(DateTimeOffset? lastLookupAt, DateTimeOffset now) => lastLookupAt is not { } last || now - last >= LookupSpacing;

    // The marketplace's answer to a lookup filtered by one of the link's codes. Found when any variant carries one of
    // the link's codes; NOT_FOUND only when the answer was read to its end and stayed small enough to be the filtered
    // one (an answer the filter did not narrow says nothing).
    public static string Outcome(IEnumerable<string?> linkCodes, IReadOnlyCollection<(string? Id, string? Sku, string? Barcode)> remoteVariants, bool answerComplete)
    {
        var codes = linkCodes.Where(code => !string.IsNullOrWhiteSpace(code)).Select(code => code!.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (codes.Count == 0) return MarketplaceListingLookupOutcome.Unsupported;
        if (remoteVariants.Any(variant => new[] { variant.Id, variant.Sku, variant.Barcode }.Any(code => !string.IsNullOrWhiteSpace(code) && codes.Contains(code!.Trim()))))
            return MarketplaceListingLookupOutcome.Found;
        if (!answerComplete || remoteVariants.Count > MaxLookupVariants) return MarketplaceListingLookupOutcome.Unknown;
        return MarketplaceListingLookupOutcome.NotFound;
    }

    // The code a platform's lookup asks by, with the filter slot its adapter reads; null when the link has no code the
    // platform can be asked by (an n11 product without a stock code, a Shopify listing without a barcode).
    public static (string Code, bool AsProductMainId)? LookupKey(string? platformCode, MarketplaceVariantLink link)
    {
        string? Clean(string? value) => string.IsNullOrWhiteSpace(value) || MarketplaceVariantLinkHealth.IsListingPlaceholder(value) ? null : value.Trim();
        return platformCode switch
        {
            // Hepsiburada finds a listing by hbSku (or merchant SKU) through its productMainId slot.
            "HEPSIBURADA" => (Clean(link.ExternalId) ?? Clean(link.ExternalSku)) is { } hepsiburada ? (hepsiburada, true) : null,
            "SHOPIFY" => Clean(link.ExternalBarcode) is { } barcode ? (barcode, false) : null,
            // n11 is asked by stock code; a product read without one is linked as "n11:{productId}".
            "N11" => (Clean(link.ExternalSku) ?? (link.ExternalId.StartsWith("n11:", StringComparison.Ordinal) ? null : Clean(link.ExternalId))) is { } stockCode ? (stockCode, false) : null,
            "TRENDYOL" or "PAZARAMA" => (Clean(link.ExternalBarcode) ?? Clean(link.ExternalId)) is { } code ? (code, false) : null,
            "PTTAVM" => (Clean(link.ExternalId) ?? Clean(link.ExternalBarcode)) is { } ptt ? (ptt, false) : null,
            _ => null
        };
    }

    public static string PlatformName(string? platformCode) => platformCode switch
    {
        "HEPSIBURADA" => "Hepsiburada",
        "SHOPIFY" => "Shopify",
        "TRENDYOL" => "Trendyol",
        "PTTAVM" => "PTT AVM",
        "N11" => "n11",
        "PAZARAMA" => "Pazarama",
        _ => platformCode ?? "Pazaryeri"
    };

    // "Trendyol: 3 ürün pazaryerinde bulunamadı." — the one summary a run leaves when codes newly broke.
    public static string NotFoundSummary(string? platformCode, int products, int variants) =>
        variants == products
            ? $"{PlatformName(platformCode)}: {products:N0} ürün pazaryerinde bulunamadı."
            : $"{PlatformName(platformCode)}: {products:N0} ürün ({variants:N0} varyant) pazaryerinde bulunamadı.";

    // Where the summary leads: the products page filtered to the platform's products the marketplace no longer knows
    // (the page's platform filter takes "<PLATFORM>:NOT_FOUND").
    public static string NotFoundHref(string platformCode) => $"/products?platform={platformCode.Trim().ToUpperInvariant()}:NOT_FOUND";
}

// The position of a connection's verification, kept as JSON in its LISTING_VERIFY_STATE cursor: the run in progress
// (its job, start, next page and counts) and the runs completed so far. An unreadable cursor starts afresh.
public sealed record ListingVerificationState(
    Guid? RunJobId = null,
    DateTimeOffset? StartedAt = null,
    string? Next = null,
    int Pages = 0,
    int Received = 0,
    int CompletedRuns = 0,
    DateTimeOffset? LastCompletedAt = null)
{
    private static readonly System.Text.Json.JsonSerializerOptions Options = new(System.Text.Json.JsonSerializerDefaults.Web);

    public static ListingVerificationState Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try { return System.Text.Json.JsonSerializer.Deserialize<ListingVerificationState>(json, Options) ?? new(); }
        catch (System.Text.Json.JsonException) { return new(); }
    }

    public string ToJson() => System.Text.Json.JsonSerializer.Serialize(this, Options);

    // A new run of this job: position and counts reset, the completed-run tally kept.
    public ListingVerificationState Begin(Guid jobId, DateTimeOffset now) => this with { RunJobId = jobId, StartedAt = now.ToUniversalTime(), Next = null, Pages = 0, Received = 0 };

    public ListingVerificationState Complete(DateTimeOffset now) => this with { RunJobId = null, Next = null, CompletedRuns = CompletedRuns + 1, LastCompletedAt = now.ToUniversalTime() };
}
