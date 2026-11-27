using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    // Queue phase 0 (27 Nov 2026), visibility only: nullable columns that record why and how long a job waited
    // (job_attempts), when a webhook's event happened (inbox_messages.EventAt) and which job first stored an order
    // (orders.CreatedByJobId); no backfill, no index on the hot tables. Each ADD COLUMN is nullable without a default: a
    // catalogue change under a brief ACCESS EXCLUSIVE lock (the migrator's lock_timeout of 15 s bounds the wait). The old
    // release keeps running against these columns (it never names them); the new one reads rows without them as unknown.
    /// <inheritdoc />
    public partial class QueueVisibilityPhase0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CreatedByJobId",
                schema: "sales",
                table: "orders",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Borrowed",
                schema: "integration",
                table: "job_attempts",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "EligibleAt",
                schema: "integration",
                table: "job_attempts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Lane",
                schema: "integration",
                table: "job_attempts",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "NotDueMs",
                schema: "integration",
                table: "job_attempts",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "QuotaWaitMs",
                schema: "integration",
                table: "job_attempts",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "QuotaWaits",
                schema: "integration",
                table: "job_attempts",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "WaitBackoffMs",
                schema: "integration",
                table: "job_attempts",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "WaitLockMs",
                schema: "integration",
                table: "job_attempts",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "WaitQuotaHoldMs",
                schema: "integration",
                table: "job_attempts",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "WaitSlotMs",
                schema: "integration",
                table: "job_attempts",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "EventAt",
                schema: "integration",
                table: "inbox_messages",
                type: "timestamp with time zone",
                nullable: true);

            // Which connection takes tokens from which quota bucket (QuotaBucketOwners), for the quota card's tenant scope.
            // Outside the EF model like the other quota tables; no tenant column (the connection names it), no foreign key (a
            // pair of a deleted connection goes with the 30-day retention). Its primary key serves the card (Key first, joined from
            // the few keys of the tenant's connections) and the retention; the table holds a few rows per connection.
            // take_rate_token: the body of 20261115205317_RateTokenTimeAfterLock with its own clean-up after 30 days instead of 2
            // (JobRetentionService.RateLimitUsageRetention). Same name, arguments, result and owner, so the running code calls it
            // unchanged; the old release's hourly retention may still delete rows older than 2 days until it stops.
            migrationBuilder.Sql("""
                CREATE TABLE IF NOT EXISTS integration.rate_limit_bucket_connections (
                    "Key" varchar(256) NOT NULL,
                    "ConnectionId" uuid NOT NULL,
                    "LastSeenAt" timestamptz NOT NULL,
                    PRIMARY KEY ("Key", "ConnectionId")
                );
                """);
            migrationBuilder.Sql(TakeRateTokenBody("30 days"));
        }

        // integration.take_rate_token as 20261115205317_RateTokenTimeAfterLock wrote it, with the usage kept for `keep`.
        private static string TakeRateTokenBody(string keep) => $$"""
            CREATE OR REPLACE FUNCTION integration.take_rate_token(p_key varchar, p_capacity double precision, p_tokens_per_second double precision)
            RETURNS double precision
            LANGUAGE plpgsql AS $function$
            DECLARE
                v_now timestamptz;
                v_updated_at timestamptz;
                v_tokens double precision;
                v_new_minute boolean;
            BEGIN
                INSERT INTO integration.rate_limit_buckets ("Key", "Tokens", "UpdatedAt")
                VALUES (p_key, p_capacity, clock_timestamp())
                ON CONFLICT ("Key") DO NOTHING;

                SELECT "Tokens", "UpdatedAt"
                INTO v_tokens, v_updated_at
                FROM integration.rate_limit_buckets
                WHERE "Key" = p_key
                FOR UPDATE;

                -- Read once the row is ours, and never before the time the last holder wrote.
                v_now := GREATEST(clock_timestamp(), v_updated_at);
                v_tokens := LEAST(p_capacity, v_tokens + GREATEST(0, EXTRACT(EPOCH FROM v_now - v_updated_at)) * p_tokens_per_second);

                IF v_tokens >= 1 THEN
                    UPDATE integration.rate_limit_buckets SET "Tokens" = v_tokens - 1, "UpdatedAt" = v_now WHERE "Key" = p_key;
                    INSERT INTO integration.rate_limit_usage ("Key", "Minute", "Requests")
                    VALUES (p_key, date_trunc('minute', v_now), 1)
                    ON CONFLICT ("Key", "Minute") DO UPDATE SET "Requests" = integration.rate_limit_usage."Requests" + 1
                    RETURNING (xmax = 0) INTO v_new_minute;
                    IF v_new_minute THEN
                        DELETE FROM integration.rate_limit_usage WHERE "Key" = p_key AND "Minute" < v_now - interval '{{keep}}';
                    END IF;
                    RETURN 0;
                END IF;

                UPDATE integration.rate_limit_buckets SET "Tokens" = v_tokens, "UpdatedAt" = v_now WHERE "Key" = p_key;
                RETURN (1 - v_tokens) / p_tokens_per_second;
            END
            $function$;
            """;

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CreatedByJobId",
                schema: "sales",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "Borrowed",
                schema: "integration",
                table: "job_attempts");

            migrationBuilder.DropColumn(
                name: "EligibleAt",
                schema: "integration",
                table: "job_attempts");

            migrationBuilder.DropColumn(
                name: "Lane",
                schema: "integration",
                table: "job_attempts");

            migrationBuilder.DropColumn(
                name: "NotDueMs",
                schema: "integration",
                table: "job_attempts");

            migrationBuilder.DropColumn(
                name: "QuotaWaitMs",
                schema: "integration",
                table: "job_attempts");

            migrationBuilder.DropColumn(
                name: "QuotaWaits",
                schema: "integration",
                table: "job_attempts");

            migrationBuilder.DropColumn(
                name: "WaitBackoffMs",
                schema: "integration",
                table: "job_attempts");

            migrationBuilder.DropColumn(
                name: "WaitLockMs",
                schema: "integration",
                table: "job_attempts");

            migrationBuilder.DropColumn(
                name: "WaitQuotaHoldMs",
                schema: "integration",
                table: "job_attempts");

            migrationBuilder.DropColumn(
                name: "WaitSlotMs",
                schema: "integration",
                table: "job_attempts");

            migrationBuilder.DropColumn(
                name: "EventAt",
                schema: "integration",
                table: "inbox_messages");

            migrationBuilder.Sql("DROP TABLE IF EXISTS integration.rate_limit_bucket_connections;");
            migrationBuilder.Sql(TakeRateTokenBody("2 days"));
        }
    }
}
