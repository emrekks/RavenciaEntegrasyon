using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Files;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Api.Operations;

/// <summary>
/// Runs the hourly leftover sweep shortly after start and then every hour: unreferenced old files are handed to the
/// purge (<see cref="FileAssetReferences.MarkOrphansAsync"/>), purged files leave disk and their rows
/// (<see cref="PurgedFileRetention"/>), uploads never committed after a day leave disk, replaced keys and deleted connections' settings go (<see cref="ConnectionLeftovers"/>).
/// Each step runs on its own, so one failing does not hold the others back.
/// </summary>
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
                var orphans = await StepAsync("orphan files", (services, now) => FileAssetReferences.MarkOrphansAsync(services.GetRequiredService<AppDbContext>(), now, stoppingToken), stoppingToken);
                var purged = await StepAsync("purged files", (services, now) => PurgedFileRetention.SweepAsync(services.GetRequiredService<AppDbContext>(), services.GetRequiredService<IPrivateFileStorage>(), now, stoppingToken, services.GetService<ProductMediaRenditions>()), stoppingToken);
                var keys = await StepAsync("replaced keys", (services, _) => ConnectionLeftovers.RemoveRevokedCredentialsAsync(services.GetRequiredService<AppDbContext>(), stoppingToken), stoppingToken);
                var connections = await StepAsync("deleted connections", (services, now) => ConnectionLeftovers.SweepDeletedConnectionsAsync(services.GetRequiredService<AppDbContext>(), now, stoppingToken), stoppingToken);
                var staged = await StepAsync("uncommitted files", (services, now) => SweepStagedAsync(services, now, stoppingToken), stoppingToken);
                logger.LogInformation("Leftover sweep: {Orphans} unreferenced files marked for purge, {Purged} files purged, {Staged} uncommitted temporary files removed, {Keys} replaced keys deleted, {Connections} deleted connections removed.", orphans, purged, staged, keys, connections);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    // Old temporary upload files: a saved row's file whose rename failed is renamed (an Error, since the upload answered
    // while its file kept the temporary name), a never-saved one is removed. A file a row records is never deleted.
    private async Task<int> SweepStagedAsync(IServiceProvider services, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var result = await services.GetRequiredService<IPrivateFileStorage>().SweepStagedAsync(now - PrivateFileStorage.StagedMaximumAge,
            (relativePath, token) => db.FileAssets.IgnoreQueryFilters().AsNoTracking().AnyAsync(x => x.RelativePath == relativePath, token), cancellationToken);
        if (result.Completed > 0 || result.Kept > 0)
            logger.LogError("Leftover sweep: {Completed} saved uploads still had their temporary name and were renamed now; {Kept} could not be renamed and stay on disk.", result.Completed, result.Kept);
        return result.Removed;
    }

    private async Task<int> StepAsync(string name, Func<IServiceProvider, DateTimeOffset, Task<int>> step, CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            return await step(scope.ServiceProvider, timeProvider.GetUtcNow());
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Leftover sweep step {Step} failed; it is retried on the next tick.", name);
            return 0;
        }
    }
}
