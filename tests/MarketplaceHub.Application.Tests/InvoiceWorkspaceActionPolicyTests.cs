using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class InvoiceWorkspaceActionPolicyTests
{
    [Fact]
    public void UnknownMarketplaceStateAllowsReadCheckOnly()
    {
        var actions = InvoiceWorkspaceActionPolicy.Resolve(
            "HEPSIBURADA", MarketplaceInvoiceStatus.Unknown, false, null, false, false, true, false);

        Assert.Equal(["CHECK_PLATFORM"], actions);
    }

    [Fact]
    public void ExistingMarketplaceInvoiceBlocksCreationAndDeliveryUntilReviewed()
    {
        var actions = InvoiceWorkspaceActionPolicy.Resolve(
            "TRENDYOL", MarketplaceInvoiceStatus.Invoiced, false, null, false, false, true, false);

        Assert.Equal(["CHECK_PLATFORM"], actions);
    }

    [Fact]
    public void VerifiedMissingInvoiceAllowsFiscalCreation()
    {
        var actions = InvoiceWorkspaceActionPolicy.Resolve(
            "HEPSIBURADA", MarketplaceInvoiceStatus.NotInvoiced, false, null, false, false, true, false);

        Assert.Equal(["CHECK_PLATFORM", "CREATE_FISCAL_INVOICE"], actions);
    }

    [Fact]
    public void ExistingAcceptedFiscalDocumentAllowsDeliveryWithoutNewFiscalCreation()
    {
        var actions = InvoiceWorkspaceActionPolicy.Resolve(
            "TRENDYOL", MarketplaceInvoiceStatus.NotInvoiced, true, InvoiceStatus.Accepted, true, false, true, false);

        Assert.Equal(["CHECK_PLATFORM", "DELIVER_EXISTING"], actions);
    }

    [Fact]
    public void ReadErrorPreventsFiscalCreateAndDelivery()
    {
        var actions = InvoiceWorkspaceActionPolicy.Resolve(
            "SHOPIFY", MarketplaceInvoiceStatus.NotInvoiced, false, null, false, false, true, true);

        Assert.Equal(["CHECK_PLATFORM"], actions);
    }

    [Fact]
    public void ManualDocumentCanOnlyBeDeliveredAfterMarketplaceAbsenceIsVerified()
    {
        var allowed = InvoiceWorkspaceActionPolicy.Resolve(
            "SHOPIFY", MarketplaceInvoiceStatus.NotInvoiced, false, null, false, true, true, false);
        var blocked = InvoiceWorkspaceActionPolicy.Resolve(
            "SHOPIFY", MarketplaceInvoiceStatus.Unknown, false, null, false, true, true, false);

        Assert.Equal(["CHECK_PLATFORM", "DELIVER_EXISTING"], allowed);
        Assert.Equal(["CHECK_PLATFORM"], blocked);
    }
}
