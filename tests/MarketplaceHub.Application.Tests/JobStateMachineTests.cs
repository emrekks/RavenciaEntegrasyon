using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Jobs;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// KUYRUK-7: the transition table every job status write goes through, and what each operation writes.
public sealed class JobStateMachineTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

    // Every transition the code makes, by its writer. Anything else is refused.
    public static TheoryData<JobStatus, JobStatus, string> Allowed => new()
    {
        { JobStatus.Pending, JobStatus.Leased, "JobLeaseService.TryLeaseAsync" },
        { JobStatus.RetryScheduled, JobStatus.Leased, "JobLeaseService.TryLeaseAsync" },
        { JobStatus.Leased, JobStatus.RetryScheduled, "JobLeaseService.ReleaseAsync / CompleteAsync / ReapExpiredAsync" },
        { JobStatus.Leased, JobStatus.Succeeded, "JobLeaseService.CompleteAsync" },
        { JobStatus.Leased, JobStatus.Blocked, "JobLeaseService.CompleteAsync" },
        { JobStatus.Leased, JobStatus.ManualReview, "JobLeaseService.CompleteAsync" },
        { JobStatus.Leased, JobStatus.Dead, "JobLeaseService.CompleteAsync / ReapExpiredAsync" },
        { JobStatus.Leased, JobStatus.Cancelled, "JobOperationsService.CancelAsync, CatalogService product delete" },
        { JobStatus.Pending, JobStatus.Dead, "JobLeaseService.ReapExpiredAsync (attempts used up)" },
        { JobStatus.RetryScheduled, JobStatus.Dead, "JobLeaseService.ReapExpiredAsync (attempts used up)" },
        { JobStatus.Pending, JobStatus.Cancelled, "operator cancel, policy pass, connection settings, invoice paths, resets" },
        { JobStatus.RetryScheduled, JobStatus.Cancelled, "operator cancel, policy pass, connection settings, invoice paths, resets" },
        { JobStatus.Pending, JobStatus.Pending, "CatalogService publication status refresh" },
        { JobStatus.RetryScheduled, JobStatus.Pending, "CatalogService publication status refresh" },
        { JobStatus.Blocked, JobStatus.Pending, "ExternalEffectSweep revive" },
        { JobStatus.Blocked, JobStatus.RetryScheduled, "JobOperationsService.RetryAsync" },
        { JobStatus.ManualReview, JobStatus.RetryScheduled, "JobOperationsService.RetryAsync" },
        { JobStatus.Dead, JobStatus.RetryScheduled, "JobOperationsService.RetryAsync" },
        { JobStatus.Blocked, JobStatus.Cancelled, "operator cancel, supersession, invoice delivery hygiene" },
        { JobStatus.ManualReview, JobStatus.Cancelled, "operator cancel, supersession, invoice delivery hygiene" },
        { JobStatus.Dead, JobStatus.Cancelled, "invoice delivery hygiene, delivery settled elsewhere" },
    };

    [Theory]
    [MemberData(nameof(Allowed))]
    public void AKnownTransitionIsAllowed(JobStatus from, JobStatus to, string writer)
    {
        Assert.False(string.IsNullOrWhiteSpace(writer));
        var job = Job(from);
        JobStateMachine.MoveTo(job, to);
        Assert.Equal(to, job.Status);
    }

    [Fact]
    public void EveryOtherTransitionIsRefused()
    {
        var allowed = Allowed.Select(row => ((JobStatus)row[0], (JobStatus)row[1])).ToHashSet();
        foreach (var from in Enum.GetValues<JobStatus>())
            foreach (var to in Enum.GetValues<JobStatus>())
            {
                Assert.Equal(allowed.Contains((from, to)), JobStateMachine.CanMove(from, to));
                if (allowed.Contains((from, to))) continue;
                var job = Job(from);
                Assert.Throws<InvalidOperationException>(() => JobStateMachine.MoveTo(job, to));
                Assert.Equal(from, job.Status);
            }
    }

    [Theory]
    [InlineData(JobStatus.Pending, "PENDING")]
    [InlineData(JobStatus.Leased, "LEASED")]
    [InlineData(JobStatus.RetryScheduled, "RETRY_SCHEDULED")]
    [InlineData(JobStatus.Blocked, "BLOCKED")]
    [InlineData(JobStatus.ManualReview, "MANUAL_REVIEW")]
    [InlineData(JobStatus.Succeeded, "SUCCEEDED")]
    [InlineData(JobStatus.Dead, "DEAD")]
    [InlineData(JobStatus.Cancelled, "CANCELLED")]
    public void TheEventStatusIsTheStoredStatusText(JobStatus status, string text) => Assert.Equal(text, JobStateMachine.StatusText(status));

    [Fact]
    public void ACancelWithoutACodeKeepsTheStoredErrorAndRaisesNoEventWhenToldSo()
    {
        using var db = Context();
        var job = Job(JobStatus.Pending);
        job.LastErrorCode = "EARLIER";
        JobStateMachine.Cancel(db, job, Now, code: null, summary: null, JobEvent.None);
        Assert.Equal(JobStatus.Cancelled, job.Status);
        Assert.Equal(Now, job.CompletedAt);
        Assert.Equal("EARLIER", job.LastErrorCode);
        Assert.Equal(2, job.Version);
        Assert.Equal("hash", job.LeaseTokenHash);
        Assert.Empty(db.ChangeTracker.Entries<IntegrationOutboxEvent>());
    }

    [Fact]
    public void ACancelWithACodeSetsItDropsTheLeaseWhenAskedAndRaisesTheEvent()
    {
        using var db = Context();
        var job = Job(JobStatus.Leased);
        JobStateMachine.Cancel(db, job, Now, "CODE", "özet", JobEvent.Publish, clearLease: true);
        Assert.Equal("CODE", job.LastErrorCode);
        Assert.Equal("özet", job.LastErrorSummary);
        Assert.Null(job.LeaseTokenHash);
        Assert.Null(job.LeaseExpiresAt);
        Assert.Null(job.HeartbeatAt);
        var outbox = Assert.Single(db.ChangeTracker.Entries<IntegrationOutboxEvent>()).Entity;
        Assert.Equal("jobs", outbox.ResourceType);
        Assert.Equal(job.Id, outbox.AggregateId);
        Assert.Contains("\"status\":\"CANCELLED\"", outbox.PayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public void ARetryMakesTheJobRunnableWithOneMoreAttempt()
    {
        using var db = Context();
        var job = Job(JobStatus.Dead);
        job.AttemptCount = 6;
        job.MaxAttempts = 6;
        job.CompletedAt = Now.AddDays(-1);
        job.LastErrorCode = "X";
        job.LastErrorSummary = "Y";
        JobStateMachine.Retry(db, job, Now, JobEvent.None);
        Assert.Equal(JobStatus.RetryScheduled, job.Status);
        Assert.Equal(Now, job.AvailableAt);
        Assert.Equal(Now, job.RetriedAt);
        Assert.Null(job.CompletedAt);
        Assert.Null(job.LastErrorCode);
        Assert.Null(job.LastErrorSummary);
        Assert.Null(job.LeaseTokenHash);
        Assert.Equal(7, job.MaxAttempts);
        Assert.Equal(2, job.Version);
    }

    [Fact]
    public void ARequeueRunsTheJobAtOnceAndKeepsItsAttempts()
    {
        using var db = Context();
        var job = Job(JobStatus.Blocked);
        job.AttemptCount = 2;
        job.CompletedAt = Now.AddDays(-1);
        job.LastErrorCode = "CONNECTION_BLOCKED";
        JobStateMachine.Requeue(db, job, Now, JobEvent.None);
        Assert.Equal(JobStatus.Pending, job.Status);
        Assert.Equal(Now, job.AvailableAt);
        Assert.Null(job.CompletedAt);
        Assert.Null(job.LastErrorCode);
        Assert.Equal(2, job.AttemptCount);
        Assert.Equal(2, job.Version);
    }

    private static AppDbContext Context() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql("Host=localhost;Database=unused").Options);

    private static IntegrationJob Job(JobStatus status) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = Guid.CreateVersion7(),
        JobType = "TEST",
        PayloadJson = "{}",
        PayloadHash = "x",
        JobDedupKey = "k",
        EffectIdempotencyKey = "k",
        CorrelationId = "c",
        Status = status,
        LeaseTokenHash = "hash",
        LeaseExpiresAt = Now.AddMinutes(1),
        HeartbeatAt = Now,
        Version = 1
    };
}
