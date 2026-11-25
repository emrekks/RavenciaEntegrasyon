using MarketplaceHub.Application;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

// The units an order line took from stock stay taken when the line is removed by a reset that keeps the stock (P-2,
// 29 Oct 2026; Ayarlar › Veri › Siparişler without Stok ve Fiyatlar, scan 25 Nov 2026 A-5). Its ORDER_LINE ledger rows and
// reservations move from the line's id to its carry key (OrderInventoryStockPolicy.ResetCarryKey), and the line the next
// sync imports under the same marketplace ids takes them over (OrderIngestionService.AdoptResetOrderLineStock) instead of
// taking the units again. A reservation another removal already left under the same key and item keeps the line's id.
// Called before the lines are deleted: by the connection full reset (its connection's orders) and by the panel reset
// (every order of the tenant). Nothing else changes: no quantity, no stock item, no order.
internal static class OrderLineStockCarry
{
    // ConnectionId null: every order line of the tenant. Runs in the caller's transaction, or in one of its own.
    public static async Task CarryAsync(AppDbContext db, Guid tenantId, Guid? connectionId, CancellationToken cancellationToken)
    {
        var own = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        try
        {
            await db.Database.ExecuteSqlInterpolatedAsync($$"""
                CREATE TEMP TABLE IF NOT EXISTS carry_order_lines ("SourceId" text PRIMARY KEY, "CarryKey" text NOT NULL) ON COMMIT DROP;
                TRUNCATE carry_order_lines;
                INSERT INTO carry_order_lines SELECT l."Id"::text, {{OrderInventoryStockPolicy.ResetCarryPrefix}} || o."ConnectionId"::text || ':' || o."ExternalOrderId" || ':' || l."ExternalLineId" FROM sales.order_lines l JOIN sales.orders o ON o."TenantId"=l."TenantId" AND o."Id"=l."OrderId" WHERE l."TenantId"={{tenantId}} AND o."TenantId"={{tenantId}} AND (CAST({{connectionId}} AS uuid) IS NULL OR o."ConnectionId"={{connectionId}});
                UPDATE inventory.stock_reservations r SET "SourceId"=c."CarryKey", "Version"=r."Version"+1 FROM carry_order_lines c WHERE r."TenantId"={{tenantId}} AND r."SourceType"='ORDER_LINE' AND r."SourceId"=c."SourceId" AND NOT EXISTS (SELECT 1 FROM inventory.stock_reservations x WHERE x."TenantId"={{tenantId}} AND x."SourceType"='ORDER_LINE' AND x."SourceId"=c."CarryKey" AND x."InventoryItemId"=r."InventoryItemId");
                UPDATE inventory.stock_ledger_entries e SET "SourceId"=c."CarryKey" FROM carry_order_lines c WHERE e."TenantId"={{tenantId}} AND e."SourceType"='ORDER_LINE' AND e."SourceId"=c."SourceId";
                """, cancellationToken);
            if (own is not null) await own.CommitAsync(cancellationToken);
        }
        finally
        {
            if (own is not null) await own.DisposeAsync();
        }
    }
}
