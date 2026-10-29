using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Jobs;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Background-processing behaviour of the 20 October 2026 deep audit that needs the real schema. Tests that act on
// every tenant (retention, sweep) run inside a transaction that is rolled back.
[Collection("postgres-integration")]
public sealed class BackgroundProcessingPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    [PostgreSqlFact]
    public async Task OnlyTheFirstLeaseOfAJobTellsThePanelAndVersionConflictsSpendAttemptsAfterThree()
    {
        var tenant = NewTenant("bg-lease");
        var job = NewJob(tenant.Id, "TEST_LEASE");
        job.MaxAttempts = 6;
        await using (var setup = fixture.CreateContext())
        {
            setup.Tenants.Add(tenant);
            setup.IntegrationJobs.Add(job);
            await setup.SaveChangesAsync();
        }

        try
        {
            var conflict = MarketplaceHub.Worker.Worker.ExceptionResult(new DbUpdateConcurrencyException("conflict"));
            for (var round = 1; round <= JobConflictPolicy.DeferredVersionConflicts + 1; round++)
            {
                await using var db = fixture.CreateContext();
                var leases = new JobLeaseService(db, fixture.TokenHasher, fixture.TimeProvider);
                await db.IntegrationJobs.Where(x => x.Id == job.Id).ExecuteUpdateAsync(setters => setters.SetProperty(x => x.AvailableAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
                var lease = await leases.TryLeaseAsync(TimeSpan.FromMinutes(2), null, null, CancellationToken.None, onlyJobId: job.Id);
                Assert.NotNull(lease);
                Assert.True(await leases.CompleteAsync(job.Id, lease.LeaseToken, conflict, CancellationToken.None));
            }

            await using var verification = fixture.CreateContext();
            var stored = await verification.IntegrationJobs.AsNoTracking().SingleAsync(x => x.Id == job.Id);
            // Three conflicts went without an attempt; the fourth spent one.
            Assert.Equal(1, stored.AttemptCount);
            Assert.Equal(JobConflictPolicy.DeferredVersionConflicts + 1, stored.ConflictCount);
            Assert.Equal(JobStatus.RetryScheduled, stored.Status);
            var events = await verification.IntegrationOutboxEvents.AsNoTracking().Where(x => x.TenantId == tenant.Id && x.AggregateId == job.Id).ToListAsync();
            Assert.All(events, item => Assert.Equal("jobs", item.ResourceType));
            // One lease event (the first) and one event per completion.
            Assert.Single(events, item => item.PayloadJson.Contains("LEASED", StringComparison.Ordinal));
            Assert.Equal(JobConflictPolicy.DeferredVersionConflicts + 2, events.Count);
        }
        finally
        {
            await CleanupAsync(tenant.Id);
        }
    }

    [PostgreSqlFact]
    public async Task TheLeaseChecksTheLockGroupStoredOnTheJob()
    {
        var tenant = NewTenant("bg-lockgroup");
        var connectionId = Guid.CreateVersion7();
        var runningReturns = NewJob(tenant.Id, MarketplaceJobTypes.ReturnSync);
        runningReturns.ConnectionId = connectionId;
        runningReturns.Status = JobStatus.Leased;
        runningReturns.AttemptCount = 1;
        runningReturns.LeaseTokenHash = "running";
        runningReturns.LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(2);
        var returnCheck = NewJob(tenant.Id, MarketplaceJobTypes.ExternalEffectReconcile);
        returnCheck.ConnectionId = connectionId;
        returnCheck.LockGroup = "returns";
        var packageCheck = NewJob(tenant.Id, MarketplaceJobTypes.ExternalEffectReconcile);
        packageCheck.ConnectionId = connectionId;
        packageCheck.LockGroup = "orders";
        await using (var setup = fixture.CreateContext())
        {
            setup.Tenants.Add(tenant);
            setup.IntegrationJobs.AddRange(runningReturns, returnCheck, packageCheck);
            await setup.SaveChangesAsync();
        }

        try
        {
            await using var db = fixture.CreateContext();
            var leases = new JobLeaseService(db, fixture.TokenHasher, fixture.TimeProvider);

            Assert.Null(await leases.TryLeaseAsync(TimeSpan.FromMinutes(2), null, null, CancellationToken.None, onlyJobId: returnCheck.Id));
            var lease = await leases.TryLeaseAsync(TimeSpan.FromMinutes(2), null, null, CancellationToken.None, onlyJobId: packageCheck.Id);
            Assert.Equal(packageCheck.Id, lease?.Id);
            Assert.NotNull(lease!.QueueWait);
        }
        finally
        {
            await CleanupAsync(tenant.Id);
        }
    }

    [PostgreSqlFact]
    public async Task AWebhookWaitsAMomentForTheOrderLockWhileALongerJobTriesOnce()
    {
        // Audit 2026-10-24 Q1: a notice arriving in the same millisecond as its sibling waits for the sibling's lock
        // instead of being deferred 30 s.
        var connectionId = Guid.CreateVersion7();
        await using var holderDb = fixture.CreateContext();
        var holder = await MarketplaceSyncExecutionLock.TryAcquireAsync(holderDb, connectionId, MarketplaceJobTypes.WebhookIngest, CancellationToken.None, "{}");
        Assert.NotNull(holder);
        try
        {
            await using var waiterDb = fixture.CreateContext();
            Assert.Null(await MarketplaceSyncExecutionLock.AcquireForJobAsync(waiterDb, connectionId, MarketplaceJobTypes.OrderSync, "{}", fixture.TimeProvider, CancellationToken.None));

            var release = Task.Run(async () => { await Task.Delay(TimeSpan.FromMilliseconds(600)); await holder.DisposeAsync(); });
            var started = System.Diagnostics.Stopwatch.StartNew();
            await using var waited = await MarketplaceSyncExecutionLock.AcquireForJobAsync(waiterDb, connectionId, MarketplaceJobTypes.WebhookIngest, "{}", fixture.TimeProvider, CancellationToken.None);
            await release;
            Assert.NotNull(waited);
            Assert.True(started.Elapsed < MarketplaceSyncExecutionLock.ShortJobLockWait, started.Elapsed.ToString());
        }
        finally
        {
            await holder.DisposeAsync();
        }
    }

    [PostgreSqlFact]
    public async Task ADeferredJobComesBackSpreadAndWithoutSpendingAnAttempt()
    {
        var tenant = NewTenant("bg-deferral");
        var job = NewJob(tenant.Id, MarketplaceJobTypes.WebhookIngest);
        await using (var setup = fixture.CreateContext())
        {
            setup.Tenants.Add(tenant);
            setup.IntegrationJobs.Add(job);
            await setup.SaveChangesAsync();
        }

        try
        {
            await using var db = fixture.CreateContext();
            var leases = new JobLeaseService(db, fixture.TokenHasher, fixture.TimeProvider);
            await db.IntegrationJobs.Where(x => x.Id == job.Id).ExecuteUpdateAsync(setters => setters.SetProperty(x => x.AvailableAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
            var lease = await leases.TryLeaseAsync(TimeSpan.FromMinutes(2), null, null, CancellationToken.None, onlyJobId: job.Id);
            Assert.NotNull(lease);
            var before = DateTimeOffset.UtcNow;
            var busy = MarketplaceSyncExecutionLock.ContentionResult(MarketplaceJobTypes.WebhookIngest, "{}", "webhook", sameTypeRunning: false);
            Assert.True(await leases.CompleteAsync(job.Id, lease.LeaseToken, busy, CancellationToken.None));
            var after = DateTimeOffset.UtcNow;

            await using var verification = fixture.CreateContext();
            var stored = await verification.IntegrationJobs.AsNoTracking().SingleAsync(x => x.Id == job.Id);
            Assert.Equal(JobStatus.RetryScheduled, stored.Status);
            Assert.Equal(0, stored.AttemptCount);
            // One second asked, spread over 1-4 s: never the old 30 s, never sooner than asked.
            Assert.InRange(stored.AvailableAt, before.AddSeconds(1), after.AddSeconds(4));
        }
        finally
        {
            await CleanupAsync(tenant.Id);
        }
    }

    [PostgreSqlFact]
    public async Task RetentionRemovesGuardsThatGuardNothingAndRetiredRows()
    {
        var now = DateTimeOffset.UtcNow;
        var old = now.AddDays(-100);
        var tenant = NewTenant("bg-retention");
        IntegrationJob Guard(string jobType, string dedup, JobStatus status, int attempts = 1, string? error = null)
        {
            var job = NewJob(tenant.Id, jobType);
            job.JobDedupKey = dedup;
            job.Status = status;
            job.AttemptCount = attempts;
            job.LastErrorCode = error;
            job.CreatedAt = old;
            job.CompletedAt = old;
            return job;
        }
        var versionedDispatch = Guard(MarketplaceJobTypes.StockProjectionDispatch, $"stock-projection:{Guid.NewGuid():N}:{Guid.NewGuid():N}:v7", JobStatus.Succeeded);
        var versionedPush = Guard(MarketplaceJobTypes.PriceInventorySync, $"price-inventory:{Guid.NewGuid():N}:revision:ABC", JobStatus.Succeeded);
        var cancelledBeforeRun = Guard(MarketplaceJobTypes.ShipmentAction, $"shipment:{Guid.NewGuid():N}", JobStatus.Cancelled, attempts: 0);
        var blockedBeforeSend = Guard(MarketplaceJobTypes.ShipmentAction, $"shipment:{Guid.NewGuid():N}", JobStatus.Blocked, error: "CONNECTION_INACTIVE");
        var sentShipment = Guard(MarketplaceJobTypes.ShipmentAction, $"shipment:{Guid.NewGuid():N}", JobStatus.Succeeded);
        var inboxKey = $"bg-{Guid.NewGuid():N}";
        var usageKey = $"bg-retired:{Guid.NewGuid():N}";
        var settledEffect = new ExternalEffectRecord { Id = Guid.CreateVersion7(), TenantId = tenant.Id, EffectType = "SHIPMENT", IdempotencyKey = $"bg-{Guid.NewGuid():N}", CreatedAt = old, CompletedAt = old, Outcome = "APPLIED" };
        var openEffect = new ExternalEffectRecord { Id = Guid.CreateVersion7(), TenantId = tenant.Id, EffectType = "SHIPMENT", IdempotencyKey = $"bg-{Guid.NewGuid():N}", CreatedAt = old };

        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        db.Tenants.Add(tenant);
        db.IntegrationJobs.AddRange(versionedDispatch, versionedPush, cancelledBeforeRun, blockedBeforeSend, sentShipment);
        db.InboxMessages.Add(new InboxMessage { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Source = "TRENDYOL_WEBHOOK", ExternalMessageId = inboxKey, PayloadHash = "h", ReceivedAt = old });
        db.ExternalEffectRecords.AddRange(settledEffect, openEffect);
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO integration.rate_limit_usage (\"Key\", \"Minute\", \"Requests\") VALUES ({usageKey}, {now.AddDays(-3)}, 4)");
        db.ChangeTracker.Clear();

        var retention = new JobRetentionService(db, fixture.TimeProvider);
        await retention.PurgeAsync(TimeSpan.FromDays(14), TimeSpan.FromDays(30), TimeSpan.FromDays(90), maximumBatches: 1_000, CancellationToken.None);
        await retention.PurgeInboxAsync(TimeSpan.FromDays(30), TimeSpan.FromDays(90), maximumBatches: 1_000, CancellationToken.None);
        await retention.PurgeRateLimitUsageAsync(maximumBatches: 1_000, CancellationToken.None);
        await retention.PurgeCompletedEffectRecordsAsync(TimeSpan.FromDays(90), maximumBatches: 1_000, CancellationToken.None);

        var remaining = await db.IntegrationJobs.AsNoTracking().Where(x => x.TenantId == tenant.Id).Select(x => x.Id).ToListAsync();
        Assert.Equal([sentShipment.Id], remaining);
        Assert.False(await db.InboxMessages.AsNoTracking().AnyAsync(x => x.TenantId == tenant.Id));
        Assert.Equal(0, await db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM integration.rate_limit_usage WHERE \"Key\" = {usageKey}").SingleAsync());
        Assert.Equal([openEffect.Id], await db.ExternalEffectRecords.AsNoTracking().Where(x => x.TenantId == tenant.Id).Select(x => x.Id).ToListAsync());
        await transaction.RollbackAsync();
    }

    // Q-1/K-1: a failed price/stock write that gave up its key guards nothing and goes after the failed retention;
    // one still holding its key is kept for the guard retention.
    [PostgreSqlFact]
    public async Task ARetiredPriceStockJobGoesAfterTheFailedRetention()
    {
        var aged = DateTimeOffset.UtcNow.AddDays(-40);
        var tenant = NewTenant("bg-retired-push");
        IntegrationJob Push(string dedup)
        {
            var job = NewJob(tenant.Id, MarketplaceJobTypes.PriceInventorySync);
            job.JobDedupKey = dedup;
            job.Status = JobStatus.Dead;
            job.AttemptCount = job.MaxAttempts;
            job.LastErrorCode = "REMOTE_5XX";
            job.CreatedAt = aged;
            job.CompletedAt = aged;
            return job;
        }
        var dedup = $"price-inventory:{Guid.NewGuid():N}:revision:ABC";
        var holding = Push($"price-inventory:{Guid.NewGuid():N}:revision:DEF");
        var retired = Push(dedup);
        retired.JobDedupKey = PriceInventoryOutboxPolicy.RetiredDedupKey(dedup, retired.Id);

        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        db.Tenants.Add(tenant);
        db.IntegrationJobs.AddRange(holding, retired);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await new JobRetentionService(db, fixture.TimeProvider).PurgeAsync(TimeSpan.FromDays(14), TimeSpan.FromDays(30), TimeSpan.FromDays(90), maximumBatches: 1_000, CancellationToken.None);

        Assert.Equal([holding.Id], await db.IntegrationJobs.AsNoTracking().Where(x => x.TenantId == tenant.Id).Select(x => x.Id).ToListAsync());
        await transaction.RollbackAsync();
    }

    // An old list snapshot nothing uses goes with its items; the current one and a fresh one still being built stay.
    [PostgreSqlFact]
    public async Task OldReferenceSnapshotsGoWithTheirItemsWhileTheCurrentAndAFreshOneStay()
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = NewTenant("bg-reference");
        var connection = NewConnection(tenant);
        ReferenceSnapshot Snapshot(bool current, DateTimeOffset fetchedAt) => new() { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, ResourceType = "BRANDS", SourceVersion = "v1", ContentHash = Guid.NewGuid().ToString("N"), FetchedAt = fetchedAt, IsCurrent = current, ItemCount = 3 };
        IEnumerable<ReferenceItem> Items(ReferenceSnapshot snapshot) => Enumerable.Range(1, 3).Select(index => new ReferenceItem { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, SnapshotId = snapshot.Id, ResourceType = "BRANDS", ExternalId = index.ToString(), Name = $"Marka {index}", NormalizedName = $"MARKA {index}", Path = $"Marka {index}", PayloadHash = "h" });
        var old = Snapshot(current: false, now.AddDays(-3));
        var current = Snapshot(current: true, now.AddDays(-2));
        var staging = Snapshot(current: false, now.AddHours(-1));
        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        db.Tenants.Add(tenant);
        db.PlatformConnections.Add(connection);
        db.ReferenceSnapshots.AddRange(old, current, staging);
        db.ReferenceItems.AddRange(Items(old).Concat(Items(current)).Concat(Items(staging)));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var deleted = await new JobRetentionService(db, fixture.TimeProvider).PurgeStaleReferenceSnapshotsAsync(maximumBatches: 10, CancellationToken.None);

        Assert.Equal(3, deleted);
        Assert.Equal(new[] { current.Id, staging.Id }.Order(), (await db.ReferenceSnapshots.AsNoTracking().Where(x => x.TenantId == tenant.Id).Select(x => x.Id).ToListAsync()).Order());
        Assert.Equal(6, await db.ReferenceItems.AsNoTracking().CountAsync(x => x.TenantId == tenant.Id));
        await transaction.RollbackAsync();
    }

    [PostgreSqlFact]
    public async Task AnIssueResolvedForStalenessIsNotDeletedInTheSamePass()
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = NewTenant("bg-issues");
        var stale = new OperationalIssue { Id = Guid.CreateVersion7(), TenantId = tenant.Id, DedupeKey = $"bg-stale-{Guid.NewGuid():N}", Code = "X", Summary = "x", Status = IssueStatus.Open, FirstSeenAt = now.AddDays(-60), LastSeenAt = now.AddDays(-40), OccurrenceCount = 1 };
        var oldResolved = new OperationalIssue { Id = Guid.CreateVersion7(), TenantId = tenant.Id, DedupeKey = $"bg-old-{Guid.NewGuid():N}", Code = "X", Summary = "x", Status = IssueStatus.Resolved, FirstSeenAt = now.AddDays(-60), LastSeenAt = now.AddDays(-40), OccurrenceCount = 1 };
        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        db.Tenants.Add(tenant);
        db.OperationalIssues.AddRange(stale, oldResolved);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var issues = new OperationalIssueRetentionService(db, fixture.TimeProvider);
        await issues.ResolveStaleAsync(TimeSpan.FromDays(30), CancellationToken.None);
        await issues.DeleteResolvedAsync(TimeSpan.FromDays(30), CancellationToken.None);

        var kept = await db.OperationalIssues.AsNoTracking().SingleAsync(x => x.TenantId == tenant.Id);
        Assert.Equal(stale.Id, kept.Id);
        Assert.Equal(IssueStatus.Resolved, kept.Status);
        Assert.NotNull(kept.ResolvedAt);
        await transaction.RollbackAsync();
    }

    [PostgreSqlFact]
    public async Task TheSweepRevivesAVerificationBlockedByThePassiveConnectionAndSettlesAClosedReturnDecision()
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = NewTenant("bg-sweep");
        var connection = NewConnection(tenant);
        var order = new Order
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            ConnectionId = connection.Id,
            ExternalOrderId = "bg-order",
            OrderNumber = "bg-order",
            Currency = "TRY",
            GrossAmount = 100m,
            NetAmount = 100m,
            OrderedAt = now.AddDays(-1),
            LastRemoteModifiedAt = now.AddDays(-1),
            CustomerSnapshotJson = "{}",
            ShipmentAddressSnapshotJson = "{}",
            InvoiceAddressSnapshotJson = "{}",
            DerivedStatus = "DELIVERED",
            CreatedAt = now,
            UpdatedAt = now,
            Version = 1
        };
        var claim = new ReturnClaim { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, OrderId = order.Id, ExternalClaimId = "bg-claim", Status = ReturnClaimStatus.ActionRequired, RawStatus = "WaitingInAction", LastRemoteModifiedAt = now, CreatedAt = now, UpdatedAt = now };
        // Blocked once while the connection was passive; the connection is active again.
        var blockedEffect = new ExternalEffectRecord { Id = Guid.CreateVersion7(), TenantId = tenant.Id, EffectType = "SHIPMENT", IdempotencyKey = $"bg-{Guid.NewGuid():N}", CreatedAt = now.AddMinutes(-20), ConnectionId = connection.Id, Action = "PICKING", TargetId = Guid.CreateVersion7() };
        var blockedJob = ExternalEffectReconcilePolicy.NewJob(blockedEffect, now.AddMinutes(-19), now.AddMinutes(-19));
        blockedJob.Status = JobStatus.Blocked;
        blockedJob.AttemptCount = 1;
        blockedJob.LastErrorCode = "CONNECTION_INACTIVE";
        blockedJob.CompletedAt = now.AddMinutes(-19);
        // Every verification ended without an answer: the record closes and its decision is settled.
        var returnEffect = new ExternalEffectRecord { Id = Guid.CreateVersion7(), TenantId = tenant.Id, EffectType = "RETURN", IdempotencyKey = $"bg-{Guid.NewGuid():N}", CreatedAt = now.AddHours(-2), ConnectionId = connection.Id, Action = "RETURN_APPROVE", TargetId = claim.Id };
        var decision = new ReturnDecision { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ClaimId = claim.Id, Action = "APPROVE", IdempotencyKey = returnEffect.IdempotencyKey, Status = "SUBMITTING", ActorUserId = Guid.CreateVersion7(), CreatedAt = now.AddHours(-2) };
        var deadJobs = ExternalEffectSweep.VerificationKeys(returnEffect.Id).Select(key =>
        {
            var job = ExternalEffectReconcilePolicy.NewJob(returnEffect, now.AddHours(-2), now.AddHours(-2));
            job.JobDedupKey = key;
            job.EffectIdempotencyKey = key;
            job.Status = JobStatus.Dead;
            job.AttemptCount = job.MaxAttempts;
            job.CompletedAt = now.AddHours(-1);
            return job;
        }).ToList();

        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        db.Tenants.Add(tenant);
        db.PlatformConnections.Add(connection);
        db.Orders.Add(order);
        db.ReturnClaims.Add(claim);
        db.ReturnDecisions.Add(decision);
        db.ExternalEffectRecords.AddRange(blockedEffect, returnEffect);
        db.IntegrationJobs.Add(blockedJob);
        db.IntegrationJobs.AddRange(deadJobs);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await new ExternalEffectSweep(db, fixture.TimeProvider).SweepAsync(CancellationToken.None);

        var revived = await db.IntegrationJobs.AsNoTracking().SingleAsync(x => x.Id == blockedJob.Id);
        Assert.Equal(JobStatus.Pending, revived.Status);
        Assert.Equal(1, await db.IntegrationJobs.AsNoTracking().CountAsync(x => x.TenantId == tenant.Id && x.JobDedupKey.StartsWith(ExternalEffectReconcilePolicy.JobDedupKey(blockedEffect.Id))));
        Assert.Null((await db.ExternalEffectRecords.AsNoTracking().SingleAsync(x => x.Id == blockedEffect.Id)).CompletedAt);
        var closed = await db.ExternalEffectRecords.AsNoTracking().SingleAsync(x => x.Id == returnEffect.Id);
        Assert.Equal("UNRESOLVED", closed.Outcome);
        var settled = await db.ReturnDecisions.AsNoTracking().SingleAsync(x => x.Id == decision.Id);
        Assert.Equal("FAILED", settled.Status);
        Assert.Equal("EXTERNAL_EFFECT_UNRESOLVED", settled.ErrorCode);
        Assert.NotNull(settled.CompletedAt);
        await transaction.RollbackAsync();
    }

    [PostgreSqlFact]
    public async Task AnIncrementalReadWaitingForTheFirstImportKeepsItsAttempts()
    {
        var tenant = NewTenant("bg-bootstrap");
        var connection = NewConnection(tenant);
        var bootstrap = NewJob(tenant.Id, MarketplaceJobTypes.OrderRecoverySync);
        bootstrap.ConnectionId = connection.Id;
        bootstrap.JobDedupKey = $"{MarketplaceJobTypes.ActivationBootstrapPrefix}{connection.Id:N}:orders";
        bootstrap.AvailableAt = DateTimeOffset.UtcNow.AddHours(1);
        await using (var setup = fixture.CreateContext())
        {
            setup.Tenants.Add(tenant);
            setup.PlatformConnections.Add(connection);
            setup.IntegrationJobs.Add(bootstrap);
            await setup.SaveChangesAsync();
        }

        try
        {
            await using var db = fixture.CreateContext();
            var processor = new MarketplaceJobProcessor(db, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, new ConfigurationBuilder().Build(), fixture.TimeProvider);
            var result = await processor.ProcessAsync(tenant.Id, connection.Id, MarketplaceJobTypes.OrderSync, "{}", "bg-bootstrap", CancellationToken.None);

            Assert.Equal(JobCompletionKind.Retry, result.Kind);
            Assert.Equal("INITIAL_SYNC_PENDING", result.ErrorCode);
            Assert.True(result.RefundAttempt);
        }
        finally
        {
            await CleanupAsync(tenant.Id);
        }
    }

    // A marketplace still processing an accepted batch is not a failed try: the checks keep their attempt rows but
    // give the attempt back, run again when the processor asked, and real failures still use up the budget.
    [PostgreSqlFact]
    public async Task WaitingChecksKeepTheirRowsButDoNotUseUpTheAttempts()
    {
        var tenant = NewTenant("bg-wait");
        var job = NewJob(tenant.Id, "TEST_WAITING_CHECK");
        job.MaxAttempts = 2;
        await using (var setup = fixture.CreateContext())
        {
            setup.Tenants.Add(tenant);
            setup.IntegrationJobs.Add(job);
            await setup.SaveChangesAsync();
        }

        try
        {
            async Task RunAsync(JobExecutionResult result)
            {
                await using var db = fixture.CreateContext();
                var leases = new JobLeaseService(db, fixture.TokenHasher, fixture.TimeProvider);
                await db.IntegrationJobs.Where(x => x.Id == job.Id).ExecuteUpdateAsync(setters => setters.SetProperty(x => x.AvailableAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
                var lease = await leases.TryLeaseAsync(TimeSpan.FromMinutes(2), null, null, CancellationToken.None, onlyJobId: job.Id);
                Assert.NotNull(lease);
                Assert.True(await leases.CompleteAsync(job.Id, lease.LeaseToken, result, CancellationToken.None));
            }

            for (var check = 0; check < 3; check++)
                await RunAsync(JobExecutionResult.Retry("PRODUCT_BATCH_PENDING", "N11 ürün açma görevi işleniyor.", TimeSpan.FromSeconds(15), "task-1"));

            await using (var afterChecks = fixture.CreateContext())
            {
                var stored = await afterChecks.IntegrationJobs.AsNoTracking().SingleAsync(x => x.Id == job.Id);
                Assert.Equal(JobStatus.RetryScheduled, stored.Status);
                Assert.Equal(3, stored.AttemptCount);
                Assert.Equal(3, stored.WaitingChecks);
                Assert.Equal(5, stored.MaxAttempts);
                // The processor's 15 seconds, not the failure backoff that grew to minutes after the third check.
                Assert.True(stored.AvailableAt - DateTimeOffset.UtcNow < TimeSpan.FromSeconds(30));
                var rows = await afterChecks.JobAttempts.AsNoTracking().Where(x => x.JobId == job.Id).ToListAsync();
                Assert.Equal(3, rows.Count);
                Assert.All(rows, row => Assert.Equal("PRODUCT_BATCH_PENDING", row.ErrorCode));
            }

            await RunAsync(JobExecutionResult.Retry("REMOTE_TIMEOUT", "Zaman aşımı."));
            await RunAsync(JobExecutionResult.Retry("REMOTE_TIMEOUT", "Zaman aşımı."));

            await using var verification = fixture.CreateContext();
            var final = await verification.IntegrationJobs.AsNoTracking().SingleAsync(x => x.Id == job.Id);
            // Two real failures use up the job's own budget of two.
            Assert.Equal(JobStatus.Dead, final.Status);
            Assert.Equal(5, final.AttemptCount);
            Assert.Equal(2, final.MaxAttempts - final.WaitingChecks);
        }
        finally
        {
            await CleanupAsync(tenant.Id);
        }
    }

    // Q-4 (29 Oct): a two-day-old dead job retried by hand gets one attempt. A busy lock right after the retry, and a
    // waiting check after that, keep it; the retry's attempt is still there for the run itself.
    [PostgreSqlFact]
    public async Task AManuallyRetriedOldJobIsNotKilledByAWaitOrACheck()
    {
        var tenant = NewTenant("bg-manual-retry");
        var job = NewJob(tenant.Id, MarketplaceJobTypes.WebhookIngest);
        job.CreatedAt = DateTimeOffset.UtcNow.AddDays(-2);
        job.Status = JobStatus.Dead;
        job.AttemptCount = 3;
        job.MaxAttempts = 3;
        job.CompletedAt = DateTimeOffset.UtcNow.AddDays(-1);
        job.LastErrorCode = "REMOTE_TIMEOUT";
        await using (var setup = fixture.CreateContext())
        {
            setup.Tenants.Add(tenant);
            setup.IntegrationJobs.Add(job);
            await setup.SaveChangesAsync();
        }

        try
        {
            await using (var db = fixture.CreateContext())
                Assert.True((await new JobOperationsService(db, fixture.TimeProvider).RetryAsync(tenant.Id, job.Id, CancellationToken.None)).Succeeded);

            var busy = MarketplaceSyncExecutionLock.ContentionResult(MarketplaceJobTypes.WebhookIngest, "{}", "webhook", sameTypeRunning: false);
            var waiting = JobExecutionResult.Retry("PRODUCT_BATCH_PENDING", "sürüyor", TimeSpan.FromSeconds(15));
            foreach (var result in new[] { busy, waiting })
            {
                await using var db = fixture.CreateContext();
                var leases = new JobLeaseService(db, fixture.TokenHasher, fixture.TimeProvider);
                await db.IntegrationJobs.Where(x => x.Id == job.Id).ExecuteUpdateAsync(setters => setters.SetProperty(x => x.AvailableAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
                var lease = await leases.TryLeaseAsync(TimeSpan.FromMinutes(2), null, null, CancellationToken.None, onlyJobId: job.Id);
                Assert.NotNull(lease);
                Assert.True(await leases.CompleteAsync(job.Id, lease.LeaseToken, result, CancellationToken.None));
            }

            await using var verification = fixture.CreateContext();
            var stored = await verification.IntegrationJobs.AsNoTracking().SingleAsync(x => x.Id == job.Id);
            Assert.Equal(JobStatus.RetryScheduled, stored.Status);
            Assert.NotNull(stored.RetriedAt);
            // The deferral gave its attempt back and the check raised the limit: one attempt is still left.
            Assert.Equal(4, stored.AttemptCount);
            Assert.Equal(5, stored.MaxAttempts);
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
        await cleanup.SyncCursors.Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
        await cleanup.OperationalIssues.Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
        await cleanup.PlatformConnections.Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
        await cleanup.Tenants.Where(x => x.Id == tenantId).ExecuteDeleteAsync();
    }

    // Tenant codes are varchar(64): a short prefix and a 32-character suffix.
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
        DisplayName = "Arka plan testi",
        ExternalStoreId = $"store-{Guid.NewGuid():N}",
        Status = "ACTIVE",
        ApiVersion = "V1",
        SettingsJson = "{}"
    };

    private static IntegrationJob NewJob(Guid tenantId, string jobType)
    {
        var now = DateTimeOffset.UtcNow;
        var suffix = Guid.NewGuid().ToString("N");
        return new IntegrationJob
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            JobType = jobType,
            PayloadJson = "{}",
            PayloadVersion = 1,
            PayloadHash = "bg-payload-hash",
            JobDedupKey = $"bg-{suffix}",
            EffectIdempotencyKey = $"bg-{suffix}",
            Priority = 1,
            Status = JobStatus.Pending,
            AvailableAt = now.AddMinutes(-1),
            MaxAttempts = 3,
            CorrelationId = $"bg-{suffix}",
            CreatedAt = now,
            Version = 1
        };
    }
}
