using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class IntegrationRuntimePolicyTests
{
    [Fact]
    public void AutomaticPriceInventoryWriteIsBlockedForStageEvenWhenSwitchesAreOn()
    {
        var connection = Connection("STAGE");
        var context = Context(IntegrationOperation.Automatic);

        Assert.False(IntegrationRuntimePolicy.AllowsExternalWrite(connection, context, globalWritesEnabled: true, connectionWritesEnabled: true));
    }

    [Fact]
    public void AutomaticPriceInventoryWriteRequiresBothSwitchesInProduction()
    {
        var connection = Connection("PRODUCTION");
        var context = Context(IntegrationOperation.Automatic);

        Assert.False(IntegrationRuntimePolicy.AllowsExternalWrite(connection, context, globalWritesEnabled: false, connectionWritesEnabled: true));
        Assert.False(IntegrationRuntimePolicy.AllowsExternalWrite(connection, context, globalWritesEnabled: true, connectionWritesEnabled: false));
        Assert.True(IntegrationRuntimePolicy.AllowsExternalWrite(connection, context, globalWritesEnabled: true, connectionWritesEnabled: true));
    }

    [Fact]
    public void ManualStageWriteKeepsItsExistingExplicitTestPath()
    {
        var connection = Connection("STAGE");
        var context = Context(IntegrationOperation.Manual);

        Assert.True(IntegrationRuntimePolicy.AllowsExternalWrite(connection, context, globalWritesEnabled: false, connectionWritesEnabled: false));
    }

    [Fact]
    public void OneTimeHepsiburadaInvoiceWriteIsNarrowAndDoesNotOpenGeneralWrites()
    {
        var connection = Connection("PRODUCTION", "HEPSIBURADA");
        var context = Context(IntegrationOperation.Manual) with
        {
            ConnectionId = connection.Id,
            IsOneTimeInvoiceDeliveryAuthorized = true,
            OneTimeInvoiceDeliveryOrderNumber = OneTimeInvoiceDeliveryPolicy.TargetOrderNumber
        };

        Assert.False(IntegrationRuntimePolicy.AllowsExternalWrite(connection, context, globalWritesEnabled: false, connectionWritesEnabled: false));
        Assert.True(IntegrationRuntimePolicy.AllowsOneTimeHepsiburadaInvoiceDelivery(connection, context, OneTimeInvoiceDeliveryPolicy.TargetOrderNumber));
        Assert.False(IntegrationRuntimePolicy.AllowsOneTimeHepsiburadaInvoiceDelivery(connection, context, "OTHER-ORDER"));
        Assert.False(IntegrationRuntimePolicy.AllowsOneTimeHepsiburadaInvoiceDelivery(connection, context with { Operation = IntegrationOperation.Automatic }, OneTimeInvoiceDeliveryPolicy.TargetOrderNumber));
        Assert.False(IntegrationRuntimePolicy.AllowsOneTimeHepsiburadaInvoiceDelivery(Connection("STAGE", "HEPSIBURADA"), context, OneTimeInvoiceDeliveryPolicy.TargetOrderNumber));
        Assert.False(IntegrationRuntimePolicy.AllowsOneTimeHepsiburadaInvoiceDelivery(Connection("PRODUCTION"), context, OneTimeInvoiceDeliveryPolicy.TargetOrderNumber));
        Assert.False(IntegrationRuntimePolicy.AllowsOneTimeHepsiburadaInvoiceDelivery(connection, context with { IsOneTimeInvoiceDeliveryAuthorized = false }, OneTimeInvoiceDeliveryPolicy.TargetOrderNumber));
    }

    [Theory]
    [InlineData("TRENDYOL")]
    [InlineData("HEPSIBURADA")]
    public void DedicatedInvoiceDeliveryPermissionDoesNotEnableOtherMarketplaceWrites(string platformCode)
    {
        var connection = Connection("PRODUCTION", platformCode);
        var context = Context(IntegrationOperation.Automatic) with
        {
            ConnectionId = connection.Id,
            IsAutomaticInvoiceMarketplaceDelivery = true
        };
        var stageConnection = Connection("STAGE", platformCode);
        stageConnection.Id = connection.Id;
        var verifiedConnection = Connection("PRODUCTION", platformCode);
        verifiedConnection.Id = connection.Id;
        verifiedConnection.Status = "VERIFIED";
        var shopifyConnection = Connection("PRODUCTION", "SHOPIFY");
        shopifyConnection.Id = connection.Id;

        Assert.False(IntegrationRuntimePolicy.AllowsAutomaticInvoiceMarketplaceDelivery(connection, context, invoiceMarketplaceDeliveryWritesEnabled: false));
        Assert.True(IntegrationRuntimePolicy.AllowsAutomaticInvoiceMarketplaceDelivery(connection, context, invoiceMarketplaceDeliveryWritesEnabled: true));
        Assert.False(IntegrationRuntimePolicy.AllowsExternalWrite(connection, context, globalWritesEnabled: false, connectionWritesEnabled: false));
        Assert.False(IntegrationRuntimePolicy.AllowsAutomaticInvoiceMarketplaceDelivery(connection, context with { IsAutomaticInvoiceMarketplaceDelivery = false }, invoiceMarketplaceDeliveryWritesEnabled: true));
        Assert.False(IntegrationRuntimePolicy.AllowsAutomaticInvoiceMarketplaceDelivery(connection, context with { ConnectionId = Guid.NewGuid() }, invoiceMarketplaceDeliveryWritesEnabled: true));
        Assert.False(IntegrationRuntimePolicy.AllowsAutomaticInvoiceMarketplaceDelivery(stageConnection, context, invoiceMarketplaceDeliveryWritesEnabled: true));
        Assert.False(IntegrationRuntimePolicy.AllowsAutomaticInvoiceMarketplaceDelivery(verifiedConnection, context, invoiceMarketplaceDeliveryWritesEnabled: true));
        Assert.False(IntegrationRuntimePolicy.AllowsAutomaticInvoiceMarketplaceDelivery(shopifyConnection, context, invoiceMarketplaceDeliveryWritesEnabled: true));
    }

    private static PlatformConnection Connection(string environment, string platformCode = "TRENDYOL") => new()
    {
        Id = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        PublicId = Guid.NewGuid(),
        PlatformCode = platformCode,
        Environment = environment,
        DisplayName = "Test",
        ExternalStoreId = "2738",
        Status = "ACTIVE",
        ApiVersion = "V2",
        LastErrorCode = null
    };

    private static AdapterContext Context(IntegrationOperation operation) =>
        new(Guid.NewGuid(), Guid.NewGuid(), "test", "test", DateTimeOffset.UtcNow.AddMinutes(1), Operation: operation);
}
