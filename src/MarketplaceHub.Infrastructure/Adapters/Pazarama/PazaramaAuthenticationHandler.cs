using System.Security.Cryptography;
using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MarketplaceHub.Infrastructure.Adapters.Pazarama;

// Loads the encrypted client id/secret of a Pazarama connection. The connection service
// stores them in the shared {ApiKey, ApiSecret} credential shape.
public sealed class PazaramaAuthenticationHandler(
    AppDbContext db,
    IDataProtectionProvider dataProtection,
    ILogger<PazaramaAuthenticationHandler> logger)
{
    private readonly IDataProtector protector = dataProtection.CreateProtector("MarketplaceHub.PlatformCredential.v1");

    public async Task<PazaramaRequestContext?> LoadAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken)
    {
        var connection = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(
            x => x.TenantId == tenantId && x.Id == connectionId && x.PlatformCode == "PAZARAMA" && x.ApiVersion == "V1",
            cancellationToken);
        if (connection is null) return null;

        var credential = await db.PlatformCredentials.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.RevokedAt == null)
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (credential is null) return null;

        CredentialPayload? payload;
        try { payload = JsonSerializer.Deserialize<CredentialPayload>(protector.Unprotect(credential.ProtectedPayload)); }
        catch (Exception exception) when (exception is CryptographicException or JsonException)
        {
            logger.LogWarning(exception, "Pazarama credential çözülemedi. ConnectionId: {ConnectionId}", connectionId);
            return null;
        }

        if (payload is null || string.IsNullOrWhiteSpace(payload.ApiKey) || string.IsNullOrWhiteSpace(payload.ApiSecret)) return null;
        return new(connection, credential.Id, payload.ApiKey.Trim(), payload.ApiSecret.Trim());
    }

    private sealed record CredentialPayload(string ApiKey, string ApiSecret)
    {
        public override string ToString() => nameof(CredentialPayload);
    }
}

// CredentialId keys the token cache, so a rotated client secret never reuses an old token.
public sealed record PazaramaRequestContext(PlatformConnection Connection, Guid CredentialId, string ClientId, string ClientSecret)
{
    // The generated ToString would print the client secret; a logged or thrown context must not leak it.
    public override string ToString() => $"PazaramaRequestContext {{ ConnectionId = {Connection.Id} }}";
}
