using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddChannelInventoryObservations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "channel_inventory_observations",
                schema: "inventory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    VariantId = table.Column<Guid>(type: "uuid", nullable: false),
                    LocationId = table.Column<Guid>(type: "uuid", nullable: true),
                    ExternalLocationId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Quantity = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    ObservedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_channel_inventory_observations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_channel_inventory_observations_inventory_locations_TenantId~",
                        columns: x => new { x.TenantId, x.LocationId },
                        principalSchema: "inventory",
                        principalTable: "inventory_locations",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_channel_inventory_observations_product_variants_TenantId_Va~",
                        columns: x => new { x.TenantId, x.VariantId },
                        principalSchema: "catalog",
                        principalTable: "product_variants",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_channel_inventory_observations_TenantId_ConnectionId_Observ~",
                schema: "inventory",
                table: "channel_inventory_observations",
                columns: new[] { "TenantId", "ConnectionId", "ObservedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_channel_inventory_observations_TenantId_ConnectionId_Varian~",
                schema: "inventory",
                table: "channel_inventory_observations",
                columns: new[] { "TenantId", "ConnectionId", "VariantId", "ExternalLocationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_channel_inventory_observations_TenantId_LocationId",
                schema: "inventory",
                table: "channel_inventory_observations",
                columns: new[] { "TenantId", "LocationId" });

            migrationBuilder.CreateIndex(
                name: "IX_channel_inventory_observations_TenantId_VariantId",
                schema: "inventory",
                table: "channel_inventory_observations",
                columns: new[] { "TenantId", "VariantId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "channel_inventory_observations",
                schema: "inventory");
        }
    }
}
