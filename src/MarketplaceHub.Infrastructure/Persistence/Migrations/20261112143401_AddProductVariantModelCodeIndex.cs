using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProductVariantModelCodeIndex : Migration
    {
        // Import matching (Trendyol, Shopify, PTT AVM), listing checks and family reads filter catalog.product_variants by
        // ModelCode; with no index each of them read the whole table (live 12 Nov 2026: ~660 full scans a minute during a
        // product pull). Built CONCURRENTLY like AddJobListAndAttemptIndexes: the previous API and Worker keep writing
        // variants during the migration, and CREATE INDEX CONCURRENTLY never blocks their reads or writes, so the migrate
        // session's lock_timeout is lifted for these statements while statement_timeout still bounds them. The index is
        // dropped first in case an earlier, interrupted run left an INVALID one. Additive only: no row changes.
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("SET lock_timeout = 0", suppressTransaction: true);
            migrationBuilder.Sql(
                "DROP INDEX CONCURRENTLY IF EXISTS catalog.\"IX_product_variants_TenantId_ModelCode\"",
                suppressTransaction: true);
            migrationBuilder.Sql(
                "CREATE INDEX CONCURRENTLY \"IX_product_variants_TenantId_ModelCode\" ON catalog.product_variants (\"TenantId\", \"ModelCode\")",
                suppressTransaction: true);
            migrationBuilder.Sql("RESET lock_timeout", suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "DROP INDEX CONCURRENTLY IF EXISTS catalog.\"IX_product_variants_TenantId_ModelCode\"",
                suppressTransaction: true);
        }
    }
}
