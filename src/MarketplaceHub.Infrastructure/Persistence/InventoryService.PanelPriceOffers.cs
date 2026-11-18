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
                            select new { offer.VariantId, variant.Sku, offer.SalePrice, offer.ListPrice, offer.FollowsPanelPrice, offer.LastSentSalePrice, VariantSale = variant.DefaultSalePrice, CardSale = card.DefaultSalePrice, VariantList = variant.DefaultListPrice, CardList = card.DefaultListPrice })
            .ToListAsync(cancellationToken);
        int following = 0, atOtherPrice = 0, withoutPanelPrice = 0, custom = 0;
        var differences = new List<PanelPriceOfferDifference>();
        var takesList = PanelPriceOfferPolicy.TakesListPrice(connection.PlatformCode);
        // Review of 18 Nov 2026: the offers whose price in force differs from what the marketplace last confirmed (or, with
        // none confirmed, from the stored price): the price each of them carries with its first stock send after the writes open.
        var priceGoesFirst = 0;
        foreach (var offer in offers)
            if (PanelPriceOfferPolicy.InForce(connection.PlatformCode, offer.FollowsPanelPrice, offer.ListPrice, offer.SalePrice, offer.VariantSale, offer.CardSale, offer.VariantList, offer.CardList) is { } inForce
                && inForce.SalePrice != (offer.LastSentSalePrice ?? offer.SalePrice))
                priceGoesFirst++;
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
            following, atOtherPrice, withoutPanelPrice, custom, differences, priceGoesFirst));
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
    public async Task<PanelPriceFillPreview> PanelPriceFillPreviewAsync(Guid tenantId, CancellationToken cancellationToken) =>
        (await PanelPriceFillPlanAsync(tenantId, cancellationToken)).Preview;

    private sealed record PanelPriceFillPlan(PanelPriceFillPreview Preview, IReadOnlyDictionary<Guid, (decimal ListPrice, decimal SalePrice)> Prices);

    private async Task<PanelPriceFillPlan> PanelPriceFillPlanAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var variants = await (from variant in db.ProductVariants.AsNoTracking()
                              join card in db.Products.AsNoTracking() on new { variant.TenantId, variant.ProductId } equals new { card.TenantId, ProductId = card.Id }
                              where variant.TenantId == tenantId && !(variant.DefaultSalePrice > 0) && !(card.DefaultSalePrice > 0)
                              select new { variant.Id, variant.Sku }).ToListAsync(cancellationToken);
        var ids = variants.Select(x => x.Id).ToArray();
        var offers = new List<(Guid OfferId, Guid VariantId, string PlatformCode, decimal ListPrice, decimal SalePrice, bool? Follows, string Status)>();
        foreach (var chunk in ids.Chunk(2_000))
            offers.AddRange((await (from offer in db.ChannelOffers.AsNoTracking()
                                    join connection in db.PlatformConnections.AsNoTracking() on new { offer.TenantId, Id = offer.ConnectionId } equals new { connection.TenantId, connection.Id }
                                    where offer.TenantId == tenantId && chunk.Contains(offer.VariantId)
                                    select new { offer.Id, offer.VariantId, connection.PlatformCode, offer.ListPrice, offer.SalePrice, offer.FollowsPanelPrice, offer.Status }).ToListAsync(cancellationToken))
                .Select(x => (x.Id, x.VariantId, x.PlatformCode, x.ListPrice, x.SalePrice, x.FollowsPanelPrice, x.Status)));
        var first = offers.GroupBy(x => x.VariantId).ToDictionary(group => group.Key, group => group.OrderBy(x => x.OfferId).First());
        int fillable = 0, zero = 0, withoutOffer = 0;
        var bySource = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var sample = new List<PanelPriceFillRow>();
        var prices = new Dictionary<Guid, (decimal ListPrice, decimal SalePrice)>();
        foreach (var variant in variants.OrderBy(x => x.Sku, StringComparer.Ordinal))
        {
            if (!first.TryGetValue(variant.Id, out var offer)) { withoutOffer++; continue; }
            if (PanelPriceOfferPolicy.ImportedPanelPrices(offer.SalePrice, offer.ListPrice) is not { } price) { zero++; continue; }
            fillable++;
            prices[variant.Id] = price;
            bySource[offer.PlatformCode] = bySource.GetValueOrDefault(offer.PlatformCode) + 1;
            if (sample.Count < MaxPanelPriceDifferences) sample.Add(new(variant.Id, variant.Sku, offer.PlatformCode, price.ListPrice, price.SalePrice));
        }
        // The offers the fill pulls to the new panel price (Özel Fiyat off, active), per marketplace.
        var changes = offers.Where(x => x.Status == "ACTIVE" && x.Follows != false && prices.ContainsKey(x.VariantId))
            .Select(x => (x.PlatformCode, Panel: PanelPriceOfferPolicy.PanelPrices(x.PlatformCode, prices[x.VariantId].SalePrice, null, prices[x.VariantId].ListPrice, null), x.ListPrice, x.SalePrice))
            .Where(x => x.Panel is { } panel && (panel.SalePrice != x.SalePrice || panel.ListPrice != x.ListPrice))
            .GroupBy(x => x.PlatformCode)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group =>
            {
                var differences = group.Select(x => Math.Abs(x.Panel!.Value.SalePrice - x.SalePrice)).Order().ToList();
                var median = differences.Count % 2 == 1 ? differences[differences.Count / 2] : (differences[differences.Count / 2 - 1] + differences[differences.Count / 2]) / 2;
                return new PanelPriceFillOfferChange(group.Key, differences.Count, differences.Sum(), median, differences.Max());
            }).ToList();
        return new(new(variants.Count, fillable, zero, withoutOffer, bySource, sample, changes), prices);
    }

    // Owner decision of 18 Nov 2026: "Panel Fiyatlarını İlk Pazaryeri Fiyatından Doldur". Writes the previewed panel price
    // to every variant whose panel price (its own and its card's) is still empty, in one transaction; a variant that has
    // one is never touched. The offers with Özel Fiyat off take the new panel price as their stored price (one rule: the
    // price on screen is the price that goes), their send marks moving with them. Nothing goes to a marketplace and no job
    // is queued: the price reaches a marketplace only with the variant's next stock or price send. The previewed count must still hold, else nothing is written; a second run finds nothing to fill.
    public Task<ServiceResult<PanelPriceFillApplied>> ApplyPanelPriceFillAsync(Guid tenantId, Guid userId, PanelPriceFillCommand command, string correlationId, CancellationToken cancellationToken) =>
        // A worker saving an offer meanwhile breaks the serializable fill (40001): it is run again a few times, the
        // previewed count checked again on every attempt.
        SerializableWriteRetry.RunAsync(db, token => ApplyPanelPriceFillOnceAsync(tenantId, userId, command, correlationId, token),
            () => ServiceResult<PanelPriceFillApplied>.Fail("PANEL_PRICE_FILL_BUSY", "Panel fiyatları bu sırada başka bir işlemle değişti; hiçbir şey yazılmadı. Birazdan tekrar deneyin.", 409),
            cancellationToken);

    private async Task<ServiceResult<PanelPriceFillApplied>> ApplyPanelPriceFillOnceAsync(Guid tenantId, Guid userId, PanelPriceFillCommand command, string correlationId, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken);
        var plan = await PanelPriceFillPlanAsync(tenantId, cancellationToken);
        if (plan.Preview.Fillable != command.ExpectedCount)
            return ServiceResult<PanelPriceFillApplied>.Fail("PANEL_PRICE_FILL_CHANGED", $"Önizlemeden sonra sayı değişti: şimdi {plan.Preview.Fillable} beden doldurulacaktı, önizlemede {command.ExpectedCount}. Hiçbir şey yazılmadı; önizlemeyi yenileyip tekrar deneyin.", 409);
        if (plan.Prices.Count == 0) return ServiceResult<PanelPriceFillApplied>.Ok(new(0, 0));
        var now = timeProvider.GetUtcNow();
        var filled = 0;
        foreach (var chunk in plan.Prices.Keys.Chunk(2_000))
        {
            // Tagged so a test can change a panel price between the plan and this read (the skip below is the second line).
            var variants = await db.ProductVariants.TagWith(PanelPriceFillLoadTag).Where(x => x.TenantId == tenantId && chunk.Contains(x.Id)).ToListAsync(cancellationToken);
            foreach (var variant in variants)
            {
                if (variant.DefaultSalePrice is > 0) continue;
                var (list, sale) = plan.Prices[variant.Id];
                if (ProductChangeLog.DefaultPrice(tenantId, variant.ProductId, variant.Id, (variant.DefaultListPrice, variant.DefaultSalePrice), (list, sale), ProductChangeSources.BulkEdit, userId, correlationId, now, PanelPriceFillNote) is { } entry)
                    db.ProductChangeEntries.Add(entry);
                variant.DefaultListPrice = list;
                variant.DefaultSalePrice = sale;
                variant.UpdatedAt = now;
                variant.Version++;
                filled++;
            }
        }
        // One rule (owner, 18 Nov 2026): the offers that do not keep their own price (Özel Fiyat off) take the new panel price,
        // with their price history; their send marks move with them, so nothing is queued or sent now.
        await db.SaveChangesAsync(cancellationToken);
        var repriced = await PanelPriceOffers.RepriceFollowersAsync(db, tenantId,
            plan.Prices.ToDictionary(x => x.Key, x => new PanelPriceOffers.PanelPriceChange(x.Value.SalePrice, null, x.Value.ListPrice, null, false)),
            new HashSet<Guid>(), PanelPriceFillNote, $"USER:{userId:D}", now, cancellationToken, keepSendMarks: true);
        db.AuditLogs.Add(new AuditLog
        {
            TenantId = tenantId,
            ActorUserId = userId,
            Action = "PANEL_PRICES_FILLED_FROM_FIRST_MARKETPLACE",
            TargetType = "ProductVariant",
            TargetId = null,
            Reason = $"Panel fiyatı ilk pazaryeri fiyatından dolduruldu: {filled} beden ({string.Join(", ", plan.Preview.FillableBySource.Select(x => $"{x.Key} {x.Value}"))}); 0 TL olduğu için boş kalan {plan.Preview.ZeroPrice}, teklifi olmayan {plan.Preview.WithoutOffer}; panel fiyatına çekilen teklif {repriced.Count}. Pazaryerine bir şey gönderilmedi.",
            CorrelationId = correlationId,
            CreatedAt = now
        });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ServiceResult<PanelPriceFillApplied>.Ok(new(filled, repriced.Count));
    }

    internal const string PanelPriceFillLoadTag = "panel-price-fill-load";
    private const string PanelPriceFillNote = "İlk pazaryeri fiyatından dolduruldu (pazaryerine gönderilmedi).";
}
