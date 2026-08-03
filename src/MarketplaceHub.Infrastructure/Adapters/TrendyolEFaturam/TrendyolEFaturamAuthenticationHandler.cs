using System.Security.Cryptography;
using System.Text.Json;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.TrendyolEFaturam.Contracts;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MarketplaceHub.Infrastructure.Adapters.TrendyolEFaturam;

public sealed class TrendyolEFaturamAuthenticationHandler(AppDbContext db, IDataProtectionProvider dataProtection, IOptions<TrendyolEFaturamOptions> options)
{
    private readonly IDataProtector _protector = dataProtection.CreateProtector("MarketplaceHub.PlatformCredential.v1");

    public async Task<TrendyolEFaturamRequestContext?> LoadAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken)
    {
        var connection = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == connectionId && x.PlatformCode == "TRENDYOL_EFATURAM" && x.ApiVersion == "1.0.0", cancellationToken);
        var credential = await db.PlatformCredentials.AsNoTracking().Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.CredentialType == "EMAIL_PASSWORD" && x.RevokedAt == null).OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(cancellationToken);
        if (connection is null || credential is null) return null;
        CredentialPayload? payload; TrendyolEFaturamConnectionSettings? settings;
        try { payload = JsonSerializer.Deserialize<CredentialPayload>(_protector.Unprotect(credential.ProtectedPayload)); settings = JsonSerializer.Deserialize<TrendyolEFaturamConnectionSettings>(connection.SettingsJson); }
        catch (Exception exception) when (exception is CryptographicException or JsonException) { return null; }
        if (payload is null || settings is null || string.IsNullOrWhiteSpace(payload.Email) || string.IsNullOrWhiteSpace(payload.Password)) return null;
        var baseAddress = connection.Environment == "PRODUCTION" ? options.Value.ProductionBaseAddress : options.Value.StageBaseAddress;
        return new(connection, baseAddress, payload.Email, payload.Password, settings);
    }

    private sealed record CredentialPayload(string Email, string Password);
}

public sealed record TrendyolEFaturamRequestContext(PlatformConnection Connection, Uri BaseAddress, string Email, string Password, TrendyolEFaturamConnectionSettings Settings)
{
    public string IntegrationModel => Settings.IntegrationModel;
    public bool ExternalWritesEnabled => Settings.ExternalWritesEnabled;
}
