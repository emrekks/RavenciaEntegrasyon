using System.Text.RegularExpressions;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// FATURA-FINANS-5 (owner, 16 Nov 2026): reports, the overview, profitability, payouts, ad reports and invoices round
// money half a kuruş away from zero, through MoneyRounding; figures that are not money keep their rule through
// MeasureRounding, and percentages and rates round half up through PercentRounding. A bare Math.Round(x, 2) rounds to even (banker's), so in those areas every rounding names its
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
        ["src/MarketplaceHub.Domain/MoneyRounding.cs:Round"] = "The shared helpers themselves (MoneyRounding, PercentRounding, MeasureRounding).",
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
        Assert.True(bare.Count == 0, "Use MoneyRounding.Round (money), PercentRounding (percentages, rates) or MeasureRounding.Round (other measures), or name the MidpointRounding:\n" + string.Join("\n", bare));
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

    [GeneratedRegex(@"\bMeasureRounding\.Round\s*\(", RegexOptions.CultureInvariant)]
    private static partial Regex MeasureCall();

    // Every use of MeasureRounding (to even) in those areas, by file, each a figure that is neither money nor a
    // percentage (owner, 16 Nov 2026: percentages and rates round half up through PercentRounding). A new use fails until
    // it is classified here, so a new percentage cannot take the to-even rule by accident.
    private static readonly Dictionary<string, (int Count, string What)> MeasureUses = new(StringComparer.Ordinal)
    {
        ["src/MarketplaceHub.Infrastructure/Persistence/ReportBuilder.cs"] =
            (5, "average days to ship and to deliver per carrier (1), orders per customer (2), units sold per day (2), days of stock cover (1)"),
        ["src/MarketplaceHub.Infrastructure/Persistence/ReportStock.cs"] = (3, "days of stock cover (1, twice) and units sold per day (2)"),
        ["src/MarketplaceHub.Infrastructure/Persistence/ReportQuestionTimes.cs"] = (2, "average and median answer hours (1)"),
    };

    [Fact]
    public void Only_classified_measures_round_to_even_and_no_percentage_does()
    {
        var found = new Dictionary<string, int>(StringComparer.Ordinal);
        var wrong = new List<string>();
        foreach (var (file, lines) in ScopedSources())
            foreach (var (number, line) in lines)
            {
                foreach (Match match in MeasureCall().Matches(line))
                {
                    found[file] = found.GetValueOrDefault(file) + 1;
                    var call = CallText(line, match.Index + match.Length - 1);
                    // Four places is the rate format (0.1234 = 12.34 %), × 100 a percentage: both belong to PercentRounding.
                    if (PercentageCall(call)) wrong.Add($"{file}:{number}: {line.Trim()}");
                }
                if (!file.EndsWith("/MoneyRounding.cs", StringComparison.Ordinal) && line.Contains("MidpointRounding.ToEven", StringComparison.Ordinal))
                    wrong.Add($"{file}:{number}: {line.Trim()}");
                // A percentage rounded by hand (× 100 inside a Math/decimal.Round) goes through PercentRounding.Percent; a
                // share of money (amount × rate / 100) is money and stays on its rule.
                if (!file.Contains("/Adapters/", StringComparison.Ordinal))
                    foreach (Match match in RoundCall().Matches(line))
                        if (TimesHundred().IsMatch(CallText(line, match.Index + match.Length - 1))) wrong.Add($"{file}:{number}: {line.Trim()}");
            }
        Assert.True(wrong.Count == 0, "A percentage or rate rounds through PercentRounding (half up):\n" + string.Join("\n", wrong));
        Assert.Equal(MeasureUses.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => (x.Key, x.Value.Count)),
            found.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => (x.Key, x.Value)));
    }

    [Theory]
    [InlineData("var x = MeasureRounding.Round(a / b, 4);", true)]
    [InlineData("var x = MeasureRounding.Round(a / b * 100m, 2);", true)]
    [InlineData("var x = MeasureRounding.Round(Sum(a, b), 1);", false)]
    public void The_scanner_finds_a_percentage_on_the_to_even_rule(string line, bool percentage)
    {
        var match = MeasureCall().Match(line);
        var call = CallText(line, match.Index + match.Length - 1);
        Assert.Equal(percentage, PercentageCall(call));
    }

    [GeneratedRegex(@"\*\s*100m\b", RegexOptions.CultureInvariant)]
    private static partial Regex TimesHundred();

    [GeneratedRegex(@",\s*4\s*\)$", RegexOptions.CultureInvariant)]
    private static partial Regex FourPlaces();

    // Four places is the rate format (0.1234 = 12.34 %), × 100 a percentage: both belong to PercentRounding.
    private static bool PercentageCall(string call) => TimesHundred().IsMatch(call) || FourPlaces().IsMatch(call);

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
