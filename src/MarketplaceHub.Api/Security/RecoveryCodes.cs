using System.Security.Cryptography;
using System.Text;
using MarketplaceHub.Infrastructure.Security;

namespace MarketplaceHub.Api.Security;

// Recovery codes are twelve Crockford base32 symbols (60 bits) shown as XXXX-XXXX-XXXX: no I, L, O or U,
// so a code read off paper is not mistyped, and a typed O, I or L is read as 0, 1, 1. Codes issued before
// (two groups of five base64url characters) stay valid until the user makes a new set: a typed code is
// checked under both readings.
public static class RecoveryCodes
{
    public const int Count = 10;
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private const int Symbols = 12;

    public static string NewCode()
    {
        var builder = new StringBuilder(Symbols + 2);
        for (var index = 0; index < Symbols; index++)
        {
            if (index is 4 or 8) builder.Append('-');
            builder.Append(Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]);
        }
        return builder.ToString();
    }

    // The form a new code is stored under.
    public static string Canonical(string code)
    {
        var builder = new StringBuilder(code.Length);
        foreach (var character in code.ToUpperInvariant())
        {
            if (character is '-' or ' ') continue;
            builder.Append(character switch { 'O' => '0', 'I' or 'L' => '1', _ => character });
        }
        return builder.ToString();
    }

    // The form codes issued before Crockford were stored under.
    public static string Legacy(string code) => code.Replace("-", "", StringComparison.Ordinal).Trim().ToUpperInvariant();

    public static string[] Digests(TokenHasher hasher, string code)
    {
        var legacy = hasher.Hash(Legacy(code));
        var canonical = hasher.Hash(Canonical(code));
        return legacy == canonical ? [legacy] : [legacy, canonical];
    }
}
