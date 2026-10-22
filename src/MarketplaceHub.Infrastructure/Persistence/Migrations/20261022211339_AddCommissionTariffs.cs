using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCommissionTariffs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "commission_tariffs",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    PlatformCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PeriodStart = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PeriodEnd = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PeriodText = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    FileName = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: false),
                    FileHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    UploadedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UploadedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    RowCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_commission_tariffs", x => x.Id);
                    table.UniqueConstraint("AK_commission_tariffs_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_commission_tariffs_platform_connections_TenantId_Connection~",
                        columns: x => new { x.TenantId, x.ConnectionId },
                        principalSchema: "integration",
                        principalTable: "platform_connections",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_commission_tariffs_tenants_TenantId",
                        column: x => x.TenantId,
                        principalSchema: "iam",
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "commission_tariff_rows",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    TariffId = table.Column<Guid>(type: "uuid", nullable: false),
                    Barcode = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    SellerSku = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    ModelCode = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    ProductName = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Size = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    Tier1Min = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: true),
                    Tier2Max = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: true),
                    Tier2Min = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: true),
                    Tier3Max = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: true),
                    Tier3Min = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: true),
                    Tier4Max = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: true),
                    Rate1 = table.Column<decimal>(type: "numeric(7,4)", precision: 7, scale: 4, nullable: true),
                    Rate2 = table.Column<decimal>(type: "numeric(7,4)", precision: 7, scale: 4, nullable: true),
                    Rate3 = table.Column<decimal>(type: "numeric(7,4)", precision: 7, scale: 4, nullable: true),
                    Rate4 = table.Column<decimal>(type: "numeric(7,4)", precision: 7, scale: 4, nullable: true),
                    BasisPrice = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: true),
                    CurrentRate = table.Column<decimal>(type: "numeric(7,4)", precision: 7, scale: 4, nullable: true),
                    CurrentSalePrice = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: true),
                    TariffGroup = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_commission_tariff_rows", x => x.Id);
                    table.ForeignKey(
                        name: "FK_commission_tariff_rows_commission_tariffs_TenantId_TariffId",
                        columns: x => new { x.TenantId, x.TariffId },
                        principalSchema: "catalog",
                        principalTable: "commission_tariffs",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_commission_tariff_rows_TariffId_Barcode",
                schema: "catalog",
                table: "commission_tariff_rows",
                columns: new[] { "TariffId", "Barcode" });

            migrationBuilder.CreateIndex(
                name: "IX_commission_tariff_rows_TariffId_SellerSku",
                schema: "catalog",
                table: "commission_tariff_rows",
                columns: new[] { "TariffId", "SellerSku" });

            migrationBuilder.CreateIndex(
                name: "IX_commission_tariff_rows_TenantId_TariffId",
                schema: "catalog",
                table: "commission_tariff_rows",
                columns: new[] { "TenantId", "TariffId" });

            migrationBuilder.CreateIndex(
                name: "IX_commission_tariffs_TenantId_ConnectionId_FileHash_PeriodSta~",
                schema: "catalog",
                table: "commission_tariffs",
                columns: new[] { "TenantId", "ConnectionId", "FileHash", "PeriodStart" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_commission_tariffs_TenantId_ConnectionId_PeriodStart_Period~",
                schema: "catalog",
                table: "commission_tariffs",
                columns: new[] { "TenantId", "ConnectionId", "PeriodStart", "PeriodEnd" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "commission_tariff_rows",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "commission_tariffs",
                schema: "catalog");
        }
    }
}
