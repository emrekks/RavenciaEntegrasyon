using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Adapters.Hepsiburada;

public static class HepsiburadaContract
{
    public const string PlatformCode = "HEPSIBURADA";
    public const string DocumentedApiVersion = "v1.0";
    public const string PortalSource = "https://developers.hepsiburada.com/tr/companies/hepsiburada";
}

public static class HepsiburadaErrorClassifier
{
    public static AdapterError FromHttpStatus(int statusCode, TimeSpan? retryAfter = null, string? requestId = null) => statusCode switch
    {
        401 or 403 => new(AdapterErrorClass.Authentication, "HEPSIBURADA_AUTHENTICATION_FAILED", "Hepsiburada kimlik doğrulaması başarısız.", statusCode, null, requestId),
        404 => new(AdapterErrorClass.NotFound, "HEPSIBURADA_RESOURCE_NOT_FOUND", "Hepsiburada kaynağı bulunamadı.", statusCode, null, requestId),
        409 => new(AdapterErrorClass.BusinessConflict, "HEPSIBURADA_BUSINESS_CONFLICT", "Hepsiburada işlemi mevcut platform durumuyla çakışıyor.", statusCode, null, requestId),
        429 => new(AdapterErrorClass.RateLimit, "HEPSIBURADA_RATE_LIMITED", "Hepsiburada istek sınırı nedeniyle işlem ertelendi.", statusCode, retryAfter, requestId),
        >= 500 and <= 599 => new(AdapterErrorClass.Remote5xx, "HEPSIBURADA_REMOTE_ERROR", "Hepsiburada geçici servis hatası döndürdü.", statusCode, retryAfter ?? TimeSpan.FromSeconds(5), requestId),
        >= 400 and <= 499 => new(AdapterErrorClass.Validation, "HEPSIBURADA_REQUEST_REJECTED", "Hepsiburada isteği doğrulama aşamasında reddetti.", statusCode, null, requestId),
        _ => new(AdapterErrorClass.ContractViolation, "HEPSIBURADA_UNEXPECTED_STATUS", "Hepsiburada beklenmeyen HTTP durumu döndürdü.", statusCode, null, requestId)
    };

    public static AdapterError Timeout() => new(AdapterErrorClass.TransientNetwork, "HEPSIBURADA_TIMEOUT", "Hepsiburada isteği zaman aşımına uğradı.", null, TimeSpan.FromSeconds(5), null);
}
