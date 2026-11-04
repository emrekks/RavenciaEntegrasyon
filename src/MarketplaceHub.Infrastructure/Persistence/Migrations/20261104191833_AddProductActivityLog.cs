using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProductActivityLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "product_change_entries",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    VariantId = table.Column<Guid>(type: "uuid", nullable: true),
                    Kind = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    PreviousStatus = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    NewStatus = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    PreviousSalePrice = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: true),
                    NewSalePrice = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: true),
                    PreviousListPrice = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: true),
                    NewListPrice = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: true),
                    Source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ConnectionId = table.Column<Guid>(type: "uuid", nullable: true),
                    ActorUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Note = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CorrelationId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_product_change_entries", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_stock_ledger_entries_TenantId_OccurredAt_Id",
                schema: "inventory",
                table: "stock_ledger_entries",
                columns: new[] { "TenantId", "OccurredAt", "Id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "IX_return_stock_dispositions_TenantId_CreatedAt_Id",
                schema: "sales",
                table: "return_stock_dispositions",
                columns: new[] { "TenantId", "CreatedAt", "Id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "IX_channel_price_history_TenantId_EffectiveAt_Id",
                schema: "inventory",
                table: "channel_price_history",
                columns: new[] { "TenantId", "EffectiveAt", "Id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "IX_product_change_entries_TenantId_OccurredAt_Id",
                schema: "catalog",
                table: "product_change_entries",
                columns: new[] { "TenantId", "OccurredAt", "Id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "IX_product_change_entries_TenantId_ProductId",
                schema: "catalog",
                table: "product_change_entries",
                columns: new[] { "TenantId", "ProductId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "product_change_entries",
                schema: "catalog");

            migrationBuilder.DropIndex(
                name: "IX_stock_ledger_entries_TenantId_OccurredAt_Id",
                schema: "inventory",
                table: "stock_ledger_entries");

            migrationBuilder.DropIndex(
                name: "IX_return_stock_dispositions_TenantId_CreatedAt_Id",
                schema: "sales",
                table: "return_stock_dispositions");

            migrationBuilder.DropIndex(
                name: "IX_channel_price_history_TenantId_EffectiveAt_Id",
                schema: "inventory",
                table: "channel_price_history");
        }
    }
}
