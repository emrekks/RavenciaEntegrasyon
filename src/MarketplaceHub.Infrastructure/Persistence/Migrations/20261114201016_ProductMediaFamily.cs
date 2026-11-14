using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProductMediaFamily : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "FamilyId",
                schema: "catalog",
                table: "product_media",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_product_media_FamilyId",
                schema: "catalog",
                table: "product_media",
                column: "FamilyId");

            migrationBuilder.AddForeignKey(
                name: "FK_product_media_product_models_FamilyId",
                schema: "catalog",
                table: "product_media",
                column: "FamilyId",
                principalSchema: "catalog",
                principalTable: "product_models",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.Sql(BackfillSql);
        }

        // Backfill (MODEL-KAYDI-PLANI Faz 2, pictures): each picture belongs to its card's model, the value ProductModelLinks
        // keeps after every save and reconcile (SyncMediaFamiliesAsync). Only the new column is written; a card without a
        // model leaves its pictures without one. Safe to run again.
        internal const string BackfillSql = """
            UPDATE catalog.product_media m SET "FamilyId" = p."FamilyId"
            FROM catalog.products p
            WHERE p."TenantId" = m."TenantId" AND p."Id" = m."ProductId" AND p."FamilyId" IS NOT NULL
              AND m."FamilyId" IS DISTINCT FROM p."FamilyId";
            """;

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_product_media_product_models_FamilyId",
                schema: "catalog",
                table: "product_media");

            migrationBuilder.DropIndex(
                name: "IX_product_media_FamilyId",
                schema: "catalog",
                table: "product_media");

            migrationBuilder.DropColumn(
                name: "FamilyId",
                schema: "catalog",
                table: "product_media");
        }
    }
}
