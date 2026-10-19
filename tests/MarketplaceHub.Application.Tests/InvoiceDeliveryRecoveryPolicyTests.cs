using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class InvoiceDeliveryRecoveryPolicyTests
{
    private static readonly DateTimeOffset AttemptAt = new(2026, 10, 10, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset FreshReadAt = AttemptAt.AddMinutes(2);

    [Fact]
    public void DoesNotRetryUntilMarketplaceReadbackIsNewerThanUncertainAttempt()
    {
        var result = InvoiceDeliveryRecoveryPolicy.Decide(
            MarketplaceInvoiceStatus.NotInvoiced,
            null,
            "INV-1",
            AttemptAt,
            AttemptAt);

        Assert.Equal(InvoiceDeliveryRecoveryAction.WaitForMarketplaceReadback, result);
    }

    [Fact]
    public void RetriesOnlyAfterFreshReadbackConfirmsPackageIsNotInvoiced()
    {
        var result = InvoiceDeliveryRecoveryPolicy.Decide(
            MarketplaceInvoiceStatus.NotInvoiced,
            null,
            "INV-1",
            FreshReadAt,
            AttemptAt);

        Assert.Equal(InvoiceDeliveryRecoveryAction.RetryDelivery, result);
    }

    [Fact]
    public void ConfirmsOnlyWhenMarketplaceInvoiceNumberMatches()
    {
        var matching = InvoiceDeliveryRecoveryPolicy.Decide(
            MarketplaceInvoiceStatus.Invoiced,
            "INV-1",
            "INV-1",
            FreshReadAt,
            AttemptAt);
        var mismatching = InvoiceDeliveryRecoveryPolicy.Decide(
            MarketplaceInvoiceStatus.Invoiced,
            "INV-2",
            "INV-1",
            FreshReadAt,
            AttemptAt);

        Assert.Equal(InvoiceDeliveryRecoveryAction.ConfirmDelivery, matching);
        Assert.Equal(InvoiceDeliveryRecoveryAction.ManualReview, mismatching);
    }

    [Fact]
    public void GenericMarketplaceInvoicedObservationWithoutAnInvoiceNumberIsNotProof()
    {
        var result = InvoiceDeliveryRecoveryPolicy.Decide(
            MarketplaceInvoiceStatus.Invoiced,
            null,
            "INV-1",
            FreshReadAt,
            AttemptAt);

        Assert.Equal(InvoiceDeliveryRecoveryAction.ManualReview, result);
    }

    [Fact]
    public void ConfirmsDelayedHepsiburadaReadbackForTheAcceptedAttempt()
    {
        Assert.True(InvoiceDeliveryRecoveryPolicy.ConfirmsAcceptedHepsiburadaAttemptReadback(
            deliveryStatus: "UNKNOWN",
            deliveryErrorCode: InvoiceDeliveryRecoveryPolicy.HepsiburadaAcceptedButReadbackMissingErrorCode,
            observedMarketplaceStatus: MarketplaceInvoiceStatus.Invoiced,
            observedAt: FreshReadAt,
            acceptedAttemptAt: AttemptAt));
    }

    [Theory]
    [InlineData("FAILED", InvoiceDeliveryRecoveryPolicy.HepsiburadaAcceptedButReadbackMissingErrorCode, MarketplaceInvoiceStatus.Invoiced, true)]
    [InlineData("UNKNOWN", "DELIVERY_TIMEOUT", MarketplaceInvoiceStatus.Invoiced, true)]
    [InlineData("UNKNOWN", InvoiceDeliveryRecoveryPolicy.HepsiburadaAcceptedButReadbackMissingErrorCode, MarketplaceInvoiceStatus.NotInvoiced, true)]
    [InlineData("UNKNOWN", InvoiceDeliveryRecoveryPolicy.HepsiburadaAcceptedButReadbackMissingErrorCode, MarketplaceInvoiceStatus.Invoiced, false)]
    public void DoesNotAcceptGenericOrStaleHepsiburadaInvoiceObservations(
        string deliveryStatus,
        string deliveryErrorCode,
        MarketplaceInvoiceStatus observedMarketplaceStatus,
        bool observedAfterAttempt)
    {
        Assert.False(InvoiceDeliveryRecoveryPolicy.ConfirmsAcceptedHepsiburadaAttemptReadback(
            deliveryStatus,
            deliveryErrorCode,
            observedMarketplaceStatus,
            observedAfterAttempt ? FreshReadAt : AttemptAt,
            AttemptAt));
    }

    [Theory]
    [InlineData(InvoiceStatus.Completed, "CONFIRMED", null, true)]
    [InlineData(InvoiceStatus.Completed, "CONFIRMED", "INV-1", false)]
    [InlineData(InvoiceStatus.MarketplacePending, "CONFIRMED", null, false)]
    [InlineData(InvoiceStatus.Completed, "UNKNOWN", null, false)]
    public void PreservesPreviouslyConfirmedDeliveryWhenMarketplaceOmitsInvoiceNumber(
        InvoiceStatus invoiceStatus,
        string deliveryStatus,
        string? remoteInvoiceNumber,
        bool expected) =>
        Assert.Equal(expected, InvoiceDeliveryRecoveryPolicy.ShouldPreservePreviouslyConfirmedDelivery(
            invoiceStatus,
            deliveryStatus,
            remoteInvoiceNumber));

    [Theory]
    [InlineData("CONFIRMED", true, true)]
    [InlineData("REVOKED", true, false)]
    [InlineData("REVIEW_REQUIRED", true, false)]
    [InlineData(null, true, true)]
    public void CurrentMarketplaceDeliveryStateOverridesHistoricalConfirmation(string? currentStatus, bool hasHistory, bool expected) =>
        Assert.Equal(expected, MarketplaceInvoiceDeliveryEvidencePolicy.HasConfirmedDelivery(currentStatus, hasHistory));

    [Fact]
    public void OrderInvoiceCompletesOnlyAfterEveryActivePackageIsConfirmed()
    {
        var packageA = Guid.NewGuid();
        var packageB = Guid.NewGuid();

        Assert.False(MarketplaceInvoiceDeliveryEvidencePolicy.AllTargetsConfirmed([packageA, packageB], [packageA]));
        Assert.True(MarketplaceInvoiceDeliveryEvidencePolicy.AllTargetsConfirmed([packageA, packageB], [packageA, packageB]));
        Assert.False(MarketplaceInvoiceDeliveryEvidencePolicy.AllTargetsConfirmed([], [packageA]));
    }

    [Theory]
    [InlineData(InvoiceStatus.Completed, null, true)]
    [InlineData(InvoiceStatus.MarketplacePending, InvoiceDeliveryRecoveryPolicy.MarketplaceInvoiceIdentityMismatchErrorCode, true)]
    [InlineData(InvoiceStatus.Accepted, null, false)]
    [InlineData(InvoiceStatus.Rejected, null, false)]
    [InlineData(InvoiceStatus.ManualReview, "OTHER_REVIEW", false)]
    public void ExplicitMarketplaceRemovalMakesOnlyExistingDeliveryEligibleForRecovery(
        InvoiceStatus invoiceStatus,
        string? lastErrorCode,
        bool expected) =>
        Assert.Equal(expected, InvoiceDeliveryRecoveryPolicy.ShouldMarkMarketplaceInvoiceNotPresent(
            invoiceStatus,
            lastErrorCode,
            MarketplaceInvoiceStatus.NotInvoiced));

    [Fact]
    public void RejectedInvoiceIsNotAutomaticallyResent()
    {
        var result = InvoiceDeliveryRecoveryPolicy.Decide(
            MarketplaceInvoiceStatus.Rejected,
            null,
            "INV-1",
            FreshReadAt,
            AttemptAt);

        Assert.Equal(InvoiceDeliveryRecoveryAction.StopRejected, result);
    }

    [Theory]
    [InlineData("HEPSIBURADA", 3, true)]
    [InlineData("hepsiburada", 4, true)]
    [InlineData("HEPSIBURADA", 2, false)]
    [InlineData("TRENDYOL", 20, false)]
    public void StopsRepeatedHepsiburadaServerFailuresOnlyAtTheConfiguredLimit(string platformCode, int failureCount, bool expected) =>
        Assert.Equal(expected, InvoiceDeliveryRecoveryPolicy.ShouldStopAfterRemoteFailures(platformCode, failureCount));
}
