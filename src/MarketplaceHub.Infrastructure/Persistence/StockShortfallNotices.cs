using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

// The STOCK_ORDER_CONSUMPTION_BLOCKED notice says an order line found too little stock and asks the owner to check the
// count. The missing units are never taken later (owner decision, 30 Oct 2026), so nothing else closes it but a
// cancellation: a stock count set by hand is that check, and it closes the open notices of that stock item.
internal static class StockShortfallNotices
{
    public const string IssueCode = "STOCK_ORDER_CONSUMPTION_BLOCKED";

    public static string DedupeKey(Guid orderLineId) => $"stock-order-consumption:{orderLineId:N}";

    // Marks the open notices of these stock items resolved; the caller saves them with its own stock change.
    public static async Task ResolveForInventoryItemsAsync(AppDbContext db, Guid tenantId, IReadOnlyCollection<Guid> inventoryItemIds, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (inventoryItemIds.Count == 0) return;
        var lineIds = await db.StockLedgerEntries.AsNoTracking()
            .Where(x => x.TenantId == tenantId && inventoryItemIds.Contains(x.InventoryItemId) && x.MovementType == OrderInventoryStockPolicy.ShortfallMovementType)
            .Select(x => x.SourceId)
            .Distinct()
            .ToListAsync(cancellationToken);
        var keys = lineIds.Select(id => Guid.TryParse(id, out var lineId) ? DedupeKey(lineId) : null).OfType<string>().ToList();
        if (keys.Count == 0) return;
        var open = await db.OperationalIssues
            .Where(x => x.TenantId == tenantId && x.Code == IssueCode && keys.Contains(x.DedupeKey) && x.Status != IssueStatus.Resolved)
            .ToListAsync(cancellationToken);
        foreach (var issue in open)
        {
            issue.Status = IssueStatus.Resolved;
            issue.ResolvedAt = now;
        }
    }
}
