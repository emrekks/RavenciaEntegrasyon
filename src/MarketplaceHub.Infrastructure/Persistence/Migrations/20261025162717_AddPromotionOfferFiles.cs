using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPromotionOfferFiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "promotion_offer_files",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    PlatformCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    FileName = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: false),
                    FileHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PeriodText = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    UploadedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UploadedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    RowCount = table.Column<int>(type: "integer", nullable: false),
                    RowsJson = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_promotion_offer_files", x => x.Id);
                    table.ForeignKey(
                        name: "FK_promotion_offer_files_platform_connections_TenantId_Connect~",
                        columns: x => new { x.TenantId, x.ConnectionId },
                        principalSchema: "integration",
                        principalTable: "platform_connections",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_promotion_offer_files_tenants_TenantId",
                        column: x => x.TenantId,
                        principalSchema: "iam",
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_promotion_offer_files_TenantId_ConnectionId_Kind",
                schema: "catalog",
                table: "promotion_offer_files",
                columns: new[] { "TenantId", "ConnectionId", "Kind" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "promotion_offer_files",
                schema: "catalog");
        }
    }
}
