using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

// Owner rule (18 Nov 2026): a variant linked to a marketplace listing without an offer there is sent at the panel price
// and the panel stock; an offer whose "Özel Fiyat" is on (FollowsPanelPrice false) keeps its own price. The missing offer
// is opened as a real record that follows the panel price (FollowsPanelPrice true), so the send, the send marks, the send
// log, the price history and the editor's price matrix all take their usual path.
// An offer is opened only when the owner acts: when the automatic price/stock writes of a connection are switched on
// (AutomaticPriceStockWrites.SaveWithBaselineAsync) and when the owner opens them for a connection from Entegrasyonlar.
// A deploy, the worker, a scan or a migration never opens one. Every opened offer counts as sent at its values (decision
// B), so opening it sends nothing; only a later change of its stock or panel price goes.
// Unknown stays unknown: a variant without a panel sale price above zero, with a list price below it, or without a VAT
// rate gets no offer and is counted for the owner.
internal static class PanelPriceOffers
{
    public const string Reason = "Panel fiyatıyla açıldı (teklif kaydı yoktu)";
    public const string AuditAction = "CHANNEL_OFFERS_OPENED_AT_PANEL_PRICE";

    public sealed record Candidate(Guid VariantId, string Sku, Guid ProductId, decimal? VariantSalePrice, decimal? ProductSalePrice, decimal? VariantListPrice, decimal? ProductListPrice, decimal? VatRate, string? VatInclusion);

    public sealed record Counts(int LinkedWithoutOffer, int Openable, int MissingPanelPrice, int MissingVat);

    // The variants of a connection linked to a live listing code but without an offer there, active and on an active card.
    public static async Task<List<Candidate>> CandidatesAsync(AppDbContext db, Guid tenantId, Guid connectionId, CancellationToken cancellationToken, IReadOnlyCollection<Guid>? variantIds = null)
    {
        var scope = variantIds?.Distinct().ToArray();
        var query = from link in db.MarketplaceVariantLinks.AsNoTracking()
                    join variant in db.ProductVariants.AsNoTracking() on new { link.TenantId, link.VariantId } equals new { variant.TenantId, VariantId = variant.Id }
                    join card in db.Products.AsNoTracking() on new { variant.TenantId, variant.ProductId } equals new { card.TenantId, ProductId = card.Id }
                    join model in db.ProductModels.AsNoTracking() on new { card.TenantId, Id = card.FamilyId } equals new { model.TenantId, Id = (Guid?)model.Id } into models
                    from model in models.DefaultIfEmpty()
                    where link.TenantId == tenantId && link.ConnectionId == connectionId && link.ExternalId != ""
                        && variant.Status == ProductStatus.Active && card.Status == ProductStatus.Active && card.ArchivedAt == null
                        && !db.ChannelOffers.Any(offer => offer.TenantId == tenantId && offer.ConnectionId == connectionId && offer.VariantId == link.VariantId)
                        && (scope == null || scope.Contains(link.VariantId))
                    orderby variant.Id
                    select new Candidate(variant.Id, variant.Sku, card.Id, variant.DefaultSalePrice, card.DefaultSalePrice, variant.DefaultListPrice, card.DefaultListPrice,
                        model == null ? card.VatRate : model.VatRate, model == null ? card.VatInclusion : model.VatInclusion);
        return await query.ToListAsync(cancellationToken);
    }

    public static Counts Count(string platformCode, IReadOnlyCollection<Candidate> candidates)
    {
        int openable = 0, missingPrice = 0, missingVat = 0;
        foreach (var candidate in candidates)
        {
            if (PanelPriceOfferPolicy.PanelPrices(platformCode, candidate.VariantSalePrice, candidate.ProductSalePrice, candidate.VariantListPrice, candidate.ProductListPrice) is null) missingPrice++;
            else if (candidate.VatRate is null) missingVat++;
            else openable++;
        }
        return new(candidates.Count, openable, missingPrice, missingVat);
    }

