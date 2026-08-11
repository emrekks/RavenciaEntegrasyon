using System.Net;
using System.Text.Json;
using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Adapters.Trendyol.ErrorMapping;

internal static class TrendyolErrorMapper
{
    public static AdapterError FromStatus(HttpStatusCode status, TimeSpan? retryAfter, string? remoteRequestId, string? vendorCode = null) => status switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new(AdapterErrorClass.Authentication, "REMOTE_AUTHENTICATION_FAILED", "Platform kimlik doğrulaması başarısız.", (int)status, null, remoteRequestId),
        HttpStatusCode.TooManyRequests => new(AdapterErrorClass.RateLimit, "REMOTE_RATE_LIMITED", "Platform hız sınırı yanıtı verdi.", 429, retryAfter, remoteRequestId),
        HttpStatusCode.NotFound => new(AdapterErrorClass.NotFound, "REMOTE_RESOURCE_NOT_FOUND", "Platform kaynağı bulunamadı.", 404, null, remoteRequestId),
        >= HttpStatusCode.InternalServerError => new(AdapterErrorClass.Remote5xx, "REMOTE_SERVER_ERROR", "Platform geçici sunucu hatası verdi.", (int)status, retryAfter, remoteRequestId),
        _ => new(AdapterErrorClass.Validation, "REMOTE_REQUEST_REJECTED", string.IsNullOrWhiteSpace(vendorCode) ? "Platform isteği reddetti." : $"Platform isteği reddetti (sağlayıcı kodu: {vendorCode}).", (int)status, null, remoteRequestId)
    };

    public static string? SafeVendorCode(string responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody) || responseBody.Length > 16_384) return null;
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            var candidates = new List<string?>();
            Add(document.RootElement, candidates);
            if (document.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
                foreach (var error in errors.EnumerateArray()) Add(error, candidates);
            return candidates.Select(Normalize).FirstOrDefault(value => value is not null);
        }
        catch (JsonException) { return null; }
    }

    private static void Add(JsonElement element, ICollection<string?> candidates)
    {
        if (element.ValueKind != JsonValueKind.Object) return;
        foreach (var name in new[] { "code", "errorCode", "error_code" })
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String) candidates.Add(value.GetString());
    }

    private static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var code = value.Trim();
        if (code.Length > 96 || !code.Any(char.IsLetter) || code.Any(character => !(char.IsLetterOrDigit(character) || character is '_' or '-' or '.'))) return null;
        return code;
    }

    public static AdapterError Configuration() => new(AdapterErrorClass.Authentication, "CONNECTION_CONFIGURATION_UNAVAILABLE", "Bağlantı veya şifreli credential kullanılamıyor.", null, null, null);
    public static AdapterError WriteClosed() => new(AdapterErrorClass.NotSupported, "EXTERNAL_WRITES_DISABLED", "Global ve connection dış yazma anahtarları kapalı.", null, null, null);
    public static AdapterError Unsupported(string message) => new(AdapterErrorClass.NotSupported, "CAPABILITY_NOT_VERIFIED", message, null, null, null);
    public static AdapterError Contract() => new(AdapterErrorClass.ContractViolation, "REMOTE_CONTRACT_INVALID", "Platform yanıtı doğrulanmış sözleşmeyle eşleşmedi.", null, null, null);
}
