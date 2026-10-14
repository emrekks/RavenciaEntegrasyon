using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class ReferenceMappingRevalidationPolicyTests
{
    [Fact]
    public void RevalidatesAnUnchangedActiveReference()
    {
        var previous = ReferenceItem("CATEGORY_ATTRIBUTES", "size", "123", isRequired: true);
        var current = ReferenceItem("CATEGORY_ATTRIBUTES", "size", "123", isRequired: true);

        Assert.True(ReferenceMappingRevalidationPolicy.CanRevalidate("CATEGORY_ATTRIBUTES", previous, current));
    }

    [Theory]
    [InlineData("ExternalId")]
    [InlineData("ParentExternalId")]
    [InlineData("Name")]
    [InlineData("Path")]
    [InlineData("IsActive")]
    [InlineData("IsRequired")]
    [InlineData("AllowsCustomValue")]
    [InlineData("AllowsMultipleValues")]
    public void DoesNotRevalidateWhenReferenceContractChanges(string changedProperty)
    {
        var previous = ReferenceItem("CATEGORY_ATTRIBUTES", "size", "123", isRequired: true);
        var current = ReferenceItem("CATEGORY_ATTRIBUTES", "size", "123", isRequired: true);
        switch (changedProperty)
        {
            case "ExternalId": current.ExternalId = "new-size"; break;
            case "ParentExternalId": current.ParentExternalId = "456"; break;
            case "Name": current.Name = "Beden grubu"; break;
            case "Path": current.Path = "Giyim/Beden"; break;
            case "IsActive": current.IsActive = false; break;
            case "IsRequired": current.IsRequired = false; break;
            case "AllowsCustomValue": current.AllowsCustomValue = true; break;
            case "AllowsMultipleValues": current.AllowsMultipleValues = true; break;
        }

        Assert.False(ReferenceMappingRevalidationPolicy.CanRevalidate("CATEGORY_ATTRIBUTES", previous, current));
    }

    [Fact]
    public void DoesNotRevalidateUnknownReferenceTypes()
    {
        var previous = ReferenceItem("UNSUPPORTED", "one", null);
        var current = ReferenceItem("UNSUPPORTED", "one", null);

        Assert.False(ReferenceMappingRevalidationPolicy.CanRevalidate("UNSUPPORTED", previous, current));
    }

    private static ReferenceItem ReferenceItem(string resourceType, string externalId, string? parentExternalId, bool isRequired = false) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        ConnectionId = Guid.NewGuid(),
        SnapshotId = Guid.NewGuid(),
        ResourceType = resourceType,
        ExternalId = externalId,
        ParentExternalId = parentExternalId,
        Name = "Beden",
        NormalizedName = "BEDEN",
        Path = "Beden",
        Depth = 0,
        IsLeaf = true,
        IsActive = true,
        IsRequired = isRequired,
        AllowsCustomValue = false,
        AllowsMultipleValues = false,
        PayloadHash = "payload"
    };
}
