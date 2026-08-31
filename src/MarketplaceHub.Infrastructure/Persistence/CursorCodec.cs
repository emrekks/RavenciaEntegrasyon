using System.Globalization;
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

    public string EncodeOrder(string sort, DateTimeOffset? dueAt, DateTimeOffset orderedAt, Guid id) =>
        _protector.Protect($"ORDER|{sort}|{dueAt?.UtcTicks.ToString(CultureInfo.InvariantCulture) ?? "-"}|{orderedAt.UtcTicks.ToString(CultureInfo.InvariantCulture)}|{id:D}", TimeSpan.FromHours(1));

    public bool TryDecodeOrder(string? cursor, out string sort, out DateTimeOffset? dueAt, out DateTimeOffset orderedAt, out Guid id)
    {
        sort = string.Empty;
        dueAt = null;
        orderedAt = default;
        id = Guid.Empty;
        if (string.IsNullOrWhiteSpace(cursor)) return true;
        try
        {
            var value = _protector.Unprotect(cursor, out var expiration);
            var parts = value.Split('|');
            long dueTicks = 0;
            long orderedTicks = 0;
            var dueValid = parts.Length == 5 && (parts[2] == "-" || long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out dueTicks));
            var orderedValid = parts.Length == 5 && long.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out orderedTicks);
            if (dueValid && parts[2] != "-") dueAt = new DateTimeOffset(dueTicks, TimeSpan.Zero);
            if (orderedValid) orderedAt = new DateTimeOffset(orderedTicks, TimeSpan.Zero);
            return expiration > timeProvider.GetUtcNow()
                && parts.Length == 5
                && parts[0] == "ORDER"
                && !string.IsNullOrWhiteSpace(parts[1])
                && dueValid
                && orderedValid
                && Guid.TryParse(parts[4], out id)
                && (sort = parts[1]).Length > 0
                && orderedAt != default;
        }
        catch (CryptographicException) { return false; }
        catch (ArgumentOutOfRangeException) { return false; }
    }
}
