using System.Collections.Concurrent;
using System.Text.Json;
using MarketplaceHub.Infrastructure.Jobs;
using MarketplaceHub.Infrastructure.Persistence;
using MarketplaceHub.Infrastructure.Security;
using MarketplaceHub.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;
using WorkerService = MarketplaceHub.Worker.Worker;

namespace MarketplaceHub.Application.Tests;

// Queue phase 2 (27 Nov 2026), the dispatcher against PostgreSQL: the per-store cap in the lease query, the census query,
// and the real Worker with Worker:LaneModel=dispatcher over a mixed stream, against the same stream on today's flexible lanes.
// The end-to-end test runs each model in a database of its own (created and dropped here), so no other test's job is leased.
[Collection("postgres-integration")]
public sealed class DispatcherPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture, ITestOutputHelper output) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    [PostgreSqlFact]
    public async Task TheLeaseHoldsAStoresCappedJobsAtTheCapAndNeverItsOrderReadsOrUserWork()
    {
        var tenant = ScheduledPolicyIsolationTests.NewTenant("dispatcher-cap");
        var store = Guid.NewGuid();
        var other = Guid.NewGuid();
        var cap = DispatcherLanes.ConnectionCap(DispatcherSettings.Default);
        var background = DispatcherLanes.All().First(lane => lane.Name == DispatcherLanes.Background);
        var orders = DispatcherLanes.All().First(lane => lane.Name == DispatcherLanes.OrderReads);
        var user = DispatcherLanes.All().First(lane => lane.Name == DispatcherLanes.User);
        await using var seed = fixture.CreateContext();
        try
        {
            seed.Tenants.Add(tenant);
            var past = DateTimeOffset.UtcNow.AddMinutes(-1);
            // Background reads of four lock groups for one store, one for another store, an order read and a user action.
            string[] types = [MarketplaceJobTypes.ReturnReconciliation, MarketplaceJobTypes.StockReconciliation, MarketplaceJobTypes.TrendyolFinanceSync, MarketplaceJobTypes.ReferenceSync];
            Guid Add(Guid connection, string type, int priority, string key)
            {
                var id = Guid.NewGuid();
                JobEnqueuer.Add(seed, new IntegrationJobRequest(tenant.Id, connection, type, "{}", $"cap:{key}", "test", past) { Priority = priority, Id = id });
                return id;
            }
            var storeReads = types.Select(type => Add(store, type, 4, type)).ToArray();
            var otherRead = Add(other, MarketplaceJobTypes.StockReconciliation, 4, "other");
            var orderRead = Add(store, MarketplaceJobTypes.N11OrderSync, 0, "orders");
            var userAction = Add(store, MarketplaceJobTypes.ShipmentAction, JobPriorityPolicy.UserAction, "user");
            await seed.SaveChangesAsync();

            // Each lease names its job (onlyJobId): other tests' jobs in the shared database are never leased.
            // Three of the store's background reads (the default cap), then the cap holds the fourth; the other store's read still goes.
            Assert.Equal(3, cap.Cap);
            Assert.NotNull(await Lease(storeReads[0], background, cap));
            Assert.NotNull(await Lease(storeReads[1], background, cap));
            Assert.NotNull(await Lease(storeReads[2], background, cap));
            Assert.Null(await Lease(storeReads[3], background, cap));
            Assert.NotNull(await Lease(otherRead, background, cap));
            // Order reads and user work are not capped (and do not count).
            Assert.NotNull(await Lease(orderRead, orders, cap));
            Assert.NotNull(await Lease(userAction, user, cap));
            // Without the cap (the earlier lane models) the store's fourth read is leased.
            Assert.NotNull(await Lease(storeReads[3], background, null));

            async Task<LeasedJob?> Lease(Guid jobId, WorkerLane lane, JobConnectionCap? connectionCap)
            {
                await using var db = fixture.CreateContext();
                return await new JobLeaseService(db, fixture.TokenHasher, fixture.TimeProvider).TryLeaseAsync(TimeSpan.FromMinutes(2), lane.MaximumPriority, lane.MinimumPriority, CancellationToken.None,
                    includedJobTypes: [.. lane.IncludedJobTypes], excludedJobTypes: [.. lane.ExcludedJobTypes], connectionCap: connectionCap, onlyJobId: jobId);
            }
        }
        finally
        {
            await seed.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM integration.job_attempts WHERE \"TenantId\" = {tenant.Id}");
            await seed.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM integration.outbox_events WHERE \"TenantId\" = {tenant.Id}");
            await seed.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM integration.jobs WHERE \"TenantId\" = {tenant.Id}");
            await seed.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM iam.tenants WHERE \"Id\" = {tenant.Id}");
        }
    }

    [PostgreSqlFact]
    public async Task TheCensusListsRunnableJobsAndLeavesOutABackedOffConnection()
    {
        var prefix = $"TEST_CENSUS_{Guid.NewGuid():N}"[..30];
        var tenant = ScheduledPolicyIsolationTests.NewTenant("dispatcher-census");
        var backedOff = Guid.NewGuid();
        await using var seed = fixture.CreateContext();
        try
        {
            seed.Tenants.Add(tenant);
            var now = DateTimeOffset.UtcNow;
            void Add(string name, DateTimeOffset availableAt, int priority = 4, Guid? connection = null, string? lockGroup = null) =>
                JobEnqueuer.Add(seed, new IntegrationJobRequest(tenant.Id, connection, $"{prefix}_{name}", "{}", $"census:{name}", "test", availableAt) { Priority = priority, LockGroup = lockGroup });
            Add("NOW", now.AddMinutes(-1));
            Add("USER", now.AddMinutes(-1), priority: -3);
            Add("LATER", now.AddMinutes(5));
            Add("BACKOFF", now.AddMinutes(-1), connection: backedOff, lockGroup: "test");
            Add("OTHERGROUP", now.AddMinutes(-1), connection: backedOff, lockGroup: "other");
            await seed.SaveChangesAsync();
            await seed.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO integration.connection_backoffs ("ConnectionId", "LockGroup", "Until", "Reason") VALUES ({backedOff}, 'test', {now.AddMinutes(2)}, 'test')
                """);
            await using var db = fixture.CreateContext();
            var kinds = (await QueueHealthService.RunnableJobKindsAsync(db, now, CancellationToken.None))
                .Where(kind => kind.JobType.StartsWith(prefix, StringComparison.Ordinal))
                .Select(kind => $"{kind.JobType[(prefix.Length + 1)..]}:{kind.Priority}").Order(StringComparer.Ordinal).ToArray();
            Assert.Equal(["NOW:4", "OTHERGROUP:4", "USER:-3"], kinds);
        }
        finally
        {
            await seed.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM integration.connection_backoffs WHERE \"ConnectionId\" = {backedOff}");
            await seed.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM integration.outbox_events WHERE \"TenantId\" = {tenant.Id}");
            await seed.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM integration.jobs WHERE \"TenantId\" = {tenant.Id}");
            await seed.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM iam.tenants WHERE \"Id\" = {tenant.Id}");
        }
    }

    // Review of 27 Nov (C1): the census names the store that alone holds a kind (a running job in its lock group, an order read
    // queued ahead of a status scan or a reconciliation in the same lock group), and the lease refuses the deferred job for the
    // same reason; a kind nothing holds is free (HeldBy null).
    [PostgreSqlFact]
    public async Task TheCensusNamesTheStoreThatHoldsAKindAndTheLeaseDefersForTheSameReason()
    {
        var tenant = ScheduledPolicyIsolationTests.NewTenant("dispatcher-held");
        Guid busyStore = Guid.NewGuid(), orderStore = Guid.NewGuid(), freeStore = Guid.NewGuid();
        Assert.Equal(MarketplaceSyncExecutionLock.GroupFor(MarketplaceJobTypes.N11OrderSync), MarketplaceSyncExecutionLock.GroupFor(MarketplaceJobTypes.OrderReconciliation));
        Assert.Equal(MarketplaceSyncExecutionLock.GroupFor(MarketplaceJobTypes.N11OrderSync), MarketplaceSyncExecutionLock.GroupFor(MarketplaceJobTypes.OrderStatusSync));
        var settings = DispatcherSettings.Default;
        await using var seed = fixture.CreateContext();
        try
        {
            seed.Tenants.Add(tenant);
            var now = DateTimeOffset.UtcNow;
            Guid Add(Guid connection, string type, int priority, string key)
            {
                var id = Guid.NewGuid();
                JobEnqueuer.Add(seed, new IntegrationJobRequest(tenant.Id, connection, type, "{}", $"held:{key}", "test", now.AddMinutes(-1)) { Priority = priority, Id = id });
                return id;
            }
            var running = Add(busyStore, MarketplaceJobTypes.N11OrderSync, 0, "running");
            Add(busyStore, MarketplaceJobTypes.OrderStatusSync, 0, "busy-scan");
            var orderRead = Add(orderStore, MarketplaceJobTypes.N11OrderSync, 0, "order-read");
            Add(orderStore, MarketplaceJobTypes.OrderStatusSync, 0, "deferred-scan");
            var reconciliation = Add(orderStore, MarketplaceJobTypes.OrderReconciliation, 4, "deferred-reconciliation");
            Add(freeStore, MarketplaceJobTypes.StockReconciliation, 4, "free");
            await seed.SaveChangesAsync();
            await seed.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE integration.jobs SET "Status" = 'LEASED', "LeaseExpiresAt" = {now.AddMinutes(2)} WHERE "Id" = {running}
                """);

            await using var db = fixture.CreateContext();
            var bulkhead = BulkheadSettings.From(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Worker:LaneModel"] = "dispatcher" }).Build(), true)
                .For(DispatcherLanes.OrderReads, WorkerLanes.DefaultHotPriorityCeiling);
            var rows = await QueueHealthService.RunnableJobKindsAsync(db, now, CancellationToken.None, DispatcherLanes.ConnectionCap(settings),
                DispatcherLanes.DeferredRules(settings), DispatcherLanes.DeferralFor(DispatcherLanes.StatusScans, settings), bulkhead);
            var mine = rows.Where(row => row.ConnectionId is { } id && (id == busyStore || id == orderStore || id == freeStore))
                .Select(row => $"{Name(row.ConnectionId)}:{row.JobType}:{Name(row.HeldBy)}").Order(StringComparer.Ordinal).ToArray();
            Assert.Equal(new[]
            {
                $"busy:{MarketplaceJobTypes.OrderStatusSync}:busy",
                $"free:{MarketplaceJobTypes.StockReconciliation}:-",
                $"orders:{MarketplaceJobTypes.N11OrderSync}:-",
                $"orders:{MarketplaceJobTypes.OrderReconciliation}:orders",
                $"orders:{MarketplaceJobTypes.OrderStatusSync}:orders"
            }.Order(StringComparer.Ordinal), mine);

            // The lease defers the reconciliation for the store's queued order read, and takes it without the deferral.
            var background = DispatcherLanes.All().First(lane => lane.Name == DispatcherLanes.Background);
            Assert.Null(await Lease(reconciliation, background, DispatcherLanes.DeferralFor(DispatcherLanes.Background, settings)));
            Assert.NotNull(await Lease(reconciliation, background, null));
            // The order read now waits for the reconciliation's lock group (the lease's own busy rule), not for a deferral.
            Assert.Null(await Lease(orderRead, DispatcherLanes.All().First(lane => lane.Name == DispatcherLanes.OrderReads), null));

            string Name(Guid? id) => id == busyStore ? "busy" : id == orderStore ? "orders" : id == freeStore ? "free" : id is null ? "-" : "other";
            async Task<LeasedJob?> Lease(Guid jobId, WorkerLane lane, JobDeferral? deferral)
            {
                await using var leaseDb = fixture.CreateContext();
                return await new JobLeaseService(leaseDb, fixture.TokenHasher, fixture.TimeProvider).TryLeaseAsync(TimeSpan.FromMinutes(2), lane.MaximumPriority, lane.MinimumPriority, CancellationToken.None,
                    includedJobTypes: [.. lane.IncludedJobTypes], excludedJobTypes: [.. lane.ExcludedJobTypes], onlyJobId: jobId, deferral: deferral);
            }
        }
        finally
        {
            await seed.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM integration.job_attempts WHERE \"TenantId\" = {tenant.Id}");
            await seed.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM integration.outbox_events WHERE \"TenantId\" = {tenant.Id}");
            await seed.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM integration.jobs WHERE \"TenantId\" = {tenant.Id}");
            await seed.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM iam.tenants WHERE \"Id\" = {tenant.Id}");
        }
    }

    // Second correctness review (28 Nov, M1): only a new-order read the lease could take now defers a status scan. A recovery
    // read (priority 6, the kind the quota hold holds) or a first-import read (priority -1) of the store does not; a runnable
    // new-order read does, the census says so too (HeldBy), and once that read has run the status scan is leased.
    [PostgreSqlFact]
    public async Task OnlyARunnableNewOrderReadDefersAStatusScan()
    {
        var tenant = ScheduledPolicyIsolationTests.NewTenant("dispatcher-defer");
        Guid recoveryStore = Guid.NewGuid(), initialStore = Guid.NewGuid(), orderStore = Guid.NewGuid();
        var settings = DispatcherSettings.Default;
        var statusLane = DispatcherLanes.All().First(lane => lane.Name == DispatcherLanes.StatusScans);
        var ordersLane = DispatcherLanes.All().First(lane => lane.Name == DispatcherLanes.OrderReads);
        var deferral = DispatcherLanes.DeferralFor(DispatcherLanes.StatusScans, settings);
        Assert.NotNull(deferral);
        // Each status scan shares its lock group with the read next to it: only the rule decides.
        Assert.Equal(MarketplaceSyncExecutionLock.GroupFor(MarketplaceJobTypes.PttAvmOrderRecoverySync), MarketplaceSyncExecutionLock.GroupFor(MarketplaceJobTypes.PttAvmOrderStatusSync));
        Assert.Equal(MarketplaceSyncExecutionLock.GroupFor(MarketplaceJobTypes.N11OrderSync), MarketplaceSyncExecutionLock.GroupFor(MarketplaceJobTypes.OrderStatusSync));
        await using var seed = fixture.CreateContext();
        try
        {
            seed.Tenants.Add(tenant);
            var now = DateTimeOffset.UtcNow;
            Guid Add(Guid connection, string type, int priority, string key)
            {
                var id = Guid.NewGuid();
                JobEnqueuer.Add(seed, new IntegrationJobRequest(tenant.Id, connection, type, "{}", $"defer:{key}", "test", now.AddMinutes(-1)) { Priority = priority, Id = id });
                return id;
            }
            Add(recoveryStore, MarketplaceJobTypes.PttAvmOrderRecoverySync, JobPriorityPolicy.Recovery, "recovery");
            var recoveryScan = Add(recoveryStore, MarketplaceJobTypes.PttAvmOrderStatusSync, 0, "recovery-scan");
            Add(initialStore, MarketplaceJobTypes.N11OrderSync, JobPriorityPolicy.InitialDataSync, "initial");
            var initialScan = Add(initialStore, MarketplaceJobTypes.OrderStatusSync, 0, "initial-scan");
            var orderRead = Add(orderStore, MarketplaceJobTypes.N11OrderSync, 0, "order-read");
            var orderScan = Add(orderStore, MarketplaceJobTypes.OrderStatusSync, 0, "order-scan");
            await seed.SaveChangesAsync();

            await using (var db = fixture.CreateContext())
            {
                var rows = await QueueHealthService.RunnableJobKindsAsync(db, now, CancellationToken.None, DispatcherLanes.ConnectionCap(settings),
                    DispatcherLanes.DeferredRules(settings), deferral, null);
                Guid? HeldBy(Guid store, string type) => rows.Single(row => row.ConnectionId == store && row.JobType == type).HeldBy;
                Assert.Null(HeldBy(recoveryStore, MarketplaceJobTypes.PttAvmOrderStatusSync));
                Assert.Null(HeldBy(initialStore, MarketplaceJobTypes.OrderStatusSync));
                Assert.Equal(orderStore, HeldBy(orderStore, MarketplaceJobTypes.OrderStatusSync));
            }
            // The lease agrees with the census.
            Assert.NotNull(await Lease(recoveryScan, statusLane, deferral));
            Assert.NotNull(await Lease(initialScan, statusLane, deferral));
            Assert.Null(await Lease(orderScan, statusLane, deferral));
            // The order read runs and ends; the status scan goes next.
            Assert.NotNull(await Lease(orderRead, ordersLane, null));
            await seed.Database.ExecuteSqlInterpolatedAsync($"UPDATE integration.jobs SET \"Status\" = 'SUCCEEDED', \"LeaseExpiresAt\" = NULL WHERE \"Id\" = {orderRead}");
            Assert.NotNull(await Lease(orderScan, statusLane, deferral));

            async Task<LeasedJob?> Lease(Guid jobId, WorkerLane lane, JobDeferral? jobDeferral)
            {
                await using var leaseDb = fixture.CreateContext();
                return await new JobLeaseService(leaseDb, fixture.TokenHasher, fixture.TimeProvider).TryLeaseAsync(TimeSpan.FromMinutes(2), lane.MaximumPriority, lane.MinimumPriority, CancellationToken.None,
                    includedJobTypes: [.. lane.IncludedJobTypes], excludedJobTypes: [.. lane.ExcludedJobTypes], onlyJobId: jobId,
                    connectionCap: DispatcherLanes.ConnectionCap(settings), deferral: jobDeferral);
            }
        }
        finally
        {
            await seed.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM integration.job_attempts WHERE \"TenantId\" = {tenant.Id}");
            await seed.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM integration.outbox_events WHERE \"TenantId\" = {tenant.Id}");
            await seed.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM integration.jobs WHERE \"TenantId\" = {tenant.Id}");
            await seed.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM iam.tenants WHERE \"Id\" = {tenant.Id}");
        }
    }

    // Load-lane check of 28 Nov: the deferral was a per-candidate EXISTS over integration.jobs, so on a 200 000-job queue a
    // background lease with the cap and the deferral ran past the 30 s command timeout (the same lease without them took
    // ~1.3 s). The connections and lock groups with a job ahead are now read once (deferring_pairs). On a 40 000-job queue
    // with a new-order read on every tenth connection, the lease with the cap and the deferral costs at most a few times the
    // lease without them, and the census answers.
    [PostgreSqlFact]
    public async Task TheDeferralKeepsALeaseOnALargeQueueAsCheapAsOneWithoutIt()
    {
        var tenants = Enumerable.Range(0, 10).Select(index => ScheduledPolicyIsolationTests.NewTenant($"dispatcher-large-{index}")).ToList();
        var tenantIds = tenants.Select(tenant => tenant.Id).ToArray();
        var correlation = $"large-{Guid.NewGuid():N}";
        await using var seed = fixture.CreateContext();
        try
        {
            seed.Tenants.AddRange(tenants);
            await seed.SaveChangesAsync();
            string[] types = [MarketplaceJobTypes.StockReconciliation, MarketplaceJobTypes.OrderReconciliation, MarketplaceJobTypes.TrendyolFinanceSync, MarketplaceJobTypes.OrderStatusSync, MarketplaceJobTypes.ReferenceSync];
            await seed.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO integration.jobs ("Id", "TenantId", "ConnectionId", "JobType", "PayloadJson", "PayloadVersion", "PayloadHash", "JobDedupKey", "EffectIdempotencyKey", "Priority", "Status", "AvailableAt", "AttemptCount", "CorrelationId", "Version", "CreatedAt")
                SELECT gen_random_uuid(), ({tenantIds}::uuid[])[1 + (n % 10)], md5({correlation} || (n % 400))::uuid,
                       CASE WHEN n % 4000 < 40 THEN {MarketplaceJobTypes.OrderSync} ELSE ({types}::text[])[1 + (n % 5)] END, chr(123) || chr(125), 1, 'h', {correlation} || ':' || n, {correlation} || ':' || n,
                       CASE WHEN n % 4000 < 40 OR n % 5 = 3 THEN 0 ELSE 4 END, 'PENDING', now() - (n || ' seconds')::interval, 0, {correlation}, 0, now() - (n || ' seconds')::interval
                FROM generate_series(1, 40000) AS n
                """);
            await seed.Database.ExecuteSqlRawAsync("ANALYZE integration.jobs");
            var settings = DispatcherSettings.Default;
            var background = DispatcherLanes.All().First(lane => lane.Name == DispatcherLanes.Background);
            async Task<double> MedianLeaseMs(bool withDeferral)
            {
                var times = new List<double>();
                for (var index = 0; index < 7; index++)
                {
                    await using var db = fixture.CreateContext();
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    var job = await new JobLeaseService(db, fixture.TokenHasher, fixture.TimeProvider).TryLeaseAsync(TimeSpan.FromMinutes(2), background.MaximumPriority, background.MinimumPriority, CancellationToken.None,
                        includedJobTypes: [.. background.IncludedJobTypes], excludedJobTypes: [.. background.ExcludedJobTypes],
                        connectionCap: withDeferral ? DispatcherLanes.ConnectionCap(settings) : null, deferral: withDeferral ? DispatcherLanes.DeferralFor(DispatcherLanes.Background, settings) : null);
                    times.Add(watch.Elapsed.TotalMilliseconds);
                    Assert.NotNull(job);
                }
                times.Sort();
                return times[times.Count / 2];
            }
            await MedianLeaseMs(false);
            var plain = await MedianLeaseMs(false);
            var deferred = await MedianLeaseMs(true);
            await using var censusDb = fixture.CreateContext();
            var censusWatch = System.Diagnostics.Stopwatch.StartNew();
            var rows = await QueueHealthService.RunnableJobKindsAsync(censusDb, DateTimeOffset.UtcNow, CancellationToken.None, DispatcherLanes.ConnectionCap(settings),
                DispatcherLanes.DeferredRules(settings), DispatcherLanes.DeferralFor(DispatcherLanes.StatusScans, settings), null);
            output.WriteLine($"40 000 jobs: lease {plain:0} ms without, {deferred:0} ms with the cap and the deferral; census {censusWatch.Elapsed.TotalMilliseconds:0} ms, {rows.Count} rows");
            Assert.NotEmpty(rows);
            Assert.True(deferred <= (plain * 3) + 500, $"the lease with the deferral took {deferred:0} ms against {plain:0} ms without it");
        }
        finally
        {
            await seed.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM integration.job_attempts WHERE \"TenantId\" = ANY({tenantIds})");
            await seed.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM integration.outbox_events WHERE \"TenantId\" = ANY({tenantIds})");
            await seed.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM integration.jobs WHERE \"TenantId\" = ANY({tenantIds})");
            await seed.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM iam.tenants WHERE \"Id\" = ANY({tenantIds})");
        }
    }

    // The real Worker (ExecuteAsync: the dispatcher or today's lane loops, the notification listener, the due-time wake, the
    // reaper) over a mixed stream of 48 jobs on four stores: user actions, webhooks, writes, order reads, status scans, return
    // reads, catalogue reads and background reads; some fail once and are retried, some stop as blocked. The job processor is
    // a recorder (nothing is sent anywhere). Both models must end every job the same way, run each job as often (a write
    // exactly once), and the dispatcher must never run more than two capped jobs of a store at once.
    [PostgreSqlFact]
    public async Task TheRealWorkerEndsAMixedStreamTheSameWayWithTheDispatcherAsWithTodaysLanes()
    {
        var flexible = await RunWorkerAsync(WorkerLaneModel.Flexible);
        var dispatcher = await RunWorkerAsync(WorkerLaneModel.Dispatcher);
        output.WriteLine($"flexible: {flexible.Elapsed}, dispatcher: {dispatcher.Elapsed}; dispatcher max capped per store {dispatcher.MaxCappedPerStore}, flexible {flexible.MaxCappedPerStore}");
        Assert.Equal(flexible.Statuses, dispatcher.Statuses);
        Assert.Equal(flexible.Runs, dispatcher.Runs);
        Assert.All(dispatcher.Statuses.Values, status => Assert.Contains(status, new[] { "SUCCEEDED", "BLOCKED" }));
        Assert.True(dispatcher.MaxCappedPerStore <= DispatcherSettings.DefaultConnectionCap, $"a store ran {dispatcher.MaxCappedPerStore} capped jobs at once");
        // The attempts name the dispatcher's lanes.
        Assert.All(dispatcher.Lanes, lane => Assert.True(DispatcherLanes.IsDispatcherLane(lane), lane));
        Assert.DoesNotContain(flexible.Lanes, DispatcherLanes.IsDispatcherLane);
    }

    private sealed record WorkerRun(Dictionary<string, string> Statuses, Dictionary<string, int> Runs, int MaxCappedPerStore, TimeSpan Elapsed, IReadOnlyList<string> Lanes);

    private static async Task<WorkerRun> RunWorkerAsync(string model)
    {
        var baseConnection = new Npgsql.NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("MARKETPLACEHUB_TEST_CONNECTION"));
        var database = $"mh_dispatch_{model}_{Guid.NewGuid():N}"[..40];
        var admin = new Npgsql.NpgsqlConnectionStringBuilder(baseConnection.ConnectionString) { Database = "postgres", Pooling = false };
        await using (var connection = new Npgsql.NpgsqlConnection(admin.ConnectionString))
        {
            await connection.OpenAsync();
            await using var create = new Npgsql.NpgsqlCommand($"CREATE DATABASE \"{database}\"", connection);
            await create.ExecuteNonQueryAsync();
        }
        var connectionString = new Npgsql.NpgsqlConnectionStringBuilder(baseConnection.ConnectionString) { Database = database, MaxPoolSize = 60 }.ConnectionString;
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString).Options;
        try
        {
            await using (var db = new AppDbContext(options)) await db.Database.MigrateAsync();
            var tenant = ScheduledPolicyIsolationTests.NewTenant("dispatcher-e2e");
            var stores = Enumerable.Range(0, 4).Select(index => new Guid($"00000000-0000-0000-0000-00000000000{index + 1}")).ToArray();
            var plan = Stream(stores);
            await using (var seed = new AppDbContext(options))
            {
                seed.Tenants.Add(tenant);
                foreach (var job in plan)
                    JobEnqueuer.Add(seed, new IntegrationJobRequest(tenant.Id, job.Store, job.JobType, JsonSerializer.Serialize(new { job.Name, job.Outcome, job.Milliseconds }), $"e2e:{job.Name}", "test", DateTimeOffset.UtcNow.AddSeconds(-1)) { Priority = job.Priority });
                await seed.SaveChangesAsync();
            }

            var processor = new RecordingProcessor();
            var services = new ServiceCollection()
                .AddScoped(_ => new AppDbContext(options))
                .AddSingleton(new TokenHasher(System.Text.Encoding.UTF8.GetBytes("dispatcher-e2e-test-key-32-bytes!!!")))
                .AddSingleton(TimeProvider.System)
                .AddScoped<IJobLeaseService>(provider => new JobLeaseService(provider.GetRequiredService<AppDbContext>(), provider.GetRequiredService<TokenHasher>(), TimeProvider.System))
                .AddSingleton<IMarketplaceJobProcessor>(processor)
                .BuildServiceProvider();
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Worker:LaneModel"] = model,
                ["Worker:EnableScheduler"] = "false",
                ["Worker:HealthFile"] = Path.Combine(Path.GetTempPath(), $"mh-dispatch-e2e-{Guid.NewGuid():N}")
            }).Build();
            var worker = new WorkerService(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<WorkerService>.Instance, configuration, new Lifetime());
            var started = System.Diagnostics.Stopwatch.StartNew();
            await worker.StartAsync(CancellationToken.None);
            try
            {
                var until = DateTime.UtcNow.AddSeconds(90);
                while (DateTime.UtcNow < until)
                {
                    await using var db = new AppDbContext(options);
                    var open = await db.IntegrationJobs.CountAsync(job => job.Status != Domain.JobStatus.Succeeded && job.Status != Domain.JobStatus.Blocked);
                    if (open == 0) break;
                    await Task.Delay(200);
                }
            }
            finally
            {
                await worker.StopAsync(CancellationToken.None);
            }
            await using (var db = new AppDbContext(options))
            {
                var statuses = await db.IntegrationJobs.AsNoTracking().Select(job => new { job.JobDedupKey, job.Status }).ToListAsync();
                var lanes = await db.JobAttempts.AsNoTracking().Where(attempt => attempt.Lane != null).Select(attempt => attempt.Lane!).Distinct().ToListAsync();
                return new WorkerRun(statuses.ToDictionary(job => job.JobDedupKey, job => job.Status.ToString().ToUpperInvariant()),
                    processor.Runs.ToDictionary(item => $"e2e:{item.Key}", item => item.Value), processor.MaxCappedPerStore, started.Elapsed, lanes);
            }
        }
        finally
        {
            Npgsql.NpgsqlConnection.ClearAllPools();
            await using var connection = new Npgsql.NpgsqlConnection(admin.ConnectionString);
            await connection.OpenAsync();
            await using var drop = new Npgsql.NpgsqlCommand($"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)", connection);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private sealed record PlannedJob(string Name, Guid Store, string JobType, int Priority, string Outcome, int Milliseconds);

    private static List<PlannedJob> Stream(Guid[] stores)
    {
        var jobs = new List<PlannedJob>();
        for (var index = 0; index < stores.Length; index++)
        {
            var store = stores[index];
            void Add(string type, int priority, string outcome = "success", int milliseconds = 150) => jobs.Add(new PlannedJob($"{index}:{type}:{jobs.Count}", store, type, priority, outcome, milliseconds));
            Add(MarketplaceJobTypes.ShipmentAction, JobPriorityPolicy.UserAction);
            Add(MarketplaceJobTypes.WebhookIngest, 0, milliseconds: 30);
            Add(MarketplaceJobTypes.ProductUpdate, 1);
            Add(MarketplaceJobTypes.N11OrderSync, 0, index == 1 ? "retry-once" : "success");
            Add(MarketplaceJobTypes.OrderStatusSync, 0);
            Add(MarketplaceJobTypes.ReturnSync, 2);
            Add(MarketplaceJobTypes.ProductSync, 5, milliseconds: 800);
            Add(MarketplaceJobTypes.QuestionSync, 3, index == 2 ? "blocked" : "success");
            Add(MarketplaceJobTypes.OrderReconciliation, 4, milliseconds: 600);
            Add(MarketplaceJobTypes.StockReconciliation, 4, milliseconds: 600);
            Add(MarketplaceJobTypes.TrendyolFinanceSync, 4, index == 3 ? "retry-once" : "success", 600);
            Add(MarketplaceJobTypes.ReferenceSync, 5, milliseconds: 600);
        }
        return jobs;
    }

    // Records each run; succeeds, fails once (retried after its delay), or stops as blocked, as the job's payload says.
    private sealed class RecordingProcessor : IMarketplaceJobProcessor
    {
        private readonly ConcurrentDictionary<Guid, int> runningPerStore = new();
        private readonly object gate = new();
        public ConcurrentDictionary<string, int> Runs { get; } = new();
        public int MaxCappedPerStore { get; private set; }

        public async Task<JobExecutionResult> ProcessAsync(Guid tenantId, Guid? connectionId, string jobType, string payloadJson, string correlationId, CancellationToken cancellationToken, Guid? jobId = null)
        {
            var payload = JsonSerializer.Deserialize<JsonElement>(payloadJson);
            var name = payload.GetProperty("Name").GetString()!;
            var outcome = payload.GetProperty("Outcome").GetString();
            var run = Runs.AddOrUpdate(name, 1, (_, count) => count + 1);
            var capped = DispatcherSettings.Default.IsCapped(DispatcherLanes.LaneFor(jobType, JobPriorityPolicy.For(jobType, false)));
            if (capped && connectionId is { } store)
                lock (gate) MaxCappedPerStore = Math.Max(MaxCappedPerStore, runningPerStore.AddOrUpdate(store, 1, (_, count) => count + 1));
            try
            {
                await Task.Delay(payload.GetProperty("Milliseconds").GetInt32(), cancellationToken);
            }
            finally
            {
                if (capped && connectionId is { } ended) runningPerStore.AddOrUpdate(ended, 0, (_, count) => count - 1);
            }
            return outcome switch
            {
                "retry-once" when run == 1 => JobExecutionResult.Retry("TEST_RETRY_ONCE", "test", TimeSpan.FromSeconds(1)),
                "blocked" => JobExecutionResult.Blocked("TEST_BLOCKED", "test"),
                _ => JobExecutionResult.Success()
            };
        }
    }

    private sealed class Lifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }
}
