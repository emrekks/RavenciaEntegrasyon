using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

public sealed partial class InventoryService
{
    // "Platformlarda Güncelle" price and stock on PTT AVM: the price-stock send of the card's PTT AVM product (the
    // composer takes the whole model family, every colour card, since they are one PTT AVM product). PTT AVM's request
    // carries price and stock together (UpdateProductsStockPrice), so a pick of one alone is refused rather than sending
    // the other unasked; the dialog bundles them. A send from the panel is manual: the write switches and the
    // connection's MANUAL_WRITE policy are the gate, as for the per-code send (EnqueuePriceInventorySyncAsync).
    private async Task<ServiceResult<Guid>> EnqueuePttAvmProductPriceInventoryAsync(Guid tenantId, Guid productId, Guid connectionId, string idempotencyKey, string correlationId, bool price, bool stock, CancellationToken cancellationToken)
    {
        if (!price || !stock) return ServiceResult<Guid>.Fail("PRICE_INVENTORY_FIELDS_UNSUPPORTED", "PttAVM fiyatı ve stoğu aynı istekte alır; ikisini birlikte seçin.", 422);
        var connection = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == connectionId && x.PlatformCode == "PTTAVM" && (x.Status == "ACTIVE" || x.Status == "VERIFIED"), cancellationToken);
        if (connection is null) return ServiceResult<Guid>.Fail("ACTIVE_CONNECTION_REQUIRED", "Ürüne özel fiyat-stok gönderimi için aktif veya doğrulanmış PttAVM bağlantısı gerekir.", 422);
        if (!IntegrationRuntimePolicy.IsSupportedEnvironment(connection)) return ServiceResult<Guid>.Fail("ENVIRONMENT_INVALID", "Fiyat-stok gönderimi yalnız test ya da canlı mağaza bağlantısında yapılabilir.", 422);
        if (PanelWritesBlocked(connection) is { } blocked) return blocked;
        var manualPolicy = await db.ConnectionSyncPolicies.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == MarketplaceExternalWritePolicies.Manual)
            .Select(x => (bool?)x.Enabled)
            .SingleOrDefaultAsync(cancellationToken);
        if (PttAvmManualWritePolicy.Decide(masterWritesEnabled: true, manualPolicy) != PttAvmManualWriteDecision.Allowed)
            return ServiceResult<Guid>.Fail(PttAvmManualWritePolicy.DisabledCode, PttAvmManualWritePolicy.DisabledMessage, 422);
        var build = await new PriceInventoryComposer(db).BuildAsync(tenantId, connectionId, cancellationToken, productId: productId);
        if (!build.Succeeded) return ServiceResult<Guid>.Fail(build.Error!.Code, build.Error.Message, build.Error.Status, build.Error.FieldErrors);
        var draft = build.Value!;
        var dedup = PriceInventoryOutboxPolicy.DedupKey(connectionId, draft.Lines);
        var existing = await StandingPriceInventoryJobAsync(tenantId, dedup, cancellationToken);
        if (existing is not null) return ServiceResult<Guid>.Ok(existing.Id);
        var id = Guid.CreateVersion7();
        var now = timeProvider.GetUtcNow();
        var payload = JsonSerializer.Serialize(new PriceInventoryJobPayload(id, connectionId, "SUBMIT", draft.PayloadHash, draft.PayloadJson, draft.Lines, null, null, null, productId, Manual: true, Trigger: PriceStockSendTriggers.PanelSend));
        Jobs.JobEnqueuer.Add(db, new IntegrationJobRequest(tenantId, connectionId, MarketplaceJobTypes.PriceInventorySync, payload, dedup, correlationId, now) { Id = id, EffectIdempotencyKey = $"{dedup}:{Hash(idempotencyKey.Trim())}", Priority = JobPriorityPolicy.ForUser(MarketplaceJobTypes.PriceInventorySync), TriggerType = "user", MaxAttempts = 10, CreatedAt = now });
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult<Guid>.Ok(id);
    }
}
