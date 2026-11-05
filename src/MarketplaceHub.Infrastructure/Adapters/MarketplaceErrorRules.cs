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

    // The gateway block of a 403 HTML page, or null for any other answer. Code: {PREFIX}_GATEWAY_BLOCKED.
    public static AdapterError? GatewayBlocked(HttpStatusCode status, string? body, string marketplaceName, string codePrefix, string? remoteRequestId = null) =>
        status == HttpStatusCode.Forbidden && LooksLikeHtml(body)
            ? new(AdapterErrorClass.RateLimit, $"{codePrefix}_GATEWAY_BLOCKED",
                $"{marketplaceName} güvenlik geçidi isteği engelledi (API yerine HTML sayfa döndü). Geçici olabilir; 15 dakika sonra yeniden denenir. Sürerse {marketplaceName} destekten sunucu adresinin izinli listeye alınmasını isteyin.",
                (int)status, GatewayBlockWait, remoteRequestId)
            : null;
}
