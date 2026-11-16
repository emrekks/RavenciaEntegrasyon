using MarketplaceHub.Infrastructure.Adapters;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// ADAPTOR-DIGER-9: one reader of the connection's ExternalWritesEnabled switch.
public sealed class ConnectionSettingsTests
{
    [Theory]
    [InlineData("{\"ExternalWritesEnabled\":true}", true)]
    [InlineData("{\"ExternalWritesEnabled\":false}", false)]
    [InlineData("{\"ExternalWritesEnabled\":\"true\"}", false)]
    [InlineData("{\"ExternalWritesEnabled\":1}", false)]
    [InlineData("{\"externalWritesEnabled\":true}", false)]
    [InlineData("{}", false)]
    [InlineData("[]", false)]
    [InlineData("{bozuk", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyAJsonTrueOpensTheConnectionSwitch(string? settings, bool expected)
    {
        Assert.Equal(expected, ConnectionSettings.ExternalWritesEnabled(settings));
        Assert.Equal(expected, InvoiceMarketplaceAutoDeliveryPolicy.ConnectionWritesEnabled(settings));
        Assert.Equal(expected, StockProjectionOutbox.StoredExternalWritesEnabled(settings));
        if (settings is not null) Assert.Equal(expected, ExternalWriteGate.ConnectionWritesEnabled(settings));
    }

    // ADAPTOR-DIGER-9: one write rule; a VERIFIED production connection with both switches on does not write.
    [Fact]
    public void AVerifiedProductionConnectionDoesNotWrite() =>
        Assert.False(ExternalWriteGate.AllowsProductionWrite(new MarketplaceHub.Domain.PlatformConnection
        {
            PlatformCode = "N11",
            Environment = "PRODUCTION",
            Status = "VERIFIED",
            SettingsJson = """{"ExternalWritesEnabled":true}""",
            DisplayName = "N11",
            ExternalStoreId = "x",
            ApiVersion = "V1"
        }, globalWritesEnabled: true));
}
