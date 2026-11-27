using System.Diagnostics;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters;
using MarketplaceHub.Infrastructure.Adapters.N11;
using MarketplaceHub.Infrastructure.Adapters.Pazarama;
using MarketplaceHub.Infrastructure.Adapters.PttAvm;
using MarketplaceHub.Infrastructure.Adapters.Trendyol;
using MarketplaceHub.Infrastructure.Jobs;
using MarketplaceHub.Infrastructure.Persistence;
using MarketplaceHub.Infrastructure.Persistence.Migrations;
using MarketplaceHub.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.Options;
using Xunit;
using Xunit.Abstractions;

namespace MarketplaceHub.Application.Tests;

// Queue phase 0 (27 Nov 2026): what the worker records about each attempt (why and how long it waited, its lane, its token
// waits), the webhook event time, the 30-day quota usage and what the queue page reads from them. Nothing here changes a
// lease: the last PostgreSQL test runs one job stream with and without the recording and finds the same decisions.
public sealed class QueueVisibilityPhase0Tests(ITestOutputHelper output)
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 27, 10, 0, 0, TimeSpan.Zero);

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static JobWaitQuery Query(Guid? connection, string? group, DateTimeOffset eligible, DateTimeOffset leased, int priority = 3, Guid? jobId = null, bool fresh = true, string jobType = "TEST_JOB") =>
        new(jobId ?? Guid.NewGuid(), connection, jobType, priority, group, eligible.AddSeconds(-30), eligible, leased, fresh);

    [Fact]
    public void TheWaitIsSplitIntoLockBackoffHoldAndSlotEachMomentCountedOnceInThatOrder()
    {
        var clock = new ManualClock(T0);
        var pressure = new QuotaPressure();
        var ledger = new JobWaitLedger(clock) { Pressure = pressure, QuotaHold = new JobWaitLedger.QuotaHoldShape(TimeSpan.FromMinutes(15), 2) };
        var connection = Guid.NewGuid();
        var lanes = new List<string>();
        ledger.LanesFor = (_, _) => ["background"];
        ledger.SlotSpells = (names, from, to) => { lanes.AddRange(names); return [new JobWaitLedger.Span(T0.AddSeconds(50), T0.AddSeconds(70))]; };
        // Window 10..100 s. Lock 10..30 (another job of the group), backoff 25..45 (5 s overlap with the lock),
        // quota full 40..60 (5 s overlap with the backoff), no slot 50..70 (10 s overlap with the hold).
        ledger.RunStarted(Guid.NewGuid(), connection, "orders", T0.AddSeconds(10));
        var other = Guid.NewGuid();
        ledger.RunStarted(other, connection, "orders", T0.AddSeconds(10));
        ledger.RunEnded(other, T0.AddSeconds(30));
        ledger.BackoffRecorded(connection, "orders", T0.AddSeconds(25), T0.AddSeconds(45));
        pressure.Learn(connection, "TEST_JOB", ["bucket-a"]);
        pressure.Saturation = TimeSpan.FromSeconds(10);
        var waiter = pressure.BeginWait("bucket-a", T0.AddSeconds(30));
        waiter.End(T0.AddSeconds(60));

        // The still running first job holds the group too, up to the lease.
        var stillRunning = ledger.Attribute(Query(connection, "orders", T0.AddSeconds(10), T0.AddSeconds(100)));
        Assert.Equal(90_000, stillRunning.LockMs);

        var fresh = new JobWaitLedger(clock) { Pressure = pressure, QuotaHold = new JobWaitLedger.QuotaHoldShape(TimeSpan.FromMinutes(15), 2), LanesFor = ledger.LanesFor, SlotSpells = ledger.SlotSpells };
        var job = Guid.NewGuid();
        fresh.RunStarted(job, connection, "orders", T0.AddSeconds(10));
        fresh.RunEnded(job, T0.AddSeconds(30));
        fresh.BackoffRecorded(connection, "orders", T0.AddSeconds(25), T0.AddSeconds(45));
        var waits = fresh.Attribute(Query(connection, "orders", T0.AddSeconds(10), T0.AddSeconds(100)));
        Assert.Equal(20_000, waits.LockMs);
        Assert.Equal(15_000, waits.BackoffMs);
        Assert.Equal(15_000, waits.QuotaHoldMs);
        Assert.Equal(10_000, waits.SlotMs);
        Assert.Equal(new[] { "background" }, lanes.Distinct());
        // A job that is never held (priority within the hot ceiling) gets the hold's time as slot time instead.
        var hot = fresh.Attribute(Query(connection, "orders", T0.AddSeconds(10), T0.AddSeconds(100), priority: 0));
        Assert.Equal((20_000, 15_000, 0, 20_000), (hot.LockMs, hot.BackoffMs, hot.QuotaHoldMs, hot.SlotMs));
        // Another group of the same connection was never busy.
        var otherGroup = fresh.Attribute(Query(connection, "returns", T0.AddSeconds(10), T0.AddSeconds(100), priority: 0));
        Assert.Equal((0, 0), (otherGroup.LockMs, otherGroup.BackoffMs));
    }

    [Fact]
    public void WhatTheProcessDidNotSeeIsUnknownNeverZero()
    {
        var clock = new ManualClock(T0);
        var ledger = new JobWaitLedger(clock);
        var connection = Guid.NewGuid();
        // The wait began before the process started: the causes are unknown, the not-due time still known for a fresh job.
        var early = ledger.Attribute(Query(connection, "orders", T0.AddSeconds(-5), T0.AddSeconds(20)));
        Assert.Equal((null, null, null, null), (early.LockMs, early.BackoffMs, early.QuotaHoldMs, early.SlotMs));
        Assert.Equal(30_000, early.NotDueMs);
        // Without a lane pool (legacy lanes) the slot figure stays unknown; a job without a connection has no lock or backoff.
        var noPool = ledger.Attribute(Query(null, null, T0.AddSeconds(5), T0.AddSeconds(20)));
        Assert.Equal((0, 0, 0, (int?)null), (noPool.LockMs, noPool.BackoffMs, noPool.QuotaHoldMs, noPool.SlotMs));
        // A job that ran before (its run ended here) is not due since that end; one that failed elsewhere is unknown.
        var job = Guid.NewGuid();
        ledger.RunStarted(job, connection, "orders", T0.AddSeconds(1));
        ledger.RunEnded(job, T0.AddSeconds(2));
        Assert.Equal(13_000, ledger.Attribute(Query(connection, "orders", T0.AddSeconds(15), T0.AddSeconds(16), jobId: job, fresh: false)).NotDueMs);
        Assert.Null(ledger.Attribute(Query(connection, "orders", T0.AddSeconds(15), T0.AddSeconds(16), fresh: false)).NotDueMs);
        // A wait older than the history the process keeps is unknown too.
        clock.Now = T0.AddHours(8);
        Assert.Null(ledger.Attribute(Query(connection, "orders", T0.AddHours(1), T0.AddHours(7.5))).LockMs);
    }

    [Fact]
    public void TheLanePoolRecordsWhenALaneHadNoFreeSlot()
    {
        var clock = new ManualClock(T0);
        var pool = new LaneSlotPool(new Dictionary<string, int> { ["a"] = 1, ["b"] = 1 }, [], clock: clock);
        Assert.True(pool.TryClaim("a", ownSlot: true));
        clock.Now = T0.AddSeconds(10);
        Assert.True(pool.TryClaim("b", ownSlot: true));
        clock.Now = T0.AddSeconds(25);
        pool.Release("b", ownSlot: true);
        clock.Now = T0.AddSeconds(40);
        // From 10 s to 25 s both slots ran: neither lane could take a job (a's own slot busy, nothing to borrow).
        var spells = pool.NoRoomSpells(["a"], T0, T0.AddSeconds(40));
        Assert.Equal(new[] { (T0.AddSeconds(10), T0.AddSeconds(25)) }, spells);
        // A job both lanes could lease waited for a slot only while neither had one; b had none since 10 s too.
        Assert.Equal(new[] { (T0.AddSeconds(10), T0.AddSeconds(25)) }, pool.NoRoomSpells(["a", "b"], T0, T0.AddSeconds(40)));
        Assert.Null(pool.Violation());
    }

    private sealed class ScriptedLimiter(params TimeSpan[] answers) : ISharedRateLimiter
    {
        private int index;
        public ValueTask<TimeSpan> TryTakeAsync(string key, double capacity, double tokensPerSecond, CancellationToken cancellationToken) =>
            ValueTask.FromResult(index < answers.Length ? answers[index++] : TimeSpan.Zero);
    }

    [Fact]
    public async Task ARequestThatWaitedForItsTokenAddsItsWaitToTheRunningJob()
    {
        var jobId = Guid.NewGuid();
        using var scope = JobQuotaScope.Enter(jobId);
        Assert.Equal(jobId, JobQuotaScope.CurrentJobId);
        await SharedRateLimits.WaitAsync(new ScriptedLimiter(TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(20)), "k", (1, 1), TimeProvider.System, new QuotaPressure(), CancellationToken.None);
        await SharedRateLimits.WaitAsync(new ScriptedLimiter(), "k", (1, 1), TimeProvider.System, new QuotaPressure(), CancellationToken.None);
        Assert.Equal(1, scope.TokenWaits);
        Assert.True(scope.TokenWait >= TimeSpan.FromMilliseconds(50), scope.TokenWait.ToString());
        Assert.Equal(new JobAttemptRun(JobWaitLedger.Milliseconds(scope.TokenWait), 1), MarketplaceHub.Worker.Worker.RunFacts(scope));
    }

    [Fact]
    public async Task OutsideAJobATokenWaitIsNotRecordedAnywhere()
    {
        Assert.Null(JobQuotaScope.CurrentJobId);
        await SharedRateLimits.WaitAsync(new ScriptedLimiter(TimeSpan.FromMilliseconds(10)), "k", (1, 1), TimeProvider.System, new QuotaPressure(), CancellationToken.None);
        using var scope = JobQuotaScope.Enter();
        Assert.Equal(0, scope.TokenWaits);
        Assert.Null(scope.JobId);
    }

    [Fact]
    public void TheWebhookEventTimeIsReadWhereTheDeliveryCarriesItAndNullElsewhere()
    {
        var received = T0;
        const string trendyol = """{"content":[{"supplierId":1,"lastModifiedDate":1795773590000},{"supplierId":1,"lastModifiedDate":1795773595500,"orderDate":1795784390000}]}""";
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1795773595500), MarketplaceWebhookService.EventTime("TRENDYOL", trendyol, new Dictionary<string, string>()));
        Assert.Null(MarketplaceWebhookService.EventTime("TRENDYOL", """{"content":[{"supplierId":1}]}""", new Dictionary<string, string>()));
        Assert.Null(MarketplaceWebhookService.EventTime("TRENDYOL", "not json", new Dictionary<string, string>()));
        var shopify = new Dictionary<string, string> { ["x-shopify-triggered-at"] = "2026-11-27T09:59:58.123Z" };
        Assert.Equal(new DateTimeOffset(2026, 11, 27, 9, 59, 58, 123, TimeSpan.Zero), MarketplaceWebhookService.EventTime("SHOPIFY", "{}", shopify, received));
        Assert.Null(MarketplaceWebhookService.EventTime("SHOPIFY", "{}", new Dictionary<string, string>(), received));
        // Hepsiburada's bodies carry business dates only.
        Assert.Null(MarketplaceWebhookService.EventTime("HEPSIBURADA", """{"orderDate":"2026-11-27T12:59:00","merchantId":"m"}""", new Dictionary<string, string>(), received));
        // A value more than a day off the receipt is not taken.
        Assert.Null(MarketplaceWebhookService.EventTime("SHOPIFY", "{}", new Dictionary<string, string> { ["X-Shopify-Triggered-At"] = "2026-11-20T10:00:00Z" }, received));
    }

    [Fact]
    public void TheMigrationKeepsTheFunctionBodyAndOnlyLengthensItsCleanUp()
    {
        var before = Assert.IsType<SqlOperation>(Assert.Single(new RateTokenTimeAfterLock().UpOperations)).Sql;
        var migration = new QueueVisibilityPhase0();
        var up = migration.UpOperations.OfType<SqlOperation>().Select(operation => operation.Sql).ToList();
        var down = migration.DownOperations.OfType<SqlOperation>().Select(operation => operation.Sql).ToList();
        Assert.Contains(before.Replace("interval '2 days'", "interval '30 days'", StringComparison.Ordinal), up);
        Assert.Contains(before, down);
        Assert.DoesNotContain(migration.UpOperations, operation => operation is DropColumnOperation or DropTableOperation or AlterColumnOperation or CreateIndexOperation);
        Assert.All(migration.UpOperations.OfType<AddColumnOperation>(), column => Assert.True(column.IsNullable && column.DefaultValue is null && column.DefaultValueSql is null, column.Name));
    }

    [Fact]
    public void TheQuotaCatalogNamesEveryBucketWithTheLimitTheLimiterUses()
    {
        var catalog = new QuotaBucketCatalog(new TrendyolOptions(), new N11Options(), new PazaramaOptions(), new PttAvmOptions());
        var (foreground, background) = TrendyolResilienceHandler.OrderQuotaSplit(new TrendyolOptions());
        Assert.Equal(new QuotaBucketCatalog.Bucket("TRENDYOL", "TRENDYOL:ORDERS_FG", "Sipariş Okuma · Ön Plan", foreground), catalog.Describe("trendyol:orders:apigw.trendyol.com:111:fg"));
        Assert.Equal(background, catalog.Describe("trendyol:orders:apigw.trendyol.com:111:bg")!.LimitPerMinute);
        Assert.Equal(12, catalog.Describe("trendyol:orders:apigw.trendyol.com:111:spacing")!.LimitPerMinute);
        Assert.Equal(60, catalog.Describe("trendyol:orders:apigw.trendyol.com:111:spacing:start")!.LimitPerMinute);
        Assert.Equal(100, catalog.Describe("trendyol:finance:apigw.trendyol.com:111")!.LimitPerMinute);
        Assert.Equal(5, catalog.Describe("trendyol:claim-actions:apigw.trendyol.com:111")!.LimitPerMinute);
        var global = catalog.Describe("trendyol:global:apigw.trendyol.com:111 - Agent:order:claims")!;
        Assert.Equal(("TRENDYOL:GLOBAL:ORDER:CLAIMS", 300d), (global.Code, global.LimitPerMinute));
        Assert.Equal(new QuotaBucketCatalog.Bucket("HEPSIBURADA", "HEPSIBURADA:CATALOG", "Katalog · Sunucu IP'si Ortak", 180, Shared: true), catalog.Describe("hepsiburada:egress:catalog"));
        Assert.Equal(100, catalog.Describe("hepsiburada:ABCDEF:listing")!.LimitPerMinute);
        Assert.Equal(600, catalog.Describe("hepsiburada:ABCDEF:oms")!.LimitPerMinute);
        Assert.Equal(600, catalog.Describe("n11:ABCDEF:orders")!.LimitPerMinute);
        Assert.Equal(1, catalog.Describe("n11:ABCDEF:questions-list")!.LimitPerMinute);
        Assert.Equal(30, catalog.Describe("pazarama:ABCDEF:reads")!.LimitPerMinute);
        Assert.Equal(4.8, catalog.Describe("pazarama:ABCDEF:price-stock")!.LimitPerMinute);
        Assert.Equal(4, catalog.Describe("pttavm:ABCDEF:product-write")!.LimitPerMinute);
        // A bucket the code keeps no limit for shows usage only; an unknown marketplace is not shown.
        Assert.Null(catalog.Describe("hepsiburada:ABCDEF:something-new")!.LimitPerMinute);
        Assert.Null(catalog.Describe("unknown:key"));
    }

    [Fact]
    public void TheOrderLatencyColoursFollowThePlansTargets()
    {
        Assert.Equal("OK", QueueHealthService.LatencyStatus(5, QueueHealthService.WebhookProcessingTargetSeconds));
        Assert.Equal("WARN", QueueHealthService.LatencyStatus(10, QueueHealthService.WebhookProcessingTargetSeconds));
        Assert.Equal("BAD", QueueHealthService.LatencyStatus(10.1, QueueHealthService.WebhookProcessingTargetSeconds));
        Assert.Equal("WARN", QueueHealthService.LatencyStatus(200, QueueHealthService.PollingTargetSeconds));
        Assert.Null(QueueHealthService.LatencyStatus(null, QueueHealthService.PollingTargetSeconds));
    }

    // The attribution runs inside the lease's transaction for every leased job: with a busy hour of history it must stay
    // far below a database round trip (the number is printed for the report).
    [Fact]
    public void AttributingAWaitCostsMicroseconds()
    {
        var clock = new ManualClock(T0.AddHours(1));
        var pressure = new QuotaPressure();
        var ledger = new JobWaitLedger(new ManualClock(T0)) { Pressure = pressure, QuotaHold = new JobWaitLedger.QuotaHoldShape(TimeSpan.FromMinutes(15), 2), LanesFor = (_, _) => ["hot", "background"] };
        var pool = new LaneSlotPool(new Dictionary<string, int> { ["hot"] = 3, ["background"] = 2 }, [], clock: clock);
        ledger.SlotSpells = (names, from, to) => [.. pool.NoRoomSpells(names, from, to).Select(spell => new JobWaitLedger.Span(spell.From, spell.To))];
        var connections = Enumerable.Range(0, 7).Select(_ => Guid.NewGuid()).ToArray();
        // An hour of live-like work: ~250 runs of 1-30 s over seven connections and four groups.
        for (var index = 0; index < 250; index++)
        {
            var job = Guid.NewGuid();
            var start = T0.AddSeconds(index * 14);
            ledger.RunStarted(job, connections[index % 7], $"group-{index % 4}", start);
            ledger.RunEnded(job, start.AddSeconds(1 + (index % 30)));
        }
        pressure.Learn(connections[0], "TEST_JOB", ["k"]);
        var watch = Stopwatch.StartNew();
        const int calls = 20_000;
        for (var index = 0; index < calls; index++)
            ledger.Attribute(Query(connections[index % 7], $"group-{index % 4}", T0.AddMinutes(30), T0.AddMinutes(30).AddSeconds(index % 60)));
        watch.Stop();
        var microseconds = watch.Elapsed.TotalMilliseconds * 1000 / calls;
        output.WriteLine($"JobWaitLedger.Attribute: {microseconds:F1} µs per leased job ({calls} calls)");
        Assert.True(microseconds < 500, $"{microseconds} µs");
    }
}

