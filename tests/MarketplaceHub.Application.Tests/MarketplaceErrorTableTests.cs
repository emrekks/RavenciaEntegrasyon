using System.Net;
using MarketplaceHub.Infrastructure.Adapters.Hepsiburada;
using MarketplaceHub.Infrastructure.Adapters.N11;
using MarketplaceHub.Infrastructure.Adapters.Pazarama;
using MarketplaceHub.Infrastructure.Adapters.PttAvm;
using MarketplaceHub.Infrastructure.Adapters.Shopify;
using MarketplaceHub.Infrastructure.Adapters.Trendyol.ErrorMapping;
using MarketplaceHub.Infrastructure.Adapters.TrendyolEFaturam.ErrorMapping;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// ADAPTOR-TY-HB-7 / ADAPTOR-DIGER-5: one table of how every adapter classes an HTTP error status. n11, Pazarama and
// PTT AVM read the shared table (MarketplaceErrorRules.FromStatus); Hepsiburada, Shopify, Trendyol and E-Faturam keep
// their own. Where adapters differ today the row states it; the table pins behaviour, it does not change it.
//
// Differences kept (each an existing behaviour):
//   - Pazarama 403 (JSON) is PAZARAMA_ACCESS_DENIED with a 15-minute wait (the token was just issued, so the
//     credentials are valid); every other adapter's 403 is a credential refusal.
//   - PTT AVM has no NotFound or Conflict class: 404 and 409 are PTTAVM_REQUEST_REJECTED; any HTML page (not only a
//     403) is its gateway block, checked before its SOAP fault.
//   - Hepsiburada: 403 is HEPSIBURADA_ACCESS_FORBIDDEN, 404 HEPSIBURADA_RESOURCE_NOT_FOUND, waits 5 s (429) and 15 s
//     (5xx); its gateway block is applied in SendAsync, not in Error.
//   - Shopify: 401 and 403 are SHOPIFY_ACCESS_DENIED, other 4xx SHOPIFY_HTTP_ERROR, waits 5 s (429) and 15 s (5xx).
//   - Trendyol: REMOTE_* codes and no default waits (its resilience handler and Retry-After decide); its gateway block
//     is applied in its transport.
//   - E-Faturam: EFATURAM_* codes, 408 waits 30 s (an invoice may exist; it is read back), no default 429/5xx wait.
public sealed class MarketplaceErrorTableTests
{
    private const string Html = "<!DOCTYPE html><html><body>blocked</body></html>";

