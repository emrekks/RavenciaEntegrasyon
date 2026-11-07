using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

public sealed class PublicationVariantMediaTests
{
    private sealed record Item(Guid? VariantId, Guid Id);

    private static readonly Guid Old1 = Guid.NewGuid(), Old2 = Guid.NewGuid(), Added = Guid.NewGuid();
    private static readonly Guid A = Guid.NewGuid(), B = Guid.NewGuid(), C = Guid.NewGuid();

    private static List<Item> Pick(IReadOnlyCollection<Item> media, Guid variant) => PublicationVariantMedia.For(media, variant, x => x.VariantId, x => x.Id);

    [Fact]
    public void A_variant_keeps_its_own_pictures()
    {
        Item[] media = [new(Old1, A), new(Added, C), new(null, B)];
        Assert.Equal([C], Pick(media, Added).Select(x => x.Id));
    }

    [Fact]
    public void Without_its_own_pictures_a_variant_takes_the_card_gallery()
    {
        Item[] media = [new(Old1, A), new(null, B)];
        Assert.Equal([B], Pick(media, Added).Select(x => x.Id));
    }

    // MZ021Y-CD-01 on the Bordo card: every picture sits on the older sizes, none on the card; the new size takes theirs.
    [Fact]
    public void On_a_card_whose_pictures_all_sit_on_other_variants_a_new_variant_takes_theirs_once_each()
    {
        Item[] media = [new(Old1, A), new(Old1, B), new(Old2, A), new(Old2, B), new(Old2, C)];
        Assert.Equal([A, B, C], Pick(media, Added).Select(x => x.Id));
    }

    // Live 6.11.2026, RYP006 Vizon: -16 and -17 hold the four pictures in the strip's order (size chart last); a picture
    // assignment renumbered -18's own rows 0..3 with the size chart first. Pictures go in the panel's order, so all three
    // sizes send the same list, the strip's front picture first.
    [Fact]
    public void Sizes_with_the_same_pictures_follow_the_panel_strip_even_when_one_was_renumbered()
    {
        var (s16, s17, s18) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var (p1, p2, p3, chart) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var other = Guid.NewGuid();
        // Ordered by SortOrder, as the composers read them: -18's renumbered rows first (0..3), then 1380..1656.
        Item[] media =
        [
            new(s18, chart), new(s18, p1), new(s18, p2), new(s18, p3),
            new(null, other),
            new(null, p1), new(s16, p1), new(s17, p1),
            new(null, p2), new(s16, p2), new(s17, p2),
            new(null, p3), new(s16, p3), new(s17, p3),
            new(null, chart), new(s16, chart), new(s17, chart)
        ];

        foreach (var size in new[] { s16, s17, s18 }) Assert.Equal([p1, p2, p3, chart], Pick(media, size).Select(x => x.Id));
    }

    [Fact]
    public void A_picture_the_strip_lacks_follows_the_strip_pictures_in_the_variants_own_order()
    {
        var (only1, only2) = (Guid.NewGuid(), Guid.NewGuid());
        Item[] media = [new(Added, only2), new(Added, B), new(Added, only1), new(Added, A), new(null, A), new(null, B)];
        Assert.Equal([A, B, only2, only1], Pick(media, Added).Select(x => x.Id));
    }

    [Fact]
    public void A_card_without_pictures_gives_none()
    {
        Assert.Empty(Pick([], Added));
    }
}
