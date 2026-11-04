using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

// "Stoğa Ekle" chosen with a return approval is kept on the decision (ReturnDecision.StockLinesJson, PENDING) and
// applied here once the approval is known: at once when the marketplace accepted the panel's call, otherwise when a
// later read of the claim (read-back, effect check, sync) shows the lines approved. Each line goes back to stock at
// most once: its disposition and RETURN_PASS ledger row carry the key "{decision key}:stock:{line}", which is unique,
// and a line never takes more than its quantity minus what was already put back. A rejected or cancelled line, or a
// decision that clearly did not reach the marketplace, drops the choice without stock.
internal static class ReturnPendingStock
{
    public const string ImmediateReason = "İade kabulünde stoğa eklendi";
    public const string ConfirmedLaterReason = "Pazaryeri onayı kesinleşince stoğa eklendi";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string Serialize(IReadOnlyCollection<ReturnPendingStockLine> lines) => JsonSerializer.Serialize(lines, Json);

    public static IReadOnlyList<ReturnPendingStockLine> Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<ReturnPendingStockLine>>(json, Json) ?? []; }
        catch (JsonException) { return []; }
    }

    public static string StockKey(string decisionKey, Guid returnLineId) => $"{decisionKey}:stock:{returnLineId:N}";

    // Settles every pending choice of the claim against what the claim says now (after a read of it).
    public static async Task SettleClaimAsync(AppDbContext db, Guid tenantId, Guid claimId, string correlationId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var decisions = await db.ReturnDecisions
            .Where(x => x.TenantId == tenantId && x.ClaimId == claimId && x.StockStatus == ReturnPendingStockPolicy.Pending)
            .OrderBy(x => x.CreatedAt)
            .ToListAsync(cancellationToken);
        foreach (var decision in decisions) await SettleAsync(db, decision, force: false, correlationId, now, cancellationToken);
    }

    // force: the marketplace accepted this decision's own approval call, so its lines are approved.
    public static async Task SettleAsync(AppDbContext db, ReturnDecision decision, bool force, string correlationId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (decision.StockStatus != ReturnPendingStockPolicy.Pending) return;
        now = now.ToUniversalTime();
        var tenantId = decision.TenantId;
        var claim = db.ReturnClaims.Local.FirstOrDefault(x => x.TenantId == tenantId && x.Id == decision.ClaimId)
            ?? await db.ReturnClaims.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == decision.ClaimId, cancellationToken);
        if (claim is null) return;
        var plan = Read(decision.StockLinesJson);
        var lineIds = plan.Select(x => x.ReturnLineId).ToArray();
        // Tracked: a line status set in this unit of work (the instant decision, a claim read) is the one judged.
        var lines = await db.ReturnLines.Where(x => x.TenantId == tenantId && x.ClaimId == claim.Id && lineIds.Contains(x.Id)).ToListAsync(cancellationToken);
        var changes = new List<StockProjectionChange>();
        var waiting = false;
        var applied = false;
        foreach (var planned in plan)
        {
            var line = lines.FirstOrDefault(x => x.Id == planned.ReturnLineId);
            if (line is null) continue;
            var verdict = force ? ReturnPendingStockVerdict.Apply : ReturnPendingStockPolicy.Settle(decision.Status, decision.ErrorCode, claim.Status, line.RawStatus);
            if (verdict == ReturnPendingStockVerdict.Wait) { waiting = true; continue; }
            if (verdict == ReturnPendingStockVerdict.Drop) continue;
            var (lineApplied, change) = await ApplyLineAsync(db, decision, claim, line, planned.Quantity, force ? ImmediateReason : ConfirmedLaterReason, correlationId, now, cancellationToken);
            applied |= lineApplied;
            if (change is not null) changes.Add(change);
        }
        // Restocked units go to every connection selling the variant, committed with the restock.
        await StockProjectionOutbox.EnqueueAsync(db, tenantId, changes, now, cancellationToken);
        if (changes.Count > 0)
            db.AuditLogs.Add(new AuditLog { TenantId = tenantId, ActorUserId = decision.ActorUserId, Action = "RETURN_STOCK_DISPOSITION", TargetType = "ReturnClaim", TargetId = claim.Id.ToString("D"), Reason = force ? ImmediateReason : ConfirmedLaterReason, CorrelationId = correlationId, CreatedAt = now });
        if (waiting) return;
        decision.StockStatus = applied ? ReturnPendingStockPolicy.Applied : ReturnPendingStockPolicy.Dropped;
        decision.StockSettledAt = now;
    }

    // Drops a pending choice whose decision ended without reaching the marketplace.
    public static void Drop(ReturnDecision decision, DateTimeOffset now)
    {
        if (decision.StockStatus != ReturnPendingStockPolicy.Pending) return;
        decision.StockStatus = ReturnPendingStockPolicy.Dropped;
        decision.StockSettledAt = now.ToUniversalTime();
    }

    // Applied: the line is back in stock (now or before, under this decision's key). False when nothing could be put
    // back (already received in full, or no MAIN stock row for its variant any more).
    private static async Task<(bool Applied, StockProjectionChange? Change)> ApplyLineAsync(AppDbContext db, ReturnDecision decision, ReturnClaim claim, ReturnLine line, decimal? plannedUnits, string reason, string correlationId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var tenantId = decision.TenantId;
        var stockKey = StockKey(decision.IdempotencyKey, line.Id);
        var added = db.ChangeTracker.Entries<ReturnStockDisposition>().Where(x => x.State == EntityState.Added).Select(x => x.Entity).Where(x => x.TenantId == tenantId && x.ReturnLineId == line.Id).ToList();
        if (added.Any(x => x.IdempotencyKey == stockKey)
            || await db.ReturnStockDispositions.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.IdempotencyKey == stockKey, cancellationToken))
            return (true, null);
        var alreadyReceived = await db.ReturnStockDispositions.AsNoTracking().Where(x => x.TenantId == tenantId && x.ReturnLineId == line.Id).SumAsync(x => x.Quantity, cancellationToken)
            + added.Sum(x => x.Quantity);
        var receivable = line.Quantity - alreadyReceived;
        if (plannedUnits is { } units) receivable = Math.Min(receivable, units);
        if (receivable <= 0) return (false, null);
        var variantId = await db.OrderLines.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == line.OrderLineId).Select(x => x.VariantId).SingleOrDefaultAsync(cancellationToken);
        if (variantId is null) return (false, null);
        var item = db.InventoryItems.Local.FirstOrDefault(x => x.TenantId == tenantId && x.VariantId == variantId && x.LocationCode == "MAIN")
            ?? await db.InventoryItems.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.VariantId == variantId && x.LocationCode == "MAIN", cancellationToken);
        if (item is null) return (false, null);
        db.ReturnStockDispositions.Add(new ReturnStockDisposition { Id = Guid.CreateVersion7(), TenantId = tenantId, ClaimId = claim.Id, ReturnLineId = line.Id, InventoryItemId = item.Id, Disposition = ReturnStockDispositionKind.Pass, Quantity = receivable, IdempotencyKey = stockKey, Reason = reason, ActorUserId = decision.ActorUserId, CreatedAt = now });
        item.OnHand += receivable;
        item.Available = Math.Max(0, item.OnHand - item.Reserved);
        item.ProjectionVersion++;
        item.Version++;
        db.StockLedgerEntries.Add(new StockLedgerEntry { Id = Guid.CreateVersion7(), TenantId = tenantId, InventoryItemId = item.Id, MovementType = "RETURN_PASS", QuantityDelta = receivable, SourceType = "RETURN_CLAIM", SourceId = claim.ExternalClaimId, SourceEventId = stockKey, IdempotencyKey = stockKey, OccurredAt = now, RecordedAt = now, ActorUserId = decision.ActorUserId, CorrelationId = correlationId });
        return (true, new StockProjectionChange(item.VariantId, item.ProjectionVersion, stockKey));
    }
}
