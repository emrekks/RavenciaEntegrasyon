using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class StockPushPipelineDefaults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // D (owner decision 30 Oct 2026, "Panel gibi düşün"): the panel is the stock master. A connection kept as
            // REMOTE_AUTHORITATIVE let a catalog import overwrite the panel's on-hand stock with the marketplace number;
            // it goes back to the default (CENTRAL). The explicit "Fiyat ve Stokları Güncelle" import still takes the
            // marketplace number, as a deliberate action. Live: the Trendyol connection, one row.
            migrationBuilder.Sql("""
                UPDATE inventory.connection_inventory_policies
                SET "AuthorityMode" = 'CENTRAL', "Version" = "Version" + 1
                WHERE "AuthorityMode" = 'REMOTE_AUTHORITATIVE';
                """);

            // F: the stock reconcile schedule is no longer switched off with the master switch; the scheduler queues it
            // only while the connection's automatic price/stock writes are open. The Trendyol schedule the master switch
            // once turned off is on again (live: three rows); with the writes off it queues nothing.
            migrationBuilder.Sql("""
                UPDATE integration.connection_sync_policies p
                SET "Enabled" = true, "Version" = p."Version" + 1
                FROM integration.platform_connections c
                WHERE c."TenantId" = p."TenantId" AND c."Id" = p."ConnectionId" AND c."Status" <> 'DELETED'
                  AND p."ResourceType" IN ('STOCK_RECONCILE_SHORT', 'STOCK_RECONCILE_MEDIUM', 'STOCK_RECONCILE_DAILY')
                  AND p."Enabled" = false;
                """);

            // J: an order shortfall issue stays open only while its line still holds the shortfall as an active
            // reservation; the order sync closes it when the reservation is released from now on. The ones whose
            // reservation is already gone are closed here (live: 294 of 311 open).
            migrationBuilder.Sql("""
                UPDATE ops.operational_issues i
                SET "Status" = 'Resolved', "ResolvedAt" = now()
                WHERE i."Code" = 'STOCK_ORDER_CONSUMPTION_BLOCKED'
                  AND i."Status" <> 'Resolved'
                  AND i."DedupeKey" LIKE 'stock-order-consumption:%'
                  AND NOT EXISTS (
                      SELECT 1 FROM inventory.stock_reservations r
                      WHERE r."TenantId" = i."TenantId"
                        AND r."SourceType" = 'ORDER_LINE'
                        AND r."Status" = 'Active'
                        AND replace(r."SourceId", '-', '') = substring(i."DedupeKey" FROM 25));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Data corrections only: the previous authority mode, schedule switch and issue states are not recorded,
            // so going down leaves the rows as they are.
        }
    }
}
