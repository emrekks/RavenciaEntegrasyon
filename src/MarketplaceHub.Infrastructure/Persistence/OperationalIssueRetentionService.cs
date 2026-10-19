using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

// An operational issue that has not been seen for a month no longer describes the live system (the
// product mapping it named was fixed, the order it named was delivered), yet it stayed open and buried
// the live ones. Such issues are resolved; the next occurrence reopens the same record.
public sealed class OperationalIssueRetentionService(AppDbContext db, TimeProvider timeProvider)
{
    public static readonly TimeSpan DefaultStaleAfter = TimeSpan.FromDays(30);

    public async Task<int> ResolveStaleAsync(TimeSpan staleAfter, CancellationToken cancellationToken)
    {
        var cutoff = timeProvider.GetUtcNow() - staleAfter;
        return await db.OperationalIssues
            .Where(issue => issue.Status == IssueStatus.Open && issue.LastSeenAt < cutoff)
            .ExecuteUpdateAsync(setters => setters.SetProperty(issue => issue.Status, IssueStatus.Resolved), cancellationToken);
    }
}
