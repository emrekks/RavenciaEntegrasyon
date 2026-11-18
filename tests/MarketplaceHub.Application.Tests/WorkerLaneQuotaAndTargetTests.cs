using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters;
using MarketplaceHub.Infrastructure.Jobs;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Worker lanes step 1 (19 Nov 2026): quota-aware leasing (QuotaPressure, JobQuotaHold) and lane wait targets with their alert
// (LaneWaitTargets, LaneWaitAlarm). No test reads the wall clock for its outcome: time is stepped by hand.
public sealed class WorkerLaneQuotaAndTargetTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ABucketIsFullOnlyOnceARequestHasWaitedTheSaturationTime()
    {
        var pressure = new QuotaPressure();
        using var waiter = pressure.BeginWait("trendyol:orders:x:bg", Start);
        Assert.False(pressure.IsFull("trendyol:orders:x:bg", Start.AddSeconds(9)));
        Assert.True(pressure.IsFull("trendyol:orders:x:bg", Start.AddSeconds(10)));
        Assert.False(pressure.IsFull("pazarama:y:orders", Start.AddSeconds(30)));
    }

    [Fact]
    public void HeldListsOnlyThePairsThatUseAFullBucket()
    {
        var pressure = new QuotaPressure();
        var throttled = Guid.NewGuid();
        var other = Guid.NewGuid();
        pressure.Learn(throttled, MarketplaceJobTypes.OrderStatusSync, ["trendyol:orders:x:bg", "trendyol:global"]);
        pressure.Learn(throttled, MarketplaceJobTypes.OrderSync, ["trendyol:orders:x:fg", "trendyol:global"]);
        pressure.Learn(other, MarketplaceJobTypes.PazaramaOrderSync, ["pazarama:y:orders"]);

        Assert.True(pressure.Held(Start, TimeSpan.FromMinutes(15)).IsEmpty);
        using var waiter = pressure.BeginWait("trendyol:orders:x:bg", Start);
        var held = pressure.Held(Start.AddSeconds(11), TimeSpan.FromMinutes(15));

        // Only the background order scan of the throttled store: its new-order read draws from another bucket.
        Assert.Equal([throttled], held.ConnectionIds);
        Assert.Equal([MarketplaceJobTypes.OrderStatusSync], held.JobTypes);
        Assert.Equal(TimeSpan.FromMinutes(15), held.MaximumHold);
    }

    [Fact]
    public void TheLastWaiterOfAFullBucketReleasesTheHeldJobsAndAShortWaitDoesNot()
    {
        var pressure = new QuotaPressure();
        var released = 0;
        pressure.Released += () => released++;

        var spacing = pressure.BeginWait("trendyol:orders:x:spacing", Start);
        spacing.End(Start.AddSeconds(5));
        Assert.Equal(0, released);

        var first = pressure.BeginWait("hepsiburada:catalog", Start);
        var second = pressure.BeginWait("hepsiburada:catalog", Start.AddSeconds(3));
        first.End(Start.AddSeconds(20));
        Assert.Equal(0, released);
        second.End(Start.AddSeconds(25));
        second.End(Start.AddSeconds(26));
        Assert.Equal(1, released);
        Assert.False(pressure.IsFull("hepsiburada:catalog", Start.AddSeconds(30)));
    }

    [Fact]
    public async Task TheSharedLimiterWaitMarksItsBucketFullWhileItWaitsAndRecordsItForTheJob()
    {
        var pressure = new QuotaPressure();
        var released = 0;
        pressure.Released += () => released++;
        var clock = new SteppingTime(Start);
        var fullDuringWait = false;
        var limiter = new ScriptedLimiter([TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30), TimeSpan.Zero],
            onCall: call => { if (call == 2) fullDuringWait = pressure.IsFull("pazarama:y:orders", clock.GetUtcNow()); });

        using var scope = JobQuotaScope.Enter();
        await SharedRateLimits.WaitAsync(limiter, "pazarama:y:orders", (1, 0.1), clock, pressure, CancellationToken.None);

        Assert.True(fullDuringWait);
        Assert.False(pressure.IsFull("pazarama:y:orders", clock.GetUtcNow()));
        Assert.Equal(1, released);
        Assert.Equal(["pazarama:y:orders"], scope.Keys);
    }

    [Fact]
    public async Task ATokenTakenAtOnceLeavesNoWaiterBehind()
    {
        var pressure = new QuotaPressure();
        var clock = new SteppingTime(Start);
        await SharedRateLimits.WaitAsync(new ScriptedLimiter([TimeSpan.Zero]), "n11:a:orders", (1, 1), clock, pressure, CancellationToken.None);
        Assert.True(pressure.Held(clock.GetUtcNow().AddMinutes(5), TimeSpan.FromMinutes(15)).IsEmpty);
        Assert.Null(JobQuotaScope.Current);
    }

    [Fact]
    public void QuotaAwareLeasingSettingsDefaultToTheReplayWinnerAndSwitchOff()
    {
        var defaults = MarketplaceHub.Worker.QuotaAwareLeasingSettings.From(new ConfigurationBuilder().Build());
        Assert.True(defaults.Enabled);
        Assert.Equal(TimeSpan.FromSeconds(10), defaults.Saturation);
        Assert.Equal(TimeSpan.FromMinutes(15), defaults.MaximumHold);
        var off = MarketplaceHub.Worker.QuotaAwareLeasingSettings.From(Config(("Worker:QuotaAwareLeasing:Enabled", "false"), ("Worker:QuotaAwareLeasing:MaximumHoldMinutes", "500")));
        Assert.False(off.Enabled);
        Assert.Equal(TimeSpan.FromMinutes(60), off.MaximumHold);
    }

    [Fact]
    public void EveryLaneHasATargetAndASettingChangesOne()
    {
        foreach (var lane in WorkerLanes.All())
            Assert.True(LaneWaitTargets.DefaultFor(lane.Name) > TimeSpan.Zero, lane.Name);
        Assert.Equal(TimeSpan.FromSeconds(5), LaneWaitTargets.For(null, "user"));
        Assert.Equal(TimeSpan.FromSeconds(60), LaneWaitTargets.For(null, "hot"));
        Assert.Equal(TimeSpan.FromSeconds(60), LaneWaitTargets.For(null, "product-catalog"));
        Assert.Equal(TimeSpan.FromSeconds(120), LaneWaitTargets.For(null, "initial-data-sync"));
        Assert.Equal(TimeSpan.FromSeconds(600), LaneWaitTargets.For(null, "background"));
        Assert.Equal(TimeSpan.FromSeconds(90), LaneWaitTargets.For(Config(("Worker:LaneTargets:hot", "90")), "hot"));
    }

    [Fact]
    public void OneLateJobDoesNotOpenAnAlertButASustainedMissDoesAndItClosesItself()
    {
        var alarm = new LaneWaitAlarm(TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10));
        var tenant = Guid.NewGuid();
        // A single late job is in the five-minute window for five looks, then gone.
        for (var minute = 0; minute < 5; minute++)
            Assert.Equal(LaneAlarmTransition.None, alarm.Observe(tenant, "hot", breached: true, open: false, Start.AddMinutes(minute)));
        Assert.Equal(LaneAlarmTransition.None, alarm.Observe(tenant, "hot", breached: false, open: false, Start.AddMinutes(5)));

        // Missed at every look for ten minutes: opened once.
        var opened = 0;
        for (var minute = 6; minute <= 20; minute++)
            if (alarm.Observe(tenant, "hot", breached: true, open: opened > 0, Start.AddMinutes(minute)) == LaneAlarmTransition.Open) opened++;
        Assert.Equal(1, opened);

        // Back within target: the alert stays until ten minutes have passed, a relapse starts the count again.
        Assert.Equal(LaneAlarmTransition.None, alarm.Observe(tenant, "hot", breached: false, open: true, Start.AddMinutes(21)));
        Assert.Equal(LaneAlarmTransition.None, alarm.Observe(tenant, "hot", breached: true, open: true, Start.AddMinutes(25)));
        Assert.Equal(LaneAlarmTransition.None, alarm.Observe(tenant, "hot", breached: false, open: true, Start.AddMinutes(26)));
        Assert.Equal(LaneAlarmTransition.None, alarm.Observe(tenant, "hot", breached: false, open: true, Start.AddMinutes(35)));
        Assert.Equal(LaneAlarmTransition.Resolve, alarm.Observe(tenant, "hot", breached: false, open: true, Start.AddMinutes(36)));
    }

    [Fact]
    public void AnAlertLeftOpenByTheLastWorkerClosesAfterTheClearTime()
    {
        var alarm = new LaneWaitAlarm(TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10));
        var tenant = Guid.NewGuid();
        Assert.Equal(LaneAlarmTransition.None, alarm.Observe(tenant, "background", breached: false, open: true, Start));
        Assert.Equal(LaneAlarmTransition.Resolve, alarm.Observe(tenant, "background", breached: false, open: true, Start.AddMinutes(10)));
        // An open alert that is still missed is not opened a second time.
        Assert.Equal(LaneAlarmTransition.None, alarm.Observe(tenant, "user", breached: true, open: true, Start));
        Assert.Equal(LaneAlarmTransition.None, alarm.Observe(tenant, "user", breached: true, open: true, Start.AddMinutes(30)));
    }

    [Fact]
    public void ALaneIsBreachedByItsRecentP95OrByAJobWaitingLongerThanTheTarget()
    {
        var tenant = Guid.NewGuid();
        Assert.False(new LaneWaitMonitor.LaneWait(tenant, "hot", TimeSpan.FromSeconds(60), 0, null, null).Breached);
        Assert.False(new LaneWaitMonitor.LaneWait(tenant, "hot", TimeSpan.FromSeconds(60), 12, 60, 59).Breached);
        Assert.True(new LaneWaitMonitor.LaneWait(tenant, "hot", TimeSpan.FromSeconds(60), 12, 61, null).Breached);
        Assert.True(new LaneWaitMonitor.LaneWait(tenant, "hot", TimeSpan.FromSeconds(60), 0, null, 61).Breached);
    }

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(item => item.Key, item => (string?)item.Value)).Build();

    private sealed class ScriptedLimiter(TimeSpan[] answers, Action<int>? onCall = null) : ISharedRateLimiter
    {
        private int calls;

        public ValueTask<TimeSpan> TryTakeAsync(string key, double capacity, double tokensPerSecond, CancellationToken cancellationToken)
        {
            var call = ++calls;
            onCall?.Invoke(call);
            return ValueTask.FromResult(answers[Math.Min(call, answers.Length) - 1]);
        }
    }

    // Every timer fires at once and moves the clock by its due time, so a Task.Delay on it ends without waiting.
    internal sealed class SteppingTime(DateTimeOffset start) : TimeProvider
    {
        private long ticks = start.UtcTicks;

        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);

        public void Advance(TimeSpan by) => Interlocked.Add(ref ticks, by.Ticks);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (dueTime > TimeSpan.Zero) Advance(dueTime);
            ThreadPool.QueueUserWorkItem(_ => callback(state));
            return new FiredTimer();
        }

        private sealed class FiredTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => false;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}

