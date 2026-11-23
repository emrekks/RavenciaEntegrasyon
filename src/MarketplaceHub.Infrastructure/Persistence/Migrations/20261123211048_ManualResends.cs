using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ManualResends : Migration
    {
        // The re-shipments the owner adds by hand under Siparişler › Yeniden Gönderimler (ManualResendService, owner
        // 23 Nov 2026). Additive only: a new, empty table, no row of another table changes. Its index is built with it (an
        // empty table: nothing to build CONCURRENTLY). Its one key, to sales.orders, cascades, so the code running before
        // this deploy (which knows nothing of the table) still deletes orders as it did, and every reset takes its rows
        // with their orders. The key takes a SHARE ROW EXCLUSIVE lock on sales.orders for an instant (empty table, nothing
        // to validate), bounded by DatabaseMigrator's lock_timeout and retries.
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "manual_resends",
                schema: "sales",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ShippedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_manual_resends", x => x.Id);
                    table.ForeignKey(
                        name: "FK_manual_resends_orders_TenantId_OrderId",
                        columns: x => new { x.TenantId, x.OrderId },
                        principalSchema: "sales",
                        principalTable: "orders",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_manual_resends_TenantId_OrderId",
                schema: "sales",
                table: "manual_resends",
                columns: new[] { "TenantId", "OrderId" },
                unique: true);
        }

        // Drops the hand-added re-shipments (panel notes; the owner adds them again if needed).
        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "manual_resends",
                schema: "sales");
        }
    }
}
