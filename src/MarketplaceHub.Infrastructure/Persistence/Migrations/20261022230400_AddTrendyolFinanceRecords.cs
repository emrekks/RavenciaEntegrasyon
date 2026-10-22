using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTrendyolFinanceRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "marketplace_cargo_invoice_items",
                schema: "sales",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    InvoiceSerialNumber = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    LineNo = table.Column<int>(type: "integer", nullable: false),
                    ShipmentPackageType = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    ParcelUniqueId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    OrderNumber = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Amount = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    Desi = table.Column<int>(type: "integer", nullable: true),
                    RawJson = table.Column<string>(type: "text", nullable: false),
                    FetchedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_marketplace_cargo_invoice_items", x => x.Id);
                    table.ForeignKey(
                        name: "FK_marketplace_cargo_invoice_items_platform_connections_Tenant~",
                        columns: x => new { x.TenantId, x.ConnectionId },
                        principalSchema: "integration",
                        principalTable: "platform_connections",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_marketplace_cargo_invoice_items_tenants_TenantId",
                        column: x => x.TenantId,
                        principalSchema: "iam",
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "marketplace_finance_sync_states",
                schema: "integration",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    CoveredFrom = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CoveredThrough = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastSyncedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RejectedTypes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    LastError = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    LastErrorAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_marketplace_finance_sync_states", x => new { x.TenantId, x.ConnectionId });
                    table.ForeignKey(
                        name: "FK_marketplace_finance_sync_states_platform_connections_Tenant~",
                        columns: x => new { x.TenantId, x.ConnectionId },
                        principalSchema: "integration",
                        principalTable: "platform_connections",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_marketplace_finance_sync_states_tenants_TenantId",
                        column: x => x.TenantId,
                        principalSchema: "iam",
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "marketplace_finance_transactions",
                schema: "sales",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    PlatformCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ExternalId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    RequestedType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TransactionSubType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    TransactionType = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Category = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    TransactionDate = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    OrderNumber = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ShipmentPackageId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Barcode = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    Debt = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    Credit = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    CommissionRate = table.Column<decimal>(type: "numeric(9,4)", precision: 9, scale: 4, nullable: true),
                    CommissionAmount = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: true),
                    SellerRevenue = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: true),
                    PaymentOrderId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    PaymentDate = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RawJson = table.Column<string>(type: "text", nullable: false),
                    FirstSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_marketplace_finance_transactions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_marketplace_finance_transactions_platform_connections_Tenan~",
                        columns: x => new { x.TenantId, x.ConnectionId },
                        principalSchema: "integration",
                        principalTable: "platform_connections",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_marketplace_finance_transactions_tenants_TenantId",
                        column: x => x.TenantId,
                        principalSchema: "iam",
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_marketplace_cargo_invoice_items_TenantId_ConnectionId_Invoi~",
                schema: "sales",
                table: "marketplace_cargo_invoice_items",
                columns: new[] { "TenantId", "ConnectionId", "InvoiceSerialNumber", "LineNo" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_marketplace_cargo_invoice_items_TenantId_ConnectionId_Order~",
                schema: "sales",
                table: "marketplace_cargo_invoice_items",
                columns: new[] { "TenantId", "ConnectionId", "OrderNumber" });

            migrationBuilder.CreateIndex(
                name: "IX_marketplace_finance_transactions_TenantId_ConnectionId_Orde~",
                schema: "sales",
                table: "marketplace_finance_transactions",
                columns: new[] { "TenantId", "ConnectionId", "OrderNumber" });

            migrationBuilder.CreateIndex(
                name: "IX_marketplace_finance_transactions_TenantId_ConnectionId_Sour~",
                schema: "sales",
                table: "marketplace_finance_transactions",
                columns: new[] { "TenantId", "ConnectionId", "Source", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_marketplace_finance_transactions_TenantId_ConnectionId_Tran~",
                schema: "sales",
                table: "marketplace_finance_transactions",
                columns: new[] { "TenantId", "ConnectionId", "TransactionDate" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "marketplace_cargo_invoice_items",
                schema: "sales");

            migrationBuilder.DropTable(
                name: "marketplace_finance_sync_states",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "marketplace_finance_transactions",
                schema: "sales");
        }
    }
}
