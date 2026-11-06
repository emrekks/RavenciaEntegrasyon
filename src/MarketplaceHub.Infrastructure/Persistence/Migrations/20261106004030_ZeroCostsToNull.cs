using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ZeroCostsToNull : Migration
    {
        // K3 (owner decision 2026-11-06): a cost of 0 means "not entered" and is never stored again, so the stored ones
        // become empty. The reports already read 0 as missing, so no report figure moves; the missing-cost summary and
        // template list them now. Live on 6 Nov: 36 variants at 0, 0 order lines with a 0 cost snapshot. Only the cost
        // columns are written (no version bump); a second run changes nothing.
        internal const string Clear = """
            UPDATE catalog.product_variants SET "CostPrice" = NULL WHERE "CostPrice" = 0;
            UPDATE sales.order_lines SET "UnitCostSnapshot" = NULL WHERE "UnitCostSnapshot" = 0;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(Clear);
        }

        // The zero costs are not restored: they meant "not entered", which is what NULL says.
        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
