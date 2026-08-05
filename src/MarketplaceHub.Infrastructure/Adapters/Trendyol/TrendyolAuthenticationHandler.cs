using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MarketplaceHub.Infrastructure.Adapters.Trendyol;

public sealed class TrendyolAuthenticationHandler(AppDbContext db, IDataProtectionProvider dataProtection, IOptions<TrendyolOptions> options)
{
    private readonly IDataProtector _protector = dataProtection.CreateProtector("MarketplaceHub.PlatformCredential.v1");

    public async Task<TrendyolRequestContext?> LoadAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken)
    {
        var connection = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == connectionId && x.PlatformCode == "TRENDYOL" && x.ApiVersion == "V2", cancellationToken);
        var credential = await db.PlatformCredentials.AsNoTracking().Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.RevokedAt == null).OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(cancellationToken);
        if (connection is null || credential is null) return null;
        CredentialPayload? payload; ConnectionSettings? settings;
        try { payload = JsonSerializer.Deserialize<CredentialPayload>(_protector.Unprotect(credential.ProtectedPayload)); settings = JsonSerializer.Deserialize<ConnectionSettings>(connection.SettingsJson); }
        catch (Exception exception) when (exception is CryptographicException or JsonException) { return null; }
        if (payload is null || settings is null || string.IsNullOrWhiteSpace(settings.UserAgentIdentity)) return null;
        var baseAddress = connection.Environment == "PRODUCTION" ? options.Value.ProductionBaseAddress : options.Value.StageBaseAddress;
        return new(connection, baseAddress, payload.ApiKey, payload.ApiSecret, settings.UserAgentIdentity, settings.ExternalWritesEnabled);
    }

    public static HttpRequestMessage Create(TrendyolRequestContext context, HttpMethod method, string relativePath, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, new Uri(context.BaseAddress, relativePath)) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{context.ApiKey}:{context.ApiSecret}")));
        request.Headers.UserAgent.ParseAdd(context.UserAgentIdentity);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("storeFrontCode", "TR");
        return request;
    }

    private sealed record CredentialPayload(string ApiKey, string ApiSecret);
    private sealed record ConnectionSettings(string UserAgentIdentity, bool ExternalWritesEnabled);
}

public sealed record TrendyolRequestContext(PlatformConnection Connection, Uri BaseAddress, string ApiKey, string ApiSecret, string UserAgentIdentity, bool ExternalWritesEnabled);
