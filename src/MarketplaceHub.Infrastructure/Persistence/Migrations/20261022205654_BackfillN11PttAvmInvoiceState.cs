using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BackfillN11PttAvmInvoiceState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The order sync now carries n11's and PTT AVM's own word on the invoice, but only for orders the
            // marketplace changes again; packages already stored kept "Unknown" and read "Fatura durumu bilinmiyor".
            // Their stored snapshots already hold that word, so it is applied once here, only to packages still Unknown:
            // n11 - the package's invoiceLink, empty until an invoice is attached (a package without the field is left);
            // PTT AVM - every live line's IsInvoice: all true is an invoice, a false on any line is none (lines without
            // the flag leave the order alone).
            migrationBuilder.Sql("""
                UPDATE sales.shipment_packages p
                SET "MarketplaceInvoiceStatus" = CASE WHEN coalesce(o."CustomerSnapshotJson"::jsonb ->> 'invoiceLink', '') <> '' THEN 'Invoiced' ELSE 'NotInvoiced' END,
                    "MarketplaceInvoiceObservedAt" = now()
                FROM sales.orders o
                JOIN integration.platform_connections c ON c."TenantId" = o."TenantId" AND c."Id" = o."ConnectionId"
                WHERE p."TenantId" = o."TenantId" AND p."OrderId" = o."Id"
                  AND upper(c."PlatformCode") = 'N11'
                  AND p."MarketplaceInvoiceStatus" = 'Unknown'
                  AND o."CustomerSnapshotJson"::jsonb ? 'invoiceLink';

                WITH flags AS (
                    SELECT l."TenantId", l."OrderId",
                           bool_and(l."SourceSnapshotJson"::jsonb ? 'IsInvoice') AS all_flagged,
                           bool_and(lower(l."SourceSnapshotJson"::jsonb ->> 'IsInvoice') = 'true') AS all_true,
                           bool_or(lower(l."SourceSnapshotJson"::jsonb ->> 'IsInvoice') = 'false') AS any_false
                    FROM sales.order_lines l
                    WHERE l."RawStatus" <> 'Cancelled'
                    GROUP BY l."TenantId", l."OrderId")
                UPDATE sales.shipment_packages p
                SET "MarketplaceInvoiceStatus" = CASE WHEN f.all_true THEN 'Invoiced' ELSE 'NotInvoiced' END,
                    "MarketplaceInvoiceObservedAt" = now()
                FROM flags f
                JOIN integration.platform_connections c ON c."TenantId" = f."TenantId"
                JOIN sales.orders o ON o."TenantId" = f."TenantId" AND o."Id" = f."OrderId" AND o."ConnectionId" = c."Id"
                WHERE p."TenantId" = f."TenantId" AND p."OrderId" = f."OrderId"
                  AND upper(c."PlatformCode") = 'PTTAVM'
                  AND p."MarketplaceInvoiceStatus" = 'Unknown'
                  AND f.all_flagged AND (f.all_true OR f.any_false);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // A data backfill from the stored marketplace snapshots: the earlier "Unknown" is not restored.
        }
    }
}
