using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20261010180000_AddFiscalInvoicePackageUniqueness")]
public sealed class AddFiscalInvoicePackageUniqueness : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Older Shopify entries were panel-only tracking records, not fiscal
        // submissions. Reclassifying them keeps the fiscal uniqueness rule
        // from treating those records as issued invoices.
        migrationBuilder.Sql("""
            UPDATE billing.invoices AS invoice
            SET "SequencePurpose" = 'SHOPIFY_MANUAL_TRACKING'
            FROM integration.platform_connections AS connection
            WHERE invoice."TenantId" = connection."TenantId"
              AND invoice."ProviderConnectionId" = connection."Id"
              AND connection."PlatformCode" = 'SHOPIFY'
              AND invoice."OriginalInvoiceId" IS NULL
              AND invoice."SequencePurpose" = 'SALE'
              AND invoice."Status" IN ('Draft', 'ValidationFailed')
              AND invoice."ExternalReference" IS NULL
              AND invoice."InvoiceNumber" IS NULL
              AND invoice."EttnUuid" IS NULL
              AND NOT EXISTS (
                    SELECT 1
                    FROM billing.invoice_documents AS document
                    WHERE document."TenantId" = invoice."TenantId"
                      AND document."InvoiceId" = invoice."Id"
              );
            """);

        // Older versions could mark an invoice complete when the fiscal
        // provider accepted it, before a marketplace delivery was confirmed.
        // Preserve the fiscal record but move unproven deliveries back into
        // the reviewable delivery-pending state. Never automatically resend.
        migrationBuilder.Sql("""
            UPDATE billing.invoices AS invoice
            SET "Status" = 'MarketplacePending',
                "LastErrorCode" = COALESCE(invoice."LastErrorCode", 'INVOICE_MARKETPLACE_DELIVERY_UNVERIFIED'),
                "Version" = invoice."Version" + 1,
                "UpdatedAt" = CURRENT_TIMESTAMP
            WHERE invoice."OriginalInvoiceId" IS NULL
              AND invoice."SequencePurpose" = 'SALE'
              AND invoice."Status" = 'Completed'
              AND NOT EXISTS (
                    SELECT 1
                    FROM billing.marketplace_delivery_states AS state
                    WHERE state."TenantId" = invoice."TenantId"
                      AND state."InvoiceId" = invoice."Id"
                      AND state."Status" = 'CONFIRMED'
              )
              AND NOT EXISTS (
                    SELECT 1
                    FROM billing.marketplace_deliveries AS delivery
                    WHERE delivery."TenantId" = invoice."TenantId"
                      AND delivery."InvoiceId" = invoice."Id"
                      AND delivery."Status" = 'CONFIRMED'
              );
            """);

        migrationBuilder.Sql("""
            DO $$
            BEGIN
                IF EXISTS (
                    SELECT 1
                    FROM billing.invoices
                    WHERE "PackageId" IS NOT NULL
                      AND "OriginalInvoiceId" IS NULL
                      AND "SequencePurpose" = 'SALE'
                    GROUP BY "TenantId", "PackageId"
                    HAVING COUNT(*) > 1
                ) THEN
                    RAISE EXCEPTION 'Duplicate fiscal sale invoices exist for one or more tenant packages; review invoice records before applying uniqueness.';
                END IF;
            END $$;
            CREATE UNIQUE INDEX "IX_invoices_TenantId_PackageId_ActiveSaleFiscal"
                ON billing.invoices ("TenantId", "PackageId")
                WHERE "PackageId" IS NOT NULL
                  AND "OriginalInvoiceId" IS NULL
                  AND "SequencePurpose" = 'SALE';
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP INDEX IF EXISTS billing.\"IX_invoices_TenantId_PackageId_ActiveSaleFiscal\";");
    }
}
