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

    private static PlatformConnection Connection(string environment) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        PublicId = Guid.NewGuid(),
        PlatformCode = "TRENDYOL",
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
