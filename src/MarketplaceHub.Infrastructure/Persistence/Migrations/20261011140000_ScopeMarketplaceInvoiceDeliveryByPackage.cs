using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20261011140000_ScopeMarketplaceInvoiceDeliveryByPackage")]
public sealed class ScopeMarketplaceInvoiceDeliveryByPackage : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Preserve the target identity of historical package-specific work.
        // Older order-level attempts can only be attached when their stored
        // external reference matches a unique package identifier.
        migrationBuilder.Sql("""
            UPDATE billing.marketplace_delivery_states AS state
            SET "PackageId" = invoice."PackageId"
            FROM billing.invoices AS invoice
            WHERE state."TenantId" = invoice."TenantId"
              AND state."InvoiceId" = invoice."Id"
              AND state."PackageId" IS NULL
              AND invoice."PackageId" IS NOT NULL;

            UPDATE billing.marketplace_delivery_states AS state
            SET "PackageId" = package."Id"
            FROM billing.invoices AS invoice
            CROSS JOIN sales.shipment_packages AS package
            WHERE state."TenantId" = invoice."TenantId"
              AND state."InvoiceId" = invoice."Id"
              AND state."PackageId" IS NULL
              AND invoice."PackageId" IS NULL
              AND package."TenantId" = invoice."TenantId"
              AND package."OrderId" = invoice."OrderId"
              AND package."ExternalPackageId" = state."ExternalReference";

            UPDATE billing.marketplace_deliveries AS delivery
            SET "PackageId" = invoice."PackageId"
            FROM billing.invoices AS invoice
            WHERE delivery."TenantId" = invoice."TenantId"
              AND delivery."InvoiceId" = invoice."Id"
              AND delivery."PackageId" IS NULL
              AND invoice."PackageId" IS NOT NULL;

            UPDATE billing.marketplace_deliveries AS delivery
            SET "PackageId" = package."Id"
            FROM billing.invoices AS invoice
            CROSS JOIN sales.shipment_packages AS package
            WHERE delivery."TenantId" = invoice."TenantId"
              AND delivery."InvoiceId" = invoice."Id"
              AND delivery."PackageId" IS NULL
              AND invoice."PackageId" IS NULL
              AND package."TenantId" = invoice."TenantId"
              AND package."OrderId" = invoice."OrderId"
              AND package."ExternalPackageId" = delivery."ExternalReference";
            """);

        migrationBuilder.DropIndex(
            name: "IX_marketplace_deliveries_TenantId_InvoiceId_AttemptNumber",
            schema: "billing",
            table: "marketplace_deliveries");
        migrationBuilder.CreateIndex(
            name: "IX_marketplace_deliveries_TenantId_InvoiceId_PackageId_AttemptNumber",
            schema: "billing",
            table: "marketplace_deliveries",
            columns: new[] { "TenantId", "InvoiceId", "PackageId", "AttemptNumber" },
            unique: true);

        migrationBuilder.DropIndex(
            name: "IX_marketplace_delivery_states_TenantId_InvoiceId",
            schema: "billing",
            table: "marketplace_delivery_states");
        migrationBuilder.CreateIndex(
            name: "IX_marketplace_delivery_states_TenantId_InvoiceId_PackageId",
            schema: "billing",
            table: "marketplace_delivery_states",
            columns: new[] { "TenantId", "InvoiceId", "PackageId" },
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_marketplace_delivery_states_TenantId_InvoiceId_PackageId",
            schema: "billing",
            table: "marketplace_delivery_states");
        migrationBuilder.CreateIndex(
            name: "IX_marketplace_delivery_states_TenantId_InvoiceId",
            schema: "billing",
            table: "marketplace_delivery_states",
            columns: new[] { "TenantId", "InvoiceId" },
            unique: true);

        migrationBuilder.DropIndex(
            name: "IX_marketplace_deliveries_TenantId_InvoiceId_PackageId_AttemptNumber",
            schema: "billing",
            table: "marketplace_deliveries");
        migrationBuilder.CreateIndex(
            name: "IX_marketplace_deliveries_TenantId_InvoiceId_AttemptNumber",
            schema: "billing",
            table: "marketplace_deliveries",
            columns: new[] { "TenantId", "InvoiceId", "AttemptNumber" },
            unique: true);
    }
}
