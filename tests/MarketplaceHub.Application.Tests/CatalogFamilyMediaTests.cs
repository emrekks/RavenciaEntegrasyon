using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class CatalogFamilyMediaTests
{
    private static readonly Guid Own = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid Bordo = Guid.Parse("00000000-0000-0000-0000-000000000002");
    private static readonly Guid Haki = Guid.Parse("00000000-0000-0000-0000-000000000003");
    private static readonly Guid BordoVariant = Guid.Parse("00000000-0000-0000-0000-0000000000b1");
    private static readonly Dictionary<Guid, string> Titles = new() { [Own] = "Bluz Siyah", [Bordo] = "Bluz Bordo", [Haki] = "Bluz Haki" };

    [Fact]
    public void Build_GivesEveryFamilyPictureItsMediaRows_VariantPicturesIncluded()
    {
        var ownMedia = Guid.NewGuid();
        var bordoProduct = Guid.NewGuid();
        var bordoVariantOnly = Guid.NewGuid();
        var bordoVariantSame = Guid.NewGuid();
        var hakiVariant = Guid.NewGuid();
        var (defaultUrls, items) = CatalogFamilyMedia.Build([
            new(ownMedia, Own, null, 0, "https://cdn.dsmcdn.com/own-1.jpg"),
            new(bordoProduct, Bordo, null, 1, "https://cdn.dsmcdn.com/bordo-1.jpg"),
            // The same picture on a bordo variant: one item, both rows.
            new(bordoVariantSame, Bordo, BordoVariant, 2, "https://cdn.dsmcdn.com/bordo-1.jpg?width=800"),
            // A variant-only picture of a colour that has product-level pictures: not in the default strip, but a custom
            // family order shows it, so it must be removable too.
            new(bordoVariantOnly, Bordo, BordoVariant, 3, "https://cdn.dsmcdn.com/bordo-variant.jpg"),
            // A colour with only variant pictures: its first one is in the default strip.
            new(hakiVariant, Haki, Guid.NewGuid(), 4, "https://cdn.dsmcdn.com/haki-variant.jpg"),
        ], Titles);

        Assert.Equal(["https://cdn.dsmcdn.com/own-1.jpg", "https://cdn.dsmcdn.com/bordo-1.jpg", "https://cdn.dsmcdn.com/haki-variant.jpg"], defaultUrls);
        Assert.Equal(["https://cdn.dsmcdn.com/own-1.jpg", "https://cdn.dsmcdn.com/bordo-1.jpg", "https://cdn.dsmcdn.com/haki-variant.jpg", "https://cdn.dsmcdn.com/bordo-variant.jpg"], items.Select(item => item.Url));
        Assert.Equal([bordoProduct, bordoVariantSame], items[1].MediaIds);
        Assert.Equal([bordoVariantOnly], items[3].MediaIds);
        Assert.Equal(["Bluz Bordo"], items[3].SourceProductTitles);
    }

    [Fact]
    public void Build_SkipsRowsWithoutAnAddress()
    {
        var (defaultUrls, items) = CatalogFamilyMedia.Build([new(Guid.NewGuid(), Own, null, 0, " "), new(Guid.NewGuid(), Own, null, 1, "https://cdn.dsmcdn.com/own-2.jpg")], Titles);
        Assert.Equal(["https://cdn.dsmcdn.com/own-2.jpg"], defaultUrls);
        Assert.Single(items);
    }

    // Live 6 Nov 2026: after a bulk variant picture assignment the strip regrouped as every colour's 1st picture, then
    // every 2nd: the assignment renumbered each variant's rows 0, 1… and the custom order read those numbers.
    [Fact]
    public void OrderedUrls_KeepsTheProductLevelOrder_WhateverTheVariantRowsAreNumbered()
    {
        var ordered = CatalogFamilyMedia.OrderedUrls([
            new(Guid.NewGuid(), Own, null, 0, "https://cdn.dsmcdn.com/siyah-1.jpg"),
            new(Guid.NewGuid(), Own, null, 3, "https://cdn.dsmcdn.com/siyah-2.jpg"),
            new(Guid.NewGuid(), Bordo, null, 6, "https://cdn.dsmcdn.com/bordo-1.jpg"),
            new(Guid.NewGuid(), Bordo, null, 9, "https://cdn.dsmcdn.com/bordo-2.jpg"),
            // The variant rows after the assignment: each variant's own 0, 1, 2.
            new(Guid.NewGuid(), Own, Guid.NewGuid(), 0, "https://cdn.dsmcdn.com/siyah-1.jpg"),
            new(Guid.NewGuid(), Own, Guid.NewGuid(), 1, "https://cdn.dsmcdn.com/siyah-2.jpg"),
            new(Guid.NewGuid(), Bordo, BordoVariant, 0, "https://cdn.dsmcdn.com/bordo-1.jpg"),
            new(Guid.NewGuid(), Bordo, BordoVariant, 1, "https://cdn.dsmcdn.com/bordo-2.jpg"),
            new(Guid.NewGuid(), Bordo, BordoVariant, 2, "https://cdn.dsmcdn.com/bordo-variant.jpg"),
        ]);

        Assert.Equal(["https://cdn.dsmcdn.com/siyah-1.jpg", "https://cdn.dsmcdn.com/siyah-2.jpg", "https://cdn.dsmcdn.com/bordo-1.jpg",
            "https://cdn.dsmcdn.com/bordo-2.jpg", "https://cdn.dsmcdn.com/bordo-variant.jpg"], ordered);
    }
}
