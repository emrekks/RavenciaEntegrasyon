using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Api.Realtime;

/// <summary>
/// Dispatches durable integration outbox events. A completed job is therefore
/// visible after the API restarts too; no moving watermark or full jobs scan is
/// needed. Dashboard projections are rebuilt once per affected tenant batch.
/// </summary>
public sealed class OperationsRealtimeBroadcaster(IServiceScopeFactory scopes, IHubContext<OperationsHub> hub, TimeProvider timeProvider, ILogger<OperationsRealtimeBroadcaster> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ReconciliationInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan OutboxCleanupInterval = TimeSpan.FromDays(1);
    private static readonly TimeSpan OutboxCleanupBacklogInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan OutboxCleanupRetryInterval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextReconciliation = timeProvider.GetUtcNow();
        var nextOutboxCleanup = timeProvider.GetUtcNow();
        DateTimeOffset? newestAnnouncedJob = null;
        using var timer = new PeriodicTimer(PollInterval, timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await db.Database.OpenConnectionAsync(stoppingToken);
                var ownsDispatchLease = false;
                try
                {
                    ownsDispatchLease = await OperationsRealtimeDispatchLease.TryAcquireAsync(db.Database.GetDbConnection(), stoppingToken);
                    if (!ownsDispatchLease) continue;

                    var dashboard = scope.ServiceProvider.GetRequiredService<IDashboardReadService>();
                    var now = timeProvider.GetUtcNow();
                    var events = await db.IntegrationOutboxEvents.AsNoTracking()
                        .Where(x => x.PublishedAt == null && x.NextAttemptAt <= now)
                        .OrderBy(x => x.CreatedAt)
                        .ThenBy(x => x.Id)
                        .Take(250)
                        .ToListAsync(stoppingToken);

                    foreach (var tenantEvents in events.GroupBy(x => x.TenantId))
                    {
                        try
                        {
                            await dashboard.RebuildTenantAsync(tenantEvents.Key, stoppingToken);
                            var resourceEvents = tenantEvents.Select(x => new
                            {
                                eventId = x.Id,
                                resource = x.ResourceType,
                                operation = x.OperationType,
                                aggregateId = x.AggregateId,
                                version = x.AggregateVersion,
                                occurredAt = x.CreatedAt
                            }).ToArray();
                            var resources = tenantEvents.Select(x => x.ResourceType).Distinct(StringComparer.Ordinal).ToArray();
                            await hub.Clients.Group(OperationsHub.TenantGroup(tenantEvents.Key.ToString("D")))
                                .SendAsync("operationsChanged", new { resources, events = resourceEvents, occurredAt = now }, stoppingToken);

                            var ids = tenantEvents.Select(x => x.Id).ToArray();
                            await db.IntegrationOutboxEvents.Where(x => ids.Contains(x.Id)).ExecuteUpdateAsync(update => update
                                .SetProperty(x => x.PublishedAt, now)
                                .SetProperty(x => x.DispatchAttempts, x => x.DispatchAttempts + 1)
                                .SetProperty(x => x.LastDispatchError, (string?)null), stoppingToken);
                        }
                        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                        catch (Exception exception)
                        {
                            await MarkFailedAsync(db, tenantEvents.Select(x => x.Id).ToArray(), now, exception, stoppingToken);
                            logger.LogWarning(exception, "Realtime outbox olayı yayınlanamadı; tekrar denenecek. TenantId: {TenantId}", tenantEvents.Key);
                        }
                    }

                    newestAnnouncedJob = await AnnounceNewJobsAsync(db, newestAnnouncedJob, now, stoppingToken);

                    if (now >= nextReconciliation)
                    {
                        var tenantIds = await db.Tenants.AsNoTracking().Select(x => x.Id).ToListAsync(stoppingToken);
                        foreach (var tenantId in tenantIds)
                            await dashboard.RebuildTenantAsync(tenantId, stoppingToken);
                        nextReconciliation = now.Add(ReconciliationInterval);
                    }

                    if (now >= nextOutboxCleanup)
                    {
                        try
                        {
                            var deleted = await OperationsRealtimeOutboxRetention.DeleteExpiredPublishedBatchAsync(db, now, stoppingToken);
                            nextOutboxCleanup = deleted == OperationsRealtimeOutboxRetention.BatchSize
                                ? now.Add(OutboxCleanupBacklogInterval)
                                : now.Add(OutboxCleanupInterval);
                            if (deleted > 0)
                                logger.LogInformation("Süresi dolmuş yayımlanmış realtime outbox kayıtları temizlendi. Silinen: {DeletedCount}", deleted);
                        }
                        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                        catch (Exception exception)
                        {
                            nextOutboxCleanup = now.Add(OutboxCleanupRetryInterval);
                            logger.LogWarning(exception, "Realtime outbox saklama temizliği başarısız; tekrar denenecek");
                        }
                    }
                }
                finally
                {
                    if (ownsDispatchLease)
                    {
                        try
                        {
                            await OperationsRealtimeDispatchLease.ReleaseAsync(db.Database.GetDbConnection(), CancellationToken.None);
                        }
                        catch (Exception exception)
                        {
                            logger.LogWarning(exception, "Realtime outbox advisory lease bırakılamadı; bağlantı kapanınca serbest kalacak");
                        }
                    }
                    await db.Database.CloseConnectionAsync();
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Realtime outbox dispatcher döngüsü başarısız oldu");
            }
        }
    }

    /// <summary>
    /// Queuing a job writes no outbox event, so the job list would only see it on its next poll. Each tick looks for
    /// active jobs created after the newest one already announced and tells their tenants that "jobs" changed. Only
    /// reads (active jobs are a small set reached through the status index); a failure here never stops the dispatcher.
    /// </summary>
    private async Task<DateTimeOffset?> AnnounceNewJobsAsync(AppDbContext db, DateTimeOffset? newestAnnounced, DateTimeOffset now, CancellationToken cancellationToken)
    {
        try
        {
            // The first tick starts from the newest job so a restart does not announce old ones.
            if (newestAnnounced is null)
                return await db.IntegrationJobs.AsNoTracking().MaxAsync(x => (DateTimeOffset?)x.CreatedAt, cancellationToken) ?? now;

            var since = newestAnnounced.Value;
            var fresh = await db.IntegrationJobs.AsNoTracking()
                .Where(x => (x.Status == JobStatus.Pending || x.Status == JobStatus.Leased || x.Status == JobStatus.RetryScheduled) && x.CreatedAt > since)
                .GroupBy(x => x.TenantId)
                .Select(group => new { TenantId = group.Key, Newest = group.Max(x => x.CreatedAt) })
                .ToListAsync(cancellationToken);
            foreach (var tenant in fresh)
            {
                await hub.Clients.Group(OperationsHub.TenantGroup(tenant.TenantId.ToString("D")))
                    .SendAsync("operationsChanged", new { resources = new[] { "jobs" }, events = Array.Empty<object>(), occurredAt = now }, cancellationToken);
            }
            return fresh.Count == 0 ? since : fresh.Max(x => x.Newest);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Yeni işler canlı akışa duyurulamadı; liste kendi yenilemesiyle güncellenecek");
            return newestAnnounced;
        }
    }

    private static async Task MarkFailedAsync(AppDbContext db, Guid[] ids, DateTimeOffset now, Exception exception, CancellationToken cancellationToken)
    {
        var safeError = exception.Message.Length > 512 ? exception.Message[..512] : exception.Message;
        var events = await db.IntegrationOutboxEvents.Where(x => ids.Contains(x.Id)).ToListAsync(cancellationToken);
        foreach (var item in events)
        {
            item.DispatchAttempts++;
            item.LastDispatchError = safeError;
            item.NextAttemptAt = now.AddSeconds(BackoffSeconds(item.DispatchAttempts));
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    private static double BackoffSeconds(int attempts) => Math.Min(60, Math.Pow(2, Math.Min(attempts + 1, 6)));
}