    public static TheoryData<string, int, bool, AdapterErrorClass, string, int?> Rows() => new()
    {
        // adapter, status, HTML body, class, code, wait in seconds (Retry-After absent)
        { "N11", 401, false, AdapterErrorClass.Authentication, "N11_CREDENTIALS_REJECTED", null },
        { "N11", 403, false, AdapterErrorClass.Authentication, "N11_CREDENTIALS_REJECTED", null },
        { "N11", 403, true, AdapterErrorClass.RateLimit, "N11_GATEWAY_BLOCKED", 900 },
        { "N11", 404, false, AdapterErrorClass.NotFound, "N11_NOT_FOUND", null },
        { "N11", 408, false, AdapterErrorClass.TransientNetwork, "N11_REMOTE_TIMEOUT", 15 },
        { "N11", 409, false, AdapterErrorClass.BusinessConflict, "N11_CONFLICT", null },
        { "N11", 429, false, AdapterErrorClass.RateLimit, "N11_RATE_LIMITED", 60 },
        { "N11", 500, false, AdapterErrorClass.Remote5xx, "N11_REMOTE_ERROR", 30 },
        { "N11", 400, false, AdapterErrorClass.Validation, "N11_REQUEST_REJECTED", null },

        { "PAZARAMA", 401, false, AdapterErrorClass.Authentication, "PAZARAMA_CREDENTIALS_REJECTED", null },
        { "PAZARAMA", 403, false, AdapterErrorClass.Authentication, "PAZARAMA_ACCESS_DENIED", 900 },
        { "PAZARAMA", 403, true, AdapterErrorClass.RateLimit, "PAZARAMA_GATEWAY_BLOCKED", 900 },
        { "PAZARAMA", 404, false, AdapterErrorClass.NotFound, "PAZARAMA_NOT_FOUND", null },
        { "PAZARAMA", 408, false, AdapterErrorClass.TransientNetwork, "PAZARAMA_REMOTE_TIMEOUT", 15 },
        { "PAZARAMA", 409, false, AdapterErrorClass.BusinessConflict, "PAZARAMA_CONFLICT", null },
        { "PAZARAMA", 429, false, AdapterErrorClass.RateLimit, "PAZARAMA_RATE_LIMITED", 60 },
        { "PAZARAMA", 500, false, AdapterErrorClass.Remote5xx, "PAZARAMA_REMOTE_ERROR", 30 },
        { "PAZARAMA", 400, false, AdapterErrorClass.Validation, "PAZARAMA_REQUEST_REJECTED", null },

        { "PTTAVM", 401, false, AdapterErrorClass.Authentication, "PTTAVM_CREDENTIALS_REJECTED", null },
        { "PTTAVM", 403, false, AdapterErrorClass.Authentication, "PTTAVM_CREDENTIALS_REJECTED", null },
        { "PTTAVM", 403, true, AdapterErrorClass.RateLimit, "PTTAVM_GATEWAY_BLOCKED", 900 },
        { "PTTAVM", 500, true, AdapterErrorClass.RateLimit, "PTTAVM_GATEWAY_BLOCKED", 900 },
        { "PTTAVM", 404, false, AdapterErrorClass.Validation, "PTTAVM_REQUEST_REJECTED", null },
        { "PTTAVM", 408, false, AdapterErrorClass.TransientNetwork, "PTTAVM_REMOTE_TIMEOUT", 15 },
        { "PTTAVM", 409, false, AdapterErrorClass.Validation, "PTTAVM_REQUEST_REJECTED", null },
        { "PTTAVM", 429, false, AdapterErrorClass.RateLimit, "PTTAVM_RATE_LIMITED", 60 },
        { "PTTAVM", 500, false, AdapterErrorClass.Remote5xx, "PTTAVM_REMOTE_ERROR", 30 },
        { "PTTAVM", 400, false, AdapterErrorClass.Validation, "PTTAVM_REQUEST_REJECTED", null },

        { "HEPSIBURADA", 401, false, AdapterErrorClass.Authentication, "HEPSIBURADA_CREDENTIALS_REJECTED", null },
        { "HEPSIBURADA", 403, false, AdapterErrorClass.Authentication, "HEPSIBURADA_ACCESS_FORBIDDEN", null },
        { "HEPSIBURADA", 404, false, AdapterErrorClass.NotFound, "HEPSIBURADA_RESOURCE_NOT_FOUND", null },
        { "HEPSIBURADA", 408, false, AdapterErrorClass.TransientNetwork, "HEPSIBURADA_REMOTE_TIMEOUT", 15 },
        { "HEPSIBURADA", 409, false, AdapterErrorClass.BusinessConflict, "HEPSIBURADA_CONFLICT", null },
        { "HEPSIBURADA", 429, false, AdapterErrorClass.RateLimit, "HEPSIBURADA_RATE_LIMITED", 5 },
        { "HEPSIBURADA", 500, false, AdapterErrorClass.Remote5xx, "HEPSIBURADA_REMOTE_ERROR", 15 },
        { "HEPSIBURADA", 400, false, AdapterErrorClass.Validation, "HEPSIBURADA_REQUEST_REJECTED", null },

        { "SHOPIFY", 401, false, AdapterErrorClass.Authentication, "SHOPIFY_ACCESS_DENIED", null },
        { "SHOPIFY", 403, false, AdapterErrorClass.Authentication, "SHOPIFY_ACCESS_DENIED", null },
        { "SHOPIFY", 403, true, AdapterErrorClass.RateLimit, "SHOPIFY_GATEWAY_BLOCKED", 900 },
        { "SHOPIFY", 404, false, AdapterErrorClass.NotFound, "SHOPIFY_NOT_FOUND", null },
        { "SHOPIFY", 408, false, AdapterErrorClass.TransientNetwork, "SHOPIFY_REMOTE_TIMEOUT", 15 },
        { "SHOPIFY", 409, false, AdapterErrorClass.BusinessConflict, "SHOPIFY_CONFLICT", null },
        { "SHOPIFY", 429, false, AdapterErrorClass.RateLimit, "SHOPIFY_RATE_LIMITED", 5 },
        { "SHOPIFY", 500, false, AdapterErrorClass.Remote5xx, "SHOPIFY_REMOTE_ERROR", 15 },
        { "SHOPIFY", 400, false, AdapterErrorClass.Validation, "SHOPIFY_HTTP_ERROR", null },

        { "TRENDYOL", 401, false, AdapterErrorClass.Authentication, "REMOTE_AUTHENTICATION_FAILED", null },
        { "TRENDYOL", 403, false, AdapterErrorClass.Authentication, "REMOTE_AUTHENTICATION_FAILED", null },
        { "TRENDYOL", 404, false, AdapterErrorClass.NotFound, "REMOTE_RESOURCE_NOT_FOUND", null },
        { "TRENDYOL", 408, false, AdapterErrorClass.TransientNetwork, "REMOTE_TIMEOUT", null },
        { "TRENDYOL", 409, false, AdapterErrorClass.BusinessConflict, "REMOTE_CONFLICT", null },
        { "TRENDYOL", 429, false, AdapterErrorClass.RateLimit, "REMOTE_RATE_LIMITED", null },
        { "TRENDYOL", 500, false, AdapterErrorClass.Remote5xx, "REMOTE_SERVER_ERROR", null },
        { "TRENDYOL", 400, false, AdapterErrorClass.Validation, "REMOTE_REQUEST_REJECTED", null },

        { "EFATURAM", 401, false, AdapterErrorClass.Authentication, "EFATURAM_AUTHENTICATION_FAILED", null },
        { "EFATURAM", 403, false, AdapterErrorClass.Authentication, "EFATURAM_AUTHENTICATION_FAILED", null },
        { "EFATURAM", 404, false, AdapterErrorClass.NotFound, "EFATURAM_RESOURCE_NOT_FOUND", null },
        { "EFATURAM", 408, false, AdapterErrorClass.TransientNetwork, "EFATURAM_REMOTE_TIMEOUT", 30 },
        { "EFATURAM", 409, false, AdapterErrorClass.BusinessConflict, "EFATURAM_CONFLICT", null },
        { "EFATURAM", 429, false, AdapterErrorClass.RateLimit, "EFATURAM_RATE_LIMITED", null },
        { "EFATURAM", 500, false, AdapterErrorClass.Remote5xx, "EFATURAM_SERVER_ERROR", null },
        { "EFATURAM", 400, false, AdapterErrorClass.Validation, "EFATURAM_REQUEST_REJECTED", null },
    };

