using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using MarketplaceHub.Application;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarketplaceHub.Infrastructure.Adapters.N11;

// n11 REST adapter. Phase 1 covers the connection test and the read-only
// capability probe; product, order, return and question flows follow the
// phases in docs/N11-ENTEGRASYON-PLANI.md and stay unsupported until then.
public sealed class N11HttpClient(
    IHttpClientFactory clients,
    N11AuthenticationHandler authentication,
    IOptions<N11Options> options,
    TimeProvider timeProvider,
    ILogger<N11HttpClient> logger)
    : IConnectionPort
{
    private const string ProductQueryGuide = "https://developer.n11.com/documentation/n11-marketplace-entegrasyonu/satici-urun-sorgulama/";
    private const string OrderListGuide = "https://developer.n11.com/documentation/n11-siparis-entegrasyonu/siparis-listeleme-servisi/";
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");
    private readonly N11Options settings = options.Value;

    internal static string ProductQuery(int page, int size) =>
        $"ms/product-query?page={page.ToString(CultureInfo.InvariantCulture)}&size={size.ToString(CultureInfo.InvariantCulture)}";

    // Dates are epoch milliseconds; n11 lists at most 15 days per request.
    internal static string ShipmentPackages(DateTimeOffset start, DateTimeOffset end, int page, int size) =>
        $"rest/delivery/v1/shipmentPackages?startDate={start.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)}&endDate={end.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)}&page={page.ToString(CultureInfo.InvariantCulture)}&size={size.ToString(CultureInfo.InvariantCulture)}&orderByDirection=DESC";

    public async Task<AdapterResult<ConnectionIdentity>> TestAsync(AdapterContext context, CancellationToken cancellationToken)
    {
        var account = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        return account is null ? CredentialMissing<ConnectionIdentity>() : await TestAsync(account, cancellationToken);
    }

    internal async Task<AdapterResult<ConnectionIdentity>> TestAsync(N11RequestContext account, CancellationToken cancellationToken)
    {
        var result = await SendAsync(account, HttpMethod.Get, ProductQuery(0, 1), cancellationToken);
        if (!result.IsSuccess) return AdapterResult<ConnectionIdentity>.Failure(result.Error!, result.RateLimit);
        using var document = result.Value!;
        if (!HasContentArray(document.RootElement))
            return Failure<ConnectionIdentity>(AdapterErrorClass.ContractViolation, "N11_CONNECTION_CONTRACT_INVALID", "N11 ürün sorgulama yanıtı beklenen sayfa yapısında değil.", HttpStatusCode.BadGateway);

        var seller = Seller(document.RootElement);
        if (seller.Nickname is not null && !SameStore(seller.Nickname, account.Connection.ExternalStoreId))
            return Failure<ConnectionIdentity>(AdapterErrorClass.Validation, "N11_STORE_MISMATCH",
                $"Bu API anahtarı \"{seller.Nickname}\" mağazasına ait, bağlantıda ise \"{account.Connection.ExternalStoreId}\" yazıyor. Mağaza adını N11'deki ile aynı yazın ya da doğru mağazanın anahtarını girin.",
                HttpStatusCode.UnprocessableEntity);

        var connection = account.Connection;
        return AdapterResult<ConnectionIdentity>.Success(
            new("N11", connection.Environment, connection.ExternalStoreId, connection.ApiVersion, seller.Id ?? connection.ExternalStoreId),
            result.RateLimit);
    }

    public async Task<AdapterResult<IReadOnlyList<CapabilityEvidence>>> DiscoverCapabilitiesAsync(AdapterContext context, CancellationToken cancellationToken)
    {
        var account = await authentication.LoadAsync(context.TenantId, context.ConnectionId, cancellationToken);
        return account is null ? CredentialMissing<IReadOnlyList<CapabilityEvidence>>() : await DiscoverCapabilitiesAsync(account, cancellationToken);
    }

    // Read-only probe: the product page from the connection test and one page
    // of recent shipment packages. Write capabilities stay UNKNOWN until a
    // controlled first write on the live store records their evidence.
    internal async Task<AdapterResult<IReadOnlyList<CapabilityEvidence>>> DiscoverCapabilitiesAsync(N11RequestContext account, CancellationToken cancellationToken)
    {
        var connection = await TestAsync(account, cancellationToken);
        if (!connection.IsSuccess) return AdapterResult<IReadOnlyList<CapabilityEvidence>>.Failure(connection.Error!, connection.RateLimit);
        var identity = connection.Value!;
        var now = timeProvider.GetUtcNow();
        var orders = await SendAsync(account, HttpMethod.Get, ShipmentPackages(now.AddDays(-14), now, 0, 1), cancellationToken);
        var ordersReadable = orders.IsSuccess && HasContentArray(orders.Value!.RootElement);
        orders.Value?.Dispose();
        IReadOnlyList<CapabilityEvidence> evidence =
        [
            new(MarketplaceCapabilities.ConnectionTest, "SUPPORTED", identity.ApiVersion, identity.Environment, identity.ExternalStoreId, ProductQueryGuide, "rest", null, null, "N11 satıcı ürün sorgulama servisi API anahtarıyla salt okunur yanıt verdi.", null, now),
            new(MarketplaceCapabilities.ProductRead, "SUPPORTED", identity.ApiVersion, identity.Environment, identity.ExternalStoreId, ProductQueryGuide, "rest", null, null, "Satıcı ürünleri sayfalı olarak salt okunur listelendi.", null, now),
            new(MarketplaceCapabilities.OrderRead, ordersReadable ? "SUPPORTED" : "UNKNOWN", identity.ApiVersion, identity.Environment, identity.ExternalStoreId, OrderListGuide, "rest", null, null,
                ordersReadable ? "Son 14 günün sipariş paketleri salt okunur listelendi." : $"Sipariş paketi listeleme denemesi tamamlanamadı ({orders.Error?.Code ?? "N11_CONTRACT_INVALID"}); yetenek UNKNOWN bırakıldı.", null, now)
        ];
        return AdapterResult<IReadOnlyList<CapabilityEvidence>>.Success(evidence, connection.RateLimit ?? orders.RateLimit);
    }

    private async Task<AdapterResult<JsonDocument>> SendAsync(N11RequestContext context, HttpMethod method, string path, CancellationToken cancellationToken, HttpContent? content = null)
    {
        if (N11CredentialFormat.Normalize(context.AppKey) is null || N11CredentialFormat.Normalize(context.AppSecret) is null)
            return Failure<JsonDocument>(AdapterErrorClass.Authentication, "N11_CREDENTIAL_FORMAT_INVALID", "Kayıtlı N11 App Key veya App Secret değerinde geçersiz karakter var. Değerleri N11 satıcı panelindeki Hesabım → API Hesapları ekranından yeniden kopyalayıp kaydedin.", HttpStatusCode.UnprocessableEntity);
        var client = clients.CreateClient("N11");
        using var request = new HttpRequestMessage(method, new Uri(settings.BaseAddress, path)) { Content = content };
        ApplyAuthentication(request, context);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(settings.Timeout > TimeSpan.Zero && settings.Timeout < TimeSpan.FromMinutes(2) ? settings.Timeout : TimeSpan.FromSeconds(30));
            using var response = await client.SendAsync(request, timeout.Token);
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            var retryAfter = response.Headers.RetryAfter?.Delta
                ?? (response.Headers.RetryAfter?.Date is { } retryDate ? retryDate - timeProvider.GetUtcNow() : null);
            var rate = retryAfter is null ? null : new RateLimitMetadata(null, null, retryAfter);
            if (!response.IsSuccessStatusCode)
            {
                var error = Error(response.StatusCode, body, retryAfter);
                logger.LogWarning("N11 API isteği reddedildi. ConnectionId: {ConnectionId}, Path: {Path}, Status: {Status}, Code: {Code}", context.Connection.Id, request.RequestUri!.AbsolutePath, (int)response.StatusCode, error.Code);
                return AdapterResult<JsonDocument>.Failure(error, rate);
            }
            try { return AdapterResult<JsonDocument>.Success(JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body), rate); }
            catch (JsonException)
            {
                return AdapterResult<JsonDocument>.Failure(new(AdapterErrorClass.ContractViolation, "N11_JSON_INVALID", "N11 geçerli JSON yanıtı vermedi.", (int)response.StatusCode, null, null), rate);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return AdapterResult<JsonDocument>.Failure(new(AdapterErrorClass.TransientNetwork, "N11_TIMEOUT", "N11 isteği zaman aşımına uğradı.", null, TimeSpan.FromSeconds(15), null));
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "N11 API isteği başarısız. ConnectionId: {ConnectionId}", context.Connection.Id);
            return AdapterResult<JsonDocument>.Failure(new(AdapterErrorClass.TransientNetwork, "N11_NETWORK_ERROR", "N11 bağlantısı geçici olarak kurulamadı.", null, TimeSpan.FromSeconds(15), null));
        }
    }

    internal static void ApplyAuthentication(HttpRequestMessage request, N11RequestContext context)
    {
        request.Headers.TryAddWithoutValidation("appkey", context.AppKey);
        request.Headers.TryAddWithoutValidation("appsecret", context.AppSecret);
    }

    internal static AdapterError Error(HttpStatusCode status, string body, TimeSpan? retryAfter)
    {
        var remote = RemoteMessage(body);
        return status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new(AdapterErrorClass.Authentication, "N11_CREDENTIALS_REJECTED", "N11 API anahtarı veya şifresi reddedildi. N11 satıcı panelinde Hesabım > API Hesapları altındaki güncel bilgileri girin.", (int)status, null, null),
            HttpStatusCode.TooManyRequests => new(AdapterErrorClass.RateLimit, "N11_RATE_LIMITED", "N11 istek sınırına ulaşıldı; işlem biraz sonra yeniden denenecek.", (int)status, retryAfter ?? TimeSpan.FromSeconds(60), null),
            HttpStatusCode.NotFound => new(AdapterErrorClass.NotFound, "N11_NOT_FOUND", remote is null ? "N11 istenen kaydı bulamadı." : $"N11 kaydı bulamadı: {remote}", (int)status, null, null),
            >= HttpStatusCode.InternalServerError => new(AdapterErrorClass.Remote5xx, "N11_REMOTE_ERROR", "N11 servisi geçici olarak hata veriyor; işlem yeniden denenecek.", (int)status, retryAfter ?? TimeSpan.FromSeconds(30), null),
            _ => new(AdapterErrorClass.Validation, "N11_REQUEST_REJECTED", remote is null ? "N11 isteği reddetti." : $"N11 isteği reddetti: {remote}", (int)status, null, null)
        };
    }

    // n11 reports errors as message/errorMessage fields or a reasons list.
    internal static string? RemoteMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            foreach (var name in new[] { "message", "errorMessage", "error", "detail" })
                if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
                    return Trim(value.GetString()!);
            if (root.TryGetProperty("reasons", out var reasons) && reasons.ValueKind == JsonValueKind.Array)
            {
                var joined = string.Join(" ", reasons.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()));
                if (!string.IsNullOrWhiteSpace(joined)) return Trim(joined);
            }
            return null;
        }
        catch (JsonException) { return null; }
    }

    internal static (string? Id, string? Nickname) Seller(JsonElement root)
    {
        if (!HasContentArray(root)) return (null, null);
        foreach (var item in root.GetProperty("content").EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var id = item.TryGetProperty("sellerId", out var sellerId) && sellerId.ValueKind is JsonValueKind.Number or JsonValueKind.String ? sellerId.ToString() : null;
            var nickname = item.TryGetProperty("sellerNickname", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString()?.Trim() : null;
            return (string.IsNullOrWhiteSpace(id) ? null : id, string.IsNullOrWhiteSpace(nickname) ? null : nickname);
        }
        return (null, null);
    }

    internal static bool SameStore(string remoteNickname, string configuredStore) =>
        string.Compare(remoteNickname.Trim(), configuredStore.Trim(), Turkish, CompareOptions.IgnoreCase) == 0;

    private static bool HasContentArray(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array;

    private static string Trim(string value)
    {
        var text = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length <= 300 ? text : text[..300];
    }

    private static AdapterResult<T> CredentialMissing<T>() =>
        Failure<T>(AdapterErrorClass.Authentication, "N11_CREDENTIAL_INVALID", "N11 bağlantısı veya şifreli API bilgileri eksik ya da geçersiz.", HttpStatusCode.Unauthorized);

    private static AdapterResult<T> Failure<T>(AdapterErrorClass @class, string code, string message, HttpStatusCode? status = null) =>
        AdapterResult<T>.Failure(new(@class, code, message, status is null ? null : (int)status, null, null));
}
