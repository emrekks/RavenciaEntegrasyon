using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MarketplaceKnownListingValues : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "marketplace_known_listing_values",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    VariantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "text", nullable: true),
                    TitleSource = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    TitleReadAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Description = table.Column<string>(type: "text", nullable: true),
                    DescriptionSource = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    DescriptionReadAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Desi = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: true),
                    DesiSource = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    DesiReadAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_marketplace_known_listing_values", x => x.Id);
                    table.ForeignKey(
                        name: "FK_marketplace_known_listing_values_product_variants_TenantId_~",
                        columns: x => new { x.TenantId, x.VariantId },
                        principalSchema: "catalog",
                        principalTable: "product_variants",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_marketplace_known_listing_values_TenantId_ConnectionId_Vari~",
                schema: "catalog",
                table: "marketplace_known_listing_values",
                columns: new[] { "TenantId", "ConnectionId", "VariantId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_marketplace_known_listing_values_TenantId_VariantId",
                schema: "catalog",
                table: "marketplace_known_listing_values",
                columns: new[] { "TenantId", "VariantId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "marketplace_known_listing_values",
                schema: "catalog");
        }
    }
}
