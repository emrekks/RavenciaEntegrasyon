using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPanelNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "panel_notifications",
                schema: "ops",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ClientKey = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Severity = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Category = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Message = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Href = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ActionLabel = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    PlatformCode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    StoreName = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    Count = table.Column<int>(type: "integer", nullable: false),
                    Payload = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReadAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DismissedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_panel_notifications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_panel_notifications_tenants_TenantId",
                        column: x => x.TenantId,
                        principalSchema: "iam",
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_panel_notifications_CreatedAt",
                schema: "ops",
                table: "panel_notifications",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_panel_notifications_TenantId_ClientKey",
                schema: "ops",
                table: "panel_notifications",
                columns: new[] { "TenantId", "ClientKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_panel_notifications_TenantId_CreatedAt_Id",
                schema: "ops",
                table: "panel_notifications",
                columns: new[] { "TenantId", "CreatedAt", "Id" },
                descending: new[] { false, true, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "panel_notifications",
                schema: "ops");
        }
    }
}
