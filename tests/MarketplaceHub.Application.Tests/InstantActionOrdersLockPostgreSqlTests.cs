using System.Diagnostics;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Audit 2026-10-19 S6: the API's instant actions project an accepted marketplace write under the
// same per-connection "orders" lock the order jobs take, and never hold it around the remote call.
[Collection("postgres-integration")]
public sealed class InstantActionOrdersLockPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    [PostgreSqlFact]
    public async Task InstantPicking_ProjectsOnlyAfterTheOrderJobReleasesTheOrdersLock()
    {
        var seed = await SeedAsync("instant-lock-released");
        try
        {
            await using var holder = fixture.CreateContext();
            var key = MarketplaceSyncExecutionLock.LockKey(seed.ConnectionId, MarketplaceSyncExecutionLock.OrdersGroup);
            await holder.Database.OpenConnectionAsync();
            await AdvisoryAsync(holder, "pg_advisory_lock", key);
            var port = new TimedActionPort();
            DateTimeOffset? releasedAt = null;

            await using var db = fixture.CreateContext();
            var service = NewService(db, port);
            var stopwatch = Stopwatch.StartNew();
            var call = service.ProcessShipmentInstantAsync(seed.TenantId, seed.PackageId, 1, "instant-lock:released", "instant-lock", CancellationToken.None);
            // The order job keeps the lock for a second and writes the same package meanwhile.
            await Task.Delay(TimeSpan.FromSeconds(1));
            await holder.ShipmentPackages.Where(x => x.Id == seed.PackageId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.CargoTrackingNumber, "from-order-job").SetProperty(x => x.Version, x => x.Version + 1));
            releasedAt = DateTimeOffset.UtcNow;
            await AdvisoryAsync(holder, "pg_advisory_unlock", key);
            var result = await call;
            stopwatch.Stop();

            Assert.Null(result.Error);
            // The marketplace call was not blocked by the held lock.
            Assert.NotNull(port.CalledAt);
            Assert.True(port.CalledAt < releasedAt, $"{port.CalledAt:O} >= {releasedAt:O}");
            Assert.True(stopwatch.Elapsed >= TimeSpan.FromMilliseconds(900), stopwatch.Elapsed.ToString());
            await using var verify = fixture.CreateContext();
            var package = await verify.ShipmentPackages.AsNoTracking().SingleAsync(x => x.Id == seed.PackageId);
            // Neither write is lost: the order job's field and the projected status are both kept.
            Assert.Equal(ShipmentPackageStatus.Processing, package.Status);
            Assert.Equal("from-order-job", package.CargoTrackingNumber);
            Assert.Equal(3, package.Version);
            Assert.True(await verify.ExternalEffectRecords.AnyAsync(x => x.TenantId == seed.TenantId && x.CompletedAt != null && x.Outcome == "APPLIED"));
            Assert.Equal("PROCESSING", (await verify.Orders.AsNoTracking().SingleAsync(x => x.Id == seed.OrderId)).DerivedStatus);
        }
        finally
        {
            await CleanupAsync(seed.TenantId);
        }
    }

    [PostgreSqlFact]
    public async Task InstantPicking_ProjectsWithoutTheLockWhenTheOrderJobKeepsItPastTheWait()
    {
        var seed = await SeedAsync("instant-lock-busy");
        await using var holder = fixture.CreateContext();
        var key = MarketplaceSyncExecutionLock.LockKey(seed.ConnectionId, MarketplaceSyncExecutionLock.OrdersGroup);
        try
        {
            await holder.Database.OpenConnectionAsync();
            await AdvisoryAsync(holder, "pg_advisory_lock", key);

            await using var db = fixture.CreateContext();
            var service = NewService(db, new TimedActionPort());
            var stopwatch = Stopwatch.StartNew();
            var result = await service.ProcessShipmentInstantAsync(seed.TenantId, seed.PackageId, 1, "instant-lock:busy", "instant-lock", CancellationToken.None);
            stopwatch.Stop();

            Assert.Null(result.Error);
            Assert.True(stopwatch.Elapsed >= MarketplaceSalesService.InstantProjectionLockWait - TimeSpan.FromMilliseconds(300), stopwatch.Elapsed.ToString());
            await using var verify = fixture.CreateContext();
            Assert.Equal(ShipmentPackageStatus.Processing, (await verify.ShipmentPackages.AsNoTracking().SingleAsync(x => x.Id == seed.PackageId)).Status);
            Assert.True(await verify.ExternalEffectRecords.AnyAsync(x => x.TenantId == seed.TenantId && x.CompletedAt != null));
        }
        finally
        {
            if (holder.Database.GetDbConnection().State == System.Data.ConnectionState.Open)
                await AdvisoryAsync(holder, "pg_advisory_unlock", key);
            await CleanupAsync(seed.TenantId);
        }
    }

    [PostgreSqlFact]
    public async Task InstantPicking_QueuesATargetedOrderReadWhenTheProjectionConflictsTwice()
    {
        var seed = await SeedAsync("instant-lock-conflict");
        try
        {
            var connectionString = Environment.GetEnvironmentVariable("MARKETPLACEHUB_TEST_CONNECTION")!;
            await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(connectionString)
                .AddInterceptors(new ConcurrentPackageWriter(fixture, seed.PackageId))
                .Options);
            var service = NewService(db, new TimedActionPort());

            var result = await service.ProcessShipmentInstantAsync(seed.TenantId, seed.PackageId, 1, "instant-lock:conflict", "instant-lock", CancellationToken.None);

            Assert.Null(result.Error);
            await using var verify = fixture.CreateContext();
            Assert.True(await verify.ExternalEffectRecords.AnyAsync(x => x.TenantId == seed.TenantId && x.CompletedAt != null && x.Outcome == "APPLIED"));
            var orderSync = MarketplaceJobTypes.ForPlatform("TRENDYOL", MarketplaceJobTypes.OrderSync);
            var job = await verify.IntegrationJobs.AsNoTracking().SingleAsync(x => x.TenantId == seed.TenantId && x.JobType == orderSync);
            Assert.Contains("instant-lock-order", job.PayloadJson);
        }
        finally
        {
            await CleanupAsync(seed.TenantId);
        }
    }

    private MarketplaceSalesService NewService(AppDbContext db, IOrderPort port) =>
        new(db, null!, new ConfigurationBuilder().Build(), null!, port, null!, null!, TimeProvider.System);

    private static async Task AdvisoryAsync(AppDbContext db, string function, long key)
    {
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {function}(@key)";
        command.Parameters.AddWithValue("key", key);
        await command.ExecuteScalarAsync();
    }

    private sealed record Seed(Guid TenantId, Guid ConnectionId, Guid OrderId, Guid PackageId);

    private async Task<Seed> SeedAsync(string code)
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"{code}-{Guid.NewGuid():N}", DisplayName = code, Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var connection = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = "TRENDYOL", Environment = "STAGE", DisplayName = "Anlık işlem kilidi", ExternalStoreId = $"store-{Guid.NewGuid():N}", Status = "ACTIVE", ApiVersion = "V1" };
        var order = new Order
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            ConnectionId = connection.Id,
            ExternalOrderId = "instant-lock-order",
            OrderNumber = "instant-lock-order",
            Currency = "TRY",
            GrossAmount = 100m,
            NetAmount = 100m,
            OrderedAt = now.AddHours(-2),
            LastRemoteModifiedAt = now.AddHours(-2),
            CustomerSnapshotJson = "{}",
            ShipmentAddressSnapshotJson = "{}",
            InvoiceAddressSnapshotJson = "{}",
            DerivedStatus = "NEW",
            CreatedAt = now,
            UpdatedAt = now,
            Version = 1
        };
        var package = new ShipmentPackage
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            ConnectionId = connection.Id,
            OrderId = order.Id,
            ExternalPackageId = "instant-lock-package",
            GrossAmount = 100m,
            NetAmount = 100m,
            Status = ShipmentPackageStatus.New,
            RawStatus = "Created",
            StatusOccurredAt = now.AddHours(-2),
            MarketplaceInvoiceStatus = MarketplaceInvoiceStatus.Unknown,
            CreatedAt = now,
            UpdatedAt = now,
            Version = 1
        };
        var line = new OrderLine { Id = Guid.CreateVersion7(), TenantId = tenant.Id, OrderId = order.Id, ExternalLineId = "4242", Sku = "LOCK-1", TitleSnapshot = "Kilit testi", OrderedQuantity = 1, UnitPrice = 100m, VatRate = 20m, RawStatus = "Created" };
        await using var db = fixture.CreateContext();
        db.Tenants.Add(tenant);
        db.PlatformConnections.Add(connection);
        db.ConnectionSyncPolicies.Add(new ConnectionSyncPolicy { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, ResourceType = MarketplaceExternalWritePolicies.Shipment, IntervalSeconds = 0, Enabled = true, Version = 1 });
        db.Orders.Add(order);
        db.OrderLines.Add(line);
        db.ShipmentPackages.Add(package);
        db.PackageLineAllocations.Add(new PackageLineAllocation { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PackageId = package.Id, OrderLineId = line.Id, AllocatedQuantity = 1, SourceEventId = "seed" });
        await db.SaveChangesAsync();
        return new(tenant.Id, connection.Id, order.Id, package.Id);
    }

    // Every row and job these tests create is removed: the lease tests in the same database take any pending job.
    private async Task CleanupAsync(Guid tenantId)
    {
        await using var db = fixture.CreateContext();
        await db.JobAttempts.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.IntegrationJobs.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.ExternalEffectRecords.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.ConnectionSyncPolicies.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.IntegrationOutboxEvents.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.OperationalIssues.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.OrderStatusHistory.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.PackageLineAllocations.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.OrderLines.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.ShipmentPackages.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.Orders.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.PlatformConnections.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.Tenants.Where(row => row.Id == tenantId).ExecuteDeleteAsync();
    }

    private sealed class TimedActionPort : IOrderPort
    {
        public DateTimeOffset? CalledAt { get; private set; }
        public Task<AdapterResult<PackageActionResult>> ExecutePackageActionAsync(AdapterContext context, PackageActionCommand command, CancellationToken cancellationToken)
        {
            CalledAt = DateTimeOffset.UtcNow;
            return Task.FromResult(AdapterResult<PackageActionResult>.Success(new(command.ExternalPackageId, "Processing", null)));
        }
        public Task<AdapterResult<AdapterPageResult<RemoteOrder>>> PollAsync(AdapterContext context, OrderPollWindow window, AdapterPageRequest page, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<RemoteOrder>> GetAsync(AdapterContext context, string externalOrderId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<IReadOnlyList<RemoteCargoCompany>>> GetChangeableCargoCompaniesAsync(AdapterContext context, string externalPackageId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<IReadOnlyList<RemotePackageableLine>>> GetPackageableLineItemsAsync(AdapterContext context, string externalLineItemId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<CreateOrderPackageResult>> CreateOrderPackageAsync(AdapterContext context, CreateOrderPackageCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<bool>> CreateCommonLabelAsync(AdapterContext context, CommonLabelRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<CommonLabelDocument>> GetCommonLabelAsync(AdapterContext context, string cargoTrackingNumber, CancellationToken cancellationToken, string format = "ZPL") => throw new NotSupportedException();
        public Task<AdapterResult<StageTestOrderResult>> CreateStageTestOrderAsync(AdapterContext context, string barcode, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    // Stands in for a writer the lock does not cover: every save that projects the package finds
    // the package changed underneath it.
    private sealed class ConcurrentPackageWriter(PostgreSqlTenantIsolationFixture fixture, Guid packageId) : SaveChangesInterceptor
    {
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context?.ChangeTracker.Entries<ShipmentPackage>().Any(entry => entry.State == EntityState.Modified && entry.Entity.Id == packageId) == true)
            {
                await using var other = fixture.CreateContext();
                await other.ShipmentPackages.Where(x => x.Id == packageId)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Version, x => x.Version + 1), cancellationToken);
            }
            return await base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}
