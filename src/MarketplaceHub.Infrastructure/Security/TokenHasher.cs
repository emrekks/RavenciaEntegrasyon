using System.Security.Cryptography;
using System.Text;

namespace MarketplaceHub.Infrastructure.Security;

public sealed class TokenHasher(byte[] key)
{
    private readonly byte[] _key = key.Length >= 32 ? key : throw new ArgumentException("Credential key must be at least 32 bytes.", nameof(key));

    public string Hash(string value) => Convert.ToHexString(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(value)));

    public bool Verify(string value, string expectedHex)
    {
        var actual = Convert.FromHexString(Hash(value));
        var expected = Convert.FromHexString(expectedHex);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    public static string NewToken(int bytes = 32) => Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
