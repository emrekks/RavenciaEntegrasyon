using System.Buffers.Binary;
using System.Security.Cryptography;

namespace MarketplaceHub.Infrastructure.Security;

public sealed class TotpService(TimeProvider timeProvider)
{
    public const int StepSeconds = 30;
    public byte[] NewSecret() => RandomNumberGenerator.GetBytes(20);

    public bool TryValidate(byte[] secret, string code, long? lastAcceptedStep, out long acceptedStep)
    {
        acceptedStep = -1;
        if (code.Length != 6 || !code.All(char.IsAsciiDigit)) return false;
        var current = timeProvider.GetUtcNow().ToUnixTimeSeconds() / StepSeconds;
        for (var offset = -1; offset <= 1; offset++)
        {
            var candidate = current + offset;
            if (candidate <= lastAcceptedStep || !CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.ASCII.GetBytes(Generate(secret, candidate)),
                    System.Text.Encoding.ASCII.GetBytes(code))) continue;
            acceptedStep = candidate;
            return true;
        }
        return false;
    }

    public static string Generate(byte[] secret, long timeStep)
    {
        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, timeStep);
        var hash = HMACSHA1.HashData(secret, counter);
        var offset = hash[^1] & 0x0f;
        var binary = ((hash[offset] & 0x7f) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }
}
