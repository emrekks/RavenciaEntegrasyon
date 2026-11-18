using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

// Owner rule of 18 Nov 2026: a linked variant without an offer goes at the panel price; Özel Fiyat keeps its own. The
// connection's preview (read only) and the owner's "Eksik Teklifleri Aç" (PanelPriceOffers.OpenAsync, nothing is sent).
public sealed partial class InventoryService
{
    private const int MaxPanelPriceDifferences = 50;

    public async Task<ServiceResult<PanelPriceOfferPreview>> PanelPriceOfferPreviewAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken)
    {
        var connection = await db.PlatformConnections.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.Id == connectionId && x.Status != "DELETED")
            .Select(x => new { x.Id, x.PlatformCode })
            .SingleOrDefaultAsync(cancellationToken);
        if (connection is null) return NotFound<PanelPriceOfferPreview>();
        var opensOffers = PanelPriceOfferPolicy.OpensOffers(connection.PlatformCode);
        var counts = PanelPriceOffers.Count(connection.PlatformCode, await PanelPriceOffers.CandidatesAsync(db, tenantId, connectionId, cancellationToken));
        var offers = await (from offer in db.ChannelOffers.AsNoTracking()
                            join variant in db.ProductVariants.AsNoTracking() on new { offer.TenantId, offer.VariantId } equals new { variant.TenantId, VariantId = variant.Id }
                            join card in db.Products.AsNoTracking() on new { variant.TenantId, variant.ProductId } equals new { card.TenantId, ProductId = card.Id }
                            where offer.TenantId == tenantId && offer.ConnectionId == connectionId && offer.Status == "ACTIVE"
                            orderby variant.Sku, variant.Id
                            select new { offer.VariantId, variant.Sku, offer.SalePrice, offer.ListPrice, offer.FollowsPanelPrice, VariantSale = variant.DefaultSalePrice, CardSale = card.DefaultSalePrice, VariantList = variant.DefaultListPrice, CardList = card.DefaultListPrice })
            .ToListAsync(cancellationToken);
        int following = 0, atOtherPrice = 0, withoutPanelPrice = 0, custom = 0;
        var differences = new List<PanelPriceOfferDifference>();
        var takesList = PanelPriceOfferPolicy.TakesListPrice(connection.PlatformCode);
        foreach (var offer in offers)
        {
            if (offer.FollowsPanelPrice == false) { custom++; continue; }
            if (PanelPriceOfferPolicy.PanelPrices(connection.PlatformCode, offer.VariantSale, offer.CardSale, offer.VariantList, offer.CardList) is not { } panel) { withoutPanelPrice++; continue; }
            following++;
            if (offer.SalePrice == panel.SalePrice && (!takesList || offer.ListPrice == panel.ListPrice)) continue;
            atOtherPrice++;
            if (differences.Count < MaxPanelPriceDifferences)
                differences.Add(new(offer.VariantId, offer.Sku, offer.SalePrice, offer.ListPrice, panel.SalePrice, panel.ListPrice, offer.FollowsPanelPrice));
        }
        var open = await AutomaticPriceStockWrites.IsOpenAsync(db, tenantId, connectionId, cancellationToken);
        return ServiceResult<PanelPriceOfferPreview>.Ok(new(connectionId, connection.PlatformCode, opensOffers, open,
            counts.LinkedWithoutOffer, opensOffers ? counts.Openable : 0, counts.MissingPanelPrice, counts.MissingVat,
            following, atOtherPrice, withoutPanelPrice, custom, differences));
    }

    public async Task<ServiceResult<PanelPriceOffersOpened>> OpenPanelPriceOffersAsync(Guid tenantId, Guid userId, Guid connectionId, CancellationToken cancellationToken)
    {
        var connection = await db.PlatformConnections.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.Id == connectionId && x.Status != "DELETED")
            .Select(x => new { x.Id, x.PlatformCode })
            .SingleOrDefaultAsync(cancellationToken);
        if (connection is null) return NotFound<PanelPriceOffersOpened>();
        if (!PanelPriceOfferPolicy.OpensOffers(connection.PlatformCode))
            return ServiceResult<PanelPriceOffersOpened>.Fail("PANEL_PRICE_OFFERS_UNSUPPORTED", "Bu bağlantı bir pazaryeri mağazası değil; panel fiyatıyla teklif açılmaz.", 422);
        for (var attempt = 1; ; attempt++)
        {
            var counts = await PanelPriceOffers.OpenAsync(db, tenantId, connectionId, connection.PlatformCode, userId, timeProvider.GetUtcNow(), cancellationToken);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return ServiceResult<PanelPriceOffersOpened>.Ok(new(connectionId, counts.Openable, counts.MissingPanelPrice, counts.MissingVat));
            }
            catch (DbUpdateException conflict) when (attempt < 3 && PanelPriceOffers.IsOfferUniqueViolation(conflict))
            {
                // Another request opened some of them meanwhile: the next attempt opens only those still missing.
                db.ChangeTracker.Clear();
            }
        }
    }

    // Owner rule of 18 Nov 2026: the variants without a saved panel price and the price each would take from the marketplace
    // it was first read from (its oldest offer: offer ids are time ordered). Read only; the fill itself is a later step
    // the owner confirms, and it never sends anything to a marketplace.
    public async Task<PanelPriceFillPreview> PanelPriceFillPreviewAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var variants = await (from variant in db.ProductVariants.AsNoTracking()
                              join card in db.Products.AsNoTracking() on new { variant.TenantId, variant.ProductId } equals new { card.TenantId, ProductId = card.Id }
                              where variant.TenantId == tenantId && !(variant.DefaultSalePrice > 0) && !(card.DefaultSalePrice > 0)
                              select new { variant.Id, variant.Sku }).ToListAsync(cancellationToken);
        var ids = variants.Select(x => x.Id).ToArray();
        var offers = new List<(Guid OfferId, Guid VariantId, string PlatformCode, decimal ListPrice, decimal SalePrice)>();
        foreach (var chunk in ids.Chunk(2_000))
            offers.AddRange((await (from offer in db.ChannelOffers.AsNoTracking()
                                    join connection in db.PlatformConnections.AsNoTracking() on new { offer.TenantId, Id = offer.ConnectionId } equals new { connection.TenantId, connection.Id }
                                    where offer.TenantId == tenantId && chunk.Contains(offer.VariantId)
                                    select new { offer.Id, offer.VariantId, connection.PlatformCode, offer.ListPrice, offer.SalePrice }).ToListAsync(cancellationToken))
                .Select(x => (x.Id, x.VariantId, x.PlatformCode, x.ListPrice, x.SalePrice)));
        var first = offers.GroupBy(x => x.VariantId).ToDictionary(group => group.Key, group => group.OrderBy(x => x.OfferId).First());
        int fillable = 0, zero = 0, withoutOffer = 0;
        var bySource = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var sample = new List<PanelPriceFillRow>();
        foreach (var variant in variants.OrderBy(x => x.Sku, StringComparer.Ordinal))
        {
            if (!first.TryGetValue(variant.Id, out var offer)) { withoutOffer++; continue; }
            if (PanelPriceOfferPolicy.ImportedPanelPrices(offer.SalePrice, offer.ListPrice) is not { } price) { zero++; continue; }
            fillable++;
            bySource[offer.PlatformCode] = bySource.GetValueOrDefault(offer.PlatformCode) + 1;
            if (sample.Count < MaxPanelPriceDifferences) sample.Add(new(variant.Id, variant.Sku, offer.PlatformCode, price.ListPrice, price.SalePrice));
        }
        return new(variants.Count, fillable, zero, withoutOffer, bySource, sample);
    }
}
