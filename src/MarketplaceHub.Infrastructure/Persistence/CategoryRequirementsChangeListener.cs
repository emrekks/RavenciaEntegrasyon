using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace MarketplaceHub.Infrastructure.Persistence;

// Holds one dedicated connection that LISTENs for catalog requirement changes and clears CategoryRequirementsCache on
// each. The cache serves entries only while this connection is up: a failure turns it off (every read goes to the
// database) until the listener is back, and the reconnect clears it again for anything missed meanwhile.
public sealed class CategoryRequirementsChangeListener(IServiceScopeFactory scopeFactory, CategoryRequirementsCache cache, ILogger<CategoryRequirementsChangeListener> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                string connectionString;
                await using (var scope = scopeFactory.CreateAsyncScope())
                    connectionString = scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.GetConnectionString()
                        ?? throw new InvalidOperationException("The database connection string is not configured.");
                var builder = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false, KeepAlive = 30 };
                await using var connection = new NpgsqlConnection(builder.ConnectionString);
                await connection.OpenAsync(stoppingToken);
                connection.Notification += (_, _) => cache.InvalidateAll();
                await using (var listen = new NpgsqlCommand($"LISTEN {CatalogRequirementNotifications.Channel}", connection))
                    await listen.ExecuteNonQueryAsync(stoppingToken);
                cache.SetActive(true);
                logger.LogInformation("Listening for catalog requirement changes on {Channel}", CatalogRequirementNotifications.Channel);
                while (!stoppingToken.IsCancellationRequested)
                    await connection.WaitAsync(TimeSpan.FromSeconds(30), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception exception)
            {
                cache.SetActive(false);
                logger.LogWarning(exception, "Catalog requirement change listener failed; requirements are read uncached while it reconnects");
                try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); }
                catch (OperationCanceledException) { }
            }
            finally
            {
                if (stoppingToken.IsCancellationRequested) cache.SetActive(false);
            }
        }
    }
}
