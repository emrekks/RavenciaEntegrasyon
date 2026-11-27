using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// İşlem Takibi › Kuyruk ve Kota: the window behind a job type's Hata number lists exactly the attempts the number counts
// (same window, same platforms, waiting checks, successes, unfinished and older attempts left out), of the tenant only.
public sealed partial class PostgreSqlTenantIsolationTests
{
    private const string FailureJobType = "PTTAVM_ORDER_SYNC";

    [PostgreSqlFact]
    public async Task JobTypeFailures_TotalEqualsTheMetricsFailedCount_GroupedByReason_PagedNewestFirst_OfTheTenantOnly()
    {
        var tenant = NewTenant("job-failures");
        var other = NewTenant("job-failures-other");
        var connection = NewQuestionConnection(tenant);
        connection.PlatformCode = "PTTAVM";
        connection.DisplayName = "Deneme Mağazası";
        var now = DateTimeOffset.UtcNow;
        IntegrationJob Job(Guid tenantId, JobStatus status, string type = FailureJobType, Guid? connectionId = null)
        {
            var job = NewJob(tenantId);
            job.JobType = type;
            job.Status = status;
            job.ConnectionId = connectionId;
            return job;
        }
        var recovered = Job(tenant.Id, JobStatus.Succeeded, connectionId: connection.Id);
        var dead = Job(tenant.Id, JobStatus.Dead, connectionId: connection.Id);
        var retrying = Job(tenant.Id, JobStatus.RetryScheduled, connectionId: connection.Id);
        var otherType = Job(tenant.Id, JobStatus.Dead, "TRENDYOL_ORDER_SYNC", connection.Id);
        var otherTenant = Job(other.Id, JobStatus.Dead);
        var attempts = new List<JobAttempt>();
        void Attempt(IntegrationJob job, int number, double minutesAgo, bool succeeded = false, string? code = "PTTAVM_NETWORK_ERROR", string? summary = "PttAVM bağlantısı geçici olarak kurulamadı.", bool finished = true) =>
            attempts.Add(new JobAttempt
            {
                Id = Guid.CreateVersion7(),
                TenantId = job.TenantId,
                JobId = job.Id,
                AttemptNumber = number,
                StartedAt = now.AddMinutes(-minutesAgo),
                CompletedAt = finished ? now.AddMinutes(-minutesAgo + 0.5) : null,
                Succeeded = succeeded,
                ErrorCode = succeeded ? null : code,
                ErrorSummary = succeeded ? null : summary
            });
        // Counted: 3 network errors, 1 rejected request, 1 failure without a code.
        Attempt(recovered, 1, 50);
        Attempt(recovered, 2, 40, succeeded: true);
        Attempt(dead, 1, 30);
        Attempt(dead, 2, 20, code: "PTTAVM_REQUEST_REJECTED", summary: "PttAVM isteği reddetti.");
        Attempt(retrying, 1, 10);
        Attempt(retrying, 2, 5, code: null, summary: null);
        // Not counted: a waiting check, an unfinished attempt, one older than the window, another type, another tenant.
        Attempt(retrying, 3, 4, code: JobWaitingPolicy.Codes.First(), summary: "Bekliyor");
        Attempt(retrying, 4, 1, finished: false);
        Attempt(dead, 3, 60 * 30);
        Attempt(otherType, 1, 15);
        Attempt(otherTenant, 1, 15);

        await using (var setup = fixture.CreateContext())
        {
            setup.Tenants.AddRange(tenant, other);
            setup.PlatformConnections.Add(connection);
            setup.IntegrationJobs.AddRange(recovered, dead, retrying, otherType, otherTenant);
            setup.JobAttempts.AddRange(attempts);
            await setup.SaveChangesAsync();
        }

        try
        {
            await using var db = fixture.CreateContext();
            var service = new JobOperationsService(db, fixture.TimeProvider);
            var window = TimeSpan.FromHours(24);
            var metric = Assert.Single(await service.MetricsAsync(tenant.Id, window, null, CancellationToken.None), row => row.JobType == FailureJobType);
            var failures = await service.FailuresAsync(tenant.Id, FailureJobType, window, null, 1, 20, CancellationToken.None);

            Assert.Equal(5, metric.Failed);
            Assert.Equal(metric.Failed, failures.TotalCount);
            Assert.Equal(3, failures.GroupCount);
            var network = failures.Groups[0];
            Assert.Equal(("PTTAVM_NETWORK_ERROR", 3, true), (network.ErrorCode, network.Count, network.Transient));
            Assert.Equal("PttAVM bağlantısı geçici olarak kurulamadı.", network.Reason);
            Assert.Equal(now.AddMinutes(-50), network.FirstSeen, TimeSpan.FromMilliseconds(1));
            Assert.Equal(now.AddMinutes(-10), network.LastSeen, TimeSpan.FromMilliseconds(1));
            Assert.False(failures.Groups.Single(group => group.ErrorCode == "PTTAVM_REQUEST_REJECTED").Transient);
            Assert.Contains(failures.Groups, group => group.ErrorCode is null && group.Reason is null && group.Count == 1);

            Assert.Equal(5, failures.Items.Count);
            Assert.True(failures.Items.Zip(failures.Items.Skip(1)).All(pair => pair.First.AttemptedAt >= pair.Second.AttemptedAt));
            var newest = failures.Items[0];
            Assert.Equal((retrying.Id, 2, "RETRY_SCHEDULED", "Deneme Mağazası", "PTTAVM"), (newest.JobId, newest.AttemptNumber, newest.JobStatus, newest.StoreName, newest.PlatformCode));
            Assert.Equal("SUCCEEDED", failures.Items.Single(item => item.JobId == recovered.Id).JobStatus);
            Assert.All(failures.Items.Where(item => item.JobId == dead.Id), item => Assert.Equal("DEAD", item.JobStatus));
            Assert.DoesNotContain(failures.Items, item => item.JobId == otherTenant.Id || item.JobId == otherType.Id);

            // Paging: two per page, the third page holds the oldest; the groups and the total stay whole.
            var third = await service.FailuresAsync(tenant.Id, FailureJobType, window, null, 3, 2, CancellationToken.None);
            Assert.Equal((5, 3, 2), (third.TotalCount, third.Page, third.PageSize));
            Assert.Equal(recovered.Id, Assert.Single(third.Items).JobId);

            // The platform filter is that of the metrics: another platform has none, this one has them all.
            var trendyolMetric = (await service.MetricsAsync(tenant.Id, window, ["TRENDYOL"], CancellationToken.None)).SingleOrDefault(row => row.JobType == FailureJobType);
            Assert.Equal(trendyolMetric?.Failed ?? 0, (await service.FailuresAsync(tenant.Id, FailureJobType, window, ["TRENDYOL"], 1, 20, CancellationToken.None)).TotalCount);
            Assert.Equal(5, (await service.FailuresAsync(tenant.Id, FailureJobType, window, ["pttavm"], 1, 20, CancellationToken.None)).TotalCount);

            // The other tenant sees its own attempt only, never this tenant's.
            var theirs = await service.FailuresAsync(other.Id, FailureJobType, window, null, 1, 20, CancellationToken.None);
            Assert.Equal(1, theirs.TotalCount);
            Assert.Equal(otherTenant.Id, Assert.Single(theirs.Items).JobId);
            Assert.Null(theirs.Items[0].StoreName);
        }
        finally
        {
            var jobIds = new[] { recovered.Id, dead.Id, retrying.Id, otherType.Id, otherTenant.Id };
            await using var cleanup = fixture.CreateContext();
            await cleanup.JobAttempts.Where(x => jobIds.Contains(x.JobId)).ExecuteDeleteAsync();
            await cleanup.IntegrationJobs.Where(x => jobIds.Contains(x.Id)).ExecuteDeleteAsync();
            await cleanup.PlatformConnections.Where(x => x.Id == connection.Id).ExecuteDeleteAsync();
            await cleanup.IntegrationOutboxEvents.Where(x => x.TenantId == tenant.Id || x.TenantId == other.Id).ExecuteDeleteAsync();
            await cleanup.Tenants.Where(x => x.Id == tenant.Id || x.Id == other.Id).ExecuteDeleteAsync();
        }
    }
}