// The lease and the alert against the real schema.
[Collection("postgres-integration")]
public sealed class WorkerLaneQuotaAndTargetPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    [PostgreSqlFact]
    public async Task AJobOfAFullQuotaIsNotLeasedUntilItOpensWhileOthersAndUserActionsAre()
    {
        var tenant = NewTenant("quota-hold");
        var throttled = NewConnection(tenant);
        var other = NewConnection(tenant);
        var scan = NewJob(tenant.Id, throttled.Id, MarketplaceJobTypes.OrderStatusSync, priority: 0);
        var otherScan = NewJob(tenant.Id, other.Id, MarketplaceJobTypes.OrderStatusSync, priority: 0);
        var user = NewJob(tenant.Id, throttled.Id, MarketplaceJobTypes.OrderStatusSync, priority: JobPriorityPolicy.UserAction);
        user.LockGroup = "user-test";
        await using (var setup = fixture.CreateContext())
        {
            setup.Tenants.Add(tenant);
            setup.PlatformConnections.AddRange(throttled, other);
            setup.IntegrationJobs.AddRange(scan, otherScan, user);
            await setup.SaveChangesAsync();
        }

        try
        {
            var hold = new JobQuotaHold([throttled.Id], [MarketplaceJobTypes.OrderStatusSync], TimeSpan.FromMinutes(15));
            await using (var db = fixture.CreateContext())
            {
                var leases = new JobLeaseService(db, fixture.TokenHasher, fixture.TimeProvider);
                // The quota is full: the throttled store's scan stays queued; the other store's scan and the user's action go.
                Assert.Null(await leases.TryLeaseAsync(TimeSpan.FromMinutes(2), null, null, CancellationToken.None, onlyJobId: scan.Id, quotaHold: hold));
                Assert.NotNull(await leases.TryLeaseAsync(TimeSpan.FromMinutes(2), null, null, CancellationToken.None, onlyJobId: otherScan.Id, quotaHold: hold));
                Assert.NotNull(await leases.TryLeaseAsync(TimeSpan.FromMinutes(2), null, null, CancellationToken.None, onlyJobId: user.Id, quotaHold: hold));
            }
            await using (var db = fixture.CreateContext())
            {
                // The quota opened (nothing held any more): leased at once.
                var leases = new JobLeaseService(db, fixture.TokenHasher, fixture.TimeProvider);
                Assert.NotNull(await leases.TryLeaseAsync(TimeSpan.FromMinutes(2), null, null, CancellationToken.None, onlyJobId: scan.Id, quotaHold: JobQuotaHold.None));
            }
        }
        finally
        {
            await CleanupAsync(tenant.Id);
        }
    }

    [PostgreSqlFact]
    public async Task AHeldJobIsLeasedOnceItHasWaitedTheMaximumHold()
    {
        var tenant = NewTenant("quota-starve");
        var connection = NewConnection(tenant);
        var scan = NewJob(tenant.Id, connection.Id, MarketplaceJobTypes.OrderStatusSync, priority: 0);
        scan.AvailableAt = DateTimeOffset.UtcNow.AddMinutes(-16);
        scan.CreatedAt = scan.AvailableAt;
        await using (var setup = fixture.CreateContext())
        {
            setup.Tenants.Add(tenant);
            setup.PlatformConnections.Add(connection);
            setup.IntegrationJobs.Add(scan);
            await setup.SaveChangesAsync();
        }

        try
        {
            await using var db = fixture.CreateContext();
            var leases = new JobLeaseService(db, fixture.TokenHasher, fixture.TimeProvider);
            var hold = new JobQuotaHold([connection.Id], [MarketplaceJobTypes.OrderStatusSync], TimeSpan.FromMinutes(15));
            Assert.NotNull(await leases.TryLeaseAsync(TimeSpan.FromMinutes(2), null, null, CancellationToken.None, onlyJobId: scan.Id, quotaHold: hold));
        }
        finally
        {
            await CleanupAsync(tenant.Id);
        }
    }

    [PostgreSqlFact]
    public async Task ASustainedWaitOpensTheLaneAlertTheLaneMetricsShowItAndItClosesWhenTheWaitEnds()
    {
        var tenant = NewTenant("lane-alert");
        var connection = NewConnection(tenant);
        var start = DateTimeOffset.UtcNow;
        // A hot-lane job (target 60 s) that has been runnable for two minutes.
        var late = NewJob(tenant.Id, connection.Id, MarketplaceJobTypes.OrderSync, priority: 0);
        late.AvailableAt = start.AddMinutes(-2);
        late.CreatedAt = late.AvailableAt;
        await using (var setup = fixture.CreateContext())
        {
            setup.Tenants.Add(tenant);
            setup.PlatformConnections.Add(connection);
            setup.IntegrationJobs.Add(late);
            await setup.SaveChangesAsync();
        }

        try
        {
            var clock = new WorkerLaneQuotaAndTargetTests.SteppingTime(start);
            var alarm = new LaneWaitAlarm(TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10));
            async Task<OperationalIssue?> EvaluateAsync(JobQuotaHold? hold = null)
            {
                await using var db = fixture.CreateContext();
                await new LaneWaitMonitor(db, clock).EvaluateAsync(alarm, LaneWaitMonitor.DefaultWindow, hold, CancellationToken.None);
                return await db.OperationalIssues.AsNoTracking().SingleOrDefaultAsync(issue => issue.TenantId == tenant.Id && issue.DedupeKey == LaneWaitTargets.IssueKey("hot"));
            }

            Assert.Null(await EvaluateAsync());
            clock.Advance(TimeSpan.FromMinutes(5));
            Assert.Null(await EvaluateAsync());
            // Held for a full quota, it waits for the marketplace, not for a slot: no miss at that look, the count starts over.
            Assert.Null(await EvaluateAsync(new JobQuotaHold([connection.Id], [MarketplaceJobTypes.OrderSync], TimeSpan.FromMinutes(60))));
            clock.Advance(TimeSpan.FromMinutes(1));
            Assert.Null(await EvaluateAsync());
            clock.Advance(TimeSpan.FromMinutes(10));
            var opened = await EvaluateAsync();
            Assert.NotNull(opened);
            Assert.Equal(IssueStatus.Open, opened.Status);
            Assert.Equal(LaneWaitTargets.IssueCode, opened.Code);
            Assert.Contains("Öncelikli İşler", opened.Summary, StringComparison.Ordinal);

            await using (var db = fixture.CreateContext())
            {
                var lanes = await new QueueHealthService(db, Microsoft.Extensions.Options.Options.Create(new MarketplaceHub.Infrastructure.Adapters.Trendyol.TrendyolOptions()), clock)
                    .LanesAsync(tenant.Id, TimeSpan.FromHours(1), CancellationToken.None);
                var hot = Assert.Single(lanes, lane => lane.Lane == "HOT");
                Assert.Equal(60, hot.TargetSeconds);
                Assert.Equal(LaneWaitStatus.Alert, hot.WaitStatus);
                Assert.NotNull(hot.AlertSince);
                Assert.True(hot.OldestWaitSeconds > 60);
                Assert.All(lanes.Where(lane => lane.Lane != "HOT"), lane => Assert.Equal(LaneWaitStatus.Ok, lane.WaitStatus));
            }

            // The job ran; ten minutes within target close the alert.
            await using (var db = fixture.CreateContext())
                await db.IntegrationJobs.Where(job => job.Id == late.Id).ExecuteUpdateAsync(setters => setters.SetProperty(job => job.Status, JobStatus.Succeeded));
            clock.Advance(TimeSpan.FromMinutes(1));
            Assert.Equal(IssueStatus.Open, (await EvaluateAsync())!.Status);
            clock.Advance(TimeSpan.FromMinutes(10));
            var closed = await EvaluateAsync();
            Assert.Equal(IssueStatus.Resolved, closed!.Status);
            Assert.NotNull(closed.ResolvedAt);
        }
        finally
        {
            await CleanupAsync(tenant.Id);
        }
    }

    private async Task CleanupAsync(Guid tenantId)
    {
        await using var cleanup = fixture.CreateContext();
        await cleanup.JobAttempts.Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
        await cleanup.IntegrationJobs.Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
        await cleanup.IntegrationOutboxEvents.Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
        await cleanup.OperationalIssues.Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
        await cleanup.PlatformConnections.Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
        await cleanup.Tenants.Where(x => x.Id == tenantId).ExecuteDeleteAsync();
    }

    private static Tenant NewTenant(string code) => new()
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

    private static PlatformConnection NewConnection(Tenant tenant) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenant.Id,
        PublicId = Guid.CreateVersion7(),
        PlatformCode = "TRENDYOL",
        Environment = "PRODUCTION",
        DisplayName = "Şerit testi",
        ExternalStoreId = $"store-{Guid.NewGuid():N}",
        Status = "ACTIVE",
        ApiVersion = "V1",
        SettingsJson = "{}"
    };

    private static IntegrationJob NewJob(Guid tenantId, Guid connectionId, string jobType, int priority)
    {
        var now = DateTimeOffset.UtcNow;
        var suffix = Guid.NewGuid().ToString("N");
        return new IntegrationJob
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            ConnectionId = connectionId,
            JobType = jobType,
            PayloadJson = "{}",
            PayloadVersion = 1,
            PayloadHash = "lane-payload-hash",
            JobDedupKey = $"lane-{suffix}",
            EffectIdempotencyKey = $"lane-{suffix}",
            Priority = priority,
            Status = JobStatus.Pending,
            AvailableAt = now.AddMinutes(-1),
            MaxAttempts = 3,
            CorrelationId = $"lane-{suffix}",
            CreatedAt = now.AddMinutes(-1),
            Version = 1
        };
    }
}
