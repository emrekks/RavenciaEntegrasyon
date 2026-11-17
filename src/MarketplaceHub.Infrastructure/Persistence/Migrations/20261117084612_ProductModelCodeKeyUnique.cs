using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProductModelCodeKeyUnique : Migration
    {
        // KATALOG-14 phase D5b (owner, 17 Nov 2026): a model is its code alone, one per tenant and model-code key; the brand
        // is a field of the model. The (TenantId, ModelCodeKey) index becomes unique; no row is written.
        // Precondition, checked first: no key held by two models (a code under two brands, which the previous release kept
        // as one model per brand) and no card without a model. Either stops the migration with its count: nothing is
        // merged or chosen here, the owner decides (17 Nov 2026 live: 0 and 0).
        // The unique index is built CONCURRENTLY under a temporary name, the old one is dropped CONCURRENTLY and the new
        // one renamed to the old name (a catalog-only change); a rerun after a failed build drops the leftover and starts
        // over. The running release's model inserts (ON CONFLICT DO NOTHING) are unaffected.
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    shared_keys bigint;
                    unlinked_cards bigint;
                BEGIN
                    SELECT count(*) INTO shared_keys FROM (
                        SELECT 1 FROM catalog.product_models GROUP BY "TenantId", "ModelCodeKey" HAVING count(*) > 1) k;
                    SELECT count(*) INTO unlinked_cards FROM catalog.products WHERE "FamilyId" IS NULL;
                    IF shared_keys > 0 OR unlinked_cards > 0 THEN
                        RAISE EXCEPTION 'KATALOG-14 D5b: model kodu tekil yapilamadi. Birden fazla modelde (birden fazla markada) kayitli model kodu: %, modele bagli olmayan kart: %. Hicbir sey birlestirilmedi; sahibin karari gerekir.', shared_keys, unlinked_cards;
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

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_product_models_TenantId_ModelCodeKey",
                schema: "catalog",
                table: "product_models");

            migrationBuilder.CreateIndex(
                name: "IX_product_models_TenantId_ModelCodeKey",
                schema: "catalog",
                table: "product_models",
                columns: new[] { "TenantId", "ModelCodeKey" });
        }
    }
}
