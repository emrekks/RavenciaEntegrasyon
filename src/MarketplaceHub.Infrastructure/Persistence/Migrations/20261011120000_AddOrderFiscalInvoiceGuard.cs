using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20261011120000_AddOrderFiscalInvoiceGuard")]
public sealed class AddOrderFiscalInvoiceGuard : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "OrderFiscalGuarded",
            schema: "billing",
            table: "invoices",
            type: "boolean",
            nullable: false,
            defaultValue: false);

        // Keep all historical rows. A single existing sale invoice claims the
        // order guard; multiple historical invoices remain untouched and are
        // detected by the workspace as a review-only conflict.
        migrationBuilder.Sql("""
            WITH sale_counts AS (
                SELECT "TenantId", "OrderId", COUNT(*) AS invoice_count
                FROM billing.invoices
                WHERE "OriginalInvoiceId" IS NULL AND "SequencePurpose" = 'SALE'
                GROUP BY "TenantId", "OrderId"
            )
            UPDATE billing.invoices AS invoice
            SET "OrderFiscalGuarded" = TRUE
            FROM sale_counts
            WHERE invoice."TenantId" = sale_counts."TenantId"
              AND invoice."OrderId" = sale_counts."OrderId"
              AND sale_counts.invoice_count = 1
              AND invoice."OriginalInvoiceId" IS NULL
              AND invoice."SequencePurpose" = 'SALE';

            CREATE UNIQUE INDEX "IX_invoices_TenantId_OrderId_ActiveSaleFiscal"
                ON billing.invoices ("TenantId", "OrderId")
                WHERE "OrderFiscalGuarded" = TRUE
                  AND "OriginalInvoiceId" IS NULL
                  AND "SequencePurpose" = 'SALE';
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_invoices_TenantId_OrderId_ActiveSaleFiscal",
            schema: "billing");

        migrationBuilder.DropColumn(
            name: "OrderFiscalGuarded",
            schema: "billing",
            table: "invoices");
    }
}
