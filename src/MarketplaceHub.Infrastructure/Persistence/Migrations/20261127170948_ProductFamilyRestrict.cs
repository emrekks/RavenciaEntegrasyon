using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProductFamilyRestrict : Migration
    {
        // KATALOG-14 D5, B1 review: the key products ("TenantId", "FamilyId") -> product_models ("TenantId", "Id") becomes
        // ON DELETE RESTRICT (it was SET NULL ("FamilyId"), which the NOT NULL column of step B1 can only answer with
        // not_null_violation). ProductModelLinks deletes a model "that has no card" with a READ COMMITTED NOT EXISTS check, so a
        // card another save adds and commits meanwhile is not seen; the database now refuses that delete with a clear
        // restrict_violation (23001) on this key, and DeleteModelsWithoutCardsAsync runs the delete again (it then keeps the model).
        // No row changes; every card already has its model (step B1), so the new key validates on live data.
        // Written by hand, the safe pattern for a key on tables with rows (docs/CI-CD-DEPLOY.md "Migration kuralları"); every
        // statement runs in its own short transaction (suppressTransaction) with lock_timeout 5 s, so a blocked ALTER gives up
        // instead of queueing the tables' readers and writers behind it (DatabaseMigrator retries the run):
        // 1. ADD CONSTRAINT <temporary name> ... ON DELETE RESTRICT NOT VALID: SHARE ROW EXCLUSIVE on catalog.products and on
        //    catalog.product_models for an instant (no scan; writes wait that instant, reads go on). From here both keys guard
        //    the link: a model with a card cannot be deleted under either (not_null_violation or restrict_violation).
        // 2. VALIDATE CONSTRAINT: scans catalog.products (about a thousand rows on live, index lookups into product_models)
        //    under SHARE UPDATE EXCLUSIVE on products and ROW SHARE on product_models: reads and writes go on.
        // 3. DROP the old key and RENAME the new one to the old name (EF's name), in one transaction: ACCESS EXCLUSIVE on both
        //    tables for an instant (dropping a key locks the referenced table too).
        // Each step can run again (the DO blocks check pg_constraint), so a run that stopped half-way is finished by the next
        // deploy's migrate; the history row is written last. A stop after step 1 or 2 leaves the old key in place beside the
        // new one, which refuses nothing that the old one allowed.
        // The previous API and Worker keep serving while this runs: they never delete a model that has a card (every path
        // removes or moves the cards first), so the only difference they can meet is the concurrent race above, where they
        // already failed (not_null_violation) and now fail with restrict_violation on this key; neither changes a row.
        private const string Key = "FK_products_product_models_TenantId_FamilyId";
        private const string NewKey = "FK_products_product_models_TenantId_FamilyId_restrict";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("SET lock_timeout = '5s'", suppressTransaction: true);
            migrationBuilder.Sql($$"""
                DO $$
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'catalog.products'::regclass AND conname = '{{Key}}' AND contype = 'f' AND confdeltype = 'r')
                       AND NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'catalog.products'::regclass AND conname = '{{NewKey}}') THEN
                        ALTER TABLE catalog.products ADD CONSTRAINT "{{NewKey}}" FOREIGN KEY ("TenantId", "FamilyId") REFERENCES catalog.product_models ("TenantId", "Id") ON DELETE RESTRICT NOT VALID;
                    END IF;
                END $$
                """, suppressTransaction: true);
            migrationBuilder.Sql($$"""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'catalog.products'::regclass AND conname = '{{NewKey}}' AND NOT convalidated) THEN
                        ALTER TABLE catalog.products VALIDATE CONSTRAINT "{{NewKey}}";
                    END IF;
                END $$
                """, suppressTransaction: true);
            migrationBuilder.Sql($$"""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'catalog.products'::regclass AND conname = '{{NewKey}}') THEN
                        ALTER TABLE catalog.products DROP CONSTRAINT IF EXISTS "{{Key}}";
                        ALTER TABLE catalog.products RENAME CONSTRAINT "{{NewKey}}" TO "{{Key}}";
                    END IF;
                END $$
                """, suppressTransaction: true);
            migrationBuilder.Sql("RESET lock_timeout", suppressTransaction: true);
        }

        // Back to ON DELETE SET NULL ("FamilyId"), the same three steps. No row changes.
        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("SET lock_timeout = '5s'", suppressTransaction: true);
            migrationBuilder.Sql($$"""
                DO $$
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'catalog.products'::regclass AND conname = '{{Key}}' AND contype = 'f' AND confdeltype = 'n')
                       AND NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'catalog.products'::regclass AND conname = '{{NewKey}}') THEN
                        ALTER TABLE catalog.products ADD CONSTRAINT "{{NewKey}}" FOREIGN KEY ("TenantId", "FamilyId") REFERENCES catalog.product_models ("TenantId", "Id") ON DELETE SET NULL ("FamilyId") NOT VALID;
                    END IF;
                END $$
                """, suppressTransaction: true);
            migrationBuilder.Sql($$"""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'catalog.products'::regclass AND conname = '{{NewKey}}' AND NOT convalidated) THEN
                        ALTER TABLE catalog.products VALIDATE CONSTRAINT "{{NewKey}}";
                    END IF;
                END $$
                """, suppressTransaction: true);
            migrationBuilder.Sql($$"""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'catalog.products'::regclass AND conname = '{{NewKey}}') THEN
                        ALTER TABLE catalog.products DROP CONSTRAINT IF EXISTS "{{Key}}";
                        ALTER TABLE catalog.products RENAME CONSTRAINT "{{NewKey}}" TO "{{Key}}";
                    END IF;
                END $$
                """, suppressTransaction: true);
            migrationBuilder.Sql("RESET lock_timeout", suppressTransaction: true);
        }
    }
}
