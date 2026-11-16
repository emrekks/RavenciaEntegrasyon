using System.Text.RegularExpressions;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// FATURA-FINANS-5 (owner, 16 Nov 2026): reports, the overview, profitability, payouts, ad reports and invoices round
// money half a kuruş away from zero, through MoneyRounding; figures that are not money keep their rule through
// MeasureRounding. A bare Math.Round(x, 2) rounds to even (banker's), so in those areas every rounding names its
// rule, and no file keeps a Round/Money/Kurus helper of its own.
public sealed partial class MoneyRoundingArchitectureTests
{
    // The areas the rule covers, by file name.
    [GeneratedRegex(@"(Report|Payout|Dashboard|Profit|Invoice|Financ|Settlement|Commission|Hakedis|ReturnMarketplaceAmounts|OrderLineReturnPolicy|MoneyRounding)", RegexOptions.CultureInvariant)]
    private static partial Regex Scope();

    [GeneratedRegex(@"(?<![\w.])(?:System\.)?(?:Math|decimal|Decimal)\.Round\s*\(", RegexOptions.CultureInvariant)]
    private static partial Regex RoundCall();

    // A method or local function of its own that rounds money: decimal Round(…), decimal? Money(…), long Kurus(…).
    [GeneratedRegex(@"\b(?:decimal|double|float|long|int)\??\s+(?<name>Round\w*|Money|Kurus\w*)\s*\(", RegexOptions.CultureInvariant)]
    private static partial Regex LocalHelper();

    // Each with its reason; nothing else may round without a rule or keep its own helper.
    private static readonly Dictionary<string, string> AllowedHelpers = new(StringComparer.Ordinal)
    {
        ["src/MarketplaceHub.Domain/MoneyRounding.cs:Round"] = "The shared helpers themselves (MoneyRounding, MeasureRounding).",
        ["src/MarketplaceHub.Infrastructure/Adapters/TrendyolEFaturam/Contracts/TrendyolEFaturamInvoicePayload.cs:Money"] =
            "The e-invoice adapter's own copy, already away from zero; adapters belong to another session (FATURA-FINANS-5 scope).",
        ["src/MarketplaceHub.Infrastructure/Adapters/TrendyolEFaturam/Contracts/TrendyolEFaturamInvoicePayload.cs:Kurus"] =
            "Whole kuruş as a long from the adapter's away-from-zero Money; same adapter, same reason.",
        ["src/MarketplaceHub.Infrastructure/Adapters/TrendyolEFaturam/Contracts/TrendyolEFaturamInvoicePayload.cs:KurusDecimal"] =
            "The provider wants kuruş to four places (amount × 100), away from zero; not a 2-place money rounding.",
    };

    [Fact]
    public void Every_rounding_in_reports_payouts_finance_and_invoices_names_its_midpoint_rule()
    {
        var bare = new List<string>();
        foreach (var (file, lines) in ScopedSources())
            foreach (var (number, line) in lines)
                foreach (Match match in RoundCall().Matches(line))
                {
                    var call = CallText(line, match.Index + match.Length - 1);
                    if (!call.Contains("MidpointRounding.", StringComparison.Ordinal)) bare.Add($"{file}:{number}: {line.Trim()}");
                }
        Assert.True(bare.Count == 0, "Use MoneyRounding.Round (money) or MeasureRounding.Round (not money), or name the MidpointRounding:\n" + string.Join("\n", bare));
    }

    [Fact]
    public void No_file_in_those_areas_keeps_a_rounding_helper_of_its_own()
    {
        var helpers = new List<string>();
        foreach (var (file, lines) in ScopedSources())
            foreach (var (number, line) in lines)
                foreach (Match match in LocalHelper().Matches(line))
                    if (!AllowedHelpers.ContainsKey($"{file}:{match.Groups["name"].Value}")) helpers.Add($"{file}:{number}: {line.Trim()}");
        Assert.True(helpers.Count == 0, "Round through MoneyRounding / MeasureRounding instead of a helper of the file's own:\n" + string.Join("\n", helpers));
    }

