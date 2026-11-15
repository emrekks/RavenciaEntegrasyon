using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProductModelSharedValues : Migration
    {
        // KATALOG-14 phase A / KATALOG-13 (expand only; docs/CI-CD-DEPLOY.md "Migration kuralları"): the previous API and
        // Worker keep serving while this runs, and nothing they read or write changes.
        //   product_models."Desi": a nullable column with no default, so ADD COLUMN only touches the catalog (a brief
        //   ACCESS EXCLUSIVE lock on a ~220-row table, no rewrite); the old code never names it (it reads the model through
        //   EF projections and raw SQL with named columns only).
        //   product_model_size_charts: a new, empty table; its key to product_models takes a brief SHARE ROW EXCLUSIVE lock
        //   on product_models while it is added (no rows to validate).
        // No data is written here: ProductModelValues fills both after the migrations (DatabaseMigrator) and on the
        // Worker's nightly model scan, in C# with the editor's own rules.
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "Desi",
                schema: "catalog",
                table: "product_models",
                type: "numeric(19,4)",
                precision: 19,
                scale: 4,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "product_model_size_charts",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ModelId = table.Column<Guid>(type: "uuid", nullable: false),
                    MeasurementsJson = table.Column<string>(type: "jsonb", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_product_model_size_charts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_product_model_size_charts_product_models_TenantId_ModelId",
                        columns: x => new { x.TenantId, x.ModelId },
                        principalSchema: "catalog",
                        principalTable: "product_models",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_product_model_size_charts_TenantId_ModelId",
                schema: "catalog",
                table: "product_model_size_charts",
                columns: new[] { "TenantId", "ModelId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "product_model_size_charts",
                schema: "catalog");

            migrationBuilder.DropColumn(
                name: "Desi",
                schema: "catalog",
                table: "product_models");
        }
    }
}
