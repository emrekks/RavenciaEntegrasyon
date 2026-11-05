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

    [Fact]
    public void Applies_a_full_family_order_and_keeps_unmentioned_new_images_at_the_end()
    {
        var images = new[] { (Key: "gray-front", Rank: 0), (Key: "green-front", Rank: 1), (Key: "navy-front", Rank: 2), (Key: "new-upload", Rank: 3) };

        var ordered = ProductFamilyMediaOrdering.OrderByRequestedKeys(images, ["navy-front", "gray-front", "green-front"], image => image.Key);

        Assert.Equal(new[] { "navy-front", "gray-front", "green-front", "new-upload" }, ordered.Select(image => image.Key));
    }

    [Fact]
    public void Family_order_keys_are_case_insensitive_and_repeated_keys_are_applied_once()
    {
        var images = new[] { (Key: "Gray", Rank: 0), (Key: "Green", Rank: 1), (Key: "Navy", Rank: 2) };

        var ordered = ProductFamilyMediaOrdering.OrderByRequestedKeys(images, ["NAVY", "gray", "navy"], image => image.Key);

        Assert.Equal(new[] { "Navy", "Gray", "Green" }, ordered.Select(image => image.Key));
    }

    [Fact]
    public void A_new_upload_never_takes_the_place_of_another_live_picture()
    {
        // After a family reorder the card's gallery sits at sparse ranks (rank × stride): the editor asks for position 2
        // (it knows two own pictures), which here holds a grey picture put on this card. Brown must not replace it, and
        // grey added back later must not replace brown.
        var brown = Guid.NewGuid(); var grey = Guid.NewGuid(); var front = Guid.NewGuid(); var back = Guid.NewGuid();
        var rows = new List<(int SortOrder, bool Active, Guid AssetId)> { (0, true, front), (2, true, grey), (4, true, back) };

        var brownSlot = ProductFamilyMediaOrdering.UploadSortOrder(2, rows, brown);
        Assert.Equal(5, brownSlot);
        rows.Add((brownSlot, true, brown));
        Assert.Equal(6, ProductFamilyMediaOrdering.UploadSortOrder(2, rows, Guid.NewGuid()));
    }

    [Fact]
    public void A_new_upload_takes_a_free_archived_or_its_own_position()
    {
        var picture = Guid.NewGuid();
        var rows = new List<(int SortOrder, bool Active, Guid AssetId)> { (0, true, Guid.NewGuid()), (1, false, Guid.NewGuid()), (2, true, picture) };

        Assert.Equal(1, ProductFamilyMediaOrdering.UploadSortOrder(1, rows, picture));
        Assert.Equal(2, ProductFamilyMediaOrdering.UploadSortOrder(2, rows, picture));
        Assert.Equal(7, ProductFamilyMediaOrdering.UploadSortOrder(7, rows, Guid.NewGuid()));
        Assert.Equal(0, ProductFamilyMediaOrdering.UploadSortOrder(0, [], picture));
    }
}
