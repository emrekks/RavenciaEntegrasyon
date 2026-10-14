using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class RequiredAttributeMappingPolicyTests
{
    [Fact]
    public void IgnoresVerifiedMappingFromAnOlderSnapshot()
    {
        var currentSnapshot = Guid.NewGuid();
        var oldSnapshot = Guid.NewGuid();
        RequiredAttributeMappingReference[] requirements = [
            new(currentSnapshot, "color"),
            new(currentSnapshot, "size")
        ];
        RequiredAttributeMappingReference[] verifiedMappings = [
            new(currentSnapshot, "color"),
            new(oldSnapshot, "size")
        ];

        var missingCount = RequiredAttributeMappingPolicy.CountMissing(currentSnapshot, requirements, verifiedMappings);

        Assert.Equal(1, missingCount);
    }

    [Fact]
    public void CountsOnlyRequirementsForTheRequestedSnapshot()
    {
        var currentSnapshot = Guid.NewGuid();
        var oldSnapshot = Guid.NewGuid();
        RequiredAttributeMappingReference[] requirements = [
            new(currentSnapshot, "color"),
            new(oldSnapshot, "size")
        ];

        var missingCount = RequiredAttributeMappingPolicy.CountMissing(currentSnapshot, requirements, []);

        Assert.Equal(1, missingCount);
    }
}
