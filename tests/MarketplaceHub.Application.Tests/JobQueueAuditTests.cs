using System.Text.Json;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Regression tests for the job queue, scheduler and database findings of the 19 October 2026 audit (Q1-Q19).
public sealed class JobQueueAuditTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AVersionConflictIsDeferredWithoutLosingAnAttempt()
    {
        var result = MarketplaceHub.Worker.Worker.ExceptionResult(new DbUpdateConcurrencyException("The database operation was expected to affect 1 row(s)."));

        Assert.Equal(JobCompletionKind.Retry, result.Kind);
        Assert.True(result.RefundAttempt);
        Assert.Equal("CONCURRENT_UPDATE", result.ErrorCode);
        Assert.InRange(result.RetryAfter!.Value.TotalSeconds, 10, 30);
    }

    [Theory]
    [InlineData(PostgresErrorCodes.UniqueViolation)]
    [InlineData(PostgresErrorCodes.CheckViolation)]
    [InlineData(PostgresErrorCodes.StringDataRightTruncation)]
    public void AConstraintViolationGoesToManualReviewInsteadOfSixRetries(string sqlState)
    {
        var violation = new PostgresException("duplicate key value violates unique constraint", "ERROR", "ERROR", sqlState);
        var result = MarketplaceHub.Worker.Worker.ExceptionResult(new DbUpdateException("An error occurred while saving the entity changes.", violation));

        Assert.Equal(JobCompletionKind.ManualReview, result.Kind);
        Assert.Equal("DATABASE_CONSTRAINT_VIOLATION", result.ErrorCode);
        Assert.Contains(sqlState, result.ErrorSummary);
    }

    [Fact]
    public void FailureSummariesNameTheExceptionType()
    {
        var saving = MarketplaceHub.Worker.Worker.ExceptionResult(new DbUpdateException("save failed"));
        var unknown = MarketplaceHub.Worker.Worker.ExceptionResult(new NotImplementedException("missing branch"));
        var dataFault = MarketplaceHub.Worker.Worker.ExceptionResult(new InvalidOperationException("Sequence contains more than one element"));

        Assert.Equal(JobCompletionKind.Retry, saving.Kind);
        Assert.Contains(nameof(DbUpdateException), saving.ErrorSummary);
        Assert.Equal("UNHANDLED_EXECUTION_FAILURE", unknown.ErrorCode);
        Assert.Contains(nameof(NotImplementedException), unknown.ErrorSummary);
        Assert.Equal(JobCompletionKind.ManualReview, dataFault.Kind);
        Assert.Contains("Sequence contains more than one element", dataFault.ErrorSummary);
    }

    [Fact]
    public void ASliceOfALongJobKeepsItsAttemptEvenAfterADay()
    {
        var slice = JobExecutionResult.Continue("next window");
        var busy = JobExecutionResult.Deferred("CONNECTION_OPERATION_BUSY");

        Assert.True(JobLeaseService.RefundsAttempt(slice, TimeSpan.FromDays(3)));
        Assert.True(JobLeaseService.RefundsAttempt(busy, TimeSpan.FromHours(2)));
        Assert.False(JobLeaseService.RefundsAttempt(busy, TimeSpan.FromDays(2)));
        Assert.False(JobLeaseService.RefundsAttempt(JobExecutionResult.Retry("REMOTE_TIMEOUT"), TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void AReturnDecisionReadBackSerializesWithReturnReadsAndPackageChecksWithOrderReads()
    {
        Assert.Equal("returns", MarketplaceSyncExecutionLock.EffectReconcileGroup("RETURN_APPROVE"));
        Assert.Equal("returns", MarketplaceSyncExecutionLock.EffectReconcileGroup("RETURN_REJECT"));
        Assert.Equal("orders", MarketplaceSyncExecutionLock.EffectReconcileGroup("PICKING"));
        Assert.Equal("orders", MarketplaceSyncExecutionLock.EffectReconcileGroup(null));
    }

    [Fact]
    public void AnEffectWhoseVerificationJobEndedGetsANewJobUntilTheCapThenCloses()
    {
        var effectId = Guid.CreateVersion7();
        var keys = ExternalEffectSweep.VerificationKeys(effectId);

        Assert.Equal(ExternalEffectSweep.MaximumVerificationJobs, keys.Count);
        Assert.Equal((false, keys[0]), ExternalEffectSweep.Decide(effectId, new Dictionary<string, JobStatus>()));
        Assert.Equal((false, (string?)null), ExternalEffectSweep.Decide(effectId, new Dictionary<string, JobStatus> { [keys[0]] = JobStatus.RetryScheduled }));
        Assert.Equal((false, keys[1]), ExternalEffectSweep.Decide(effectId, new Dictionary<string, JobStatus> { [keys[0]] = JobStatus.Dead }));
        Assert.Equal((false, keys[2]), ExternalEffectSweep.Decide(effectId, new Dictionary<string, JobStatus> { [keys[0]] = JobStatus.Blocked, [keys[1]] = JobStatus.Cancelled }));
        Assert.Equal((true, (string?)null), ExternalEffectSweep.Decide(effectId, keys.ToDictionary(key => key, _ => JobStatus.Dead)));
    }

    [Fact]
    public void APolicyAUserSavedIsNeverUpgradedToANewerDefault()
    {
        var historicalDefault = new ConnectionSyncPolicy { ResourceType = "ORDERS", IntervalSeconds = 180, OverlapSeconds = 600, JitterSeconds = 5 };
        var userChoice = new ConnectionSyncPolicy { ResourceType = "ORDERS", IntervalSeconds = 180, OverlapSeconds = 600, JitterSeconds = 5, UserDefined = true };
        var writeFlow = new ConnectionSyncPolicy { ResourceType = MarketplaceExternalWritePolicies.Stock, IntervalSeconds = 300, OverlapSeconds = 60, JitterSeconds = 15 };

        Assert.True(ScheduledJobProducer.ShouldUpgradeToDefault(historicalDefault, "TRENDYOL"));
        Assert.False(ScheduledJobProducer.ShouldUpgradeToDefault(userChoice, "TRENDYOL"));
        Assert.False(ScheduledJobProducer.IsKnownDefault(writeFlow, "TRENDYOL"));
    }

    [Fact]
    public void AHepsiburadaRunResumesItsSavedWindowsAfterAFailure()
    {
        var incremental = new MarketplaceJobProcessor.HepsiburadaOrderSyncState("hepsiburada-orders-v2", Now.AddMinutes(-5), Now.AddHours(-30), 1, 0);
        var baseline = incremental with { AnchorEnd = Now.AddHours(-3), StartAt = HepsiburadaOrderHistoryPolicy.InitialWindowStart(Now.AddHours(-3)), WindowIndex = 12 };
        var staleBaseline = baseline with { AnchorEnd = Now.AddDays(-2), StartAt = HepsiburadaOrderHistoryPolicy.InitialWindowStart(Now.AddDays(-2)) };

        Assert.Equal(incremental, MarketplaceJobProcessor.ReadHepsiburadaOrderSyncState(JsonSerializer.Serialize(incremental), Now, forceBaseline: false));
        // A baseline asked for again continues the one under way instead of reading its first windows forever.
        Assert.Equal(baseline, MarketplaceJobProcessor.ReadHepsiburadaOrderSyncState(JsonSerializer.Serialize(baseline), Now, forceBaseline: true));
        Assert.Null(MarketplaceJobProcessor.ReadHepsiburadaOrderSyncState(JsonSerializer.Serialize(incremental), Now, forceBaseline: true));
        Assert.Null(MarketplaceJobProcessor.ReadHepsiburadaOrderSyncState(JsonSerializer.Serialize(staleBaseline), Now, forceBaseline: true));
        Assert.Null(MarketplaceJobProcessor.ReadHepsiburadaOrderSyncState("{bozuk", Now, forceBaseline: false));
    }

    [Fact]
    public void AHepsiburadaWindowRemainsUntilTheReadReachesItsStart()
    {
        var state = new MarketplaceJobProcessor.HepsiburadaOrderSyncState("hepsiburada-orders-v2", Now, Now.AddDays(-2).AddHours(-1), 0, 0);

        Assert.True(MarketplaceJobProcessor.HepsiburadaWindowRemains(state with { WindowIndex = 1 }));
        Assert.True(MarketplaceJobProcessor.HepsiburadaWindowRemains(state with { WindowIndex = 2 }));
        Assert.False(MarketplaceJobProcessor.HepsiburadaWindowRemains(state with { WindowIndex = 3 }));
    }

    [Fact]
    public void TheHepsiburadaLifecycleScanPagesByTheLastOrderReadNotByOffset()
    {
        var cursor = MarketplaceJobProcessor.HepsiburadaLifecycleCursor("4736002251");

        Assert.Equal("4736002251", MarketplaceJobProcessor.HepsiburadaLifecycleLastSeen(cursor));
        // An offset saved by the previous version starts the scan from the first order.
        Assert.Null(MarketplaceJobProcessor.HepsiburadaLifecycleLastSeen("25"));
        Assert.Null(MarketplaceJobProcessor.HepsiburadaLifecycleLastSeen(null));
    }

    [Fact]
    public void OrdersPlacedWhileNothingWasDeliveredAreNotWebhookMisses()
    {
        var storedClockAhead = TimeSpan.FromHours(3);

        // The server was off overnight: the last delivery was yesterday evening, so the night's orders are not misses.
        Assert.Equal(Now.AddHours(-11) + storedClockAhead, WebhookPollingPolicy.MissedOrderCutoff(Now, storedClockAhead, Now.AddHours(-11)));
        // Deliveries are flowing: the usual ten-minute grace applies.
        Assert.Equal(Now.AddMinutes(-10) + storedClockAhead, WebhookPollingPolicy.MissedOrderCutoff(Now, storedClockAhead, Now.AddMinutes(-1)));
        Assert.Null(WebhookPollingPolicy.MissedOrderCutoff(Now, storedClockAhead, null));
    }

    [Fact]
    public void TheLongTrendyolOpenPackageScanHasItsOwnLane()
    {
        Assert.Contains(MarketplaceJobTypes.OrderStatusSync, MarketplaceHub.Worker.Worker.HotLaneExcludedJobTypes);
        Assert.Contains(MarketplaceJobTypes.OrderStatusSync, MarketplaceHub.Worker.Worker.OrderStatusLaneJobTypes);
        Assert.DoesNotContain(MarketplaceJobTypes.WebhookIngest, MarketplaceHub.Worker.Worker.HotLaneExcludedJobTypes);
        Assert.DoesNotContain(MarketplaceJobTypes.OrderSync, MarketplaceHub.Worker.Worker.HotLaneExcludedJobTypes);
    }

    [Fact]
    public void RetentionKeepsTheJobsThatGuardAnEffect()
    {
        Assert.Contains(MarketplaceJobTypes.ProductCreate, JobRetentionService.EffectGuardJobTypes);
        Assert.Contains(MarketplaceJobTypes.ShipmentAction, JobRetentionService.EffectGuardJobTypes);
        Assert.Contains(InvoicingJobTypes.InvoiceSubmit, JobRetentionService.EffectGuardJobTypes);
        Assert.Contains(InvoicingJobTypes.MarketplaceDelivery, JobRetentionService.EffectGuardJobTypes);
        Assert.DoesNotContain(MarketplaceJobTypes.WebhookIngest, JobRetentionService.EffectGuardJobTypes);
        Assert.DoesNotContain(MarketplaceJobTypes.OrderSync, JobRetentionService.EffectGuardJobTypes);
        Assert.DoesNotContain(MarketplaceJobTypes.ExternalEffectReconcile, JobRetentionService.EffectGuardJobTypes);
    }
}
