using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RestrictConnectionChildCascades : Migration
    {
        // VERITABANI-DAGITIM-16, the optional part: the last five foreign keys to platform_connections that cascaded become
        // RESTRICT. Every path that deletes a connection row deletes these children first (ConnectionLeftovers.PurgeAsync;
        // PanelFactoryResetService removes the two sync states with the connection's settings and the tariffs and report
        // files with the Promotions category), so nothing relied on the cascade. One did by accident: a factory reset
        // without Promotions removed a deleted connection's tariffs and report files through it; now that connection row
        // stays until they go, as the reset already does for the other categories. Live 16 Nov 2026 (read-only SQL): 0
        // tariffs, 0 report files, 1 finance and 4 question sync states, all of active connections.
        // Same swap as ScanFixesSchema: per table one short transaction (lock_timeout 5 s) drops the key and adds it again
        // NOT VALID, unless it already restricts; VALIDATE in its own statement does not block writes. No row changes.
        private static readonly (string Table, string Key)[] Keys =
        [
            ("catalog.commission_tariffs", "FK_commission_tariffs_platform_connections_TenantId_Connection~"),
            ("catalog.promotion_offer_files", "FK_promotion_offer_files_platform_connections_TenantId_Connect~"),
            ("catalog.ad_report_files", "FK_ad_report_files_platform_connections_TenantId_ConnectionId"),
            ("integration.marketplace_finance_sync_states", "FK_marketplace_finance_sync_states_platform_connections_Tenant~"),
            ("integration.marketplace_question_sync_states", "FK_question_sync_states_connection"),
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) => Swap(migrationBuilder, 'r', "RESTRICT");

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) => Swap(migrationBuilder, 'c', "CASCADE");

        private static void Swap(MigrationBuilder migrationBuilder, char deleteAction, string onDelete)
        {
            migrationBuilder.Sql("SET lock_timeout = '5s'", suppressTransaction: true);
            foreach (var (table, key) in Keys)
            {
                migrationBuilder.Sql($"""
                    DO $$
                    BEGIN
                        IF NOT EXISTS (
                            SELECT 1 FROM pg_constraint
                            WHERE conrelid = '{table}'::regclass AND conname = '{key}' AND contype = 'f' AND confdeltype = '{deleteAction}'
                        ) THEN
                            ALTER TABLE {table} DROP CONSTRAINT IF EXISTS "{key}";
                            ALTER TABLE {table} ADD CONSTRAINT "{key}" FOREIGN KEY ("TenantId", "ConnectionId")
                                REFERENCES integration.platform_connections ("TenantId", "Id") ON DELETE {onDelete} NOT VALID;
                        END IF;
                    END $$
                    """, suppressTransaction: true);
                migrationBuilder.Sql($"ALTER TABLE {table} VALIDATE CONSTRAINT \"{key}\"", suppressTransaction: true);
            }
            migrationBuilder.Sql("RESET lock_timeout", suppressTransaction: true);
        }
    }
}