    private static AdapterError Classify(string adapter, HttpStatusCode status, string body) => adapter switch
    {
        "N11" => N11HttpClient.Error(status, body, null),
        "PAZARAMA" => PazaramaHttpClient.Error(status, body, null),
        "PTTAVM" => PttAvmHttpClient.SoapResponse(status, body, "Test").Error!,
        "HEPSIBURADA" => HepsiburadaHttpClient.Error(status, null, null),
        "SHOPIFY" => ShopifyHttpClient.MapHttpError(status, body),
        "TRENDYOL" => TrendyolErrorMapper.FromStatus(status, null, null),
        "EFATURAM" => TrendyolEFaturamErrorMapper.FromStatus(status, null, null),
        _ => throw new ArgumentOutOfRangeException(nameof(adapter))
    };

    [Theory]
    [MemberData(nameof(Rows))]
    public void EveryAdapterClassesAnHttpErrorAsTheTableSays(string adapter, int status, bool html, AdapterErrorClass expectedClass, string expectedCode, int? expectedWaitSeconds)
    {
        var error = Classify(adapter, (HttpStatusCode)status, html ? Html : "");

        Assert.Equal(expectedClass, error.Class);
        Assert.Equal(expectedCode, error.Code);
        Assert.Equal(status, error.HttpStatus);
        Assert.Equal(expectedWaitSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null, error.RetryAfter);
    }

