using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProductFamilyRequired : Migration
    {
        // expand-contract: phase 2 of 20261114125517_ProductModels
        // KATALOG-14 phase D5, step B1 stage 2: every colour card belongs to a model (its brand's model of its code, or a model
        // of its own "#<card id>"), so catalog.products.FamilyId becomes NOT NULL. Stage 1 is live: a save gives every new card
        // its model before the insert (ProductModelLinks.GiveNewCardsModels), a card taken off its model goes straight to one of
        // its own (GiveOwnModelsAsync), the deletes and resets remove the cards before their model and the Marka Listesi reset
        // detaches them first (PanelFactoryResetInventory.Detaches); no path writes a NULL link, so the code running while this
        // deploy migrates keeps working. Live (read-only SQL, 27 Nov 2026): 0 of 1013 cards without a model, 0 links to a missing
        // model, 0 tenant mismatches.
        // The model key is not touched: it stays ON DELETE SET NULL ("FamilyId"); with the column NOT NULL a model that still
        // has a card can no longer be deleted (not_null_violation), which every delete path already respects.
        // The migration changes no row. When a card without a model exists it stops with their count and changes nothing (the
        // owner's data is never fixed, assigned or deleted by an automatic path).
        // Locks: every statement runs in its own short transaction (suppressTransaction) with lock_timeout 5 s, so a blocked
        // ALTER gives up instead of queueing the table's readers and writers behind it (DatabaseMigrator retries the run):
        // 1. the NULL count (no lock, a quick stop), then CHECK ("FamilyId" IS NOT NULL) NOT VALID (ACCESS EXCLUSIVE for an
        //    instant, no scan) and the count again under that lock (IX_products_FamilyId holds the NULLs, so it is an index
        //    read): from here no NULL can come in, and if one came in meanwhile the whole step rolls back, constraint included;
        // 2. VALIDATE CONSTRAINT scans under SHARE UPDATE EXCLUSIVE (reads and writes go on);
        // 3. SET NOT NULL takes ACCESS EXCLUSIVE for an instant: PostgreSQL 12+ skips its scan because the validated check
        //    proves the column holds no NULL; the check is then dropped.
        // Every step can run again, so a run that stopped half-way is finished by the next deploy's migrate (the history row
        // is written last). A step-2/3 stop can only leave the NOT VALID check behind, which refuses nothing but a NULL.
        private const string Check = "CK_products_FamilyId_NotNull";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("SET lock_timeout = '5s'", suppressTransaction: true);
            migrationBuilder.Sql($$"""
                DO $$
                DECLARE
                    unlinked bigint;
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_attribute WHERE attrelid = 'catalog.products'::regclass AND attname = 'FamilyId' AND attnotnull) THEN
                        RETURN;
                    END IF;
                    SELECT count(*) INTO unlinked FROM catalog.products WHERE "FamilyId" IS NULL;
                    IF unlinked > 0 THEN
                        RAISE EXCEPTION 'Model bagi zorunlu yapilamadi: modeli olmayan % urun karti var. Hicbir sey degistirilmedi.', unlinked
                            USING ERRCODE = 'check_violation';
                    END IF;
                    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'catalog.products'::regclass AND conname = '{{Check}}') THEN
                        ALTER TABLE catalog.products ADD CONSTRAINT "{{Check}}" CHECK ("FamilyId" IS NOT NULL) NOT VALID;
                    END IF;
                    SELECT count(*) INTO unlinked FROM catalog.products WHERE "FamilyId" IS NULL;
                    IF unlinked > 0 THEN
                        RAISE EXCEPTION 'Model bagi zorunlu yapilamadi: modeli olmayan % urun karti var. Hicbir sey degistirilmedi.', unlinked
                            USING ERRCODE = 'check_violation';
                    END IF;
                END $$
                """, suppressTransaction: true);
            migrationBuilder.Sql($$"""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'catalog.products'::regclass AND conname = '{{Check}}' AND NOT convalidated) THEN
                        ALTER TABLE catalog.products VALIDATE CONSTRAINT "{{Check}}";
                    END IF;
                END $$
                """, suppressTransaction: true);
            migrationBuilder.Sql("""ALTER TABLE catalog.products ALTER COLUMN "FamilyId" SET NOT NULL""", suppressTransaction: true);
            migrationBuilder.Sql($"""ALTER TABLE catalog.products DROP CONSTRAINT IF EXISTS "{Check}" """, suppressTransaction: true);
            migrationBuilder.Sql("RESET lock_timeout", suppressTransaction: true);
        }

        // Back to the nullable column (stage 1's schema). No row changes; the model key was never touched.
        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("SET lock_timeout = '5s'", suppressTransaction: true);
            migrationBuilder.Sql($"""ALTER TABLE catalog.products DROP CONSTRAINT IF EXISTS "{Check}" """, suppressTransaction: true);
            migrationBuilder.Sql("""ALTER TABLE catalog.products ALTER COLUMN "FamilyId" DROP NOT NULL""", suppressTransaction: true);
            migrationBuilder.Sql("RESET lock_timeout", suppressTransaction: true);
        }
    }
}
