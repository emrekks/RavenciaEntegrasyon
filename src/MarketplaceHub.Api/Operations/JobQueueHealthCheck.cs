using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace MarketplaceHub.Api.Operations;

// Reports a stuck job queue. It never returns Unhealthy, so /health/ready (which deploys and
// the proxy rely on) stays 200 and only shows "Degraded"; /health/queues turns Degraded into 503
// for monitoring.
public sealed class JobQueueHealthCheck(IServiceScopeFactory scopeFactory, IConfiguration configuration, TimeProvider timeProvider) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var now = timeProvider.GetUtcNow();
            // Jobs held back by their connection's busy lock group or a quota backoff are not stuck
            // (the same "runnable" rule as the panel's queue depth, QueueHealthService.RunnableDepthAsync).
            var oldestRunnable = await QueueHealthService.OldestRunnableAvailableAtAsync(db, now, cancellationToken);
            var staleLeaseCutoff = now - TimeSpan.FromMinutes(5);
            var staleLeases = await db.IntegrationJobs.AsNoTracking()
                .CountAsync(job => job.Status == JobStatus.Leased && job.LeaseExpiresAt < staleLeaseCutoff, cancellationToken);
            return Evaluate(now, oldestRunnable, staleLeases,
                TimeSpan.FromMinutes(Math.Clamp(configuration.GetValue("Health:Queues:MaximumWaitMinutes", 10), 1, 240)));
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Degraded("Job queue health could not be read.", exception);
        }
    }

    internal static HealthCheckResult Evaluate(DateTimeOffset now, DateTimeOffset? oldestRunnableAvailableAt, int staleLeases, TimeSpan maximumWait)
    {
        var waitSeconds = oldestRunnableAvailableAt is { } availableAt ? Math.Max(0, Math.Round((now - availableAt).TotalSeconds)) : 0;
        var data = new Dictionary<string, object> { ["oldestRunnableWaitSeconds"] = waitSeconds, ["staleLeases"] = staleLeases };
        if (staleLeases > 0)
            return HealthCheckResult.Degraded($"{staleLeases} job lease(s) expired more than 5 minutes ago; the worker or its lease reaper is not running.", data: data);
        if (waitSeconds > maximumWait.TotalSeconds)
            return HealthCheckResult.Degraded($"A runnable job has been waiting {waitSeconds} s (limit {maximumWait.TotalSeconds} s).", data: data);
        return HealthCheckResult.Healthy("Job queue is moving.", data);
    }
}
