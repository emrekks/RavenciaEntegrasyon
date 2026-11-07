using System.Text.RegularExpressions;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Owner, 7 Nov 2026: every message the panel shows must say in plain Turkish what is wrong and what to do. A refusal
// (ServiceResult.Fail) and a note written on a job outside the job result (closing, hygiene, supersession) reach the
// screen as written, so their literals are held to the same words as JobErrorMessages.
public sealed class UserFacingTextTests
{
    private static readonly Regex Jargon = new(
        @"listing|payload|composer|dedup|\bnull\b|snapshot|\bbatch\b|\blease\b|contract|endpoint|cursor|imleci|merchantSku|hbSku|contentId|variantId|trackingId|\bjob\b|job'|canary|capability|fixture|\bpoll\b|projection|sözleşme|\bStage\b|\bSIT\b|If-Match|Idempotency|tenant|\benum\b|\bscope\b|\bmapping\b|\bworker\b|Unsupported",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex Fail = new(
        @"ServiceResult(?:<[^>]*>)?\.Fail\(\s*""(?<code>[A-Z0-9_]+)""\s*,\s*\$?@?""(?<text>(?:[^""\\]|\\.)*)""",
        RegexOptions.CultureInvariant);

    // A note set on a job by hand: job.LastErrorSummary = "…", SetProperty(x => x.LastErrorSummary, "…").
    private static readonly Regex SummaryWrite = new(
        @"LastErrorSummary\s*(?:\?\?=|=|,)\s*\$?@?""(?<text>(?:[^""\\]|\\.)*)""",
        RegexOptions.CultureInvariant);

    private static readonly Regex Hole = new(@"\{[^{}]*\}", RegexOptions.CultureInvariant);
    private static readonly Regex Turkish = new("[çğışöüÇĞİŞÖÜ]", RegexOptions.CultureInvariant);
    private static readonly Regex RawCode = new(@"\b[A-Z]{2,}_[A-Z0-9_]{3,}\b", RegexOptions.CultureInvariant);
    private static readonly Regex Guid36 = new(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", RegexOptions.CultureInvariant);

    // Refusals only an operator or the API itself meets (no screen sends such a request); each says why it stays.
    private static readonly HashSet<string> FailAllowlist = new(StringComparer.Ordinal)
    {
    };

    [Fact]
    public void Refusals_are_plain_turkish()
    {
        var offending = Scan(Fail, match => FailAllowlist.Contains(match.Groups["code"].Value) ? null : $"{match.Groups["code"].Value}: {match.Groups["text"].Value}");
        Assert.True(offending.Count == 0, "Rewrite these ServiceResult.Fail messages in plain Turkish:\n" + string.Join("\n", offending));
    }

    [Fact]
    public void Notes_written_on_jobs_are_plain_turkish()
    {
        var offending = Scan(SummaryWrite, match => match.Groups["text"].Value);
        Assert.True(offending.Count == 0, "Rewrite these job notes in plain Turkish:\n" + string.Join("\n", offending));
    }

    [Fact]
    public void Closing_notes_name_no_job_id_and_no_raw_code()
    {
        var notes = new List<string>
        {
            ProductWriteSupersession.Summary(DateTimeOffset.Parse("2026-11-07T00:48:00Z")),
            PublicationJobSupersession.Summary,
            ScheduledReadSupersession.Summary
        };
        foreach (var status in Enum.GetValues<InvoiceStatus>())
            foreach (var confirmed in new[] { false, true })
                foreach (var owner in new[] { false, true })
                    if (InvoiceDeliveryJobHygiene.CloseReason(status, confirmed, owner) is { } reason) notes.Add(reason.Summary);
        foreach (var note in notes)
        {
            Assert.DoesNotMatch(Guid36, note);
            Assert.DoesNotMatch(RawCode, note);
            Assert.DoesNotMatch(Jargon, note);
            Assert.Matches(Turkish, note);
        }
    }

    [Fact]
    public void A_closing_note_written_before_reads_without_the_job_id_and_the_raw_code()
    {
        var legacy = "Aynı ürünün bu bağlantıdaki sonraki yazma işi (01a1b37a-62a0-7d31-9107-bba39995557e, 7 Kasım 2026 03:48) başarılı oldu; bu eski hata kaydı kapatıldı (önceki hata: PRODUCT_UPDATE_STATE_INCOMPLETE).";
        Assert.Equal(
            "Aynı ürünün bu mağazadaki sonraki güncellemesi (7 Kasım 2026 03:48) başarılı oldu; bu eski hata kaydı kapatıldı.",
            JobErrorMessages.Display(ProductWriteSupersession.Code, legacy));
    }

    // Each literal, with its interpolation holes taken out, is checked; an interpolated expression with quotes of its
    // own ({(a ? "x" : "y")}) cannot be read by the pattern and is skipped.
    private static List<string> Scan(Regex pattern, Func<Match, string?> describe)
    {
        var offending = new List<string>();
        var scanned = 0;
        foreach (var file in Directory.EnumerateFiles(Path.Combine(RepositoryRoot(), "src"), "*.cs", SearchOption.AllDirectories))
        {
            var separator = Path.DirectorySeparatorChar;
            if (file.Contains($"{separator}Migrations{separator}", StringComparison.Ordinal)
                || file.Contains($"{separator}obj{separator}", StringComparison.Ordinal)
                || file.Contains($"{separator}bin{separator}", StringComparison.Ordinal)) continue;
            foreach (Match match in pattern.Matches(File.ReadAllText(file)))
            {
                scanned++;
                var text = match.Groups["text"].Value;
                if (text.StartsWith("{(", StringComparison.Ordinal) || text.StartsWith("{await", StringComparison.Ordinal)) continue;
                var words = Hole.Replace(text, " ");
                var plain = !Jargon.IsMatch(words) && (Turkish.IsMatch(words) || words.Trim().Length == 0);
                if (!plain && describe(match) is { } line) offending.Add($"{Path.GetFileName(file)}: {line}");
            }
        }
        Assert.True(scanned >= 5, $"Only {scanned} literals found; the scan no longer reads the source.");
        return offending;
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "MarketplaceHub.sln"))) return directory.FullName;
        throw new InvalidOperationException("MarketplaceHub.sln not found above the test output.");
    }
}
