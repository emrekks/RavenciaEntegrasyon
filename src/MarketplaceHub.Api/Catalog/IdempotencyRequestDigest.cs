using System.Security.Cryptography;
using System.Text;
using MarketplaceHub.Infrastructure.Security;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

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

    // The keyed digest of a request: method, path, query, the If-Match version it was made against, then the body.
    public static async Task<string> RequestHashAsync(TokenHasher hasher, string method, string requestPath, string query, string ifMatch, string? contentType, byte[] body, CancellationToken cancellationToken)
    {
        var versionLine = string.IsNullOrEmpty(ifMatch) ? string.Empty : $"if-match:{ifMatch}\n";
        var hashInput = Encoding.UTF8.GetBytes($"{method}\n{requestPath}\n{query}\n{versionLine}");
        var digestedBody = await CanonicalBodyAsync(contentType, body, cancellationToken);
        var combined = new byte[hashInput.Length + digestedBody.Length];
        hashInput.CopyTo(combined, 0); digestedBody.CopyTo(combined, hashInput.Length);
        return hasher.HashBytes(Prefix(combined));
    }

    private static readonly byte[] MultipartLabel = Encoding.UTF8.GetBytes("multipart/form-data;parts-v1\n");

    // The body as it is digested. A browser packs every multipart upload with a new random boundary, so the same file
    // sent again (the panel retries a write cut off by a deploy or a dropped connection) has other raw bytes; it would
    // be refused as a reused key although nothing differs. A multipart body is therefore digested part by part (each
    // part's Content-Disposition and Content-Type, then the SHA-256 of its content) without its boundary. Every other
    // body, and a multipart body that cannot be read, is digested as sent.
    public static async Task<byte[]> CanonicalBodyAsync(string? contentType, byte[] body, CancellationToken cancellationToken)
    {
        if (!MediaTypeHeaderValue.TryParse(contentType, out var mediaType)
            || !mediaType.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase))
            return body;
        var boundary = HeaderUtilities.RemoveQuotes(mediaType.Boundary).Value;
        if (string.IsNullOrEmpty(boundary)) return body;
        try
        {
            using var canonical = new MemoryStream();
            canonical.Write(MultipartLabel);
            var reader = new MultipartReader(boundary, new MemoryStream(body, writable: false));
            var parts = 0;
            while (await reader.ReadNextSectionAsync(cancellationToken) is { } section)
            {
                parts++;
                canonical.Write(Encoding.UTF8.GetBytes($"part\ncontent-disposition:{section.ContentDisposition?.Trim()}\ncontent-type:{section.ContentType?.Trim()}\n"));
                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[81920];
                int read;
                while ((read = await section.Body.ReadAsync(buffer, cancellationToken)) > 0) sha.AppendData(buffer, 0, read);
                canonical.Write(Encoding.UTF8.GetBytes(Convert.ToHexString(sha.GetHashAndReset()) + "\n"));
            }
            return parts == 0 ? body : canonical.ToArray();
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException)
        {
            return body;
        }
    }

    // "{user}:{key}"; a key too long to fit with the user prefix is stored as its SHA-256.
    public static string StoredKey(Guid userId, string key)
    {
        var plain = $"{userId:N}:{key}";
        return plain.Length <= MaximumStoredKeyLength ? plain : $"{userId:N}:sha256:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))}";
    }
}

// Successful answers that hand out a secret once (an instant-notification route token and its generated password).
// Their bodies are not stored for replay; a repeat of the same key is told the work is done instead.
public static class SecretResponseRoutes
{
    public static bool Matches(string method, string path)
    {
        if (!HttpMethods.IsPost(method)) return false;
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        // Ayarlar › Kullanıcılar: adding a user and resetting a password answer with a one-time password.
        if (segments.Length >= 4 && segments[0].Equals("api", StringComparison.OrdinalIgnoreCase) && segments[1].Equals("v1", StringComparison.OrdinalIgnoreCase)
            && segments[2].Equals("settings", StringComparison.OrdinalIgnoreCase) && segments[3].Equals("users", StringComparison.OrdinalIgnoreCase)
            && (segments.Length == 4 || segments.Length == 6 && segments[5].Equals("password-reset", StringComparison.OrdinalIgnoreCase)))
            return true;
        // Google ile Bağlan: Google's consent address carries the one-time OAuth state, of which the server keeps only a hash.
        if (segments.Length == 6 && segments[0].Equals("api", StringComparison.OrdinalIgnoreCase) && segments[1].Equals("v1", StringComparison.OrdinalIgnoreCase)
            && segments[2].Equals("settings", StringComparison.OrdinalIgnoreCase) && segments[3].Equals("offsite-backup", StringComparison.OrdinalIgnoreCase)
            && segments[4].Equals("google", StringComparison.OrdinalIgnoreCase) && segments[5].Equals("authorize", StringComparison.OrdinalIgnoreCase))
            return true;
        return segments.Length == 5
            && segments[0].Equals("api", StringComparison.OrdinalIgnoreCase) && segments[1].Equals("v1", StringComparison.OrdinalIgnoreCase)
            && segments[2].Equals("connections", StringComparison.OrdinalIgnoreCase)
            && segments[4].Equals("instant-notifications", StringComparison.OrdinalIgnoreCase);
    }
}
