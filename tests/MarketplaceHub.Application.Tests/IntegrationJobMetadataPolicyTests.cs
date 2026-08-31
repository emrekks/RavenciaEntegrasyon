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
}
