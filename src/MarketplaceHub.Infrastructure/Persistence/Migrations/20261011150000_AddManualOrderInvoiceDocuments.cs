using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddManualOrderInvoiceDocuments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "manual_order_invoice_documents",
                schema: "billing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    PackageId = table.Column<Guid>(type: "uuid", nullable: true),
                    ConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    InvoiceNumber = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IssuedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    IdempotencyKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    DocumentType = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    DocumentUrl = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: false),
                    FileAssetId = table.Column<Guid>(type: "uuid", nullable: true),
                    Sha256 = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_manual_order_invoice_documents", x => x.Id);
                    table.UniqueConstraint("AK_manual_order_invoice_documents_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_manual_order_invoice_documents_file_assets_TenantId_FileAss~",
                        columns: x => new { x.TenantId, x.FileAssetId },
                        principalSchema: "ops",
                        principalTable: "file_assets",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_manual_order_invoice_documents_orders_TenantId_OrderId",
                        columns: x => new { x.TenantId, x.OrderId },
                        principalSchema: "sales",
                        principalTable: "orders",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_manual_order_invoice_documents_platform_connections_TenantI~",
                        columns: x => new { x.TenantId, x.ConnectionId },
                        principalSchema: "integration",
                        principalTable: "platform_connections",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_manual_order_invoice_documents_shipment_packages_TenantId_P~",
                        columns: x => new { x.TenantId, x.PackageId },
                        principalSchema: "sales",
                        principalTable: "shipment_packages",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "manual_order_invoice_delivery_states",
                schema: "billing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    PackageId = table.Column<Guid>(type: "uuid", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    RequestHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    ExternalReference = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    ErrorCode = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_manual_order_invoice_delivery_states", x => x.Id);
                    table.ForeignKey(
                        name: "FK_manual_order_invoice_delivery_states_manual_order_invoice_d~",
                        columns: x => new { x.TenantId, x.DocumentId },
                        principalSchema: "billing",
                        principalTable: "manual_order_invoice_documents",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_manual_order_invoice_delivery_states_platform_connections_T~",
                        columns: x => new { x.TenantId, x.ConnectionId },
                        principalSchema: "integration",
                        principalTable: "platform_connections",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_manual_order_invoice_delivery_states_shipment_packages_Tena~",
                        columns: x => new { x.TenantId, x.PackageId },
                        principalSchema: "sales",
                        principalTable: "shipment_packages",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_manual_order_invoice_delivery_states_TenantId_ConnectionId",
                schema: "billing",
                table: "manual_order_invoice_delivery_states",
                columns: new[] { "TenantId", "ConnectionId" });

            migrationBuilder.CreateIndex(
                name: "IX_manual_order_invoice_delivery_states_TenantId_DocumentId_Pa~",
                schema: "billing",
                table: "manual_order_invoice_delivery_states",
                columns: new[] { "TenantId", "DocumentId", "PackageId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_manual_order_invoice_delivery_states_TenantId_IdempotencyKey",
                schema: "billing",
                table: "manual_order_invoice_delivery_states",
                columns: new[] { "TenantId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_manual_order_invoice_delivery_states_TenantId_PackageId",
                schema: "billing",
                table: "manual_order_invoice_delivery_states",
                columns: new[] { "TenantId", "PackageId" });

            migrationBuilder.CreateIndex(
                name: "IX_manual_order_invoice_documents_TenantId_ConnectionId",
                schema: "billing",
                table: "manual_order_invoice_documents",
                columns: new[] { "TenantId", "ConnectionId" });

            migrationBuilder.CreateIndex(
                name: "IX_manual_order_invoice_documents_TenantId_FileAssetId",
                schema: "billing",
                table: "manual_order_invoice_documents",
                columns: new[] { "TenantId", "FileAssetId" });

            migrationBuilder.CreateIndex(
                name: "IX_manual_order_invoice_documents_TenantId_IdempotencyKey",
                schema: "billing",
                table: "manual_order_invoice_documents",
                columns: new[] { "TenantId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_manual_order_invoice_documents_TenantId_OrderId_Active",
                schema: "billing",
                table: "manual_order_invoice_documents",
                columns: new[] { "TenantId", "OrderId", "Status" },
                unique: true,
                filter: "\"Status\" = 'ACTIVE'");

            migrationBuilder.CreateIndex(
                name: "IX_manual_order_invoice_documents_TenantId_PackageId",
                schema: "billing",
                table: "manual_order_invoice_documents",
                columns: new[] { "TenantId", "PackageId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "manual_order_invoice_delivery_states",
                schema: "billing");

            migrationBuilder.DropTable(
                name: "manual_order_invoice_documents",
                schema: "billing");
        }
    }
}
