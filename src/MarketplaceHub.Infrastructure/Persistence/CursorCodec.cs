using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace MarketplaceHub.Infrastructure.Persistence;

public sealed class CursorCodec(IDataProtectionProvider provider, TimeProvider timeProvider)
{
    private readonly ITimeLimitedDataProtector _protector = provider.CreateProtector("MarketplaceHub.Cursor.v1").ToTimeLimitedDataProtector();

    public string Encode(Guid id) => _protector.Protect(id.ToString("D"), TimeSpan.FromHours(1));

    public bool TryDecode(string? cursor, out Guid id)
    {
        id = Guid.Empty;
        if (string.IsNullOrWhiteSpace(cursor)) return true;
        try
        {
            var value = _protector.Unprotect(cursor, out var expiration);
            return expiration > timeProvider.GetUtcNow() && Guid.TryParse(value, out id);
        }
        catch (CryptographicException) { return false; }
    }
}
