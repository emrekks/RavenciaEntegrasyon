using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Audit of 14 Nov 2026: two reset requests at once queued two resets that kept each other (and every job of the tenant)
// from being leased for ever; a reset's notification purge took the pending webhook retries with it.
public sealed partial class PostgreSqlConnectionFullResetTests
{
    [PostgreSqlFact]
    public async Task PanelFactoryReset_TwoRequestsAtOnceQueueOneReset()
    {
        var a = NewSeed("pf-twice");
        try
        {
            await SeedAsync(a);
            await using var first = fixture.CreateContext();
            await using var second = fixture.CreateContext();
            var views = await Task.WhenAll(
                NewPanelService(first).RequestAsync(a.Tenant.Id, Guid.CreateVersion7(), null, new PanelFactoryResetCommand("PANELİ SIFIRLA"), "corr-1", CancellationToken.None),
                NewPanelService(second).RequestAsync(a.Tenant.Id, Guid.CreateVersion7(), null, new PanelFactoryResetCommand("PANELİ SIFIRLA"), "corr-2", CancellationToken.None));
            Assert.All(views, view => Assert.True(view.Succeeded, view.Error?.Message));
            Assert.Equal(views[0].Value!.Job!.JobId, views[1].Value!.Job!.JobId);
            await using var verification = fixture.CreateContext();
            Assert.Equal(1, await verification.IntegrationJobs.CountAsync(x => x.TenantId == a.Tenant.Id && x.JobType == PanelJobTypes.FactoryReset));
        }
        finally
        {
            await DeleteTenantAsync(a.Tenant.Id);
        }
    }

    [PostgreSqlFact]
    public async Task PanelFactoryReset_OfTwoOpenResetsTheOldestIsLeased()
    {
        var a = NewSeed("pf-two-open");
        try
        {
            await SeedAsync(a);
            IntegrationJob older, newer;
            await using (var db = fixture.CreateContext())
            {
                older = Job(a.Tenant.Id, a.Other.Id, PanelJobTypes.FactoryReset, JobStatus.Pending);
                newer = Job(a.Tenant.Id, a.Other.Id, PanelJobTypes.FactoryReset, JobStatus.Pending);
                older.ConnectionId = null;
                newer.ConnectionId = null;
                older.CreatedAt = fixture.Now.AddMinutes(-2);
                newer.CreatedAt = fixture.Now.AddMinutes(-1);
                db.IntegrationJobs.AddRange(older, newer);
                await db.SaveChangesAsync();
            }
            await using (var db = fixture.CreateContext())
            {
                var leases = new JobLeaseService(db, fixture.TokenHasher, fixture.TimeProvider);
                Assert.Null(await leases.TryLeaseAsync(TimeSpan.FromMinutes(5), null, null, CancellationToken.None, onlyJobId: newer.Id));
                Assert.NotNull(await leases.TryLeaseAsync(TimeSpan.FromMinutes(5), null, null, CancellationToken.None, onlyJobId: older.Id));
            }
        }
        finally
        {
            await DeleteTenantAsync(a.Tenant.Id);
        }
    }

    [PostgreSqlFact]
    public async Task PanelCategoryReset_Notifications_KeepsThePendingWebhookRetries()
    {
        var a = NewSeed("pf-notify");
        try
        {
            await SeedAsync(a);
            var pause = InstantNotificationService.PausePendingKey(a.Other.Id);
            var resume = InstantNotificationService.ResumePendingKey(a.Reset.Id);
            var resolvedPause = InstantNotificationService.PausePendingKey(Guid.CreateVersion7());
            await using (var db = fixture.CreateContext())
            {
                db.OperationalIssues.AddRange(Issue(a.Tenant.Id, pause), Issue(a.Tenant.Id, resume), Issue(a.Tenant.Id, "order-coverage:x:1:1"));
                var done = Issue(a.Tenant.Id, resolvedPause);
                done.Status = IssueStatus.Resolved;
                db.OperationalIssues.Add(done);
                await db.SaveChangesAsync();
            }
            Guid jobId;
            await using (var db = fixture.CreateContext())
            {
                var view = await NewPanelService(db).RequestAsync(a.Tenant.Id, Guid.CreateVersion7(), null, new PanelFactoryResetCommand("Verileri sil", ["NOTIFICATIONS"]), "corr", CancellationToken.None);
                Assert.True(view.Succeeded, view.Error?.Message);
                jobId = view.Value!.Job!.JobId;
                await db.IntegrationJobs.Where(x => x.Id == jobId).ExecuteUpdateAsync(x => x.SetProperty(j => j.Status, JobStatus.Leased));
            }
            await using (var db = fixture.CreateContext())
            {
                var job = await db.IntegrationJobs.AsNoTracking().SingleAsync(x => x.Id == jobId);
                var result = await NewPanelService(db).RunAsync(a.Tenant.Id, jobId, job.PayloadJson, CancellationToken.None);
                Assert.True(result.Succeeded, result.ErrorSummary);
            }
            await using var verification = fixture.CreateContext();
            var left = await verification.OperationalIssues.AsNoTracking().Where(x => x.TenantId == a.Tenant.Id).Select(x => x.DedupeKey).OrderBy(x => x).ToListAsync();
            Assert.Equal(new[] { pause, resume }.Order(StringComparer.Ordinal), left.Order(StringComparer.Ordinal));
            // The panel does not count them as notifications still to remove.
            var after = (await NewPanelService(verification).GetAsync(a.Tenant.Id, CancellationToken.None)).Value!;
            Assert.Equal(0, after.Groups.Single(x => x.Key == "NOTIFICATIONS").Count);
        }
        finally
        {
            await DeleteTenantAsync(a.Tenant.Id);
        }
    }
}
