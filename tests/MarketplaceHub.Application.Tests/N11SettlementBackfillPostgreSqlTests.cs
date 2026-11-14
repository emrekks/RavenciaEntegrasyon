using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Jobs.Handlers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// After a reset n11's settlements older than the 45-day list are read once more, back to the store's oldest order, by a
// cursor of their own; a failing older read never fails the regular one.
public sealed class N11SettlementBackfillPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    // 15 Nov 2026 01:00 Istanbul: the regular list starts 1 Oct.
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 22, 0, 0, TimeSpan.Zero);

    [PostgreSqlFact]
    public async Task Older_settlements_are_read_back_to_the_oldest_order_and_a_reset_reads_them_again()
    {
        var (tenant, connection) = await SeedAsync();
        try
        {
            var port = new Port();
            port.Days[new DateOnly(2026, 10, 8)] = "478000001";
            port.Days[new DateOnly(2026, 9, 14)] = "470000001";
            port.Days[new DateOnly(2026, 8, 20)] = "460000001";

            // The regular read and the first older slice (17 Aug - 30 Sep), then the last one (11 - 16 Aug).
            var first = await RunAsync(tenant.Id, connection.Id, port);
            Assert.True(first.Result.Continues, first.Result.ErrorSummary);
            var second = await RunAsync(tenant.Id, connection.Id, port);
            Assert.True(second.Result.Succeeded, second.Result.ErrorSummary);
            Assert.Contains((new DateOnly(2026, 8, 17), new DateOnly(2026, 9, 30)), port.Lists);
            Assert.Contains((new DateOnly(2026, 8, 11), new DateOnly(2026, 8, 16)), port.Lists);
            Assert.DoesNotContain(port.Lists, x => x.Start < new DateOnly(2026, 8, 11));
            Assert.Equal(["460000001:20260820", "470000001:20260914", "478000001:20261008"], await IdsAsync(tenant.Id));

            // Done: a further run asks n11 for the regular list only.
            port.Lists.Clear();
            Assert.True((await RunAsync(tenant.Id, connection.Id, port)).Result.Succeeded);
            Assert.Single(port.Lists);

            // A reset deletes the lines and the cursor together: the history is read again.
            await using (var db = fixture.CreateContext())
            {
                await db.MarketplaceFinanceTransactions.Where(x => x.TenantId == tenant.Id).ExecuteDeleteAsync();
                await db.SyncCursors.Where(x => x.TenantId == tenant.Id).ExecuteDeleteAsync();
            }
            Assert.True((await RunAsync(tenant.Id, connection.Id, port)).Result.Continues);
            Assert.True((await RunAsync(tenant.Id, connection.Id, port)).Result.Succeeded);
            Assert.Equal(3, (await IdsAsync(tenant.Id)).Count);
        }
        finally
        {
            await CleanAsync(tenant.Id);
        }
    }

    [PostgreSqlFact]
    public async Task A_failing_older_read_keeps_the_regular_read_and_is_tried_again()
    {
        var (tenant, connection) = await SeedAsync();
        try
        {
            var port = new Port { RefuseBefore = new DateOnly(2026, 10, 1) };
            port.Days[new DateOnly(2026, 10, 8)] = "478000001";
            port.Days[new DateOnly(2026, 9, 14)] = "470000001";

            var outcome = await RunAsync(tenant.Id, connection.Id, port);
            Assert.True(outcome.Result.Succeeded, outcome.Result.ErrorSummary);
            Assert.Equal(["478000001:20261008"], await IdsAsync(tenant.Id));
            await using (var db = fixture.CreateContext())
            {
                var cursor = await db.SyncCursors.SingleAsync(x => x.TenantId == tenant.Id && x.ResourceType == N11SettlementSyncJobHandler.BackfillResource);
                Assert.Equal(1, cursor.ConsecutiveFailureCount);
                Assert.Null(cursor.LastModifiedWatermark);
                Assert.Contains("N11_REFUSED", cursor.LastError);
            }

            port.RefuseBefore = null;
            Assert.True((await RunAsync(tenant.Id, connection.Id, port)).Result.Continues);
            Assert.Contains("470000001:20260914", await IdsAsync(tenant.Id));
            await using (var db = fixture.CreateContext())
                Assert.Equal(0, (await db.SyncCursors.SingleAsync(x => x.TenantId == tenant.Id && x.ResourceType == N11SettlementSyncJobHandler.BackfillResource)).ConsecutiveFailureCount);
        }
        finally
        {
            await CleanAsync(tenant.Id);
        }
    }

    private async Task<(Tenant Tenant, PlatformConnection Connection)> SeedAsync()
    {
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"n11bf-{Guid.NewGuid():N}"[..20], DisplayName = "n11", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = Now, UpdatedAt = Now, Version = 1 };
        var connection = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = "N11", Environment = "PRODUCTION", DisplayName = "n11", ExternalStoreId = $"m-{Guid.NewGuid():N}", Status = "ACTIVE", ApiVersion = "V1" };
        // The oldest order: 11 Aug 2026 20:11 Istanbul.
        var order = new Order { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, ExternalOrderId = "n11-old", OrderNumber = "n11-old", Currency = "TRY", OrderedAt = new DateTimeOffset(2026, 8, 11, 17, 11, 0, TimeSpan.Zero), LastRemoteModifiedAt = Now, CustomerSnapshotJson = "{}", ShipmentAddressSnapshotJson = "{}", InvoiceAddressSnapshotJson = "{}", DerivedStatus = "DELIVERED", CreatedAt = Now, UpdatedAt = Now, Version = 1 };
        await using var seed = fixture.CreateContext();
        seed.Tenants.Add(tenant);
        seed.PlatformConnections.Add(connection);
        seed.Orders.Add(order);
        await seed.SaveChangesAsync();
        return (tenant, connection);
    }

    private async Task<List<string>> IdsAsync(Guid tenantId)
    {
        await using var db = fixture.CreateContext();
        return await db.MarketplaceFinanceTransactions.Where(x => x.TenantId == tenantId).OrderBy(x => x.ExternalId).Select(x => x.ExternalId).ToListAsync();
    }

    private async Task CleanAsync(Guid tenantId)
    {
        await using var db = fixture.CreateContext();
        await db.MarketplaceFinanceTransactions.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.SyncCursors.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.OperationalIssues.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.IntegrationOutboxEvents.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.Orders.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.PlatformConnections.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.Tenants.Where(row => row.Id == tenantId).ExecuteDeleteAsync();
    }

    private async Task<JobOutcome> RunAsync(Guid tenantId, Guid connectionId, Port port)
    {
        await using var db = fixture.CreateContext();
        var handler = new N11SettlementSyncJobHandler(db, port, new FixedClock(Now), new ConfigurationBuilder().Build());
        return await handler.HandleAsync(new JobRequest(tenantId, connectionId, MarketplaceJobTypes.N11SettlementSync, "{}", "test", null), CancellationToken.None);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    // n11 as seen live: only settled days are listed, each with one paid line.
    private sealed class Port : IN11SettlementPort
    {
        public Dictionary<DateOnly, string> Days { get; } = [];
        public List<(DateOnly Start, DateOnly End)> Lists { get; } = [];
        public DateOnly? RefuseBefore { get; set; }

        public Task<AdapterResult<RemoteSettlementDayPage>> GetSettlementDaysAsync(AdapterContext context, DateOnly start, DateOnly end, int page, CancellationToken cancellationToken)
        {
            Lists.Add((start, end));
            if (RefuseBefore is { } limit && start < limit)
                return Task.FromResult(AdapterResult<RemoteSettlementDayPage>.Failure(new AdapterError(AdapterErrorClass.Validation, "N11_REFUSED", "startDate geçersiz", 400, null, null)));
            var days = Days.Keys.Where(day => day >= start && day <= end).Select(day => new RemoteSettlementDay(day, day.AddDays(7), "Success", 799m, 200m, 599m)).ToList();
            return Task.FromResult(AdapterResult<RemoteSettlementDayPage>.Success(new(days, page, 1)));
        }

        public Task<AdapterResult<RemoteSettlementItemPage>> GetSettlementItemsAsync(AdapterContext context, DateOnly day, int page, CancellationToken cancellationToken)
        {
            List<RemoteSettlementItem> items = Days.TryGetValue(day, out var id)
                ? [new(id, $"o{id}", 20.34m, 0m, 0m, 0m, 0m, 200m, 799m, 599m, null, day, day.AddDays(7), day.AddDays(7), $"{{\"orderItemId\":\"{id}\"}}")]
                : [];
            return Task.FromResult(AdapterResult<RemoteSettlementItemPage>.Success(new(items, page, 1, "Success")));
        }
    }
}
