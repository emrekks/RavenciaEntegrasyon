using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ScanFixesSchema : Migration
    {
        // Schema half of the 2026-11-15 scan fixes, written by hand because the previous API and Worker keep serving while
        // it runs (docs/CI-CD-DEPLOY.md "Migration kuralları") and because EF cannot write ON DELETE SET NULL (column):
        //   KATALOG-7       product_models -> categories: CASCADE becomes SET NULL ("CategoryId") (the tenant stays).
        //   KATALOG-15      products / product_media FamilyId: tenant-scoped ("TenantId","FamilyId") key, SET NULL ("FamilyId").
        //   VERITABANI-10   NULLS NOT DISTINCT on the attribute assignment, model attribute and media unique indexes.
        //   VERITABANI-11   billing.invoices: unique ETTN and unique invoice number per issuer (partial indexes); EF drops the
        //                   (TenantId, LegalEntityProfileId) FK index because the new number index starts with it.
        //   VERITABANI-15   ck_jobs_status and ck_invoices_status; the text equals the model's so EF sees no change.
        //   VERITABANI-16   the three finance history FKs to platform_connections: CASCADE becomes RESTRICT.
        // Every statement runs in its own transaction (suppressTransaction), so a failed or lock-timed-out run leaves only
        // whole steps behind and the next attempt repeats them: indexes are built under a temporary name, the old one is
        // dropped and the new one renamed; constraint swaps are DO blocks that skip a constraint already in its new form.
        // Indexes: CREATE/DROP INDEX CONCURRENTLY with lock_timeout lifted (they never block reads or writes).
        // Constraints: one short transaction per table with lock_timeout 5 s (a blocked ALTER gives up instead of queueing
        // the table's readers and writers; DatabaseMigrator retries the run). An ON DELETE action cannot be altered in place,
        // so a key is dropped and added again: dropping a foreign key takes ACCESS EXCLUSIVE on the referenced table too
        // (categories, product_models, platform_connections) for that instant. Keys and checks are added NOT VALID and
        // validated in a separate statement, which takes only SHARE UPDATE EXCLUSIVE and does not block writes. No row
        // changes. Live (read-only SQL, 15 Nov 2026): no row breaks any new index or constraint.
        private const string AssignmentIndex = "IX_product_attribute_assignments_TenantId_ProductId_VariantId_~";
        private const string ModelAttributeIndex = "IX_product_model_attributes_TenantId_ModelId_AttributeId_Value~";
        private const string MediaPlaceIndex = "IX_product_media_TenantId_ProductId_VariantId_SortOrder";
        private const string CategoryKey = "FK_product_models_categories_TenantId_CategoryId";
        private const string ProductFamilyKey = "FK_products_product_models_TenantId_FamilyId";
        private const string MediaFamilyKey = "FK_product_media_product_models_TenantId_FamilyId";
        private const string OldProductFamilyKey = "FK_products_product_models_FamilyId";
        private const string OldMediaFamilyKey = "FK_product_media_product_models_FamilyId";
        private const string CargoInvoiceItemKey = "FK_marketplace_cargo_invoice_items_platform_connections_Tenant~";
        private const string FinanceTransactionKey = "FK_marketplace_finance_transactions_platform_connections_Tenan~";
        private const string PayoutOrderKey = "FK_marketplace_payout_orders_platform_connections_TenantId_Con~";
        private const string JobStatusCheck = "\"Status\" IN ('PENDING', 'LEASED', 'RETRY_SCHEDULED', 'BLOCKED', 'MANUAL_REVIEW', 'SUCCEEDED', 'DEAD', 'CANCELLED')";
        private const string InvoiceStatusCheck = "\"Status\" IN ('Draft', 'Validating', 'ValidationFailed', 'Ready', 'Submitting', 'UnknownResult', 'Submitted', 'Accepted', 'Rejected', 'MarketplacePending', 'MarketplaceFailed', 'Completed', 'CancellationPending', 'Cancelled', 'CancellationRejected', 'AdjustmentRequired', 'ManualReview', 'CancelledLocal')";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("SET lock_timeout = 0", suppressTransaction: true);

            // Indexes the new tenant-scoped FamilyId keys use.
            CreateIndexConcurrently(migrationBuilder, "catalog", "IX_products_TenantId_FamilyId",
                "CREATE INDEX CONCURRENTLY \"IX_products_TenantId_FamilyId\" ON catalog.products (\"TenantId\", \"FamilyId\")");
            CreateIndexConcurrently(migrationBuilder, "catalog", "IX_product_media_TenantId_FamilyId",
                "CREATE INDEX CONCURRENTLY \"IX_product_media_TenantId_FamilyId\" ON catalog.product_media (\"TenantId\", \"FamilyId\")");

            // NULLS NOT DISTINCT unique indexes, same names as before.
            SwapIndexConcurrently(migrationBuilder, "catalog", AssignmentIndex, "IX_product_attribute_assignments_scan_swap",
                "CREATE UNIQUE INDEX CONCURRENTLY {0} ON catalog.product_attribute_assignments (\"TenantId\", \"ProductId\", \"VariantId\", \"AttributeId\", \"ValueId\") NULLS NOT DISTINCT");
            SwapIndexConcurrently(migrationBuilder, "catalog", ModelAttributeIndex, "IX_product_model_attributes_scan_swap",
                "CREATE UNIQUE INDEX CONCURRENTLY {0} ON catalog.product_model_attributes (\"TenantId\", \"ModelId\", \"AttributeId\", \"ValueId\") NULLS NOT DISTINCT");
            SwapIndexConcurrently(migrationBuilder, "catalog", MediaPlaceIndex, "IX_product_media_scan_swap",
                "CREATE UNIQUE INDEX CONCURRENTLY {0} ON catalog.product_media (\"TenantId\", \"ProductId\", \"VariantId\", \"SortOrder\") NULLS NOT DISTINCT");

            // One official invoice per ETTN and per issuer's invoice number.
            CreateIndexConcurrently(migrationBuilder, "billing", "UX_invoices_TenantId_EttnUuid",
                "CREATE UNIQUE INDEX CONCURRENTLY \"UX_invoices_TenantId_EttnUuid\" ON billing.invoices (\"TenantId\", \"EttnUuid\") WHERE \"EttnUuid\" IS NOT NULL");
            CreateIndexConcurrently(migrationBuilder, "billing", "UX_invoices_TenantId_LegalEntityProfileId_InvoiceNumber",
                "CREATE UNIQUE INDEX CONCURRENTLY \"UX_invoices_TenantId_LegalEntityProfileId_InvoiceNumber\" ON billing.invoices (\"TenantId\", \"LegalEntityProfileId\", \"InvoiceNumber\") WHERE \"InvoiceNumber\" IS NOT NULL");
            migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS billing.\"IX_invoices_TenantId_LegalEntityProfileId\"", suppressTransaction: true);

            migrationBuilder.Sql("SET lock_timeout = '5s'", suppressTransaction: true);

            // KATALOG-7: a deleted category empties the model's CategoryId only.
            ReplaceForeignKey(migrationBuilder, "catalog.product_models", CategoryKey, CategoryKey, 'n',
                "FOREIGN KEY (\"TenantId\", \"CategoryId\") REFERENCES catalog.categories (\"TenantId\", \"Id\") ON DELETE SET NULL (\"CategoryId\")");

            // KATALOG-15: a card or picture links only to a model of its own tenant.
            ReplaceForeignKey(migrationBuilder, "catalog.products", OldProductFamilyKey, ProductFamilyKey, 'n',
                "FOREIGN KEY (\"TenantId\", \"FamilyId\") REFERENCES catalog.product_models (\"TenantId\", \"Id\") ON DELETE SET NULL (\"FamilyId\")");
            ReplaceForeignKey(migrationBuilder, "catalog.product_media", OldMediaFamilyKey, MediaFamilyKey, 'n',
                "FOREIGN KEY (\"TenantId\", \"FamilyId\") REFERENCES catalog.product_models (\"TenantId\", \"Id\") ON DELETE SET NULL (\"FamilyId\")");

            // VERITABANI-DAGITIM-16: finance history is never deleted with its connection.
            ReplaceForeignKey(migrationBuilder, "sales.marketplace_finance_transactions", FinanceTransactionKey, FinanceTransactionKey, 'r',
                "FOREIGN KEY (\"TenantId\", \"ConnectionId\") REFERENCES integration.platform_connections (\"TenantId\", \"Id\") ON DELETE RESTRICT");
            ReplaceForeignKey(migrationBuilder, "sales.marketplace_payout_orders", PayoutOrderKey, PayoutOrderKey, 'r',
                "FOREIGN KEY (\"TenantId\", \"ConnectionId\") REFERENCES integration.platform_connections (\"TenantId\", \"Id\") ON DELETE RESTRICT");
            ReplaceForeignKey(migrationBuilder, "sales.marketplace_cargo_invoice_items", CargoInvoiceItemKey, CargoInvoiceItemKey, 'r',
                "FOREIGN KEY (\"TenantId\", \"ConnectionId\") REFERENCES integration.platform_connections (\"TenantId\", \"Id\") ON DELETE RESTRICT");

            // VERITABANI-DAGITIM-15: status values the code knows.
            AddCheck(migrationBuilder, "integration.jobs", "ck_jobs_status", JobStatusCheck);
            AddCheck(migrationBuilder, "billing.invoices", "ck_invoices_status", InvoiceStatusCheck);

            migrationBuilder.Sql("RESET lock_timeout", suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("SET lock_timeout = '5s'", suppressTransaction: true);
            migrationBuilder.Sql("ALTER TABLE billing.invoices DROP CONSTRAINT IF EXISTS ck_invoices_status", suppressTransaction: true);
            migrationBuilder.Sql("ALTER TABLE integration.jobs DROP CONSTRAINT IF EXISTS ck_jobs_status", suppressTransaction: true);

            ReplaceForeignKey(migrationBuilder, "sales.marketplace_cargo_invoice_items", CargoInvoiceItemKey, CargoInvoiceItemKey, 'c',
                "FOREIGN KEY (\"TenantId\", \"ConnectionId\") REFERENCES integration.platform_connections (\"TenantId\", \"Id\") ON DELETE CASCADE");
            ReplaceForeignKey(migrationBuilder, "sales.marketplace_payout_orders", PayoutOrderKey, PayoutOrderKey, 'c',
                "FOREIGN KEY (\"TenantId\", \"ConnectionId\") REFERENCES integration.platform_connections (\"TenantId\", \"Id\") ON DELETE CASCADE");
            ReplaceForeignKey(migrationBuilder, "sales.marketplace_finance_transactions", FinanceTransactionKey, FinanceTransactionKey, 'c',
                "FOREIGN KEY (\"TenantId\", \"ConnectionId\") REFERENCES integration.platform_connections (\"TenantId\", \"Id\") ON DELETE CASCADE");
            ReplaceForeignKey(migrationBuilder, "catalog.product_media", MediaFamilyKey, OldMediaFamilyKey, 'n',
                "FOREIGN KEY (\"FamilyId\") REFERENCES catalog.product_models (\"Id\") ON DELETE SET NULL");
            ReplaceForeignKey(migrationBuilder, "catalog.products", ProductFamilyKey, OldProductFamilyKey, 'n',
                "FOREIGN KEY (\"FamilyId\") REFERENCES catalog.product_models (\"Id\") ON DELETE SET NULL");
            ReplaceForeignKey(migrationBuilder, "catalog.product_models", CategoryKey, CategoryKey, 'c',
                "FOREIGN KEY (\"TenantId\", \"CategoryId\") REFERENCES catalog.categories (\"TenantId\", \"Id\") ON DELETE CASCADE");

            migrationBuilder.Sql("SET lock_timeout = 0", suppressTransaction: true);
            CreateIndexConcurrently(migrationBuilder, "billing", "IX_invoices_TenantId_LegalEntityProfileId",
                "CREATE INDEX CONCURRENTLY \"IX_invoices_TenantId_LegalEntityProfileId\" ON billing.invoices (\"TenantId\", \"LegalEntityProfileId\")");
            migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS billing.\"UX_invoices_TenantId_LegalEntityProfileId_InvoiceNumber\"", suppressTransaction: true);
            migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS billing.\"UX_invoices_TenantId_EttnUuid\"", suppressTransaction: true);

            SwapIndexConcurrently(migrationBuilder, "catalog", MediaPlaceIndex, "IX_product_media_scan_swap",
                "CREATE UNIQUE INDEX CONCURRENTLY {0} ON catalog.product_media (\"TenantId\", \"ProductId\", \"VariantId\", \"SortOrder\")");
            SwapIndexConcurrently(migrationBuilder, "catalog", ModelAttributeIndex, "IX_product_model_attributes_scan_swap",
                "CREATE UNIQUE INDEX CONCURRENTLY {0} ON catalog.product_model_attributes (\"TenantId\", \"ModelId\", \"AttributeId\", \"ValueId\")");
            SwapIndexConcurrently(migrationBuilder, "catalog", AssignmentIndex, "IX_product_attribute_assignments_scan_swap",
                "CREATE UNIQUE INDEX CONCURRENTLY {0} ON catalog.product_attribute_assignments (\"TenantId\", \"ProductId\", \"VariantId\", \"AttributeId\", \"ValueId\")");

            migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS catalog.\"IX_product_media_TenantId_FamilyId\"", suppressTransaction: true);
            migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS catalog.\"IX_products_TenantId_FamilyId\"", suppressTransaction: true);
            migrationBuilder.Sql("RESET lock_timeout", suppressTransaction: true);
        }

        // Drops a leftover (possibly INVALID) index of the same name from an interrupted run, then builds it.
        private static void CreateIndexConcurrently(MigrationBuilder migrationBuilder, string schema, string name, string definition)
        {
            migrationBuilder.Sql($"DROP INDEX CONCURRENTLY IF EXISTS {schema}.\"{name}\"", suppressTransaction: true);
            migrationBuilder.Sql(definition, suppressTransaction: true);
        }

        // Builds the new form under a temporary name, drops the old index and gives the new one its name. Every step can be
        // repeated: a rerun rebuilds the temporary index and replaces whichever index holds the name by then.
        private static void SwapIndexConcurrently(MigrationBuilder migrationBuilder, string schema, string name, string temporaryName, string definition)
        {
            var temporary = $"\"{temporaryName}\"";
            migrationBuilder.Sql($"DROP INDEX CONCURRENTLY IF EXISTS {schema}.{temporary}", suppressTransaction: true);
            migrationBuilder.Sql(string.Format(System.Globalization.CultureInfo.InvariantCulture, definition, temporary), suppressTransaction: true);
            migrationBuilder.Sql($"DROP INDEX CONCURRENTLY IF EXISTS {schema}.\"{name}\"", suppressTransaction: true);
            migrationBuilder.Sql($"ALTER INDEX {schema}.{temporary} RENAME TO \"{name}\"", suppressTransaction: true);
        }

        // Swaps a foreign key in one transaction (old dropped, new added NOT VALID, so the table is never without one), unless
        // the new key already exists with the expected ON DELETE action; then validates it without blocking writes.
        // deleteAction is pg_constraint.confdeltype: c cascade, n set null, r restrict.
        private static void ReplaceForeignKey(MigrationBuilder migrationBuilder, string table, string oldName, string newName, char deleteAction, string definition)
        {
            var dropLeftover = oldName == newName ? "" : $"ALTER TABLE {table} DROP CONSTRAINT IF EXISTS \"{newName}\";";
            migrationBuilder.Sql($"""
                DO $$
                BEGIN
                    IF NOT EXISTS (
                        SELECT 1 FROM pg_constraint
                        WHERE conrelid = '{table}'::regclass AND conname = '{newName}' AND contype = 'f' AND confdeltype = '{deleteAction}'
                    ) THEN
                        ALTER TABLE {table} DROP CONSTRAINT IF EXISTS "{oldName}";
                        {dropLeftover}
                        ALTER TABLE {table} ADD CONSTRAINT "{newName}" {definition} NOT VALID;
                    END IF;
                END $$
                """, suppressTransaction: true);
            migrationBuilder.Sql($"ALTER TABLE {table} VALIDATE CONSTRAINT \"{newName}\"", suppressTransaction: true);
        }

        private static void AddCheck(MigrationBuilder migrationBuilder, string table, string name, string expression)
        {
            migrationBuilder.Sql($"""
                DO $$
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = '{table}'::regclass AND conname = '{name}') THEN
                        ALTER TABLE {table} ADD CONSTRAINT {name} CHECK ({expression}) NOT VALID;
                    END IF;
                END $$
                """, suppressTransaction: true);
            migrationBuilder.Sql($"ALTER TABLE {table} VALIDATE CONSTRAINT {name}", suppressTransaction: true);
        }
    }
}
