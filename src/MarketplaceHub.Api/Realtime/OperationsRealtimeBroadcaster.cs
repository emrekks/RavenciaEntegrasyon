using MarketplaceHub.Application;
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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextReconciliation = timeProvider.GetUtcNow();
        using var timer = new PeriodicTimer(PollInterval, timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
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

                if (now >= nextReconciliation)
                {
                    var tenantIds = await db.Tenants.AsNoTracking().Select(x => x.Id).ToListAsync(stoppingToken);
                    foreach (var tenantId in tenantIds)
                        await dashboard.RebuildTenantAsync(tenantId, stoppingToken);
                    nextReconciliation = now.Add(ReconciliationInterval);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Realtime outbox dispatcher döngüsü başarısız oldu");
            }
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
