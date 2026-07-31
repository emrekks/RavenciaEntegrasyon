using System.Net;
using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Adapters.Trendyol.ErrorMapping;

internal static class TrendyolErrorMapper
{
    public static AdapterError FromStatus(HttpStatusCode status, TimeSpan? retryAfter, string? remoteRequestId) => status switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new(AdapterErrorClass.Authentication, "REMOTE_AUTHENTICATION_FAILED", "Platform kimlik doğrulaması başarısız.", (int)status, null, remoteRequestId),
        HttpStatusCode.TooManyRequests => new(AdapterErrorClass.RateLimit, "REMOTE_RATE_LIMITED", "Platform hız sınırı yanıtı verdi.", 429, retryAfter, remoteRequestId),
        HttpStatusCode.NotFound => new(AdapterErrorClass.NotFound, "REMOTE_RESOURCE_NOT_FOUND", "Platform kaynağı bulunamadı.", 404, null, remoteRequestId),
        >= HttpStatusCode.InternalServerError => new(AdapterErrorClass.Remote5xx, "REMOTE_SERVER_ERROR", "Platform geçici sunucu hatası verdi.", (int)status, retryAfter, remoteRequestId),
        _ => new(AdapterErrorClass.Validation, "REMOTE_REQUEST_REJECTED", "Platform isteği reddetti.", (int)status, null, remoteRequestId)
    };

    public static AdapterError Configuration() => new(AdapterErrorClass.Authentication, "CONNECTION_CONFIGURATION_UNAVAILABLE", "Bağlantı veya şifreli credential kullanılamıyor.", null, null, null);
    public static AdapterError WriteClosed() => new(AdapterErrorClass.NotSupported, "EXTERNAL_WRITES_DISABLED", "Global ve connection dış yazma anahtarları kapalı.", null, null, null);
    public static AdapterError Unsupported(string message) => new(AdapterErrorClass.NotSupported, "CAPABILITY_NOT_VERIFIED", message, null, null, null);
    public static AdapterError Contract() => new(AdapterErrorClass.ContractViolation, "REMOTE_CONTRACT_INVALID", "Platform yanıtı doğrulanmış sözleşmeyle eşleşmedi.", null, null, null);
}
