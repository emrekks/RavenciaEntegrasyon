using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddChannelOfferFollowsPanelPrice : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Schema only: every existing offer stays null ("never chosen"), and the screens read its choice from the
            // prices until the person picks one.
            migrationBuilder.AddColumn<bool>(
                name: "FollowsPanelPrice",
                schema: "inventory",
                table: "channel_offers",
                type: "boolean",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FollowsPanelPrice",
                schema: "inventory",
                table: "channel_offers");
        }
    }
}
