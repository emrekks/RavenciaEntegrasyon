using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddConnectionAttributeDefaults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "connection_attribute_defaults",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExternalAttributeId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    AttributeName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ExternalValueId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ValueName = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    CustomValue = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_connection_attribute_defaults", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_connection_attribute_defaults_TenantId_ConnectionId_Externa~",
                schema: "catalog",
                table: "connection_attribute_defaults",
                columns: new[] { "TenantId", "ConnectionId", "ExternalAttributeId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "connection_attribute_defaults",
                schema: "catalog");
        }
    }
}
