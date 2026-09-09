using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMarketplaceDeliveryState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExternalIdempotencyKey",
                schema: "billing",
                table: "marketplace_deliveries",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "marketplace_delivery_states",
                schema: "billing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    InvoiceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    PackageId = table.Column<Guid>(type: "uuid", nullable: true),
                    AttemptNumber = table.Column<int>(type: "integer", nullable: false),
                    ExternalIdempotencyKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    RequestHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    DeliveryType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ExternalReference = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ErrorCode = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_marketplace_delivery_states", x => x.Id);
                    table.ForeignKey(
                        name: "FK_marketplace_delivery_states_invoices_TenantId_InvoiceId",
                        columns: x => new { x.TenantId, x.InvoiceId },
                        principalSchema: "billing",
                        principalTable: "invoices",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_marketplace_delivery_states_platform_connections_TenantId_C~",
                        columns: x => new { x.TenantId, x.ConnectionId },
                        principalSchema: "integration",
                        principalTable: "platform_connections",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_marketplace_delivery_states_shipment_packages_TenantId_Pack~",
                        columns: x => new { x.TenantId, x.PackageId },
                        principalSchema: "sales",
                        principalTable: "shipment_packages",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_marketplace_delivery_states_TenantId_ConnectionId",
                schema: "billing",
                table: "marketplace_delivery_states",
                columns: new[] { "TenantId", "ConnectionId" });

            migrationBuilder.CreateIndex(
                name: "IX_marketplace_delivery_states_TenantId_ExternalIdempotencyKey",
                schema: "billing",
                table: "marketplace_delivery_states",
                columns: new[] { "TenantId", "ExternalIdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_marketplace_delivery_states_TenantId_InvoiceId",
                schema: "billing",
                table: "marketplace_delivery_states",
                columns: new[] { "TenantId", "InvoiceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_marketplace_delivery_states_TenantId_PackageId",
                schema: "billing",
                table: "marketplace_delivery_states",
                columns: new[] { "TenantId", "PackageId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "marketplace_delivery_states",
                schema: "billing");

            migrationBuilder.DropColumn(
                name: "ExternalIdempotencyKey",
                schema: "billing",
                table: "marketplace_deliveries");
        }
    }
}
