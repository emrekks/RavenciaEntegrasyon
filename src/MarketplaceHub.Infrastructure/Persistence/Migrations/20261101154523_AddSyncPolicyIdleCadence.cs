using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSyncPolicyIdleCadence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IdleBackoffEnabled",
                schema: "integration",
                table: "connection_sync_policies",
                type: "boolean",
                nullable: false,
                // Existing policies keep thinning out as they did.
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "IdleIntervalSeconds",
                schema: "integration",
                table: "connection_sync_policies",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IdleBackoffEnabled",
                schema: "integration",
                table: "connection_sync_policies");

            migrationBuilder.DropColumn(
                name: "IdleIntervalSeconds",
                schema: "integration",
                table: "connection_sync_policies");
        }
    }
}
