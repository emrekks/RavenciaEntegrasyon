using MarketplaceHub.Domain;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class ProductDraftPolicyTests
{
    [Theory]
    [InlineData("DRAFT", true)]
    [InlineData(" draft ", true)]
    [InlineData("ACTIVE", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void OnlyAnExplicitDraftAsksForTheRelaxedSave(string? status, bool expected) =>
        Assert.Equal(expected, ProductDraftPolicy.IsDraftRequest(status));

    [Theory]
    [InlineData(ProductStatus.Draft, true)]
    [InlineData(ProductStatus.Active, false)]
    [InlineData(ProductStatus.Archived, false)]
    public void OnlyADraftStaysADraft(ProductStatus current, bool expected) =>
        Assert.Equal(expected, ProductDraftPolicy.MayBecomeDraft(current));
}
