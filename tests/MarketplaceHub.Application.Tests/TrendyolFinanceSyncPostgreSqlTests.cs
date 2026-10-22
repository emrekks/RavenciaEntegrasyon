using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.Trendyol;
using MarketplaceHub.Infrastructure.Jobs.Handlers;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

[Collection("postgres-integration")]
public sealed class TrendyolFinanceSyncPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    [PostgreSqlFact]
    public async Task A_backfill_runs_in_slices_and_a_second_run_stores_nothing_twice()
    {
        var (tenantId, connectionId) = await SeedAsync("fin-backfill");
        try
        {
            var port = new FakeFinancePort();
            var slices = 0;
            JobOutcome outcome;
            do
            {
                outcome = await RunAsync(tenantId, connectionId, port, Now);
                slices++;
            }
            while (outcome.Result.Continues && slices < 10);
            Assert.True(outcome.Result.Succeeded, outcome.Result.ErrorSummary);
            // 90 days in 15-day windows, two windows a slice.
            Assert.Equal(3, slices);

            await using (var db = fixture.CreateContext())
            {
                var state = await db.MarketplaceFinanceSyncStates.AsNoTracking().SingleAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId);
                Assert.Equal(Now.AddDays(-90), state.CoveredFrom);
                Assert.Equal(Now, state.CoveredThrough);
                Assert.Equal(Now, state.LastSyncedAt);
                Assert.Equal(20, await db.MarketplaceFinanceTransactions.CountAsync(x => x.TenantId == tenantId));
                Assert.Equal(10, await db.MarketplaceFinanceTransactions.CountAsync(x => x.TenantId == tenantId && x.Category == MarketplaceFinanceCategories.Cargo));
                Assert.Equal(20, await db.MarketplaceCargoInvoiceItems.CountAsync(x => x.TenantId == tenantId));
            }

            // The next day's run reads the last three days again: the record two days back is neither added twice nor
            // changed, and its cargo lines are not read again.
            var cargoReads = port.CargoReads;
            outcome = await RunAsync(tenantId, connectionId, port, Now.AddDays(1));
            Assert.True(outcome.Result.Succeeded, outcome.Result.ErrorSummary);
            Assert.Equal(2, outcome.Telemetry.Received);
            Assert.Equal(0, outcome.Telemetry.Inserted);
            Assert.Equal(0, outcome.Telemetry.Updated);
            await using (var db = fixture.CreateContext())
            {
                Assert.Equal(20, await db.MarketplaceFinanceTransactions.CountAsync(x => x.TenantId == tenantId));
                Assert.Equal(20, await db.MarketplaceCargoInvoiceItems.CountAsync(x => x.TenantId == tenantId));
                Assert.Equal(Now.AddDays(1), (await db.MarketplaceFinanceSyncStates.AsNoTracking().SingleAsync(x => x.TenantId == tenantId)).LastSyncedAt);
            }
            Assert.Equal(cargoReads, port.CargoReads);
        }
        finally
        {
            await CleanupAsync(tenantId);
        }
    }

    [PostgreSqlFact]
    public async Task A_window_that_fails_part_way_stores_nothing_of_it_and_opens_an_issue()
    {
        var (tenantId, connectionId) = await SeedAsync("fin-failure");
        try
        {
            // The second window's returns fail after its sales were read.
            var port = new FakeFinancePort { FailReturnsFrom = Now.AddDays(-75) };
            var outcome = await RunAsync(tenantId, connectionId, port, Now);
            Assert.False(outcome.Result.Succeeded);
            Assert.False(outcome.Result.Continues);

            await using var db = fixture.CreateContext();
            var state = await db.MarketplaceFinanceSyncStates.AsNoTracking().SingleAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId);
            // Only the first window is stored and covered.
            Assert.Equal(Now.AddDays(-75), state.CoveredThrough);
            Assert.Null(state.LastSyncedAt);
            Assert.NotNull(state.LastError);
            Assert.Equal(4, await db.MarketplaceFinanceTransactions.CountAsync(x => x.TenantId == tenantId));
            var issue = await db.OperationalIssues.AsNoTracking().SingleAsync(x => x.TenantId == tenantId && x.DedupeKey == TrendyolFinanceSyncJobHandler.FailureKey(connectionId));
            Assert.Equal(IssueStatus.Open, issue.Status);
            Assert.Equal(TrendyolFinanceSyncJobHandler.FailureCode, issue.Code);
        }
        finally
        {
            await CleanupAsync(tenantId);
        }
    }

    private async Task<JobOutcome> RunAsync(Guid tenantId, Guid connectionId, FakeFinancePort port, DateTimeOffset now)
    {
        await using var db = fixture.CreateContext();
        var handler = new TrendyolFinanceSyncJobHandler(db, port, new FixedClock(now), new ConfigurationBuilder().Build());
        return await handler.HandleAsync(new JobRequest(tenantId, connectionId, MarketplaceJobTypes.TrendyolFinanceSync, "{}", "test", null), CancellationToken.None);
    }

    private async Task<(Guid TenantId, Guid ConnectionId)> SeedAsync(string code)
    {
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"{code}-{Guid.NewGuid():N}"[..20], DisplayName = code, Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = Now, UpdatedAt = Now, Version = 1 };
        var connection = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = "TRENDYOL", Environment = "PRODUCTION", DisplayName = code, ExternalStoreId = $"store-{Guid.NewGuid():N}", Status = "ACTIVE", ApiVersion = "V1" };
        await using var db = fixture.CreateContext();
        db.Tenants.Add(tenant);
        db.PlatformConnections.Add(connection);
        await db.SaveChangesAsync();
        return (tenant.Id, connection.Id);
    }

    private async Task CleanupAsync(Guid tenantId)
    {
        await using var db = fixture.CreateContext();
        await db.MarketplaceCargoInvoiceItems.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.MarketplaceFinanceTransactions.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.MarketplaceFinanceSyncStates.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.OperationalIssues.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.IntegrationOutboxEvents.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.PlatformConnections.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.Tenants.Where(row => row.Id == tenantId).ExecuteDeleteAsync();
    }

    // A sale and a cargo invoice (with two lines) on each of ten days: every tenth day of the backfill and one two days
    // before Now, which the next day's run reads again.
    private sealed class FakeFinancePort : ITrendyolFinancePort
    {
        private static readonly DateTimeOffset[] Days = [.. Enumerable.Range(0, 9).Select(k => Now.AddDays(-90 + 10 * k).AddHours(1)), Now.AddDays(-2).AddHours(1)];

        public DateTimeOffset? FailReturnsFrom { get; init; }
        public int CargoReads { get; private set; }

        public Task<AdapterResult<RemoteStatementPage>> GetSettlementsAsync(AdapterContext context, string transactionType, DateTimeOffset start, DateTimeOffset end, int page, CancellationToken cancellationToken)
        {
            if (transactionType == "Return" && FailReturnsFrom is { } from && start >= from)
                return Task.FromResult(AdapterResult<RemoteStatementPage>.Failure(new(AdapterErrorClass.Remote5xx, "REMOTE_SERVER_ERROR", "Platform geçici sunucu hatası verdi.", 503, null, null)));
            var items = transactionType == "Sale"
                ? Days.Where(day => day >= start && day < end).Select(day => new RemoteStatementRecord($"sale-{day:yyyyMMdd}", TrendyolFinanceRequestContract.Settlements, "Sale", null, "Satış", day, "Satış",
                    $"order-{day:yyyyMMdd}", null, "BAR-1", 0m, 100m, 21.5m, 21.5m, 78.5m, null, null, $$"""{"id":"sale-{{day:yyyyMMdd}}"}""")).ToArray()
                : [];
            return Task.FromResult(AdapterResult<RemoteStatementPage>.Success(new(items, 0, 1, items.Length)));
        }

        public Task<AdapterResult<RemoteStatementPage>> GetOtherFinancialsAsync(AdapterContext context, string transactionType, string? transactionSubType, DateTimeOffset start, DateTimeOffset end, int page, CancellationToken cancellationToken)
        {
            var items = transactionType == TrendyolFinanceCategorizer.DeductionInvoices && transactionSubType is null
                ? Days.Where(day => day >= start && day < end).Select(day => new RemoteStatementRecord($"INV-{day:yyyyMMdd}", TrendyolFinanceRequestContract.OtherFinancials, transactionType, null, "Kargo Faturası", day,
                    null, null, null, null, 68.48m, 0m, null, null, null, null, null, $$"""{"id":"INV-{{day:yyyyMMdd}}"}""")).ToArray()
                : [];
            return Task.FromResult(AdapterResult<RemoteStatementPage>.Success(new(items, 0, 1, items.Length)));
        }

        public Task<AdapterResult<RemoteCargoInvoicePage>> GetCargoInvoiceItemsAsync(AdapterContext context, string invoiceSerialNumber, int page, CancellationToken cancellationToken)
        {
            CargoReads++;
            RemoteCargoInvoiceItem[] items =
            [
                new("Gönderi Kargo Bedeli", "7260001151141191", $"order-{invoiceSerialNumber[4..]}", 34.24m, 1, "{}"),
                new("İade Kargo Bedeli", "7265609146531138", $"order-{invoiceSerialNumber[4..]}", 34.24m, 1, "{}")
            ];
            return Task.FromResult(AdapterResult<RemoteCargoInvoicePage>.Success(new(items, 0, 1)));
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
