using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

// An operational issue that has not been seen for a month no longer describes the live system (the
// product mapping it named was fixed, the order it named was delivered), yet it stayed open and buried
// the live ones. Such issues are resolved; the next occurrence reopens the same record.
// A resolved issue is deleted once it was resolved, and last seen, longer ago than the retention: many
// issue keys carry an event id or a timestamp, so they would otherwise pile up forever. An issue the stale
// pass resolves today is therefore kept for the retention period instead of being deleted in the same pass.
public sealed class OperationalIssueRetentionService(AppDbContext db, TimeProvider timeProvider)
{
    public static readonly TimeSpan DefaultStaleAfter = TimeSpan.FromDays(30);

    public async Task<int> ResolveStaleAsync(TimeSpan staleAfter, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var cutoff = now - staleAfter;
        return await db.OperationalIssues
            .Where(issue => issue.Status == IssueStatus.Open && issue.LastSeenAt < cutoff)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(issue => issue.Status, IssueStatus.Resolved)
                .SetProperty(issue => issue.ResolvedAt, now), cancellationToken);
    }

    public async Task<int> DeleteResolvedAsync(TimeSpan retention, CancellationToken cancellationToken)
    {
        var cutoff = timeProvider.GetUtcNow() - retention;
        // Issues resolved before ResolvedAt existed (or by a path that does not set it) fall back to LastSeenAt.
        return await db.OperationalIssues
            .Where(issue => issue.Status == IssueStatus.Resolved && issue.LastSeenAt < cutoff
                && (issue.ResolvedAt ?? issue.LastSeenAt) < cutoff)
            .ExecuteDeleteAsync(cancellationToken);
    }
}