    // ADAPTOR-DIGER-5: the errors of the shared send step itself (no answer came back). Shopify sends through it without a
    // quota and keeps its earlier codes with no wait; the quota-paced adapters wait 15 s.
    [Theory]
    [InlineData("N11", 15)]
    [InlineData("PAZARAMA", 15)]
    [InlineData("PTTAVM", 15)]
    [InlineData("HEPSIBURADA", 15)]
    [InlineData("SHOPIFY", null)]
    public void TheSharedSendStepClassesATimeoutAndANetworkErrorAsTheTableSays(string adapter, int? expectedWaitSeconds)
    {
        var transport = adapter switch
        {
            "N11" => N11HttpClient.Transport,
            "PAZARAMA" => PazaramaHttpClient.Transport,
            "PTTAVM" => PttAvmHttpClient.Transport,
            "HEPSIBURADA" => HepsiburadaHttpClient.Transport,
            _ => ShopifyHttpClient.Transport
        };
        var wait = expectedWaitSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : (TimeSpan?)null;

        var timeout = transport.Timeout();
        Assert.Equal((AdapterErrorClass.TransientNetwork, $"{adapter}_TIMEOUT", (int?)null, wait), (timeout.Class, timeout.Code, timeout.HttpStatus, timeout.RetryAfter));
        var network = transport.NetworkError();
        Assert.Equal((AdapterErrorClass.TransientNetwork, $"{adapter}_NETWORK_ERROR", (int?)null, wait), (network.Class, network.Code, network.HttpStatus, network.RetryAfter));
        var quotaWait = transport.LocalQuotaWait();
        Assert.Equal((AdapterErrorClass.RateLimit, $"{adapter}_LOCAL_QUOTA_WAIT", (int?)429, (TimeSpan?)TimeSpan.FromSeconds(15)), (quotaWait.Class, quotaWait.Code, quotaWait.HttpStatus, quotaWait.RetryAfter));
    }

    [Theory]
    [InlineData("N11")]
    [InlineData("PAZARAMA")]
    [InlineData("PTTAVM")]
    [InlineData("HEPSIBURADA")]
    [InlineData("SHOPIFY")]
    public void ARetryAfterTheMarketplaceSentReplacesTheDefaultWait(string adapter)
    {
        var wait = TimeSpan.FromSeconds(42);
        var error = adapter switch
        {
            "N11" => N11HttpClient.Error(HttpStatusCode.TooManyRequests, "", wait),
            "PAZARAMA" => PazaramaHttpClient.Error(HttpStatusCode.TooManyRequests, "", wait),
            "PTTAVM" => PttAvmHttpClient.SoapResponse(HttpStatusCode.TooManyRequests, "", "Test", wait).Error!,
            "HEPSIBURADA" => HepsiburadaHttpClient.Error(HttpStatusCode.TooManyRequests, wait, null),
            _ => ShopifyHttpClient.MapHttpError(HttpStatusCode.TooManyRequests, "", wait)
        };
        Assert.Equal(wait, error.RetryAfter);
    }
}
