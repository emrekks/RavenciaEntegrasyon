using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WebhookSubscriptionCloseAndRefusal : Migration
    {
        // Why and when a webhook definition was closed, and the last call its address got while closed (23 Nov 2026: a
        // closed Hepsiburada definition was called for four days without a trace). Additive only: three nullable columns,
        // no row is written; null stays "not known". The previous code ignores them during the deploy window.
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ClosedAt",
                schema: "integration",
                table: "webhook_subscriptions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClosedReason",
                schema: "integration",
                table: "webhook_subscriptions",
                type: "character varying(24)",
                maxLength: 24,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastRefusedAt",
                schema: "integration",
                table: "webhook_subscriptions",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ClosedAt",
                schema: "integration",
                table: "webhook_subscriptions");

            migrationBuilder.DropColumn(
                name: "ClosedReason",
                schema: "integration",
                table: "webhook_subscriptions");

            migrationBuilder.DropColumn(
                name: "LastRefusedAt",
                schema: "integration",
                table: "webhook_subscriptions");
        }
    }
}
