using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProductVat : Migration
    {
        // The product card's VAT starts from its variants' marketplace offers: the rate most of its offers with a VAT
        // above zero carry (a tie goes to the lower rate) and the VAT inclusion most of those offers carry (a tie goes to
        // INCLUDED). A card with no such offer stays NULL: its VAT is unknown and is never guessed. Only a card whose VAT
        // is still NULL is written, so a second run changes nothing.
        internal const string BackfillSql = """
            WITH offer_vat AS (
                SELECT v."TenantId" AS tenant_id, v."ProductId" AS product_id, o."VatRate" AS vat_rate,
                       upper(btrim(o."VatInclusion")) AS vat_inclusion
                FROM inventory.channel_offers o
                JOIN catalog.product_variants v ON v."TenantId" = o."TenantId" AND v."Id" = o."VariantId"
                WHERE o."VatRate" > 0
            ),
            rate AS (
                SELECT DISTINCT ON (tenant_id, product_id) tenant_id, product_id, vat_rate
                FROM offer_vat
                GROUP BY tenant_id, product_id, vat_rate
                ORDER BY tenant_id, product_id, count(*) DESC, vat_rate ASC
            ),
            inclusion AS (
                SELECT DISTINCT ON (tenant_id, product_id) tenant_id, product_id, vat_inclusion
                FROM offer_vat
                WHERE vat_inclusion IN ('INCLUDED', 'EXCLUDED')
                GROUP BY tenant_id, product_id, vat_inclusion
                ORDER BY tenant_id, product_id, count(*) DESC, (vat_inclusion = 'INCLUDED') DESC
            )
            UPDATE catalog.products p
            SET "VatRate" = rate.vat_rate, "VatInclusion" = inclusion.vat_inclusion
            FROM rate
            LEFT JOIN inclusion ON inclusion.tenant_id = rate.tenant_id AND inclusion.product_id = rate.product_id
            WHERE p."TenantId" = rate.tenant_id AND p."Id" = rate.product_id AND p."VatRate" IS NULL;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "VatInclusion",
                schema: "catalog",
                table: "products",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "VatRate",
                schema: "catalog",
                table: "products",
                type: "numeric(7,4)",
                precision: 7,
                scale: 4,
                nullable: true);

            migrationBuilder.Sql(BackfillSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "VatInclusion",
                schema: "catalog",
                table: "products");

            migrationBuilder.DropColumn(
                name: "VatRate",
                schema: "catalog",
                table: "products");
        }
    }
}
