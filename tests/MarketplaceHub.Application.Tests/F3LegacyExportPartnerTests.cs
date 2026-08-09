using MarketplaceHub.Infrastructure.Persistence;

namespace MarketplaceHub.Application.Tests;

public sealed class F3LegacyExportPartnerTests
{
    [Theory]
    [InlineData("PM3- Arvato")]
    [InlineData("pm3 arvato")]
    public void Legacy_partner_identity_is_classified_as_export(string name)
    {
        Assert.True(F3SalesService.IsLegacyTrendyolExportPartner(name));
    }

    [Theory]
    [InlineData("Arvato")]
    [InlineData("PM3")]
    [InlineData("Normal müşteri")]
    public void Partial_or_unrelated_identity_is_not_classified_as_export(string name)
    {
        Assert.False(F3SalesService.IsLegacyTrendyolExportPartner(name));
    }
}
