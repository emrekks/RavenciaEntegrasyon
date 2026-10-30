using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace MarketplaceHub.Infrastructure.Persistence;

public sealed class InventoryService(AppDbContext db, CursorCodec cursors, TimeProvider timeProvider, IConfiguration configuration) : IInventoryService
{
    private const string SalePriceRequired = "Satış fiyatı sıfırdan büyük olmalıdır.";

    public async Task<PageResult<InventoryItemView>> ListAsync(Guid tenantId, int limit, string? after, CancellationToken cancellationToken)
    {
        var afterId = Decode(after);
        var query = from item in db.InventoryItems.AsNoTracking()
                    join variant in db.ProductVariants.AsNoTracking() on new { item.TenantId, Id = item.VariantId } equals new { variant.TenantId, variant.Id }
                    where item.TenantId == tenantId && (afterId == Guid.Empty || item.Id.CompareTo(afterId) > 0)
                    orderby item.Id
                    select new InventoryItemView(item.Id, item.VariantId, variant.Sku, item.LocationCode, item.OnHand, item.Reserved, item.Available, item.ProjectionVersion, item.ReconciledAt, item.Version, item.ObservedRemoteQuantity, item.ObservedRemoteAt);
        var rows = await query.Take(limit + 1).ToListAsync(cancellationToken);
        return Page(rows, limit, x => x.Id);
    }

    public async Task<PageResult<LedgerEntryView>> LedgerAsync(Guid tenantId, Guid variantId, int limit, string? after, CancellationToken cancellationToken)
    {
        var afterId = Decode(after);
        var itemIds = db.InventoryItems.Where(x => x.TenantId == tenantId && x.VariantId == variantId).Select(x => x.Id);
        var rows = await db.StockLedgerEntries.AsNoTracking()
            // Opening balances and shipment-progress markers are reconciliation
            // metadata, not new physical stock movements shown in this ledger.
            .Where(x => x.TenantId == tenantId
                && x.MovementType != "ORDER_STOCK_BASELINE"
                && x.MovementType != "ORDER_SHIPMENT_PROGRESS"
                && x.MovementType != OrderInventoryStockPolicy.ShortfallMovementType
                && itemIds.Contains(x.InventoryItemId) && (afterId == Guid.Empty || x.Id.CompareTo(afterId) > 0))
            .OrderBy(x => x.Id).Take(limit + 1)
            .Select(x => new LedgerEntryView(x.Id, x.MovementType, x.QuantityDelta, x.SourceType, x.SourceId, x.OccurredAt, x.CorrelationId))
            .ToListAsync(cancellationToken);
        return Page(rows, limit, x => x.Id);
    }

