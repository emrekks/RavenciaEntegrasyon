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
}
