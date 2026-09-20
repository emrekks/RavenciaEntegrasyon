using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations;

public partial class AddReturnCargoProviderName : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "CargoProviderName",
            schema: "sales",
            table: "return_claims",
            type: "character varying(128)",
            maxLength: 128,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "CargoTrackingNumber",
            schema: "sales",
            table: "return_claims",
            type: "character varying(256)",
            maxLength: 256,
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "CargoProviderName",
            schema: "sales",
            table: "return_claims");

        migrationBuilder.DropColumn(
            name: "CargoTrackingNumber",
            schema: "sales",
            table: "return_claims");
    }
}
