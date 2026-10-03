using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace MarketplaceHub.Infrastructure.Persistence.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20261003100000_AllowReusableAttributeValueMappings")]
public sealed class AllowReusableAttributeValueMappings : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_attribute_value_mappings_TenantId_ConnectionId_LocalId_Scop~",
            schema: "catalog",
            table: "attribute_value_mappings");

        migrationBuilder.CreateIndex(
            name: "UX_attribute_value_mappings_remote_scope",
            schema: "catalog",
            table: "attribute_value_mappings",
            columns: new[] { "TenantId", "ConnectionId", "ScopeExternalId", "ExternalId" },
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "UX_attribute_value_mappings_remote_scope",
            schema: "catalog",
            table: "attribute_value_mappings");

        migrationBuilder.CreateIndex(
            name: "IX_attribute_value_mappings_TenantId_ConnectionId_LocalId_Scop~",
            schema: "catalog",
            table: "attribute_value_mappings",
            columns: new[] { "TenantId", "ConnectionId", "LocalId", "ScopeExternalId" },
            unique: true);
    }
}
