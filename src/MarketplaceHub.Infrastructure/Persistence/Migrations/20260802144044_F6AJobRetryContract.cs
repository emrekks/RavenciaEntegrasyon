using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class F6AJobRetryContract : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_jobs_Status_AvailableAt_Priority",
                schema: "integration",
                table: "jobs");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CreatedAt",
                schema: "integration",
                table: "jobs",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now()");

            migrationBuilder.AddColumn<int>(
                name: "MaxAttempts",
                schema: "integration",
                table: "jobs",
                type: "integer",
                nullable: false,
                defaultValue: 6);

            migrationBuilder.Sql(
                """
                UPDATE integration.jobs
                SET "AttemptCount" = GREATEST(0, "AttemptCount"),
                    "MaxAttempts" = GREATEST(6, "AttemptCount");

                UPDATE integration.jobs
                SET "Status" = CASE "Status"
                    WHEN 'Pending' THEN 'PENDING'
                    WHEN 'Running' THEN 'RETRY_SCHEDULED'
                    WHEN 'Succeeded' THEN 'SUCCEEDED'
                    WHEN 'Failed' THEN 'BLOCKED'
                    WHEN 'DeadLettered' THEN 'DEAD'
                    ELSE UPPER("Status")
                END,
                "AvailableAt" = CASE WHEN "Status" = 'Running' THEN now() ELSE "AvailableAt" END,
                "LeaseTokenHash" = CASE WHEN "Status" = 'Running' THEN NULL ELSE "LeaseTokenHash" END,
                "LeaseExpiresAt" = CASE WHEN "Status" = 'Running' THEN NULL ELSE "LeaseExpiresAt" END,
                "HeartbeatAt" = CASE WHEN "Status" = 'Running' THEN NULL ELSE "HeartbeatAt" END,
                "LastErrorCode" = CASE WHEN "Status" = 'Running' THEN 'MIGRATION_RECOVERY' ELSE "LastErrorCode" END;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_jobs_Status_Priority_AvailableAt_CreatedAt",
                schema: "integration",
                table: "jobs",
                columns: new[] { "Status", "Priority", "AvailableAt", "CreatedAt" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_job_attempt_bounds",
                schema: "integration",
                table: "jobs",
                sql: "\"AttemptCount\" >= 0 AND \"MaxAttempts\" > 0 AND \"AttemptCount\" <= \"MaxAttempts\"");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                UPDATE integration.jobs
                SET "Status" = CASE "Status"
                    WHEN 'PENDING' THEN 'Pending'
                    WHEN 'LEASED' THEN 'Running'
                    WHEN 'RETRY_SCHEDULED' THEN 'Pending'
                    WHEN 'BLOCKED' THEN 'Failed'
                    WHEN 'SUCCEEDED' THEN 'Succeeded'
                    WHEN 'DEAD' THEN 'DeadLettered'
                    WHEN 'CANCELLED' THEN 'Failed'
                    ELSE "Status"
                END;
                """);

            migrationBuilder.DropIndex(
                name: "IX_jobs_Status_Priority_AvailableAt_CreatedAt",
                schema: "integration",
                table: "jobs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_job_attempt_bounds",
                schema: "integration",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "CreatedAt",
                schema: "integration",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "MaxAttempts",
                schema: "integration",
                table: "jobs");

            migrationBuilder.CreateIndex(
                name: "IX_jobs_Status_AvailableAt_Priority",
                schema: "integration",
                table: "jobs",
                columns: new[] { "Status", "AvailableAt", "Priority" });
        }
    }
}
