using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Jobs;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Worker lanes step 2 (19 Nov 2026): connection fairness (JobFairness, ConnectionLeaseClock) and per-marketplace bulkheads
// (JobBulkhead, WorkerBulkheads). No outcome depends on the wall clock's pace: every job's age is set far from the limits.
public sealed class WorkerLaneFairnessTests
{
    [Fact]
    public void TheDefaultBulkheadsMatchTheDedicatedLanesAndSettingsChangeOrDropThem()
    {
        var defaults = MarketplaceHub.Worker.BulkheadSettings.From(new ConfigurationBuilder().Build());
        Assert.True(defaults.Enabled);
        Assert.Empty(defaults.LaneCaps);
        var hot = defaults.For("hot")!;
        Assert.Null(hot.LaneCap);
        Assert.Equal(["pazarama-orders", "pttavm-orders", "hepsiburada-orders", "hepsiburada-status", "order-status"], hot.ClassNames);
        Assert.All(hot.ClassCaps, cap => Assert.Equal(1, cap));
        // Each dedicated lane has one slot today: the class cap is the same isolation.
        foreach (var bulkheadClass in WorkerBulkheads.DefaultClasses)
        {
            var lane = Assert.Single(WorkerLanes.All(), lane => lane.IncludedJobTypes.SequenceEqual(bulkheadClass.JobTypes));
            Assert.Equal(lane.DefaultSlots, bulkheadClass.DefaultCap);
        }
        Assert.Equal(hot.JobTypes.Count, hot.Classes.Count);

        var configured = MarketplaceHub.Worker.BulkheadSettings.From(Config(
            ("Worker:Bulkhead:Classes:order-status", "0"), ("Worker:Bulkhead:LaneCaps:hot", "2"), ("Worker:Bulkhead:LaneCaps:background", "x")));
        var configuredHot = configured.For("hot")!;
        Assert.DoesNotContain("order-status", configuredHot.ClassNames);
        Assert.Equal(2, configuredHot.LaneCap);
        Assert.Null(configured.For("background")!.LaneCap);
        Assert.Null(MarketplaceHub.Worker.BulkheadSettings.From(Config(("Worker:Bulkhead:Enabled", "false"))).For("hot"));
    }

    [Fact]
    public void FairnessDefaultsToFifteenMinutesOfAgingAndSwitchesOff()
    {
        var defaults = MarketplaceHub.Worker.FairnessSettings.From(new ConfigurationBuilder().Build());
        Assert.True(defaults.Enabled);
        Assert.Equal(TimeSpan.FromMinutes(15), defaults.Aging);
        Assert.False(MarketplaceHub.Worker.FairnessSettings.From(Config(("Worker:Fairness:Enabled", "false"))).Enabled);
    }

    [Fact]
    public void TheLeaseClockRemembersTheLatestLeasePerConnection()
    {
        var clock = new ConnectionLeaseClock();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var start = new DateTimeOffset(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);
        clock.Record(a, start);
        clock.Record(b, start.AddMinutes(1));
        clock.Record(a, start.AddMinutes(2));
        var fairness = clock.Fairness(TimeSpan.FromMinutes(15));
        Assert.Equal(TimeSpan.FromMinutes(15), fairness.Aging);
        Assert.Equal(start.AddMinutes(2), fairness.LastLeasedAt[fairness.ConnectionIds.ToList().IndexOf(a)]);
        Assert.Equal(start.AddMinutes(1), fairness.LastLeasedAt[fairness.ConnectionIds.ToList().IndexOf(b)]);
    }

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(item => item.Key, item => (string?)item.Value)).Build();
}

