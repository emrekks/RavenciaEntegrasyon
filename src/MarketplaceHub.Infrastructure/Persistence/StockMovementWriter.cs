using MarketplaceHub.Application;
using MarketplaceHub.Domain;

namespace MarketplaceHub.Infrastructure.Persistence;

// How a stock movement rounds (SIPARIS-STOK-8). The callers grew apart and each keeps what it did; unifying them is an
// owner decision, not a refactor.
internal enum StockMovementRounding
{
    // Nothing is rounded in code; PostgreSQL rounds the stored 4 places half away from zero (return restocks).
    None,
    // The new on-hand is rounded to 4 places (to even); the ledger keeps the delta as planned (order projection).
    OnHand,
    // The new on-hand and the ledger delta are both rounded to 4 places (to even) (manual adjustment).
    OnHandAndLedger,
}

// Where a ledger row comes from and the key that makes it unique ((TenantId, IdempotencyKey) is a unique index, so the
// same movement can never be written twice: a second write with the same key fails the save).
internal readonly record struct StockLedgerSource(
    string MovementType,
    string SourceType,
    string SourceId,
    string SourceEventId,
    string IdempotencyKey,
    DateTimeOffset OccurredAt,
    DateTimeOffset RecordedAt,
    string CorrelationId,
    Guid? ActorUserId = null);

// SIPARIS-STOK-8: the one place that changes a MAIN inventory item and writes its stock ledger row in the same unit of
// work. The caller owns the transaction, the save, any key check before the write, and the dispatch queue
// (StockProjectionOutbox): the change returned names the variant, the new projection version and the source event the
// dispatch carries. Automatic marketplace writes stay as the outbox decides (off by owner choice); nothing here sends.
internal static class StockMovementWriter
{
    // On-hand moves by delta. Available follows (InventoryProjection); the projection
    // and row versions move once.
    public static StockProjectionChange Move(AppDbContext db, InventoryItem item, decimal delta, StockMovementRounding rounding, StockLedgerSource source, string? dispatchSourceEventId = null)
    {
        var onHand = NextOnHand(item.OnHand, delta, rounding);
        var ledgerDelta = rounding == StockMovementRounding.OnHandAndLedger ? Round(delta) : delta;
        return Apply(db, item, onHand, ledgerDelta, source, dispatchSourceEventId);
    }

    // On-hand is set to target (already rounded by the caller); the ledger takes the difference.
    public static StockProjectionChange SetOnHand(AppDbContext db, InventoryItem item, decimal target, StockLedgerSource source, string? dispatchSourceEventId = null) =>
        Apply(db, item, target, target - item.OnHand, source, dispatchSourceEventId);

    // The on-hand a move would leave, for a caller that refuses a negative result before writing.
    public static decimal NextOnHand(decimal onHand, decimal delta, StockMovementRounding rounding) =>
        rounding == StockMovementRounding.None ? onHand + delta : Round(onHand + delta);

    // Reserved units released (no on-hand change). The ledger row is the caller's (the release and its shortfall marker).
    public static StockProjectionChange ReleaseReserved(InventoryItem item, decimal quantity, string dispatchSourceEventId)
    {
        item.Reserved = Math.Max(0m, item.Reserved - quantity);
        item.Available = InventoryProjection.Available(item.OnHand, item.Reserved);
        item.ProjectionVersion++;
        item.Version++;
        return new StockProjectionChange(item.VariantId, item.ProjectionVersion, dispatchSourceEventId);
    }

    // A ledger row that moves no stock: an order line's baseline, shortfall marker, shipment progress, a reservation
    // release, or the opening row of an item created with its number.
    public static StockLedgerEntry Record(AppDbContext db, Guid tenantId, Guid inventoryItemId, decimal quantity, StockLedgerSource source)
    {
        var row = Row(tenantId, inventoryItemId, quantity, source);
        db.StockLedgerEntries.Add(row);
        return row;
    }

    // The open "stock could not give" notices of the items close: a stock count set by hand answers them. Only the
    // manual adjustment and the stock count do this today; returns and marketplace reads do not (SIPARIS-STOK-8, kept).
    public static Task CloseShortfallNoticesAsync(AppDbContext db, Guid tenantId, IReadOnlyCollection<Guid> inventoryItemIds, DateTimeOffset now, CancellationToken cancellationToken) =>
        StockShortfallNotices.ResolveForInventoryItemsAsync(db, tenantId, inventoryItemIds, now, cancellationToken);

    private static StockProjectionChange Apply(AppDbContext db, InventoryItem item, decimal onHand, decimal ledgerDelta, StockLedgerSource source, string? dispatchSourceEventId)
    {
        item.OnHand = onHand;
        item.Available = InventoryProjection.Available(item.OnHand, item.Reserved);
        item.ProjectionVersion++;
        item.Version++;
        db.StockLedgerEntries.Add(Row(item.TenantId, item.Id, ledgerDelta, source));
        return new StockProjectionChange(item.VariantId, item.ProjectionVersion, dispatchSourceEventId ?? source.SourceEventId);
    }

    private static StockLedgerEntry Row(Guid tenantId, Guid inventoryItemId, decimal quantity, StockLedgerSource source) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        InventoryItemId = inventoryItemId,
        MovementType = source.MovementType,
        QuantityDelta = quantity,
        SourceType = source.SourceType,
        SourceId = source.SourceId,
        SourceEventId = source.SourceEventId,
        IdempotencyKey = source.IdempotencyKey,
        OccurredAt = source.OccurredAt,
        RecordedAt = source.RecordedAt,
        ActorUserId = source.ActorUserId,
        CorrelationId = source.CorrelationId
    };

    private static decimal Round(decimal value) => decimal.Round(value, 4, MidpointRounding.ToEven);
}
