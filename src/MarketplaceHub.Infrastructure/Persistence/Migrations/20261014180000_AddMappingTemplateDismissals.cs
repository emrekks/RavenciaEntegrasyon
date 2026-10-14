using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMappingTemplateDismissals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "mapping_template_dismissals",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ScopeExternalId = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    LocalId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mapping_template_dismissals", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_mapping_template_dismissals_TenantId_ConnectionId_Kind_Scop~",
                schema: "catalog",
                table: "mapping_template_dismissals",
                columns: new[] { "TenantId", "ConnectionId", "Kind", "ScopeExternalId", "LocalId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "mapping_template_dismissals",
                schema: "catalog");
        }
    }
}
