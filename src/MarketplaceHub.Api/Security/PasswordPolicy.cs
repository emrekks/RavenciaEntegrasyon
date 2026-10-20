using System.Globalization;
using System.Text;

namespace MarketplaceHub.Api.Security;

// New passwords are 15-64 characters (Identity also asks for upper/lower case, a digit and a symbol) and
// must not be a common password dressed up to reach that length. Most entries of the usual "10,000 most
// common passwords" lists are shorter than 15 characters, so instead of shipping such a list the check
// folds the password (lower case, Turkish letters to ASCII, l33t letters back to letters, the digits and
// symbols around it removed) and refuses it when what is left is
//   - a bundled common password or phrase (CommonPasswords.Phrases),
//   - one or two bundled common words, a word repeated, or a keyboard/number run,
//   - too few different characters, or
//   - the person's own e-mail name or display name.
public static class PasswordPolicy
{
    public const int MinimumLength = 15;
    public const int MaximumLength = 64;
    private const int MinimumDistinctCharacters = 6;

    public static bool IsAcceptable(string? password, params string?[] personalValues)
    {
        if (password is null || password.Length is < MinimumLength or > MaximumLength) return false;
        if (password.Distinct().Count() < MinimumDistinctCharacters) return false;

        var lowered = FoldLetters(password);
        if (CommonPasswords.Phrases.Contains(lowered)) return false;
        if (IsRun(new string(lowered.Where(char.IsLetterOrDigit).ToArray()))) return false;

        // Words with digits or symbols only around them ("P@ssw0rd123456789!", "MarketplaceHub1!"): the part in
        // between, with l33t letters read back, decides. Digits or symbols inside the words make it something else.
        var core = Unleet(TrimNonLetters(lowered));
        if (core.Length > 0 && core.All(IsAsciiLetter)
            && (CommonPasswords.Phrases.Contains(core) || IsCommonWords(core) || IsRepeated(core) || IsRun(core))) return false;
        // The same common word more than once, whatever stands between ("Ravencia2026Ravencia").
        var plainLetters = new string(lowered.Where(IsAsciiLetter).ToArray());
        if (RepeatedPiece(plainLetters) is { } piece && CommonPasswords.Words.Contains(piece)) return false;

        foreach (var value in personalValues.SelectMany(PersonalParts))
        {
            if (plainLetters.Contains(value, StringComparison.Ordinal) || core.Contains(value, StringComparison.Ordinal)) return false;
        }
        return true;
    }

    // Lower case with Turkish letters folded to their ASCII look-alikes (ş→s, ı→i, ğ→g, ü→u, ö→o, ç→c).
    internal static string FoldLetters(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value.ToLower(CultureInfo.InvariantCulture).Replace('ı', 'i').Normalize(NormalizationForm.FormD))
        {
            // Accents and cedillas (ş, ğ, ü, ö, ç, İ's dot) are separate marks after FormD and are dropped.
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark) builder.Append(character);
        }
        return builder.ToString();
    }

    private static string TrimNonLetters(string value)
    {
        var start = 0; var end = value.Length;
        while (start < end && !IsAsciiLetter(value[start])) start++;
        while (end > start && !IsAsciiLetter(value[end - 1])) end--;
        return value[start..end];
    }

    private static string Unleet(string value) => new(value.Select(character => character switch
    {
        '@' or '4' => 'a',
        '$' or '5' => 's',
        '0' => 'o',
        '3' => 'e',
        '1' or '!' or '|' => 'i',
        '7' or '+' => 't',
        '8' => 'b',
        '9' => 'g',
        _ => character
    }).ToArray());

    private static bool IsAsciiLetter(char character) => character is >= 'a' and <= 'z';

    // One or two bundled words, in any order ("marketplacehub", "passwordpassword", "galatasaray").
    private static bool IsCommonWords(string letters)
    {
        if (CommonPasswords.Words.Contains(letters)) return true;
        for (var split = CommonPasswords.ShortestWord; split <= letters.Length - CommonPasswords.ShortestWord; split++)
        {
            if (CommonPasswords.Words.Contains(letters[..split]) && CommonPasswords.Words.Contains(letters[split..])) return true;
        }
        return false;
    }

    // "abcabcabcabc", "xyzxyz": the same piece over and over.
    private static bool IsRepeated(string value) => RepeatedPiece(value) is not null;

    private static string? RepeatedPiece(string value)
    {
        for (var length = 1; length <= value.Length / 2; length++)
        {
            if (value.Length % length != 0) continue;
            var piece = value[..length];
            if (Enumerable.Range(0, value.Length / length).All(index => string.CompareOrdinal(value, index * length, piece, 0, length) == 0)) return piece;
        }
        return null;
    }

    // A run up or down the alphabet or the digits ("123456789012345", "abcdefghijklmnop") or along a keyboard row.
    private static bool IsRun(string value)
    {
        if (value.Length < 4) return false;
        if (CommonPasswords.KeyboardRows.Any(row => row.Contains(value, StringComparison.Ordinal))) return true;
        var up = true; var down = true;
        for (var index = 1; index < value.Length; index++)
        {
            var step = Step(value[index - 1], value[index]);
            up &= step == 1; down &= step == -1;
        }
        return up || down;
    }

    private static int Step(char previous, char next)
    {
        if (char.IsDigit(previous) && char.IsDigit(next))
        {
            var difference = (next - previous + 10) % 10;
            return difference == 1 ? 1 : difference == 9 ? -1 : 0;
        }
        return next - previous;
    }

    // The e-mail's local part and each word of the display name, folded, when long enough to matter.
    private static IEnumerable<string> PersonalParts(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) yield break;
        var folded = FoldLetters(value);
        var at = folded.IndexOf('@');
        if (at > 0) folded = folded[..at];
        foreach (var part in folded.Split([' ', '.', '_', '-', '+'], StringSplitOptions.RemoveEmptyEntries))
        {
            var letters = new string(part.Where(IsAsciiLetter).ToArray());
            if (letters.Length >= 4) yield return letters;
        }
    }
}
