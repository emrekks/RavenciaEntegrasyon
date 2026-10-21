using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class ProductFamilyKeyPolicyTests
{
    private static readonly Guid BrandA = Guid.Parse("0190a000-0000-7000-8000-00000000000a");
    private static readonly Guid BrandB = Guid.Parse("0190a000-0000-7000-8000-00000000000b");

    [Theory]
    [InlineData("ABC-01", "abc 01")]
    [InlineData("ABC_01", " ABC-01 ")]
    [InlineData("kırmızı-1", "KIRMIZI 1")]
    public void SameModelCodeKeyOfOneBrandIsOneFamily(string left, string right)
    {
        Assert.True(ProductFamilyKeyPolicy.SameFamily(BrandA, left, BrandA, right));
        Assert.Equal(
            ProductFamilyKeyPolicy.FamilyKey(Guid.NewGuid(), BrandA, left),
            ProductFamilyKeyPolicy.FamilyKey(Guid.NewGuid(), BrandA, right));
    }

    [Fact]
    public void SameModelCodeOfTwoBrandsIsTwoFamilies()
    {
        Assert.False(ProductFamilyKeyPolicy.SameFamily(BrandA, "ABC-01", BrandB, "ABC-01"));
        Assert.False(ProductFamilyKeyPolicy.SameFamily(BrandA, "ABC-01", null, "ABC-01"));
        Assert.NotEqual(
            ProductFamilyKeyPolicy.FamilyKey(Guid.NewGuid(), BrandA, "ABC-01"),
            ProductFamilyKeyPolicy.FamilyKey(Guid.NewGuid(), BrandB, "ABC-01"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("--")]
    public void ProductWithoutModelCodeIsAFamilyOfItsOwn(string? modelCode)
    {
        var productId = Guid.NewGuid();
        Assert.Null(ProductFamilyKeyPolicy.ModelCodeKey(modelCode));
        Assert.Equal($"product:{productId:D}", ProductFamilyKeyPolicy.FamilyKey(productId, BrandA, modelCode));
        Assert.False(ProductFamilyKeyPolicy.SameFamily(BrandA, modelCode, BrandA, modelCode));
    }
}
