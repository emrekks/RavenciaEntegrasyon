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