// The same recording against PostgreSQL: the attempt row, the completion, the inbox, the bucket owners, the queue page's
// figures (one tenant never sees another's) and the unchanged lease decisions.
[Collection("postgres-integration")]
public sealed class QueueVisibilityPhase0PostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private async Task<(Tenant Tenant, string JobType)> TenantAsync(string code)
    {
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"{code}-{Guid.NewGuid():N}", DisplayName = code, Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, Version = 1 };
        await using var db = fixture.CreateContext();
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        return (tenant, $"PHASE0_{Guid.NewGuid():N}"[..38]);
    }

    private async Task<PlatformConnection> ConnectionAsync(Guid tenantId, string platform, string name)
    {
        var connection = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = tenantId, PublicId = Guid.CreateVersion7(), PlatformCode = platform, Environment = "PRODUCTION", DisplayName = name, ExternalStoreId = $"store-{Guid.NewGuid():N}", Status = "ACTIVE", ApiVersion = "V1", SettingsJson = "{}" };
        await using var db = fixture.CreateContext();
        db.PlatformConnections.Add(connection);
        await db.SaveChangesAsync();
        return connection;
    }

    private static IntegrationJob NewJob(Guid tenantId, Guid? connectionId, string jobType, DateTimeOffset at, int priority = 3, string? lockGroup = null, string trigger = "system")
    {
        var suffix = Guid.NewGuid().ToString("N");
        return new IntegrationJob
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            ConnectionId = connectionId,
            JobType = jobType,
            TriggerType = trigger,
            PayloadJson = "{}",
            PayloadVersion = 1,
            PayloadHash = "phase0",
            JobDedupKey = $"phase0-{suffix}",
            EffectIdempotencyKey = $"phase0-{suffix}",
            Priority = priority,
            Status = JobStatus.Pending,
            AvailableAt = at,
            MaxAttempts = 6,
            CorrelationId = $"phase0-{suffix}",
            CreatedAt = at,
            LockGroup = lockGroup ?? $"group-{suffix}",
            Version = 1
        };
    }

    private async Task CleanupAsync(Guid tenantId)
    {
        await using var cleanup = fixture.CreateContext();
        var connections = await cleanup.PlatformConnections.Where(x => x.TenantId == tenantId).Select(x => x.Id).ToArrayAsync();
        await cleanup.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM integration.rate_limit_bucket_connections WHERE \"ConnectionId\" = ANY({connections})");
        await cleanup.Orders.Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
        await cleanup.InboxMessages.Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
        await cleanup.JobAttempts.Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
        await cleanup.IntegrationJobs.Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
        await cleanup.IntegrationOutboxEvents.Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
        await cleanup.PlatformConnections.Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
        await cleanup.Tenants.Where(x => x.Id == tenantId).ExecuteDeleteAsync();
    }

    [PostgreSqlFact]
    public async Task TheAttemptRowCarriesItsEligibleMomentLaneAndWaitCausesAndUnknownStaysNull()
    {
        var (tenant, jobType) = await TenantAsync("phase0-attempt");
        try
        {
            var connection = await ConnectionAsync(tenant.Id, "TRENDYOL", "Mağaza");
            var now = DateTimeOffset.UtcNow;
            var measured = NewJob(tenant.Id, connection.Id, jobType, now.AddSeconds(-40), lockGroup: "orders");
            measured.AvailableAt = now.AddSeconds(-12);
            var plain = NewJob(tenant.Id, connection.Id, jobType, now.AddSeconds(-30));
            var failing = NewJob(tenant.Id, null, jobType, now.AddSeconds(-20));
            await using (var seed = fixture.CreateContext())
            {
                seed.IntegrationJobs.AddRange(measured, plain, failing);
                await seed.SaveChangesAsync();
            }
            JobWaitQuery? seen = null;
            await using (var db = fixture.CreateContext())
            {
                var leases = new JobLeaseService(db, fixture.TokenHasher, TimeProvider.System);
                var leased = await leases.TryLeaseAsync(TimeSpan.FromMinutes(2), null, null, CancellationToken.None, onlyJobId: measured.Id, includedJobTypes: [jobType],
                    attemptStart: new JobAttemptStart("hot", true, query => { seen = query; return new JobWaitBreakdown(28_000, 7_000, 0, 1_500, 2_000); }));
                Assert.NotNull(leased);
                Assert.Equal(("orders", 3), (leased.LockGroup, leased.Priority));
                Assert.Equal(measured.AvailableAt, leased.EligibleAt!.Value, TimeSpan.FromMilliseconds(1));
                Assert.NotNull(await leases.TryLeaseAsync(TimeSpan.FromMinutes(2), null, null, CancellationToken.None, onlyJobId: plain.Id, includedJobTypes: [jobType]));
                // An attribution that throws leaves the causes unknown; the lease itself goes through.
                Assert.NotNull(await leases.TryLeaseAsync(TimeSpan.FromMinutes(2), null, null, CancellationToken.None, onlyJobId: failing.Id, includedJobTypes: [jobType],
                    attemptStart: new JobAttemptStart("background", false, _ => throw new InvalidOperationException("boom"))));
            }
            Assert.NotNull(seen);
            Assert.True(seen!.Fresh);
            Assert.Equal((connection.Id, "orders", jobType), (seen.ConnectionId!.Value, seen.LockGroup, seen.JobType));

            await using var verification = fixture.CreateContext();
            var attempts = await verification.JobAttempts.AsNoTracking().Where(x => x.TenantId == tenant.Id).ToDictionaryAsync(x => x.JobId);
            var first = attempts[measured.Id];
            Assert.Equal(measured.AvailableAt, first.EligibleAt!.Value, TimeSpan.FromMilliseconds(1));
            Assert.Equal(("hot", true, 28_000, 7_000, 0, 1_500, 2_000), (first.Lane, first.Borrowed!.Value, first.NotDueMs!.Value, first.WaitLockMs!.Value, first.WaitBackoffMs!.Value, first.WaitQuotaHoldMs!.Value, first.WaitSlotMs!.Value));
            Assert.Null(first.QuotaWaitMs);
            var second = attempts[plain.Id];
            Assert.Equal(plain.CreatedAt, second.EligibleAt!.Value, TimeSpan.FromMilliseconds(1));
            Assert.Equal((null, (bool?)null, (int?)null, (int?)null, (int?)null), (second.Lane, second.Borrowed, second.NotDueMs, second.WaitLockMs, second.WaitSlotMs));
            var third = attempts[failing.Id];
            Assert.Equal(("background", false), (third.Lane, third.Borrowed!.Value));
            Assert.Equal(((int?)null, (int?)null, (int?)null), (third.NotDueMs, third.WaitLockMs, third.WaitSlotMs));
        }
        finally { await CleanupAsync(tenant.Id); }
    }

    [PostgreSqlFact]
    public async Task TheCompletionWritesTheRunsTokenWaitsAndNothingWhenNoneWasMeasured()
    {
        var (tenant, jobType) = await TenantAsync("phase0-complete");
        try
        {
            var now = DateTimeOffset.UtcNow;
            var a = NewJob(tenant.Id, null, jobType, now.AddSeconds(-5));
            var b = NewJob(tenant.Id, null, jobType, now.AddSeconds(-4));
            await using (var seed = fixture.CreateContext())
            {
                seed.IntegrationJobs.AddRange(a, b);
                await seed.SaveChangesAsync();
            }
            foreach (var (job, run) in new[] { (a, new JobAttemptRun(4_200, 3)), (b, (JobAttemptRun?)null) })
            {
                await using var db = fixture.CreateContext();
                var leases = new JobLeaseService(db, fixture.TokenHasher, TimeProvider.System);
                var leased = await leases.TryLeaseAsync(TimeSpan.FromMinutes(2), null, null, CancellationToken.None, onlyJobId: job.Id, includedJobTypes: [jobType]);
                await using var completion = fixture.CreateContext();
                Assert.True(await new JobLeaseService(completion, fixture.TokenHasher, TimeProvider.System).CompleteAsync(leased!.Id, leased.LeaseToken, JobExecutionResult.Success(), CancellationToken.None, run));
            }
            await using var verification = fixture.CreateContext();
            var attempts = await verification.JobAttempts.AsNoTracking().Where(x => x.TenantId == tenant.Id).ToDictionaryAsync(x => x.JobId);
            Assert.Equal((4_200, 3), (attempts[a.Id].QuotaWaitMs!.Value, attempts[a.Id].QuotaWaits!.Value));
            Assert.Equal(((int?)null, (int?)null), (attempts[b.Id].QuotaWaitMs, attempts[b.Id].QuotaWaits));
        }
        finally { await CleanupAsync(tenant.Id); }
    }

    [PostgreSqlFact]
    public async Task TheInboxKeepsTheMarketplacesEventTimeOrNull()
    {
        var (tenant, _) = await TenantAsync("phase0-inbox");
        try
        {
            var connection = await ConnectionAsync(tenant.Id, "TRENDYOL", "Mağaza");
            var now = DateTimeOffset.UtcNow;
            var eventAt = now.AddSeconds(-14);
            foreach (var (message, at) in new[] { ($"m-{Guid.NewGuid():N}", (DateTimeOffset?)eventAt), ($"m-{Guid.NewGuid():N}", null) })
            {
                await using var db = fixture.CreateContext();
                var job = NewJob(tenant.Id, connection.Id, MarketplaceHub.Application.MarketplaceJobTypes.ForPlatform("TRENDYOL", MarketplaceHub.Application.MarketplaceJobTypes.WebhookIngest), now, priority: 0, trigger: "webhook");
                var outcome = await WebhookInboxWriter.InsertAsync(db, new InboxMessage { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Source = "TRENDYOL_WEBHOOK", ExternalMessageId = message, PayloadHash = "h", ReceivedAt = now, EventAt = at }, job, CancellationToken.None);
                Assert.True(outcome.InboxCreated);
            }
            await using var verification = fixture.CreateContext();
            var rows = await verification.InboxMessages.AsNoTracking().Where(x => x.TenantId == tenant.Id).Select(x => x.EventAt).ToListAsync();
            Assert.Equal(2, rows.Count);
            Assert.Contains(rows, value => value is null);
            Assert.Contains(rows, value => value is { } stored && (stored - eventAt).Duration() < TimeSpan.FromMilliseconds(1));
        }
        finally { await CleanupAsync(tenant.Id); }
    }

    // The quota card: each bucket a connection used, its last hour against the code's limit, one grouped query; another
    // tenant's buckets never show, the server-wide Hepsiburada catalogue bucket shows to both as shared.
    [PostgreSqlFact]
    public async Task TheQuotaCardShowsEveryBucketOfTheTenantWithItsLastHourAndNoOtherTenants()
    {
        var (tenant, _) = await TenantAsync("phase0-quota");
        var (other, _) = await TenantAsync("phase0-quota-other");
        var keys = new List<string>();
        try
        {
            var hb = await ConnectionAsync(tenant.Id, "HEPSIBURADA", "HB Mağaza");
            var pazarama = await ConnectionAsync(tenant.Id, "PAZARAMA", "Pazarama Mağaza");
            var otherHb = await ConnectionAsync(other.Id, "HEPSIBURADA", "Başka Kiracı");
            var account = Guid.NewGuid().ToString("N")[..16].ToUpperInvariant();
            var listing = $"hepsiburada:{account}:listing";
            var reads = $"pazarama:{account}:reads";
            var idle = $"pazarama:{account}:orders";
            var catalog = "hepsiburada:egress:catalog";
            var foreign = $"hepsiburada:OTHER{account[5..]}:oms";
            keys.AddRange([listing, reads, idle, foreign]);
            var now = DateTimeOffset.UtcNow;
            var minute = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, TimeSpan.Zero);
            await using (var seed = fixture.CreateContext())
            {
                await seed.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM integration.rate_limit_usage WHERE \"Key\" = {catalog}");
                await seed.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO integration.rate_limit_usage ("Key", "Minute", "Requests") VALUES
                    ({listing}, {minute.AddMinutes(-3)}, 81), ({listing}, {minute.AddMinutes(-2)}, 19), ({listing}, {minute.AddHours(-3)}, 99),
                    ({reads}, {minute.AddMinutes(-10)}, 27), ({reads}, {minute.AddMinutes(-9)}, 3),
                    ({catalog}, {minute.AddMinutes(-1)}, 43),
                    ({foreign}, {minute.AddMinutes(-1)}, 7)
                    """);
                await QuotaBucketOwners.RecordAsync(seed, hb.Id, [listing, catalog], now, CancellationToken.None);
                await QuotaBucketOwners.RecordAsync(seed, pazarama.Id, [reads, idle], now, CancellationToken.None);
                await QuotaBucketOwners.RecordAsync(seed, otherHb.Id, [foreign, catalog], now, CancellationToken.None);
            }
            await using var db = fixture.CreateContext();
            var service = new QueueHealthService(db, Options.Create(new TrendyolOptions()), TimeProvider.System);
            var quota = await service.QuotaAsync(tenant.Id, CancellationToken.None);
            Assert.Equal(["HEPSIBURADA:CATALOG", "HEPSIBURADA:LISTING", "PAZARAMA:ORDERS", "PAZARAMA:READS"], quota.Select(row => row.Bucket).Order(StringComparer.Ordinal));
            var listingRow = Assert.Single(quota, row => row.Bucket == "HEPSIBURADA:LISTING");
            Assert.Equal(("HB Mağaza", 100d, 100, 81, 1.7, false), (listingRow.StoreName, listingRow.LimitPerMinute!.Value, listingRow.LastHourRequests, listingRow.PeakMinuteRequests, listingRow.AveragePerMinute, listingRow.Shared));
            var readsRow = Assert.Single(quota, row => row.Bucket == "PAZARAMA:READS");
            Assert.Equal((30d, 30, 27, 0.5), (readsRow.LimitPerMinute!.Value, readsRow.LastHourRequests, readsRow.PeakMinuteRequests, readsRow.AveragePerMinute));
            var idleRow = Assert.Single(quota, row => row.Bucket == "PAZARAMA:ORDERS");
            Assert.Equal((0, 0), (idleRow.LastHourRequests, idleRow.PeakMinuteRequests));
            Assert.True(Assert.Single(quota, row => row.Bucket == "HEPSIBURADA:CATALOG").Shared);
            var otherQuota = await service.QuotaAsync(other.Id, CancellationToken.None);
            Assert.Equal(["HEPSIBURADA:CATALOG", "HEPSIBURADA:OMS"], otherQuota.Select(row => row.Bucket).Order(StringComparer.Ordinal));
            Assert.DoesNotContain(otherQuota, row => row.StoreName.Contains("HB Mağaza", StringComparison.Ordinal));
        }
        finally
        {
            await using (var cleanup = fixture.CreateContext())
                await cleanup.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM integration.rate_limit_usage WHERE \"Key\" = ANY({keys.ToArray()})");
            await CleanupAsync(tenant.Id);
            await CleanupAsync(other.Id);
        }
    }

    [PostgreSqlFact]
    public async Task ABucketOwnerIsWrittenOnceInItsRefreshTimeAndTheRetentionDropsOldPairs()
    {
        var (tenant, _) = await TenantAsync("phase0-owners");
        try
        {
            var connection = await ConnectionAsync(tenant.Id, "N11", "N11 Mağaza");
            var now = DateTimeOffset.UtcNow;
            var key = $"n11:{Guid.NewGuid():N}:orders";
            var old = $"n11:{Guid.NewGuid():N}:products";
            Assert.Equal([key], QuotaBucketOwners.Due(connection.Id, [key, key], now));
            await using var db = fixture.CreateContext();
            await QuotaBucketOwners.RecordAsync(db, connection.Id, [key], now, CancellationToken.None);
            Assert.Empty(QuotaBucketOwners.Due(connection.Id, [key], now.AddHours(1)));
            Assert.Equal([key], QuotaBucketOwners.Due(connection.Id, [key], now.AddHours(7)));
            await QuotaBucketOwners.RecordAsync(db, connection.Id, [old], now.AddDays(-31), CancellationToken.None);
            await new JobRetentionService(db, TimeProvider.System).PurgeRateLimitUsageAsync(maximumBatches: 10, CancellationToken.None);
            var kept = await db.Database.SqlQuery<string>($"SELECT \"Key\" AS \"Value\" FROM integration.rate_limit_bucket_connections WHERE \"ConnectionId\" = {connection.Id}").ToListAsync();
            Assert.Equal([key], kept);
        }
        finally { await CleanupAsync(tenant.Id); }
    }

    // The order block: the last 24 hours' new orders per marketplace, split by the job that stored them, its figures equal to
    // the stored times; first imports, history reads and other tenants are left out; a marketplace without orders shows none.
    [PostgreSqlFact]
    public async Task TheOrderBlockMeasuresOrderTimeToPanelPerSourceAndLeavesOtherTenantsOut()
    {
        var (tenant, _) = await TenantAsync("phase0-latency");
        var (other, _) = await TenantAsync("phase0-latency-other");
        try
        {
            var trendyol = await ConnectionAsync(tenant.Id, "TRENDYOL", "TY");
            await ConnectionAsync(tenant.Id, "PAZARAMA", "PZ");
            var otherTrendyol = await ConnectionAsync(other.Id, "TRENDYOL", "Başka");
            var now = DateTimeOffset.UtcNow.AddMinutes(-30);
            var webhookJobs = new List<IntegrationJob>();
            var orders = new List<Order>();
            Order NewOrder(Guid tenantId, Guid connectionId, DateTimeOffset orderedAt, DateTimeOffset createdAt, Guid? jobId) => new()
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                ConnectionId = connectionId,
                ExternalOrderId = $"o-{Guid.NewGuid():N}",
                OrderNumber = $"n-{Guid.NewGuid():N}",
                Currency = "TRY",
                OrderedAt = orderedAt,
                LastRemoteModifiedAt = orderedAt,
                CustomerSnapshotJson = "{}",
                ShipmentAddressSnapshotJson = "{}",
                InvoiceAddressSnapshotJson = "{}",
                DerivedStatus = "NEW",
                CreatedAt = createdAt,
                UpdatedAt = createdAt,
                CreatedByJobId = jobId
            };
            // Three webhook orders: the marketplace sent each 20/30/40 s after the order, the panel stored it 1/2/3 s later.
            for (var index = 0; index < 3; index++)
            {
                var ordered = now.AddMinutes(index);
                var job = NewJob(tenant.Id, trendyol.Id, "TRENDYOL_WEBHOOK_INGEST", ordered.AddSeconds(20 + (10 * index)), priority: 0, trigger: "webhook");
                webhookJobs.Add(job);
                orders.Add(NewOrder(tenant.Id, trendyol.Id, ordered, job.CreatedAt.AddSeconds(1 + index), job.Id));
            }
            // Two read orders (60 and 240 s), one without a known job (100 s), one of a first import and one 9-day-old history read.
            var read = NewJob(tenant.Id, trendyol.Id, "TRENDYOL_ORDER_SYNC", now, priority: 0, trigger: "scheduled");
            var initial = NewJob(tenant.Id, trendyol.Id, "TRENDYOL_ORDER_SYNC", now, priority: MarketplaceHub.Application.JobPriorityPolicy.InitialDataSync);
            orders.Add(NewOrder(tenant.Id, trendyol.Id, now, now.AddSeconds(60), read.Id));
            orders.Add(NewOrder(tenant.Id, trendyol.Id, now, now.AddSeconds(240), read.Id));
            orders.Add(NewOrder(tenant.Id, trendyol.Id, now, now.AddSeconds(100), null));
            orders.Add(NewOrder(tenant.Id, trendyol.Id, now, now.AddSeconds(7_000), initial.Id));
            orders.Add(NewOrder(tenant.Id, trendyol.Id, now.AddDays(-9), now, read.Id));
            // Another tenant's slow order and an order stored two days ago.
            orders.Add(NewOrder(other.Id, otherTrendyol.Id, now, now.AddSeconds(900), null));
            orders.Add(NewOrder(tenant.Id, trendyol.Id, now.AddDays(-2), now.AddDays(-2).AddSeconds(5), null));
            await using (var seed = fixture.CreateContext())
            {
                seed.IntegrationJobs.AddRange([.. webhookJobs, read, initial]);
                seed.Orders.AddRange(orders);
                await seed.SaveChangesAsync();
            }
            await using var db = fixture.CreateContext();
            var latency = await new QueueHealthService(db, Options.Create(new TrendyolOptions()), TimeProvider.System).OrderLatencyAsync(tenant.Id, CancellationToken.None);

            static double Percentile(double[] values, double fraction)
            {
                var sorted = values.Order().ToArray();
                var position = fraction * (sorted.Length - 1);
                var lower = (int)Math.Floor(position);
                var upper = (int)Math.Ceiling(position);
                return Math.Round(sorted[lower] + ((sorted[upper] - sorted[lower]) * (position - lower)), 1);
            }
            var allTotals = new double[] { 21, 32, 43, 60, 240, 100 };
            var all = Assert.Single(latency, row => row.PlatformCode == "TRENDYOL" && row.Source == "ALL");
            Assert.Equal((6, Percentile(allTotals, 0.5), Percentile(allTotals, 0.95)), (all.Orders, all.P50Seconds, all.P95Seconds));
            Assert.Null(all.Status);
            var webhook = Assert.Single(latency, row => row.PlatformCode == "TRENDYOL" && row.Source == "WEBHOOK");
            Assert.Equal((3, 32d, Percentile([21, 32, 43], 0.95)), (webhook.Orders, webhook.P50Seconds, webhook.P95Seconds));
            Assert.Equal((30d, Percentile([20, 30, 40], 0.95), 2d, Percentile([1, 2, 3], 0.95)), (webhook.NoticeP50Seconds, webhook.NoticeP95Seconds, webhook.ProcessingP50Seconds, webhook.ProcessingP95Seconds));
            Assert.Equal((5d, "OK"), (webhook.TargetSeconds, webhook.Status));
            var polling = Assert.Single(latency, row => row.PlatformCode == "TRENDYOL" && row.Source == "POLLING");
            Assert.Equal((2, 150d, Percentile([60, 240], 0.95), "WARN"), (polling.Orders, polling.P50Seconds, polling.P95Seconds, polling.Status));
            Assert.Null(polling.NoticeP50Seconds);
            var pazarama = Assert.Single(latency, row => row.PlatformCode == "PAZARAMA");
            Assert.Equal(("ALL", 0, (double?)null), (pazarama.Source, pazarama.Orders, pazarama.P95Seconds));
            var otherLatency = await new QueueHealthService(db, Options.Create(new TrendyolOptions()), TimeProvider.System).OrderLatencyAsync(other.Id, CancellationToken.None);
            Assert.Equal((1, 900d), (Assert.Single(otherLatency).Orders, otherLatency[0].P95Seconds!.Value));
        }
        finally
        {
            await CleanupAsync(tenant.Id);
            await CleanupAsync(other.Id);
        }
    }

    // The lane table: a recorded attempt is measured from its own EligibleAt (a later retry rewrote the job's AvailableAt),
    // and the lane's wait causes add up its recorded attempts; none recorded, none shown.
    [PostgreSqlFact]
    public async Task TheLaneTableMeasuresFromTheAttemptsEligibleMomentAndAddsUpItsCauses()
    {
        var (tenant, _) = await TenantAsync("phase0-lanes");
        try
        {
            var connection = await ConnectionAsync(tenant.Id, "TRENDYOL", "TY");
            var now = DateTimeOffset.UtcNow.AddMinutes(-10);
            var job = NewJob(tenant.Id, connection.Id, MarketplaceJobTypes.ReturnSync, now.AddSeconds(-60));
            // The job retried later: its AvailableAt now lies after its first attempt (the old rule could not measure it).
            job.AvailableAt = now.AddMinutes(5);
            job.AttemptCount = 2;
            job.Status = JobStatus.RetryScheduled;
            await using (var seed = fixture.CreateContext())
            {
                seed.IntegrationJobs.Add(job);
                seed.JobAttempts.AddRange(
                    new JobAttempt { Id = Guid.NewGuid(), TenantId = tenant.Id, JobId = job.Id, AttemptNumber = 1, StartedAt = now, CompletedAt = now.AddSeconds(1), EligibleAt = now.AddSeconds(-40), Lane = "returns-read", Borrowed = false, WaitLockMs = 25_000, WaitBackoffMs = 0, WaitQuotaHoldMs = 0, WaitSlotMs = 5_000 },
                    new JobAttempt { Id = Guid.NewGuid(), TenantId = tenant.Id, JobId = job.Id, AttemptNumber = 2, StartedAt = now.AddMinutes(1), EligibleAt = now.AddMinutes(1).AddSeconds(-10), Lane = "returns-read", Borrowed = false, WaitLockMs = 0, WaitBackoffMs = 4_000, WaitQuotaHoldMs = 0, WaitSlotMs = null });
                await seed.SaveChangesAsync();
            }
            await using var db = fixture.CreateContext();
            var lanes = await new QueueHealthService(db, Options.Create(new TrendyolOptions()), TimeProvider.System).LanesAsync(tenant.Id, TimeSpan.FromHours(1), CancellationToken.None);
            var returns = Assert.Single(lanes, row => row.Lane == "RETURNS_READ");
            Assert.Equal((2, 40d, 40d), (returns.Attempts, returns.AverageWaitSeconds!.Value, returns.P95WaitSeconds!.Value));
            Assert.Equal(new LaneWaitCausesView(2, 50, 25, 4, 0, 5, 16), returns.WaitCauses);
            Assert.All(lanes.Where(row => row.Lane != "RETURNS_READ"), row => Assert.Null(row.WaitCauses));
        }
        finally { await CleanupAsync(tenant.Id); }
    }

    // No behaviour change: one mixed job stream (priorities, ages, a busy lock group, a quota backoff, a deferral, a retry) is
    // leased and completed twice on a simulated clock, once as before and once with every phase 0 recording on; the jobs are
    // leased in the same order with the same outcomes and next due times.
    [PostgreSqlFact]
    public async Task TheSameJobStreamGetsTheSameLeaseDecisionsWithTheRecordingOn()
    {
        // The same job ids both times: retry and deferral spreads are hashed from the id.
        var ids = Enumerable.Range(0, 8).Select(_ => Guid.CreateVersion7()).ToArray();
        var plain = await RunStreamAsync(recording: false, ids);
        var recorded = await RunStreamAsync(recording: true, ids);
        // Every job ended once, the deferral and the two retries came back, and B's returns job waited for the running one.
        Assert.Equal(7, plain.Count(entry => entry.Contains(":Succeeded:", StringComparison.Ordinal)));
        Assert.Equal(3, plain.Count(entry => entry.Contains(":RetryScheduled:", StringComparison.Ordinal)));
        int Step(string index) => int.Parse(plain.First(entry => entry.Split(':')[1] == index).Split(':')[0], System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(Step("6") > Step("0"), string.Join(" | ", plain));
        Assert.Equal(plain, recorded);
    }

    private async Task<List<string>> RunStreamAsync(bool recording, Guid[] ids)
    {
        var (tenant, jobType) = await TenantAsync(recording ? "phase0-stream-on" : "phase0-stream-off");
        var start = new DateTimeOffset(2026, 11, 27, 9, 0, 0, TimeSpan.Zero);
        var clock = new ManualClock(start);
        try
        {
            var a = (await ConnectionAsync(tenant.Id, "TRENDYOL", "A")).Id;
            var b = (await ConnectionAsync(tenant.Id, "HEPSIBURADA", "B")).Id;
            // index: connection, priority, seconds before start it became runnable, lock group, scripted outcome
            var script = new (Guid? Connection, int Priority, int Age, string Group, JobExecutionResult Result)[]
            {
                (a, 3, 50, "orders", JobExecutionResult.Success()),
                (a, 3, 45, "orders", JobExecutionResult.Deferred("SYNC_LOCK_BUSY", retryAfter: TimeSpan.FromSeconds(5))),
                (b, 0, 10, "orders", new JobExecutionResult(JobCompletionKind.Retry, "HB_QUOTA", RetryAfter: TimeSpan.FromSeconds(20), BackOffConnection: true)),
                (b, 3, 40, "orders", JobExecutionResult.Success()),
                (null, 4, 30, "none", JobExecutionResult.Retry("REMOTE_TIMEOUT")),
                (a, 2, 5, "returns", JobExecutionResult.Success()),
                (b, 3, 60, "returns", JobExecutionResult.Success())
            };
            var jobs = script.Select((item, index) =>
            {
                var job = NewJob(tenant.Id, item.Connection, jobType, start.AddSeconds(-item.Age), item.Priority, item.Group);
                job.Id = ids[index];
                job.JobDedupKey = $"stream-{index}";
                return job;
            }).ToList();
            // Another slot runs a job of B's "returns" group until 30 s after the start (its lease then lapses): B's
            // returns job waits for it.
            var blocker = NewJob(tenant.Id, b, $"{jobType[..30]}_RUN", start.AddMinutes(-1), 3, "returns");
            blocker.Id = ids[^1];
            blocker.Status = JobStatus.Leased;
            blocker.AttemptCount = 1;
            blocker.LeaseTokenHash = "running";
            blocker.LeaseExpiresAt = start.AddSeconds(30);
            await using (var seed = fixture.CreateContext())
            {
                seed.IntegrationJobs.AddRange([.. jobs, blocker]);
                await seed.SaveChangesAsync();
            }
            var ledger = new JobWaitLedger(new ManualClock(start.AddMinutes(-5))) { LanesFor = (_, _) => ["background"], SlotSpells = (_, _, _) => [] };
            var log = new List<string>();
            var completions = new Dictionary<Guid, int>();
            for (var step = 0; step < 40 && log.Count < 30; step++)
            {
                await using var db = fixture.CreateContext();
                var leases = new JobLeaseService(db, fixture.TokenHasher, clock);
                var leased = await leases.TryLeaseAsync(TimeSpan.FromMinutes(2), null, null, CancellationToken.None, includedJobTypes: [jobType],
                    fairness: new JobFairness(TimeSpan.FromMinutes(15), [], []),
                    attemptStart: recording ? new JobAttemptStart("background", false, ledger.Attribute) : null);
                if (leased is not null)
                {
                    var index = jobs.FindIndex(job => job.Id == leased.Id);
                    if (recording) ledger.RunStarted(leased.Id, leased.ConnectionId, leased.LockGroup, clock.Now);
                    clock.Now = clock.Now.AddSeconds(2);
                    var times = completions[leased.Id] = completions.GetValueOrDefault(leased.Id) + 1;
                    var result = times == 1 ? script[index].Result : JobExecutionResult.Success();
                    await using var completion = fixture.CreateContext();
                    Assert.True(await new JobLeaseService(completion, fixture.TokenHasher, clock).CompleteAsync(leased.Id, leased.LeaseToken, result, CancellationToken.None,
                        recording ? new JobAttemptRun(0, 0) : null));
                    if (recording)
                    {
                        ledger.RunEnded(leased.Id, clock.Now);
                        ledger.BackoffRecorded(leased, result, clock.Now);
                    }
                    var stored = await completion.IntegrationJobs.AsNoTracking().SingleAsync(job => job.Id == leased.Id);
                    log.Add($"{step}:{index}:{stored.Status}:{stored.AttemptCount}:{(stored.AvailableAt - start).TotalSeconds:F3}");
                }
                else clock.Now = clock.Now.AddSeconds(5);
            }
            return log;
        }
        finally { await CleanupAsync(tenant.Id); }
    }
}
