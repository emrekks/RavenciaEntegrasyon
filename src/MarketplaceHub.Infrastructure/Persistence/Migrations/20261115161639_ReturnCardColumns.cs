using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ReturnCardColumns : Migration
    {
        // SIPARIS-STOK-9 (scan 2026-11-15), expand step: the Returns page's card columns on sales.return_claims (TabGroup,
        // CardSplit, ApprovedAt; all nullable, null meaning "not filled yet") and the indexes its tab pages and split
        // lookups use. Written by hand because the previous API and Worker keep serving while it runs (docs/CI-CD-DEPLOY.md
        // "Migration kuralları"): they never name these columns (EF lists its columns; no raw SQL reads return_claims or
        // return_lines positionally), and the rows they insert meanwhile simply stay unfilled. The values are written by
        // the application (ReturnCardColumns: the rule is C#), not here: the deploy's backfill and the worker's repair pass
        // fill them, and every read falls back to the old computation while a tenant has an unfilled or stale row.
        // Every statement runs in its own transaction (suppressTransaction) and can be repeated after a partial run:
        //   1. ADD COLUMN IF NOT EXISTS x3 in one ALTER: nullable columns without a default only change the catalog, so the
        //      ACCESS EXCLUSIVE lock is held for an instant; lock_timeout 5 s makes a blocked ALTER give up instead of
        //      queueing the table's readers and writers behind it (DatabaseMigrator retries the run).
        //   2. The indexes with CREATE INDEX CONCURRENTLY (lock_timeout lifted; they never block reads or writes), each
        //      preceded by DROP INDEX CONCURRENTLY IF EXISTS so an INVALID leftover of an interrupted run is rebuilt.
        private const string RequestedIndex = "IX_return_claims_TenantId_TabGroup_RequestedAt_Id";
        private const string ApprovedIndex = "IX_return_claims_TenantId_TabGroup_ApprovedAt_Id";
        private const string LineStatusIndex = "IX_return_lines_TenantId_ClaimId_RawStatus";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("SET lock_timeout = '5s'", suppressTransaction: true);
            migrationBuilder.Sql("""
                ALTER TABLE sales.return_claims
                    ADD COLUMN IF NOT EXISTS "TabGroup" character varying(24) NULL,
                    ADD COLUMN IF NOT EXISTS "CardSplit" boolean NULL,
                    ADD COLUMN IF NOT EXISTS "ApprovedAt" timestamp with time zone NULL
                """, suppressTransaction: true);

            migrationBuilder.Sql("SET lock_timeout = 0", suppressTransaction: true);
            CreateIndexConcurrently(migrationBuilder, RequestedIndex,
                $"CREATE INDEX CONCURRENTLY \"{RequestedIndex}\" ON sales.return_claims (\"TenantId\", \"TabGroup\", \"RequestedAt\" DESC, \"Id\")");
            CreateIndexConcurrently(migrationBuilder, ApprovedIndex,
                $"CREATE INDEX CONCURRENTLY \"{ApprovedIndex}\" ON sales.return_claims (\"TenantId\", \"TabGroup\", \"ApprovedAt\" DESC, \"Id\")");
            CreateIndexConcurrently(migrationBuilder, LineStatusIndex,
                $"CREATE INDEX CONCURRENTLY \"{LineStatusIndex}\" ON sales.return_lines (\"TenantId\", \"ClaimId\", \"RawStatus\")");
            migrationBuilder.Sql("RESET lock_timeout", suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("SET lock_timeout = 0", suppressTransaction: true);
            migrationBuilder.Sql($"DROP INDEX CONCURRENTLY IF EXISTS sales.\"{LineStatusIndex}\"", suppressTransaction: true);
            migrationBuilder.Sql($"DROP INDEX CONCURRENTLY IF EXISTS sales.\"{ApprovedIndex}\"", suppressTransaction: true);
            migrationBuilder.Sql($"DROP INDEX CONCURRENTLY IF EXISTS sales.\"{RequestedIndex}\"", suppressTransaction: true);
            migrationBuilder.Sql("SET lock_timeout = '5s'", suppressTransaction: true);
            migrationBuilder.Sql("""
                ALTER TABLE sales.return_claims
                    DROP COLUMN IF EXISTS "ApprovedAt",
                    DROP COLUMN IF EXISTS "CardSplit",
                    DROP COLUMN IF EXISTS "TabGroup"
                """, suppressTransaction: true);
            migrationBuilder.Sql("RESET lock_timeout", suppressTransaction: true);
        }

        // Drops a leftover (possibly INVALID) index of the same name from an interrupted run, then builds it.
        private static void CreateIndexConcurrently(MigrationBuilder migrationBuilder, string name, string definition)
        {
            migrationBuilder.Sql($"DROP INDEX CONCURRENTLY IF EXISTS sales.\"{name}\"", suppressTransaction: true);
            migrationBuilder.Sql(definition, suppressTransaction: true);
        }
    }
}
