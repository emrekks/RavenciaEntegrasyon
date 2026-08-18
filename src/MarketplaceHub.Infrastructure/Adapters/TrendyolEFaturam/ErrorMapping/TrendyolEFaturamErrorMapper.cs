using System.Net;
using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Adapters.TrendyolEFaturam.Contracts;

namespace MarketplaceHub.Infrastructure.Adapters.TrendyolEFaturam.ErrorMapping;

internal static class TrendyolEFaturamErrorMapper
{
    public static AdapterError FromStatus(HttpStatusCode status, TimeSpan? retryAfter, string? remoteRequestId) => status switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new(AdapterErrorClass.Authentication, "EFATURAM_AUTHENTICATION_FAILED", "E-Faturam kimlik doğrulaması başarısız.", (int)status, null, remoteRequestId),
        HttpStatusCode.TooManyRequests => new(AdapterErrorClass.RateLimit, "EFATURAM_RATE_LIMITED", "E-Faturam hız sınırı yanıtı verdi.", 429, retryAfter, remoteRequestId),
        HttpStatusCode.NotFound => new(AdapterErrorClass.NotFound, "EFATURAM_RESOURCE_NOT_FOUND", "E-Faturam kaynağı bulunamadı.", 404, null, remoteRequestId),
        HttpStatusCode.Conflict => new(AdapterErrorClass.BusinessConflict, "EFATURAM_CONFLICT", "E-Faturam aynı işleme ilişkin çakışma bildirdi.", 409, null, remoteRequestId),
        >= HttpStatusCode.InternalServerError => new(AdapterErrorClass.Remote5xx, "EFATURAM_SERVER_ERROR", "E-Faturam geçici sunucu hatası verdi.", (int)status, retryAfter, remoteRequestId),
        _ => new(AdapterErrorClass.Validation, "EFATURAM_REQUEST_REJECTED", "E-Faturam isteği reddetti.", (int)status, null, remoteRequestId)
    };

    public static AdapterError FromAuthorizedStatus(
        HttpStatusCode status,
        TimeSpan? retryAfter,
        string? remoteRequestId) =>
        string.Equals(remoteRequestId, TrendyolEFaturamProblemDetails.ApplicationMismatchReference, StringComparison.Ordinal)
            ? new(
                AdapterErrorClass.BusinessConflict,
                "EFATURAM_APPLICATION_NOT_ACTIVE",
                "E-Faturam, gönderen hesabın fatura uygulamasını bu işlem için aktif görmüyor. Stage hesabında ilgili E-Arşiv/E-Fatura API hizmeti sağlayıcı tarafından etkinleştirilmelidir.",
                (int)status,
                null,
                remoteRequestId)
            :
        status switch
        {
            HttpStatusCode.Unauthorized => new(AdapterErrorClass.Authentication, "EFATURAM_ACCESS_TOKEN_REJECTED", "E-Faturam girişi başarılı, ancak sağlayıcı sign-in yanıtındaki taze JWT tokenını korumalı fatura API'sinde geçersiz veya süresi dolmuş olarak reddetti.", 401, null, remoteRequestId),
            HttpStatusCode.Forbidden => new(AdapterErrorClass.Authentication, "EFATURAM_OPERATION_FORBIDDEN", "E-Faturam hesabı bu işlemi yapmaya yetkili değil.", 403, null, remoteRequestId),
            _ => FromStatus(status, retryAfter, remoteRequestId)
        };

    public static AdapterError Configuration() => new(AdapterErrorClass.Authentication, "EFATURAM_CONFIGURATION_UNAVAILABLE", "E-Faturam bağlantısı veya şifreli credential kullanılamıyor.", null, null, null);
    public static AdapterError Unsupported(string message) => new(AdapterErrorClass.NotSupported, "CAPABILITY_NOT_VERIFIED", message, null, null, null);
    public static AdapterError Contract() => new(AdapterErrorClass.ContractViolation, "EFATURAM_CONTRACT_INVALID", "E-Faturam yanıtı doğrulanmış sözleşmeyle eşleşmedi.", null, null, null);
}
