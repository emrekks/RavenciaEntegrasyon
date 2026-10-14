using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class FullOrderSyncConflictPolicyTests
{
    [Theory]
    [InlineData(MarketplaceJobTypes.HepsiburadaOrderSync)]
    [InlineData(MarketplaceJobTypes.HepsiburadaOrderRecoverySync)]
    [InlineData(MarketplaceJobTypes.HepsiburadaOrderStatusSync)]
    public void FullHepsiburadaScanPromotesPendingOrderRead(string conflictingType)
    {
        var resolution = FullOrderSyncConflictPolicy.Resolve(
            MarketplaceJobTypes.HepsiburadaOrderRecoverySync,
            conflictingType,
            JobStatus.Pending,
            requestedFullScan: true,
            conflictingJobTargetsSingleOrder: false,
            conflictingJobHasStarted: false);

        Assert.Equal(FullOrderSyncConflictResolution.PromotePending, resolution);
    }

    [Theory]
    [InlineData(JobStatus.Leased)]
    [InlineData(JobStatus.RetryScheduled)]
    [InlineData(JobStatus.Succeeded)]
    public void FullHepsiburadaScanDoesNotReplaceStartedOrFinishedWork(JobStatus status)
    {
        var resolution = FullOrderSyncConflictPolicy.Resolve(
            MarketplaceJobTypes.HepsiburadaOrderRecoverySync,
            MarketplaceJobTypes.HepsiburadaOrderSync,
            status,
            requestedFullScan: true,
            conflictingJobTargetsSingleOrder: false,
            conflictingJobHasStarted: false);

        Assert.Equal(FullOrderSyncConflictResolution.Reject, resolution);
    }

    [Fact]
    public void FullHepsiburadaScanDoesNotOverwritePendingSingleOrderRead()
    {
        var resolution = FullOrderSyncConflictPolicy.Resolve(
            MarketplaceJobTypes.HepsiburadaOrderRecoverySync,
            MarketplaceJobTypes.HepsiburadaOrderSync,
            JobStatus.Pending,
            requestedFullScan: true,
            conflictingJobTargetsSingleOrder: true,
            conflictingJobHasStarted: false);

        Assert.Equal(FullOrderSyncConflictResolution.Reject, resolution);
    }

    [Fact]
    public void IncrementalReadKeepsExistingCoalescingBehavior()
    {
        var resolution = FullOrderSyncConflictPolicy.Resolve(
            MarketplaceJobTypes.HepsiburadaOrderRecoverySync,
            MarketplaceJobTypes.HepsiburadaOrderSync,
            JobStatus.Pending,
            requestedFullScan: false,
            conflictingJobTargetsSingleOrder: false,
            conflictingJobHasStarted: false);

        Assert.Equal(FullOrderSyncConflictResolution.ReuseExisting, resolution);
    }

    [Fact]
    public void FullHepsiburadaScanRejectsStartedPendingJob()
    {
        var resolution = FullOrderSyncConflictPolicy.Resolve(
            MarketplaceJobTypes.HepsiburadaOrderRecoverySync,
            MarketplaceJobTypes.HepsiburadaOrderSync,
            JobStatus.Pending,
            requestedFullScan: true,
            conflictingJobTargetsSingleOrder: false,
            conflictingJobHasStarted: true);

        Assert.Equal(FullOrderSyncConflictResolution.Reject, resolution);
    }

    [Fact]
    public void FullHepsiburadaScanDoesNotPromoteAnotherMarketplaceJob()
    {
        var resolution = FullOrderSyncConflictPolicy.Resolve(
            MarketplaceJobTypes.HepsiburadaOrderRecoverySync,
            MarketplaceJobTypes.OrderSync,
            JobStatus.Pending,
            requestedFullScan: true,
            conflictingJobTargetsSingleOrder: false,
            conflictingJobHasStarted: false);

        Assert.Equal(FullOrderSyncConflictResolution.Reject, resolution);
    }
}
