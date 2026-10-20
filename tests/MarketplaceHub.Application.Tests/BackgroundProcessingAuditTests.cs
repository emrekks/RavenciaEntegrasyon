using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Jobs;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;
using WorkerService = MarketplaceHub.Worker.Worker;

namespace MarketplaceHub.Application.Tests;

// Regression tests for the background-processing findings of the 20 October 2026 deep audit (G/Q items).
public sealed class BackgroundProcessingAuditTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void OnlyTheFirstFewConsecutiveVersionConflictsGoWithoutAnAttempt()
    {
        var conflict = WorkerService.ExceptionResult(new DbUpdateConcurrencyException("expected to affect 1 row"));
        var count = 0;
        for (var expected = 1; expected <= JobConflictPolicy.DeferredVersionConflicts; expected++)
        {
            (var deferred, count) = JobConflictPolicy.Apply(conflict, count);
            Assert.Equal(expected, count);
            Assert.True(deferred.RefundAttempt);
        }

        var (retried, afterLimit) = JobConflictPolicy.Apply(conflict, count);
        Assert.Equal(JobConflictPolicy.DeferredVersionConflicts + 1, afterLimit);
        Assert.Equal(JobCompletionKind.Retry, retried.Kind);
        Assert.False(retried.RefundAttempt);
        Assert.False(JobLeaseService.RefundsAttempt(retried, TimeSpan.FromMinutes(5)));

        // Any other outcome ends the run of conflicts.
        Assert.Equal(0, JobConflictPolicy.Apply(JobExecutionResult.Success(), afterLimit).ConflictCount);
        Assert.Equal(0, JobConflictPolicy.Apply(JobExecutionResult.Deferred("CONNECTION_OPERATION_BUSY"), afterLimit).ConflictCount);
    }

    [Fact]
    public void ADuplicateInsertIsRetriedTwiceBeforeItGoesToReview()
    {
        var violation = new PostgresException("duplicate key value violates unique constraint", "ERROR", "ERROR", PostgresErrorCodes.UniqueViolation);
        var duplicate = WorkerService.ExceptionResult(new DbUpdateException("save failed", violation));

        Assert.Equal(JobCompletionKind.Retry, duplicate.Kind);
        Assert.Equal(JobConflictPolicy.DuplicateInsert, duplicate.ErrorCode);
        Assert.False(duplicate.RefundAttempt);
        Assert.InRange(duplicate.RetryAfter!.Value.TotalSeconds, 5, 15);

        var (first, one) = JobConflictPolicy.Apply(duplicate, 0);
        var (second, two) = JobConflictPolicy.Apply(duplicate, one);
        var (third, _) = JobConflictPolicy.Apply(duplicate, two);
        Assert.Equal(JobCompletionKind.Retry, first.Kind);
        Assert.Equal(JobCompletionKind.Retry, second.Kind);
        Assert.Equal(JobCompletionKind.ManualReview, third.Kind);
        Assert.Equal(JobConflictPolicy.DuplicateInsert, third.ErrorCode);
    }

    [Theory]
    [InlineData(PostgresErrorCodes.CheckViolation)]
    [InlineData(PostgresErrorCodes.StringDataRightTruncation)]
    public void ACheckOrTruncationViolationStillGoesToReview(string sqlState)
    {
        var violation = new PostgresException("violates check constraint", "ERROR", "ERROR", sqlState);
        var result = WorkerService.ExceptionResult(new DbUpdateException("save failed", violation));

        Assert.Equal(JobCompletionKind.ManualReview, result.Kind);
        Assert.Equal("DATABASE_CONSTRAINT_VIOLATION", result.ErrorCode);
    }

    [Fact]
    public void AnInvalidOperationFromTheConnectionOrShutdownIsRetried()
    {
        var broken = WorkerService.ExceptionResult(new InvalidOperationException("An exception has been raised that is likely due to a transient failure.", new NpgsqlException("Exception while reading from stream")));
        var closed = WorkerService.ExceptionResult(new InvalidOperationException("Connection is not open"));
        var disposed = WorkerService.ExceptionResult(new ObjectDisposedException("IServiceProvider"));
        var dataFault = WorkerService.ExceptionResult(new InvalidOperationException("Sequence contains more than one element"));

        Assert.Equal(JobCompletionKind.Retry, broken.Kind);
        Assert.Equal("TRANSIENT_EXECUTION_FAILURE", broken.ErrorCode);
        Assert.Equal(JobCompletionKind.Retry, closed.Kind);
        Assert.Equal(JobCompletionKind.Retry, disposed.Kind);
        Assert.Equal(JobCompletionKind.ManualReview, dataFault.Kind);
    }

    [Fact]
    public void RetryJitterComesFromTheWholeJobId()
    {
        // UUIDv7 ids created together share their leading timestamp bytes.
        var ids = Enumerable.Range(0, 64).Select(_ => Guid.CreateVersion7(Now)).ToArray();
        var jitters = ids.Select(JobRetryPolicy.JitterPercent).ToArray();

        Assert.All(jitters, jitter => Assert.InRange(jitter, 0, 20));
        Assert.True(jitters.Distinct().Count() > 5);
        Assert.Equal(JobRetryPolicy.JitterPercent(ids[0]), JobRetryPolicy.JitterPercent(ids[0]));
    }

    [Fact]
    public void AReturnDecisionVerificationJobCarriesTheLockGroupItsLockTakes()
    {
        var returnEffect = new ExternalEffectRecord { Id = Guid.CreateVersion7(), TenantId = Guid.CreateVersion7(), EffectType = "RETURN", IdempotencyKey = "k", ConnectionId = Guid.CreateVersion7(), Action = "RETURN_APPROVE" };
        var packageEffect = new ExternalEffectRecord { Id = Guid.CreateVersion7(), TenantId = Guid.CreateVersion7(), EffectType = "SHIPMENT", IdempotencyKey = "p", ConnectionId = Guid.CreateVersion7(), Action = "PICKING" };
        var returnJob = ExternalEffectReconcilePolicy.NewJob(returnEffect, Now, Now);
        var packageJob = ExternalEffectReconcilePolicy.NewJob(packageEffect, Now, Now);

        Assert.Equal("returns", returnJob.LockGroup);
        Assert.Equal(MarketplaceSyncExecutionLock.EffectReconcileGroup("RETURN_APPROVE"), JobLeaseService.LockGroupOf(returnJob));
        Assert.Equal("orders", JobLeaseService.LockGroupOf(packageJob));
        Assert.Equal("orders", JobLeaseService.LockGroupOf(new IntegrationJob { JobType = MarketplaceJobTypes.ExternalEffectReconcile, PayloadJson = "{}", PayloadHash = "h", JobDedupKey = "d", EffectIdempotencyKey = "e", CorrelationId = "c" }));
    }

    [Fact]
    public void TheQueueWaitCountsFromWhenTheJobBecameRunnable()
    {
        var job = new IntegrationJob { JobType = "X", PayloadJson = "{}", PayloadHash = "h", JobDedupKey = "d", EffectIdempotencyKey = "e", CorrelationId = "c", CreatedAt = Now.AddMinutes(-5), AvailableAt = Now.AddSeconds(-32) };

        Assert.Equal(TimeSpan.FromSeconds(32), JobLeaseService.QueueWait(job, Now));
        job.AvailableAt = Now.AddMinutes(-10);
        Assert.Equal(TimeSpan.FromMinutes(5), JobLeaseService.QueueWait(job, Now));
        Assert.Equal(TimeSpan.Zero, JobLeaseService.QueueWait(job, Now.AddMinutes(-6)));
    }

    [Fact]
    public void JobOutcomeTagsTellDeferralsAndSlicesFromRetries()
    {
        Assert.Equal("succeeded", WorkerService.OutcomeTag(true, JobExecutionResult.Success()));
        Assert.Equal("continued", WorkerService.OutcomeTag(true, JobExecutionResult.Continue("next")));
        Assert.Equal("deferred", WorkerService.OutcomeTag(true, JobExecutionResult.Deferred("CONNECTION_OPERATION_BUSY")));
        Assert.Equal("retry", WorkerService.OutcomeTag(true, JobExecutionResult.Retry("REMOTE_TIMEOUT")));
        Assert.Equal("blocked", WorkerService.OutcomeTag(true, JobExecutionResult.Blocked("X")));
        Assert.Equal("manual-review", WorkerService.OutcomeTag(true, JobExecutionResult.ManualReview("X")));
        Assert.Equal("dead", WorkerService.OutcomeTag(true, JobExecutionResult.Dead("X")));
        Assert.Equal("lease-lost", WorkerService.OutcomeTag(false, JobExecutionResult.Success()));
        Assert.Equal("no-result", WorkerService.OutcomeTag(true, null));
    }

    [Fact]
    public async Task AFailedCompletionWriteIsRetriedWhileTheLeaseIsHeld()
    {
        var calls = 0;
        var waits = new List<TimeSpan>();
        var failures = 0;
        var completed = await WorkerService.CompleteWithRetryAsync(
            _ => ++calls < 3 ? throw new TimeoutException("database blip") : Task.FromResult(true),
            () => false,
            (_, _) => failures++,
            delay => { waits.Add(delay); return Task.CompletedTask; });

        Assert.True(completed);
        Assert.Equal(3, calls);
        Assert.Equal(2, failures);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)], waits);
    }

    [Fact]
    public async Task ACompletionRetryStopsWhenTheLeaseIsLostOrTheWriteIsFenced()
    {
        var calls = 0;
        var lost = await WorkerService.CompleteWithRetryAsync(_ => { calls++; throw new TimeoutException(); }, () => true, (_, _) => { }, _ => Task.CompletedTask);
        Assert.Null(lost);
        Assert.Equal(1, calls);

        var fenced = await WorkerService.CompleteWithRetryAsync(_ => Task.FromResult(false), () => false, (_, _) => { }, _ => Task.CompletedTask);
        Assert.False(fenced);

        calls = 0;
        var exhausted = await WorkerService.CompleteWithRetryAsync(_ => { calls++; throw new TimeoutException(); }, () => false, (_, _) => { }, _ => Task.CompletedTask);
        Assert.Null(exhausted);
        Assert.Equal(WorkerService.CompletionRetryDelays.Length + 1, calls);
        Assert.True(WorkerService.CompletionRetryDelays.Aggregate(TimeSpan.Zero, (sum, delay) => sum + delay) < JobRetryPolicy.DefaultLeaseDuration);
    }

    [Fact]
    public void StatusScansOfEveryMarketplaceStayOutOfTheHotLane()
    {
        Assert.Contains(MarketplaceJobTypes.HepsiburadaOrderStatusSync, WorkerService.HotLaneExcludedJobTypes);
        Assert.Contains(MarketplaceJobTypes.ShopifyOrderStatusSync, WorkerService.HotLaneExcludedJobTypes);
        Assert.Equal("order-status", WorkerLanes.LaneFor(MarketplaceJobTypes.ShopifyOrderStatusSync, JobPriorityPolicy.Realtime));
        Assert.Equal("order-status", WorkerLanes.LaneFor(MarketplaceJobTypes.OrderStatusSync, JobPriorityPolicy.Realtime));
        Assert.Equal("hepsiburada-status", WorkerLanes.LaneFor(MarketplaceJobTypes.HepsiburadaOrderStatusSync, JobPriorityPolicy.Realtime));
        Assert.Equal("hot", WorkerLanes.LaneFor(MarketplaceJobTypes.WebhookIngest, JobPriorityPolicy.Realtime));
    }

    [Fact]
    public void QueueMetricsUseTheWorkersRealLanes()
    {
        Assert.Equal("user", WorkerLanes.LaneFor(MarketplaceJobTypes.ShipmentAction, JobPriorityPolicy.UserAction));
        Assert.Equal("initial-data-sync", WorkerLanes.LaneFor(MarketplaceJobTypes.OrderSync, JobPriorityPolicy.InitialDataSync));
        Assert.Equal("returns-read", WorkerLanes.LaneFor(MarketplaceJobTypes.ReturnSync, JobPriorityPolicy.Interactive));
        Assert.Equal("product-catalog", WorkerLanes.LaneFor(MarketplaceJobTypes.ProductSync, JobPriorityPolicy.Catalog));
        Assert.Equal("pazarama-orders", WorkerLanes.LaneFor(MarketplaceJobTypes.PazaramaOrderSync, JobPriorityPolicy.Realtime));
        Assert.Equal("background", WorkerLanes.LaneFor(MarketplaceJobTypes.OrderRecoverySync, JobPriorityPolicy.Recovery));
        Assert.Equal("hot", WorkerLanes.LaneFor(MarketplaceJobTypes.PriceInventorySync, JobPriorityPolicy.Writes));
        Assert.Equal(10, WorkerLanes.All().Count);
    }

    [Fact]
    public void DailyPoliciesRunInTheIstanbulAfternoonWindow()
    {
        // 14:30 Istanbul is 11:30 UTC.
        var afternoon = new DateTimeOffset(2026, 10, 20, 11, 30, 0, TimeSpan.Zero);
        var morningCatchUp = new DateTimeOffset(2026, 10, 20, 6, 5, 0, TimeSpan.Zero);
        var yesterdayAfternoon = afternoon.AddDays(-1).AddMinutes(-20);

        Assert.True(DailyScheduleWindowPolicy.Applies(86_400));
        Assert.False(DailyScheduleWindowPolicy.Applies(21_600));
        // The 09:00 start-up catch-up no longer runs a daily job that ran yesterday afternoon.
        Assert.False(DailyScheduleWindowPolicy.IsDue(morningCatchUp, yesterdayAfternoon, 86_400, 0));
        Assert.True(DailyScheduleWindowPolicy.IsDue(afternoon, yesterdayAfternoon, 86_400, 0));
        // Once a day: a run earlier in today's window is not repeated.
        Assert.False(DailyScheduleWindowPolicy.IsDue(afternoon.AddHours(1), afternoon, 86_400, 0));
        // A job that drifted to the morning moves to the afternoon of the next day.
        Assert.False(DailyScheduleWindowPolicy.IsDue(afternoon, morningCatchUp, 86_400, 0));
        Assert.True(DailyScheduleWindowPolicy.IsDue(afternoon.AddDays(1), morningCatchUp, 86_400, 0));
        // The jitter shifts the start inside the window.
        Assert.False(DailyScheduleWindowPolicy.IsDue(afternoon, yesterdayAfternoon, 86_400, 3_600));
        // A policy that missed a whole window runs at once, even in the morning.
        Assert.True(DailyScheduleWindowPolicy.IsDue(morningCatchUp, morningCatchUp.AddDays(-2).AddHours(-1), 86_400, 0));
        Assert.True(DailyScheduleWindowPolicy.IsDue(afternoon, null, 86_400, 0));
        Assert.False(DailyScheduleWindowPolicy.IsDue(morningCatchUp, null, 86_400, 0));
    }

    [Fact]
    public void TheSchedulerChecksWhetherAPolicyIsDueBeforeAnyLookup()
    {
        Assert.False(ScheduledJobProducer.MayBeDue(Now.AddSeconds(-100), 180, 10, Now, isHotOrder: false));
        Assert.True(ScheduledJobProducer.MayBeDue(Now.AddSeconds(-200), 180, 10, Now, isHotOrder: false));
        Assert.True(ScheduledJobProducer.MayBeDue(null, 180, 10, Now, isHotOrder: false));
        // A hot order read may run at once for a connection without orders, so it always reaches the lookups.
        Assert.True(ScheduledJobProducer.MayBeDue(Now.AddSeconds(-5), 60, 5, Now, isHotOrder: true));
        Assert.False(ScheduledJobProducer.IsDue(Now.AddSeconds(-5), 60, 5, Now));
    }

    [Fact]
    public void OnlyACadenceChangeMakesAPolicyTheUsersOwn()
    {
        var defaults = ScheduledJobProducer.DefaultPolicyValues(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), "TRENDYOL", "RETURNS");

        Assert.Equal((180, 900, 10), defaults);
        Assert.False(ScheduledJobProducer.MarksUserDefined(false, (180, 900, 10), (180, 900, 10)));
        Assert.True(ScheduledJobProducer.MarksUserDefined(false, (180, 900, 10), (300, 900, 10)));
        Assert.True(ScheduledJobProducer.MarksUserDefined(true, (180, 900, 10), (180, 900, 10)));
        Assert.Null(ScheduledJobProducer.DefaultPolicyValues(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), "TRENDYOL", "UNKNOWN"));
    }

    [Fact]
    public void RetentionLetsGoOfGuardsThatGuardNothing()
    {
        Assert.Contains(MarketplaceJobTypes.StockProjectionDispatch, JobRetentionService.VersionedGuardJobTypes);
        Assert.Contains(MarketplaceJobTypes.PriceInventorySync, JobRetentionService.VersionedGuardJobTypes);
        Assert.DoesNotContain(MarketplaceJobTypes.ShipmentAction, JobRetentionService.VersionedGuardJobTypes);
        Assert.All(JobRetentionService.VersionedGuardJobTypes, type => Assert.Contains(type, JobRetentionService.EffectGuardJobTypes));
        Assert.StartsWith("stock-projection:", StockProjectionOutboxPolicy.DedupKey(Guid.CreateVersion7(), Guid.CreateVersion7(), 7));
        Assert.Contains("EXTERNAL_WRITES_DISABLED", JobRetentionService.PreSendBlockCodes);
        Assert.Contains("CONNECTION_INACTIVE", JobRetentionService.PreSendBlockCodes);
        Assert.DoesNotContain("F3_JOB_REJECTED", JobRetentionService.PreSendBlockCodes);
    }

    [Fact]
    public void AStockChangeQueuesNoDispatchForAConnectionWithWritesOff()
    {
        Assert.True(StockProjectionOutbox.StoredExternalWritesEnabled("{\"ExternalWritesEnabled\":true}"));
        Assert.False(StockProjectionOutbox.StoredExternalWritesEnabled("{\"ExternalWritesEnabled\":false}"));
        Assert.False(StockProjectionOutbox.StoredExternalWritesEnabled("{}"));
        Assert.False(StockProjectionOutbox.StoredExternalWritesEnabled("{bozuk"));
        Assert.False(StockProjectionOutbox.StoredExternalWritesEnabled(null));
    }

    [Fact]
    public void TheRealtimeDispatcherNoLongerScansForNewJobs()
    {
        var method = typeof(MarketplaceHub.Api.Realtime.OperationsRealtimeBroadcaster).GetMethod("AnnounceNewJobsAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        Assert.Null(method);
        Assert.Equal("jobs", JobLeaseService.JobEventResource);
    }

    [Fact]
    public void TheRequiredPoolSizeFollowsTheLaneSlots()
    {
        var defaultSlots = WorkerLanes.All().Sum(lane => lane.DefaultSlots);

        Assert.Equal(15, defaultSlots);
        Assert.Equal(40, WorkerLanes.RequiredPoolSize(defaultSlots));
        Assert.True(WorkerLanes.RequiredPoolSize(defaultSlots) > 25);
    }
}
