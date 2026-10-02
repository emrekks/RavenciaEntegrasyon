using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class IntegrationJobMetadataPolicyTests
{
    [Theory]
    [InlineData("TRENDYOL_ORDER_SYNC", "orders")]
    [InlineData("TRENDYOL_RETURN_RECONCILIATION", "returns")]
    [InlineData("TRENDYOL_PRICE_INVENTORY_SYNC", "inventory")]
    [InlineData("INVOICE_RECONCILE", "invoices")]
    [InlineData("TRENDYOL_PRODUCT_UPDATE", "products")]
    [InlineData("HEPSIBURADA_REFERENCE_SYNC", "connections")]
    [InlineData("EFATURAM_CONNECTION_TEST", "connections")]
    public void Known_job_types_have_explicit_resource(string jobType, string resource)
    {
        Assert.Equal(resource, IntegrationJobMetadataPolicy.FromJobType(jobType).ResourceType);
    }

    [Fact]
    public void Unknown_job_type_does_not_match_by_substring()
    {
        Assert.Equal("jobs", IntegrationJobMetadataPolicy.FromJobType("THIRD_PARTY_ORDERLY_TASK").ResourceType);
    }

    [Fact]
    public void Hepsiburada_reference_sync_has_a_provider_specific_read_job_type()
    {
        Assert.Equal(MarketplaceJobTypes.HepsiburadaReferenceSync, MarketplaceJobTypes.ForPlatform("hepsiburada", MarketplaceJobTypes.ReferenceSync));
        Assert.True(MarketplaceJobTypes.IsMarketplaceProcessorJob(MarketplaceJobTypes.HepsiburadaReferenceSync));
    }
}
