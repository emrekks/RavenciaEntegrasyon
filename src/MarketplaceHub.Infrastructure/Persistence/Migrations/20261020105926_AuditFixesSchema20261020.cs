using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AuditFixesSchema20261020 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MfaFailedAttempts",
                schema: "iam",
                table: "user_sessions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "ActionDueAtEstimated",
                schema: "sales",
                table: "return_claims",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DisabledAt",
                schema: "integration",
                table: "platform_connections",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastScheduledAt",
                schema: "integration",
                table: "connection_sync_policies",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "UserDefined",
                schema: "integration",
                table: "connection_sync_policies",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ResponseContentType",
                schema: "ops",
                table: "api_idempotency_records",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResponseETag",
                schema: "ops",
                table: "api_idempotency_records",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResponseLocation",
                schema: "ops",
                table: "api_idempotency_records",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_orders_TenantId_ConnectionId_CreatedAt",
                schema: "sales",
                table: "orders",
                columns: new[] { "TenantId", "ConnectionId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_inbox_messages_ProcessedAt",
                schema: "integration",
                table: "inbox_messages",
                column: "ProcessedAt",
                filter: "\"ProcessedAt\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_orders_TenantId_ConnectionId_CreatedAt",
                schema: "sales",
                table: "orders");

            migrationBuilder.DropIndex(
                name: "IX_inbox_messages_ProcessedAt",
                schema: "integration",
                table: "inbox_messages");

            migrationBuilder.DropColumn(
                name: "MfaFailedAttempts",
                schema: "iam",
                table: "user_sessions");

            migrationBuilder.DropColumn(
                name: "ActionDueAtEstimated",
                schema: "sales",
                table: "return_claims");

            migrationBuilder.DropColumn(
                name: "DisabledAt",
                schema: "integration",
                table: "platform_connections");

            migrationBuilder.DropColumn(
                name: "LastScheduledAt",
                schema: "integration",
                table: "connection_sync_policies");

            migrationBuilder.DropColumn(
                name: "UserDefined",
                schema: "integration",
                table: "connection_sync_policies");

            migrationBuilder.DropColumn(
                name: "ResponseContentType",
                schema: "ops",
                table: "api_idempotency_records");

            migrationBuilder.DropColumn(
                name: "ResponseETag",
                schema: "ops",
                table: "api_idempotency_records");

            migrationBuilder.DropColumn(
                name: "ResponseLocation",
                schema: "ops",
                table: "api_idempotency_records");
        }
    }
}
