using MarketplaceHub.Infrastructure.Adapters.TrendyolEFaturam.Contracts;
using MarketplaceHub.Infrastructure.Adapters.TrendyolEFaturam.Mapping;

namespace MarketplaceHub.Adapters.ContractTests;

public sealed class F4TrendyolEFaturamContractTests
{
    [Fact]
    public void Anonymous_taxpayer_fixture_maps_without_personal_data()
    {
        var json = Fixture("taxpayer-registered-anonymous.json"); var result = TrendyolEFaturamJsonMapper.Taxpayer(json);
        Assert.True(result.IsRegistered); Assert.Equal("100001", result.ProviderCustomerId); Assert.Equal(2, result.Applications.Count); Assert.All(result.Applications, application => Assert.True(application.Activated)); Assert.Equal(64, result.RawResultHash.Length);
        Assert.DoesNotContain("@", json, StringComparison.Ordinal); Assert.DoesNotContain("+90", json, StringComparison.Ordinal);
    }


    [Theory]
    [InlineData("earchive-status-approved-anonymous.json", "ACCEPTED", true, "205")]
    [InlineData("earchive-status-cancelled-anonymous.json", "CANCELLED", true, "305")]
    [InlineData("earchive-status-rejected-anonymous.json", "REJECTED", true, "105")]
    public void Official_earchive_status_codes_are_mapped_without_text_guessing(string fixture, string canonical, bool terminal, string raw)
    {
        var result = TrendyolEFaturamJsonMapper.InvoiceStatus(Fixture(fixture), "local-reference");
        Assert.Equal(canonical, result.CanonicalStatus);
        Assert.Equal(terminal, result.IsTerminal);
        Assert.Equal(raw, result.RawStatus);
        Assert.Equal("RVN2026000000001", result.InvoiceNumber);
    }

    [Theory]
    [InlineData(10, "PENDING", false)]
    [InlineData(20, "PENDING", false)]
    [InlineData(29, "REJECTED", true)]
    [InlineData(30, "PENDING", false)]
    [InlineData(40, "PENDING", false)]
    [InlineData(50, "PENDING", false)]
    [InlineData(100, "PENDING", false)]
    [InlineData(105, "REJECTED", true)]
    [InlineData(200, "PENDING", false)]
    [InlineData(205, "ACCEPTED", true)]
    [InlineData(305, "CANCELLED", true)]
    [InlineData(405, "REJECTED", true)]
    [InlineData(999, "MANUAL_REVIEW", true)]
    public void Official_status_catalog_is_fail_closed(int status, string canonical, bool terminal)
    {
        var result = TrendyolEFaturamStatusCatalog.Classify(status);
        Assert.Equal(canonical, result.CanonicalStatus);
        Assert.Equal(terminal, result.IsTerminal);
    }

    [Fact]
    public void Customer_signin_scope_is_mapped_from_official_body()
    {
        var access = TrendyolEFaturamJsonMapper.CustomerAccess(Fixture("customer-signin-anonymous.json"));
        Assert.Equal(10, access.CompanyId);
        Assert.Equal(20, access.UserId);
        Assert.Equal(100001, access.PartnerCustomerId);
        Assert.Equal("anonymous-stage-token", access.AccessToken);
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
