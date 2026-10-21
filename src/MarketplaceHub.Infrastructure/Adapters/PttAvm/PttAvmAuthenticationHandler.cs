using System.Security.Cryptography;
using System.Text.Json;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using MarketplaceHub.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MarketplaceHub.Infrastructure.Adapters.PttAvm;

// Loads the encrypted API Key and Access Token of a PTT AVM connection. Both are produced in the
// seller panel (Hesap Yönetimi › Entegrasyon Bilgileri) for the selected integrator and store.
public sealed class PttAvmAuthenticationHandler(
    AppDbContext db,
    IDataProtectionProvider dataProtection,
    ILogger<PttAvmAuthenticationHandler> logger)
{
    private readonly IDataProtector protector = dataProtection.CreateProtector("MarketplaceHub.PlatformCredential.v1");

    public async Task<PttAvmRequestContext?> LoadAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken)
    {
        var connection = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(
            x => x.TenantId == tenantId && x.Id == connectionId && x.PlatformCode == "PTTAVM" && x.ApiVersion == "V1",
            cancellationToken);
        if (connection is null) return null;

        var credential = await db.PlatformCredentials.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.RevokedAt == null)
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (credential is null) return null;

        CredentialPayload? payload;
        try { payload = JsonSerializer.Deserialize<CredentialPayload>(BoundSecret.UnprotectForConnection(protector, credential.TenantId, credential.ConnectionId, credential.ProtectedPayload)); }
        catch (Exception exception) when (exception is CryptographicException or JsonException)
        {
            logger.LogWarning(exception, "PTT AVM credential çözülemedi. ConnectionId: {ConnectionId}", connectionId);
            return null;
        }

        if (payload is null || string.IsNullOrWhiteSpace(payload.ApiKey) || string.IsNullOrWhiteSpace(payload.AccessToken)) return null;
        return new(connection, payload.ApiKey.Trim(), payload.AccessToken.Trim());
    }

    private sealed record CredentialPayload(string ApiKey, string AccessToken)
    {
        public override string ToString() => nameof(CredentialPayload);
    }
}

public sealed record PttAvmRequestContext(PlatformConnection Connection, string ApiKey, string AccessToken)
{
    // The generated ToString would print the key and the token; a logged or thrown context must not leak them.
    public override string ToString() => $"PttAvmRequestContext {{ ConnectionId = {Connection.Id} }}";
}
