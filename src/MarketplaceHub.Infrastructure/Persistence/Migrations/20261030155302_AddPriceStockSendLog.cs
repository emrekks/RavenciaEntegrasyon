using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPriceStockSendLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "price_stock_send_log",
                schema: "integration",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    PlatformCode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    JobId = table.Column<Guid>(type: "uuid", nullable: false),
                    VariantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OfferId = table.Column<Guid>(type: "uuid", nullable: false),
                    Barcode = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    StockCode = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    PreviousQuantity = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: true),
                    PreviousSalePrice = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: true),
                    PreviousListPrice = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: true),
                    NewQuantity = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: true),
                    NewSalePrice = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: true),
                    NewListPrice = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: true),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    Trigger = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ExternalRequestId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Result = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    ErrorCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ErrorMessage = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_price_stock_send_log", x => x.Id);
                    table.ForeignKey(
                        name: "FK_price_stock_send_log_tenants_TenantId",
                        column: x => x.TenantId,
                        principalSchema: "iam",
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_price_stock_send_log_JobId",
                schema: "integration",
                table: "price_stock_send_log",
                column: "JobId");

            migrationBuilder.CreateIndex(
                name: "IX_price_stock_send_log_TenantId_ConnectionId_CreatedAt_Id",
                schema: "integration",
                table: "price_stock_send_log",
                columns: new[] { "TenantId", "ConnectionId", "CreatedAt", "Id" },
                descending: new[] { false, false, true, true });

            migrationBuilder.CreateIndex(
                name: "IX_price_stock_send_log_TenantId_CreatedAt_Id",
                schema: "integration",
                table: "price_stock_send_log",
                columns: new[] { "TenantId", "CreatedAt", "Id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "IX_price_stock_send_log_TenantId_VariantId_CreatedAt_Id",
                schema: "integration",
                table: "price_stock_send_log",
                columns: new[] { "TenantId", "VariantId", "CreatedAt", "Id" },
                descending: new[] { false, false, true, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "price_stock_send_log",
                schema: "integration");
        }
    }
}
