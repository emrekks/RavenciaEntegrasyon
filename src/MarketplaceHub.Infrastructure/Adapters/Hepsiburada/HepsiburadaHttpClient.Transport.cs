using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Adapters.Http;
using Microsoft.Extensions.Logging;

namespace MarketplaceHub.Infrastructure.Adapters.Hepsiburada;

public sealed partial class HepsiburadaHttpClient
{
    private async Task<AdapterResult<JsonDocument>> SendAsync(HepsiburadaRequestContext context, Uri baseAddress, HttpMethod method, string path, CancellationToken cancellationToken) =>
        await SendAsync(context, baseAddress, method, path, null, cancellationToken);

    private async Task<AdapterResult<JsonDocument>> SendAsync(HepsiburadaRequestContext context, Uri baseAddress, HttpMethod method, string path, HttpContent? content, CancellationToken cancellationToken, Action<HttpRequestMessage>? configure = null)
    {
        if (!string.Equals(settings.AuthenticationMode, "BASIC", StringComparison.OrdinalIgnoreCase))
            return AdapterResult<JsonDocument>.Failure(new(AdapterErrorClass.NotSupported, "HEPSIBURADA_AUTHENTICATION_UNVERIFIED", "Hepsiburada auth biçimi SIT hesabında doğrulanana kadar bağlantı isteği gönderilmedi.", null, null, null));
        using var request = new HttpRequestMessage(method, new Uri(baseAddress, path));
        request.Content = content;
        if (!ApplyAuthentication(request, context))
            return Failure<JsonDocument>(AdapterErrorClass.Validation, "HEPSIBURADA_INTEGRATOR_NAME_INVALID", "Hepsiburada entegratör adı geçerli bir User-Agent kimliği olmalıdır.", HttpStatusCode.UnprocessableEntity);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        configure?.Invoke(request);
        using var exchange = await Transport.SendAsync(clients, request, RequestTimeout, cancellationToken);
        if (exchange.Error is { } transportError)
        {
            if (exchange.Exception is { } exception) logger.LogWarning(exception, "Hepsiburada API isteği başarısız. ConnectionId: {ConnectionId}", context.Connection.Id);
            return AdapterResult<JsonDocument>.Failure(transportError);
        }
        var response = exchange.Response!;
        var body = exchange.Body;
        var rate = RateLimit(response);
        var requestId = response.Headers.TryGetValues("X-Request-Id", out var ids) ? ids.FirstOrDefault() : null;
        if (rate is not null)
            logger.LogDebug("Hepsiburada yanıt hız sınırı başlıkları. ConnectionId: {ConnectionId}, Limit: {Limit}, Remaining: {Remaining}, ResetAt: {ResetAt}, RetryAfterSeconds: {RetryAfterSeconds}", context.Connection.Id, rate.Limit, rate.Remaining, rate.ResetAt, rate.RetryAfter?.TotalSeconds);
        if (!response.IsSuccessStatusCode)
        {
            var retryAfter = RetryAfter(response);
            var error = LocalCircuitResponse.Refusal(response, "HEPSIBURADA", "Hepsiburada") is { } localCircuit
                ? localCircuit
                : response.StatusCode == HttpStatusCode.Unauthorized && IsProductUpdatePath(path)
                ? ProductUpdateAuthenticationRejected(requestId)
                : MarketplaceErrorRules.GatewayBlocked(response.StatusCode, body, "Hepsiburada", "HEPSIBURADA", requestId)
                    ?? Error(response.StatusCode, retryAfter ?? rate?.RetryAfter, requestId);
            var invoiceErrorDetails = method == HttpMethod.Put
                && path.EndsWith("/invoice", StringComparison.OrdinalIgnoreCase)
                ? SafeInvoiceErrorDetails(body)
                : null;
            logger.LogWarning("Hepsiburada API isteği reddedildi. ConnectionId: {ConnectionId}, Status: {Status}, Code: {Code}, RequestId: {RequestId}, Limit: {Limit}, Remaining: {Remaining}, ResetAt: {ResetAt}, RetryAfterSeconds: {RetryAfterSeconds}, InvoiceErrorDetails: {InvoiceErrorDetails}", context.Connection.Id, (int)response.StatusCode, error.Code, requestId, rate?.Limit, rate?.Remaining, rate?.ResetAt, error.RetryAfter?.TotalSeconds ?? rate?.RetryAfter?.TotalSeconds, invoiceErrorDetails);
            return AdapterResult<JsonDocument>.Failure(error, rate);
        }
        try { return AdapterResult<JsonDocument>.Success(JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body), rate); }
        catch (JsonException)
        {
            return AdapterResult<JsonDocument>.Failure(new(AdapterErrorClass.ContractViolation, "HEPSIBURADA_JSON_INVALID", "Hepsiburada geçerli JSON yanıtı vermedi.", (int)response.StatusCode, null, requestId), rate);
        }
    }

    private TimeSpan RequestTimeout => settings.Timeout > TimeSpan.Zero && settings.Timeout < TimeSpan.FromMinutes(2) ? settings.Timeout : TimeSpan.FromSeconds(30);

    // The Hepsiburada send step (MarketplaceTransport: quota-aware timeout, transport errors HEPSIBURADA_*).
    internal static readonly MarketplaceTransport Transport = new("Hepsiburada", "HEPSIBURADA", "Hepsiburada", HepsiburadaRateLimitHandler.QuotaAcquiredKey);

    internal static bool ApplyAuthentication(HttpRequestMessage request, HepsiburadaRequestContext context)
    {
        // C20: the integrator name is sent as written (spaces allowed) as long as it is printable ASCII; the
        // product-token parser refused names with spaces and every request was blocked locally. A Turkish letter or
        // a control character still cannot go in a header and stops the request here.
        var integratorName = (context.IntegratorName ?? context.Username).Trim();
        if (integratorName.Length == 0 || !integratorName.All(character => character is >= ' ' and <= '~'))
            return false;
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{context.Username}:{context.Password}")));
        return request.Headers.TryAddWithoutValidation("User-Agent", integratorName);
    }

    // The Retry-After of a refused answer; a date in the past is no wait (it used to be a negative one, ADAPTOR-TY-HB-7).
    internal TimeSpan? RetryAfter(HttpResponseMessage response) =>
        RetryAfterHeader.Delay(response.Headers.RetryAfter, timeProvider.GetUtcNow());

    internal RateLimitMetadata? RateLimit(HttpResponseMessage response)
    {
        int? limit = response.Headers.TryGetValues("X-RateLimit-Limit", out var limitValues) && int.TryParse(limitValues.FirstOrDefault(), out var parsedLimit) ? parsedLimit : null;
        int? remaining = response.Headers.TryGetValues("X-RateLimit-Remaining", out var values) && int.TryParse(values.FirstOrDefault(), out var parsedRemaining) ? parsedRemaining : null;
        DateTimeOffset? resetAt = null;
        TimeSpan? retryAfter = response.Headers.RetryAfter?.Delta;
        if (response.Headers.TryGetValues("X-RateLimit-Reset", out var resets) && long.TryParse(resets.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var reset))
        {
            var wait = ResetWait(reset, timeProvider.GetUtcNow());
            retryAfter ??= wait;
            resetAt = timeProvider.GetUtcNow().Add(wait);
        }
        return limit is null && remaining is null && resetAt is null && retryAfter is null ? null : new(remaining, resetAt, retryAfter, limit);
    }

    // X-RateLimit-Reset is documented as seconds until the window resets, but a value past 1e9 can only be a
    // Unix time (seconds) of the reset; read as a delay it would park the connection for half a century.
    internal static TimeSpan ResetWait(long reset, DateTimeOffset now)
    {
        if (reset <= 0) return TimeSpan.Zero;
        if (reset <= 1_000_000_000) return TimeSpan.FromSeconds(reset);
        if (reset > DateTimeOffset.MaxValue.ToUnixTimeSeconds()) return TimeSpan.Zero;
        var wait = DateTimeOffset.FromUnixTimeSeconds(reset) - now;
        return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
    }

    internal static AdapterError Error(HttpStatusCode status, TimeSpan? retryAfter, string? requestId) => status switch
    {
        HttpStatusCode.Unauthorized => new(AdapterErrorClass.Authentication, "HEPSIBURADA_CREDENTIALS_REJECTED", "Hepsiburada HTTP 401: entegratör kullanıcı adı ve servis anahtarı bu ortama uymuyor. Aynı aktif Entegratörlerim kaydından alınan kullanıcı adı ve canlı servis anahtarını kontrol edin.", (int)status, null, requestId),
        HttpStatusCode.Forbidden => new(AdapterErrorClass.Authentication, "HEPSIBURADA_ACCESS_FORBIDDEN", "Hepsiburada HTTP 403: mağaza veya endpoint erişim yetkisi reddedildi. Mağaza ID’sini ve entegratör erişim yetkisini kontrol edin.", (int)status, null, requestId),
        HttpStatusCode.TooManyRequests => new(AdapterErrorClass.RateLimit, "HEPSIBURADA_RATE_LIMITED", "Hepsiburada istek sınırına ulaşıldı.", 429, retryAfter ?? TimeSpan.FromSeconds(5), requestId),
        HttpStatusCode.NotFound => new(AdapterErrorClass.NotFound, "HEPSIBURADA_RESOURCE_NOT_FOUND", "Hepsiburada kaynağı bulunamadı.", 404, null, requestId),
        HttpStatusCode.RequestTimeout => new(AdapterErrorClass.TransientNetwork, "HEPSIBURADA_REMOTE_TIMEOUT", "Hepsiburada isteği kendi tarafında zaman aşımına uğrattı; yeniden denenecek.", 408, retryAfter ?? TimeSpan.FromSeconds(15), requestId),
        HttpStatusCode.Conflict => new(AdapterErrorClass.BusinessConflict, "HEPSIBURADA_CONFLICT", "Hepsiburada isteği kaydın mevcut durumuyla çakıştığı için reddetti.", 409, null, requestId),
        >= HttpStatusCode.InternalServerError => new(AdapterErrorClass.Remote5xx, "HEPSIBURADA_REMOTE_ERROR", "Hepsiburada geçici sunucu hatası verdi.", (int)status, retryAfter ?? TimeSpan.FromSeconds(15), requestId),
        _ => new(AdapterErrorClass.Validation, "HEPSIBURADA_REQUEST_REJECTED", "Hepsiburada isteği doğrulama nedeniyle reddedildi.", (int)status, null, requestId)
    };
}
