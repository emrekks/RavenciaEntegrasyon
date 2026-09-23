using MarketplaceHub.Api.Catalog;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class ProductFamilyMediaOrderingTests
{
    [Fact]
    public void Applies_the_same_repeated_moves_to_each_colour_gallery()
    {
        var gray = new List<string> { "gray-front", "gray-side", "gray-detail", "gray-back" };
        var green = new List<string> { "green-front", "green-side", "green-detail", "green-back" };
        var navy = new List<string> { "navy-front", "navy-side", "navy-detail", "navy-back" };

        foreach (var gallery in new[] { gray, green, navy })
        {
            ProductFamilyMediaOrdering.Move(gallery, 2, 0);
            ProductFamilyMediaOrdering.Move(gallery, 3, 1);
        }

        Assert.Equal(new[] { "gray-detail", "gray-back", "gray-front", "gray-side" }, gray);
        Assert.Equal(new[] { "green-detail", "green-back", "green-front", "green-side" }, green);
        Assert.Equal(new[] { "navy-detail", "navy-back", "navy-front", "navy-side" }, navy);
    }

    [Fact]
    public void Ignores_same_or_out_of_range_positions_without_mutating_gallery()
    {
        var gallery = new List<string> { "front", "side" };

        Assert.False(ProductFamilyMediaOrdering.Move(gallery, 1, 1));
        Assert.False(ProductFamilyMediaOrdering.Move(gallery, 2, 0));
        Assert.False(ProductFamilyMediaOrdering.Move(gallery, 0, 2));
        Assert.Equal(new[] { "front", "side" }, gallery);
    }
}
