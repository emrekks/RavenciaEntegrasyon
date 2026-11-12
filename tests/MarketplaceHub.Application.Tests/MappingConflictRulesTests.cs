using Xunit;

namespace MarketplaceHub.Application.Tests;

// "Gözden Geçirilecek Eşlemeler" (9.1.14): Hepsiburada's deliberate several values per panel value are not conflicts.
public sealed class MappingConflictRulesTests
{
    [Fact]
    public void SeveralValuesInOneField_AreAConflictExceptOnHepsiburada()
    {
        Assert.True(MappingConflictRules.IsSameCategoryConflict("TRENDYOL", ["1", "2"]));
        Assert.True(MappingConflictRules.IsSameCategoryConflict("N11", ["1", "2"]));
        Assert.False(MappingConflictRules.IsSameCategoryConflict("HEPSIBURADA", ["1", "2"]));
        Assert.False(MappingConflictRules.IsSameCategoryConflict(" hepsiburada ", ["1", "2"]));
        Assert.False(MappingConflictRules.IsSameCategoryConflict("TRENDYOL", ["1", "1"]));
    }

    [Fact]
    public void DifferentValuesAcrossCategories_OnHepsiburadaOnlyWhenTheSetsDiffer()
    {
        (string, string)[] sameSets = [("A", "red"), ("A", "bordo"), ("B", "bordo"), ("B", "red")];
        (string, string)[] differentSets = [("A", "red"), ("A", "bordo"), ("B", "red")];
        (string, string)[] oneCategory = [("A", "red"), ("A", "bordo")];

        Assert.False(MappingConflictRules.IsCrossCategoryConflict("HEPSIBURADA", sameSets));
        Assert.True(MappingConflictRules.IsCrossCategoryConflict("HEPSIBURADA", differentSets));
        Assert.False(MappingConflictRules.IsCrossCategoryConflict("HEPSIBURADA", oneCategory));
        Assert.True(MappingConflictRules.IsCrossCategoryConflict("TRENDYOL", sameSets));
        Assert.True(MappingConflictRules.IsCrossCategoryConflict("TRENDYOL", [("A", "red"), ("B", "bordo")]));
        Assert.False(MappingConflictRules.IsCrossCategoryConflict("TRENDYOL", [("A", "red"), ("B", "red")]));
        Assert.False(MappingConflictRules.IsCrossCategoryConflict("TRENDYOL", oneCategory));
    }
}
