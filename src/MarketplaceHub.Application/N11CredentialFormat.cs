namespace MarketplaceHub.Application;

// n11 reads the App Key and App Secret from HTTP headers, which only carry
// ASCII. Copying them from the seller panel or an e-mail can bring along
// spaces and invisible characters (zero-width space, BOM, no-break space);
// those are dropped. Anything else outside printable ASCII is rejected so the
// user fixes the value instead of seeing a failed request.
public static class N11CredentialFormat
{
    public static string? Normalize(string? value)
    {
        if (value is null) return null;
        var cleaned = new string(value.Where(character => !IsIgnorable(character)).ToArray());
        return cleaned.Length > 0 && cleaned.All(character => character is >= '!' and <= '~') ? cleaned : null;
    }

    private static bool IsIgnorable(char character) =>
        char.IsWhiteSpace(character) || character is '​' or '‌' or '‍' or '⁠' or '﻿';
}
