using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RestrictConnectionChildCascades : Migration
    {
        // VERITABANI-DAGITIM-16, the optional part: the last five foreign keys to platform_connections that cascaded become
        // RESTRICT. Both paths that delete a connection row delete these children first: the hourly sweep
        // (ConnectionLeftovers.PurgeAsync) removes a deleted connection's tariffs, report files and sync states, then the row;
        // the factory reset's Eski Bağlantı Kayıtları (PanelFactoryResetService.CleanUpLegacyConnectionsAsync) removes the
        // same tables for deleted connections only, whether or not Promotions is chosen, then the row. Before, that reset
        // left the tariffs and files to the cascade. A deleted connection still holding orders or invoices of a category
        // not chosen stays, and the reset's note counts it. Live 16 Nov 2026 (read-only SQL): 0 tariffs, 0 report files,
        // 1 finance and 4 question sync states, all of active connections.
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
