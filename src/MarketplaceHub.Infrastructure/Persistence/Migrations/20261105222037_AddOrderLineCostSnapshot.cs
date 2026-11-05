using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderLineCostSnapshot : Migration
    {
        // Every line already tied to a variant takes the variant's cost price and desi as they are now, so no report
        // figure moves today; from here on a line takes them when it is first tied to its variant (OrderLineCostSnapshot)
        // and a later edit of the variant leaves past sales as they were. A cost or desi of 0 or below is not kept (a cost
        // of 0 is a missing one to the reports), so it stays null and the line keeps following the variant. Only the two new
        // columns are written (no version bump: the running code does not know them); a second run changes nothing.
        internal const string Backfill = """
            UPDATE sales.order_lines AS l
            SET "UnitCostSnapshot" = CASE WHEN v."CostPrice" > 0 THEN v."CostPrice" END,
                "DesiSnapshot" = CASE WHEN v."Desi" > 0 THEN v."Desi" END
            FROM catalog.product_variants AS v
            WHERE v."TenantId" = l."TenantId" AND v."Id" = l."VariantId"
              AND l."UnitCostSnapshot" IS NULL AND l."DesiSnapshot" IS NULL
              AND (v."CostPrice" > 0 OR v."Desi" > 0);
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "DesiSnapshot",
                schema: "sales",
                table: "order_lines",
                type: "numeric(19,4)",
                precision: 19,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "UnitCostSnapshot",
                schema: "sales",
                table: "order_lines",
                type: "numeric(19,4)",
                precision: 19,
                scale: 4,
                nullable: true);

            migrationBuilder.Sql(Backfill);
        }

        // Dropping the columns loses the values the lines kept: run Up again and they take the variants' values of that
        // day, which may differ from those of the sale.
        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DesiSnapshot",
                schema: "sales",
                table: "order_lines");

            migrationBuilder.DropColumn(
                name: "UnitCostSnapshot",
                schema: "sales",
                table: "order_lines");
        }
    }
}
