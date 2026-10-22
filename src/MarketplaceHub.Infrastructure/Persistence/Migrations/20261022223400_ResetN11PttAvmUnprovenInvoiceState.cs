using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ResetN11PttAvmUnprovenInvoiceState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // n11's empty invoiceLink and PTT AVM's IsInvoice=false were taken for "no invoice" (the backfill of
            // 20261022205654 and the order sync), but an invoice uploaded in either marketplace's own panel leaves them
            // so (live, 23 Oct 2026: n11 order 241612122428 and PTT AVM orders with invoices read empty / false). Only
            // those two wrote NotInvoiced for these platforms, so every n11 and PTT AVM package NotInvoiced is set
            // back to Unknown; a hand-set status is untouched.
            migrationBuilder.Sql("""
                UPDATE sales.shipment_packages p
                SET "MarketplaceInvoiceStatus" = 'Unknown', "MarketplaceInvoiceObservedAt" = NULL
                FROM integration.platform_connections c
                WHERE c."TenantId" = p."TenantId" AND c."Id" = p."ConnectionId"
                  AND upper(c."PlatformCode") IN ('N11', 'PTTAVM')
                  AND p."MarketplaceInvoiceStatus" = 'NotInvoiced';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // A data repair: the unproven "not invoiced" is not restored.
        }
    }
}
