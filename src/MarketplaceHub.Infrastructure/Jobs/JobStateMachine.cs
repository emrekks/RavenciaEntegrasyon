using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Jobs;

// Whether a transition raises the job's "jobs" outbox event (the job screens refetch on it, and a finance read's event
// tells the report cache). Some writers have never raised it; they say so with None until the owner decides.
public enum JobEvent { Publish, None }

// The one place a job's Status changes (KUYRUK-7, 15 Nov 2026). Every writer goes through MoveTo, directly or through one
// of the operations below, so a transition that is not in the table throws instead of leaving a job in a state nothing
// expects. The table lists the transitions the code makes today:
// - Pending / RetryScheduled -> Leased (lease), Dead (attempts used up), Cancelled, Pending (queued again at once).
// - Leased -> the completion results, RetryScheduled (refunded deferral, shutdown release) and Cancelled.
// - Blocked / ManualReview / Dead -> RetryScheduled (operator retry) and Cancelled (operator, closed as settled or
//   superseded); Blocked -> Pending (a verification job blocked by its connection runs again).
// - Succeeded and Cancelled are final.
public static class JobStateMachine
{
    private static readonly IReadOnlyDictionary<JobStatus, JobStatus[]> Allowed = new Dictionary<JobStatus, JobStatus[]>
    {
        [JobStatus.Pending] = [JobStatus.Leased, JobStatus.Dead, JobStatus.Cancelled, JobStatus.Pending],
        [JobStatus.RetryScheduled] = [JobStatus.Leased, JobStatus.Dead, JobStatus.Cancelled, JobStatus.Pending],
        [JobStatus.Leased] = [JobStatus.Succeeded, JobStatus.RetryScheduled, JobStatus.Blocked, JobStatus.ManualReview, JobStatus.Dead, JobStatus.Cancelled],
        [JobStatus.Blocked] = [JobStatus.RetryScheduled, JobStatus.Pending, JobStatus.Cancelled],
        [JobStatus.ManualReview] = [JobStatus.RetryScheduled, JobStatus.Cancelled],
        [JobStatus.Dead] = [JobStatus.RetryScheduled, JobStatus.Cancelled],
        [JobStatus.Succeeded] = [],
        [JobStatus.Cancelled] = []
    };

    public static bool CanMove(JobStatus from, JobStatus to) => Allowed[from].Contains(to);

    // Sets the status alone; the caller sets the fields that go with it (or uses one of the operations below).
    public static void MoveTo(IntegrationJob job, JobStatus to)
    {
        if (!CanMove(job.Status, to))
            throw new InvalidOperationException($"Job {job.Id} ({job.JobType}) cannot move from {job.Status} to {to}.");
        job.Status = to;
    }

    public static void ClearLease(IntegrationJob job)
    {
        job.LeaseTokenHash = null;
        job.LeaseExpiresAt = null;
        job.HeartbeatAt = null;
    }

    // Cancels a job that has not finished. A null code or summary leaves the stored one as it is (the writers that set
    // none keep doing so). clearLease drops a running job's lease, so its worker can no longer complete it.
    public static void Cancel(AppDbContext db, IntegrationJob job, DateTimeOffset now, string? code, string? summary, JobEvent jobEvent, bool clearLease = false)
    {
        MoveTo(job, JobStatus.Cancelled);
        job.CompletedAt = now;
        if (code is not null) job.LastErrorCode = code;
        if (summary is not null) job.LastErrorSummary = summary;
        if (clearLease) ClearLease(job);
        job.Version++;
        if (jobEvent == JobEvent.Publish) Publish(db, job, now);
    }

    // Closes the open attempt of a running job that is stopped from outside the worker (operator cancel, product deleted).
    public static async Task CloseRunningAttemptAsync(AppDbContext db, IntegrationJob job, DateTimeOffset now, string code, string summary, CancellationToken cancellationToken)
    {
        if (job.Status != JobStatus.Leased) return;
        var attempt = await db.JobAttempts.SingleOrDefaultAsync(x =>
            x.JobId == job.Id && x.AttemptNumber == job.AttemptCount && x.CompletedAt == null,
            cancellationToken);
        if (attempt is null) return;
        attempt.CompletedAt = now;
        attempt.Succeeded = false;
        attempt.ErrorCode = code;
        attempt.ErrorSummary = summary;
    }

