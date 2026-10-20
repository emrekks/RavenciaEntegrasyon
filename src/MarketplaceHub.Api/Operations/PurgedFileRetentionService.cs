using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Files;
using MarketplaceHub.Infrastructure.Persistence;

namespace MarketplaceHub.Api.Operations;

/// <summary>Runs <see cref="PurgedFileRetention"/> shortly after start and then every hour.</summary>
public sealed class PurgedFileRetentionService(IServiceScopeFactory scopes, TimeProvider timeProvider, ILogger<PurgedFileRetentionService> logger) : BackgroundService
{
    private static readonly TimeSpan StartDelay = TimeSpan.FromMinutes(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartDelay, timeProvider, stoppingToken);
            using var timer = new PeriodicTimer(PurgedFileRetention.Interval, timeProvider);
            do
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var removed = await PurgedFileRetention.SweepAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), scope.ServiceProvider.GetRequiredService<IPrivateFileStorage>(), timeProvider.GetUtcNow(), stoppingToken);
                    if (removed > 0) logger.LogInformation("Removed {Count} stored files left by data resets.", removed);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "Purged file sweep failed; it is retried on the next tick.");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
