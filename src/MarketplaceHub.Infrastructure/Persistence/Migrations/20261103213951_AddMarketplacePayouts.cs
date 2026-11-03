using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMarketplacePayouts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Affiliate",
                schema: "sales",
                table: "marketplace_finance_transactions",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Country",
                schema: "sales",
                table: "marketplace_finance_transactions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Currency",
                schema: "sales",
                table: "marketplace_finance_transactions",
                type: "character varying(8)",
                maxLength: 8,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InvoiceNumber",
                schema: "sales",
                table: "marketplace_finance_transactions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "marketplace_payout_orders",
                schema: "sales",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    PlatformCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ExternalId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    PaymentType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Amount = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    Currency = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    PayoutDate = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RegionCode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    RegionName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    LinesReadAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RawJson = table.Column<string>(type: "text", nullable: false),
                    FirstSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_marketplace_payout_orders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_marketplace_payout_orders_platform_connections_TenantId_Con~",
                        columns: x => new { x.TenantId, x.ConnectionId },
                        principalSchema: "integration",
                        principalTable: "platform_connections",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_marketplace_payout_orders_tenants_TenantId",
                        column: x => x.TenantId,
                        principalSchema: "iam",
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_marketplace_finance_transactions_TenantId_ConnectionId_Pay~1",
                schema: "sales",
                table: "marketplace_finance_transactions",
                columns: new[] { "TenantId", "ConnectionId", "PaymentOrderId" });

            migrationBuilder.CreateIndex(
                name: "IX_marketplace_finance_transactions_TenantId_ConnectionId_Paym~",
                schema: "sales",
                table: "marketplace_finance_transactions",
                columns: new[] { "TenantId", "ConnectionId", "PaymentDate" });

            migrationBuilder.CreateIndex(
                name: "IX_marketplace_payout_orders_TenantId_ConnectionId_ExternalId",
                schema: "sales",
                table: "marketplace_payout_orders",
                columns: new[] { "TenantId", "ConnectionId", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_marketplace_payout_orders_TenantId_ConnectionId_PayoutDate",
                schema: "sales",
                table: "marketplace_payout_orders",
                columns: new[] { "TenantId", "ConnectionId", "PayoutDate" });

            // The stored Trendyol records already carry these fields in their JSON; the mapper fills them from now on.
            // Read only from the record's own JSON, cut to the column length; an absent or empty field stays null.
            migrationBuilder.Sql("""
                UPDATE sales.marketplace_finance_transactions
                SET "Country" = left(NULLIF(btrim("RawJson"::jsonb ->> 'country'), ''), 64),
                    "Affiliate" = left(NULLIF(btrim("RawJson"::jsonb ->> 'affiliate'), ''), 32),
                    "Currency" = left(NULLIF(btrim("RawJson"::jsonb ->> 'currency'), ''), 8),
                    "InvoiceNumber" = left(NULLIF(btrim("RawJson"::jsonb ->> 'commissionInvoiceSerialNumber'), ''), 64)
                WHERE "PlatformCode" = 'TRENDYOL';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "marketplace_payout_orders",
                schema: "sales");

            migrationBuilder.DropIndex(
                name: "IX_marketplace_finance_transactions_TenantId_ConnectionId_Pay~1",
                schema: "sales",
                table: "marketplace_finance_transactions");

            migrationBuilder.DropIndex(
                name: "IX_marketplace_finance_transactions_TenantId_ConnectionId_Paym~",
                schema: "sales",
                table: "marketplace_finance_transactions");

            migrationBuilder.DropColumn(
                name: "Affiliate",
                schema: "sales",
                table: "marketplace_finance_transactions");

            migrationBuilder.DropColumn(
                name: "Country",
                schema: "sales",
                table: "marketplace_finance_transactions");

            migrationBuilder.DropColumn(
                name: "Currency",
                schema: "sales",
                table: "marketplace_finance_transactions");

            migrationBuilder.DropColumn(
                name: "InvoiceNumber",
                schema: "sales",
                table: "marketplace_finance_transactions");
        }
    }
}
