using MarketplaceHub.Api.Security;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// G5: operations users keep editing a connection, but only owners and administrators change what it does on the marketplace.
public sealed class ConnectionSettingsChangePolicyTests
{
    private static bool Touches(UpdateConnectionCommand command, string? environment = null, string? store = null) =>
        ConnectionSettingsChangePolicy.TouchesProtectedSettings(command, currentExternalWrites: false, currentInvoiceCreation: true, "PRODUCTION", environment, "store-1", store);

    [Fact]
    public void ANameOrUserAgentEditIsNotAProtectedChange()
    {
        Assert.False(Touches(new UpdateConnectionCommand("Yeni ad", "123 - Firma")));
        Assert.False(Touches(new UpdateConnectionCommand("Yeni ad", null, N11ShipmentTemplate: "Şablon", N11PreparingDay: 2)));
    }

    [Fact]
    public void ValuesSentBackUnchangedAreNotAChange()
    {
        Assert.False(Touches(new UpdateConnectionCommand("Ad", null, Environment: "production", ExternalStoreId: "store-1", ExternalWritesEnabled: false, InvoiceCreationEnabled: true), "PRODUCTION", "store-1"));
    }

    [Fact]
    public void WritesInvoicesEnvironmentAndStoreAreProtected()
    {
        Assert.True(Touches(new UpdateConnectionCommand("Ad", null, ExternalWritesEnabled: true)));
        Assert.True(Touches(new UpdateConnectionCommand("Ad", null, InvoiceCreationEnabled: false)));
        Assert.True(Touches(new UpdateConnectionCommand("Ad", null, Environment: "STAGE"), "STAGE"));
        Assert.True(Touches(new UpdateConnectionCommand("Ad", null, ExternalStoreId: "store-2"), store: "store-2"));
    }

    [Theory]
    [InlineData("OWNER", true)]
    [InlineData("ADMINISTRATOR", true)]
    [InlineData("OPERATIONS", false)]
    [InlineData("ACCOUNTING", false)]
    [InlineData("READONLY", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void OnlyOwnersAndAdministratorsAreElevated(string? role, bool elevated)
    {
        Assert.Equal(elevated, RoleAuthorizationMiddleware.IsElevated(role));
    }
}
