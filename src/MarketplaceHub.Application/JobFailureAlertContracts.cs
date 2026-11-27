using System.Security.Cryptography;
using System.Text;

namespace MarketplaceHub.Application;

// Queue phase 1A, item 4 (27 Nov 2026): a background job that stops for good reaches the bell, through the same shared
// rows the "Yeni Sipariş" notices use (ops.panel_notifications, UserId null, server key "job-alert:", listed for the
// bell by GET /notifications/new-orders). Raised for: a job that ended DEAD (its tries used up), a job that went to
// MANUAL_REVIEW, and a job BLOCKED for more than a day. One notification per (connection, job type, error code) and
// Istanbul day: ten dead order reads of one store with the same cause are one bell entry. Never raised: a job whose own
// retry later succeeded (it ends SUCCEEDED, not DEAD), a job stopped because a newer one replaced it (any *SUPERSEDED*
// code) and a write blocked before anything was sent (the connection off, writes switched off: the owner's choice).
// No e-mail, no sound.
public static class JobFailureAlertPolicy
{
    public const string KeyPrefix = "job-alert:";
    public const string Category = "JOB";
    public const string ActionLabel = "İşe Git";
    public static readonly TimeSpan BlockedFor = TimeSpan.FromHours(24);

    public enum Kind { Dead, ManualReview, Blocked }

    // Short enough for the bell's 120-character id: the day and a digest of what makes the alert one.
    public static string ClientKey(Guid? connectionId, string jobType, string? errorCode, DateTimeOffset raisedAt)
    {
        var day = raisedAt.ToOffset(TurkeyClock.Offset).ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{connectionId:N}|{jobType}|{errorCode?.Trim()}")))[..16];
        return $"{KeyPrefix}{day}:{digest}";
    }

    // Review of 27 Nov 2026 (D1): at most DailyCap alerts per tenant and Istanbul day; past it ONE summary alert of the day
    // counts the rest ("… ve N iş daha"), so an outage cannot flood the bell.
    public const int DailyCap = 30;
    public const int SummaryKeyLimit = 400;
    public const string SummaryTitle = "Başka İşler de Durdu";
    public const string SummaryActionLabel = "İşlem Takibine Git";
    public const string SummaryHref = "/jobs";

    public static string SummaryKey(string day) => $"{KeyPrefix}{day}:summary";

    // The Istanbul day (yyyyMMdd) a key belongs to.
    public static string DayOf(string clientKey) => clientKey.Substring(KeyPrefix.Length, 8);

    public static string SummaryMessage(int more) =>
        $"Bugün tek tek bildirilen {DailyCap} işin ardından {more} iş daha durdu ya da incelemenizi bekliyor. Ayrıntılar İşlem Takibi'nde.";

    public static bool IsSuperseded(string? errorCode) => errorCode is not null && errorCode.Contains("SUPERSEDED", StringComparison.Ordinal);

    public static string Severity(Kind kind) => kind == Kind.Dead ? PanelNotificationSeverities.Error : PanelNotificationSeverities.Warning;

    public static string Title(Kind kind) => kind switch
    {
        Kind.Dead => "Arka Plan İşi Durdu",
        Kind.ManualReview => "İş İnceleme Bekliyor",
        _ => "İş Bir Gündür Engelli"
    };

    // "Pazarama Mağazam: Yeni Siparişler işi denemeleri bittiği için durdu. Neden: …". No " · " in it: the bell shows a
    // message up to its first " · ".
    public static string Message(Kind kind, string? storeName, string platformName, string jobType, string reason)
    {
        var store = string.IsNullOrWhiteSpace(storeName) ? platformName : storeName.Trim();
        var what = kind switch
        {
            Kind.Dead => "denemeleri bittiği için durdu",
            Kind.ManualReview => "incelemenizi bekliyor",
            _ => "bir günden uzun süredir engelli"
        };
        var name = JobName(jobType);
        var subject = string.IsNullOrWhiteSpace(store) ? name : $"{store}: {name}";
        var text = $"{subject} işi {what}. Neden: {reason.Replace(" · ", ", ", StringComparison.Ordinal).Trim()}";
        return text.Length > 600 ? text[..600] : text;
    }

    // The job's own page in İşlem Takibi (the search finds it by its id, as the bell's other job links do).
    public static string Href(Guid jobId) => $"/jobs?ara={jobId:D}";

    // The reason as İşlem Takibi words it: the code's Turkish text, else the job's own summary, else the code.
    public static string Reason(string? errorCode, string? errorSummary)
    {
        if (errorCode is not null && JobErrorMessages.Messages.TryGetValue(errorCode.Trim(), out var known)) return known;
        var summary = errorSummary?.Trim();
        if (!string.IsNullOrEmpty(summary)) return summary.Length > 300 ? summary[..300] : summary;
        return string.IsNullOrWhiteSpace(errorCode) ? "Neden kaydedilmedi" : errorCode.Trim();
    }

