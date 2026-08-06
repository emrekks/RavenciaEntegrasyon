using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace MarketplaceHub.Infrastructure.Persistence;

public sealed class InventoryService(AppDbContext db, CursorCodec cursors, TimeProvider timeProvider, IConfiguration configuration) : IInventoryService
{
    public async Task<PageResult<InventoryItemView>> ListAsync(Guid tenantId, int limit, string? after, CancellationToken cancellationToken)
    {
        var afterId = Decode(after);
        var query = from item in db.InventoryItems.AsNoTracking()
                    join variant in db.ProductVariants.AsNoTracking() on new { item.TenantId, Id = item.VariantId } equals new { variant.TenantId, variant.Id }
                    where item.TenantId == tenantId && (afterId == Guid.Empty || item.Id.CompareTo(afterId) > 0)
                    orderby item.Id
                    select new InventoryItemView(item.Id, item.VariantId, variant.Sku, item.LocationCode, item.OnHand, item.Reserved, item.Available, item.ProjectionVersion, item.ReconciledAt, item.Version);
        var rows = await query.Take(limit + 1).ToListAsync(cancellationToken);
        return Page(rows, limit, x => x.Id);
    }

    public async Task<PageResult<LedgerEntryView>> LedgerAsync(Guid tenantId, Guid variantId, int limit, string? after, CancellationToken cancellationToken)
    {
        var afterId = Decode(after);
        var itemIds = db.InventoryItems.Where(x => x.TenantId == tenantId && x.VariantId == variantId).Select(x => x.Id);
        var rows = await db.StockLedgerEntries.AsNoTracking()
            .Where(x => x.TenantId == tenantId && itemIds.Contains(x.InventoryItemId) && (afterId == Guid.Empty || x.Id.CompareTo(afterId) > 0))
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
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        var sku = await db.ProductVariants.Where(x => x.TenantId == tenantId && x.Id == item.VariantId).Select(x => x.Sku).SingleAsync(cancellationToken);
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
        if (command.ListPrice < 0 || command.SalePrice < 0 || command.ListPrice < command.SalePrice) return Invalid<ChannelOfferView>("salePrice", "Fiyatlar negatif olamaz ve liste fiyatı satış fiyatından küçük olamaz.");
        var currency = command.Currency.Trim().ToUpperInvariant();
        try { _ = Money.Create(command.SalePrice, currency); } catch (ArgumentException) { return Invalid<ChannelOfferView>("currency", "Currency üç büyük harften oluşmalıdır."); }
        if (command.SafetyStock < 0 || command.VatRate < 0) return Invalid<ChannelOfferView>("safetyStock", "Güvenlik stoğu ve KDV negatif olamaz.");
        if (string.IsNullOrWhiteSpace(command.Reason) || string.IsNullOrWhiteSpace(command.VatInclusion) || string.IsNullOrWhiteSpace(command.RoundingMode) || string.IsNullOrWhiteSpace(command.Status)) return Invalid<ChannelOfferView>("reason", "Fiyat değişikliği nedeni ve teklif politikası alanları zorunludur.");

        var offer = new ChannelOffer
        {
            Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = command.ConnectionId, VariantId = command.VariantId,
            ListPrice = decimal.Round(command.ListPrice, 4, MidpointRounding.ToEven), SalePrice = decimal.Round(command.SalePrice, 4, MidpointRounding.ToEven), Currency = currency,
            VatRate = decimal.Round(command.VatRate, 4, MidpointRounding.ToEven), VatInclusion = command.VatInclusion.Trim(), RoundingMode = command.RoundingMode.Trim(),
            SafetyStock = decimal.Round(command.SafetyStock, 4, MidpointRounding.ToEven), Status = command.Status.Trim(), PriceVersion = 1, Version = 1
        };
        db.ChannelOffers.Add(offer);
        db.ChannelPriceHistory.Add(new ChannelPriceHistory { Id = Guid.CreateVersion7(), TenantId = tenantId, OfferId = offer.Id, PriceVersion = 1, ListPrice = offer.ListPrice, SalePrice = offer.SalePrice, Currency = offer.Currency, Reason = command.Reason.Trim(), ActorSource = $"USER:{userId:D}", EffectiveAt = timeProvider.GetUtcNow() });
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult<ChannelOfferView>.Ok(Map(offer));
    }

    public async Task<ServiceResult<ChannelOfferView>> UpdateOfferAsync(Guid tenantId, Guid userId, Guid id, long expectedVersion, UpdateChannelOfferCommand command, CancellationToken cancellationToken)
    {
        var offer = await db.ChannelOffers.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, cancellationToken);
        if (offer is null) return NotFound<ChannelOfferView>();
        if (offer.Version != expectedVersion) return ServiceResult<ChannelOfferView>.Fail("CONCURRENCY_CONFLICT", $"Kayıt sürümü değişti; güncel sürüm v{offer.Version}.", 412);
        if (command.ListPrice < 0 || command.SalePrice < 0 || command.ListPrice < command.SalePrice) return Invalid<ChannelOfferView>("salePrice", "Fiyatlar negatif olamaz ve listPrice salePrice değerinden küçük olamaz.");
        var currency = command.Currency.Trim().ToUpperInvariant();
        try { _ = Money.Create(command.SalePrice, currency); } catch (ArgumentException) { return Invalid<ChannelOfferView>("currency", "Currency üç büyük harften oluşmalıdır."); }
        if (command.SafetyStock < 0 || command.VatRate < 0) return Invalid<ChannelOfferView>("safetyStock", "Safety stock ve VAT negatif olamaz.");
        if (string.IsNullOrWhiteSpace(command.Reason) || string.IsNullOrWhiteSpace(command.VatInclusion) || string.IsNullOrWhiteSpace(command.RoundingMode) || string.IsNullOrWhiteSpace(command.Status)) return Invalid<ChannelOfferView>("reason", "Fiyat değişikliği nedeni ve teklif politikası alanları zorunludur.");

