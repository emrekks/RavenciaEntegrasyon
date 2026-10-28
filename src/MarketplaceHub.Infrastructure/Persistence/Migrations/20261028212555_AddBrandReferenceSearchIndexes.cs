using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBrandReferenceSearchIndexes : Migration
    {
        // The brand picker searches the current brand snapshot instead of downloading it (Trendyol's holds 808k brands):
        // a name prefix through a byte-ordered btree, a name fragment through trigrams. Both indexes are partial on
        // BRANDS (about 0.9 of the table's 1.3 million rows). Built CONCURRENTLY: the previous API and Worker keep
        // writing reference items during the migration, and CREATE INDEX CONCURRENTLY never blocks them, so the
        // migrate session's lock_timeout (DatabaseMigrator) is lifted for these statements while statement_timeout
        // still bounds them. Each index is dropped first in case an interrupted run left an INVALID one.
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS pg_trgm", suppressTransaction: true);
            migrationBuilder.Sql("SET lock_timeout = 0", suppressTransaction: true);

            migrationBuilder.Sql(
                "DROP INDEX CONCURRENTLY IF EXISTS integration.\"IX_reference_items_brand_name_prefix\"",
                suppressTransaction: true);
            migrationBuilder.Sql(
                "CREATE INDEX CONCURRENTLY \"IX_reference_items_brand_name_prefix\" ON integration.reference_items (\"TenantId\", \"SnapshotId\", \"NormalizedName\" COLLATE \"C\") WHERE \"ResourceType\" = 'BRANDS'",
                suppressTransaction: true);

            migrationBuilder.Sql(
                "DROP INDEX CONCURRENTLY IF EXISTS integration.\"IX_reference_items_brand_name_trigram\"",
                suppressTransaction: true);
            migrationBuilder.Sql(
                "CREATE INDEX CONCURRENTLY \"IX_reference_items_brand_name_trigram\" ON integration.reference_items USING gin (\"NormalizedName\" gin_trgm_ops) WHERE \"ResourceType\" = 'BRANDS'",
                suppressTransaction: true);

            migrationBuilder.Sql("RESET lock_timeout", suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "DROP INDEX CONCURRENTLY IF EXISTS integration.\"IX_reference_items_brand_name_trigram\"",
                suppressTransaction: true);
            migrationBuilder.Sql(
                "DROP INDEX CONCURRENTLY IF EXISTS integration.\"IX_reference_items_brand_name_prefix\"",
                suppressTransaction: true);
            migrationBuilder.Sql("DROP EXTENSION IF EXISTS pg_trgm", suppressTransaction: true);
        }
    }
}
