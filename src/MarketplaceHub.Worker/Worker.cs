using MarketplaceHub.Application;

namespace MarketplaceHub.Worker;

public sealed class Worker(IServiceScopeFactory scopeFactory, ILogger<Worker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var jobs = scope.ServiceProvider.GetRequiredService<IJobLeaseService>();
                var reaped = await jobs.ReapExpiredAsync(stoppingToken);
                if (reaped > 0) logger.LogWarning("Reaped {Count} expired job leases", reaped);
                var job = await jobs.TryLeaseAsync(TimeSpan.FromMinutes(2), stoppingToken);
                if (job is not null)
                {
                    logger.LogWarning("Failing unsupported F1 job type {JobType} with correlation-safe metadata", job.JobType);
                    await jobs.CompleteAsync(job.Id, job.LeaseToken, false, "UNSUPPORTED_JOB_TYPE", stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception exception) { logger.LogError(exception, "Worker loop failed"); }
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }
}
