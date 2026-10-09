using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class CapabilityEvidencePolicyTests
{
    [Theory]
    [InlineData("SUPPORTED", CapabilitySupportLevel.Supported)]
    [InlineData("NOT_SUPPORTED", CapabilitySupportLevel.NotSupported)]
    [InlineData("NOTSUPPORTED", CapabilitySupportLevel.NotSupported)]
    [InlineData("TEMPORARILY_UNAVAILABLE", CapabilitySupportLevel.TemporarilyUnavailable)]
    [InlineData("unrecognized", CapabilitySupportLevel.Unknown)]
    public void ParsesEveryCapabilitySupportLevel(string value, CapabilitySupportLevel expected)
    {
        Assert.Equal(expected, CapabilityEvidencePolicy.ParseSupportLevel(value));
    }

    [Fact]
    public void ApplyingFreshEvidenceReplacesStaleCapabilityScopeAndPreservesExplicitMissingPermissions()
    {
        var capability = new PlatformCapability
        {
            Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), ConnectionId = Guid.NewGuid(), Code = MarketplaceCapabilities.ReturnWrite,
            ApiVersion = "2026-04", Environment = "STAGE", StoreScope = "old-shop", Version = 1
        };
        var verifiedAt = DateTimeOffset.UtcNow;
        var evidence = new CapabilityEvidence(MarketplaceCapabilities.ReturnWrite, "NOT_SUPPORTED", "2026-07", "PRODUCTION", "shop-name",
            "https://shopify.dev/docs/api/admin-graphql/2026-07/mutations/returnApproveRequest", "2026-07", "write_returns,write_marketplace_returns",
            null, "Shopify uygulamasında gerekli iade yazma izni yok.", null, verifiedAt);

        CapabilityEvidencePolicy.ApplyEvidence(capability, evidence);

        Assert.Equal(CapabilitySupportLevel.NotSupported, capability.SupportLevel);
        Assert.Equal("2026-07", capability.ApiVersion);
        Assert.Equal("PRODUCTION", capability.Environment);
        Assert.Equal("shop-name", capability.StoreScope);
        Assert.Equal("write_returns,write_marketplace_returns", capability.RequiredScope);
        Assert.Equal("Shopify uygulamasında gerekli iade yazma izni yok.", capability.EvidenceNote);
        Assert.Equal(verifiedAt, capability.VerifiedAt);
        Assert.Equal(2, capability.Version);
    }

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
