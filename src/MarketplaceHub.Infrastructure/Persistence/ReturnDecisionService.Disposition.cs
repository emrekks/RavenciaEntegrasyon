using System.Data;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;
using static MarketplaceHub.Infrastructure.Persistence.InvoiceLabelPolicy;
using static MarketplaceHub.Infrastructure.Persistence.OrderSnapshotReader;
using static MarketplaceHub.Infrastructure.Persistence.SalesResults;

namespace MarketplaceHub.Infrastructure.Persistence;

internal sealed partial class ReturnDecisionService
{
    public async Task<ServiceResult<ReturnDetailView>> ApplyDispositionAsync(Guid tenantId, Guid userId, Guid claimId, ReturnDispositionCommand command, string idempotencyKey, string correlationId, CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<ReturnStockDispositionKind>(command.Disposition, true, out var disposition)) return Invalid<ReturnDetailView>("disposition", "Disposition PASS, QUARANTINE, DAMAGED veya NOT_RECEIVED olmalıdır.");
        if (command.Quantity <= 0 || string.IsNullOrWhiteSpace(command.Reason)) return Invalid<ReturnDetailView>("quantity", "Pozitif quantity ve açıklama zorunludur.");

        // The return line is locked before anything is read, and held until the disposition commits: two operators,
        // or an operator and the automatic "Stoğa Ekle" settlement (ReturnPendingStock, which takes the same lock),
        // put the line's units somewhere one after the other, each reading what the other already did (YENİ-04).
        // Read committed, so the read after the lock sees the other's committed rows; a serializable snapshot taken
        // before the wait would not.
        // The inventory item carries a version: a sale or another stock change on the same item between the read and the
        // save is a concurrency conflict. The whole attempt (lock, reads, the delta on the fresh on-hand) runs again, a few
        // times, and a conflict that keeps coming back is a 409 STOCK_CHANGED instead of a server error (scan 13 Nov 2026).
        var outcome = await SerializableWriteRetry.RunAsync<ServiceResult<ReturnDetailView>?>(db, async token =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
            try
            {
                await ReturnPendingStock.LockLinesAsync(db, tenantId, [command.ReturnLineId], token);
                var existing = await db.ReturnStockDispositions.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.IdempotencyKey == idempotencyKey, token);
                if (existing)
                {
                    await transaction.CommitAsync(token);
                    return null;
                }

                var claim = await db.ReturnClaims.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == claimId, token);
                if (claim is null) return NotFound<ReturnDetailView>();
                var line = await db.ReturnLines.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ClaimId == claimId && x.Id == command.ReturnLineId, token);
                if (line is null) return NotFound<ReturnDetailView>();
                // Decided per line: a line approved on its own takes stock while the claim's other lines still wait.
                if (!ReturnDecisionLinePolicy.StockDispositionAllowed(claim.Status, line.RawStatus)) return ServiceResult<ReturnDetailView>.Fail("RETURN_DISPOSITION_NOT_ALLOWED", "Stok kararı yalnız onaylanan iade ürünlerine uygulanabilir.", 409);
                var already = await ReturnPendingStock.DisposedQuantityAsync(db, tenantId, line.Id, token);
                if (already + command.Quantity > line.Quantity) return ServiceResult<ReturnDetailView>.Fail("RETURN_QUANTITY_EXCEEDED", "Disposition toplamı iade satırı miktarını aşamaz.", 409);
                var variantId = await db.OrderLines.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == line.OrderLineId).Select(x => x.VariantId).SingleAsync(token);
                if (variantId is null) return ServiceResult<ReturnDetailView>.Fail("INVENTORY_MAPPING_REQUIRED", "İade satırı yerel varyantla eşleşmiyor.", 422);
                var item = await db.InventoryItems.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.VariantId == variantId && x.LocationCode == "MAIN", token);
                if (item is null) return ServiceResult<ReturnDetailView>.Fail("INVENTORY_MAPPING_REQUIRED", "MAIN inventory item bulunamadı.", 422);
                // SIPARIS-STOK-6: with the other claims of the same order line, never more back in stock than was sold.
                if (disposition == ReturnStockDispositionKind.Pass
                    && await StockMovementWriter.ReturnRestockRoomAsync(db, tenantId, line.OrderLineId, token) is var room && command.Quantity > room.Left)
                    return ServiceResult<ReturnDetailView>.Fail("RETURN_ORDER_LINE_QUANTITY_EXCEEDED", $"Bu sipariş satırından {StockMovementWriter.Units(room.Sold)} adet satıldı ve iade taleplerinden {StockMovementWriter.Units(room.Restocked)} adedi zaten stoğa eklendi; en fazla {StockMovementWriter.Units(room.Left)} adet daha stoğa eklenebilir.", 409);
                var now = timeProvider.GetUtcNow();
                db.ReturnStockDispositions.Add(new ReturnStockDisposition { Id = Guid.CreateVersion7(), TenantId = tenantId, ClaimId = claimId, ReturnLineId = line.Id, InventoryItemId = item.Id, Disposition = disposition, Quantity = command.Quantity, IdempotencyKey = idempotencyKey, Reason = command.Reason.Trim(), ActorUserId = userId, CreatedAt = now });
                if (disposition == ReturnStockDispositionKind.Pass)
                {
                    var change = StockMovementWriter.Move(db, item, command.Quantity, StockMovementRounding.None, ReturnPendingStock.RestockSource(claim, idempotencyKey, userId, correlationId, now));
                    await StockProjectionOutbox.EnqueueAsync(db, tenantId, [change], now, token);
                }
                db.AuditLogs.Add(new AuditLog { TenantId = tenantId, ActorUserId = userId, Action = "RETURN_STOCK_DISPOSITION", TargetType = "ReturnClaim", TargetId = claimId.ToString("D"), Reason = command.Reason.Trim(), CorrelationId = correlationId, CreatedAt = now });
                await db.SaveChangesAsync(token);
                await transaction.CommitAsync(token);
            }
            catch (DbUpdateException exception) when (!SerializableWriteRetry.IsRetryable(exception))
            {
                await transaction.RollbackAsync(CancellationToken.None);
                // A replay that committed meanwhile (the same key) is the same disposition.
                if (await db.ReturnStockDispositions.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.IdempotencyKey == idempotencyKey, token)) return null;
                throw;
            }
            return null;
        }, () => ServiceResult<ReturnDetailView>.Fail("STOCK_CHANGED", "Stok aynı anda başka bir işlemle (ör. yeni sipariş) değiştiği için kaydedilemedi. Sayfayı yenileyip tekrar deneyin.", 409), cancellationToken);
        return outcome ?? await ReturnAsync(tenantId, claimId, cancellationToken);
    }
}
