using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations;

public partial class PersistTenantSettings : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "tenant_settings",
            schema: "iam",
            columns: table => new
            {
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                Key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                ValueJson = table.Column<string>(type: "jsonb", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                Version = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_tenant_settings", x => new { x.TenantId, x.Key });
                table.ForeignKey(
                    name: "FK_tenant_settings_tenants_TenantId",
                    column: x => x.TenantId,
                    principalSchema: "iam",
                    principalTable: "tenants",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "tenant_settings", schema: "iam");
    }
}
