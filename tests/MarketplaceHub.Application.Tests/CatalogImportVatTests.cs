using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// ISLEYICI-2 (15 Nov 2026): a catalog import whose marketplace reports no VAT (Shopify never does) keeps the offer's VAT
// instead of writing 0; a new offer takes the card's VAT, 0 only when neither is known.
public sealed class CatalogImportVatTests
{
    [Fact]
    public void A_missing_remote_vat_keeps_the_offers_vat()
    {
        Assert.Equal((20m, false), MarketplaceJobProcessor.CatalogOfferVatRate(remote: null, offer: 20m, card: 10m));
    }

    [Fact]
    public void A_reported_remote_vat_wins()
    {
        Assert.Equal((10m, false), MarketplaceJobProcessor.CatalogOfferVatRate(remote: 10m, offer: 20m, card: 20m));
        Assert.Equal((0m, false), MarketplaceJobProcessor.CatalogOfferVatRate(remote: -1m, offer: 20m, card: null));
    }

    [Fact]
    public void A_new_offer_takes_the_cards_vat_and_is_unknown_only_without_one()
    {
        Assert.Equal((20m, false), MarketplaceJobProcessor.CatalogOfferVatRate(remote: null, offer: null, card: 20m));
        Assert.Equal((0m, true), MarketplaceJobProcessor.CatalogOfferVatRate(remote: null, offer: null, card: null));
    }
}
