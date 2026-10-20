using MarketplaceHub.Infrastructure.Persistence;

namespace MarketplaceHub.Api.Catalog;

/// <summary>Runs <see cref="ApiIdempotencyRetention"/> shortly after start and then every hour.</summary>
public sealed class ApiIdempotencyRetentionService(IServiceScopeFactory scopes, TimeProvider timeProvider, ILogger<ApiIdempotencyRetentionService> logger) : BackgroundService
{
    private static readonly TimeSpan StartDelay = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartDelay, timeProvider, stoppingToken);
            using var timer = new PeriodicTimer(ApiIdempotencyRetention.Interval, timeProvider);
            do
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    var (deleted, markedUnknown) = await ApiIdempotencyRetention.SweepAsync(db, timeProvider.GetUtcNow(), stoppingToken);
                    if (deleted > 0 || markedUnknown > 0)
                        logger.LogInformation("Idempotency sweep deleted {Deleted} expired records and kept {MarkedUnknown} unfinished ones as unknown.", deleted, markedUnknown);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "Idempotency sweep failed; it is retried on the next tick.");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
