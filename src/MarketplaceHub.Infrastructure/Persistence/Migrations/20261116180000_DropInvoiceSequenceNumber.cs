using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations;

// VERITABANI-DAGITIM-11, expand-contract phase 2: billing.invoices."SequenceNumber" (bigint, nullable, no default) is
// dropped. It was never written or read; the model stopped mapping it in 20261116090000_StopMappingInvoiceSequenceNumber
// (release 6802b4706b64, live 16 Nov 2026 10:33Z), and every image since (live ce8d854e99b4 and the release before it)
// names it nowhere, so the API and Worker that keep running while this migrates, and an image rollback after it, never
// select it. Live 16 Nov 2026, read-only: 0 of 7 invoices hold a value. Repo, deploy, backup, restore and reporting
// scripts name it nowhere.
// DROP COLUMN only takes a brief ACCESS EXCLUSIVE lock (no table rewrite); a short lock_timeout makes it give way to the
// running application instead of queueing every invoice write behind it (DatabaseMigrator retries the run). Re-runnable:
// IF EXISTS. No model change, so no snapshot change.
// expand-contract: phase 2 of 20261116090000_StopMappingInvoiceSequenceNumber
[DbContext(typeof(AppDbContext))]
[Migration("20261116180000_DropInvoiceSequenceNumber")]
public sealed class DropInvoiceSequenceNumber : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("SET LOCAL lock_timeout = '5s'");
        migrationBuilder.Sql("ALTER TABLE billing.invoices DROP COLUMN IF EXISTS \"SequenceNumber\"");
    }

    // Puts the empty nullable column back, as it was before (the model still does not map it).
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("SET LOCAL lock_timeout = '5s'");
        migrationBuilder.Sql("ALTER TABLE billing.invoices ADD COLUMN IF NOT EXISTS \"SequenceNumber\" bigint NULL");
    }
}
