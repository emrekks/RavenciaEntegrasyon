using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ClearHepsiburadaShiftedInvoiceSourceTimes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Hepsiburada's order detail has no update time, so its invoice observation carries the order date as the
            // source time. Before the Turkey-time fix (b28cd791, 20 Oct 2026) that date was stored three hours late;
            // read correctly now it is "older" than the stored one, and every later observation was dropped as stale
            // (live, 23 Oct 2026: order 4607432281 stored 20:36Z for an order placed 17:36Z, no Hepsiburada package
            // observed since 21 Oct). Clearing the source time lets the next read apply by observation time.
            migrationBuilder.Sql("""
                UPDATE sales.shipment_packages p
                SET "MarketplaceInvoiceSourceUpdatedAt" = NULL
                FROM integration.platform_connections c
                WHERE c."TenantId" = p."TenantId" AND c."Id" = p."ConnectionId"
                  AND upper(c."PlatformCode") = 'HEPSIBURADA'
                  AND p."MarketplaceInvoiceSourceUpdatedAt" IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // A data repair: the shifted source times are not restored.
        }
    }
}
