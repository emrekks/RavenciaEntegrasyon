using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProductModelCodeKeyPerBrand : Migration
    {
        // Owner (17 Nov 2026, "farklı markalar aynı model kodunu kullanabiliyor"): a model is its brand and its code again;
        // the same code under another brand is another model. The unique (TenantId, ModelCodeKey) index of
        // 20261117151526_ProductModelCodeKeyUnique goes back to a plain one under the same name (no row is written). The
        // unique (TenantId, BrandId, ModelCodeKey) NULLS NOT DISTINCT index stays as it is.
        // The plain index is built CONCURRENTLY under a temporary name first, the unique one dropped CONCURRENTLY and the
        // new one renamed to the old name (a catalog-only change); a rerun after a failed build drops the leftover and
        // starts over. The previous release runs unharmed on the plain index (it keeps
        // refusing another brand's card under a code, which is harmless).
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("SET lock_timeout = 0", suppressTransaction: true);
            migrationBuilder.Sql(
                "DROP INDEX CONCURRENTLY IF EXISTS catalog.\"IX_product_models_TenantId_ModelCodeKey_plain\"",
                suppressTransaction: true);
            migrationBuilder.Sql(
                "CREATE INDEX CONCURRENTLY \"IX_product_models_TenantId_ModelCodeKey_plain\" ON catalog.product_models (\"TenantId\", \"ModelCodeKey\")",
                suppressTransaction: true);
            migrationBuilder.Sql(
                "DROP INDEX CONCURRENTLY IF EXISTS catalog.\"IX_product_models_TenantId_ModelCodeKey\"",
                suppressTransaction: true);
            migrationBuilder.Sql("RESET lock_timeout", suppressTransaction: true);
            migrationBuilder.Sql(
                "ALTER INDEX catalog.\"IX_product_models_TenantId_ModelCodeKey_plain\" RENAME TO \"IX_product_models_TenantId_ModelCodeKey\"");
        }

        // Back to the code alone (20261117151526's Up): it stops with a count when a code is held by models of two brands,
        // which this release allows; nothing is merged or chosen here.
        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    shared_keys bigint;
                BEGIN
                    SELECT count(*) INTO shared_keys FROM (
                        SELECT 1 FROM catalog.product_models GROUP BY "TenantId", "ModelCodeKey" HAVING count(*) > 1) k;
                    IF shared_keys > 0 THEN
                        RAISE EXCEPTION 'Model kodu tekil yapilamadi: birden fazla markada kayitli model kodu: %. Hicbir sey birlestirilmedi.', shared_keys;
                    END IF;
                END $$;
                """, suppressTransaction: true);
            migrationBuilder.Sql("SET lock_timeout = 0", suppressTransaction: true);
            migrationBuilder.Sql(
                "DROP INDEX CONCURRENTLY IF EXISTS catalog.\"IX_product_models_TenantId_ModelCodeKey_unique\"",
                suppressTransaction: true);
            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX CONCURRENTLY \"IX_product_models_TenantId_ModelCodeKey_unique\" ON catalog.product_models (\"TenantId\", \"ModelCodeKey\")",
                suppressTransaction: true);
            migrationBuilder.Sql(
                "DROP INDEX CONCURRENTLY IF EXISTS catalog.\"IX_product_models_TenantId_ModelCodeKey\"",
                suppressTransaction: true);
            migrationBuilder.Sql("RESET lock_timeout", suppressTransaction: true);
            migrationBuilder.Sql(
                "ALTER INDEX catalog.\"IX_product_models_TenantId_ModelCodeKey_unique\" RENAME TO \"IX_product_models_TenantId_ModelCodeKey\"");
        }
    }
}
