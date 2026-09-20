using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProductImportStaging : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "product_import_sessions",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    JobId = table.Column<Guid>(type: "uuid", nullable: false),
                    Phase = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    NextCursor = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    ReceivedProducts = table.Column<int>(type: "integer", nullable: false),
                    TotalProducts = table.Column<int>(type: "integer", nullable: true),
                    PageNumber = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_product_import_sessions", x => x.Id);
                    table.UniqueConstraint("AK_product_import_sessions_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_product_import_sessions_platform_connections_TenantId_Conne~",
                        columns: x => new { x.TenantId, x.ConnectionId },
                        principalSchema: "integration",
                        principalTable: "platform_connections",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_product_import_sessions_tenants_TenantId",
                        column: x => x.TenantId,
                        principalSchema: "iam",
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "product_import_staging_records",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    JobId = table.Column<Guid>(type: "uuid", nullable: false),
                    ModelKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ExternalProductId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    SnapshotHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    SnapshotJson = table.Column<string>(type: "jsonb", nullable: false),
                    ReceivedOrder = table.Column<int>(type: "integer", nullable: false),
                    State = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FinalizedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ErrorSummary = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_product_import_staging_records", x => x.Id);
                    table.UniqueConstraint("AK_product_import_staging_records_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_product_import_staging_records_platform_connections_TenantI~",
                        columns: x => new { x.TenantId, x.ConnectionId },
                        principalSchema: "integration",
                        principalTable: "platform_connections",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_product_import_staging_records_tenants_TenantId",
                        column: x => x.TenantId,
                        principalSchema: "iam",
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_product_import_sessions_TenantId_ConnectionId_Phase",
                schema: "catalog",
                table: "product_import_sessions",
                columns: new[] { "TenantId", "ConnectionId", "Phase" });

            migrationBuilder.CreateIndex(
                name: "IX_product_import_sessions_TenantId_JobId",
                schema: "catalog",
                table: "product_import_sessions",
                columns: new[] { "TenantId", "JobId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_product_import_staging_records_TenantId_ConnectionId",
                schema: "catalog",
                table: "product_import_staging_records",
                columns: new[] { "TenantId", "ConnectionId" });

            migrationBuilder.CreateIndex(
                name: "IX_product_import_staging_records_TenantId_JobId_ExternalProdu~",
                schema: "catalog",
                table: "product_import_staging_records",
                columns: new[] { "TenantId", "JobId", "ExternalProductId", "SnapshotHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_product_import_staging_records_TenantId_JobId_ModelKey_State",
                schema: "catalog",
                table: "product_import_staging_records",
                columns: new[] { "TenantId", "JobId", "ModelKey", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_product_import_staging_records_TenantId_JobId_State_Receive~",
                schema: "catalog",
                table: "product_import_staging_records",
                columns: new[] { "TenantId", "JobId", "State", "ReceivedOrder" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "product_import_sessions",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "product_import_staging_records",
                schema: "catalog");
        }
    }
}
