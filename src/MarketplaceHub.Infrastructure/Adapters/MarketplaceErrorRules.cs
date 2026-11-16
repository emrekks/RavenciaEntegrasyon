using System.Net;
using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Adapters;

// The error rules every marketplace adapter shares, so one kind of failure is classed the same way everywhere.
//
// A 403 whose body is an HTML page never reached the marketplace's API: its security gateway (WAF) turned the call
// away, which comes and goes (live on Pazarama and PTT AVM: one call passes, the next is blocked). It is a temporary
// wait of a quarter hour (RateLimit, so the job keeps its attempt and the connection backs off), never a refusal of the
// credentials and never an outcome-unknown write: the request did not arrive. A 401 stays a credential refusal.
//
// An unexplained server fault (5xx without the marketplace's own answer) is Remote5xx on every marketplace: a read
// retries it; a write treats it as outcome unknown (WriteOutcome.IsUncertain) and reads the result back before any resend.
internal static class MarketplaceErrorRules
{
    public static readonly TimeSpan GatewayBlockWait = TimeSpan.FromMinutes(15);

    public static bool LooksLikeHtml(string? body)
    {
        if (string.IsNullOrEmpty(body)) return false;
        var trimmed = body.AsSpan().TrimStart();
        return trimmed.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("<html", StringComparison.OrdinalIgnoreCase);
    }

    // The texts an adapter shows for each status class (its own words, its own advice). A null NotFound or Conflict
    // means the adapter has no class of its own for that status: it is a plain refusal ({PREFIX}_REQUEST_REJECTED).
    internal sealed record StatusTexts(string CredentialsRejected, string RateLimited, string? NotFound, string RemoteTimeout, string? Conflict, string RemoteError, string RequestRejected);

    public static readonly TimeSpan RateLimitedWait = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan RemoteTimeoutWait = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan RemoteErrorWait = TimeSpan.FromSeconds(30);

    // ADAPTOR-DIGER-5: the HTTP status table n11 (REST), Pazarama and PTT AVM (SOAP, after its fault check) share:
    //   401, 403  Authentication     {P}_CREDENTIALS_REJECTED   (a 403 HTML page is the gateway block above)
    //   404       NotFound           {P}_NOT_FOUND              (Validation {P}_REQUEST_REJECTED when NotFound is null)
    //   408       TransientNetwork   {P}_REMOTE_TIMEOUT         Retry-After, else 15 s
    //   409       BusinessConflict   {P}_CONFLICT               (Validation {P}_REQUEST_REJECTED when Conflict is null)
    //   429       RateLimit          {P}_RATE_LIMITED           Retry-After, else 60 s
    //   5xx       Remote5xx          {P}_REMOTE_ERROR           Retry-After, else 30 s
    //   other     Validation         {P}_REQUEST_REJECTED
    // overrides answers first for a status an adapter treats its own way (Pazarama's 403), null to use the table.
    // Hepsiburada, Shopify, Trendyol and E-Faturam keep their own tables (other codes and waits); the differences are
    // listed by MarketplaceErrorTableTests.
    internal static AdapterError FromStatus(string codePrefix, string marketplaceName, HttpStatusCode status, string? body, TimeSpan? retryAfter, StatusTexts texts, Func<HttpStatusCode, AdapterError?>? overrides = null)
    {
        if (overrides?.Invoke(status) is { } special) return special;
        if (GatewayBlocked(status, body, marketplaceName, codePrefix) is { } gatewayBlock) return gatewayBlock;
        var code = (int)status;
        return status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new(AdapterErrorClass.Authentication, $"{codePrefix}_CREDENTIALS_REJECTED", texts.CredentialsRejected, code, null, null),
            HttpStatusCode.TooManyRequests => new(AdapterErrorClass.RateLimit, $"{codePrefix}_RATE_LIMITED", texts.RateLimited, code, retryAfter ?? RateLimitedWait, null),
            HttpStatusCode.NotFound when texts.NotFound is { } notFound => new(AdapterErrorClass.NotFound, $"{codePrefix}_NOT_FOUND", notFound, code, null, null),
            HttpStatusCode.RequestTimeout => new(AdapterErrorClass.TransientNetwork, $"{codePrefix}_REMOTE_TIMEOUT", texts.RemoteTimeout, code, retryAfter ?? RemoteTimeoutWait, null),
            HttpStatusCode.Conflict when texts.Conflict is { } conflict => new(AdapterErrorClass.BusinessConflict, $"{codePrefix}_CONFLICT", conflict, code, null, null),
            >= HttpStatusCode.InternalServerError => new(AdapterErrorClass.Remote5xx, $"{codePrefix}_REMOTE_ERROR", texts.RemoteError, code, retryAfter ?? RemoteErrorWait, null),
            _ => new(AdapterErrorClass.Validation, $"{codePrefix}_REQUEST_REJECTED", texts.RequestRejected, code, null, null)
        };
    }

    // The gateway block of a 403 HTML page, or null for any other answer. Code: {PREFIX}_GATEWAY_BLOCKED.
    public static AdapterError? GatewayBlocked(HttpStatusCode status, string? body, string marketplaceName, string codePrefix, string? remoteRequestId = null) =>
        status == HttpStatusCode.Forbidden && LooksLikeHtml(body)
            ? new(AdapterErrorClass.RateLimit, $"{codePrefix}_GATEWAY_BLOCKED",
                $"{marketplaceName} güvenlik geçidi isteği engelledi (API yerine HTML sayfa döndü). Geçici olabilir; 15 dakika sonra yeniden denenir. Sürerse {marketplaceName} destekten sunucu adresinin izinli listeye alınmasını isteyin.",
                (int)status, GatewayBlockWait, remoteRequestId)
            : null;
}
