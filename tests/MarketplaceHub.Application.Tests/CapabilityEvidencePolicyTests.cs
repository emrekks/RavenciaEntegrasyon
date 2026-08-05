using MarketplaceHub.Application;

namespace MarketplaceHub.Application.Tests;

public sealed class CapabilityEvidencePolicyTests
{
    [Theory]
    [InlineData("TRENDYOL", "developers.trendyol.com")]
    [InlineData("trendyol_efaturam", "developers.trendyolefaturam.com")]
    public void Official_documentation_host_is_platform_scoped(string platformCode, string expected) =>
        Assert.Equal(expected, CapabilityEvidencePolicy.OfficialDocumentationHost(platformCode));

    [Theory]
    [InlineData(F4Capabilities.InvoiceSubmit, true)]
    [InlineData(F4Capabilities.InvoiceCancel, true)]
    [InlineData(F4Capabilities.InvoiceDeliver, true)]
    [InlineData(F4Capabilities.InvoiceStatusRead, false)]
    [InlineData(F4Capabilities.InvoiceDocumentRead, false)]
    [InlineData(F4Capabilities.TaxpayerQuery, false)]
    public void Financial_writes_require_stage_fixture_checksum(string capability, bool expected) =>
        Assert.Equal(expected, CapabilityEvidencePolicy.RequiresStageFixtureChecksum(capability));

    [Fact]
    public void Unsupported_platform_is_rejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => CapabilityEvidencePolicy.OfficialDocumentationHost("OTHER"));
}
