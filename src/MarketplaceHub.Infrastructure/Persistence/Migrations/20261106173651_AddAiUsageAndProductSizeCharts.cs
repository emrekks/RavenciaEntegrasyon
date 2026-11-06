using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAiUsageAndProductSizeCharts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ai_usage_logs",
                schema: "ops",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Feature = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    SubjectType = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: true),
                    SubjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    SubjectLabel = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Model = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    InputTokens = table.Column<long>(type: "bigint", nullable: true),
                    OutputTokens = table.Column<long>(type: "bigint", nullable: true),
                    CacheReadInputTokens = table.Column<long>(type: "bigint", nullable: true),
                    CacheCreationInputTokens = table.Column<long>(type: "bigint", nullable: true),
                    CostUsd = table.Column<decimal>(type: "numeric(12,6)", precision: 12, scale: 6, nullable: true),
                    Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    Error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    DurationMs = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_usage_logs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ai_usage_logs_tenants_TenantId",
                        column: x => x.TenantId,
                        principalSchema: "iam",
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "product_size_charts",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    FileAssetId = table.Column<Guid>(type: "uuid", nullable: true),
                    LastGeneratedAssetId = table.Column<Guid>(type: "uuid", nullable: true),
                    MeasurementsJson = table.Column<string>(type: "jsonb", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedByUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_product_size_charts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_product_size_charts_file_assets_FileAssetId",
                        column: x => x.FileAssetId,
                        principalSchema: "ops",
                        principalTable: "file_assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_product_size_charts_file_assets_LastGeneratedAssetId",
                        column: x => x.LastGeneratedAssetId,
                        principalSchema: "ops",
                        principalTable: "file_assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_product_size_charts_products_TenantId_ProductId",
                        columns: x => new { x.TenantId, x.ProductId },
                        principalSchema: "catalog",
                        principalTable: "products",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ai_usage_logs_TenantId_CreatedAt",
                schema: "ops",
                table: "ai_usage_logs",
                columns: new[] { "TenantId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_product_size_charts_FileAssetId",
                schema: "catalog",
                table: "product_size_charts",
                column: "FileAssetId");

            migrationBuilder.CreateIndex(
                name: "IX_product_size_charts_LastGeneratedAssetId",
                schema: "catalog",
                table: "product_size_charts",
                column: "LastGeneratedAssetId");

            migrationBuilder.CreateIndex(
                name: "IX_product_size_charts_TenantId_ProductId",
                schema: "catalog",
                table: "product_size_charts",
                columns: new[] { "TenantId", "ProductId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ai_usage_logs",
                schema: "ops");

            migrationBuilder.DropTable(
                name: "product_size_charts",
                schema: "catalog");
        }
    }
}
