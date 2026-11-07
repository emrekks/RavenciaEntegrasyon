using System.Text.RegularExpressions;
using MarketplaceHub.Application;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Owner, 7 Nov 2026: a job stopped with "Ürün güncelleme listing state kayıtları eksik." Every job result the owner can
// read must name its cause in plain Turkish; a result written in developer wording needs a JobErrorMessages entry.
public sealed class JobErrorMessagesTests
{
    // Developer words the owner cannot act on. A summary with one of them (or one written in English) needs a plain entry.
    private static readonly Regex Jargon = new(
        @"listing|payload|composer|dedup|\bnull\b|snapshot|\bbatch\b|\blease\b|contract|endpoint|cursor|imleci|merchantSku|hbSku|contentId|variantId|trackingId|\bjob\b|canary|capability|fixture|\bpoll\b|projection|sözleşme|\bStage\b|uzak referans|veritabanı|altyapı|Unsupported",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex JobResult = new(
        @"JobExecutionResult\.(?:Blocked|ManualReview|Dead|Retry|Deferred|SuccessWithNote)\(\s*""(?<code>[A-Z0-9_]+)""\s*,\s*\$?@?""(?<summary>(?:[^""\\]|\\.)*)""",
        RegexOptions.CultureInvariant);

    private static readonly Regex Turkish = new("[çğışöüÇĞİŞÖÜ]", RegexOptions.CultureInvariant);

    [Fact]
    public void Every_developer_worded_job_result_has_a_plain_message()
    {
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        var scanned = 0;
        foreach (var file in Directory.EnumerateFiles(Path.Combine(RepositoryRoot(), "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) continue;
            foreach (Match match in JobResult.Matches(File.ReadAllText(file)))
            {
                scanned++;
                var code = match.Groups["code"].Value;
                var summary = match.Groups["summary"].Value;
                // An interpolated expression with its own quotes ({(a ? "x" : "y")} …) cannot be read here.
                if (summary.StartsWith("{(", StringComparison.Ordinal)) continue;
                var developerWorded = Jargon.IsMatch(summary) || !Turkish.IsMatch(summary);
                if (developerWorded && !JobErrorMessages.Messages.ContainsKey(code)) missing.Add($"{code}: {summary}");
            }
        }
        Assert.True(scanned > 100, $"Only {scanned} job results found; the scan no longer reads the source.");
        Assert.True(missing.Count == 0, "Add a plain JobErrorMessages entry for:\n" + string.Join("\n", missing));
    }

    [Fact]
    public void Every_message_is_plain_turkish_and_says_what_happened()
    {
        foreach (var (code, message) in JobErrorMessages.Messages)
        {
            Assert.False(string.IsNullOrWhiteSpace(message), code);
            Assert.False(Jargon.IsMatch(message), $"{code}: {message}");
            Assert.Matches(Turkish, message);
            Assert.EndsWith(".", message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_stored_summary_reads_as_its_plain_sentence_and_unknown_codes_keep_theirs()
    {
        Assert.Equal(
            "Bu ürünün pazaryeri kaydı panelde eksik; güncelleme pazaryerine gönderilmedi. Ürün senkronizasyonu çalışınca düzelecek, sonra Platformlarda Güncelle'ye yeniden basın.",
            JobErrorMessages.Display("PRODUCT_UPDATE_STATE_INCOMPLETE", "Ürün güncelleme listing state kayıtları eksik."));
        Assert.Equal("Trendyol ürünü reddetti: desi eksik.", JobErrorMessages.Display("TRENDYOL_REJECTED", "Trendyol ürünü reddetti: desi eksik."));
        Assert.Null(JobErrorMessages.Display(null, null));
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "MarketplaceHub.sln"))) return directory.FullName;
        throw new InvalidOperationException("MarketplaceHub.sln not found above the test output.");
    }
}
