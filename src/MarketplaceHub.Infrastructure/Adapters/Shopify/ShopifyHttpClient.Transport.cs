using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Adapters.Http;
using Microsoft.Extensions.Logging;

namespace MarketplaceHub.Infrastructure.Adapters.Shopify;

public sealed partial class ShopifyHttpClient
{
    private async Task<AdapterResult<JsonDocument>> QueryAsync(ShopifyRequestContext context, string query, object? variables = null, CancellationToken cancellationToken = default)
    {
        var client = clients.CreateBoundedClient("Shopify");
        var endpoint = new Uri($"https://{context.Shop}.myshopify.com/admin/api/{context.ApiVersion}/graphql.json");
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new StringContent(JsonSerializer.Serialize(new { query, variables }), Encoding.UTF8, "application/json") };
        request.Headers.TryAddWithoutValidation("X-Shopify-Access-Token", context.AccessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        // The client's 45 s timeout cancels inside the pipeline; the circuit tells it from the caller's cancellation.
        CallerCancellation.Mark(request, cancellationToken);
        try
        {
            using var response = await client.SendAsync(request, cancellationToken);
            // The panel's own open circuit refused it: never sent, so never an outcome-unknown write.
            if (LocalCircuitResponse.Refusal(response, "SHOPIFY", "Shopify") is { } localCircuit) return Failure<JsonDocument>(localCircuit);
            var raw = await response.Content.ReadAsStringAsync(cancellationToken);
            // An error answer may be an HTML page (a 502 or 503 from Shopify's edge); it is classed by its
            // status instead of failing as unreadable JSON.
            JsonDocument? parsed = null;
            try { parsed = JsonDocument.Parse(raw); }
            catch (JsonException) when (!response.IsSuccessStatusCode) { }
            // A success status whose body is not JSON (an HTML page from a proxy, a cut-off body) is a broken contract,
            // reported as one instead of escaping the adapter as an exception.
            catch (JsonException)
            {
                var requestId = response.Headers.TryGetValues("X-Request-ID", out var ids) ? ids.FirstOrDefault() : null;
                return Failure<JsonDocument>(new(AdapterErrorClass.ContractViolation, "SHOPIFY_RESPONSE_NOT_JSON", "Shopify başarılı yanıtı JSON değil; yanıt okunamadı.", (int)HttpStatusCode.BadGateway, null, requestId));
            }
            using var document = parsed;
            var rate = document is null ? null : ReadRateLimit(document);
            if (document is not null) LogQueryCost(document, context.Shop);
            var remoteRequestId = response.Headers.TryGetValues("X-Request-ID", out var requestIds) ? requestIds.FirstOrDefault() : null;
            // Shopify answers with the oldest supported version when the asked one is retired.
            if (response.Headers.TryGetValues("X-Shopify-API-Version", out var servedVersions) && servedVersions.FirstOrDefault() is { Length: > 0 } servedVersion
                && !string.Equals(servedVersion, context.ApiVersion, StringComparison.OrdinalIgnoreCase))
                logger.LogWarning("Shopify served API version {ServedVersion} instead of {RequestedVersion} for {Shop}", servedVersion, context.ApiVersion, context.Shop);
            if (!response.IsSuccessStatusCode || document is null)
            {
                // ADAPTOR-DIGER-2: a client-credentials token Shopify refuses (the app reinstalled, the token revoked) is
                // dropped from the cache, so the next request asks for a new one instead of reusing it until it expires.
                if (response.StatusCode == HttpStatusCode.Unauthorized && context.ExpiresAt != DateTimeOffset.MaxValue)
                    authentication.Forget(context.Connection.Id);
                var retryAfter = RetryAfterHeader.Unchecked(response.Headers.RetryAfter, timeProvider.GetUtcNow());
                return Failure<JsonDocument>(MapHttpError(response.StatusCode, raw, retryAfter, remoteRequestId), rate);
            }
            if (GraphQlError(document.RootElement, (int)response.StatusCode, remoteRequestId) is { } graphQlError)
                return Failure<JsonDocument>(graphQlError, rate);
            if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            {
                return Failure<JsonDocument>(new(
                    AdapterErrorClass.ContractViolation,
                    "SHOPIFY_GRAPHQL_CONTRACT_INVALID",
                    "Shopify GraphQL yanıtı beklenen veri alanını içermiyor.",
                    (int)HttpStatusCode.BadGateway,
                    null,
                    remoteRequestId), rate);
            }

            // Shopify GraphQL wraps every successful result in a top-level
            // `data` object. Keep the adapter contract flat so the catalog,
            // order and connection checks can consume the queried fields
            // consistently.
            return AdapterResult<JsonDocument>.Success(JsonDocument.Parse(data.GetRawText()), rate);
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "Shopify GraphQL isteği başarısız. Shop: {Shop}", context.Shop);
            return Failure<JsonDocument>(new(AdapterErrorClass.TransientNetwork, "SHOPIFY_NETWORK_ERROR", "Shopify bağlantısı geçici olarak kurulamadı.", null, null, null));
        }
        // The client's timeout (not the caller's cancellation) ended the request.
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Shopify GraphQL isteği zaman aşımına uğradı. Shop: {Shop}", context.Shop);
            return Failure<JsonDocument>(new(AdapterErrorClass.TransientNetwork, "SHOPIFY_TIMEOUT", "Shopify isteği zaman aşımına uğradı.", null, null, null));
        }
    }
    internal static AdapterError MapHttpError(HttpStatusCode status, string body, TimeSpan? retryAfter = null, string? remoteRequestId = null) =>
        MarketplaceErrorRules.GatewayBlocked(status, body, "Shopify", "SHOPIFY", remoteRequestId) ?? MapHttpStatus(status, retryAfter, remoteRequestId);

    private static AdapterError MapHttpStatus(HttpStatusCode status, TimeSpan? retryAfter, string? remoteRequestId) => status switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new(AdapterErrorClass.Authentication, "SHOPIFY_ACCESS_DENIED", "Shopify erişim izni reddedildi.", (int)status, null, remoteRequestId),
        (HttpStatusCode)429 => new(AdapterErrorClass.RateLimit, "SHOPIFY_RATE_LIMITED", "Shopify API sınırına ulaşıldı.", (int)status, retryAfter ?? TimeSpan.FromSeconds(5), remoteRequestId),
        HttpStatusCode.NotFound => new(AdapterErrorClass.NotFound, "SHOPIFY_NOT_FOUND", "Shopify istenen kaynağı bulamadı (404).", (int)status, null, remoteRequestId),
        HttpStatusCode.RequestTimeout => new(AdapterErrorClass.TransientNetwork, "SHOPIFY_REMOTE_TIMEOUT", "Shopify isteği kendi tarafında zaman aşımına uğrattı; yeniden denenecek.", (int)status, retryAfter ?? TimeSpan.FromSeconds(15), remoteRequestId),
        HttpStatusCode.Conflict => new(AdapterErrorClass.BusinessConflict, "SHOPIFY_CONFLICT", "Shopify isteği kaydın mevcut durumuyla çakıştığı için reddetti.", (int)status, null, remoteRequestId),
        >= HttpStatusCode.InternalServerError => new(AdapterErrorClass.Remote5xx, "SHOPIFY_REMOTE_ERROR", "Shopify geçici bir sunucu hatası döndürdü.", (int)status, retryAfter ?? TimeSpan.FromSeconds(15), remoteRequestId),
        _ => new(AdapterErrorClass.Validation, "SHOPIFY_HTTP_ERROR", $"Shopify isteği reddedildi ({(int)status}).", (int)status, null, remoteRequestId)
    };
    internal const string QueryCostExceededCode = "SHOPIFY_QUERY_COST_EXCEEDED";

    internal const string GraphQlInternalErrorCode = "SHOPIFY_INTERNAL_ERROR";

    // O13: every entry of a GraphQL `errors` array is read, not only the first. Shopify reports rate limiting
    // (THROTTLED) and its own failures (INTERNAL_SERVER_ERROR) in a 200 body instead of 429/500, and documents
    // both as "retry the request" (shopify.dev/docs/api/usage/response-codes, GraphQL error handling). One
    // retryable entry makes the whole answer retryable; then the cost ceiling, a missing scope and the first
    // message, in that order. Null when the body carries no errors.
    internal static AdapterError? GraphQlError(JsonElement root, int httpStatus, string? remoteRequestId)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("errors", out var errors) || errors.ValueKind != JsonValueKind.Array || errors.GetArrayLength() == 0)
            return null;
        var entries = errors.EnumerateArray().Where(entry => entry.ValueKind == JsonValueKind.Object).ToList();
        static string? Code(JsonElement entry) =>
            entry.TryGetProperty("extensions", out var extensions) && extensions.ValueKind == JsonValueKind.Object
            && extensions.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String ? code.GetString() : null;
        JsonElement? With(params string[] codes) =>
            entries.Cast<JsonElement?>().FirstOrDefault(entry => codes.Any(known => string.Equals(Code(entry!.Value), known, StringComparison.OrdinalIgnoreCase)));

        if (With("THROTTLED") is not null)
            return new(AdapterErrorClass.RateLimit, "SHOPIFY_RATE_LIMITED", "Shopify API sınırına ulaşıldı; bağlantı bazında yeniden denenecek.",
                httpStatus, ThrottleWait(root) ?? TimeSpan.FromSeconds(5), remoteRequestId);
        if (With("INTERNAL_SERVER_ERROR") is not null)
            return new(AdapterErrorClass.Remote5xx, GraphQlInternalErrorCode, "Shopify kendi tarafında bir hata bildirdi; istek yeniden denenecek.",
                httpStatus, TimeSpan.FromSeconds(30), remoteRequestId);
        // A query over Shopify's single-query cost ceiling: the caller may ask again for a smaller page.
        if (With("MAX_COST_EXCEEDED") is not null)
            return new(AdapterErrorClass.Validation, QueryCostExceededCode, "Shopify sorgusu tek sorgu maliyet sınırını aştı.", httpStatus, null, remoteRequestId);
        if (With("ACCESS_DENIED", "FORBIDDEN") is { } denied)
        {
            var deniedField = denied.TryGetProperty("path", out var path) && path.ValueKind == JsonValueKind.Array
                ? string.Join(".", path.EnumerateArray()
                    .Select(value => value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString())
                    .Where(value => !string.IsNullOrWhiteSpace(value)))
                : null;
            return new(AdapterErrorClass.Authentication, "SHOPIFY_REQUIRED_READ_SCOPE",
                $"Shopify API izni eksik{(string.IsNullOrWhiteSpace(deniedField) ? string.Empty : $" ({deniedField})")}. read_products, read_inventory, read_orders, read_customers, read_locations, write_products ve write_inventory izinlerini kontrol edin.",
                httpStatus, null, remoteRequestId);
        }
        var message = entries.Select(entry => entry.TryGetProperty("message", out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() : null)
            .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text));
        return new(AdapterErrorClass.Validation, "SHOPIFY_GRAPHQL_ERROR", message ?? "Shopify GraphQL isteği reddedildi.", httpStatus, null, remoteRequestId);
    }

    // Queries close to Shopify's 1000-point ceiling are logged with their requested and actual cost, so the
    // nested page sizes can be tuned from real numbers.
    private void LogQueryCost(JsonDocument document, string shop)
    {
        if (!document.RootElement.TryGetProperty("extensions", out var extensions) || !extensions.TryGetProperty("cost", out var cost)
            || RemoteJson.Int32(cost, "requestedQueryCost") is not { } requested) return;
        if (requested < 500) return;
        int? actual = cost.TryGetProperty("actualQueryCost", out var actualElement) && actualElement.ValueKind == JsonValueKind.Number && actualElement.TryGetInt32(out var actualValue) ? actualValue : null;
        logger.LogInformation("Shopify query for {Shop} requested {RequestedCost} points (actual {ActualCost})", shop, requested, actual);
    }

    // A THROTTLED query waits until the bucket holds what the query asked for ((requested − available) /
    // restoreRate), not until the whole bucket has refilled. At least one second; null without cost figures.
    internal static TimeSpan? ThrottleWait(JsonElement root)
    {
        if (!root.TryGetProperty("extensions", out var extensions) || !extensions.TryGetProperty("cost", out var cost)
            || RemoteJson.Double(cost, "requestedQueryCost") is not { } requested
            || !cost.TryGetProperty("throttleStatus", out var throttle)
            || RemoteJson.Double(throttle, "currentlyAvailable") is not { } available
            || RemoteJson.Double(throttle, "restoreRate") is not { } restoreRate || restoreRate <= 0)
            return null;
        var seconds = Math.Ceiling(Math.Max(0, requested - available) / restoreRate);
        return TimeSpan.FromSeconds(Math.Max(1, seconds));
    }

    private static RateLimitMetadata? ReadRateLimit(JsonDocument document)
    {
        if (!document.RootElement.TryGetProperty("extensions", out var extensions)
            || !extensions.TryGetProperty("cost", out var cost)
            || !cost.TryGetProperty("throttleStatus", out var throttle)
            || RemoteJson.Int32(throttle, "currentlyAvailable") is not { } available) return null;
        var maximum = RemoteJson.Int32(throttle, "maximumAvailable");
        var restoreRate = RemoteJson.Double(throttle, "restoreRate");
        TimeSpan? retryAfter = maximum is { } limit && restoreRate is > 0 && available < limit
            ? TimeSpan.FromSeconds((limit - available) / restoreRate.Value)
            : null;
        return new(available, null, retryAfter);
    }
}
