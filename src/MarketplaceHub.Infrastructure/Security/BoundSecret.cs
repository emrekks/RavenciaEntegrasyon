using Microsoft.AspNetCore.DataProtection;

namespace MarketplaceHub.Infrastructure.Security;

// Secrets stored with Data Protection are bound to what they belong to: a marketplace credential or
// webhook secret to its tenant and connection, an authenticator secret to its user. A value copied onto
// another row (another tenant's connection, another user) no longer decrypts there.
//
// Bound values carry the "b1." prefix (Data Protection output is base64url and never contains a dot).
// Values written before binding have no prefix and are still read with the plain purpose; they are
// re-written bound the next time the secret is saved (credential rotation, new webhook, new authenticator).
public static class BoundSecret
{
    public const string Prefix = "b1.";

    public static string ConnectionScope(Guid tenantId, Guid connectionId) => $"tenant:{tenantId:N}/connection:{connectionId:N}";
    public static string UserScope(Guid userId) => $"user:{userId:N}";

    public static string Protect(IDataProtector purpose, string scope, string plaintext) =>
        Prefix + purpose.CreateProtector(scope).Protect(plaintext);

    public static string Unprotect(IDataProtector purpose, string scope, string stored) =>
        stored.StartsWith(Prefix, StringComparison.Ordinal)
            ? purpose.CreateProtector(scope).Unprotect(stored[Prefix.Length..])
            : purpose.Unprotect(stored);

    public static bool IsBound(string? stored) => stored?.StartsWith(Prefix, StringComparison.Ordinal) == true;

    // Marketplace credentials and webhook verifier secrets.
    public static string ProtectForConnection(IDataProtector purpose, Guid tenantId, Guid connectionId, string plaintext) =>
        Protect(purpose, ConnectionScope(tenantId, connectionId), plaintext);

    public static string UnprotectForConnection(IDataProtector purpose, Guid tenantId, Guid connectionId, string stored) =>
        Unprotect(purpose, ConnectionScope(tenantId, connectionId), stored);
}