        offer.ListPrice = decimal.Round(command.ListPrice, 4, MidpointRounding.ToEven);
        offer.SalePrice = decimal.Round(command.SalePrice, 4, MidpointRounding.ToEven);
        offer.Currency = currency;
        offer.VatRate = decimal.Round(command.VatRate, 4, MidpointRounding.ToEven);
        offer.VatInclusion = command.VatInclusion.Trim();
        offer.RoundingMode = command.RoundingMode.Trim();
        offer.SafetyStock = decimal.Round(command.SafetyStock, 4, MidpointRounding.ToEven);
        offer.Status = command.Status.Trim();
        offer.PriceVersion++;
        offer.Version++;
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
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult<ChannelOfferView>.Ok(Map(offer));
    }

    public Task<ServiceResult<Guid>> ValidateExternalSyncAsync(Guid tenantId, string operation, CancellationToken cancellationToken) =>
        Task.FromResult(ServiceResult<Guid>.Fail("USE_COMBINED_PRICE_INVENTORY", $"{operation} Trendyol'da ayrık gönderilmez; birleşik price-and-inventory işi kullanılmalıdır.", 422));

    public async Task<ServiceResult<Guid>> EnqueuePriceInventorySyncAsync(Guid tenantId, Guid connectionId, string idempotencyKey, string correlationId, CancellationToken cancellationToken)
    {
        var connection = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == connectionId && x.PlatformCode == "TRENDYOL" && x.Status == "ACTIVE", cancellationToken);
        if (connection is null) return ServiceResult<Guid>.Fail("ACTIVE_CONNECTION_REQUIRED", "Fiyat-stok gönderimi için ACTIVE Trendyol bağlantısı gerekir.", 422);
        var capabilities = await db.PlatformCapabilities.AsNoTracking().Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && (x.Code == F3Capabilities.InventoryWrite || x.Code == F3Capabilities.PriceWrite) && x.SupportLevel == CapabilitySupportLevel.Supported).Select(x => x.Code).ToListAsync(cancellationToken);
        if (!capabilities.Contains(F3Capabilities.InventoryWrite) || !capabilities.Contains(F3Capabilities.PriceWrite)) return ServiceResult<Guid>.Fail("CAPABILITY_UNKNOWN", "INVENTORY_WRITE ve PRICE_WRITE Stage/SIT kanıtı olmadan birleşik iş oluşturulmaz.", 422);
        if (!WritesEnabled(connection.SettingsJson)) return ServiceResult<Guid>.Fail("EXTERNAL_WRITES_DISABLED", "Global veya connection dış yazma anahtarı kapalı.", 422);
        var build = await new PriceInventoryComposer(db).BuildAsync(tenantId, connectionId, cancellationToken);
        if (!build.Succeeded) return ServiceResult<Guid>.Fail(build.Error!.Code, build.Error.Message, build.Error.Status, build.Error.FieldErrors);
        var draft = build.Value!; var dedup = $"price-inventory:{connectionId:N}:{draft.PayloadHash}";
        var existing = await db.IntegrationJobs.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.JobType == F3JobTypes.PriceInventorySync && x.JobDedupKey == dedup, cancellationToken);
        if (existing is not null) return ServiceResult<Guid>.Ok(existing.Id);
        var id = Guid.CreateVersion7(); var now = timeProvider.GetUtcNow();
        var payload = JsonSerializer.Serialize(new PriceInventoryJobPayload(id, connectionId, "SUBMIT", draft.PayloadHash, draft.PayloadJson, draft.Lines, null, null));
        db.IntegrationJobs.Add(new IntegrationJob { Id = id, TenantId = tenantId, ConnectionId = connectionId, JobType = F3JobTypes.PriceInventorySync, PayloadJson = payload, PayloadVersion = 1, PayloadHash = Hash(payload), JobDedupKey = dedup, EffectIdempotencyKey = $"{dedup}:{Hash(idempotencyKey.Trim())}", Status = JobStatus.Pending, AvailableAt = now, MaxAttempts = 10, CorrelationId = correlationId, CreatedAt = now, Version = 1 });
        await db.SaveChangesAsync(cancellationToken); return ServiceResult<Guid>.Ok(id);
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
    private static InventoryItemView Map(InventoryItem value, string sku) => new(value.Id, value.VariantId, sku, value.LocationCode, value.OnHand, value.Reserved, value.Available, value.ProjectionVersion, value.ReconciledAt, value.Version);
    private static ChannelOfferView Map(ChannelOffer value) => new(value.Id, value.ConnectionId, value.VariantId, value.ListPrice, value.SalePrice, value.Currency, value.VatRate, value.VatInclusion, value.RoundingMode, value.SafetyStock, value.Status, value.PriceVersion, value.Version);
    private static ServiceResult<T> Invalid<T>(string field, string message) => ServiceResult<T>.Fail("VALIDATION_FAILED", message, 422, new Dictionary<string, string[]> { [field] = [message] });
    private static ServiceResult<T> NotFound<T>() => ServiceResult<T>.Fail("RESOURCE_NOT_FOUND", "Kayıt bulunamadı.", 404);
    private static ServiceResult<T> Conflict<T>(string code, string message) => ServiceResult<T>.Fail(code, message, 409);
}
