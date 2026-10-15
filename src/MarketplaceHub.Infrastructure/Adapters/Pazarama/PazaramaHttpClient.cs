using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MarketplaceHub.Application;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarketplaceHub.Infrastructure.Adapters.Pazarama;

// Pazarama REST adapter (isortagimapi.pazarama.com). Every call carries an OAuth2
// client-credentials token from isortagimgiris.pazarama.com; every answer is wrapped in
// {data, success, messageCode, message, userMessage}. The flows follow the phases in
// docs/PAZARAMA-ENTEGRASYON-PLANI.md; phase 1 is the connection test and read-only probe.
public sealed partial class PazaramaHttpClient(
    IHttpClientFactory clients,
    PazaramaAuthenticationHandler authentication,
    PazaramaTokenCache tokens,
    IOptions<PazaramaOptions> options,
    TimeProvider timeProvider,
    ILogger<PazaramaHttpClient> logger,
    IConfiguration configuration)
    : IConnectionPort
{
    internal const string ClientName = "Pazarama";
    private const string DocumentationHost = "https://isortagim.pazarama.com/auth/integration/";
    internal const string ApprovedProducts = "product/products/approved";
    internal const string Brands = "brand/getBrands";
    internal const string Orders = "order/getOrdersForApi";
    // Pazarama works in Turkey time; order dates are sent as plain dates.
    internal static readonly TimeSpan TurkeyOffset = TimeSpan.FromHours(3);
    private readonly PazaramaOptions settings = options.Value;

    // Every Pazarama write goes through this gate: the server-wide switch, the connection switch,
    // a production connection that was verified or activated. Callers above check the same switches
    // when they queue work; this keeps a write from leaving even when a caller forgets.
    internal AdapterResult<T>? WriteBlocked<T>(PazaramaRequestContext account, string what)
    {
        var connection = account.Connection;
        var allowed = configuration.GetValue<bool>("FeatureFlags:ExternalWrites")
            && ConnectionWritesEnabled(connection.SettingsJson)
            && string.Equals(connection.Environment, "PRODUCTION", StringComparison.OrdinalIgnoreCase)
            && (string.Equals(connection.Status, "ACTIVE", StringComparison.OrdinalIgnoreCase) || string.Equals(connection.Status, "VERIFIED", StringComparison.OrdinalIgnoreCase));
        return allowed
            ? null
            : Failure<T>(AdapterErrorClass.Validation, "PAZARAMA_EXTERNAL_WRITES_DISABLED", $"Pazarama bağlantısında dış yazma kapalı; {what} gönderilmedi.", HttpStatusCode.Forbidden);
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

    // The approved product list answers with the seller's own products, so a valid token
    // and a usable seller account are proven by one page of size 1.
    internal async Task<AdapterResult<ConnectionIdentity>> TestAsync(PazaramaRequestContext account, CancellationToken cancellationToken)
    {
        var result = await SendAsync(account, HttpMethod.Get, $"{ApprovedProducts}?Size=1", cancellationToken);
        if (!result.IsSuccess) return AdapterResult<ConnectionIdentity>.Failure(result.Error!, result.RateLimit);
        using var document = result.Value!;
        if (!Data(document.RootElement, out _))
            return Failure<ConnectionIdentity>(AdapterErrorClass.ContractViolation, "PAZARAMA_CONNECTION_CONTRACT_INVALID", "Pazarama ürün listesi yanıtı beklenen yapıda değil.", HttpStatusCode.BadGateway);
        var connection = account.Connection;
        return AdapterResult<ConnectionIdentity>.Success(
            new("PAZARAMA", connection.Environment, connection.ExternalStoreId, connection.ApiVersion, connection.ExternalStoreId),
            result.RateLimit);
    }

    public async Task<AdapterResult<IReadOnlyList<CapabilityEvidence>>> DiscoverCapabilitiesAsync(AdapterContext context, CancellationToken cancellationToken)
    {
        var account = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        return account is null ? CredentialMissing<IReadOnlyList<CapabilityEvidence>>() : await DiscoverCapabilitiesAsync(account, cancellationToken);
    }

    // Read-only probe: the product page of the connection test, one page of the last day's
    // orders and one brand. Write capabilities stay UNKNOWN until a controlled first write on
    // the live store records their evidence.
    internal async Task<AdapterResult<IReadOnlyList<CapabilityEvidence>>> DiscoverCapabilitiesAsync(PazaramaRequestContext account, CancellationToken cancellationToken)
    {
        var connection = await TestAsync(account, cancellationToken);
        if (!connection.IsSuccess) return AdapterResult<IReadOnlyList<CapabilityEvidence>>.Failure(connection.Error!, connection.RateLimit);
        var identity = connection.Value!;
        var now = timeProvider.GetUtcNow();

        var orders = await SendAsync(account, HttpMethod.Post, Orders, cancellationToken, Json(OrderProbeBody(now)));
        var ordersReadable = orders.IsSuccess && Data(orders.Value!.RootElement, out _);
        orders.Value?.Dispose();
        var brands = await SendAsync(account, HttpMethod.Get, $"{Brands}?Page=1&Size=1", cancellationToken);
        var referencesReadable = brands.IsSuccess && Data(brands.Value!.RootElement, out _);
        brands.Value?.Dispose();

        IReadOnlyList<CapabilityEvidence> evidence =
        [
            new(MarketplaceCapabilities.ConnectionTest, "SUPPORTED", identity.ApiVersion, identity.Environment, identity.ExternalStoreId, DocumentationHost + "token-alma", "rest", null, null, "Pazarama token alındı ve onaylı ürün listesi salt okunur yanıt verdi.", null, now),
            new(MarketplaceCapabilities.ProductRead, "SUPPORTED", identity.ApiVersion, identity.Environment, identity.ExternalStoreId, DocumentationHost + "onayli-onaysiz-urun-listeleme", "rest", null, null, "Onaylı ürünler salt okunur listelendi.", null, now),
            new(MarketplaceCapabilities.OrderRead, ordersReadable ? "SUPPORTED" : "UNKNOWN", identity.ApiVersion, identity.Environment, identity.ExternalStoreId, DocumentationHost + "siparisler", "rest", null, null,
                ordersReadable ? "Son günün siparişleri salt okunur listelendi." : $"Sipariş listeleme denemesi tamamlanamadı ({orders.Error?.Code ?? "PAZARAMA_CONTRACT_INVALID"}); yetenek UNKNOWN bırakıldı.", null, now),
            new(MarketplaceCapabilities.ReferenceRead, referencesReadable ? "SUPPORTED" : "UNKNOWN", identity.ApiVersion, identity.Environment, identity.ExternalStoreId, DocumentationHost + "brand-marka", "rest", null, null,
                referencesReadable ? "Marka listesi salt okunur okundu." : $"Marka listesi okunamadı ({brands.Error?.Code ?? "PAZARAMA_CONTRACT_INVALID"}); yetenek UNKNOWN bırakıldı.", null, now)
        ];
        return AdapterResult<IReadOnlyList<CapabilityEvidence>>.Success(evidence, connection.RateLimit ?? orders.RateLimit);
    }

    // Pazarama returns orders before endDate (that day is excluded), so the probe asks for
    // yesterday through tomorrow in Turkey time.
    internal static object OrderProbeBody(DateTimeOffset now)
    {
        var today = now.ToOffset(TurkeyOffset).Date;
        return new { pageSize = 1, pageNumber = 1, startDate = Day(today.AddDays(-1)), endDate = Day(today.AddDays(1)) };
    }

    internal static string Day(DateTime value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private async Task<AdapterResult<JsonDocument>> SendAsync(PazaramaRequestContext account, HttpMethod method, string path, CancellationToken cancellationToken, HttpContent? content = null)
    {
        var body = content is null ? null : await content.ReadAsStringAsync(cancellationToken);
        var mediaType = content?.Headers.ContentType?.MediaType;
        for (var attempt = 0; ; attempt++)
        {
            var token = await tokens.GetAsync(account.CredentialId, ct => IssueTokenAsync(account, ct), cancellationToken);
            if (!token.IsSuccess) return AdapterResult<JsonDocument>.Failure(token.Error!);

            using var request = new HttpRequestMessage(method, new Uri(settings.BaseAddress, path));
            if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, mediaType ?? "application/json");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Options.Set(PazaramaRateLimitHandler.AccountKeyOption, account.Connection.Id.ToString("N"));
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(settings.Timeout > TimeSpan.Zero && settings.Timeout < TimeSpan.FromMinutes(2) ? settings.Timeout : TimeSpan.FromSeconds(30));
                using var response = await clients.CreateClient(ClientName).SendAsync(request, timeout.Token);
                var text = await response.Content.ReadAsStringAsync(timeout.Token);
                var retryAfter = response.Headers.RetryAfter?.Delta
                    ?? (response.Headers.RetryAfter?.Date is { } retryDate ? retryDate - timeProvider.GetUtcNow() : null);
                var rate = retryAfter is null ? null : new RateLimitMetadata(null, null, retryAfter);
                // A token revoked by "Yeni API Key Üret" in the seller panel is answered with 401; one
                // fresh token is tried before the credentials count as rejected.
                if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
                {
                    tokens.Forget(account.CredentialId);
                    continue;
                }
                if (!response.IsSuccessStatusCode)
                {
                    var error = Error(response.StatusCode, text, retryAfter);
                    logger.LogWarning("Pazarama API isteği reddedildi. ConnectionId: {ConnectionId}, Path: {Path}, Status: {Status}, Code: {Code}, Message: {Message}", account.Connection.Id, request.RequestUri!.AbsolutePath, (int)response.StatusCode, error.Code, error.SafeMessage);
                    return AdapterResult<JsonDocument>.Failure(error, rate);
                }
                JsonDocument document;
                try { document = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text); }
                catch (JsonException)
                {
                    return AdapterResult<JsonDocument>.Failure(new(AdapterErrorClass.ContractViolation, "PAZARAMA_JSON_INVALID", "Pazarama geçerli JSON yanıtı vermedi.", (int)response.StatusCode, null, null), rate);
                }
                if (EnvelopeError(document.RootElement) is { } refused)
                {
                    document.Dispose();
                    logger.LogWarning("Pazarama isteği reddetti. ConnectionId: {ConnectionId}, Path: {Path}, Code: {Code}, Message: {Message}", account.Connection.Id, request.RequestUri!.AbsolutePath, refused.Code, refused.SafeMessage);
                    return AdapterResult<JsonDocument>.Failure(refused, rate);
                }
                return AdapterResult<JsonDocument>.Success(document, rate);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return AdapterResult<JsonDocument>.Failure(new(AdapterErrorClass.TransientNetwork, "PAZARAMA_TIMEOUT", "Pazarama isteği zaman aşımına uğradı.", null, TimeSpan.FromSeconds(15), null));
            }
            catch (HttpRequestException exception)
            {
                logger.LogWarning(exception, "Pazarama API isteği başarısız. ConnectionId: {ConnectionId}", account.Connection.Id);
                return AdapterResult<JsonDocument>.Failure(new(AdapterErrorClass.TransientNetwork, "PAZARAMA_NETWORK_ERROR", "Pazarama bağlantısı geçici olarak kurulamadı.", null, TimeSpan.FromSeconds(15), null));
            }
        }
    }

    // OAuth2 client credentials: Basic clientId:clientSecret, form grant_type and scope.
    private async Task<PazaramaToken> IssueTokenAsync(PazaramaRequestContext account, CancellationToken cancellationToken)
    {
        if (N11CredentialFormat.Normalize(account.ClientId) is null || N11CredentialFormat.Normalize(account.ClientSecret) is null)
            return new(null, TimeSpan.Zero, new(AdapterErrorClass.Authentication, "PAZARAMA_CREDENTIAL_FORMAT_INVALID", "Kayıtlı Pazarama Client Id veya Client Secret değerinde geçersiz karakter var. Değerleri Pazarama satıcı panelindeki Hesabım → Entegrasyon Bilgileri ekranından yeniden kopyalayıp kaydedin.", 422, null, null));
        using var request = new HttpRequestMessage(HttpMethod.Post, settings.TokenAddress)
        {
            Content = new FormUrlEncodedContent([new("grant_type", "client_credentials"), new("scope", settings.Scope)])
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{account.ClientId}:{account.ClientSecret}")));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(settings.Timeout > TimeSpan.Zero && settings.Timeout < TimeSpan.FromMinutes(2) ? settings.Timeout : TimeSpan.FromSeconds(30));
            using var response = await clients.CreateClient(ClientName).SendAsync(request, timeout.Token);
            var token = TokenResponse(response.StatusCode, await response.Content.ReadAsStringAsync(timeout.Token));
            if (!token.IsSuccess) logger.LogWarning("Pazarama token alınamadı. ConnectionId: {ConnectionId}, Status: {Status}, Code: {Code}", account.Connection.Id, (int)response.StatusCode, token.Error?.Code);
            return token;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(null, TimeSpan.Zero, new(AdapterErrorClass.TransientNetwork, "PAZARAMA_TIMEOUT", "Pazarama oturum servisi zaman aşımına uğradı.", null, TimeSpan.FromSeconds(15), null));
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "Pazarama token isteği başarısız. ConnectionId: {ConnectionId}", account.Connection.Id);
            return new(null, TimeSpan.Zero, new(AdapterErrorClass.TransientNetwork, "PAZARAMA_NETWORK_ERROR", "Pazarama oturum servisine bağlanılamadı.", null, TimeSpan.FromSeconds(15), null));
        }
    }

    // Standard OAuth answers {access_token, expires_in, token_type}; Pazarama's token service wraps
    // its answer in the shared {data, success, userMessage} envelope. Both shapes are read, with the
    // field names matched case-insensitively (accessToken / access_token, expiresIn / expires_in).
    internal static PazaramaToken TokenResponse(HttpStatusCode status, string body)
    {
        string? accessToken = null, error = null, userMessage = null;
        var expiresIn = TimeSpan.FromHours(1);
        var successFalse = false;
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                ReadToken(root, ref accessToken, ref expiresIn);
                if (Member(root, "data") is { ValueKind: JsonValueKind.Object } data) ReadToken(data, ref accessToken, ref expiresIn);
                else if (Member(root, "data") is { ValueKind: JsonValueKind.String } dataText && accessToken is null) accessToken = dataText.GetString();
                error = Member(root, "error") is { ValueKind: JsonValueKind.String } code ? code.GetString() : null;
                successFalse = Member(root, "success") is { ValueKind: JsonValueKind.False };
                userMessage = Text(root, "userMessage") ?? Text(root, "message");
            }
        }
        catch (JsonException) { }
        if ((int)status is >= 200 and < 300 && !string.IsNullOrWhiteSpace(accessToken) && !successFalse) return new(accessToken, expiresIn, null);
        if (error is "invalid_client" or "unauthorized_client" || status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden || successFalse)
            return new(null, TimeSpan.Zero, new(AdapterErrorClass.Authentication, "PAZARAMA_CREDENTIALS_REJECTED", userMessage is null
                ? "Pazarama Client Id ve Client Secret çiftini kabul etmedi. Pazarama satıcı panelinde Hesabım → Entegrasyon Bilgileri'ndeki güncel değerleri girin; orada yeni anahtar üretildiyse eskisi geçersiz olur."
                : $"Pazarama Client Id ve Client Secret çiftini kabul etmedi: {userMessage}", (int)status, null, null));
        if (error == "invalid_scope")
            return new(null, TimeSpan.Zero, new(AdapterErrorClass.Authentication, "PAZARAMA_SCOPE_DENIED", "Pazarama bu API hesabına entegrasyon (merchantgatewayapi.fullaccess) yetkisi vermiyor. Pazarama destekle API erişimini açtırın.", (int)status, null, null));
        if ((int)status == 429)
            return new(null, TimeSpan.Zero, new(AdapterErrorClass.RateLimit, "PAZARAMA_RATE_LIMITED", "Pazarama oturum servisi istek sınırına ulaştı; biraz sonra yeniden denenecek.", 429, TimeSpan.FromSeconds(60), null));
        if ((int)status >= 500)
            return new(null, TimeSpan.Zero, new(AdapterErrorClass.Remote5xx, "PAZARAMA_REMOTE_ERROR", "Pazarama oturum servisi geçici olarak hata veriyor; işlem yeniden denenecek.", (int)status, TimeSpan.FromSeconds(30), null));
        return new(null, TimeSpan.Zero, new(AdapterErrorClass.ContractViolation, "PAZARAMA_TOKEN_INVALID", $"Pazarama oturum servisi beklenen token yanıtını vermedi (yanıt alanları: {ShapeOf(body)}).", (int)status, null, null));
    }

    private static void ReadToken(JsonElement element, ref string? accessToken, ref TimeSpan expiresIn)
    {
        foreach (var name in new[] { "access_token", "accessToken", "token", "jwtToken" })
            if (accessToken is null && Member(element, name) is { ValueKind: JsonValueKind.String } value && !string.IsNullOrWhiteSpace(value.GetString())) accessToken = value.GetString();
        foreach (var name in new[] { "expires_in", "expiresIn", "expiresInSeconds" })
            if (Member(element, name) is { ValueKind: JsonValueKind.Number } expires && expires.TryGetInt32(out var seconds) && seconds > 0) expiresIn = TimeSpan.FromSeconds(seconds);
    }

    private static JsonElement? Member(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        foreach (var property in element.EnumerateObject())
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) return property.Value;
        return null;
    }

    // Field names of an unexpected answer (never their values), so the next failure names what
    // Pazarama really sends without leaking a token.
    internal static string ShapeOf(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return document.RootElement.ValueKind.ToString();
            var names = new List<string>();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                names.Add(property.Name);
                if (property.Value.ValueKind == JsonValueKind.Object) names.AddRange(property.Value.EnumerateObject().Select(child => property.Name + "." + child.Name));
            }
            return names.Count == 0 ? "boş" : string.Join(", ", names.Take(20));
        }
        catch (JsonException) { return "JSON değil"; }
    }

    internal static AdapterError Error(HttpStatusCode status, string body, TimeSpan? retryAfter)
    {
        var remote = RemoteMessage(body);
        return status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new(AdapterErrorClass.Authentication, "PAZARAMA_CREDENTIALS_REJECTED", "Pazarama erişimi reddetti. Pazarama satıcı panelinde Hesabım → Entegrasyon Bilgileri'ndeki güncel Client Id ve Client Secret değerlerini girin.", (int)status, null, null),
            HttpStatusCode.TooManyRequests => new(AdapterErrorClass.RateLimit, "PAZARAMA_RATE_LIMITED", "Pazarama istek sınırına ulaşıldı; işlem biraz sonra yeniden denenecek.", (int)status, retryAfter ?? TimeSpan.FromSeconds(60), null),
            HttpStatusCode.NotFound => new(AdapterErrorClass.NotFound, "PAZARAMA_NOT_FOUND", remote is null ? "Pazarama istenen kaydı bulamadı." : $"Pazarama kaydı bulamadı: {remote}", (int)status, null, null),
            >= HttpStatusCode.InternalServerError => new(AdapterErrorClass.Remote5xx, "PAZARAMA_REMOTE_ERROR", "Pazarama servisi geçici olarak hata veriyor; işlem yeniden denenecek.", (int)status, retryAfter ?? TimeSpan.FromSeconds(30), null),
            _ => new(AdapterErrorClass.Validation, "PAZARAMA_REQUEST_REJECTED", remote is null ? "Pazarama isteği reddetti." : $"Pazarama isteği reddetti: {remote}", (int)status, null, null)
        };
    }

    // A 200 answer can still say success=false; userMessage is meant for the seller.
    internal static AdapterError? EnvelopeError(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.False) return null;
        var message = Text(root, "userMessage") ?? Text(root, "message");
        var code = Text(root, "messageCode");
        return new(AdapterErrorClass.Validation, "PAZARAMA_REQUEST_REJECTED",
            message is null ? code is null ? "Pazarama isteği reddetti." : $"Pazarama isteği reddetti (kod {code})." : $"Pazarama isteği reddetti: {message}",
            null, null, null);
    }

    internal static string? RemoteMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            foreach (var name in new[] { "userMessage", "message", "title", "detail", "error" })
                if (Text(root, name) is { } text) return text;
            if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Object)
            {
                var joined = string.Join(" ", errors.EnumerateObject()
                    .SelectMany(entry => entry.Value.ValueKind == JsonValueKind.Array ? entry.Value.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()) : [])
                    .Where(x => !string.IsNullOrWhiteSpace(x)));
                if (!string.IsNullOrWhiteSpace(joined)) return Trim(joined);
            }
            return null;
        }
        catch (JsonException) { return null; }
    }

    // The useful part of the envelope; Pazarama sends data as an object, an array or a scalar.
    internal static bool Data(JsonElement root, out JsonElement data)
    {
        data = default;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var value) || value.ValueKind is JsonValueKind.Undefined) return false;
        data = value;
        return true;
    }

    internal static StringContent Json(object body) => new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    private static string? Text(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String) return null;
        var text = value.GetString();
        return string.IsNullOrWhiteSpace(text) || text.Trim() == "null" ? null : Trim(text);
    }

    private static string Trim(string value)
    {
        var text = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length <= 300 ? text : text[..300];
    }

    private static AdapterResult<T> CredentialMissing<T>() =>
        Failure<T>(AdapterErrorClass.Authentication, "PAZARAMA_CREDENTIAL_INVALID", "Pazarama bağlantısı veya şifreli API bilgileri eksik ya da geçersiz.", HttpStatusCode.Unauthorized);

    private static AdapterResult<T> Failure<T>(AdapterErrorClass @class, string code, string message, HttpStatusCode? status = null) =>
        AdapterResult<T>.Failure(new(@class, code, message, status is null ? null : (int)status, null, null));
}
