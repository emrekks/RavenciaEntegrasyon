using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Jobs.Handlers;

// A finance record sent without an id, a readable main amount or a date is never stored with a made-up 0 or "now"
// (it would put a payout on the wrong day or a wrong amount into the totals); the mapper leaves it out
// (SkippedFinanceRecords) and the job names it here in an operational issue, one per record and connection, while the
// other records of the page or job are stored. The issue is resolved when the same record is stored later (the
// marketplace completed it); one not seen again is resolved by the retention after a month.
internal static class FinanceRecordIssues
{
    internal const string Code = "FINANCE_RECORD_INCOMPLETE";
    // Beyond this many skipped records in a run the rest are counted in one issue: a changed answer format would
    // otherwise open an issue for every record.
    internal const int MaximumPerRun = 20;

    internal static string Prefix(Guid connectionId) => $"finance-record:{connectionId:N}:";

    internal static string MoreKey(Guid connectionId) => Prefix(connectionId) + "*";

    // Opens (or keeps open) an issue for each skipped record and resolves the issues of the records stored now.
    internal static async Task RecordAsync(AppDbContext db, Guid tenantId, Guid connectionId, string marketplace, IReadOnlyCollection<RemoteSkippedFinanceRecord> skipped,
        IEnumerable<string> storedKeys, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var prefix = Prefix(connectionId);
        var more = MoreKey(connectionId);
        var issues = await db.OperationalIssues
            .Where(x => x.TenantId == tenantId && x.Code == Code && x.DedupeKey.StartsWith(prefix))
            .ToListAsync(cancellationToken);
        foreach (var local in db.OperationalIssues.Local.Where(x => x.TenantId == tenantId && x.Code == Code && x.DedupeKey.StartsWith(prefix, StringComparison.Ordinal)))
            if (!issues.Contains(local)) issues.Add(local);
        var byKey = issues.GroupBy(x => x.DedupeKey, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);

        var distinct = skipped.DistinctBy(x => x.Key, StringComparer.Ordinal).ToList();
        foreach (var record in distinct.Take(MaximumPerRun))
            Upsert(prefix + record.Key, Short($"{marketplace} finans kaydı saklanmadı, {record.Description}. Kayıt hakediş ve kâr toplamlarına girmedi; {marketplace} kaydı tamamladığında sonraki okumada kendiliğinden eklenir.", 1000));
        if (distinct.Count > MaximumPerRun)
            Upsert(more, $"{marketplace} finans kayıtlarından {distinct.Count - MaximumPerRun} tanesi daha kimliği, tutarı ya da tarihi okunamadığı için saklanmadı; ilk {MaximumPerRun} kayıt ayrı ayrı listelendi. Bu kayıtlar hakediş ve kâr toplamlarına girmedi.");
        else if (byKey.TryGetValue(more, out var moreIssue)) Resolve(moreIssue);

        var skippedKeys = distinct.Select(x => prefix + x.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var key in storedKeys.Where(key => !string.IsNullOrWhiteSpace(key)).Select(key => prefix + key.Trim()).Distinct(StringComparer.Ordinal))
            if (!skippedKeys.Contains(key) && byKey.TryGetValue(key, out var issue)) Resolve(issue);

        void Upsert(string key, string summary)
        {
            if (!byKey.TryGetValue(key, out var issue))
            {
                issue = new OperationalIssue { Id = Guid.CreateVersion7(), TenantId = tenantId, DedupeKey = key, Code = Code, Summary = summary, Status = IssueStatus.Open, FirstSeenAt = now, LastSeenAt = now, OccurrenceCount = 1 };
                db.OperationalIssues.Add(issue);
                byKey[key] = issue;
                return;
            }
            if (issue.Status == IssueStatus.Resolved) issue.OccurrenceCount++;
            issue.Status = IssueStatus.Open;
            issue.ResolvedAt = null;
            issue.Summary = summary;
            issue.LastSeenAt = now;
        }

        void Resolve(OperationalIssue issue)
        {
            if (issue.Status == IssueStatus.Resolved) return;
            issue.Status = IssueStatus.Resolved;
            issue.LastSeenAt = now;
            issue.ResolvedAt = now;
        }
    }

    private static string Short(string value, int length) => value.Length <= length ? value : value[..length];
}
