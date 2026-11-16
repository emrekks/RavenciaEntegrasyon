using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace MarketplaceHub.Infrastructure.Persistence;

public sealed partial class MarketplaceJobProcessor
{
    private async Task<JobExecutionResult> DispatchStockProjection(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, Guid? jobId, CancellationToken cancellationToken)
    {
        Guid variantId; string trigger;
        try
        {
            using var payload = JsonDocument.Parse(payloadJson);
            variantId = payload.RootElement.GetProperty("variantId").GetGuid();
            // The change that queued the dispatch names why the send goes (the send log's trigger).
            trigger = PriceStockSendTriggers.FromSourceEvent(payload.RootElement.TryGetProperty("sourceEventId", out var source) && source.ValueKind == JsonValueKind.String ? source.GetString() : null);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException) { return JobExecutionResult.Blocked("STOCK_PROJECTION_PAYLOAD_INVALID", "Stok projection işi geçersiz payload içeriyor."); }
        var (blocked, decision) = await StockProjectionGateAsync(tenantId, connectionId, cancellationToken);
        if (blocked is not null) return blocked;
        if (decision == StockProjectionDispatchDecision.Skip) return JobExecutionResult.SuccessWithNote("AUTOMATIC_WRITES_OFF", "Otomatik fiyat-stok yazması kapalı; pazaryerine gönderilmedi.");
        if (!await db.ChannelOffers.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.VariantId == variantId && x.Status == "ACTIVE", cancellationToken)) return JobExecutionResult.Success();
        if (decision == StockProjectionDispatchDecision.Dispatch && await OrderFreshnessWaitAsync(tenantId, connectionId, jobId, cancellationToken) is { } wait) return wait;
        return await DispatchStockProjectionVariant(tenantId, connectionId, variantId, decision, correlationId, trigger, cancellationToken);
    }

    // Owner decision C: an automatic send waits (bounded) for an order read of the connection that started after the
    // change was queued, so an order placed before the change is taken off the panel before the absolute number goes out.
    // The job's own creation time is the change's time: the outbox queues it in the same save as the change.
    private async Task<JobExecutionResult?> OrderFreshnessWaitAsync(Guid tenantId, Guid connectionId, Guid? jobId, CancellationToken cancellationToken)
    {
        if (jobId is not { } id) return null;
        var changedAt = await db.IntegrationJobs.AsNoTracking().Where(x => x.Id == id).Select(x => (DateTimeOffset?)x.CreatedAt).SingleOrDefaultAsync(cancellationToken);
        var readsOrders = await db.ConnectionSyncPolicies.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == "ORDERS" && x.Enabled, cancellationToken);
        var cursor = await db.SyncCursors.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == OrderFreshnessGatePolicy.OrderReadCursor)
            .Select(x => new { x.LastAttemptAt, x.LastSuccessAt })
            .SingleOrDefaultAsync(cancellationToken);
        var maxWait = TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("MarketplaceSync:StockPush:OrderFreshnessMaxWaitSeconds", (int)OrderFreshnessGatePolicy.DefaultMaxWait.TotalSeconds), 0, 3_600));
        var now = timeProvider.GetUtcNow();
        var decision = OrderFreshnessGatePolicy.Decide(readsOrders && cursor is not null, changedAt, cursor?.LastAttemptAt, cursor?.LastSuccessAt, now, maxWait);
        // Webhook first: healthy order notifications cover the change once they have had time to arrive and every one
        // received has been ingested, without waiting for the (stretched) scheduled read.
        if (decision == OrderFreshnessDecision.Wait && OrderFreshnessGatePolicy.NotificationGraceElapsed(changedAt, now)
            && await ScheduledJobProducer.WebhookHealthyAsync(db, tenantId, connectionId, now, cancellationToken))
        {
            var notificationsPending = await db.IntegrationJobs.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId
                && (x.JobType == MarketplaceJobTypes.WebhookIngest || x.JobType == MarketplaceJobTypes.ShopifyWebhookIngest || x.JobType == MarketplaceJobTypes.HepsiburadaWebhookIngest)
                && (x.Status == JobStatus.Pending || x.Status == JobStatus.Leased || x.Status == JobStatus.RetryScheduled), cancellationToken);
            decision = OrderFreshnessGatePolicy.Decide(readsOrders && cursor is not null, changedAt, cursor?.LastAttemptAt, cursor?.LastSuccessAt, now, maxWait, webhookHealthy: true, notificationsPending);
        }
        return decision == OrderFreshnessDecision.Wait
            ? JobExecutionResult.Deferred(OrderFreshnessGatePolicy.WaitCode, OrderFreshnessGatePolicy.WaitMessage, OrderFreshnessGatePolicy.RecheckDelay)
            : null;
    }

    // Whether an automatic stock write may go out on the connection at all; read once per reconcile pass
    // instead of once per candidate. A write policy without a row is off (owner decision A).
    private async Task<(JobExecutionResult? Blocked, StockProjectionDispatchDecision Decision)> StockProjectionGateAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken)
    {
        var connection = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == connectionId, cancellationToken);
        if (connection is null) return (JobExecutionResult.Blocked("CONNECTION_NOT_FOUND", "Bağlantı bulunamadı."), StockProjectionDispatchDecision.Skip);
        // Stock projection is an automatic write path. Stage manual writes remain
        // available, but an automatic projection must never enqueue a remote write
        // while the explicit external-write switch is off.
        if (!WritesEnabled(connection.SettingsJson)) return (null, StockProjectionDispatchDecision.Skip);
        return (null, StockProjectionOutboxPolicy.Decide(
            await StrictWritePolicyEnabledAsync(tenantId, connectionId, MarketplaceExternalWritePolicies.Stock, cancellationToken),
            await StrictWritePolicyEnabledAsync(tenantId, connectionId, MarketplaceExternalWritePolicies.Price, cancellationToken)));
    }

    private async Task<JobExecutionResult> DispatchStockProjectionVariant(Guid tenantId, Guid connectionId, Guid variantId, StockProjectionDispatchDecision decision, string correlationId, string trigger, CancellationToken cancellationToken)
    {
        if (decision == StockProjectionDispatchDecision.HoldForPricePolicy)
        {
            // The request would carry the price too; sending it with the price policy off could
            // overwrite a price the seller manages on the marketplace. The stock waits, visibly.
            await RecordIssue(tenantId, $"stock-projection-price-policy:{connectionId:N}", "STOCK_WRITE_HELD_PRICE_POLICY_DISABLED", "Stok değişikliği pazaryerine gönderilmedi: fiyat-stok isteği fiyatı da taşıdığı için fiyat dış yazma akışı da açık olmalı. Fiyat akışı açıldığında stok kontrol taramasıyla gönderilir.", cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            return JobExecutionResult.Success();
        }
        var composer = new PriceInventoryComposer(db);
        var build = await composer.BuildAsync(tenantId, connectionId, cancellationToken, variantId, automatic: true);
        if (!build.Succeeded)
        {
            var code = build.Error!.Code;
            if (code == "INVENTORY_REQUIRED")
            {
                await RecordIssue(tenantId, $"price-inventory-inventory-required:{connectionId:N}", code, build.Error.Message, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
            }
            // An offer the user has to fix stays visible as an issue instead of a blocked job (G: no silent freeze).
            if (PriceInventoryOfferIssues.IsOfferProblem(code))
            {
                foreach (var invalid in composer.InvalidOffers)
                    await RecordIssue(tenantId, PriceInventoryOfferIssues.Key(connectionId, invalid.VariantId), invalid.Code, invalid.Message, cancellationToken);
                if (composer.InvalidOffers.Count == 0) await RecordIssue(tenantId, PriceInventoryOfferIssues.Key(connectionId, variantId), code, build.Error.Message, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
                return JobExecutionResult.Success();
            }
            if (!StockProjectionOutboxPolicy.IsSettledBuildFailure(code)) return JobExecutionResult.Blocked(code, build.Error.Message);
            // Already sent or not live on this connection: nothing to send now.
            return JobExecutionResult.Success();
        }
        return await QueueAutomaticPushAsync(tenantId, connectionId, build.Value!, variantId, correlationId, trigger, cancellationToken);
    }

    // One automatic PRICE_INVENTORY_SYNC job for a built draft (one variant's change or a reconcile batch). Offers the
    // build left out are reported; the offers it carries are fine again, so their offer issues close.
    private async Task<JobExecutionResult> QueueAutomaticPushAsync(Guid tenantId, Guid connectionId, PriceInventoryDraft draft, Guid? variantId, string correlationId, string trigger, CancellationToken cancellationToken)
    {
        foreach (var invalid in draft.InvalidOffers)
            await RecordIssue(tenantId, PriceInventoryOfferIssues.Key(connectionId, invalid.VariantId), invalid.Code, invalid.Message, cancellationToken);
        await ResolveOfferIssuesAsync(tenantId, connectionId, draft.Lines.Select(line => line.VariantId).Distinct().ToArray(), cancellationToken);
        var dedup = PriceInventoryOutboxPolicy.DedupKey(connectionId, draft.Lines);
        var now = timeProvider.GetUtcNow();
        // A failed job of the same values gives up its key in the same save as the new job (Q-1/K-1).
        var existing = await db.IntegrationJobs.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.JobType == MarketplaceJobTypes.PriceInventorySync && x.JobDedupKey == dedup, cancellationToken);
        if (existing is not null)
        {
            if (!PriceInventoryOutboxPolicy.AutomaticSendMayRetire(existing.Status, existing.LastErrorCode, existing.CompletedAt, now))
            {
                await db.SaveChangesAsync(cancellationToken);
                return JobExecutionResult.Success();
            }
            existing.JobDedupKey = PriceInventoryOutboxPolicy.RetiredDedupKey(dedup, existing.Id); existing.Version++;
        }
        var id = Guid.CreateVersion7();
        var jobPayload = JsonSerializer.Serialize(new PriceInventoryJobPayload(id, connectionId, "SUBMIT", draft.PayloadHash, draft.PayloadJson, draft.Lines, null, null, variantId, Automatic: true, Trigger: trigger));
        var writeDelay = await ExternalWriteDelaySecondsAsync(tenantId, connectionId, MarketplaceExternalWritePolicies.Stock, cancellationToken);
        Jobs.JobEnqueuer.Add(db, new IntegrationJobRequest(tenantId, connectionId, MarketplaceJobTypes.PriceInventorySync, jobPayload, dedup, correlationId, now.AddSeconds(writeDelay)) { Id = id, Priority = JobPriorityPolicy.For(MarketplaceJobTypes.PriceInventorySync, userInitiated: true), MaxAttempts = 10, CreatedAt = now });
        await db.SaveChangesAsync(cancellationToken);
        return JobExecutionResult.Success();
    }

    private async Task ResolveOfferIssuesAsync(Guid tenantId, Guid connectionId, IReadOnlyCollection<Guid> variantIds, CancellationToken cancellationToken)
    {
        if (variantIds.Count == 0) return;
        var keys = variantIds.Select(variantId => PriceInventoryOfferIssues.Key(connectionId, variantId)).ToArray();
        var now = timeProvider.GetUtcNow();
        foreach (var issue in await db.OperationalIssues.Where(x => x.TenantId == tenantId && keys.Contains(x.DedupeKey) && x.Status != IssueStatus.Resolved).ToListAsync(cancellationToken))
        {
            issue.Status = IssueStatus.Resolved;
            issue.ResolvedAt = now;
        }
    }

    private bool WritesEnabled(string settingsJson)
    {
        if (!configuration.GetValue<bool>("FeatureFlags:ExternalWrites")) return false;
        return ConnectionSettings.ExternalWritesEnabled(settingsJson);
    }

    private async Task<bool> ExternalWriteMasterAllowedAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken)
    {
        var connection = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == connectionId, cancellationToken);
        return connection is not null && (string.Equals(connection.Environment, "STAGE", StringComparison.OrdinalIgnoreCase) || WritesEnabled(connection.SettingsJson));
    }

    // The PTT AVM MANUAL_WRITE switch, checked after the master switch (the callers check that first). A missing row
    // refuses the write: it is not read as permission.
    private async Task<bool> PttAvmManualWritesAllowedAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken)
    {
        var enabled = await db.ConnectionSyncPolicies.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == MarketplaceExternalWritePolicies.Manual)
            .Select(x => (bool?)x.Enabled)
            .SingleOrDefaultAsync(cancellationToken);
        return PttAvmManualWritePolicy.Decide(masterWritesEnabled: true, enabled) == PttAvmManualWriteDecision.Allowed;
    }

    private async Task<bool> HasHepsiburadaPublicationEvidenceAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken)
    {
        // A STAGE (SIT) connection does not require PRODUCT_WRITE: the SIT product import is what produces
        // that evidence. PRICE_WRITE/INVENTORY_WRITE stay required; PRODUCTION requires all three.
        var connection = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == connectionId && x.PlatformCode == "HEPSIBURADA", cancellationToken);
        if (connection is null) return false;
        return await HasHepsiburadaWriteEvidenceAsync(tenantId, connectionId, cancellationToken, HepsiburadaStageCertificationPolicy.PublicationEvidenceCapabilities(connection));
    }

    private async Task<bool> HasHepsiburadaWriteEvidenceAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken, params string[] required)
    {
        var connection = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == connectionId && x.PlatformCode == "HEPSIBURADA", cancellationToken);
        if (connection is null || required.Length == 0) return false;
        var capabilities = await db.PlatformCapabilities.AsNoTracking().Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && required.Contains(x.Code)).ToListAsync(cancellationToken);
        return required.All(code => CapabilityEvidencePolicy.IsVerifiedWriteCapability(capabilities.SingleOrDefault(x => x.Code == code), connection, code));
    }

    private async Task<bool> ExternalWriteAllowedAsync(Guid tenantId, Guid connectionId, string resourceType, CancellationToken cancellationToken)
    {
        var connection = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == connectionId, cancellationToken);
        if (connection is null) return false;
        if (!string.Equals(connection.Environment, "STAGE", StringComparison.OrdinalIgnoreCase) && !WritesEnabled(connection.SettingsJson)) return false;
        return await ExternalWritePolicyEnabledAsync(tenantId, connectionId, resourceType, cancellationToken);
    }

    private async Task<bool> ExternalWritePolicyEnabledAsync(Guid tenantId, Guid connectionId, string resourceType, CancellationToken cancellationToken)
    {
        var policy = await db.ConnectionSyncPolicies.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == resourceType, cancellationToken);
        return policy is null || policy.Enabled;
    }

    private async Task<bool> StrictWritePolicyEnabledAsync(Guid tenantId, Guid connectionId, string resourceType, CancellationToken cancellationToken) =>
        await db.ConnectionSyncPolicies.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == resourceType && x.Enabled, cancellationToken);

    private async Task<int> ExternalWriteDelaySecondsAsync(Guid tenantId, Guid connectionId, string resourceType, CancellationToken cancellationToken)
    {
        var policy = await db.ConnectionSyncPolicies.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == resourceType, cancellationToken);
        return policy is null ? 0 : Math.Clamp(policy.IntervalSeconds, 0, 86_400);
    }

    // F: the catch-up for every platform. One pass builds one batch of the offers whose price or stock was not sent
    // (the platform's own batch limit: Trendyol 1,000, N11 and Pazarama their request caps, Hepsiburada its upload
    // limit, PTT AVM 1,000 products) and queues one push, so the platform's request rate is one request per pass.
    // A pass waits while an automatic push of the connection is still on its way, and (decision C) until an order read
    // started after the pass was queued has finished. Offers it cannot send are reported, not a reason to stop.
    private async Task<JobExecutionResult> ReconcileStock(Guid tenantId, Guid connectionId, string correlationId, Guid? jobId, CancellationToken cancellationToken)
    {
        var (blocked, decision) = await StockProjectionGateAsync(tenantId, connectionId, cancellationToken);
        if (blocked is not null || decision == StockProjectionDispatchDecision.Skip)
            return JobExecutionResult.SuccessWithNote("AUTOMATIC_WRITES_OFF", "Otomatik fiyat-stok yazması kapalı; stok uzlaştırması gönderim yapmadı.");
        if (decision == StockProjectionDispatchDecision.HoldForPricePolicy)
        {
            // Nothing goes out while the price policy is off: one held candidate is enough to show the issue.
            var held = await db.ChannelOffers.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.Status == "ACTIVE")
                .OrderBy(x => x.VariantId).Select(x => (Guid?)x.VariantId).FirstOrDefaultAsync(cancellationToken);
            if (held is { } heldVariant) await DispatchStockProjectionVariant(tenantId, connectionId, heldVariant, decision, correlationId, PriceStockSendTriggers.Reconcile, cancellationToken);
            return JobExecutionResult.Success();
        }
        if (await AutomaticPushInFlightAsync(tenantId, connectionId, cancellationToken))
            return JobExecutionResult.SuccessWithNote("PRICE_INVENTORY_PUSH_IN_FLIGHT", "Bağlantının önceki fiyat-stok gönderimi sürüyor; uzlaştırma bir sonraki turda devam edecek.");
        if (await OrderFreshnessWaitAsync(tenantId, connectionId, jobId, cancellationToken) is { } wait) return wait;

        var composer = new PriceInventoryComposer(db);
        var build = await composer.BuildAsync(tenantId, connectionId, cancellationToken, automatic: true);
        if (!build.Succeeded)
        {
            var code = build.Error!.Code;
            if (code == "INVENTORY_REQUIRED")
                await RecordIssue(tenantId, $"price-inventory-inventory-required:{connectionId:N}", code, build.Error.Message, cancellationToken);
            // Every offer the pass could send is one the user has to fix: each is reported by itself.
            foreach (var invalid in composer.InvalidOffers)
                await RecordIssue(tenantId, PriceInventoryOfferIssues.Key(connectionId, invalid.VariantId), invalid.Code, invalid.Message, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            if (StockProjectionOutboxPolicy.IsSettledBuildFailure(code) || code == "INVENTORY_REQUIRED" || PriceInventoryOfferIssues.IsOfferProblem(code)) return JobExecutionResult.Success();
            return JobExecutionResult.Blocked(code, build.Error.Message);
        }
        if (build.Value!.MissingInventorySkus.Count > 0)
            await RecordIssue(tenantId, $"price-inventory-inventory-required:{connectionId:N}", "INVENTORY_REQUIRED", PriceInventoryComposer.MissingInventoryMessage(build.Value.MissingInventorySkus), cancellationToken);
        // ReconciledAt is an observation watermark, not an enqueue timestamp. It is updated only when a remote catalog
        // read observes stock; a disabled, blocked, or merely queued write remains visible as stale until then.
        return await QueueAutomaticPushAsync(tenantId, connectionId, build.Value, null, correlationId, PriceStockSendTriggers.Reconcile, cancellationToken);
    }

    // An automatic price/stock push of the connection that is queued, running or retrying.
    private Task<bool> AutomaticPushInFlightAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        db.IntegrationJobs.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId
            && x.JobType == MarketplaceJobTypes.PriceInventorySync && x.TriggerType != "user"
            && (x.Status == JobStatus.Pending || x.Status == JobStatus.Leased || x.Status == JobStatus.RetryScheduled), cancellationToken);
}