// The lease order and caps against the real schema. Every job uses a job type of its own test, and the leases ask only for
// it (includedJobTypes), so other tests' rows never compete.
[Collection("postgres-integration")]
public sealed class WorkerLaneFairnessPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    private static readonly TimeSpan Aging = TimeSpan.FromMinutes(15);

    [PostgreSqlFact]
    public async Task AConnectionBusyWithALongReadDoesNotKeepAnotherConnectionWaitingAndOffKeepsTheOldOrder()
    {
        await using var scenario = await Scenario.CreateAsync(fixture, "fair-busy");
        var a = await scenario.ConnectionAsync("TRENDYOL");
        var b = await scenario.ConnectionAsync("HEPSIBURADA");
        // Store A runs its 50-minute catalog read and has three older jobs queued; store B's job came a minute later.
        await scenario.RunningAsync(a, "catalog");
        var aJobs = new List<Guid>();
        for (var index = 0; index < 3; index++) aJobs.Add(await scenario.QueuedAsync(a, priority: 3, minutesAgo: 3 - (index * 0.1)));
        var bJob = await scenario.QueuedAsync(b, priority: 3, minutesAgo: 2);

        Assert.Equal(aJobs[0], await scenario.PeekAsync(fairness: null));
        Assert.Equal(bJob, await scenario.PeekAsync(new JobFairness(Aging, [], [])));
    }

    [PostgreSqlFact]
    public async Task AtEqualLoadTheConnectionServedLongestAgoGoesFirst()
    {
        await using var scenario = await Scenario.CreateAsync(fixture, "fair-served");
        var a = await scenario.ConnectionAsync("TRENDYOL");
        var b = await scenario.ConnectionAsync("PAZARAMA");
        var aJob = await scenario.QueuedAsync(a, priority: 3, minutesAgo: 3);
        var bJob = await scenario.QueuedAsync(b, priority: 3, minutesAgo: 2);
        var now = DateTimeOffset.UtcNow;

        Assert.Equal(bJob, await scenario.PeekAsync(new JobFairness(Aging, [a, b], [now.AddSeconds(-5), now.AddMinutes(-10)])));
        Assert.Equal(aJob, await scenario.PeekAsync(new JobFairness(Aging, [a, b], [now.AddMinutes(-10), now.AddSeconds(-5)])));
        // Never served (not in the clock) counts as served longest ago.
        Assert.Equal(bJob, await scenario.PeekAsync(new JobFairness(Aging, [a], [now.AddSeconds(-5)])));
    }

    [PostgreSqlFact]
    public async Task PriorityAndUserOrderStayAboveFairness()
    {
        await using var scenario = await Scenario.CreateAsync(fixture, "fair-priority");
        var a = await scenario.ConnectionAsync("TRENDYOL");
        var b = await scenario.ConnectionAsync("N11");
        await scenario.RunningAsync(a, "busy");
        var now = DateTimeOffset.UtcNow;
        var fairness = new JobFairness(Aging, [a, b], [now, now.AddHours(-1)]);
        // A hot job of the busy, recently served store still goes before the other store's background job.
        var hot = await scenario.QueuedAsync(a, priority: 0, minutesAgo: 1);
        await scenario.QueuedAsync(b, priority: 3, minutesAgo: 5);
        Assert.Equal(hot, await scenario.PeekAsync(fairness));
        // User actions keep their first-come order whatever the connection.
        var firstUser = await scenario.QueuedAsync(a, priority: JobPriorityPolicy.UserAction, minutesAgo: 2);
        await scenario.QueuedAsync(b, priority: JobPriorityPolicy.UserAction, minutesAgo: 1);
        Assert.Equal(firstUser, await scenario.PeekAsync(fairness));
    }

    [PostgreSqlFact]
    public async Task AJobPastTheAgingTimeGoesAheadSoNothingStarves()
    {
        await using var scenario = await Scenario.CreateAsync(fixture, "fair-aging");
        var a = await scenario.ConnectionAsync("TRENDYOL");
        var b = await scenario.ConnectionAsync("PTTAVM");
        await scenario.RunningAsync(a, "busy");
        var old = await scenario.QueuedAsync(a, priority: 3, minutesAgo: 20);
        var fresh = await scenario.QueuedAsync(b, priority: 3, minutesAgo: 1);
        Assert.Equal(old, await scenario.PeekAsync(new JobFairness(TimeSpan.FromMinutes(15), [], [])));
        Assert.Equal(fresh, await scenario.PeekAsync(new JobFairness(TimeSpan.FromMinutes(30), [], [])));
    }

    [PostgreSqlFact]
    public async Task OneConnectionsManyOlderJobsDoNotHideAnotherConnectionsJob()
    {
        await using var scenario = await Scenario.CreateAsync(fixture, "fair-candidates");
        var a = await scenario.ConnectionAsync("TRENDYOL");
        var b = await scenario.ConnectionAsync("HEPSIBURADA");
        // More older jobs of store A than the per-tenant candidate list holds (50).
        for (var index = 0; index < 60; index++) await scenario.QueuedAsync(a, priority: 3, minutesAgo: 10 - (index * 0.01));
        var bJob = await scenario.QueuedAsync(b, priority: 3, minutesAgo: 1);
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(bJob, await scenario.PeekAsync(new JobFairness(Aging, [a], [now])));
    }

    [PostgreSqlFact]
    public async Task AClassCapHoldsOnlyItsMarketplaceAndNeverAUserAction()
    {
        await using var scenario = await Scenario.CreateAsync(fixture, "bulkhead-class");
        var running = await scenario.ConnectionAsync("PAZARAMA");
        var samePlatform = await scenario.ConnectionAsync("PAZARAMA");
        var otherPlatform = await scenario.ConnectionAsync("PTTAVM");
        await scenario.RunningAsync(running, "orders");
        var blocked = await scenario.QueuedAsync(samePlatform, priority: 0, minutesAgo: 3);
        var other = await scenario.QueuedAsync(otherPlatform, priority: 0, minutesAgo: 1);
        var bulkhead = new JobBulkhead([scenario.JobType], ["test-class"], ["test-class"], [1], null);
        Assert.Null(await scenario.PeekAsync(null, bulkhead, onlyJobId: blocked));
        Assert.Equal(other, await scenario.PeekAsync(null, bulkhead));
        Assert.Equal(blocked, await scenario.PeekAsync(null, bulkhead with { ClassCaps = [2] }, onlyJobId: blocked));
        var user = await scenario.QueuedAsync(samePlatform, priority: JobPriorityPolicy.UserAction, minutesAgo: 1);
        Assert.Equal(user, await scenario.PeekAsync(null, bulkhead, onlyJobId: user));
    }

    [PostgreSqlFact]
    public async Task ALaneCapCountsTheLanesRunningJobsOfTheSameMarketplace()
    {
        await using var scenario = await Scenario.CreateAsync(fixture, "bulkhead-lane");
        var running = await scenario.ConnectionAsync("TRENDYOL");
        var samePlatform = await scenario.ConnectionAsync("TRENDYOL");
        await scenario.RunningAsync(running, "lane");
        var queued = await scenario.QueuedAsync(samePlatform, priority: 3, minutesAgo: 1);
        var none = new JobBulkhead([], [], [], [], null);
        Assert.Equal(queued, await scenario.PeekAsync(null, none with { LaneCap = 2 }));
        Assert.Null(await scenario.PeekAsync(null, none with { LaneCap = 1 }));
        Assert.Equal(queued, await scenario.PeekAsync(null, null));
    }

    // One test's tenant, connections and jobs of a job type of its own; the lease runs in a transaction that is rolled back,
    // so a peek leases nothing.
    private sealed class Scenario : IAsyncDisposable
    {
        private readonly PostgreSqlTenantIsolationFixture fixture;
        private readonly Tenant tenant;

        private Scenario(PostgreSqlTenantIsolationFixture fixture, Tenant tenant, string jobType)
        {
            this.fixture = fixture;
            this.tenant = tenant;
            JobType = jobType;
        }

        public string JobType { get; }

        public static async Task<Scenario> CreateAsync(PostgreSqlTenantIsolationFixture fixture, string code)
        {
            var tenant = new Tenant
            {
                Id = Guid.CreateVersion7(),
                Code = $"{code}-{Guid.NewGuid():N}",
                DisplayName = code,
                Status = RecordStatus.Active,
                Timezone = "Europe/Istanbul",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                Version = 1
            };
            await using var db = fixture.CreateContext();
            db.Tenants.Add(tenant);
            await db.SaveChangesAsync();
            return new Scenario(fixture, tenant, $"LANE_TEST_{Guid.NewGuid():N}"[..40]);
        }

        public async Task<Guid> ConnectionAsync(string platform)
        {
            var connection = new PlatformConnection
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenant.Id,
                PublicId = Guid.CreateVersion7(),
                PlatformCode = platform,
                Environment = "PRODUCTION",
                DisplayName = "Adalet testi",
                ExternalStoreId = $"store-{Guid.NewGuid():N}",
                Status = "ACTIVE",
                ApiVersion = "V1",
                SettingsJson = "{}"
            };
            await using var db = fixture.CreateContext();
            db.PlatformConnections.Add(connection);
            await db.SaveChangesAsync();
            return connection.Id;
        }

        public async Task RunningAsync(Guid connectionId, string lockGroup)
        {
            var job = NewJob(connectionId, 3, 60);
            job.LockGroup = $"running-{lockGroup}";
            job.Status = JobStatus.Leased;
            job.AttemptCount = 1;
            job.LeaseTokenHash = "running";
            job.LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30);
            await using var db = fixture.CreateContext();
            db.IntegrationJobs.Add(job);
            await db.SaveChangesAsync();
        }

        // A queued job in a lock group of its own (so only the order decides), runnable for minutesAgo.
        public async Task<Guid> QueuedAsync(Guid connectionId, int priority, double minutesAgo)
        {
            var job = NewJob(connectionId, priority, minutesAgo);
            job.LockGroup = $"queued-{Guid.NewGuid():N}";
            await using var db = fixture.CreateContext();
            db.IntegrationJobs.Add(job);
            await db.SaveChangesAsync();
            return job.Id;
        }

        // The job the lease takes now; it is put back in the queue afterwards, so the next look sees the same queue.
        public async Task<Guid?> PeekAsync(JobFairness? fairness, JobBulkhead? bulkhead = null, Guid? onlyJobId = null)
        {
            await using var db = fixture.CreateContext();
            var leases = new JobLeaseService(db, fixture.TokenHasher, TimeProvider.System);
            var job = await leases.TryLeaseAsync(TimeSpan.FromMinutes(2), null, null, CancellationToken.None, onlyJobId: onlyJobId,
                includedJobTypes: [JobType], fairness: fairness, bulkhead: bulkhead);
            if (job is null) return null;
            await using var reset = fixture.CreateContext();
            await reset.JobAttempts.Where(x => x.JobId == job.Id).ExecuteDeleteAsync();
            await reset.IntegrationOutboxEvents.Where(x => x.TenantId == tenant.Id).ExecuteDeleteAsync();
            await reset.IntegrationJobs.Where(x => x.Id == job.Id).ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, JobStatus.Pending)
                .SetProperty(x => x.AttemptCount, 0)
                .SetProperty(x => x.LeaseTokenHash, (string?)null)
                .SetProperty(x => x.LeaseExpiresAt, (DateTimeOffset?)null)
                .SetProperty(x => x.HeartbeatAt, (DateTimeOffset?)null)
                .SetProperty(x => x.StartedAt, (DateTimeOffset?)null));
            return job.Id;
        }

        private IntegrationJob NewJob(Guid connectionId, int priority, double minutesAgo)
        {
            var at = DateTimeOffset.UtcNow.AddMinutes(-minutesAgo);
            var suffix = Guid.NewGuid().ToString("N");
            return new IntegrationJob
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenant.Id,
                ConnectionId = connectionId,
                JobType = JobType,
                PayloadJson = "{}",
                PayloadVersion = 1,
                PayloadHash = "lane-payload-hash",
                JobDedupKey = $"lane-{suffix}",
                EffectIdempotencyKey = $"lane-{suffix}",
                Priority = priority,
                Status = JobStatus.Pending,
                AvailableAt = at,
                MaxAttempts = 3,
                CorrelationId = $"lane-{suffix}",
                CreatedAt = at,
                Version = 1
            };
        }

        public async ValueTask DisposeAsync()
        {
            await using var cleanup = fixture.CreateContext();
            await cleanup.JobAttempts.Where(x => x.TenantId == tenant.Id).ExecuteDeleteAsync();
            await cleanup.IntegrationJobs.Where(x => x.TenantId == tenant.Id).ExecuteDeleteAsync();
            await cleanup.IntegrationOutboxEvents.Where(x => x.TenantId == tenant.Id).ExecuteDeleteAsync();
            await cleanup.PlatformConnections.Where(x => x.TenantId == tenant.Id).ExecuteDeleteAsync();
            await cleanup.Tenants.Where(x => x.Id == tenant.Id).ExecuteDeleteAsync();
        }
    }
}
