using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class ProductCatalogSortTests
{
    private static readonly DateTimeOffset Older = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Newer = new(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);

    private static readonly ProductCatalogSortKey Cheap = new(199m, Older, Guid.Parse("00000000-0000-0000-0000-000000000001"));
    private static readonly ProductCatalogSortKey MidOld = new(599m, Older, Guid.Parse("00000000-0000-0000-0000-000000000002"));
    private static readonly ProductCatalogSortKey MidNew = new(599m, Newer, Guid.Parse("00000000-0000-0000-0000-000000000003"));
    private static readonly ProductCatalogSortKey Dear = new(1299m, Older, Guid.Parse("00000000-0000-0000-0000-000000000004"));
    private static readonly ProductCatalogSortKey NoPrice = new(null, Newer, Guid.Parse("00000000-0000-0000-0000-000000000005"));

    private static readonly ProductCatalogSortKey[] Cards = [NoPrice, Dear, MidOld, Cheap, MidNew];

    [Theory]
    [InlineData("price-asc")]
    [InlineData(" PRICE-DESC ")]
    [InlineData("stock-asc")]
    [InlineData("stock-desc")]
    public void Parse_KnowsPriceAndStockInBothDirections(string value)
    {
        Assert.NotNull(ProductCatalogSort.Parse(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("default")]
    [InlineData("title-asc")]
    public void Parse_LeavesTheDefaultOrderForOtherValues(string? value)
    {
        Assert.Null(ProductCatalogSort.Parse(value));
    }

    [Fact]
    public void Ascending_PutsCheapestFirstAndUnknownPriceLast()
    {
        var sort = ProductCatalogSort.Parse("price-asc")!;
        Assert.True(sort.ByPrice);
        Assert.Equal([Cheap, MidNew, MidOld, Dear, NoPrice], Cards.OrderBy(card => card, sort));
    }

    [Fact]
    public void Descending_PutsDearestFirstAndUnknownPriceStillLast()
    {
        var sort = ProductCatalogSort.Parse("price-desc")!;
        Assert.Equal([Dear, MidNew, MidOld, Cheap, NoPrice], Cards.OrderBy(card => card, sort));
    }

    [Fact]
    public void EqualValues_KeepTheCatalogOrderNewestFirst()
    {
        var sort = ProductCatalogSort.Parse("stock-desc")!;
        Assert.False(sort.ByPrice);
        Assert.True(sort.Compare(MidNew, MidOld) < 0);
        Assert.Equal(0, sort.Compare(MidOld, MidOld));
    }

    [Fact]
    public void Cursor_CarriesTheLastCardAndOnlyOpensForTheSameSort()
    {
        var codec = new CursorCodec(new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider(), TimeProvider.System);
        var priced = codec.EncodeProductSort("price-asc", new ProductCatalogSortKey(599.90m, Newer, MidNew.Id));
        Assert.True(codec.TryDecodeProductSort(priced, "price-asc", out var key));
        Assert.Equal(new ProductCatalogSortKey(599.90m, Newer, MidNew.Id), key);
        Assert.False(codec.TryDecodeProductSort(priced, "price-desc", out _));
        Assert.True(codec.TryDecodeProductSort(codec.EncodeProductSort("stock-asc", NoPrice), "stock-asc", out var unknown));
        Assert.Null(unknown.Metric);
        Assert.False(codec.TryDecodeProductSort(codec.EncodeProduct(Newer, MidNew.Id), "price-asc", out _));
    }

    [Fact]
    public void PagesSplitByTheLastCardContinueWithoutGapsOrRepeats()
    {
        var sort = ProductCatalogSort.Parse("price-asc")!;
        var ordered = Cards.OrderBy(card => card, sort).ToList();
        var firstPage = ordered.Take(2).ToList();
        var secondPage = ordered.Where(card => sort.Compare(card, firstPage[^1]) > 0).Take(2).ToList();
        var thirdPage = ordered.Where(card => sort.Compare(card, secondPage[^1]) > 0).Take(2).ToList();
        Assert.Equal(ordered, firstPage.Concat(secondPage).Concat(thirdPage));
    }
}
