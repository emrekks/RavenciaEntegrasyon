using MarketplaceHub.Infrastructure.Persistence;

namespace MarketplaceHub.Adapters.ContractTests;

public sealed class CommonLabelCarrierPolicyTests
{
    [Theory]
    [InlineData("Aras Kargo Marketplace")]
    [InlineData("TEX")]
    [InlineData("tex marketplace")]
    public void Supports_returns_true_only_for_common_label_carriers(string carrier) =>
        Assert.True(CommonLabelCarrierPolicy.Supports(carrier));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Yurtiçi Kargo Marketplace")]
    [InlineData("PTT Kargo")]
    public void Supports_rejects_ineligible_carriers(string? carrier) =>
        Assert.False(CommonLabelCarrierPolicy.Supports(carrier));
}
