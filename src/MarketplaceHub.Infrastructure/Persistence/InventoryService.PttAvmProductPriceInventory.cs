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
        if (!price || !stock) return ServiceResult<Guid>.Fail("PRICE_INVENTORY_FIELDS_UNSUPPORTED", "PTT AVM fiyatı ve stoğu aynı istekte alır; ikisini birlikte seçin.", 422);
        var connection = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == connectionId && x.PlatformCode == "PTTAVM" && (x.Status == "ACTIVE" || x.Status == "VERIFIED"), cancellationToken);
        if (connection is null) return ServiceResult<Guid>.Fail("ACTIVE_CONNECTION_REQUIRED", "Ürüne özel fiyat-stok gönderimi için aktif veya doğrulanmış PTT AVM bağlantısı gerekir.", 422);
        if (!IntegrationRuntimePolicy.IsSupportedEnvironment(connection)) return ServiceResult<Guid>.Fail("ENVIRONMENT_INVALID", "Fiyat-stok gönderimi yalnız STAGE veya PRODUCTION bağlantısında çalışır.", 422);
        if (!WritesEnabled(connection.SettingsJson)) return ServiceResult<Guid>.Fail("EXTERNAL_WRITES_DISABLED", "Global veya connection dış yazma anahtarı kapalı.", 422);
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
        db.IntegrationJobs.Add(new IntegrationJob { Id = id, TenantId = tenantId, ConnectionId = connectionId, JobType = MarketplaceJobTypes.PriceInventorySync, PayloadJson = payload, PayloadVersion = 1, PayloadHash = Hash(payload), JobDedupKey = dedup, EffectIdempotencyKey = $"{dedup}:{Hash(idempotencyKey.Trim())}", Priority = JobPriorityPolicy.ForUser(MarketplaceJobTypes.PriceInventorySync), TriggerType = "user", Status = JobStatus.Pending, AvailableAt = now, MaxAttempts = 10, CorrelationId = correlationId, CreatedAt = now, Version = 1 });
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult<Guid>.Ok(id);
    }
}
