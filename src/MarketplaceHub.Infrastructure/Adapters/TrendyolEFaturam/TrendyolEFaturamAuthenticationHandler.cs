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

        TrendyolEFaturamCredentialPayload? payload;
        TrendyolEFaturamConnectionSettings? settings;
        try
        {
            payload = JsonSerializer.Deserialize<TrendyolEFaturamCredentialPayload>(_protector.Unprotect(credential.ProtectedPayload));
            settings = JsonSerializer.Deserialize<TrendyolEFaturamConnectionSettings>(connection.SettingsJson);
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException)
        {
            return null;
        }

        if (payload is null || settings is null || !ValidCredential(settings.IntegrationModel, payload)) return null;
        var baseAddress = connection.Environment == "PRODUCTION" ? options.Value.ProductionBaseAddress : options.Value.StageBaseAddress;
        return new(connection, baseAddress, payload, settings);
    }

    private static bool ValidCredential(string model, TrendyolEFaturamCredentialPayload payload) => model switch
    {
        "API_USER" => !string.IsNullOrWhiteSpace(payload.Email) && !string.IsNullOrWhiteSpace(payload.Password),
        "MARKETPLACE" => !string.IsNullOrWhiteSpace(payload.PartnerEmail) && !string.IsNullOrWhiteSpace(payload.PartnerPassword)
                         && !string.IsNullOrWhiteSpace(payload.CustomerEmail) && !string.IsNullOrWhiteSpace(payload.CustomerPassword)
                         && payload.CustomerTaxId is { Length: 10 or 11 } taxId && taxId.All(char.IsAsciiDigit),
        _ => false
    };
}

public sealed record TrendyolEFaturamRequestContext(
    PlatformConnection Connection,
    Uri BaseAddress,
    TrendyolEFaturamCredentialPayload Credential,
    TrendyolEFaturamConnectionSettings Settings)
{
    public string IntegrationModel => Settings.IntegrationModel;
    public bool ExternalWritesEnabled => Settings.ExternalWritesEnabled;
}

public sealed record TrendyolEFaturamAccessContext(string AccessToken, long CompanyId, long UserId, long? PartnerId);
