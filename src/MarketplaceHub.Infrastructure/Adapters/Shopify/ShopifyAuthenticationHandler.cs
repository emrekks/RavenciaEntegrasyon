using System.Security.Cryptography;
using System.Text.Json;
using System.Collections.Concurrent;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
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
    private readonly ConcurrentDictionary<Guid, CachedToken> tokens = new();

    public async Task<ShopifyRequestContext?> LoadAsync(Guid tenantId, Guid connectionId, string apiVersion, CancellationToken cancellationToken)
    {
        var connection = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(
            x => x.TenantId == tenantId && x.Id == connectionId && x.PlatformCode == "SHOPIFY" && x.ApiVersion == apiVersion,
            cancellationToken);
        var credential = await db.PlatformCredentials.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.RevokedAt == null)
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (connection is null || credential is null) return null;

        ShopifyCredentialPayload? payload;
        try { payload = JsonSerializer.Deserialize<ShopifyCredentialPayload>(protector.Unprotect(credential.ProtectedPayload)); }
        catch (Exception exception) when (exception is CryptographicException or JsonException)
        {
            logger.LogWarning(exception, "Shopify credential çözülemedi. ConnectionId: {ConnectionId}", connectionId);
            return null;
        }

        var shop = connection.ExternalStoreId.Trim().ToLowerInvariant();
        if (payload is null || string.IsNullOrWhiteSpace(payload.ResolvedClientId) || string.IsNullOrWhiteSpace(payload.ResolvedClientSecret) || !IsValidShop(shop))
        {
            logger.LogWarning("Shopify bağlantı bilgileri eksik veya mağaza kimliği geçersiz. ConnectionId: {ConnectionId}", connectionId);
            return null;
        }

        var token = await AccessTokenAsync(connectionId, shop, payload, cancellationToken);
        if (token is null) return null;
        var modelCode = ModelCodeMetafield(connection.SettingsJson);
        return new(connection, shop, apiVersion, token.Token, token.ExpiresAt, modelCode.Namespace, modelCode.Key);
    }

    private async Task<CachedToken?> AccessTokenAsync(Guid connectionId, string shop, ShopifyCredentialPayload payload, CancellationToken cancellationToken)
    {
        if (tokens.TryGetValue(connectionId, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1)) return cached;

        var endpoint = new Uri($"https://{shop}.myshopify.com/admin/oauth/access_token");
        using var client = clients.CreateClient("Shopify");
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = payload.ResolvedClientId!,
                ["client_secret"] = payload.ResolvedClientSecret!
            })
        };
        try
        {
            using var response = await client.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Shopify token alınamadı. ConnectionId: {ConnectionId}, Status: {Status}", connectionId, (int)response.StatusCode);
                return null;
            }
            using var json = JsonDocument.Parse(body);
            var accessToken = json.RootElement.TryGetProperty("access_token", out var tokenElement) ? tokenElement.GetString() : null;
            var expiresIn = json.RootElement.TryGetProperty("expires_in", out var expiryElement) && expiryElement.TryGetInt32(out var seconds) ? seconds : 86_400;
            if (string.IsNullOrWhiteSpace(accessToken)) return null;
            cached = new(accessToken, DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, expiresIn)));
            tokens[connectionId] = cached;
            return cached;
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "Shopify token isteği başarısız. ConnectionId: {ConnectionId}", connectionId);
            return null;
        }
    }

    private static bool IsValidShop(string shop) => shop.Length is >= 3 and <= 100
        && shop[0] is >= 'a' and <= 'z'
        && shop[^1] is >= 'a' and <= 'z' or >= '0' and <= '9'
        && shop.All(value => value is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');

    private static (string Namespace, string Key) ModelCodeMetafield(string settingsJson)
    {
        const string fallbackNamespace = "ravencia";
        const string fallbackKey = "model_code";
        try
        {
            using var document = JsonDocument.Parse(settingsJson);
            var value = document.RootElement.TryGetProperty("ModelCodeMetafield", out var property) && property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;
            var separator = value?.IndexOf('.') ?? -1;
            if (separator <= 0 || separator != value!.LastIndexOf('.') || separator == value.Length - 1) return (fallbackNamespace, fallbackKey);
            var modelNamespace = value[..separator];
            var modelKey = value[(separator + 1)..];
            return IsMetafieldPart(modelNamespace) && IsMetafieldPart(modelKey)
                ? (modelNamespace, modelKey)
                : (fallbackNamespace, fallbackKey);
        }
        catch (JsonException) { return (fallbackNamespace, fallbackKey); }
    }

    private static bool IsMetafieldPart(string value) => value.Length is >= 1 and <= 80
        && value.All(character => character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-');

    // Existing credential rows use the shared ApiKey/ApiSecret JSON shape;
    // accept both shapes while Shopify-specific credentials roll out.
    private sealed record ShopifyCredentialPayload(string? ClientId, string? ClientSecret, string? ApiKey = null, string? ApiSecret = null)
    {
        public string? ResolvedClientId => ClientId ?? ApiKey;
        public string? ResolvedClientSecret => ClientSecret ?? ApiSecret;
    }
    private sealed record CachedToken(string Token, DateTimeOffset ExpiresAt);
}

public sealed record ShopifyRequestContext(PlatformConnection Connection, string Shop, string ApiVersion, string AccessToken, DateTimeOffset ExpiresAt, string ModelCodeNamespace = "ravencia", string ModelCodeKey = "model_code");