    [Fact]
    public void The_scan_covers_the_report_payout_ad_and_invoice_files()
    {
        var files = ScopedSources().Select(x => x.File).ToHashSet(StringComparer.Ordinal);
        foreach (var expected in new[]
        {
            "src/MarketplaceHub.Infrastructure/Persistence/ReportBuilder.cs", "src/MarketplaceHub.Infrastructure/Persistence/ReportCosts.cs",
            "src/MarketplaceHub.Infrastructure/Persistence/PayoutsService.cs", "src/MarketplaceHub.Infrastructure/Persistence/AdReportService.cs",
            "src/MarketplaceHub.Infrastructure/Persistence/UnitProfit.cs", "src/MarketplaceHub.Infrastructure/Persistence/DashboardReadService.cs",
            "src/MarketplaceHub.Domain/InvoiceAmounts.cs"
        })
            Assert.Contains(expected, files);
        // Every allow-listed helper still exists, so the list does not outlive what it excuses.
        foreach (var key in AllowedHelpers.Keys)
            Assert.Contains(key[..key.LastIndexOf(':')], files);
    }

    [Theory]
    [InlineData("var x = Math.Round(a * b, 2);", true)]
    [InlineData("var x = decimal.Round(Sum(a, b), 4);", true)]
    [InlineData("var x = Math.Round(Sum(a, b), 2, MidpointRounding.AwayFromZero);", false)]
    [InlineData("var x = MoneyRounding.Round(a);", false)]
    public void The_scanner_finds_a_bare_rounding(string line, bool bare)
    {
        var match = RoundCall().Match(line);
        Assert.Equal(bare, match.Success && !CallText(line, match.Index + match.Length - 1).Contains("MidpointRounding.", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("    private static decimal Round(decimal value) => Math.Round(value, 2);", true)]
    [InlineData("    static decimal? Money(decimal? value) => value;", true)]
    [InlineData("    private static long KurusOf(decimal value) => 0;", true)]
    [InlineData("    private static decimal RealAmount(Fact order) => 0m;", false)]
    public void The_scanner_finds_a_helper_of_its_own(string line, bool helper) => Assert.Equal(helper, LocalHelper().IsMatch(line));

    // The text of a call from its opening parenthesis to the one that closes it (on the same line).
    private static string CallText(string line, int open)
    {
        var depth = 0;
        for (var index = open; index < line.Length; index++)
        {
            if (line[index] == '(') depth++;
            else if (line[index] == ')' && --depth == 0) return line[open..(index + 1)];
        }
        return line[open..];
    }

    private static IEnumerable<(string File, List<(int Number, string Line)> Lines)> ScopedSources()
    {
        var root = TestRepository.Root();
        var src = Path.Combine(root, "src");
        var separator = Path.DirectorySeparatorChar;
        return Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{separator}obj{separator}", StringComparison.Ordinal) && !file.Contains($"{separator}bin{separator}", StringComparison.Ordinal)
                && !file.Contains($"{separator}Migrations{separator}", StringComparison.Ordinal) && !file.Contains($"{separator}MarketplaceHub.Web{separator}", StringComparison.Ordinal))
            .Where(file => Scope().IsMatch(Path.GetFileName(file)))
            .Order(StringComparer.Ordinal)
            .Select(file => (Path.GetRelativePath(root, file).Replace('\\', '/'), File.ReadAllLines(file).Select((line, index) => (index + 1, Code(line))).ToList()));
    }

    // A line without its comment (a comment may quote a rounding).
    private static string Code(string line)
    {
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith("///", StringComparison.Ordinal)) return "";
        var comment = line.IndexOf(" // ", StringComparison.Ordinal);
        return comment >= 0 ? line[..comment] : line;
    }
}
