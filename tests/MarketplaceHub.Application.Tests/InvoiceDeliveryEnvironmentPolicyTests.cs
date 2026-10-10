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

    [Fact]
    public void WorkspaceRequiresProviderCredentialsFromTheMarketplaceEnvironment()
    {
        string[] configuredProviderEnvironments = ["STAGE"];

        Assert.True(InvoiceDeliveryEnvironmentPolicy.HasProviderCredentialForMarketplace("STAGE", configuredProviderEnvironments));
        Assert.False(InvoiceDeliveryEnvironmentPolicy.HasProviderCredentialForMarketplace("PRODUCTION", configuredProviderEnvironments));
        Assert.False(InvoiceDeliveryEnvironmentPolicy.HasProviderCredentialForMarketplace("UNKNOWN", configuredProviderEnvironments));
        Assert.False(InvoiceDeliveryEnvironmentPolicy.HasProviderCredentialForMarketplace("PRODUCTION", []));
        Assert.False(InvoiceDeliveryEnvironmentPolicy.HasProviderCredentialForMarketplace("UNKNOWN", null, legacyProviderHasCredential: true));
    }

    [Fact]
    public void AutomaticMarketplaceDeliveryUsesTheProductionWriteContext()
    {
        var context = new AdapterContext(Guid.NewGuid(), Guid.NewGuid(), "correlation", "delivery", DateTimeOffset.UtcNow.AddMinutes(2));
        var automatic = InvoiceDeliveryOperationPolicy.ForAutomaticMarketplaceDelivery(context);
        var connection = new MarketplaceHub.Domain.PlatformConnection
        {
            PlatformCode = "HEPSIBURADA",
            Environment = "PRODUCTION",
            DisplayName = "Hepsiburada test",
            ExternalStoreId = "merchant-test",
            ApiVersion = "v1",
            Status = "ACTIVE"
        };

        Assert.Equal(IntegrationOperation.Automatic, automatic.Operation);
        Assert.True(IntegrationRuntimePolicy.AllowsExternalWrite(connection, automatic, globalWritesEnabled: true, connectionWritesEnabled: true));
        Assert.False(IntegrationRuntimePolicy.AllowsExternalWrite(connection, automatic, globalWritesEnabled: false, connectionWritesEnabled: true));
    }
}
