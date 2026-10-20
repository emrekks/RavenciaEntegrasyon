using System.Security.Cryptography;
using System.Text;

namespace MarketplaceHub.Api.Catalog;

// How a keyed request is identified in ops.api_idempotency_records.
public static class IdempotencyRequestDigest
{
    private static readonly byte[] Domain = Encoding.UTF8.GetBytes("marketplacehub/idempotency-request/v2\n");
    private const int MaximumStoredKeyLength = 256;

    // The bytes keyed by TokenHasher.HashBytes: a domain label first, so the digest cannot stand in for any
    // other HMAC the server makes with the same key.
    public static byte[] Prefix(byte[] request)
    {
        var bytes = new byte[Domain.Length + request.Length];
        Domain.CopyTo(bytes, 0); request.CopyTo(bytes, Domain.Length);
        return bytes;
    }

    // "{user}:{key}"; a key too long to fit with the user prefix is stored as its SHA-256.
    public static string StoredKey(Guid userId, string key)
    {
        var plain = $"{userId:N}:{key}";
        return plain.Length <= MaximumStoredKeyLength ? plain : $"{userId:N}:sha256:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))}";
    }
}

// Successful answers that hand out a secret once (a webhook route token, a generated webhook password).
// Their bodies are not stored for replay; a repeat of the same key is told the work is done instead.
public static class SecretResponseRoutes
{
    public static bool Matches(string method, string path)
    {
        if (!HttpMethods.IsPost(method)) return false;
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 5
            && segments[0].Equals("api", StringComparison.OrdinalIgnoreCase) && segments[1].Equals("v1", StringComparison.OrdinalIgnoreCase)
            && segments[2].Equals("connections", StringComparison.OrdinalIgnoreCase)
            && (segments[4].Equals("webhooks", StringComparison.OrdinalIgnoreCase) || segments[4].Equals("instant-notifications", StringComparison.OrdinalIgnoreCase));
    }
}
