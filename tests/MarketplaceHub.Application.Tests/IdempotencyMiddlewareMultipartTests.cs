using System.Text;
using MarketplaceHub.Api.Catalog;
using MarketplaceHub.Infrastructure.Security;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// API-GUVENLIK-2: a browser packs each multipart upload with a new random boundary. The same file sent again under the
// same Idempotency-Key (the panel retries a write whose answer was lost) must count as the same request and replay the
// first answer, not be refused as a reused key; a different file under that key still is.
public sealed class IdempotencyMiddlewareMultipartTests
{
    private static readonly TokenHasher Hasher = new(Enumerable.Repeat((byte)5, 32).ToArray());
    private const string Path = "/api/v1/files/product-media";

    [Fact]
    public async Task TheSameFileUnderAnotherBoundary_HasTheSameDigest()
    {
        var first = await HashAsync("----WebKitFormBoundaryAbc123", Photo);
        var second = await HashAsync("----WebKitFormBoundaryXyz789", Photo);

        Assert.Equal(first, second);
    }

    [Fact]
    public async Task AFileThatDiffersByOneByte_HasAnotherDigest()
    {
        var changed = Photo.ToArray();
        changed[^1] ^= 0x01;

        Assert.NotEqual(await HashAsync("----WebKitFormBoundaryAbc123", Photo), await HashAsync("----WebKitFormBoundaryAbc123", changed));
    }

    [Fact]
    public async Task AnotherFileNameOrField_HasAnotherDigest()
    {
        var original = await HashAsync("b1", Photo);

        Assert.NotEqual(original, await HashAsync("b2", Photo, fileName: "other.jpg"));
        Assert.NotEqual(original, await HashAsync("b2", Photo, productId: "22222222-2222-2222-2222-222222222222"));
    }

    [Fact]
    public async Task AJsonBody_IsDigestedAsSent()
    {
        var body = "{\"title\":\"x\"}"u8.ToArray();

        Assert.Same(body, await IdempotencyRequestDigest.CanonicalBodyAsync("application/json", body, CancellationToken.None));
        // A multipart body that cannot be read is digested as sent, not refused.
        var broken = "not a multipart body"u8.ToArray();
        Assert.Same(broken, await IdempotencyRequestDigest.CanonicalBodyAsync("multipart/form-data; boundary=zzz", broken, CancellationToken.None));
    }

    private static readonly byte[] Photo = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x02, 0x03];

    private static Task<string> HashAsync(string boundary, byte[] file, string fileName = "photo.jpg", string productId = "11111111-1111-1111-1111-111111111111")
    {
        using var body = new MemoryStream();
        void Text(string value) => body.Write(Encoding.UTF8.GetBytes(value));
        Text($"--{boundary}\r\nContent-Disposition: form-data; name=\"productId\"\r\n\r\n{productId}\r\n");
        Text($"--{boundary}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"{fileName}\"\r\nContent-Type: image/jpeg\r\n\r\n");
        body.Write(file);
        Text($"\r\n--{boundary}--\r\n");
        return IdempotencyRequestDigest.RequestHashAsync(Hasher, "POST", Path, string.Empty, string.Empty,
            $"multipart/form-data; boundary={boundary}", body.ToArray(), CancellationToken.None);
    }
}
