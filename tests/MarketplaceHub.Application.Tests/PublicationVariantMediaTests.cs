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

    [Fact]
    public void A_card_without_pictures_gives_none()
    {
        Assert.Empty(Pick([], Added));
    }
}
