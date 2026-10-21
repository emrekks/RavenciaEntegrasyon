using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MarketplaceHub.Application;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarketplaceHub.Infrastructure.Adapters.PttAvm;

// PTT AVM REST adapter (integration-api.pttavm.com/api/v1). Every call carries the Api-Key and
// Access-Token headers produced in the seller panel and a fresh X-Correlation-Id. Category answers are
// wrapped in {success, ..., error:{error_code, error_message}}; order and product lists are bare arrays.
// The flows follow the phases in docs/PTTAVM-ENTEGRASYON-PLANI.md; phase 1 is the connection test and
// read-only probe.
public sealed class PttAvmHttpClient(
    IHttpClientFactory clients,
    PttAvmAuthenticationHandler authentication,
    IOptions<PttAvmOptions> options,
    TimeProvider timeProvider,
    ILogger<PttAvmHttpClient> logger,
    IConfiguration configuration)
    : IConnectionPort
{
    internal const string ClientName = "PttAvm";
    internal const string DocumentationHost = "https://developers.pttavm.com/tr/";
    internal const string MainCategories = "categories/main";
    internal const string Orders = "orders/search";
    internal const string ProductsByBarcode = "products/get-by-barcodes";
    // A barcode no store uses: the product probe only proves the list answers, it never reads a product.
    internal const string ProbeBarcode = "RAVENCIA-CONNECTION-PROBE";
    // PTT AVM works in Turkey time; order dates are sent as plain dates.
    internal static readonly TimeSpan TurkeyOffset = TimeSpan.FromHours(3);
    private readonly PttAvmOptions settings = options.Value;

    // Every PTT AVM write goes through this gate: the server-wide switch, the connection switch,
    // a production connection that was verified or activated. Callers above check the same switches
    // when they queue work; this keeps a write from leaving even when a caller forgets.
    internal AdapterResult<T>? WriteBlocked<T>(PttAvmRequestContext account, string what)
    {
        var connection = account.Connection;
        var allowed = configuration.GetValue<bool>("FeatureFlags:ExternalWrites")
            && ConnectionWritesEnabled(connection.SettingsJson)
            && string.Equals(connection.Environment, "PRODUCTION", StringComparison.OrdinalIgnoreCase)
            && (string.Equals(connection.Status, "ACTIVE", StringComparison.OrdinalIgnoreCase) || string.Equals(connection.Status, "VERIFIED", StringComparison.OrdinalIgnoreCase));
        return allowed
            ? null
            : Failure<T>(AdapterErrorClass.Validation, "PTTAVM_EXTERNAL_WRITES_DISABLED", $"PTT AVM bağlantısında dış yazma kapalı; {what} gönderilmedi.", HttpStatusCode.Forbidden);
    }

    internal static bool ConnectionWritesEnabled(string settingsJson)
    {
        try
        {
            using var document = JsonDocument.Parse(settingsJson);
            return document.RootElement.TryGetProperty("ExternalWritesEnabled", out var enabled) && enabled.ValueKind == JsonValueKind.True;
        }
        catch (JsonException) { return false; }
    }

    public async Task<AdapterResult<ConnectionIdentity>> TestAsync(AdapterContext context, CancellationToken cancellationToken)
    {
        var account = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        return account is null ? CredentialMissing<ConnectionIdentity>() : await TestAsync(account, cancellationToken);
    }

    // The main category list is the cheapest authenticated read: one small answer, no filters.
    internal async Task<AdapterResult<ConnectionIdentity>> TestAsync(PttAvmRequestContext account, CancellationToken cancellationToken)
    {
        var result = await SendAsync(account, HttpMethod.Get, MainCategories, cancellationToken);
        if (!result.IsSuccess) return AdapterResult<ConnectionIdentity>.Failure(result.Error!, result.RateLimit);
        using var document = result.Value!;
        if (!MainCategoryList(document.RootElement))
            return Failure<ConnectionIdentity>(AdapterErrorClass.ContractViolation, "PTTAVM_CONNECTION_CONTRACT_INVALID", $"PTT AVM ana kategori yanıtı beklenen yapıda değil (yanıt alanları: {ShapeOf(document.RootElement)}).", HttpStatusCode.BadGateway);
        var connection = account.Connection;
        return AdapterResult<ConnectionIdentity>.Success(
            new("PTTAVM", connection.Environment, connection.ExternalStoreId, connection.ApiVersion, connection.ExternalStoreId),
            result.RateLimit);
    }

    public async Task<AdapterResult<IReadOnlyList<CapabilityEvidence>>> DiscoverCapabilitiesAsync(AdapterContext context, CancellationToken cancellationToken)
    {
        var account = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        return account is null ? CredentialMissing<IReadOnlyList<CapabilityEvidence>>() : await DiscoverCapabilitiesAsync(account, cancellationToken);
    }

    // Read-only probe: the category list of the connection test, yesterday's and today's orders and a
    // barcode lookup for a barcode no store has. A capability whose answer does not have the documented
    // shape stays UNKNOWN; write capabilities stay UNKNOWN until a controlled first write on the live store.
    internal async Task<AdapterResult<IReadOnlyList<CapabilityEvidence>>> DiscoverCapabilitiesAsync(PttAvmRequestContext account, CancellationToken cancellationToken)
    {
        var connection = await TestAsync(account, cancellationToken);
        if (!connection.IsSuccess) return AdapterResult<IReadOnlyList<CapabilityEvidence>>.Failure(connection.Error!, connection.RateLimit);
        var identity = connection.Value!;
        var now = timeProvider.GetUtcNow();

        var orders = await SendAsync(account, HttpMethod.Get, OrderProbePath(now), cancellationToken);
        var ordersReadable = orders.IsSuccess && orders.Value!.RootElement.ValueKind == JsonValueKind.Array;
        var ordersShape = orders.IsSuccess ? ShapeOf(orders.Value!.RootElement) : null;
        orders.Value?.Dispose();
        var products = await SendAsync(account, HttpMethod.Post, ProductsByBarcode, cancellationToken, Json(new { barcodes = new[] { ProbeBarcode } }));
        var productsReadable = products.IsSuccess && products.Value!.RootElement.ValueKind == JsonValueKind.Array;
        var productsShape = products.IsSuccess ? ShapeOf(products.Value!.RootElement) : null;
        products.Value?.Dispose();

        IReadOnlyList<CapabilityEvidence> evidence =
        [
            new(MarketplaceCapabilities.ConnectionTest, "SUPPORTED", identity.ApiVersion, identity.Environment, identity.ExternalStoreId, DocumentationHost + "katalog-entegrasyonu/ana-kategori-listesi", "rest", null, null, "PTT AVM API Key ve Access Token kabul edildi; ana kategori listesi salt okunur yanıt verdi.", null, now),
            new(MarketplaceCapabilities.ReferenceRead, "SUPPORTED", identity.ApiVersion, identity.Environment, identity.ExternalStoreId, DocumentationHost + "katalog-entegrasyonu/ana-kategori-listesi", "rest", null, null, "Ana kategori listesi salt okunur okundu.", null, now),
            new(MarketplaceCapabilities.OrderRead, ordersReadable ? "SUPPORTED" : "UNKNOWN", identity.ApiVersion, identity.Environment, identity.ExternalStoreId, DocumentationHost + "siparis-entegrasyonu/siparis-kontrol-v2", "rest", null, null,
                ordersReadable ? "Dünün ve bugünün siparişleri salt okunur listelendi." : $"Sipariş listeleme denemesi tamamlanamadı ({orders.Error?.Code ?? $"PTTAVM_CONTRACT_INVALID: {ordersShape}"}); yetenek UNKNOWN bırakıldı.", null, now),
            new(MarketplaceCapabilities.ProductRead, productsReadable ? "SUPPORTED" : "UNKNOWN", identity.ApiVersion, identity.Environment, identity.ExternalStoreId, DocumentationHost + "katalog-entegrasyonu/barkod-kontrol-bulk", "rest", null, null,
                productsReadable ? "Barkodla ürün sorgusu salt okunur yanıt verdi." : $"Barkodla ürün sorgusu tamamlanamadı ({products.Error?.Code ?? $"PTTAVM_CONTRACT_INVALID: {productsShape}"}); yetenek UNKNOWN bırakıldı.", null, now)
        ];
        return AdapterResult<IReadOnlyList<CapabilityEvidence>>.Success(evidence, connection.RateLimit ?? orders.RateLimit);
    }

    // orders/search takes plain dates, at most 40 days apart; isActiveOrders=false returns every status.
    internal static string OrderProbePath(DateTimeOffset now)
    {
        var today = now.ToOffset(TurkeyOffset).Date;
        return $"{Orders}?startDate={Day(today.AddDays(-1))}&endDate={Day(today)}&isActiveOrders=false";
    }

    internal static string Day(DateTime value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    // The documented answer of categories/main: {success: true, main_category: [...]}.
    internal static bool MainCategoryList(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object
        && Member(root, "main_category") is { ValueKind: JsonValueKind.Array };

    private static readonly TimeSpan MaximumQuotaWait = TimeSpan.FromMinutes(5);

    // The headers of one call. The documentation lists Api-Key, Access-Token and X-Correlation-Id as
    // required; a new correlation id per request lets PTT AVM support find a single call.
    internal static HttpRequestMessage BuildRequest(PttAvmRequestContext account, HttpMethod method, Uri address, string? body)
    {
        var request = new HttpRequestMessage(method, address);
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        request.Headers.TryAddWithoutValidation("Api-Key", account.ApiKey);
        request.Headers.TryAddWithoutValidation("Access-Token", account.AccessToken);
        request.Headers.TryAddWithoutValidation("X-Correlation-Id", Guid.NewGuid().ToString());
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Options.Set(PttAvmRateLimitHandler.AccountKeyOption, PttAvmRateLimitHandler.StoreKey(account.AccessToken));
        return request;
    }

    private async Task<AdapterResult<JsonDocument>> SendAsync(PttAvmRequestContext account, HttpMethod method, string path, CancellationToken cancellationToken, HttpContent? content = null)
    {
        var body = content is null ? null : await content.ReadAsStringAsync(cancellationToken);
        using var request = BuildRequest(account, method, new Uri(settings.BaseAddress, path), body);
        // The request timeout starts when the quota token is taken; the wait for it has its own, longer cap.
        var requestTimeout = settings.Timeout > TimeSpan.Zero && settings.Timeout < TimeSpan.FromMinutes(2) ? settings.Timeout : TimeSpan.FromSeconds(30);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(requestTimeout + MaximumQuotaWait);
        var quotaAcquired = false;
        request.Options.Set(PttAvmRateLimitHandler.QuotaAcquiredKey, () => { quotaAcquired = true; timeout.CancelAfter(requestTimeout); });
        CallerCancellation.Mark(request, cancellationToken);
        try
        {
            using var response = await clients.CreateBoundedClient(ClientName).SendAsync(request, timeout.Token);
            var text = await response.Content.ReadAsStringAsync(timeout.Token);
            var retryAfter = response.Headers.RetryAfter?.Delta
                ?? (response.Headers.RetryAfter?.Date is { } retryDate ? retryDate - timeProvider.GetUtcNow() : null);
            var rate = retryAfter is null ? null : new RateLimitMetadata(null, null, retryAfter);
            if (!response.IsSuccessStatusCode)
            {
                var error = Error(response.StatusCode, text, retryAfter);
                // The answer's outline (field names or "JSON değil", never its values) tells a gateway page from an API refusal.
                logger.LogWarning("PTT AVM API isteği reddedildi. ConnectionId: {ConnectionId}, Path: {Path}, Status: {Status}, Code: {Code}, Message: {Message}, Body: {BodyShape} ({ContentType}, {BodyLength} bayt)", account.Connection.Id, request.RequestUri!.AbsolutePath, (int)response.StatusCode, error.Code, error.SafeMessage, ShapeOf(text), response.Content.Headers.ContentType?.MediaType ?? "içerik tipi yok", text.Length);
                return AdapterResult<JsonDocument>.Failure(error, rate);
            }
            JsonDocument document;
            try { document = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text); }
            catch (JsonException)
            {
                return AdapterResult<JsonDocument>.Failure(new(AdapterErrorClass.ContractViolation, "PTTAVM_JSON_INVALID", "PTT AVM geçerli JSON yanıtı vermedi.", (int)response.StatusCode, null, null), rate);
            }
            if (EnvelopeError(document.RootElement) is { } refused)
            {
                document.Dispose();
                logger.LogWarning("PTT AVM isteği reddetti. ConnectionId: {ConnectionId}, Path: {Path}, Code: {Code}, Message: {Message}", account.Connection.Id, request.RequestUri!.AbsolutePath, refused.Code, refused.SafeMessage);
                return AdapterResult<JsonDocument>.Failure(refused, rate);
            }
            return AdapterResult<JsonDocument>.Success(document, rate);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Never sent: the quota stayed full. This is not an "outcome unknown" write.
            if (!quotaAcquired)
                return AdapterResult<JsonDocument>.Failure(new(AdapterErrorClass.RateLimit, "PTTAVM_LOCAL_QUOTA_WAIT", "PTT AVM istek kotası dolu olduğu için istek gönderilmedi; yeniden denenecek.", 429, TimeSpan.FromSeconds(15), null));
            return AdapterResult<JsonDocument>.Failure(new(AdapterErrorClass.TransientNetwork, "PTTAVM_TIMEOUT", "PTT AVM isteği zaman aşımına uğradı.", null, TimeSpan.FromSeconds(15), null));
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "PTT AVM API isteği başarısız. ConnectionId: {ConnectionId}", account.Connection.Id);
            return AdapterResult<JsonDocument>.Failure(new(AdapterErrorClass.TransientNetwork, "PTTAVM_NETWORK_ERROR", "PTT AVM bağlantısı geçici olarak kurulamadı.", null, TimeSpan.FromSeconds(15), null));
        }
    }

    internal static AdapterError Error(HttpStatusCode status, string body, TimeSpan? retryAfter)
    {
        var remote = RemoteMessage(body);
        return status switch
        {
            // A 403 that is an HTML page never reached the API: a security gateway blocked the call, which comes and goes.
            HttpStatusCode.Forbidden when remote is null && LooksLikeHtml(body) => new(AdapterErrorClass.RateLimit, "PTTAVM_GATEWAY_BLOCKED",
                "PTT AVM'nin güvenlik geçidi isteği engelledi (API yerine HTML sayfa döndü). Geçici olabilir; 15 dakika sonra yeniden denenir.", (int)status, TimeSpan.FromMinutes(15), null),
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new(AdapterErrorClass.Authentication, "PTTAVM_CREDENTIALS_REJECTED",
                remote is null ? PttAvmErrorAdvice.CredentialsRejected : $"{PttAvmErrorAdvice.CredentialsRejected} PTT AVM yanıtı: {remote}", (int)status, null, null),
            HttpStatusCode.TooManyRequests => new(AdapterErrorClass.RateLimit, "PTTAVM_RATE_LIMITED", "PTT AVM istek sınırına ulaşıldı; işlem biraz sonra yeniden denenecek.", (int)status, retryAfter ?? TimeSpan.FromSeconds(60), null),
            HttpStatusCode.NotFound => new(AdapterErrorClass.NotFound, "PTTAVM_NOT_FOUND", remote is null ? "PTT AVM istenen kaydı bulamadı." : $"PTT AVM kaydı bulamadı: {remote}", (int)status, null, null),
            HttpStatusCode.RequestTimeout => new(AdapterErrorClass.TransientNetwork, "PTTAVM_REMOTE_TIMEOUT", "PTT AVM isteği kendi tarafında zaman aşımına uğrattı; işlem yeniden denenecek.", (int)status, retryAfter ?? TimeSpan.FromSeconds(15), null),
            >= HttpStatusCode.InternalServerError => new(AdapterErrorClass.Remote5xx, "PTTAVM_REMOTE_ERROR", "PTT AVM servisi geçici olarak hata veriyor; işlem yeniden denenecek.", (int)status, retryAfter ?? TimeSpan.FromSeconds(30), null),
            _ => new(AdapterErrorClass.Validation, "PTTAVM_REQUEST_REJECTED", remote is null ? "PTT AVM isteği reddetti." : $"PTT AVM isteği reddetti: {PttAvmErrorAdvice.WithAdvice(remote)}", (int)status, null, null)
        };
    }

    internal static bool LooksLikeHtml(string body)
    {
        var trimmed = body.AsSpan().TrimStart();
        return trimmed.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("<html", StringComparison.OrdinalIgnoreCase);
    }

    // A 200 answer can still say success=false with error.error_message meant for the seller.
    internal static AdapterError? EnvelopeError(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || Member(root, "success") is not { ValueKind: JsonValueKind.False }) return null;
        var message = ErrorMessage(root);
        var code = Member(root, "error") is { ValueKind: JsonValueKind.Object } error ? Text(error, "error_code") : null;
        return new(AdapterErrorClass.Validation, "PTTAVM_REQUEST_REJECTED",
            message is null ? code is null ? "PTT AVM isteği reddetti." : $"PTT AVM isteği reddetti (kod {code})." : $"PTT AVM isteği reddetti: {PttAvmErrorAdvice.WithAdvice(message)}",
            null, null, null);
    }

    internal static string? RemoteMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object ? ErrorMessage(document.RootElement) : null;
        }
        catch (JsonException) { return null; }
    }

    private static string? ErrorMessage(JsonElement root)
    {
        if (Member(root, "error") is { ValueKind: JsonValueKind.Object } error && Text(error, "error_message") is { } documented) return documented;
        foreach (var name in new[] { "message", "title", "detail", "error" })
            if (Text(root, name) is { } text) return text;
        return null;
    }

    // Field names of an answer that did not have the expected shape (never the values).
    internal static string ShapeOf(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return element.ValueKind.ToString();
        var names = element.EnumerateObject().Select(property => property.Name + (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array ? ":" + property.Value.ValueKind : "")).Take(20).ToList();
        return names.Count == 0 ? "boş" : string.Join(", ", names);
    }

    internal static string ShapeOf(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            return ShapeOf(document.RootElement);
        }
        catch (JsonException) { return "JSON değil"; }
    }

    internal static StringContent Json(object body) => new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    private static JsonElement? Member(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        foreach (var property in element.EnumerateObject())
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) return property.Value;
        return null;
    }

    private static string? Text(JsonElement element, string property)
    {
        if (Member(element, property) is not { ValueKind: JsonValueKind.String } value) return null;
        var text = value.GetString();
        if (string.IsNullOrWhiteSpace(text) || text.Trim() == "null") return null;
        text = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length <= 300 ? text : text[..300];
    }

    private static AdapterResult<T> CredentialMissing<T>() =>
        Failure<T>(AdapterErrorClass.Authentication, "PTTAVM_CREDENTIAL_INVALID", "PTT AVM bağlantısı veya şifreli API bilgileri eksik ya da geçersiz.", HttpStatusCode.Unauthorized);

    private static AdapterResult<T> Failure<T>(AdapterErrorClass @class, string code, string message, HttpStatusCode? status = null) =>
        AdapterResult<T>.Failure(new(@class, code, message, status is null ? null : (int)status, null, null));
}
