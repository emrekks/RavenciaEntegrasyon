using System.Security.Cryptography;
using System.Text.Json;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using MarketplaceHub.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MarketplaceHub.Infrastructure.Adapters.PttAvm;

// Loads the encrypted API user name and password of a PTT AVM connection. The seller panel's Hesap Yönetimi ›
// Entegrasyon Bilgileri screen shows them next to the Mağaza ID, which is kept as the connection's ExternalStoreId.
public sealed class PttAvmAuthenticationHandler(
    AppDbContext db,
    IDataProtectionProvider dataProtection,
    ILogger<PttAvmAuthenticationHandler> logger)
{
    private readonly IDataProtector protector = dataProtection.CreateProtector("MarketplaceHub.PlatformCredential.v1");

    public async Task<PttAvmCredentialLoad> LoadAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken)
    {
        var connection = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(
            x => x.TenantId == tenantId && x.Id == connectionId && x.PlatformCode == "PTTAVM" && x.ApiVersion == "V1",
            cancellationToken);
        if (connection is null) return PttAvmCredentialLoad.Missing;

        var credential = await db.PlatformCredentials.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.RevokedAt == null)
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (credential is null) return PttAvmCredentialLoad.Missing;

        string json;
        try { json = BoundSecret.UnprotectForConnection(protector, credential.TenantId, credential.ConnectionId, credential.ProtectedPayload); }
        catch (CryptographicException exception)
        {
            logger.LogWarning(exception, "PTT AVM credential çözülemedi. ConnectionId: {ConnectionId}", connectionId);
            return PttAvmCredentialLoad.Missing;
        }
        return Read(connection, json);
    }

    // The first PTT AVM release stored {ApiKey, AccessToken} for the REST API; such a payload cannot sign a SOAP
    // call and is reported as rejected credentials, so the owner enters the user name and password instead.
    internal static PttAvmCredentialLoad Read(PlatformConnection connection, string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return PttAvmCredentialLoad.Missing;
            var username = Text(root, "Username");
            var password = Text(root, "Password");
            if (username is not null && password is not null) return new(new(connection, username.Trim(), password), false);
            var legacy = Text(root, "ApiKey") is not null || Text(root, "AccessToken") is not null;
            return new(null, legacy);
        }
        catch (JsonException) { return PttAvmCredentialLoad.Missing; }
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString() : null;
}

// Account is null when the connection has no usable credential; LegacyApiKeyPayload says the stored one is the
// old API Key / Access Token pair.
public sealed record PttAvmCredentialLoad(PttAvmRequestContext? Account, bool LegacyApiKeyPayload)
{
    public static readonly PttAvmCredentialLoad Missing = new(null, false);
}

public sealed record PttAvmRequestContext(PlatformConnection Connection, string Username, string Password)
{
    // The generated ToString would print the user name and the password; a logged or thrown context must not leak them.
    public override string ToString() => $"PttAvmRequestContext {{ ConnectionId = {Connection.Id} }}";
}
