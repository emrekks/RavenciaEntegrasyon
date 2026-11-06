using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class VariantArchivedWithProductAndZeroCostsToNull : Migration
    {
        // K5 (owner decision 2026-11-06): why an archived variant is archived. Only a variant archived at the same moment
        // as its product (the product-level archive writes the product's ArchivedAt and the variant's UpdatedAt from one
        // clock reading; one second of tolerance) counts as archived with it, and activating the product reopens it.
        // Every other archived variant (its product not archived, no product archive time, or another moment) counts as
        // archived on its own and stays archived. Live on 6 Nov: 49 archived variants, all on active products: all false.
        // Only the new column is written (no version bump: the running code does not know it); a second run changes nothing.
        internal const string BackfillArchivedWithProduct = """
            UPDATE catalog.product_variants AS v
            SET "ArchivedWithProduct" = (p."Status" = 'Archived' AND p."ArchivedAt" IS NOT NULL
                AND abs(extract(epoch FROM v."UpdatedAt" - p."ArchivedAt")) <= 1)
            FROM catalog.products AS p
            WHERE p."TenantId" = v."TenantId" AND p."Id" = v."ProductId"
              AND v."Status" = 'Archived' AND v."ArchivedWithProduct" IS NULL;
            """;

        // K3 (owner decision 2026-11-06): a cost of 0 means "not entered" and is never stored again, so the stored ones
        // become empty. The reports already read 0 as missing, so no report figure moves; the missing-cost summary and
        // template now list them. Live on 6 Nov: 36 variants at 0, 0 order lines with a 0 cost snapshot.
        internal const string ClearZeroCosts = """
            UPDATE catalog.product_variants SET "CostPrice" = NULL WHERE "CostPrice" = 0;
            UPDATE sales.order_lines SET "UnitCostSnapshot" = NULL WHERE "UnitCostSnapshot" = 0;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ArchivedWithProduct",
                schema: "catalog",
                table: "product_variants",
                type: "boolean",
                nullable: true);

            migrationBuilder.Sql(BackfillArchivedWithProduct);
            migrationBuilder.Sql(ClearZeroCosts);
        }

        // The zero costs are not restored: they meant "not entered", which is what NULL says.
        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ArchivedWithProduct",
                schema: "catalog",
                table: "product_variants");
        }
    }
}
