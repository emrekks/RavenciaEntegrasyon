using System.Collections.Concurrent;
using System.Text.Json;
using MarketplaceHub.Infrastructure.Jobs;
using MarketplaceHub.Infrastructure.Persistence;
using MarketplaceHub.Infrastructure.Security;
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
            foreach (var type in types)
                JobEnqueuer.Add(seed, new IntegrationJobRequest(tenant.Id, store, type, "{}", $"cap:{type}", "test", past) { Priority = 4 });
            JobEnqueuer.Add(seed, new IntegrationJobRequest(tenant.Id, other, MarketplaceJobTypes.StockReconciliation, "{}", "cap:other", "test", past) { Priority = 4 });
            JobEnqueuer.Add(seed, new IntegrationJobRequest(tenant.Id, store, MarketplaceJobTypes.N11OrderSync, "{}", "cap:orders", "test", past) { Priority = 0 });
            JobEnqueuer.Add(seed, new IntegrationJobRequest(tenant.Id, store, MarketplaceJobTypes.ShipmentAction, "{}", "cap:user", "test", past) { Priority = JobPriorityPolicy.UserAction });
            await seed.SaveChangesAsync();

            // Two of the store's background reads, then the cap holds its other two; the other store's read still goes.
            var leased = new List<LeasedJob>();
            for (var index = 0; index < 4; index++)
                if (await LeaseOwn(background, cap) is { } job) leased.Add(job);
            Assert.Equal(2, leased.Count(job => job.ConnectionId == store));
            Assert.Equal(1, leased.Count(job => job.ConnectionId == other));
            // Order reads and user work are not capped (they are not counted either).
            Assert.NotNull(await LeaseOwn(orders, cap));
            Assert.NotNull(await LeaseOwn(user, cap));
            // Without the cap (the earlier lane models) the store's third read is leased.
            Assert.NotNull(await LeaseOwn(background, null));

            async Task<LeasedJob?> LeaseOwn(WorkerLane lane, JobConnectionCap? connectionCap)
            {
                await using var db = fixture.CreateContext();
                // Only this test's tenant: other tests' jobs in the shared database are never touched.
                var candidates = await db.IntegrationJobs.AsNoTracking().Where(job => job.TenantId == tenant.Id && (job.Status == Domain.JobStatus.Pending || job.Status == Domain.JobStatus.RetryScheduled))
                    .Select(job => job.JobType).Distinct().ToListAsync();
                var included = lane.IncludedJobTypes.Count == 0 ? candidates.Where(type => !lane.ExcludedJobTypes.Contains(type)).ToArray() : lane.IncludedJobTypes.Intersect(candidates).ToArray();
                if (included.Length == 0) return null;
                return await new JobLeaseService(db, fixture.TokenHasher, fixture.TimeProvider).TryLeaseAsync(TimeSpan.FromMinutes(2), lane.MaximumPriority, lane.MinimumPriority, CancellationToken.None,
                    includedJobTypes: included, connectionCap: connectionCap, onlyJobId: null, onlyJobType: null,
                    excludedJobTypes: [.. lane.ExcludedJobTypes]);
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
