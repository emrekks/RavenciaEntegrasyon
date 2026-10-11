using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMarketplaceInvoiceReferenceHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "marketplace_invoice_reference_history",
                schema: "sales",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    PackageId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    InvoiceNumber = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    InvoiceUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    ChangeReason = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ChangedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_marketplace_invoice_reference_history", x => x.Id);
                    table.CheckConstraint("ck_marketplace_invoice_reference_history_has_reference", "\"InvoiceNumber\" IS NOT NULL OR \"InvoiceUrl\" IS NOT NULL");
                    table.ForeignKey(
                        name: "FK_marketplace_invoice_reference_history_orders_TenantId_Order~",
                        columns: x => new { x.TenantId, x.OrderId },
                        principalSchema: "sales",
                        principalTable: "orders",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_marketplace_invoice_reference_history_platform_connections_~",
                        columns: x => new { x.TenantId, x.ConnectionId },
                        principalSchema: "integration",
                        principalTable: "platform_connections",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_marketplace_invoice_reference_history_shipment_packages_Ten~",
                        columns: x => new { x.TenantId, x.PackageId },
                        principalSchema: "sales",
                        principalTable: "shipment_packages",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_marketplace_invoice_reference_history_TenantId_ConnectionId",
                schema: "sales",
                table: "marketplace_invoice_reference_history",
                columns: new[] { "TenantId", "ConnectionId" });

            migrationBuilder.CreateIndex(
                name: "IX_marketplace_invoice_reference_history_TenantId_OrderId",
                schema: "sales",
                table: "marketplace_invoice_reference_history",
                columns: new[] { "TenantId", "OrderId" });

            migrationBuilder.CreateIndex(
                name: "IX_marketplace_invoice_reference_history_TenantId_PackageId_Ch~",
                schema: "sales",
                table: "marketplace_invoice_reference_history",
                columns: new[] { "TenantId", "PackageId", "ChangedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "marketplace_invoice_reference_history",
                schema: "sales");
        }
    }
}
