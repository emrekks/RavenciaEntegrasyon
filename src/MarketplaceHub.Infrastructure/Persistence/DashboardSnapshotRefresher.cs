using MarketplaceHub.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MarketplaceHub.Infrastructure.Persistence;

// When a tenant's dashboard snapshot is rebuilt. A data change (an outbox event other than a job's own lease or
// completion) marks it for a rebuild at most once per debounce window; without changes it is still rebuilt every
// few minutes, since "late" and the job counts move with the clock alone.
public static class DashboardSnapshotRefreshPolicy
{
    public static readonly TimeSpan Debounce = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan Reconciliation = TimeSpan.FromMinutes(5);
    public const string JobAggregateType = "IntegrationJob";

    public static bool IsDue(DateTimeOffset? updatedAt, bool changedSinceUpdate, DateTimeOffset now) =>
        updatedAt is not { } updated
        || now - updated >= Reconciliation
        || (changedSinceUpdate && now - updated >= Debounce);
}

// Runs in the worker: the API only announces changes. Each tenant is rebuilt in its own scope and its own
// try/catch, so one failing tenant neither stops the others nor makes every tenant rebuild on the next tick.
public sealed class DashboardSnapshotRefresher(IServiceScopeFactory scopes, TimeProvider timeProvider, ILogger<DashboardSnapshotRefresher> logger) : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Tick, timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await RefreshDueTenantsAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception exception) { logger.LogWarning(exception, "Pano özetleri yenilenecek kiracılar okunamadı; sonraki turda denenecek"); }
        }
    }

    internal async Task<int> RefreshDueTenantsAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        List<Guid> due;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var tenants = await (from tenant in db.Tenants.AsNoTracking()
                                 join snapshot in db.DashboardSnapshots.AsNoTracking() on tenant.Id equals snapshot.TenantId into snapshots
                                 from snapshot in snapshots.DefaultIfEmpty()
                                 select new
                                 {
                                     tenant.Id,
                                     UpdatedAt = snapshot == null ? (DateTimeOffset?)null : snapshot.UpdatedAt,
                                     Changed = snapshot != null && db.IntegrationOutboxEvents.Any(change => change.TenantId == tenant.Id
                                         && change.CreatedAt > snapshot.UpdatedAt
                                         && change.AggregateType != DashboardSnapshotRefreshPolicy.JobAggregateType)
                                 }).ToListAsync(cancellationToken);
            due = tenants.Where(x => DashboardSnapshotRefreshPolicy.IsDue(x.UpdatedAt, x.Changed, now)).Select(x => x.Id).ToList();
        }

        var rebuilt = 0;
        foreach (var tenantId in due)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<IDashboardReadService>().RebuildTenantAsync(tenantId, cancellationToken);
                rebuilt++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Pano özeti yenilenemedi; bu kiracı sonraki turda yeniden denenecek. TenantId: {TenantId}", tenantId);
            }
        }
        return rebuilt;
    }
}
