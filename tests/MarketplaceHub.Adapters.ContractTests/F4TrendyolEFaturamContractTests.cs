using MarketplaceHub.Infrastructure.Adapters.TrendyolEFaturam.Contracts;
using MarketplaceHub.Infrastructure.Adapters.TrendyolEFaturam.Mapping;

namespace MarketplaceHub.Adapters.ContractTests;

public sealed class F4TrendyolEFaturamContractTests
{
    [Fact]
    public void Anonymous_taxpayer_fixture_maps_without_personal_data()
    {
        var json = Fixture("taxpayer-registered-anonymous.json"); var result = TrendyolEFaturamJsonMapper.Taxpayer(json);
        Assert.True(result.IsRegistered); Assert.Equal("100001", result.ProviderCustomerId); Assert.Equal(64, result.RawResultHash.Length);
        Assert.DoesNotContain("@", json, StringComparison.Ordinal); Assert.DoesNotContain("+90", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1.0.0", true)]
    [InlineData("latest", false)]
    [InlineData("1.0", false)]
    public void Api_version_is_exactly_pinned(string value, bool expected) => Assert.Equal(expected, TrendyolEFaturamContractGuard.IsPinnedApiVersion(value));

    [Fact]
    public void Tax_id_format_is_not_treated_as_taxpayer_evidence()
    {
        Assert.True(TrendyolEFaturamContractGuard.IsTaxIdFormat("1234567890"));
        Assert.False(TrendyolEFaturamContractGuard.IsTaxIdFormat("123456789"));
    }

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(FindRoot(), "src", "MarketplaceHub.Infrastructure", "Adapters", "TrendyolEFaturam", "Fixtures", name));
    private static string FindRoot() { var path = AppContext.BaseDirectory; while (!File.Exists(Path.Combine(path, "MarketplaceHub.sln"))) path = Directory.GetParent(path)?.FullName ?? throw new InvalidOperationException("Root not found"); return path; }
}