    // The job's Turkish name as İşlem Takibi titles it (features/operations/job-presentation.ts, without the platform).
    public static string JobName(string jobType)
    {
        var type = jobType.ToUpperInvariant();
        if (Names.TryGetValue(type, out var name)) return name;
        if (type.EndsWith("_REFERENCE_SYNC", StringComparison.Ordinal)) return "Kategori ve Marka Listesi";
        if (type.EndsWith("_ORDER_RECOVERY_SYNC", StringComparison.Ordinal)) return "Sipariş Geçmişi";
        if (type.EndsWith("_ORDER_STATUS_SYNC", StringComparison.Ordinal)) return "Açık Sipariş Durumları";
        if (type.EndsWith("_ORDER_INVOICE_RECONCILIATION", StringComparison.Ordinal)) return "Paket Fatura Durumları";
        if (type.EndsWith("_ORDER_RECONCILIATION", StringComparison.Ordinal)) return "Sipariş Uzlaştırması";
        if (type.EndsWith("_ORDER_SYNC", StringComparison.Ordinal)) return "Yeni Siparişler";
        if (type.EndsWith("_WEBHOOK_INGEST", StringComparison.Ordinal)) return "Anlık Sipariş Bildirimi";
        if (type.EndsWith("_RETURN_SYNC", StringComparison.Ordinal)) return "Yeni İadeler";
        if (type.EndsWith("_PRODUCT_SYNC", StringComparison.Ordinal)) return "Ürünler";
        if (type.EndsWith("_CONNECTION_TEST", StringComparison.Ordinal)) return "Bağlantı Testi";
        if (type.EndsWith("_FINANCE_SYNC", StringComparison.Ordinal) || type.EndsWith("_SETTLEMENT_SYNC", StringComparison.Ordinal)) return "Hakediş Kayıtları";
        return "Arka Plan";
    }

    private static readonly IReadOnlyDictionary<string, string> Names = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["ORDER_STATUS_SYNC"] = "Açık Sipariş Durumları",
        ["ORDER_RECONCILIATION"] = "Sipariş Uzlaştırması",
        ["ORDER_INVOICE_RECONCILIATION"] = "Paket Fatura Durumları",
        ["TRENDYOL_ORDER_CARGO_INFO_RECONCILIATION"] = "Kargo Bilgileri",
        ["SHIPMENT_ACTION"] = "Paket Durumu Gönderimi",
        ["TRENDYOL_COMMON_LABEL"] = "Kargo Etiketi",
        ["PRODUCT_SYNC"] = "Ürünler",
        ["PRODUCT_CREATE"] = "Ürün Oluşturma",
        ["PRODUCT_UPDATE"] = "Ürün Güncelleme",
        ["PRODUCT_ARCHIVE"] = "Ürünü Satıştan Kaldırma",
        ["PRODUCT_APPROVAL_RECONCILE"] = "Ürün Onay Durumu",
        ["LISTING_VERIFY"] = "Yayın Doğrulaması",
        ["LISTING_LOOKUP"] = "Varyant Arama",
        ["PRICE_INVENTORY_SYNC"] = "Fiyat ve Stok Gönderimi",
        ["STOCK_PROJECTION_DISPATCH"] = "Stok Gönderim Hazırlığı",
        ["STOCK_RECONCILIATION"] = "Stok Uzlaştırması",
        ["RETURN_SYNC"] = "Yeni İadeler",
        ["RETURN_STATUS_SYNC"] = "Açık İade Durumları",
        ["RETURN_RECONCILIATION"] = "İade Uzlaştırması",
        ["RETURN_ACTION"] = "İade Kararı",
        ["EXTERNAL_EFFECT_RECONCILE"] = "Gönderim Doğrulaması",
        ["MARKETPLACE_QUESTION_SYNC"] = "Müşteri Soruları",
        ["TRENDYOL_FINANCE_SYNC"] = "Cari Hesap Ekstresi",
        ["PAZARAMA_FINANCE_SYNC"] = "Finans Hareketleri",
        ["CATALOG_STRUCTURE_IMPORT"] = "Ürün Yapısı",
        ["INVOICE_SUBMIT"] = "Fatura Kesimi",
        ["INVOICE_RECONCILE"] = "Fatura Durumu",
        ["INVOICE_DOCUMENT_FETCH"] = "Fatura Belgesi",
        ["INVOICE_MARKETPLACE_DELIVERY"] = "Fatura Gönderimi",
        ["MANUAL_ORDER_INVOICE_DELIVERY"] = "Fatura Gönderimi (Elle)",
        ["INVOICE_CANCELLATION"] = "Fatura İptali",
        ["INVOICE_DUE_SCAN"] = "Fatura Süre Takibi",
        ["IMPORT_PREVIEW"] = "Dosya Önizleme",
        ["IMPORT_APPLY"] = "Dosya İçe Aktarma"
    };
}
