using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20261006150000_AddManualInvoiceWorkspaceStatus")]
public sealed class AddManualInvoiceWorkspaceStatus : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.AddColumn<string>(
            name: "ManualInvoiceStatus",
            schema: "sales",
            table: "shipment_packages",
            type: "character varying(16)",
            maxLength: 16,
            nullable: true);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropColumn(
            name: "ManualInvoiceStatus",
            schema: "sales",
            table: "shipment_packages");
}
