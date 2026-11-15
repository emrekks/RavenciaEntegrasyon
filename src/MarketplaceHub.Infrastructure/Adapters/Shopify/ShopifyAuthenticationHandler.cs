using System.Security.Cryptography;
using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using MarketplaceHub.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MarketplaceHub.Infrastructure.Adapters.Shopify;

public sealed class ShopifyAuthenticationHandler(
    AppDbContext db,
    IDataProtectionProvider dataProtection,
    IHttpClientFactory clients,
    ILogger<ShopifyAuthenticationHandler> logger)
{
    private readonly IDataProtector protector = dataProtection.CreateProtector("MarketplaceHub.PlatformCredential.v1");
    // Shared by every request and job of the process (the handler itself is scoped), keyed by the credential
    // so a rotated credential never reuses the old token.
    // Fresh until a minute before it expires (the system clock, as before).
    private static readonly SingleFlightTokenCache<(Guid ConnectionId, Guid CredentialId), CachedToken> Tokens =
        new(TimeProvider.System, (cached, now) => cached.ExpiresAt > now.AddMinutes(1));

    // Why the last LoadAsync returned null when the token service could not be reached or timed out: the caller
    // answers this transient failure instead of reporting the credentials as invalid.
    public AdapterError? TokenRequestFailure { get; private set; }

    public async Task<ShopifyRequestContext?> LoadAsync(Guid tenantId, Guid connectionId, string apiVersion, CancellationToken cancellationToken)
    {
        TokenRequestFailure = null;
        var connection = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(
            // The API version comes from the server's settings; the version stored on the connection is the one it
            // was last verified with, so raising the setting does not cut every connection off.
            x => x.TenantId == tenantId && x.Id == connectionId && x.PlatformCode == "SHOPIFY",
            cancellationToken);
        var credential = await db.PlatformCredentials.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.RevokedAt == null)
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (connection is null || credential is null) return null;

        ShopifyCredentialPayload? payload;
        try { payload = JsonSerializer.Deserialize<ShopifyCredentialPayload>(BoundSecret.UnprotectForConnection(protector, credential.TenantId, credential.ConnectionId, credential.ProtectedPayload)); }
        catch (Exception exception) when (exception is CryptographicException or JsonException)
        {
            logger.LogWarning(exception, "Shopify credential çözülemedi. ConnectionId: {ConnectionId}", connectionId);
            return null;
        }

        var shop = connection.ExternalStoreId.Trim().ToLowerInvariant();
        if (payload is null || !IsValidShop(shop))
        {
            logger.LogWarning("Shopify bağlantı bilgileri eksik veya mağaza kimliği geçersiz. ConnectionId: {ConnectionId}", connectionId);
            return null;
        }

        if (!string.IsNullOrWhiteSpace(payload.AccessToken))
        {
            return new(connection, shop, apiVersion, payload.AccessToken.Trim(), DateTimeOffset.MaxValue);
        }

        // Existing client-credentials records remain readable during the credential
        // transition; all newly saved Shopify credentials use the app token above.
        if (string.IsNullOrWhiteSpace(payload.ResolvedClientId) || string.IsNullOrWhiteSpace(payload.ResolvedClientSecret))
        {
            logger.LogWarning("Shopify uygulama tokenı bulunamadı. ConnectionId: {ConnectionId}", connectionId);
            return null;
        }

        var token = await AccessTokenAsync((connectionId, credential.Id), shop, payload, cancellationToken);
        if (token is null) return null;
        return new(connection, shop, apiVersion, token.Token, token.ExpiresAt);
    }

    private Task<CachedToken?> AccessTokenAsync((Guid ConnectionId, Guid CredentialId) key, string shop, ShopifyCredentialPayload payload, CancellationToken cancellationToken) =>
        CachedAsync(key, token => RequestTokenAsync(key.ConnectionId, shop, payload, token), cancellationToken);

    // The process-wide token of a credential: kept until a minute before it expires; a failed request is not kept.
    // ADAPTOR-DIGER-2: concurrent jobs of one credential wait for a single token request.
    internal static Task<CachedToken?> CachedAsync((Guid ConnectionId, Guid CredentialId) key, Func<CancellationToken, Task<CachedToken?>> request, CancellationToken cancellationToken) =>
        Tokens.GetAsync(key, request, (token, _) => token, cached => (CachedToken?)cached, cancellationToken);

    private async Task<CachedToken?> RequestTokenAsync(Guid connectionId, string shop, ShopifyCredentialPayload payload, CancellationToken cancellationToken)
    {
        var endpoint = new Uri($"https://{shop}.myshopify.com/admin/oauth/access_token");
        using var client = clients.CreateBoundedClient("Shopify");
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = payload.ResolvedClientId!,
                ["client_secret"] = payload.ResolvedClientSecret!
            })
        };
        // The client's 45 s timeout cancels inside the pipeline; the circuit tells it from the caller's cancellation.
        CallerCancellation.Mark(request, cancellationToken);
        try
        {
            using var response = await client.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Shopify token alınamadı. ConnectionId: {ConnectionId}, Status: {Status}", connectionId, (int)response.StatusCode);
                // ADAPTOR-DIGER-2: a throttled or failing token service is a passing failure, not invalid credentials;
                // only a refusal (400/401/403 and the like) is answered as SHOPIFY_CREDENTIAL_INVALID by the caller.
                TokenRequestFailure = TokenServiceFailure(response);
                return null;
            }
            using var json = JsonDocument.Parse(body);
            var accessToken = json.RootElement.TryGetProperty("access_token", out var tokenElement) && tokenElement.ValueKind == JsonValueKind.String ? tokenElement.GetString() : null;
            var expiresIn = RemoteJson.Int32(json.RootElement, "expires_in") ?? 86_400;
            if (string.IsNullOrWhiteSpace(accessToken)) return null;
            return new(accessToken, DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, expiresIn)));
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "Shopify token isteği başarısız. ConnectionId: {ConnectionId}", connectionId);
            TokenRequestFailure = new(AdapterErrorClass.TransientNetwork, "SHOPIFY_NETWORK_ERROR", "Shopify oturum servisine bağlanılamadı.", null, null, null);
            return null;
        }
        // The client's timeout (not the caller's cancellation) ended the token request; it is answered like a
        // network failure instead of escaping as TaskCanceledException.
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Shopify token isteği zaman aşımına uğradı. ConnectionId: {ConnectionId}", connectionId);
            TokenRequestFailure = new(AdapterErrorClass.TransientNetwork, "SHOPIFY_TIMEOUT", "Shopify oturum servisi zaman aşımına uğradı.", null, null, null);
            return null;
        }
    }

    internal const string TokenServiceUnavailableCode = "SHOPIFY_TOKEN_SERVICE_UNAVAILABLE";

    // The error of a token answer that is a passing failure (429 or 5xx); null for any other status.
    internal static AdapterError? TokenServiceFailure(HttpResponseMessage response)
    {
        var status = (int)response.StatusCode;
        if (status != 429 && status < 500) return null;
        var retryAfter = response.Headers.RetryAfter?.Delta is { } delta && delta > TimeSpan.Zero ? delta : TimeSpan.FromSeconds(30);
        return new(status == 429 ? AdapterErrorClass.RateLimit : AdapterErrorClass.Remote5xx, TokenServiceUnavailableCode,
            "Shopify oturum servisi geçici olarak yanıt vermiyor.", status, retryAfter, null);
    }

    // Drops the cached client-credentials tokens of a connection after Shopify refused one (401).
    internal static void Forget(Guid connectionId) => Tokens.ForgetWhere(key => key.ConnectionId == connectionId);

    private static bool IsValidShop(string shop) => shop.Length is >= 3 and <= 100
        && shop[0] is >= 'a' and <= 'z'
        && shop[^1] is >= 'a' and <= 'z' or >= '0' and <= '9'
        && shop.All(value => value is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');

    // Existing credential rows use the shared ApiKey/ApiSecret JSON shape;
    // accept both shapes while Shopify-specific credentials roll out.
    private sealed record ShopifyCredentialPayload(string? AccessToken = null, string? ClientId = null, string? ClientSecret = null, string? ApiKey = null, string? ApiSecret = null)
    {
        public string? ResolvedClientId => ClientId ?? ApiKey;
        public string? ResolvedClientSecret => ClientSecret ?? ApiSecret;
        public override string ToString() => nameof(ShopifyCredentialPayload);
    }
    internal sealed record CachedToken(string Token, DateTimeOffset ExpiresAt)
    {
        public override string ToString() => $"CachedToken {{ ExpiresAt = {ExpiresAt:O} }}";
    }
}

public sealed record ShopifyRequestContext(PlatformConnection Connection, string Shop, string ApiVersion, string AccessToken, DateTimeOffset ExpiresAt)
{
    // The generated ToString would print the access token; a logged or thrown context must not leak it.
    public override string ToString() => $"ShopifyRequestContext {{ ConnectionId = {Connection.Id}, Shop = {Shop} }}";
}
