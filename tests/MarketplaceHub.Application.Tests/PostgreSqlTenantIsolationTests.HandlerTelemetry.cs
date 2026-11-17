using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// ISLEYICI-9 round 10 step 3: a job handler returns a product import's own counts (JobTelemetry.Import), and the run's
// cursor counts them as it counts a product import the processor runs itself.
public sealed partial class PostgreSqlTenantIsolationTests
{
    [PostgreSqlFact]
    public async Task AHandlersImportTelemetryIsWhatTheRunsCursorCounts()
    {
        var (tenant, connection, _) = await SeedReadGuardConnectionAsync("handler-import-telemetry", "TRENDYOL", "PRODUCTS");
        try
        {
            await using (var db = fixture.CreateContext())
            {
                var handler = new FixedOutcomeHandler(MarketplaceJobTypes.ProductSync,
                    new JobOutcome(JobExecutionResult.Success(), new JobTelemetry(Requests: 3, Received: 40, Skipped: 1, Import: new JobImportTelemetry(Received: 10, Processed: 7, Skipped: 2, Failed: 1))));
                var processor = new MarketplaceJobProcessor(db, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, new ConfigurationBuilder().Build(), fixture.TimeProvider, handlers: [handler]);
                var result = await processor.ProcessAsync(tenant.Id, connection.Id, MarketplaceJobTypes.ProductSync, "{}", "handler-import-telemetry", CancellationToken.None);
                Assert.Equal(JobCompletionKind.Succeeded, result.Kind);
            }
            await using var check = fixture.CreateContext();
            var cursor = await check.SyncCursors.AsNoTracking().SingleAsync(x => x.TenantId == tenant.Id && x.ResourceType == "PRODUCTS");
            Assert.Equal(3, cursor.LastRequestCount);
            Assert.Equal(10, cursor.LastReceivedCount);
            Assert.Equal(7, cursor.LastChangedCount);
            Assert.Equal(2, cursor.LastSkippedCount);
        }
        finally { await DeleteReadGuardTenantAsync(tenant.Id); }
    }

    private sealed class FixedOutcomeHandler(string jobType, JobOutcome outcome) : IJobHandler
    {
        public bool Handles(string candidate) => candidate == jobType;
        public Task<JobOutcome> HandleAsync(JobRequest request, CancellationToken cancellationToken) => Task.FromResult(outcome);
    }
}
