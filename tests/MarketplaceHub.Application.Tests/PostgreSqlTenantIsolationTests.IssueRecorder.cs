using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// ISLEYICI-12 point 5 (16 Nov 2026): the shared IssueRecorder opens, reopens and resolves by key as the processor's and
// the handlers' copies did; an issue opened and resolved before one save is resolved, and each caller's stamps stay.
public sealed partial class PostgreSqlTenantIsolationTests
{
    [PostgreSqlFact]
    public async Task Isleyici12_TheIssueRecorderOpensReopensAndResolvesByKey()
    {
        var tenant = NewTenant("isleyici12-issues");
        await using (var seedDb = fixture.CreateContext())
        {
            seedDb.Tenants.Add(tenant);
            await seedDb.SaveChangesAsync();
        }
        var now = fixture.Now;
        try
        {
            await using (var db = fixture.CreateContext())
            {
                var recorder = new IssueRecorder(db);
                await recorder.RecordAsync(tenant.Id, "k1", "C1", "first", now, CancellationToken.None);
                // Opened and resolved in one unit of work: resolved, not added twice.
                await recorder.ResolveAsync(tenant.Id, "k1", now, CancellationToken.None);
                await recorder.RecordAsync(tenant.Id, "k2", "C2", "second", now, CancellationToken.None);
                await db.SaveChangesAsync();
            }
            await using (var db = fixture.CreateContext())
            {
                var recorder = new IssueRecorder(db);
                await recorder.RecordAsync(tenant.Id, "k2", "C3", "again", now.AddMinutes(1), CancellationToken.None);
                await recorder.ResolveAsync(tenant.Id, "k1", now.AddMinutes(1), CancellationToken.None, IssueResolveStamps.ResolvedAt | IssueResolveStamps.LastSeenAt);
                await db.SaveChangesAsync();
            }
            await using var check = fixture.CreateContext();
            var issues = await check.OperationalIssues.AsNoTracking().Where(x => x.TenantId == tenant.Id).OrderBy(x => x.DedupeKey).ToListAsync();
            Assert.Equal(2, issues.Count);
            Assert.Equal(IssueStatus.Resolved, issues[0].Status);
            Assert.True((issues[0].ResolvedAt!.Value - now).Duration() < TimeSpan.FromMilliseconds(1));
            // Already resolved: the second resolve leaves it as it was.
            Assert.True((issues[0].LastSeenAt - now).Duration() < TimeSpan.FromMilliseconds(1));
            Assert.Equal((IssueStatus.Open, "C3", "again", 2), (issues[1].Status, issues[1].Code, issues[1].Summary, issues[1].OccurrenceCount));
        }
        finally
        {
            await using var cleanupDb = fixture.CreateContext();
            await cleanupDb.OperationalIssues.Where(x => x.TenantId == tenant.Id).ExecuteDeleteAsync();
            await DeleteQuestionSyncTestTenantAsync(tenant.Id);
        }
    }
}
