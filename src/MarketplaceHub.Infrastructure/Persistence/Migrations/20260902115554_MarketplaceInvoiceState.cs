using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MarketplaceInvoiceState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MarketplaceInvoiceNumber",
                schema: "sales",
                table: "shipment_packages",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "MarketplaceInvoiceObservedAt",
                schema: "sales",
                table: "shipment_packages",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MarketplaceInvoiceRawStatus",
                schema: "sales",
                table: "shipment_packages",
                type: "character varying(96)",
                maxLength: 96,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "MarketplaceInvoiceSourceUpdatedAt",
                schema: "sales",
                table: "shipment_packages",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MarketplaceInvoiceStatus",
                schema: "sales",
                table: "shipment_packages",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Unknown");

            migrationBuilder.AddColumn<string>(
                name: "MarketplaceInvoiceUrl",
                schema: "sales",
                table: "shipment_packages",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MarketplaceInvoiceNumber",
                schema: "sales",
                table: "shipment_packages");

            migrationBuilder.DropColumn(
                name: "MarketplaceInvoiceObservedAt",
                schema: "sales",
                table: "shipment_packages");

            migrationBuilder.DropColumn(
                name: "MarketplaceInvoiceRawStatus",
                schema: "sales",
                table: "shipment_packages");

            migrationBuilder.DropColumn(
                name: "MarketplaceInvoiceSourceUpdatedAt",
                schema: "sales",
                table: "shipment_packages");

            migrationBuilder.DropColumn(
                name: "MarketplaceInvoiceStatus",
                schema: "sales",
                table: "shipment_packages");

            migrationBuilder.DropColumn(
                name: "MarketplaceInvoiceUrl",
                schema: "sales",
                table: "shipment_packages");
        }
    }
}
