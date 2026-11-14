using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProductModelAttributes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AttributesSavedAt",
                schema: "catalog",
                table: "product_models",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "product_model_attributes",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ModelId = table.Column<Guid>(type: "uuid", nullable: false),
                    AttributeId = table.Column<Guid>(type: "uuid", nullable: false),
                    ValueId = table.Column<Guid>(type: "uuid", nullable: true),
                    TextValue = table.Column<string>(type: "text", nullable: true),
                    NumberValue = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: true),
                    BooleanValue = table.Column<bool>(type: "boolean", nullable: true),
                    SortOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_product_model_attributes", x => x.Id);
                    table.CheckConstraint("ck_product_model_attributes_exactly_one_value", "num_nonnulls(\"ValueId\",\"TextValue\",\"NumberValue\",\"BooleanValue\")=1");
                    table.ForeignKey(
                        name: "FK_product_model_attributes_attribute_definitions_TenantId_Att~",
                        columns: x => new { x.TenantId, x.AttributeId },
                        principalSchema: "catalog",
                        principalTable: "attribute_definitions",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_product_model_attributes_attribute_values_TenantId_ValueId",
                        columns: x => new { x.TenantId, x.ValueId },
                        principalSchema: "catalog",
                        principalTable: "attribute_values",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_product_model_attributes_product_models_TenantId_ModelId",
                        columns: x => new { x.TenantId, x.ModelId },
                        principalSchema: "catalog",
                        principalTable: "product_models",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_product_model_attributes_TenantId_AttributeId",
                schema: "catalog",
                table: "product_model_attributes",
                columns: new[] { "TenantId", "AttributeId" });

            migrationBuilder.CreateIndex(
                name: "IX_product_model_attributes_TenantId_ModelId_AttributeId_Value~",
                schema: "catalog",
                table: "product_model_attributes",
                columns: new[] { "TenantId", "ModelId", "AttributeId", "ValueId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_product_model_attributes_TenantId_ValueId",
                schema: "catalog",
                table: "product_model_attributes",
                columns: new[] { "TenantId", "ValueId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "product_model_attributes",
                schema: "catalog");

            migrationBuilder.DropColumn(
                name: "AttributesSavedAt",
                schema: "catalog",
                table: "product_models");
        }
    }
}
