using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

public sealed class JobOperationsService(AppDbContext db, TimeProvider timeProvider) : IJobOperationsService
{
    public async Task<IReadOnlyList<JobSummaryView>> ListAsync(Guid tenantId, int limit, string? status, CancellationToken cancellationToken)
    {
        var query = db.IntegrationJobs.AsNoTracking().Where(x => x.TenantId == tenantId);
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!TryParseStatus(status, out var parsed)) return [];
            query = query.Where(x => x.Status == parsed);
        }
        var jobs = await query.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).Take(Math.Clamp(limit, 1, 200)).ToListAsync(cancellationToken);
        return jobs.Select(Summary).ToList();
    }

    public async Task<ServiceResult<JobDetailView>> GetAsync(Guid tenantId, Guid jobId, CancellationToken cancellationToken)
    {
        var job = await db.IntegrationJobs.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == jobId, cancellationToken);
        return job is null
            ? ServiceResult<JobDetailView>.Fail("JOB_NOT_FOUND", "Job bulunamadı.", 404)
            : ServiceResult<JobDetailView>.Ok(await DetailAsync(job, cancellationToken));
    }

    public async Task<ServiceResult<JobDetailView>> RetryAsync(Guid tenantId, Guid jobId, CancellationToken cancellationToken)
    {
        var job = await db.IntegrationJobs.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == jobId, cancellationToken);
        if (job is null) return ServiceResult<JobDetailView>.Fail("JOB_NOT_FOUND", "Job bulunamadı.", 404);
        if (job.Status is JobStatus.Leased or JobStatus.Pending or JobStatus.RetryScheduled or JobStatus.Succeeded or JobStatus.Cancelled)
            return ServiceResult<JobDetailView>.Fail("JOB_NOT_RETRYABLE", "Bu job mevcut durumunda manuel yeniden deneme kabul etmiyor.", 409);

        job.Status = JobStatus.RetryScheduled;
        job.AvailableAt = timeProvider.GetUtcNow();
        job.CompletedAt = null;
        job.LeaseTokenHash = null;
        job.LeaseExpiresAt = null;
        job.HeartbeatAt = null;
        job.MaxAttempts = Math.Max(job.MaxAttempts, job.AttemptCount + 1);
        job.Version++;
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult<JobDetailView>.Ok(await DetailAsync(job, cancellationToken));
    }

    public async Task<ServiceResult<JobDetailView>> CancelAsync(Guid tenantId, Guid jobId, CancellationToken cancellationToken)
    {
        var job = await db.IntegrationJobs.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == jobId, cancellationToken);
        if (job is null) return ServiceResult<JobDetailView>.Fail("JOB_NOT_FOUND", "Job bulunamadı.", 404);
        if (job.Status == JobStatus.Leased) return ServiceResult<JobDetailView>.Fail("JOB_ALREADY_RUNNING", "Çalışan job lease süresi bitmeden iptal edilemez.", 409);
        if (job.Status is JobStatus.Succeeded or JobStatus.Dead or JobStatus.Cancelled)
            return ServiceResult<JobDetailView>.Fail("JOB_TERMINAL", "Terminal durumdaki job iptal edilemez.", 409);

        job.Status = JobStatus.Cancelled;
        job.CompletedAt = timeProvider.GetUtcNow();
        job.LastErrorCode = "CANCELLED_BY_OPERATOR";
        job.LastErrorSummary = "Job kullanıcı tarafından iptal edildi.";
        job.Version++;
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult<JobDetailView>.Ok(await DetailAsync(job, cancellationToken));
    }

    private async Task<JobDetailView> DetailAsync(IntegrationJob job, CancellationToken cancellationToken)
    {
        var attempts = await db.JobAttempts.AsNoTracking()
            .Where(x => x.TenantId == job.TenantId && x.JobId == job.Id)
            .OrderByDescending(x => x.AttemptNumber)
            .Select(x => new JobAttemptDetailView(x.AttemptNumber, x.StartedAt, x.CompletedAt, x.Succeeded, x.ErrorCode, x.ErrorSummary))
            .ToListAsync(cancellationToken);
        return new JobDetailView(Summary(job), attempts);
    }

    private static JobSummaryView Summary(IntegrationJob x) => new(
        x.Id, x.ConnectionId, x.JobType, Wire(x.Status), x.AttemptCount, x.MaxAttempts,
        x.AvailableAt, x.LastErrorCode, x.LastErrorSummary, x.CorrelationId,
        x.CreatedAt, x.StartedAt, x.CompletedAt);

    private static bool TryParseStatus(string value, out JobStatus status) => Enum.TryParse(value.Replace("_", string.Empty, StringComparison.Ordinal), true, out status);
    private static string Wire(JobStatus status) => status switch
    {
        JobStatus.RetryScheduled => "RETRY_SCHEDULED",
        JobStatus.ManualReview => "MANUAL_REVIEW",
        _ => status.ToString().ToUpperInvariant()
    };
}
