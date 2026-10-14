using MarketplaceHub.Application;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class MarketplaceJobDispatchPolicyTests
{
    [Theory]
    [InlineData(MarketplaceJobTypes.HepsiburadaOrderInvoiceReconciliation)]
    [InlineData(MarketplaceJobTypes.HepsiburadaOrderStatusSync)]
    [InlineData(MarketplaceJobTypes.HepsiburadaReturnSync)]
    [InlineData(MarketplaceJobTypes.HepsiburadaWebhookIngest)]
    public void HepsiburadaReadJobsAreRoutedToMarketplaceProcessor(string jobType)
    {
        Assert.True(MarketplaceJobTypes.IsMarketplaceProcessorJob(jobType));
    }

    [Theory]
    [InlineData(InvoicingJobTypes.InvoiceSubmit)]
    [InlineData(InvoicingJobTypes.InvoiceReconcile)]
    [InlineData("UNKNOWN_JOB")]
    public void NonMarketplaceJobsAreNotRoutedToMarketplaceProcessor(string jobType)
    {
        Assert.False(MarketplaceJobTypes.IsMarketplaceProcessorJob(jobType));
    }
}
