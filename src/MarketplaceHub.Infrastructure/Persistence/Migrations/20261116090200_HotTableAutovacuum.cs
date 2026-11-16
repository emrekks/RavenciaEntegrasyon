using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations;

// VERITABANI-DAGITIM-6, step 3 of docs/RUNBOOK-GERI-ALMA.md section 8: the tables the worker updates all day are vacuumed and
// analysed after 2 % of their rows changed instead of PostgreSQL's 20 % (10 % for analyse), so dead rows and stale
// statistics do not pile up between runs (live 16 Nov 2026: integration.jobs 12,090 rows, 1,258 dead). Storage parameters
// only: ALTER TABLE ... SET takes SHARE UPDATE EXCLUSIVE, which blocks neither reads nor writes; no data changes, and the
// running code is untouched. No model change, so no snapshot change.
// migration-lock-ok: ALTER TABLE ... SET (storage parameters) takes SHARE UPDATE EXCLUSIVE
[DbContext(typeof(AppDbContext))]
[Migration("20261116090200_HotTableAutovacuum")]
public sealed class HotTableAutovacuum : Migration
{
    private static readonly string[] Tables =
    [
        "integration.jobs", "integration.job_attempts", "integration.rate_limit_usage", "integration.inbox_messages",
        "integration.reference_items", "inventory.inventory_items", "inventory.channel_offers",
    ];

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        foreach (var table in Tables)
            migrationBuilder.Sql($"ALTER TABLE {table} SET (autovacuum_vacuum_scale_factor = 0.02, autovacuum_analyze_scale_factor = 0.02)");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        foreach (var table in Tables)
            migrationBuilder.Sql($"ALTER TABLE {table} RESET (autovacuum_vacuum_scale_factor, autovacuum_analyze_scale_factor)");
    }
}
