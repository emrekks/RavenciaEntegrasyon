using MarketplaceHub.Application;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class CategoryReusePolicyTests
{
    private static HashSet<string> Keys(params string[] keys) => new(keys, StringComparer.Ordinal);

    [Fact]
    public void CreatesNewCategoryWhenPanelHasNoSameNamedCategory()
    {
        var result = CategoryReusePolicy.Decide("Alt - Üst Takım", "Hepsiburada", Keys("RENK", "BEDEN"), []);

        Assert.Equal(CategoryReuseDecision.CreateNew, result.Decision);
        Assert.Equal("Alt - Üst Takım", result.PanelName);
        Assert.Null(result.CategoryId);
    }

    [Fact]
    public void ReusesTheSameNamedCategoryWhenAttributeSetsAreIdentical()
    {
        var id = Guid.NewGuid();
        var result = CategoryReusePolicy.Decide("Bluz", "Hepsiburada", Keys("RENK", "BEDEN"), [new(id, "Bluz", Keys("BEDEN", "RENK"))]);

        Assert.Equal(CategoryReuseDecision.ReuseExisting, result.Decision);
        Assert.Equal(id, result.CategoryId);
        Assert.Equal("Bluz", result.PanelName);
    }

    [Fact]
    public void KeepsCategoriesSeparateWhenAttributeSetsDiffer()
    {
        var result = CategoryReusePolicy.Decide("Bluz", "Hepsiburada", Keys("RENK", "BEDEN", "KUMAS-TIPI"), [new(Guid.NewGuid(), "Bluz", Keys("RENK", "BEDEN"))]);

        Assert.Equal(CategoryReuseDecision.CreateSeparate, result.Decision);
        Assert.Null(result.CategoryId);
        Assert.Equal("Bluz (Hepsiburada)", result.PanelName);
    }

    [Fact]
    public void SendsToReviewWhenRemoteAttributesAreUnknown()
    {
        var result = CategoryReusePolicy.Decide("Bluz", "Hepsiburada", Keys(), [new(Guid.NewGuid(), "Bluz", Keys("RENK"))]);

        Assert.Equal(CategoryReuseDecision.NeedsReview, result.Decision);
    }

    [Fact]
    public void SendsToReviewWhenSeveralPanelCategoriesMatchExactly()
    {
        var result = CategoryReusePolicy.Decide("Bluz", "Hepsiburada", Keys("RENK"), [new(Guid.NewGuid(), "Bluz", Keys("RENK")), new(Guid.NewGuid(), "Bluz (Trendyol)", Keys("RENK"))]);

        Assert.Equal(CategoryReuseDecision.NeedsReview, result.Decision);
        Assert.Null(result.CategoryId);
    }

    [Fact]
    public void TreatsRepeatedEntriesOfOneCategoryAsASingleMatch()
    {
        var id = Guid.NewGuid();
        var result = CategoryReusePolicy.Decide("Bluz", "Hepsiburada", Keys("RENK"), [new(id, "Bluz", Keys("RENK")), new(id, "Bluz", Keys("RENK", "BEDEN"))]);

        Assert.Equal(CategoryReuseDecision.ReuseExisting, result.Decision);
        Assert.Equal(id, result.CategoryId);
    }

    [Fact]
    public void PicksTheMatchingCandidateAmongSeveral()
    {
        var match = Guid.NewGuid();
        var result = CategoryReusePolicy.Decide("Bluz", "Hepsiburada", Keys("RENK", "KUMAS-TIPI"), [new(Guid.NewGuid(), "Bluz", Keys("RENK")), new(match, "Bluz (Trendyol)", Keys("KUMAS-TIPI", "RENK"))]);

        Assert.Equal(CategoryReuseDecision.ReuseExisting, result.Decision);
        Assert.Equal(match, result.CategoryId);
    }
}
