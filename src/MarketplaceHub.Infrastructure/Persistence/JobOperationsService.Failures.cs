using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

// İşlem Takibi › Kuyruk ve Kota › İş Türüne Göre Kuyruk: the failed attempts behind a job type's Hata number. The rows
// are those MetricsAsync counts as Failed: attempts of the tenant started in the window, finished, not succeeded, and not
// a waiting check (JobWaitingPolicy); the platforms filter is the same. Read-only; served by job_attempts
// (TenantId, StartedAt) INCLUDE (JobId) and the jobs' primary key. Stored summaries are returned through
// JobErrorMessages.Display like every job screen; no payload, and nothing here is logged.
public sealed partial class JobOperationsService
{
    public async Task<JobTypeFailuresView> FailuresAsync(Guid tenantId, string jobType, TimeSpan window, IReadOnlyList<string>? platforms, int page, int pageSize, CancellationToken cancellationToken)
    {
        var type = jobType.Trim();
        var since = (timeProvider.GetUtcNow() - window).ToUniversalTime();
        var waitingCodes = JobWaitingPolicy.Codes.ToArray();
        var codes = (platforms ?? []).Select(code => code.Trim().ToUpperInvariant()).Where(code => code.Length > 0).Distinct().ToArray();
        var failed =
            from attempt in db.JobAttempts.AsNoTracking()
            join job in db.IntegrationJobs.AsNoTracking() on attempt.JobId equals job.Id
            where attempt.TenantId == tenantId && job.TenantId == tenantId && job.JobType == type
                && attempt.StartedAt >= since && attempt.CompletedAt != null && !attempt.Succeeded
                && (attempt.ErrorCode == null || !waitingCodes.Contains(attempt.ErrorCode))
            select new { Attempt = attempt, job.Status, job.ConnectionId };
        if (codes.Length > 0)
            failed = failed.Where(x => x.ConnectionId != null && db.PlatformConnections.Any(connection =>
                connection.TenantId == tenantId && connection.Id == x.ConnectionId && codes.Contains(connection.PlatformCode)));

        // Grouped by the stored code and summary in SQL, then by the sentence a job screen shows (a code with a plain
        // sentence reads the same whatever its stored summary said).
        var stored = await failed
            .GroupBy(x => new { x.Attempt.ErrorCode, x.Attempt.ErrorSummary })
            .Select(group => new { group.Key.ErrorCode, group.Key.ErrorSummary, Count = group.Count(), First = group.Min(x => x.Attempt.StartedAt), Last = group.Max(x => x.Attempt.StartedAt) })
            .ToListAsync(cancellationToken);
        var groups = stored
            .GroupBy(row => (row.ErrorCode, Reason: JobErrorMessages.Display(row.ErrorCode, row.ErrorSummary)))
            .Select(group => new JobTypeFailureGroupView(group.Key.ErrorCode, group.Key.Reason, group.Sum(row => row.Count), group.Min(row => row.First), group.Max(row => row.Last), ConnectionRetestPolicy.IsTransient(group.Key.ErrorCode)))
            .OrderByDescending(group => group.Count).ThenByDescending(group => group.LastSeen).ThenBy(group => group.ErrorCode, StringComparer.Ordinal)
            .ToList();
        var total = groups.Sum(group => group.Count);

        var rows = await failed
            .OrderByDescending(x => x.Attempt.StartedAt).ThenByDescending(x => x.Attempt.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new { x.Attempt.Id, x.Attempt.JobId, x.Attempt.StartedAt, x.Attempt.AttemptNumber, x.Attempt.ErrorCode, x.Attempt.ErrorSummary, x.Status, x.ConnectionId })
            .ToListAsync(cancellationToken);
        var connectionIds = rows.Where(row => row.ConnectionId != null).Select(row => row.ConnectionId!.Value).Distinct().ToArray();
        Dictionary<Guid, FailureConnection> connections = connectionIds.Length == 0
            ? []
            : await db.PlatformConnections.AsNoTracking()
                .Where(connection => connection.TenantId == tenantId && connectionIds.Contains(connection.Id))
                .Select(connection => new { connection.Id, connection.DisplayName, connection.PlatformCode })
                .ToDictionaryAsync(connection => connection.Id, connection => new FailureConnection(connection.DisplayName, connection.PlatformCode), cancellationToken);
        var items = rows.Select(row =>
        {
            var connection = row.ConnectionId is { } id ? connections.GetValueOrDefault(id) : null;
            return new JobTypeFailureView(row.JobId, row.Id, row.StartedAt, row.AttemptNumber, connection?.DisplayName, connection?.PlatformCode, row.ErrorCode,
                JobErrorMessages.Display(row.ErrorCode, row.ErrorSummary), JobStatusText(row.Status), ConnectionRetestPolicy.IsTransient(row.ErrorCode));
        }).ToList();
        return new(type, total, groups.Count, [.. groups.Take(JobTypeFailuresView.MaximumGroups)], items, page, pageSize);
    }

    private sealed record FailureConnection(string DisplayName, string PlatformCode);
}
