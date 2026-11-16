using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

// ISLEYICI-9 stage 1 / ISLEYICI-12 point 5 (16 Nov 2026): the one place an operational issue is opened, reopened or
// resolved by its key. The job processor, the finance handlers and the invoice adjustment tracking each kept a copy of
// this code. It works on the caller's AppDbContext (the same unit of work) and never saves: the change goes with the
// caller's next save, as before. An issue this unit of work already holds is found first, so one opened and resolved
// before the next save is resolved, not added twice (ISLEYICI-8).
internal sealed class IssueRecorder(AppDbContext db)
{
    // Opens the issue under its key, or reopens it with the new code and summary and counts the occurrence.
    public async Task RecordAsync(Guid tenantId, string key, string code, string summary, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var issue = await FindAsync(tenantId, key, cancellationToken);
        if (issue is null)
        {
            db.OperationalIssues.Add(new OperationalIssue { Id = Guid.CreateVersion7(), TenantId = tenantId, DedupeKey = key, Code = code, Summary = summary, Status = IssueStatus.Open, FirstSeenAt = now, LastSeenAt = now, OccurrenceCount = 1 });
            return;
        }
        issue.Status = IssueStatus.Open;
        issue.Code = code;
        issue.Summary = summary;
        issue.LastSeenAt = now;
        issue.OccurrenceCount++;
    }

    // Resolves the issue under its key when it is not resolved yet. The stamps are what each caller wrote before: the
    // processor stamps ResolvedAt, the finance handlers ResolvedAt and LastSeenAt, the invoice adjustment tracking
    // LastSeenAt only.
    public async Task ResolveAsync(Guid tenantId, string key, DateTimeOffset now, CancellationToken cancellationToken, IssueResolveStamps stamps = IssueResolveStamps.ResolvedAt)
    {
        var issue = await FindAsync(tenantId, key, cancellationToken);
        if (issue is null || issue.Status == IssueStatus.Resolved) return;
        issue.Status = IssueStatus.Resolved;
        if (stamps.HasFlag(IssueResolveStamps.ResolvedAt)) issue.ResolvedAt = now;
        if (stamps.HasFlag(IssueResolveStamps.LastSeenAt)) issue.LastSeenAt = now;
    }

    private async Task<OperationalIssue?> FindAsync(Guid tenantId, string key, CancellationToken cancellationToken) =>
        db.OperationalIssues.Local.FirstOrDefault(x => x.TenantId == tenantId && x.DedupeKey == key)
        ?? await db.OperationalIssues.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.DedupeKey == key, cancellationToken);
}

[Flags]
internal enum IssueResolveStamps
{
    None = 0,
    ResolvedAt = 1,
    LastSeenAt = 2
}
