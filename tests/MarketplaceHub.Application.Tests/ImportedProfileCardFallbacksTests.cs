using MarketplaceHub.Domain;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// The notices an update raises when the listing profile made from a marketplace match has no value of its own and the
// card's value goes instead (owner decision 6 Nov 2026: the card wins, never silently).
public sealed class ImportedProfileCardFallbacksTests
{
    private const string Imported = ChannelListingProfileSource.MarketplaceImport;
    private const string Label = "RYP003T (Kadın Haki Kazak)";

    [Fact]
    public void Trendyol_DeliveryFromTheCard_IsNamedWithItsValue()
    {
        var notice = Assert.Single(ImportedProfileCardFallbacks.Notices("TRENDYOL", "Trendyol", Label, Imported, null, null, 3, null, null));
        Assert.Equal("RYP003T (Kadın Haki Kazak) · Trendyol: pazaryerinden oluşturulan yayın profilinde Teslimat Süresi yok; ürün kartındaki 3 gün gönderildi.", notice);
        Assert.Single(ImportedProfileCardFallbacks.Notices("TRENDYOL", "Trendyol", Label, Imported, null, null, 3, null, [ProductUpdateFields.Delivery]));
        // Delivery not picked: nothing of it goes, nothing to say.
        Assert.Empty(ImportedProfileCardFallbacks.Notices("TRENDYOL", "Trendyol", Label, Imported, null, null, 3, null, [ProductUpdateFields.Title]));
    }

    [Fact]
    public void Hepsiburada_NamesDeliveryAndWarranty_WarrantyZeroWhenTheCardHasNone()
    {
        var notices = ImportedProfileCardFallbacks.Notices("HEPSIBURADA", "Hepsiburada", Label, Imported, null, null, 2, null, null);
        Assert.Equal(2, notices.Count);
        Assert.Contains(notices, notice => notice.EndsWith("Teslimat Süresi yok; ürün kartındaki 2 gün gönderildi.", StringComparison.Ordinal));
        Assert.Contains(notices, notice => notice.EndsWith("Garanti Süresi yok; ürün kartındaki 0 ay gönderildi.", StringComparison.Ordinal));
        Assert.Single(ImportedProfileCardFallbacks.Notices("HEPSIBURADA", "Hepsiburada", Label, Imported, null, null, 2, 24, [ProductUpdateFields.Warranty]), notice => notice.Contains("24 ay", StringComparison.Ordinal));
    }

    [Fact]
    public void EmptyCardDelivery_StopsTheUpdate_NamingProductMarketplaceAndField()
    {
        Assert.Equal("RYP003T – Trendyol: Teslimat Süresi boş; ürün kartında doldurun.", ImportedProfileCardFallbacks.EmptyCardField("TRENDYOL", "Trendyol", "RYP003T", Imported, null, null, null));
        Assert.Equal("RYP003T – PTT AVM: Teslimat Süresi boş; ürün kartında doldurun.", ImportedProfileCardFallbacks.EmptyCardField("PTTAVM", "PTT AVM", "RYP003T", Imported, null, 0, [ProductUpdateFields.Delivery]));
        // Not sent, held by the card or the profile, n11 (its own preparing day), or a panel profile: no stop.
        Assert.Null(ImportedProfileCardFallbacks.EmptyCardField("TRENDYOL", "Trendyol", "RYP003T", Imported, null, null, [ProductUpdateFields.Title]));
        Assert.Null(ImportedProfileCardFallbacks.EmptyCardField("TRENDYOL", "Trendyol", "RYP003T", Imported, null, 3, null));
        Assert.Null(ImportedProfileCardFallbacks.EmptyCardField("HEPSIBURADA", "Hepsiburada", "RYP003T", Imported, 2, null, null));
        Assert.Null(ImportedProfileCardFallbacks.EmptyCardField("N11", "n11", "RYP003T", Imported, null, null, null));
        Assert.Null(ImportedProfileCardFallbacks.EmptyCardField("TRENDYOL", "Trendyol", "RYP003T", null, null, null, null));
    }

    [Fact]
    public void PanelProfiles_ProfileValues_AndCardsWithoutAValue_RaiseNothing()
    {
        // A profile the panel made (null source) behaves as it always did.
        Assert.Empty(ImportedProfileCardFallbacks.Notices("TRENDYOL", "Trendyol", Label, null, null, null, 3, null, null));
        // The profile has its own value: the card's is not used.
        Assert.Empty(ImportedProfileCardFallbacks.Notices("TRENDYOL", "Trendyol", Label, Imported, 4, null, 3, null, null));
        // The card has none either: nothing is sent (the composer refuses a picked field with its own message).
        Assert.Empty(ImportedProfileCardFallbacks.Notices("TRENDYOL", "Trendyol", Label, Imported, null, null, null, null, null));
        // n11 and Pazarama never take these from the profile.
        Assert.Empty(ImportedProfileCardFallbacks.Notices("N11", "n11", Label, Imported, null, null, 3, null, null));
        Assert.Empty(ImportedProfileCardFallbacks.Notices("PAZARAMA", "Pazarama", Label, Imported, null, null, 3, null, null));
    }
}
