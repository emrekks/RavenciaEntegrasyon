using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCommissionTariffSelections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "commission_tariff_picks",
                schema: "catalog",
                columns: table => new
                {
                    TariffId = table.Column<Guid>(type: "uuid", nullable: false),
                    RowId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Tier = table.Column<int>(type: "integer", nullable: true),
                    Price = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_commission_tariff_picks", x => new { x.TariffId, x.RowId });
                    table.ForeignKey(
                        name: "FK_commission_tariff_picks_commission_tariff_rows_RowId",
                        column: x => x.RowId,
                        principalSchema: "catalog",
                        principalTable: "commission_tariff_rows",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_commission_tariff_picks_commission_tariffs_TenantId_TariffId",
                        columns: x => new { x.TenantId, x.TariffId },
                        principalSchema: "catalog",
                        principalTable: "commission_tariffs",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "commission_tariff_selections",
                schema: "catalog",
                columns: table => new
                {
                    TariffId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    BulkKind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    BulkTier = table.Column<int>(type: "integer", nullable: true),
                    MinRate = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: true),
                    MinAmount = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: true),
                    MaxRate = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: true),
                    MaxAmount = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_commission_tariff_selections", x => x.TariffId);
                    table.ForeignKey(
                        name: "FK_commission_tariff_selections_commission_tariffs_TenantId_Ta~",
                        columns: x => new { x.TenantId, x.TariffId },
                        principalSchema: "catalog",
                        principalTable: "commission_tariffs",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "commission_tariff_source_files",
                schema: "catalog",
                columns: table => new
                {
                    TariffId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Content = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_commission_tariff_source_files", x => x.TariffId);
                    table.ForeignKey(
                        name: "FK_commission_tariff_source_files_commission_tariffs_TenantId_~",
                        columns: x => new { x.TenantId, x.TariffId },
                        principalSchema: "catalog",
                        principalTable: "commission_tariffs",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_commission_tariff_picks_RowId",
                schema: "catalog",
                table: "commission_tariff_picks",
                column: "RowId");

            migrationBuilder.CreateIndex(
                name: "IX_commission_tariff_picks_TenantId_TariffId",
                schema: "catalog",
                table: "commission_tariff_picks",
                columns: new[] { "TenantId", "TariffId" });

            migrationBuilder.CreateIndex(
                name: "IX_commission_tariff_selections_TenantId_TariffId",
                schema: "catalog",
                table: "commission_tariff_selections",
                columns: new[] { "TenantId", "TariffId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_commission_tariff_source_files_TenantId_TariffId",
                schema: "catalog",
                table: "commission_tariff_source_files",
                columns: new[] { "TenantId", "TariffId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "commission_tariff_picks",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "commission_tariff_selections",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "commission_tariff_source_files",
                schema: "catalog");
        }
    }
}
