using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// KUYRUK-2 step 4 (16 Nov 2026): an effect check that read the marketplace before an accepted request was applied removed
// its record ("not applied"); the job's save of the accepted request then failed and the request went a second time. The
// record is now put back as APPLIED with the job's other changes in the same save, and the check's issue is resolved.
public sealed partial class PostgreSqlTenantIsolationTests
{
    [PostgreSqlFact]
    public async Task Kuyruk2_AnAcceptedWriteWhoseRecordWasRemovedMeanwhileIsSavedAsApplied()
    {
        var tenant = NewTenant("kuyruk2-accepted");
        var key = $"kuyruk2-{Guid.NewGuid():N}";
        await using (var seedDb = fixture.CreateContext())
        {
            seedDb.Tenants.Add(tenant);
            await seedDb.SaveChangesAsync();
        }
        try
        {
            await using var db = fixture.CreateContext();
            var guard = new ExternalEffectGuard(db, fixture.TimeProvider);
            var effect = guard.New(tenant.Id, MarketplaceJobTypes.ShipmentAction, key, action: "PICKING");
            var send = await guard.SendAsync(effect, async () =>
            {
                // The check runs while the request is out: it removes the record and opens its issue.
                await using var checkDb = fixture.CreateContext();
                await checkDb.ExternalEffectRecords.Where(x => x.Id == effect.Id).ExecuteDeleteAsync();
                checkDb.OperationalIssues.Add(new OperationalIssue { Id = Guid.CreateVersion7(), TenantId = tenant.Id, DedupeKey = $"external-effect:{effect.Id}", Code = "EXTERNAL_EFFECT_NOT_APPLIED", Summary = "x", Status = IssueStatus.Open, FirstSeenAt = fixture.Now, LastSeenAt = fixture.Now, OccurrenceCount = 1 });
                await checkDb.SaveChangesAsync();
                return AdapterResult<bool>.Success(true);
            }, CancellationToken.None);
            Assert.True(send.Accepted);
            // A change of the same save that must not be lost.
            db.OperationalIssues.Add(new OperationalIssue { Id = Guid.CreateVersion7(), TenantId = tenant.Id, DedupeKey = $"kuyruk2-other:{key}", Code = "OTHER", Summary = "y", Status = IssueStatus.Open, FirstSeenAt = fixture.Now, LastSeenAt = fixture.Now, OccurrenceCount = 1 });

            await guard.SaveAcceptedAsync([effect], CancellationToken.None);

            await using var check = fixture.CreateContext();
            var saved = await check.ExternalEffectRecords.AsNoTracking().SingleAsync(x => x.Id == effect.Id);
            Assert.Equal(ExternalEffectReconcilePolicy.AppliedOutcome, saved.Outcome);
            Assert.NotNull(saved.CompletedAt);
            Assert.Equal(IssueStatus.Resolved, (await check.OperationalIssues.AsNoTracking().SingleAsync(x => x.TenantId == tenant.Id && x.DedupeKey == $"external-effect:{effect.Id}")).Status);
            Assert.True(await check.OperationalIssues.AnyAsync(x => x.TenantId == tenant.Id && x.DedupeKey == $"kuyruk2-other:{key}"));
        }
        finally
        {
            await using var cleanupDb = fixture.CreateContext();
            await cleanupDb.ExternalEffectRecords.Where(x => x.TenantId == tenant.Id).ExecuteDeleteAsync();
            await cleanupDb.OperationalIssues.Where(x => x.TenantId == tenant.Id).ExecuteDeleteAsync();
            await DeleteQuestionSyncTestTenantAsync(tenant.Id);
        }
    }
}
