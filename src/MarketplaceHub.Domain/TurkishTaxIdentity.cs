namespace MarketplaceHub.Domain;

/// <summary>Check-digit validation for Turkish identity (TCKN, 11 digits) and tax (VKN, 10 digits) numbers.</summary>
public static class TurkishTaxIdentity
{
    public static bool IsValid(string? value) => value?.Length switch
    {
        10 => IsValidVkn(value),
        11 => IsValidTckn(value),
        _ => false
    };

    public static bool IsValidTckn(string? value)
    {
        if (value is not { Length: 11 } || !value.All(char.IsAsciiDigit) || value[0] == '0') return false;
        var digits = value.Select(character => character - '0').ToArray();
        var odd = digits[0] + digits[2] + digits[4] + digits[6] + digits[8];
        var even = digits[1] + digits[3] + digits[5] + digits[7];
        var tenth = ((odd * 7 - even) % 10 + 10) % 10;
        if (digits[9] != tenth) return false;
        return digits[10] == digits.Take(10).Sum() % 10;
    }

    public static bool IsValidVkn(string? value)
    {
        if (value is not { Length: 10 } || !value.All(char.IsAsciiDigit)) return false;
        var sum = 0;
        for (var index = 0; index < 9; index++)
        {
            var shifted = (value[index] - '0' + 9 - index) % 10;
            var weighted = shifted * (1 << (9 - index)) % 9;
            if (shifted != 0 && weighted == 0) weighted = 9;
            sum += weighted;
        }
        return value[9] - '0' == (10 - sum % 10) % 10;
    }
}
