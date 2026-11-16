using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProductModelSizeChartPicture : Migration
    {
        // KATALOG-13 / KATALOG-14 phase D4 (expand only): the model's size-chart picture, product_model_size_charts.FileAssetId
        // (nullable, no default: no rewrite; the running image neither reads nor writes it), indexed CONCURRENTLY and tied to
        // ops.file_assets ON DELETE SET NULL like a card chart's picture (NOT VALID, then VALIDATE: writes to file_assets are
        // not blocked while the rows are checked). Filled by the deploy's model refresh (ProductModelValues).
        // Ordering: the index needs the column, so the column is added first, outside a transaction and IF NOT EXISTS, so a
        // rerun after a failed index build starts over cleanly; the constraint and the history row commit together.
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""ALTER TABLE catalog.product_model_size_charts ADD COLUMN IF NOT EXISTS "FileAssetId" uuid NULL""", suppressTransaction: true);
            migrationBuilder.Sql("SET lock_timeout = 0", suppressTransaction: true);
            migrationBuilder.Sql(
                "DROP INDEX CONCURRENTLY IF EXISTS catalog.\"IX_product_model_size_charts_FileAssetId\"",
                suppressTransaction: true);
            migrationBuilder.Sql(
                "CREATE INDEX CONCURRENTLY \"IX_product_model_size_charts_FileAssetId\" ON catalog.product_model_size_charts (\"FileAssetId\")",
                suppressTransaction: true);
            migrationBuilder.Sql("RESET lock_timeout", suppressTransaction: true);
            migrationBuilder.Sql("""
                ALTER TABLE catalog.product_model_size_charts ADD CONSTRAINT "FK_product_model_size_charts_file_assets_FileAssetId"
                    FOREIGN KEY ("FileAssetId") REFERENCES ops.file_assets ("Id") ON DELETE SET NULL NOT VALID;
                """);
            migrationBuilder.Sql("""ALTER TABLE catalog.product_model_size_charts VALIDATE CONSTRAINT "FK_product_model_size_charts_file_assets_FileAssetId";""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_product_model_size_charts_file_assets_FileAssetId",
                schema: "catalog",
                table: "product_model_size_charts");

            migrationBuilder.DropIndex(
                name: "IX_product_model_size_charts_FileAssetId",
                schema: "catalog",
                table: "product_model_size_charts");

            migrationBuilder.DropColumn(
                name: "FileAssetId",
                schema: "catalog",
                table: "product_model_size_charts");
        }
    }
}
