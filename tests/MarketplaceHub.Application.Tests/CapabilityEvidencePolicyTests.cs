using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class CapabilityEvidencePolicyTests
{
    [Fact]
    public void HepsiburadaWriteCapabilityRequiresSupportedExactScopeAndFixtureChecksum()
    {
        var connection = new PlatformConnection
        {
            Id = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            PlatformCode = "HEPSIBURADA",
            Environment = "STAGE",
            DisplayName = "SIT Store",
            ExternalStoreId = "merchant-17",
            Status = "VERIFIED",
            ApiVersion = "V1.0"
        };
        var capability = new PlatformCapability
        {
            Id = Guid.NewGuid(),
            TenantId = connection.TenantId,
            ConnectionId = connection.Id,
            Code = MarketplaceCapabilities.ProductWrite,
            SupportLevel = CapabilitySupportLevel.Supported,
            ApiVersion = connection.ApiVersion,
            Environment = connection.Environment,
            StoreScope = connection.ExternalStoreId,
            SourceUrl = "https://developers.hepsiburada.com/tr/companies/hepsiburada",
            FixtureChecksum = new string('A', 64),
            VerifiedAt = DateTimeOffset.UtcNow
        };

        Assert.True(CapabilityEvidencePolicy.IsVerifiedWriteCapability(capability, connection, MarketplaceCapabilities.ProductWrite));
        Assert.False(CapabilityEvidencePolicy.IsVerifiedWriteCapability(capability, connection, MarketplaceCapabilities.PriceWrite));

        capability.Environment = "PRODUCTION";
        Assert.False(CapabilityEvidencePolicy.IsVerifiedWriteCapability(capability, connection, MarketplaceCapabilities.ProductWrite));
        capability.Environment = connection.Environment;
        capability.FixtureChecksum = "not-a-fixture";
        Assert.False(CapabilityEvidencePolicy.IsVerifiedWriteCapability(capability, connection, MarketplaceCapabilities.ProductWrite));
    }
}
