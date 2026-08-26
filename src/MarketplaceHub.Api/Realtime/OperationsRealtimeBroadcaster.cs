using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Api.Realtime;

public sealed class OperationsRealtimeBroadcaster(IServiceScopeFactory scopes, IHubContext<OperationsHub> hub, TimeProvider timeProvider, ILogger<OperationsRealtimeBroadcaster> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var watermark = timeProvider.GetUtcNow().AddSeconds(-5);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var completed = await db.IntegrationJobs.AsNoTracking()
                    .Where(x => x.CompletedAt != null && x.CompletedAt > watermark)
                    .OrderBy(x => x.CompletedAt)
                    .Take(250)
                    .Select(x => new { x.TenantId, x.JobType, x.Status, x.CompletedAt })
                    .ToListAsync(stoppingToken);
                if (completed.Count == 0) continue;
                watermark = completed.Max(x => x.CompletedAt)!.Value;
                foreach (var tenant in completed.GroupBy(x => x.TenantId))
                {
                    var resources = tenant.Select(x => Resource(x.JobType)).Distinct(StringComparer.Ordinal).ToArray();
                    await hub.Clients.Group(OperationsHub.TenantGroup(tenant.Key.ToString("D")))
                        .SendAsync("operationsChanged", new { resources, occurredAt = watermark }, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Realtime operasyon değişiklikleri yayınlanamadı");
            }
        }
    }

    private static string Resource(string jobType) => jobType switch
    {
        var value when value.Contains("ORDER", StringComparison.Ordinal) => "orders",
        var value when value.Contains("RETURN", StringComparison.Ordinal) => "returns",
        var value when value.Contains("INVENTORY", StringComparison.Ordinal) || value.Contains("STOCK", StringComparison.Ordinal) => "inventory",
        var value when value.Contains("PRODUCT", StringComparison.Ordinal) => "products",
        _ => "jobs"
    };
}
