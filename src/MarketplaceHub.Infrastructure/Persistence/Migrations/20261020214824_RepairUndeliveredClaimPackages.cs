using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RepairUndeliveredClaimPackages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Trendyol orders rebuilt from a "Teslim edilemeyen gönderi" claim (reason UNDELIVERED) were stored as
            // delivered, with the claim's last change as their delivery time (nine live orders on 20 Oct 2026). Only rows
            // that came from that fallback are touched: a Trendyol package still "Delivered" whose status time is exactly
            // its undelivered claim's last change. They become undelivered; nothing reached the customer.
            migrationBuilder.Sql("""
                CREATE TEMP TABLE repair_undelivered_packages ON COMMIT DROP AS
                SELECT DISTINCT p."Id" AS "PackageId", p."OrderId", p."TenantId"
                FROM sales.shipment_packages p
                JOIN sales.return_claims c ON c."TenantId" = p."TenantId" AND c."OrderId" = p."OrderId"
                JOIN integration.platform_connections pc ON pc."TenantId" = c."TenantId" AND pc."Id" = c."ConnectionId"
                WHERE upper(pc."PlatformCode") = 'TRENDYOL'
                  AND upper(c."ReasonCode") = 'UNDELIVERED'
                  AND p."RawStatus" = 'Delivered'
                  AND p."StatusOccurredAt" = c."LastRemoteModifiedAt";

                UPDATE sales.package_line_allocations a SET "DeliveredQuantity" = 0
                FROM repair_undelivered_packages r WHERE a."TenantId" = r."TenantId" AND a."PackageId" = r."PackageId";

                UPDATE sales.shipment_packages p
                SET "Status" = 'Undelivered', "RawStatus" = 'UnDelivered', "Version" = p."Version" + 1, "UpdatedAt" = now()
                FROM repair_undelivered_packages r WHERE p."TenantId" = r."TenantId" AND p."Id" = r."PackageId";

                UPDATE sales.orders o SET "DerivedStatus" = 'UNDELIVERED', "Version" = o."Version" + 1, "UpdatedAt" = now()
                FROM (SELECT DISTINCT "TenantId", "OrderId" FROM repair_undelivered_packages) r
                WHERE o."TenantId" = r."TenantId" AND o."Id" = r."OrderId" AND o."DerivedStatus" = 'DELIVERED'
                  AND NOT EXISTS (SELECT 1 FROM sales.shipment_packages other
                                  WHERE other."TenantId" = o."TenantId" AND other."OrderId" = o."Id"
                                    AND other."Status" NOT IN ('Undelivered', 'Cancelled'));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // A data repair: the wrong "delivered" projection is not restored.
        }
    }
}
