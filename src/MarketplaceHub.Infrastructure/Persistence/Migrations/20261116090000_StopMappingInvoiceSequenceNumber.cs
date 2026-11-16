using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class StopMappingInvoiceSequenceNumber : Migration
    {
        // VERITABANI-DAGITIM-11, expand-contract phase 1: billing.invoices."SequenceNumber" was never written or read (the
        // official number is "InvoiceNumber", from the provider; live 16 Nov 2026: 0 of 2 invoices hold a value). The model
        // stops mapping it here, so no SELECT or INSERT names it any more; the column itself stays, because the previous
        // API and Worker still select it while this release migrates. A later release drops it, marked as phase 2 of this
        // migration (tests/ops/migration-guard.sh): ALTER TABLE billing.invoices DROP COLUMN IF EXISTS "SequenceNumber"
        // (docs/RUNBOOK-GERI-ALMA.md, follow-ups).

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