    // An operator's retry of a stopped job: runnable at once with one more attempt, its error and lease cleared. The
    // retry time starts the deferral and waiting allowances afresh (JobLeaseService.AllowanceAge), and the run of write
    // conflicts starts again (a job sent to review for duplicates gets its duplicate retries back). The waiting checks
    // stay: MaxAttempts - WaitingChecks is the job's own attempt budget.
    public static void Retry(AppDbContext db, IntegrationJob job, DateTimeOffset now, JobEvent jobEvent)
    {
        MoveTo(job, JobStatus.RetryScheduled);
        job.AvailableAt = now;
        job.CompletedAt = null;
        ClearLease(job);
        job.LastErrorCode = null;
        job.LastErrorSummary = null;
        job.MaxAttempts = Math.Max(job.MaxAttempts, job.AttemptCount + 1);
        job.ConflictCount = 0;
        job.RetriedAt = now;
        job.Version++;
        if (jobEvent == JobEvent.Publish) Publish(db, job, now);
    }

    // A waiting job asked for again, or a blocked one let run again: runnable at once, its error cleared, its attempts kept.
    public static void Requeue(AppDbContext db, IntegrationJob job, DateTimeOffset now, JobEvent jobEvent)
    {
        MoveTo(job, JobStatus.Pending);
        job.AvailableAt = now;
        job.CompletedAt = null;
        job.LastErrorCode = null;
        job.LastErrorSummary = null;
        job.Version++;
        if (jobEvent == JobEvent.Publish) Publish(db, job, now);
    }

    // Cancels every job of the query in one statement (supersession and resets). The query's own filter says which
    // statuses may be closed. keepEarlierCompletedAt keeps a stopped job's original end time.
    public static Task<int> CancelWhereAsync(IQueryable<IntegrationJob> jobs, DateTimeOffset now, string code, string summary, bool keepEarlierCompletedAt, CancellationToken cancellationToken) =>
        keepEarlierCompletedAt
            ? jobs.ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, JobStatus.Cancelled)
                .SetProperty(x => x.LastErrorCode, code)
                .SetProperty(x => x.LastErrorSummary, summary)
                .SetProperty(x => x.CompletedAt, x => x.CompletedAt ?? now)
                .SetProperty(x => x.Version, x => x.Version + 1), cancellationToken)
            : jobs.ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, JobStatus.Cancelled)
                .SetProperty(x => x.LastErrorCode, code)
                .SetProperty(x => x.LastErrorSummary, summary)
                .SetProperty(x => x.CompletedAt, now)
                .SetProperty(x => x.Version, x => x.Version + 1), cancellationToken);

    public static IntegrationOutboxEvent Publish(AppDbContext db, IntegrationJob job, DateTimeOffset now)
    {
        var outboxEvent = NewJobEvent(job, now);
        db.IntegrationOutboxEvents.Add(outboxEvent);
        return outboxEvent;
    }

    // A job's state change is published as a "jobs" event, so only the job screens refetch; the domain the job
    // works on (orders, returns, ...) travels in the payload. The data a job changes raises its own events.
    public const string JobEventResource = "jobs";

    public static IntegrationOutboxEvent NewJobEvent(IntegrationJob job, DateTimeOffset now)
    {
        var metadata = IntegrationJobMetadataPolicy.Apply(job);
        return new IntegrationOutboxEvent
        {
            Id = Guid.CreateVersion7(),
            TenantId = job.TenantId,
            ResourceType = JobEventResource,
            OperationType = metadata.OperationType,
            AggregateType = "IntegrationJob",
            AggregateId = job.Id,
            AggregateVersion = job.Version,
            PayloadJson = JsonSerializer.Serialize(new { jobId = job.Id, jobType = job.JobType, resource = metadata.ResourceType, status = StatusText(job.Status), version = job.Version }),
            CreatedAt = now,
            NextAttemptAt = now
        };
    }

    // The status strings of the ck_jobs_status check constraint.
    public static string StatusText(JobStatus status) => status switch
    {
        JobStatus.RetryScheduled => "RETRY_SCHEDULED",
        JobStatus.ManualReview => "MANUAL_REVIEW",
        _ => status.ToString().ToUpperInvariant()
    };
}
