using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    // Job types that every marketplace shares lose their TRENDYOL_ prefix. Only the stored job type
    // changes; the model is untouched, so this migration carries no model snapshot of its own.
    [DbContext(typeof(AppDbContext))]
    [Migration("20261015200000_RenameSharedJobTypes")]
    public partial class RenameSharedJobTypes : Migration
    {
        private static readonly (string Old, string New)[] Renames =
        [
            ("TRENDYOL_PRODUCT_SYNC", "PRODUCT_SYNC"),
            ("TRENDYOL_ORDER_STATUS_SYNC", "ORDER_STATUS_SYNC"),
            ("TRENDYOL_ORDER_RECONCILIATION", "ORDER_RECONCILIATION"),
            ("TRENDYOL_ORDER_INVOICE_RECONCILIATION", "ORDER_INVOICE_RECONCILIATION"),
            ("TRENDYOL_SHIPMENT_ACTION", "SHIPMENT_ACTION"),
            ("TRENDYOL_RETURN_SYNC", "RETURN_SYNC"),
            ("TRENDYOL_RETURN_STATUS_SYNC", "RETURN_STATUS_SYNC"),
            ("TRENDYOL_RETURN_RECONCILIATION", "RETURN_RECONCILIATION"),
            ("TRENDYOL_RETURN_ACTION", "RETURN_ACTION"),
            ("TRENDYOL_PRODUCT_CREATE", "PRODUCT_CREATE"),
            ("TRENDYOL_PRODUCT_APPROVAL_RECONCILE", "PRODUCT_APPROVAL_RECONCILE"),
            ("TRENDYOL_PRODUCT_UPDATE", "PRODUCT_UPDATE"),
            ("TRENDYOL_PRODUCT_ARCHIVE", "PRODUCT_ARCHIVE"),
            ("TRENDYOL_PRICE_INVENTORY_SYNC", "PRICE_INVENTORY_SYNC"),
            ("TRENDYOL_STOCK_RECONCILIATION", "STOCK_RECONCILIATION")
        ];

        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var (oldName, newName) in Renames)
                migrationBuilder.Sql($"UPDATE integration.jobs SET \"JobType\" = '{newName}' WHERE \"JobType\" = '{oldName}';");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var (oldName, newName) in Renames)
                migrationBuilder.Sql($"UPDATE integration.jobs SET \"JobType\" = '{oldName}' WHERE \"JobType\" = '{newName}';");
        }
    }
}
