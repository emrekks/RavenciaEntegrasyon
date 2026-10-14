using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarketplaceHub.Infrastructure.Adapters.Trendyol;

public sealed class TrendyolAuthenticationHandler(AppDbContext db, IDataProtectionProvider dataProtection, IOptions<TrendyolOptions> options, ILogger<TrendyolAuthenticationHandler> logger)
{
    private readonly IDataProtector _protector = dataProtection.CreateProtector("MarketplaceHub.PlatformCredential.v1");

    public async Task<TrendyolRequestContext?> LoadAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken)
    {
        var connection = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == connectionId && x.PlatformCode == "TRENDYOL" && x.ApiVersion == "V2", cancellationToken);
        var credential = await db.PlatformCredentials.AsNoTracking().Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.RevokedAt == null).OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(cancellationToken);
        if (connection is null)
        {
            logger.LogWarning("Trendyol bağlantı testi yapılandırma bulunamadığı için başlatılamadı. ConnectionId: {ConnectionId}", connectionId);
            return null;
        }
        if (credential is null)
        {
            logger.LogWarning("Trendyol bağlantı testi için aktif credential bulunamadı. ConnectionId: {ConnectionId}", connectionId);
            return null;
        }
        CredentialPayload? payload; ConnectionSettings? settings;
        try { payload = JsonSerializer.Deserialize<CredentialPayload>(_protector.Unprotect(credential.ProtectedPayload)); settings = JsonSerializer.Deserialize<ConnectionSettings>(connection.SettingsJson); }
        catch (Exception exception) when (exception is CryptographicException or JsonException)
        {
            logger.LogWarning(exception, "Trendyol credential veya bağlantı ayarları çözülemedi. ConnectionId: {ConnectionId}", connectionId);
            return null;
        }
        if (payload is null || settings is null || string.IsNullOrWhiteSpace(settings.UserAgentIdentity))
        {
            logger.LogWarning("Trendyol bağlantı ayarları eksik veya User-Agent kimliği boş. ConnectionId: {ConnectionId}", connectionId);
            return null;
        }
        if (!IntegrationRuntimePolicy.TryResolveBaseAddress(connection.Environment, options.Value.StageBaseAddress, options.Value.ProductionBaseAddress, out var baseAddress))
        {
            logger.LogWarning("Trendyol bağlantısı için geçersiz environment yapılandırması: {Environment}. ConnectionId: {ConnectionId}", connection.Environment, connectionId);
            return null;
        }
        logger.LogDebug("Trendyol bağlantısı {Environment} ortamı için {BaseAddress} adresine çözüldü. ConnectionId: {ConnectionId}", connection.Environment, baseAddress, connectionId);
        return new(connection, baseAddress, payload.ApiKey, payload.ApiSecret, settings.UserAgentIdentity, settings.ExternalWritesEnabled);
    }

    public static HttpRequestMessage Create(TrendyolRequestContext context, HttpMethod method, string relativePath, HttpContent? content = null, bool includeStoreFrontCode = true, string? storeFrontCode = "TR")
    {
        var request = new HttpRequestMessage(method, new Uri(context.BaseAddress, relativePath)) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{context.ApiKey}:{context.ApiSecret}")));
        request.Headers.UserAgent.ParseAdd(context.UserAgentIdentity);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (includeStoreFrontCode && !string.IsNullOrWhiteSpace(storeFrontCode)) request.Headers.TryAddWithoutValidation("storeFrontCode", storeFrontCode.Trim().ToUpperInvariant());
        return request;
    }

    private sealed record CredentialPayload(string ApiKey, string ApiSecret);
    private sealed record ConnectionSettings(string UserAgentIdentity, bool ExternalWritesEnabled);
}

public sealed record TrendyolRequestContext(PlatformConnection Connection, Uri BaseAddress, string ApiKey, string ApiSecret, string UserAgentIdentity, bool ExternalWritesEnabled);
