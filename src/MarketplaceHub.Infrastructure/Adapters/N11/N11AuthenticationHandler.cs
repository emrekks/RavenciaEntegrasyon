using System.Security.Cryptography;
using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MarketplaceHub.Infrastructure.Adapters.N11;

public sealed class N11AuthenticationHandler(
    AppDbContext db,
    IDataProtectionProvider dataProtection,
    ILogger<N11AuthenticationHandler> logger)
{
    private readonly IDataProtector protector = dataProtection.CreateProtector("MarketplaceHub.PlatformCredential.v1");

    public async Task<N11RequestContext?> LoadAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken)
    {
        var connection = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(
            x => x.TenantId == tenantId && x.Id == connectionId && x.PlatformCode == "N11" && x.ApiVersion == "V1",
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
            logger.LogWarning(exception, "N11 credential çözülemedi. ConnectionId: {ConnectionId}", connectionId);
            return null;
        }

        if (payload is null || string.IsNullOrWhiteSpace(payload.ApiKey) || string.IsNullOrWhiteSpace(payload.ApiSecret)) return null;
        // Older records may still carry invisible characters copied from the
        // seller panel; a value that cannot be cleaned is passed on as is and
        // rejected by the client with a clear message.
        return new(connection, N11CredentialFormat.Normalize(payload.ApiKey) ?? payload.ApiKey, N11CredentialFormat.Normalize(payload.ApiSecret) ?? payload.ApiSecret);
    }

    private sealed record CredentialPayload(string ApiKey, string ApiSecret);
}

public sealed record N11RequestContext(PlatformConnection Connection, string AppKey, string AppSecret);
