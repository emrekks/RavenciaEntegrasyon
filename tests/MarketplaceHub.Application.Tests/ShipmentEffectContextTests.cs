using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class ShipmentEffectContextTests
{
    [Fact]
    public void Picking_IsVerifiableWithoutExpectedValue()
    {
        var (action, expected) = MarketplaceJobProcessor.ShipmentEffectContext(" picking ", "{}");

        Assert.Equal("PICKING", action);
        Assert.Null(expected);
    }

    [Fact]
    public void TrendyolCarrierChange_RecordsTheRequestedCarrier()
    {
        var (action, expected) = MarketplaceJobProcessor.ShipmentEffectContext("CHANGE_CARGO_PROVIDER", """{"cargoProvider":" YKMP "}""");

        Assert.Equal("CHANGE_CARGO_PROVIDER", action);
        Assert.Equal("YKMP", expected);
    }

    [Theory]
    [InlineData("CHANGE_CARGO_PROVIDER", """{"CargoCompanyShortName":"MNG"}""")]
    [InlineData("CHANGE_CARGO_PROVIDER", """{"cargoProvider":""}""")]
    [InlineData("CHANGE_CARGO_PROVIDER", "not json")]
    [InlineData("UNPACK", "{}")]
    [InlineData("SPLIT", """{"cargoProvider":"YKMP"}""")]
    public void ActionsAReadCannotJudge_CarryNoAction(string action, string payload)
    {
        var (effectAction, expected) = MarketplaceJobProcessor.ShipmentEffectContext(action, payload);

        Assert.Null(effectAction);
        Assert.Null(expected);
    }
}