    // Adds the missing offers of a connection to the caller's unit of work, each counting as sent at its values (B): its
    // price mark is the one the platform's build computes and its stock mark the variant's current stock version. Nothing
    // is queued. Every marketplace opens them (owner, 18 Nov 2026: the price in force goes with every stock send anyway).
    public static async Task<Counts> OpenAsync(AppDbContext db, Guid tenantId, Guid connectionId, string platformCode, Guid? actorUserId, DateTimeOffset now, CancellationToken cancellationToken, IReadOnlyCollection<Guid>? variantIds = null)
    {
        if (!PanelPriceOfferPolicy.OpensOffers(platformCode)) return new(0, 0, 0, 0);
        var candidates = await CandidatesAsync(db, tenantId, connectionId, cancellationToken, variantIds);
        // An offer this unit of work already adds (a retry, another path of the same request) is not added twice.
        var adding = db.ChannelOffers.Local.Where(offer => offer.TenantId == tenantId && offer.ConnectionId == connectionId).Select(offer => offer.VariantId).ToHashSet();
        candidates.RemoveAll(candidate => adding.Contains(candidate.VariantId));
        var counts = Count(platformCode, candidates);
        if (counts.Openable == 0) return counts;
        var safetyStock = await db.ConnectionInventoryPolicies.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId)
            .Select(x => (decimal?)x.DefaultSafetyStock)
            .FirstOrDefaultAsync(cancellationToken) ?? 0m;
        var openVariantIds = candidates.Select(candidate => candidate.VariantId).ToArray();
        var stockVersions = await db.InventoryItems.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.LocationCode == "MAIN" && openVariantIds.Contains(x.VariantId))
            .Select(x => new { x.VariantId, x.ProjectionVersion })
            .ToDictionaryAsync(x => x.VariantId, x => x.ProjectionVersion, cancellationToken);
        var opened = 0;
        foreach (var candidate in candidates)
        {
            if (PanelPriceOfferPolicy.PanelPrices(platformCode, candidate.VariantSalePrice, candidate.ProductSalePrice, candidate.VariantListPrice, candidate.ProductListPrice) is not { } prices || candidate.VatRate is not { } vatRate) continue;
            var vatInclusion = ChannelOfferPricePolicy.IsKnownVatInclusion(candidate.VatInclusion ?? "") ? candidate.VatInclusion!.Trim().ToUpperInvariant() : "INCLUDED";
            var offer = new ChannelOffer
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                ConnectionId = connectionId,
                VariantId = candidate.VariantId,
                ListPrice = prices.ListPrice,
                SalePrice = prices.SalePrice,
                Currency = "TRY",
                VatRate = decimal.Round(vatRate, 4, MidpointRounding.ToEven),
                VatInclusion = vatInclusion,
                RoundingMode = "HALF_EVEN",
                SafetyStock = decimal.Round(Math.Max(0m, safetyStock), 4, MidpointRounding.ToEven),
                Status = "ACTIVE",
                FollowsPanelPrice = true,
                PriceVersion = 1,
                Version = 1
            };
            // B: counts as sent at its values; the marketplace's own values stay unknown (no LastSent* values).
            offer.LastPriceHash = PriceInventoryComposer.CurrentPriceHash(platformCode, offer);
            offer.LastStockProjectionVersion = stockVersions.TryGetValue(candidate.VariantId, out var stockVersion) ? stockVersion : null;
            db.ChannelOffers.Add(offer);
            db.ChannelPriceHistory.Add(new ChannelPriceHistory { Id = Guid.CreateVersion7(), TenantId = tenantId, OfferId = offer.Id, PriceVersion = 1, ListPrice = offer.ListPrice, SalePrice = offer.SalePrice, Currency = offer.Currency, Reason = Reason, ActorSource = actorUserId is { } user ? $"USER:{user:D}" : "SYSTEM", EffectiveAt = now });
            opened++;
        }
        db.AuditLogs.Add(new AuditLog
        {
            TenantId = tenantId,
            ActorUserId = actorUserId,
            Action = AuditAction,
            TargetType = "PlatformConnection",
            TargetId = connectionId.ToString("D"),
            Reason = $"{platformCode}: panel fiyatıyla {opened} teklif açıldı (gönderilmiş sayıldı, pazaryerine bir şey gitmedi); panel fiyatı olmayan {counts.MissingPanelPrice}, KDV'si olmayan {counts.MissingVat} beden açılmadı.",
            CorrelationId = $"panel-price-offers:{connectionId:N}",
            CreatedAt = now
        });
        return counts with { Openable = opened };
    }

    // A variant's panel price as a change left it, and whether it had one saved before (PanelPriceOfferPolicy.FollowsOnPanelChange).
    public sealed record PanelPriceChange(decimal? VariantSalePrice, decimal? ProductSalePrice, decimal? VariantListPrice, decimal? ProductListPrice, bool SavedBefore);

    // A panel price the owner changed reaches the offers that follow it (Özel Fiyat off): their prices become the new panel
    // price, as a new price version with its history row, in the caller's unit of work. The caller queues the returned
    // offers (StockProjectionOutbox.EnqueuePriceChangesAsync: only to a connection whose automatic writes are open). An
    // offer the same change prices itself (skipOfferIds) and one with Özel Fiyat on keep theirs; without a panel price
    // (0, empty, list below sale) nothing changes.
    public static async Task<List<ChannelOffer>> RepriceFollowersAsync(AppDbContext db, Guid tenantId, IReadOnlyDictionary<Guid, PanelPriceChange> changes, IReadOnlySet<Guid> skipOfferIds, string reason, string actorSource, DateTimeOffset now, CancellationToken cancellationToken, bool keepSendMarks = false, IReadOnlyCollection<Guid>? offerIds = null, IReadOnlyDictionary<Guid, string>? knownPlatforms = null)
    {
        var repriced = new List<ChannelOffer>();
        if (changes.Count == 0) return repriced;
        var platforms = knownPlatforms ?? await db.PlatformConnections.AsNoTracking().Where(x => x.TenantId == tenantId).Select(x => new { x.Id, x.PlatformCode }).ToDictionaryAsync(x => x.Id, x => x.PlatformCode, cancellationToken);
        var only = offerIds?.ToArray();
        foreach (var chunk in changes.Keys.Chunk(2_000))
        {
            var offers = await db.ChannelOffers.Where(x => x.TenantId == tenantId && chunk.Contains(x.VariantId) && x.Status == "ACTIVE" && x.FollowsPanelPrice != false && (only == null || only.Contains(x.Id))).ToListAsync(cancellationToken);
            foreach (var offer in offers.OrderBy(x => x.Id))
            {
                var change = changes[offer.VariantId];
                if (skipOfferIds.Contains(offer.Id) || !PanelPriceOfferPolicy.FollowsOnPanelChange(offer.FollowsPanelPrice, change.SavedBefore)) continue;
                var platformCode = platforms.GetValueOrDefault(offer.ConnectionId);
                if (PanelPriceOfferPolicy.PanelPrices(platformCode, change.VariantSalePrice, change.ProductSalePrice, change.VariantListPrice, change.ProductListPrice) is not { } panel) continue;
                if (offer.SalePrice == panel.SalePrice && offer.ListPrice == panel.ListPrice) continue;
                // keepSendMarks ("Doldur"): an offer marked as sent at its old price stays marked at its new one, so the pull
                // queues nothing and no reconcile scan sends it; the price goes with the variant's next stock send.
                var markedAsSent = keepSendMarks && platformCode is not null && offer.LastPriceHash == PriceInventoryComposer.CurrentPriceHash(platformCode, offer);
                // The price the marketplace was known to hold before the pull (nothing confirmed, so the stored price read
                // from it) is kept as the known one (LastSent* without a code): Pazarama, which refuses an equal price,
                // compares with it, so the new price goes with the next stock send; the send log shows it as previous.
                if (keepSendMarks && offer.LastSentSalePrice is null)
                    (offer.LastSentSalePrice, offer.LastSentListPrice) = (PriceInventoryComposer.OutboundSalePrice(offer), PriceInventoryComposer.OutboundListPrice(offer));
                offer.SalePrice = panel.SalePrice;
                offer.ListPrice = panel.ListPrice;
                offer.PriceVersion++;
                offer.Version++;
                if (markedAsSent) offer.LastPriceHash = PriceInventoryComposer.CurrentPriceHash(platformCode!, offer);
                db.ChannelPriceHistory.Add(new ChannelPriceHistory { Id = Guid.CreateVersion7(), TenantId = tenantId, OfferId = offer.Id, PriceVersion = offer.PriceVersion, ListPrice = offer.ListPrice, SalePrice = offer.SalePrice, Currency = offer.Currency, Reason = reason, ActorSource = actorSource, EffectiveAt = now });
                repriced.Add(offer);
            }
        }
        return repriced;
    }

    // A unique-index refusal on the offers: another request opened the same offer meanwhile (the retry reads it as there).
    public static bool IsOfferUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is Npgsql.PostgresException { SqlState: "23505" } postgres
        && (postgres.ConstraintName?.Contains("channel_offers", StringComparison.OrdinalIgnoreCase) == true || postgres.TableName == "channel_offers");
}
