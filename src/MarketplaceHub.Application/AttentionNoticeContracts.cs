namespace MarketplaceHub.Application;

// Background states that need the owner's eye and that no screen action produced, for the notification center
// (audit 2026-11-06 Y2, owner: "bildirim merkezinde de gözüksün"). Each notice stands while its cause stands: the panel
// keeps one entry per Key, updates it in place while Revision stays the same, raises it again (unread) when Revision
// changes, and removes it once the notice is no longer listed (the read succeeded, the card was settled).
public sealed record AttentionNoticeView(
    string Key,
    string Revision,
    string Code,
    string Title,
    string Message,
    Guid ConnectionId,
    string PlatformCode,
    string StoreName,
    string? ResourceType,
    int FailureCount,
    DateTimeOffset? LastAttemptAt,
    DateTimeOffset? SinceAt,
    string? LastError,
    IReadOnlyList<AttentionNoticeItemView> Items);

// A record a notice is about: a product card with the barcodes it concerns.
public sealed record AttentionNoticeItemView(Guid? ProductId, string Label, IReadOnlyList<string> Barcodes);

public interface IAttentionNoticeService
{
    Task<IReadOnlyList<AttentionNoticeView>> ListAsync(Guid tenantId, CancellationToken cancellationToken);
}

public static class AttentionNoticePolicy
{
    public const string SyncCursorFailingCode = "SYNC_CURSOR_FAILING";
    public const string ApprovalDeadlineExpiredCode = "PRODUCT_APPROVAL_DEADLINE_EXPIRED";

    // Three failed runs in a row make a read worth a notice: one or two are usually a passing marketplace hiccup.
    public const int ConsecutiveFailureThreshold = 3;

    // A streak whose last failure is older than this belongs to a read no longer scheduled; it is not raised.
    public static readonly TimeSpan StaleFailureAge = TimeSpan.FromDays(14);

    public static bool IsFailing(int consecutiveFailures, DateTimeOffset? lastErrorAt, DateTimeOffset now) =>
        consecutiveFailures >= ConsecutiveFailureThreshold
        && lastErrorAt is { } errorAt && now - errorAt <= StaleFailureAge;

    // The approval check of an expired card has its own notice naming the products; the cursor streak behind it is
    // not raised a second time.
    public static bool CoveredByOwnNotice(string resourceType, string? lastError) =>
        string.Equals(resourceType, "PRODUCT_APPROVAL", StringComparison.Ordinal)
        && string.Equals(lastError?.Trim(), ApprovalDeadlineExpiredCode, StringComparison.Ordinal);

    // Cursors of a write or of a single user action: their failures are reported on the action itself (the card, the
    // package, the connection test), not as a failing read.
    private static readonly HashSet<string> ActionResources = new(StringComparer.Ordinal)
    {
        "CONNECTION_TEST", "PRODUCT_CREATE", "PRODUCT_UPDATE", "PRODUCT_ARCHIVE", "PRICE_INVENTORY", "STOCK_PROJECTION",
        "SHIPMENT_ACTION", "RETURN_ACTION", "COMMON_LABEL", "CAPABILITY_PROBE", "STAGE_TEST_ORDER"
    };

    public static bool IsReadResource(string resourceType) => !ActionResources.Contains(resourceType);

    public static string ResourceLabel(string resourceType) => resourceType switch
    {
        "ORDERS_HOT" => "Sipariş okuma",
        "ORDERS_RECOVERY" => "Sipariş kurtarma taraması",
        "ORDER_LIFECYCLE" => "Açık sipariş durum kontrolü",
        "ORDER_RECONCILIATION" => "Sipariş uzlaştırma",
        "ORDER_CARGO_INFO_RECONCILIATION" => "Kargo bilgisi uzlaştırma",
        "ORDER_INVOICE_RECONCILIATION" => "Sipariş fatura kontrolü",
        "RETURNS" => "İade okuma",
        "RETURN_LIFECYCLE" => "Açık iade kontrolü",
        "RETURN_RECONCILIATION" => "İade uzlaştırma",
        "QUESTIONS" => "Müşteri sorusu okuma",
        "REFERENCE_DATA" => "Kategori ve marka okuma",
        "PRODUCTS" => "Ürün aktarımı",
        "PRODUCT_APPROVAL" => "Ürün onay kontrolü",
        "STOCK_RECONCILIATION" => "Stok uzlaştırma",
        "FINANCE" => "Finans okuma",
        "WEBHOOK_INGEST" or "WEBHOOK_INGEST_RETURNS" => "Anlık bildirim işleme",
        "LISTING_VERIFY" => "İlan doğrulama",
        "LISTING_LOOKUP" => "İlan arama",
        _ => resourceType
    };

    public const string FailingTitle = "Senkronizasyon Art Arda Başarısız";

    public static string FailingMessage(string resourceType, int failures, string? lastError) =>
        $"{ResourceLabel(resourceType)} art arda {failures} kez başarısız oldu{(string.IsNullOrWhiteSpace(lastError) ? "" : $" (son hata: {lastError.Trim()})")}. Başarılı ilk çalışmada bu bildirim kendiliğinden kalkar.";

    public static string ExpiredTitle(string marketplaceName) => $"{marketplaceName} Onayı Sonuçlanmadı";

    public static string ExpiredMessage(string marketplaceName, int products) =>
        $"{products} ürünün {marketplaceName} onayı kontrol süresi içinde sonuçlanmadı; onaylandığı ya da reddedildiği bilinmiyor. Barkodları {marketplaceName} satıcı panelinden kontrol edip ürün kartından yayın durumunu yeniden sorgulayın.";

    public static string PlatformName(string platformCode) => platformCode.ToUpperInvariant() switch
    {
        "TRENDYOL" => "Trendyol",
        "HEPSIBURADA" => "Hepsiburada",
        "N11" => "N11",
        "PAZARAMA" => "Pazarama",
        "PTTAVM" => "PttAVM",
        "SHOPIFY" => "Shopify",
        _ => platformCode
    };
}
