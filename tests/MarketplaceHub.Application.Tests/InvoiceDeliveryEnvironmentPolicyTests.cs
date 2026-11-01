using MarketplaceHub.Application;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class InvoiceDeliveryEnvironmentPolicyTests
{
    [Theory]
    [InlineData("PRODUCTION", "production")]
    [InlineData(" stage ", "STAGE")]
    public void AllowsDeliveryOnlyWhenBothConnectionsShareAValidEnvironment(string providerEnvironment, string marketplaceEnvironment)
    {
        Assert.True(InvoiceDeliveryEnvironmentPolicy.IsCompatible(providerEnvironment, marketplaceEnvironment));
    }

    [Theory]
    [InlineData("STAGE", "PRODUCTION")]
    [InlineData("PRODUCTION", "STAGE")]
    [InlineData(null, "PRODUCTION")]
    [InlineData("UNKNOWN", "UNKNOWN")]
    public void RejectsDeliveryAcrossOrWithoutKnownEnvironments(string? providerEnvironment, string? marketplaceEnvironment)
    {
        Assert.False(InvoiceDeliveryEnvironmentPolicy.IsCompatible(providerEnvironment, marketplaceEnvironment));
        Assert.Contains("yalnız aynı ortamdaki pazaryeri siparişine", InvoiceDeliveryEnvironmentPolicy.DescribeMismatch(providerEnvironment, marketplaceEnvironment), StringComparison.OrdinalIgnoreCase);
    }
}
