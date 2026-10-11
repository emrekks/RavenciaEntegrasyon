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
        Assert.Equal(50, ScheduledJobProducer.InvoiceReconciliationBatchSize("TRENDYOL", configuration));
        Assert.Equal(20, ScheduledJobProducer.InvoiceReconciliationBatchSize("SHOPIFY", configuration));
    }

    [Fact]
    public void InvoiceReadBatchConfigurationIsBounded()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MarketplaceSync:OrderInvoiceReconciliation:HepsiburadaBatchSize"] = "999",
            ["MarketplaceSync:OrderInvoiceReconciliation:TrendyolBatchSize"] = "999",
            ["MarketplaceSync:OrderInvoiceReconciliation:BatchSize"] = "0"
        }).Build();

        Assert.Equal(250, ScheduledJobProducer.InvoiceReconciliationBatchSize("HEPSIBURADA", configuration));
        Assert.Equal(100, ScheduledJobProducer.InvoiceReconciliationBatchSize("TRENDYOL", configuration));
        Assert.Equal(1, ScheduledJobProducer.InvoiceReconciliationBatchSize("SHOPIFY", configuration));
    }

    [Fact]
    public void TrendyolInvoiceReadBatchCanBeConfiguredWithoutChangingOtherPlatforms()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MarketplaceSync:OrderInvoiceReconciliation:TrendyolBatchSize"] = "3",
            ["MarketplaceSync:OrderInvoiceReconciliation:BatchSize"] = "40"
        }).Build();

        Assert.Equal(3, ScheduledJobProducer.InvoiceReconciliationBatchSize("TRENDYOL", configuration));
        Assert.Equal(40, ScheduledJobProducer.InvoiceReconciliationBatchSize("SHOPIFY", configuration));
    }

    [Fact]
    public void InvoiceReconciliationUsesFastUnresolvedAndSlowerStableCadences()
    {
        var now = new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal(TimeSpan.FromMinutes(15), MarketplaceInvoiceReconciliationCadencePolicy.IntervalFor(
            MarketplaceInvoiceStatus.NotInvoiced, false, now.AddDays(-200), now));
        Assert.Equal(TimeSpan.FromMinutes(15), MarketplaceInvoiceReconciliationCadencePolicy.IntervalFor(
            MarketplaceInvoiceStatus.Invoiced, true, now.AddDays(-200), now));
        Assert.Equal(TimeSpan.FromHours(1), MarketplaceInvoiceReconciliationCadencePolicy.IntervalFor(
            MarketplaceInvoiceStatus.Invoiced, false, now.AddDays(-89), now));
        Assert.Equal(TimeSpan.FromHours(24), MarketplaceInvoiceReconciliationCadencePolicy.IntervalFor(
            MarketplaceInvoiceStatus.Invoiced, false, now.AddDays(-90), now));
    }

    [Fact]
    public void InvoiceReconciliationCadenceUsesLastSuccessfulCheckTime()
    {
        var now = new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

        Assert.True(MarketplaceInvoiceReconciliationCadencePolicy.IsDue(
            MarketplaceInvoiceStatus.Invoiced, false, now.AddDays(-5), now.AddHours(-1), now));
        Assert.False(MarketplaceInvoiceReconciliationCadencePolicy.IsDue(
            MarketplaceInvoiceStatus.Invoiced, false, now.AddDays(-5), now.AddMinutes(-59), now));
        Assert.True(MarketplaceInvoiceReconciliationCadencePolicy.IsDue(
            MarketplaceInvoiceStatus.NotInvoiced, false, now.AddDays(-5), now.AddMinutes(-15), now));
    }

    [Fact]
    public void ShopifyInvoiceReadReconciliationIsScheduledAsReadOnlyWork()
    {
        const string resourceType = "ORDER_INVOICE_RECONCILIATION";

        Assert.True(ScheduledJobProducer.SupportsShopifyPolicy(resourceType));
        Assert.True(ScheduledJobProducer.IsShopifyReadPolicy(resourceType));
        Assert.True(ScheduledJobProducer.DefaultPolicyEnabled("SHOPIFY", resourceType));
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
