using MarketplaceHub.Application;
using MarketplaceHub.Domain;
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
    // The announcement of a rebuilt snapshot: it tells open panels to re-read their counts and is never itself a
    // data change that calls for another rebuild.
    public const string SnapshotAggregateType = "DashboardSnapshot";
    public const string ResourceType = "dashboard";

    public static bool IsDue(DateTimeOffset? updatedAt, bool changedSinceUpdate, DateTimeOffset now) =>
        updatedAt is not { } updated
        || now - updated >= Reconciliation
        || (changedSinceUpdate && now - updated >= Debounce);

    // Whether IsDue depends on a change since the snapshot: only between the debounce and the reconciliation age.
    // Outside that window the outbox need not be read at all.
    public static bool NeedsChangeCheck(DateTimeOffset? updatedAt, DateTimeOffset now) =>
        updatedAt is { } updated && now - updated >= Debounce && now - updated < Reconciliation;

    // The data changes since the snapshot, with its time written into the SQL as a constant. A correlated
    // "CreatedAt > snapshot.UpdatedAt" (and a generic plan for a "CreatedAt > $1" parameter) got a fixed
    // one-third row estimate and a sequential scan of the whole outbox (~90 ms every tick on live); a known time
    // is estimated from the column statistics and read on the (TenantId, CreatedAt) index (~0.05 ms).
    public static IQueryable<IntegrationOutboxEvent> ChangesSince(AppDbContext db, Guid tenantId, DateTimeOffset updatedAt) =>
        db.IntegrationOutboxEvents.AsNoTracking().Where(change => change.TenantId == tenantId
            && change.CreatedAt > EF.Constant(updatedAt)
            && change.AggregateType != JobAggregateType
            && change.AggregateType != SnapshotAggregateType);
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
                                     UpdatedAt = snapshot == null ? (DateTimeOffset?)null : snapshot.UpdatedAt
                                 }).ToListAsync(cancellationToken);
            due = [];
            foreach (var tenant in tenants)
            {
                var changed = DashboardSnapshotRefreshPolicy.NeedsChangeCheck(tenant.UpdatedAt, now)
                    && await DashboardSnapshotRefreshPolicy.ChangesSince(db, tenant.Id, tenant.UpdatedAt!.Value).AnyAsync(cancellationToken);
                if (DashboardSnapshotRefreshPolicy.IsDue(tenant.UpdatedAt, changed, now)) due.Add(tenant.Id);
            }
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
