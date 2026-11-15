using System.Text.RegularExpressions;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// ADAPTOR-TY-HB-9 / ADAPTOR-DIGER-10: the marketplace fixtures are copied from official samples and live answers, and the
// repository is public. They carry no personal data: no valid Turkish identity number, no real mobile number, no e-mail
// address outside the reserved example domains. Synthetic values stay allowed (00000000000, 5550000000, *@example.test).
public sealed partial class FixturePersonalDataTests
{
    // Well-known test identity numbers with a valid checksum.
    private static readonly HashSet<string> SyntheticIdentityNumbers = ["11111111110", "10000000146", "12345678950"];
    private static readonly string[] ExampleMailSuffixes = [".test", ".example", ".invalid", ".localhost", "example.com", "example.org", "example.net"];

    [Fact]
    public void FixturesCarryNoPersonalData()
    {
        var root = Path.Combine(TestRepository.TestProject(), "Fixtures");
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToList();
        Assert.NotEmpty(files);
        var findings = new List<string>();
        foreach (var file in files)
        {
            var name = Path.GetRelativePath(root, file).Replace('\\', '/');
            var lines = File.ReadAllLines(file);
            for (var index = 0; index < lines.Length; index++)
                findings.AddRange(Findings(lines[index]).Select(finding => $"{name}:{index + 1}: {finding}"));
        }
        Assert.True(findings.Count == 0, "Personal data in fixtures (replace with synthetic values):\n" + string.Join("\n", findings));
    }

    [Theory]
    [InlineData("\"turkishIdentityNumber\": \"10000000078\"", "identity number")]
    [InlineData("<a:TCKN>10000000078</a:TCKN>", "identity number")]
    [InlineData("\"phone\": \"5321234568\"", "mobile number")]
    [InlineData("\"gsm\": \"0532 123 45 68\"", "mobile number")]
    [InlineData("\"note\": \"call +90 532 123 45 68\"", "mobile number")]
    [InlineData("\"email\": \"ayse.yilmaz@gmail.com\"", "e-mail")]
    public void RealLookingValuesAreFound(string line, string kind) =>
        Assert.Contains(Findings(line), finding => finding.StartsWith(kind, StringComparison.Ordinal));

    [Theory]
    [InlineData("\"turkishIdentityNumber\": \"00000000000\"")]
    [InlineData("\"turkishIdentityNumber\": \"11111111110\"")]
    [InlineData("\"orderNumber\": \"10654411111\"")]
    [InlineData("\"lineItemId\": \"61680dc6-b404-55fe-1171-d92506060606\"")]
    [InlineData("\"phone\": \"5550000000\"")]
    [InlineData("\"phoneNumber\": \"05550000000\"")]
    [InlineData("\"phone\": \"+905550000000\"")]
    [InlineData("\"gsm\": \"5xxxxxxxxx\"")]
    [InlineData("\"code\": \"6225816234669\"")]
    [InlineData("\"agreedDeliveryDate\": 1725310828346")]
    [InlineData("\"email\": \"customer@example.test\"")]
    [InlineData("\"email\": \"buyer@shop.example.com\"")]
    public void SyntheticValuesPass(string line) => Assert.Empty(Findings(line));

    private static IEnumerable<string> Findings(string line)
    {
        foreach (Match match in ElevenDigits().Matches(line))
            if (IsValidIdentityNumber(match.Value) && !SyntheticIdentityNumbers.Contains(match.Value))
                yield return $"identity number {match.Value} (valid TCKN checksum)";

        var phones = PhoneField().Matches(line).Select(match => match.Groups["value"].Value)
            .Concat(PrefixedMobile().Matches(line).Select(match => match.Value));
        foreach (var phone in phones)
        {
            var digits = new string(phone.Where(char.IsAsciiDigit).ToArray());
            if (digits.StartsWith("0090", StringComparison.Ordinal)) digits = digits[4..];
            else if (digits.StartsWith("90", StringComparison.Ordinal) && digits.Length == 12) digits = digits[2..];
            else if (digits.StartsWith('0') && digits.Length == 11) digits = digits[1..];
            if (digits.Length == 10 && digits[0] == '5' && !IsSyntheticSubscriber(digits[3..]))
                yield return $"mobile number {phone}";
        }

        foreach (Match match in MailAddress().Matches(line))
        {
            var domain = match.Groups["domain"].Value.ToLowerInvariant();
            if (!ExampleMailSuffixes.Any(suffix => domain == suffix.TrimStart('.') || domain.EndsWith(suffix.StartsWith('.') ? suffix : "." + suffix, StringComparison.Ordinal)))
                yield return $"e-mail {match.Value} (use an example.test address)";
        }
    }

    // TCKN: first digit not 0; d10 = (7 x (d1+d3+d5+d7+d9) - (d2+d4+d6+d8)) mod 10; d11 = (d1+...+d10) mod 10.
    private static bool IsValidIdentityNumber(string value)
    {
        var d = value.Select(c => c - '0').ToArray();
        if (d.Length != 11 || d[0] == 0) return false;
        var odd = d[0] + d[2] + d[4] + d[6] + d[8];
        var even = d[1] + d[3] + d[5] + d[7];
        return ((odd * 7 - even) % 10 + 10) % 10 == d[9] && d.Take(10).Sum() % 10 == d[10];
    }

    // The seven subscriber digits of a made-up number: one digit repeated (5550000000) or a counting run.
    private static bool IsSyntheticSubscriber(string subscriber) =>
        subscriber.Distinct().Count() == 1 || subscriber is "1234567" or "7654321";

    [GeneratedRegex(@"(?<![0-9A-Za-z])[1-9][0-9]{10}(?![0-9A-Za-z])")]
    private static partial Regex ElevenDigits();

    // The value of a phone-like JSON key or XML element.
    [GeneratedRegex(@"(?i)(?:""[a-z_]*(?:phone|gsm|mobile|telefon|tel)[a-z_]*""\s*:\s*""|<(?:[a-z]+:)?[a-z_]*(?:phone|gsm|mobile|telefon)[a-z_]*>)(?<value>[^""<]*)")]
    private static partial Regex PhoneField();

    // A Turkish mobile number written with its country or trunk prefix anywhere in the text.
    [GeneratedRegex(@"(?<![0-9A-Za-z])(?:\+90|0090|0)[\s-]?5[0-9]{2}[\s-]?[0-9]{3}[\s-]?[0-9]{2}[\s-]?[0-9]{2}(?![0-9A-Za-z])")]
    private static partial Regex PrefixedMobile();

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@(?<domain>[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,})")]
    private static partial Regex MailAddress();
}
