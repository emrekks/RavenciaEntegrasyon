using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class InvoiceReconciliationSchedulingTests
{
    [Fact]
    public void HepsiburadaUsesLargerInvoiceReadBatchByDefault()
    {
        var configuration = new ConfigurationBuilder().Build();

        Assert.Equal(100, ScheduledJobProducer.InvoiceReconciliationBatchSize("HEPSIBURADA", configuration));
        Assert.Equal(20, ScheduledJobProducer.InvoiceReconciliationBatchSize("TRENDYOL", configuration));
        Assert.Equal(20, ScheduledJobProducer.InvoiceReconciliationBatchSize("SHOPIFY", configuration));
    }

    [Fact]
    public void InvoiceReadBatchConfigurationIsBounded()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MarketplaceSync:OrderInvoiceReconciliation:HepsiburadaBatchSize"] = "999",
            ["MarketplaceSync:OrderInvoiceReconciliation:BatchSize"] = "0"
        }).Build();

        Assert.Equal(250, ScheduledJobProducer.InvoiceReconciliationBatchSize("HEPSIBURADA", configuration));
        Assert.Equal(1, ScheduledJobProducer.InvoiceReconciliationBatchSize("TRENDYOL", configuration));
    }

    [Theory]
    [InlineData("HEPSIBURADA", 900, 30, true)]
    [InlineData("HEPSIBURADA", 300, 10, true)]
    [InlineData("TRENDYOL", 900, 30, true)]
    [InlineData("TRENDYOL", 300, 10, false)]
    public void OnlyRecognizedInvoiceDefaultsAreAutomaticallyUpgraded(
        string platformCode,
        int intervalSeconds,
        int jitterSeconds,
        bool expected)
    {
        var policy = new ConnectionSyncPolicy
        {
            ResourceType = "ORDER_INVOICE_RECONCILIATION",
            IntervalSeconds = intervalSeconds,
            OverlapSeconds = 0,
            JitterSeconds = jitterSeconds
        };

        Assert.Equal(expected, ScheduledJobProducer.IsKnownDefault(policy, platformCode));
    }
}