    public async Task<ServiceResult<InventoryItemView>> AdjustAsync(Guid tenantId, Guid userId, Guid variantId, StockAdjustmentCommand command, string idempotencyKey, string correlationId, CancellationToken cancellationToken)
    {
        if (command.QuantityDelta == 0) return Invalid<InventoryItemView>("quantityDelta", "Stok düzeltme miktarı sıfır olamaz.");
        if (string.IsNullOrWhiteSpace(command.Reason) || string.IsNullOrWhiteSpace(command.SourceEventId)) return Invalid<InventoryItemView>("reason", "Reason ve sourceEventId zorunludur.");
        var existing = await db.StockLedgerEntries.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.IdempotencyKey == idempotencyKey, cancellationToken);
        if (existing is not null)
        {
            var prior = await FindItemAsync(tenantId, existing.InventoryItemId, cancellationToken);
            return prior is null ? NotFound<InventoryItemView>() : ServiceResult<InventoryItemView>.Ok(prior);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken);
        var item = await db.InventoryItems.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.VariantId == variantId && x.LocationCode == "MAIN", cancellationToken);
        if (item is null) return NotFound<InventoryItemView>();
        var newOnHand = decimal.Round(item.OnHand + command.QuantityDelta, 4, MidpointRounding.ToEven);
        if (newOnHand < 0) return Conflict<InventoryItemView>("NEGATIVE_STOCK_REJECTED", "MAIN stok miktarı negatif olamaz.");
        var reserved = await db.StockReservations.Where(x => x.TenantId == tenantId && x.InventoryItemId == item.Id && x.Status == ReservationStatus.Active).SumAsync(x => (decimal?)x.Quantity, cancellationToken) ?? 0;
        item.OnHand = newOnHand;
        item.Reserved = reserved;
        item.Available = InventoryProjection.Available(newOnHand, reserved);
        item.ProjectionVersion++;
        item.Version++;
        var now = timeProvider.GetUtcNow();
        db.StockLedgerEntries.Add(new StockLedgerEntry
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            InventoryItemId = item.Id,
            MovementType = "MANUAL_ADJUSTMENT",
            QuantityDelta = decimal.Round(command.QuantityDelta, 4, MidpointRounding.ToEven),
            SourceType = "MANUAL",
            SourceId = command.Reason.Trim(),
            SourceEventId = command.SourceEventId.Trim(),
            IdempotencyKey = idempotencyKey,
            OccurredAt = now,
            RecordedAt = now,
            ActorUserId = userId,
            CorrelationId = correlationId
        });
        // The new stock goes to every connection selling the variant, committed with the adjustment.
        await StockProjectionOutbox.EnqueueAsync(db, tenantId, [new StockProjectionChange(item.VariantId, item.ProjectionVersion, $"manual:{command.SourceEventId.Trim()}")], now, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        var sku = await db.ProductVariants.Where(x => x.TenantId == tenantId && x.Id == item.VariantId).Select(x => x.Sku).SingleAsync(cancellationToken);
        return ServiceResult<InventoryItemView>.Ok(Map(item, sku));
    }

    // W2/W3/W18: the panel sends the stock the user typed together with the stock the user saw. The change is
    // applied only while MAIN on-hand still equals that value, so a sale, return or another tab in between is
    // never overwritten by a delta computed against a stale number (409 STOCK_CHANGED instead).
    public async Task<ServiceResult<InventoryItemView>> SetOnHandAsync(Guid tenantId, Guid userId, Guid variantId, StockSetCommand command, string idempotencyKey, string correlationId, CancellationToken cancellationToken)
    {
        if (command.OnHand < 0) return Invalid<InventoryItemView>("onHand", "Stok negatif olamaz.");
        if (command.OnHand > 1_000_000_000m) return Invalid<InventoryItemView>("onHand", "Stok miktarı çok büyük.");
        if (string.IsNullOrWhiteSpace(command.Reason) || string.IsNullOrWhiteSpace(command.SourceEventId)) return Invalid<InventoryItemView>("reason", "Reason ve sourceEventId zorunludur.");
        var existing = await db.StockLedgerEntries.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.IdempotencyKey == idempotencyKey, cancellationToken);
        if (existing is not null)
        {
            var prior = await FindItemAsync(tenantId, existing.InventoryItemId, cancellationToken);
            return prior is null ? NotFound<InventoryItemView>() : ServiceResult<InventoryItemView>.Ok(prior);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken);
        var item = await db.InventoryItems.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.VariantId == variantId && x.LocationCode == "MAIN", cancellationToken);
        if (item is null) return NotFound<InventoryItemView>();
        var target = decimal.Round(command.OnHand, 4, MidpointRounding.ToEven);
        var sku = await db.ProductVariants.Where(x => x.TenantId == tenantId && x.Id == item.VariantId).Select(x => x.Sku).SingleAsync(cancellationToken);
        if (item.OnHand == target)
        {
            await transaction.CommitAsync(cancellationToken);
            return ServiceResult<InventoryItemView>.Ok(Map(item, sku));
        }
        if (item.OnHand != decimal.Round(command.ExpectedOnHand, 4, MidpointRounding.ToEven))
            return ServiceResult<InventoryItemView>.Fail("STOCK_CHANGED", $"Stok başka bir işlemle değişti; güncel stok {item.OnHand.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)}. Sayfayı yenileyip tekrar deneyin.", 409);
        var delta = target - item.OnHand;
        var reserved = await db.StockReservations.Where(x => x.TenantId == tenantId && x.InventoryItemId == item.Id && x.Status == ReservationStatus.Active).SumAsync(x => (decimal?)x.Quantity, cancellationToken) ?? 0;
        item.OnHand = target;
        item.Reserved = reserved;
        item.Available = InventoryProjection.Available(target, reserved);
        item.ProjectionVersion++;
        item.Version++;
        var now = timeProvider.GetUtcNow();
        db.StockLedgerEntries.Add(new StockLedgerEntry
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            InventoryItemId = item.Id,
            MovementType = "MANUAL_ADJUSTMENT",
            QuantityDelta = delta,
            SourceType = "MANUAL",
            SourceId = command.Reason.Trim(),
            SourceEventId = command.SourceEventId.Trim(),
            IdempotencyKey = idempotencyKey,
            OccurredAt = now,
            RecordedAt = now,
            ActorUserId = userId,
            CorrelationId = correlationId
        });
        await StockProjectionOutbox.EnqueueAsync(db, tenantId, [new StockProjectionChange(item.VariantId, item.ProjectionVersion, $"manual:{command.SourceEventId.Trim()}")], now, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ServiceResult<InventoryItemView>.Ok(Map(item, sku));
    }

    public async Task<ServiceResult<ChannelOfferView>> GetOfferAsync(Guid tenantId, Guid id, CancellationToken cancellationToken)
    {
        var offer = await db.ChannelOffers.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, cancellationToken);
        return offer is null ? NotFound<ChannelOfferView>() : ServiceResult<ChannelOfferView>.Ok(Map(offer));
    }

    public async Task<ServiceResult<ChannelOfferView>> UpsertOfferAsync(Guid tenantId, Guid userId, UpsertChannelOfferCommand command, CancellationToken cancellationToken)
    {
        var existing = await db.ChannelOffers.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == command.ConnectionId && x.VariantId == command.VariantId, cancellationToken);
        if (existing is not null)
            return await UpdateOfferAsync(tenantId, userId, existing.Id, existing.Version, new(command.ListPrice, command.SalePrice, command.Currency, command.VatRate, command.VatInclusion, command.RoundingMode, command.SafetyStock, command.Status, command.Reason), cancellationToken);

        var connectionExists = await db.PlatformConnections.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.Id == command.ConnectionId, cancellationToken);
        var variantExists = await db.ProductVariants.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.Id == command.VariantId, cancellationToken);
        if (!connectionExists || !variantExists) return Invalid<ChannelOfferView>(!connectionExists ? "connectionId" : "variantId", "Bağlantı veya varyant bulunamadı.");
        // W-3: no marketplace takes a sale price of zero (every composer refuses it), so it is not stored either.
        if (command.SalePrice <= 0) return Invalid<ChannelOfferView>("salePrice", SalePriceRequired);
        if (command.ListPrice < 0 || command.SalePrice < 0 || command.ListPrice < command.SalePrice) return Invalid<ChannelOfferView>("salePrice", "Fiyatlar negatif olamaz ve liste fiyatı satış fiyatından küçük olamaz.");
        var currency = command.Currency.Trim().ToUpperInvariant();
        try { _ = Money.Create(command.SalePrice, currency); } catch (ArgumentException) { return Invalid<ChannelOfferView>("currency", "Currency üç büyük harften oluşmalıdır."); }
        if (command.SafetyStock < 0 || command.VatRate < 0) return Invalid<ChannelOfferView>("safetyStock", "Güvenlik stoğu ve KDV negatif olamaz.");
        if (string.IsNullOrWhiteSpace(command.Reason) || string.IsNullOrWhiteSpace(command.VatInclusion) || string.IsNullOrWhiteSpace(command.RoundingMode) || string.IsNullOrWhiteSpace(command.Status)) return Invalid<ChannelOfferView>("reason", "Fiyat değişikliği nedeni ve teklif politikası alanları zorunludur.");
        if (ValidatePricing(command.VatInclusion, command.RoundingMode, command.VatRate) is { } pricingError) return pricingError;

        var offer = new ChannelOffer
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            ConnectionId = command.ConnectionId,
            VariantId = command.VariantId,
            ListPrice = decimal.Round(command.ListPrice, 4, MidpointRounding.ToEven),
            SalePrice = decimal.Round(command.SalePrice, 4, MidpointRounding.ToEven),
            Currency = currency,
            VatRate = decimal.Round(command.VatRate, 4, MidpointRounding.ToEven),
            VatInclusion = command.VatInclusion.Trim().ToUpperInvariant(),
            RoundingMode = command.RoundingMode.Trim().ToUpperInvariant(),
            SafetyStock = decimal.Round(command.SafetyStock, 4, MidpointRounding.ToEven),
            Status = command.Status.Trim(),
            PriceVersion = 1,
            Version = 1
        };
        db.ChannelOffers.Add(offer);
        db.ChannelPriceHistory.Add(new ChannelPriceHistory { Id = Guid.CreateVersion7(), TenantId = tenantId, OfferId = offer.Id, PriceVersion = 1, ListPrice = offer.ListPrice, SalePrice = offer.SalePrice, Currency = offer.Currency, Reason = command.Reason.Trim(), ActorSource = $"USER:{userId:D}", EffectiveAt = timeProvider.GetUtcNow() });
        // E: the new price goes to the offer's connection when its automatic writes are on, committed with the edit.
        await StockProjectionOutbox.EnqueuePriceChangesAsync(db, tenantId, [offer], timeProvider.GetUtcNow(), cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult<ChannelOfferView>.Ok(Map(offer));
    }

    public async Task<ServiceResult<ChannelOfferView>> UpdateOfferAsync(Guid tenantId, Guid userId, Guid id, long expectedVersion, UpdateChannelOfferCommand command, CancellationToken cancellationToken)
    {
        var offer = await db.ChannelOffers.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, cancellationToken);
        if (offer is null) return NotFound<ChannelOfferView>();
        if (offer.Version != expectedVersion) return ServiceResult<ChannelOfferView>.Fail("CONCURRENCY_CONFLICT", $"Kayıt sürümü değişti; güncel sürüm v{offer.Version}.", 412);
        if (command.SalePrice <= 0) return Invalid<ChannelOfferView>("salePrice", SalePriceRequired);
        if (command.ListPrice < 0 || command.SalePrice < 0 || command.ListPrice < command.SalePrice) return Invalid<ChannelOfferView>("salePrice", "Fiyatlar negatif olamaz ve listPrice salePrice değerinden küçük olamaz.");
        var currency = command.Currency.Trim().ToUpperInvariant();
        try { _ = Money.Create(command.SalePrice, currency); } catch (ArgumentException) { return Invalid<ChannelOfferView>("currency", "Currency üç büyük harften oluşmalıdır."); }
        if (command.SafetyStock < 0 || command.VatRate < 0) return Invalid<ChannelOfferView>("safetyStock", "Safety stock ve VAT negatif olamaz.");
        if (string.IsNullOrWhiteSpace(command.Reason) || string.IsNullOrWhiteSpace(command.VatInclusion) || string.IsNullOrWhiteSpace(command.RoundingMode) || string.IsNullOrWhiteSpace(command.Status)) return Invalid<ChannelOfferView>("reason", "Fiyat değişikliği nedeni ve teklif politikası alanları zorunludur.");
        if (ValidatePricing(command.VatInclusion, command.RoundingMode, command.VatRate) is { } pricingError) return pricingError;

        var listPrice = decimal.Round(command.ListPrice, 4, MidpointRounding.ToEven);
        var salePrice = decimal.Round(command.SalePrice, 4, MidpointRounding.ToEven);
        var vatRate = decimal.Round(command.VatRate, 4, MidpointRounding.ToEven);
        var vatInclusion = command.VatInclusion.Trim().ToUpperInvariant();
        var roundingMode = command.RoundingMode.Trim().ToUpperInvariant();
        var safetyStock = decimal.Round(command.SafetyStock, 4, MidpointRounding.ToEven);
        var status = command.Status.Trim();
        var change = ChannelOfferChangePolicy.Classify(offer, listPrice, salePrice, currency, vatRate, vatInclusion, roundingMode, safetyStock, status);
        if (!change.Any) return ServiceResult<ChannelOfferView>.Ok(Map(offer));
        offer.ListPrice = listPrice;
        offer.SalePrice = salePrice;
        offer.Currency = currency;
        offer.VatRate = vatRate;
        offer.VatInclusion = vatInclusion;
        offer.RoundingMode = roundingMode;
        offer.SafetyStock = safetyStock;
        offer.Status = status;
        offer.Version++;
        // Only a change of the values the outbound price is made of is a new price version: a save that leaves them as they
        // were (a safety stock or status edit, a save without changes) sends no price to any marketplace.
        if (change.Price)
        {
            offer.PriceVersion++;
            db.ChannelPriceHistory.Add(new ChannelPriceHistory
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                OfferId = offer.Id,
                PriceVersion = offer.PriceVersion,
                ListPrice = offer.ListPrice,
                SalePrice = offer.SalePrice,
                Currency = offer.Currency,
                Reason = command.Reason.Trim(),
                ActorSource = $"USER:{userId:D}",
                EffectiveAt = timeProvider.GetUtcNow()
            });
        }
        // The quantity sent is the available stock less the safety stock (1 kept of 3 sends 2), so a new safety stock is a
        // stock change of this offer: its stock mark is cleared and the next send carries the new quantity on every platform.
        if (change.SafetyStock) offer.LastStockProjectionVersion = null;
        // E: the edited price or quantity goes to the offer's connection when its automatic writes are on, committed with the edit.
        if (change.Price || change.SafetyStock || change.Status)
            await StockProjectionOutbox.EnqueuePriceChangesAsync(db, tenantId, [offer], timeProvider.GetUtcNow(), cancellationToken, _ => change.Price);
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult<ChannelOfferView>.Ok(Map(offer));
    }

    public Task<ServiceResult<Guid>> ValidateExternalSyncAsync(Guid tenantId, string operation, CancellationToken cancellationToken) =>
        Task.FromResult(ServiceResult<Guid>.Fail("USE_COMBINED_PRICE_INVENTORY", $"{operation} Trendyol'da ayrık gönderilmez; birleşik price-and-inventory işi kullanılmalıdır.", 422));

    // stockCode (n11) limits the send to one linked variant, which is how the
    // first controlled n11 write is made before the automatic flows are on.
    public async Task<ServiceResult<Guid>> EnqueuePriceInventorySyncAsync(Guid tenantId, Guid connectionId, string idempotencyKey, string correlationId, CancellationToken cancellationToken, string? stockCode = null)
    {
        var connection = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == connectionId && (x.PlatformCode == "TRENDYOL" && x.Status == "ACTIVE" || (x.PlatformCode == "N11" || x.PlatformCode == "PAZARAMA" || x.PlatformCode == "PTTAVM" || x.PlatformCode == "HEPSIBURADA") && (x.Status == "ACTIVE" || x.Status == "VERIFIED")), cancellationToken);
        // Hepsiburada is accepted only for the SIT certification upload of a STAGE connection; any other
        // Hepsiburada connection (PRODUCTION) gets the same refusal as before.
        if (connection is not null && connection.PlatformCode == "HEPSIBURADA" && !HepsiburadaStageCertificationPolicy.IsStageHepsiburada(connection)) connection = null;
        if (connection is null) return ServiceResult<Guid>.Fail("ACTIVE_CONNECTION_REQUIRED", "Fiyat-stok gönderimi için aktif Trendyol veya aktif/doğrulanmış N11, Pazarama ya da PTT AVM bağlantısı gerekir.", 422);
        if (connection.PlatformCode == "HEPSIBURADA") return await EnqueueHepsiburadaStagePriceInventoryAsync(tenantId, connection, idempotencyKey, correlationId, stockCode, cancellationToken);
        Guid? variantId = null;
        if (!string.IsNullOrWhiteSpace(stockCode))
        {
            if (connection.PlatformCode is not ("N11" or "PAZARAMA" or "PTTAVM")) return ServiceResult<Guid>.Fail("VALIDATION_FAILED", "Stok koduyla tekil gönderim yalnız N11, Pazarama ve PTT AVM bağlantısında kullanılabilir.", 422);
            var code = stockCode.Trim();
            variantId = connection.PlatformCode == "PTTAVM"
                ? await PttAvmSendVariantAsync(tenantId, connectionId, code, cancellationToken)
                : await db.MarketplaceVariantLinks.AsNoTracking()
                    .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ExternalId == code)
                    .Select(x => (Guid?)x.VariantId)
                    .SingleOrDefaultAsync(cancellationToken);
            if (variantId is null && connection.PlatformCode == "PTTAVM") return ServiceResult<Guid>.Fail("PTTAVM_BARCODE_NOT_LINKED", $"'{code}' barkodu panelde bir varyanta bağlı değil. Önce PTT AVM ürün eşleştirmesini çalıştırın.", 422);
            if (variantId is null) return ServiceResult<Guid>.Fail(connection.PlatformCode == "PAZARAMA" ? "PAZARAMA_BARCODE_NOT_LINKED" : "N11_STOCK_CODE_NOT_LINKED", connection.PlatformCode == "PAZARAMA" ? $"'{code}' barkodu panelde bir varyanta bağlı değil. Önce Pazarama ürün eşleştirmesini çalıştırın." : $"'{code}' stok kodu panelde bir varyanta bağlı değil. Önce N11 ürün eşleştirmesini çalıştırın.", 404);
            if (connection.PlatformCode is not ("PAZARAMA" or "PTTAVM") && !await db.ConnectionSyncPolicies.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == MarketplaceExternalWritePolicies.Stock && x.Enabled, cancellationToken))
                return ServiceResult<Guid>.Fail("EXTERNAL_WRITE_POLICY_DISABLED", "Stok dış yazma akışı kapalı.", 422);
        }
        if (!IntegrationRuntimePolicy.IsSupportedEnvironment(connection)) return ServiceResult<Guid>.Fail("ENVIRONMENT_INVALID", "Fiyat-stok gönderimi yalnız STAGE veya PRODUCTION bağlantısında çalışır.", 422);
        if (!WritesEnabled(connection.SettingsJson)) return ServiceResult<Guid>.Fail("EXTERNAL_WRITES_DISABLED", "Global veya connection dış yazma anahtarı kapalı.", 422);
        // A PTT AVM send from the panel also needs the connection's "Manuel Dış Yazmalar" switch (MANUAL_WRITE).
        if (PttAvmManualWritePolicy.Applies(connection.PlatformCode))
        {
            var manualPolicyEnabled = await db.ConnectionSyncPolicies.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == MarketplaceExternalWritePolicies.Manual)
                .Select(x => (bool?)x.Enabled)
                .SingleOrDefaultAsync(cancellationToken);
            if (PttAvmManualWritePolicy.Decide(masterWritesEnabled: true, manualPolicyEnabled) != PttAvmManualWriteDecision.Allowed)
                return ServiceResult<Guid>.Fail(PttAvmManualWritePolicy.DisabledCode, PttAvmManualWritePolicy.DisabledMessage, 422);
        }
        var pricePolicy = await db.ConnectionSyncPolicies.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == MarketplaceExternalWritePolicies.Price, cancellationToken);
        // Pazarama and PTT AVM sends started from the panel are manual: the connection switch is the only gate; the
        // automatic price/stock policies only govern sends the system starts on its own.
        var manual = connection.PlatformCode is "PAZARAMA" or "PTTAVM";
        if (!manual && pricePolicy is not null && !pricePolicy.Enabled) return ServiceResult<Guid>.Fail("EXTERNAL_WRITE_POLICY_DISABLED", "Fiyat dış yazma akışı kapalı.", 422);
        // The request writes stock as well (Trendyol's too), so the stock policy must allow it.
        if (!manual && !await StockWritePolicyAllowsAsync(tenantId, connectionId, cancellationToken)) return ServiceResult<Guid>.Fail("EXTERNAL_WRITE_POLICY_DISABLED", "Stok dış yazma akışı kapalı.", 422);
        var build = await new PriceInventoryComposer(db).BuildAsync(tenantId, connectionId, cancellationToken, variantId);
        if (!build.Succeeded) return ServiceResult<Guid>.Fail(build.Error!.Code, build.Error.Message, build.Error.Status, build.Error.FieldErrors);
        var draft = build.Value!; var dedup = PriceInventoryOutboxPolicy.DedupKey(connectionId, draft.Lines);
        var existing = await StandingPriceInventoryJobAsync(tenantId, dedup, cancellationToken);
        if (existing is not null) return ServiceResult<Guid>.Ok(existing.Id);
        var id = Guid.CreateVersion7(); var now = timeProvider.GetUtcNow();
        var payload = JsonSerializer.Serialize(new PriceInventoryJobPayload(id, connectionId, "SUBMIT", draft.PayloadHash, draft.PayloadJson, draft.Lines, null, null, variantId, null, manual, Trigger: PriceStockSendTriggers.PanelSend));
        var writeDelay = pricePolicy is null || manual ? 0 : Math.Clamp(pricePolicy.IntervalSeconds, 0, 86_400);
        db.IntegrationJobs.Add(new IntegrationJob { Id = id, TenantId = tenantId, ConnectionId = connectionId, JobType = MarketplaceJobTypes.PriceInventorySync, PayloadJson = payload, PayloadVersion = 1, PayloadHash = Hash(payload), JobDedupKey = dedup, EffectIdempotencyKey = $"{dedup}:{Hash(idempotencyKey.Trim())}", Priority = JobPriorityPolicy.ForUser(MarketplaceJobTypes.PriceInventorySync), TriggerType = "user", Status = JobStatus.Pending, AvailableAt = now.AddSeconds(writeDelay), MaxAttempts = 10, CorrelationId = correlationId, CreatedAt = now, Version = 1 });
        await db.SaveChangesAsync(cancellationToken); return ServiceResult<Guid>.Ok(id);
    }

    // A PTT AVM send names a variant by its code (stock code or barcode) or a product by its PTT AVM barcode (the model
    // code); the composer sends the whole product either way, so any linked variant of the product will do.
    private async Task<Guid?> PttAvmSendVariantAsync(Guid tenantId, Guid connectionId, string code, CancellationToken cancellationToken) =>
        await db.MarketplaceVariantLinks.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && (x.ExternalId == code || x.ExternalBarcode == code || x.ExternalSku == code))
            .OrderBy(x => x.Id)
            .Select(x => (Guid?)x.VariantId)
            .FirstOrDefaultAsync(cancellationToken)
        ?? await (from productLink in db.MarketplaceProductLinks.AsNoTracking()
                  join variant in db.ProductVariants.AsNoTracking() on new { productLink.TenantId, productLink.ProductId } equals new { variant.TenantId, variant.ProductId }
                  join variantLink in db.MarketplaceVariantLinks.AsNoTracking() on new { variant.TenantId, VariantId = variant.Id, productLink.ConnectionId } equals new { variantLink.TenantId, variantLink.VariantId, variantLink.ConnectionId }
                  where productLink.TenantId == tenantId && productLink.ConnectionId == connectionId && productLink.ExternalCode == code
                  orderby variant.SortOrder, variant.Id
                  select (Guid?)variant.Id)
            .FirstOrDefaultAsync(cancellationToken);

    // Hepsiburada SIT certification: an upload of the imported SIT offers, started from the panel on a STAGE
    // connection. It is a manual Stage operation, so the global/connection write switches and the automatic
    // price/stock policies do not apply; the worker sends it as a manual operation and the adapter still
    // requires an operational STAGE connection with PRICE_WRITE/INVENTORY_WRITE evidence. Each click (a new
    // Idempotency-Key) makes a new upload, so a fresh inventoryUploadId can always be produced.
    private async Task<ServiceResult<Guid>> EnqueueHepsiburadaStagePriceInventoryAsync(Guid tenantId, PlatformConnection connection, string idempotencyKey, string correlationId, string? stockCode, CancellationToken cancellationToken)
    {
        if (!IntegrationRuntimePolicy.IsOperationalStage(connection)) return ServiceResult<Guid>.Fail("STAGE_CONNECTION_NOT_READY", "SIT fiyat-stok yüklemesi için bağlantı aktif/doğrulanmış olmalı ve son bağlantı testi hatasız tamamlanmalı.", 422);
        if (!string.Equals(configuration["Hepsiburada:AuthenticationMode"], "BASIC", StringComparison.OrdinalIgnoreCase)) return ServiceResult<Guid>.Fail("HEPSIBURADA_AUTHENTICATION_UNVERIFIED", "Hepsiburada auth biçimi SIT hesabında doğrulanana kadar fiyat-stok yüklemesi kuyruğa alınamaz.", 422);
        Guid? variantId = null;
        if (!string.IsNullOrWhiteSpace(stockCode))
        {
            var code = stockCode.Trim();
            var variantIds = await db.MarketplaceVariantLinks.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.ConnectionId == connection.Id && x.ExternalId == code)
                .Select(x => x.VariantId)
                .ToListAsync(cancellationToken);
            if (variantIds.Count != 1) return ServiceResult<Guid>.Fail("HEPSIBURADA_SKU_NOT_LINKED", $"'{code}' hbSku'su panelde tek bir varyanta bağlı değil. Önce Hepsiburada ürün eşleştirmesini çalıştırın.", 404);
            variantId = variantIds[0];
        }
        var build = await new PriceInventoryComposer(db).BuildHepsiburadaStageAsync(tenantId, connection.Id, cancellationToken, variantId);
        if (!build.Succeeded) return ServiceResult<Guid>.Fail(build.Error!.Code, build.Error.Message, build.Error.Status, build.Error.FieldErrors);
        var draft = build.Value!;
        var key = idempotencyKey.Trim();
        var dedup = $"{PriceInventoryOutboxPolicy.DedupKey(connection.Id, draft.Lines)}:sit:{Hash(key)[..32]}";
        var existing = await db.IntegrationJobs.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.JobType == MarketplaceJobTypes.PriceInventorySync && x.JobDedupKey == dedup, cancellationToken);
        if (existing is not null) return ServiceResult<Guid>.Ok(existing.Id);
        var id = Guid.CreateVersion7(); var now = timeProvider.GetUtcNow();
        var payload = JsonSerializer.Serialize(new PriceInventoryJobPayload(id, connection.Id, "SUBMIT", draft.PayloadHash, draft.PayloadJson, draft.Lines, null, null, variantId, null, Manual: true, Trigger: PriceStockSendTriggers.PanelSend));
        db.IntegrationJobs.Add(new IntegrationJob { Id = id, TenantId = tenantId, ConnectionId = connection.Id, JobType = MarketplaceJobTypes.PriceInventorySync, PayloadJson = payload, PayloadVersion = 1, PayloadHash = Hash(payload), JobDedupKey = dedup, EffectIdempotencyKey = $"{dedup}:{Hash(key)}", Priority = JobPriorityPolicy.ForUser(MarketplaceJobTypes.PriceInventorySync), TriggerType = "user", Status = JobStatus.Pending, AvailableAt = now, MaxAttempts = 10, CorrelationId = correlationId, CreatedAt = now, Version = 1 });
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult<Guid>.Ok(id);
    }

    // price/stock: what "Platformlarda Güncelle" picked; both by default. The part not picked is held (left out of the
    // request, not recorded as sent). The write policies are checked as before, whatever was picked.
    public async Task<ServiceResult<Guid>> EnqueueProductPriceInventorySyncAsync(Guid tenantId, Guid productId, Guid connectionId, string idempotencyKey, string correlationId, CancellationToken cancellationToken, bool price = true, bool stock = true)
    {
        if (!price && !stock) return ServiceResult<Guid>.Fail("PRICE_INVENTORY_FIELDS_REQUIRED", "Fiyat veya stoktan en az biri seçilmelidir.", 422);
        var connection = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == connectionId && (x.PlatformCode == "TRENDYOL" || x.PlatformCode == "HEPSIBURADA") && x.Status == "ACTIVE", cancellationToken);
        if (connection is null) return ServiceResult<Guid>.Fail("ACTIVE_CONNECTION_REQUIRED", "Ürüne özel fiyat-stok gönderimi için ACTIVE Trendyol veya Hepsiburada bağlantısı gerekir.", 422);
        if (!IntegrationRuntimePolicy.IsSupportedEnvironment(connection)) return ServiceResult<Guid>.Fail("ENVIRONMENT_INVALID", "Fiyat-stok gönderimi yalnız STAGE veya PRODUCTION bağlantısında çalışır.", 422);
        if (!WritesEnabled(connection.SettingsJson)) return ServiceResult<Guid>.Fail("EXTERNAL_WRITES_DISABLED", "Global veya connection dış yazma anahtarı kapalı.", 422);
        var pricePolicy = await db.ConnectionSyncPolicies.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == MarketplaceExternalWritePolicies.Price, cancellationToken);
        if (pricePolicy is not null && !pricePolicy.Enabled) return ServiceResult<Guid>.Fail("EXTERNAL_WRITE_POLICY_DISABLED", "Fiyat dış yazma akışı kapalı.", 422);
        if (!await StockWritePolicyAllowsAsync(tenantId, connectionId, cancellationToken)) return ServiceResult<Guid>.Fail("EXTERNAL_WRITE_POLICY_DISABLED", "Stok dış yazma akışı kapalı.", 422);
        var build = await new PriceInventoryComposer(db).BuildAsync(tenantId, connectionId, cancellationToken, productId: productId, holdPrice: !price, holdStock: !stock);
        if (!build.Succeeded) return ServiceResult<Guid>.Fail(build.Error!.Code, build.Error.Message, build.Error.Status, build.Error.FieldErrors);
        var draft = build.Value!;
        // A send of one part is a job of its own: a standing full send of the same values must not answer for it, nor
        // the other way round.
        var dedup = PriceInventoryOutboxPolicy.DedupKey(connectionId, draft.Lines) + (price && stock ? "" : price ? ":price" : ":stock");
        var existing = await StandingPriceInventoryJobAsync(tenantId, dedup, cancellationToken);
        if (existing is not null) return ServiceResult<Guid>.Ok(existing.Id);
        var id = Guid.CreateVersion7();
        var now = timeProvider.GetUtcNow();
        var payload = JsonSerializer.Serialize(new PriceInventoryJobPayload(id, connectionId, "SUBMIT", draft.PayloadHash, draft.PayloadJson, draft.Lines, null, null, null, productId, Trigger: PriceStockSendTriggers.PanelSend, HoldPrice: !price, HoldStock: !stock));
        var writeDelay = pricePolicy is null ? 0 : Math.Clamp(pricePolicy.IntervalSeconds, 0, 86_400);
        db.IntegrationJobs.Add(new IntegrationJob { Id = id, TenantId = tenantId, ConnectionId = connectionId, JobType = MarketplaceJobTypes.PriceInventorySync, PayloadJson = payload, PayloadVersion = 1, PayloadHash = Hash(payload), JobDedupKey = dedup, EffectIdempotencyKey = $"{dedup}:{Hash(idempotencyKey.Trim())}", Priority = JobPriorityPolicy.ForUser(MarketplaceJobTypes.PriceInventorySync), TriggerType = "user", Status = JobStatus.Pending, AvailableAt = now.AddSeconds(writeDelay), MaxAttempts = 10, CorrelationId = correlationId, CreatedAt = now, Version = 1 });
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult<Guid>.Ok(id);
    }

    // Q-1/K-1: a queued, running or succeeded job of the same values is the answer to a send from the panel. A
    // failed one (blocked, dead, cancelled, manual review) gives up its key, saved with the new job, so the
    // values are sent again instead of the failed job being returned as if it had been queued.
    private async Task<IntegrationJob?> StandingPriceInventoryJobAsync(Guid tenantId, string dedup, CancellationToken cancellationToken)
    {
        var existing = await db.IntegrationJobs.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.JobType == MarketplaceJobTypes.PriceInventorySync && x.JobDedupKey == dedup, cancellationToken);
        if (existing is null || PriceInventoryOutboxPolicy.HoldsDedupKey(existing.Status)) return existing;
        existing.JobDedupKey = PriceInventoryOutboxPolicy.RetiredDedupKey(dedup, existing.Id);
        existing.Version++;
        return null;
    }

    // A missing policy row means the flow was never switched off, the same rule the worker applies.
    private async Task<bool> StockWritePolicyAllowsAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken)
    {
        var policy = await db.ConnectionSyncPolicies.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == MarketplaceExternalWritePolicies.Stock, cancellationToken);
        return policy is null || policy.Enabled;
    }

    // An offer kept without VAT is grossed up with its own rate when it is sent, so the VAT mode and
    // the rounding mode must be ones the outbound price rule knows.
    private static ServiceResult<ChannelOfferView>? ValidatePricing(string vatInclusion, string roundingMode, decimal vatRate)
    {
        if (!ChannelOfferPricePolicy.IsKnownVatInclusion(vatInclusion)) return Invalid<ChannelOfferView>("vatInclusion", "KDV durumu INCLUDED veya EXCLUDED olmalıdır.");
        if (!ChannelOfferPricePolicy.IsKnownRoundingMode(roundingMode)) return Invalid<ChannelOfferView>("roundingMode", "Yuvarlama modu HALF_EVEN, HALF_UP, UP, DOWN, CEILING veya FLOOR olmalıdır.");
        if (ChannelOfferPricePolicy.IsVatExcluded(vatInclusion) && vatRate > 100) return Invalid<ChannelOfferView>("vatRate", "KDV hariç fiyat için KDV oranı 0-100 arasında olmalıdır.");
        return null;
    }

    private bool WritesEnabled(string settingsJson)
    {
        if (!configuration.GetValue<bool>("FeatureFlags:ExternalWrites")) return false;
        try { using var document = JsonDocument.Parse(settingsJson); return document.RootElement.TryGetProperty("ExternalWritesEnabled", out var enabled) && enabled.ValueKind == JsonValueKind.True; }
        catch (JsonException) { return false; }
    }
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private async Task<InventoryItemView?> FindItemAsync(Guid tenantId, Guid itemId, CancellationToken cancellationToken) =>
        await (from item in db.InventoryItems.AsNoTracking()
               join variant in db.ProductVariants.AsNoTracking() on new { item.TenantId, Id = item.VariantId } equals new { variant.TenantId, variant.Id }
               where item.TenantId == tenantId && item.Id == itemId
               select Map(item, variant.Sku)).SingleOrDefaultAsync(cancellationToken);

    private Guid Decode(string? cursor) => cursors.TryDecode(cursor, out var id) ? id : throw new ArgumentException("Cursor geçersiz veya süresi dolmuş.", nameof(cursor));
    private PageResult<T> Page<T>(List<T> rows, int limit, Func<T, Guid> id) { var hasMore = rows.Count > limit; var items = rows.Take(limit).ToList(); return new(items, hasMore ? cursors.Encode(id(items[^1])) : null, hasMore); }
    private static InventoryItemView Map(InventoryItem value, string sku) => new(value.Id, value.VariantId, sku, value.LocationCode, value.OnHand, value.Reserved, value.Available, value.ProjectionVersion, value.ReconciledAt, value.Version, value.ObservedRemoteQuantity, value.ObservedRemoteAt);
    private static ChannelOfferView Map(ChannelOffer value) => new(value.Id, value.ConnectionId, value.VariantId, value.ListPrice, value.SalePrice, value.Currency, value.VatRate, value.VatInclusion, value.RoundingMode, value.SafetyStock, value.Status, value.PriceVersion, value.Version);
    private static ServiceResult<T> Invalid<T>(string field, string message) => ServiceResult<T>.Fail("VALIDATION_FAILED", message, 422, new Dictionary<string, string[]> { [field] = [message] });
    private static ServiceResult<T> NotFound<T>() => ServiceResult<T>.Fail("RESOURCE_NOT_FOUND", "Kayıt bulunamadı.", 404);
    private static ServiceResult<T> Conflict<T>(string code, string message) => ServiceResult<T>.Fail(code, message, 409);
}
