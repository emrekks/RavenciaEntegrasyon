using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20261019090000_AddRateLimitUsage")]
public sealed class AddRateLimitUsage : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Requests taken from each shared bucket per minute, so the panel can show how full a
        // marketplace quota runs. A key's rows older than two days go when its next minute starts.
        migrationBuilder.Sql("""
            CREATE TABLE integration.rate_limit_usage (
                "Key" varchar(256) NOT NULL,
                "Minute" timestamptz NOT NULL,
                "Requests" integer NOT NULL,
                PRIMARY KEY ("Key", "Minute")
            );

            CREATE OR REPLACE FUNCTION integration.take_rate_token(p_key varchar, p_capacity double precision, p_tokens_per_second double precision)
            RETURNS double precision
            LANGUAGE plpgsql AS $function$
            DECLARE
                v_now timestamptz := clock_timestamp();
                v_tokens double precision;
                v_new_minute boolean;
            BEGIN
                INSERT INTO integration.rate_limit_buckets ("Key", "Tokens", "UpdatedAt")
                VALUES (p_key, p_capacity, v_now)
                ON CONFLICT ("Key") DO NOTHING;

                SELECT LEAST(p_capacity, "Tokens" + GREATEST(0, EXTRACT(EPOCH FROM v_now - "UpdatedAt")) * p_tokens_per_second)
                INTO v_tokens
                FROM integration.rate_limit_buckets
                WHERE "Key" = p_key
                FOR UPDATE;

                IF v_tokens >= 1 THEN
                    UPDATE integration.rate_limit_buckets SET "Tokens" = v_tokens - 1, "UpdatedAt" = v_now WHERE "Key" = p_key;
                    INSERT INTO integration.rate_limit_usage ("Key", "Minute", "Requests")
                    VALUES (p_key, date_trunc('minute', v_now), 1)
                    ON CONFLICT ("Key", "Minute") DO UPDATE SET "Requests" = integration.rate_limit_usage."Requests" + 1
                    RETURNING (xmax = 0) INTO v_new_minute;
                    IF v_new_minute THEN
                        DELETE FROM integration.rate_limit_usage WHERE "Key" = p_key AND "Minute" < v_now - interval '2 days';
                    END IF;
                    RETURN 0;
                END IF;

                UPDATE integration.rate_limit_buckets SET "Tokens" = v_tokens, "UpdatedAt" = v_now WHERE "Key" = p_key;
                RETURN (1 - v_tokens) / p_tokens_per_second;
            END
            $function$;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE OR REPLACE FUNCTION integration.take_rate_token(p_key varchar, p_capacity double precision, p_tokens_per_second double precision)
            RETURNS double precision
            LANGUAGE plpgsql AS $function$
            DECLARE
                v_now timestamptz := clock_timestamp();
                v_tokens double precision;
            BEGIN
                INSERT INTO integration.rate_limit_buckets ("Key", "Tokens", "UpdatedAt")
                VALUES (p_key, p_capacity, v_now)
                ON CONFLICT ("Key") DO NOTHING;

                SELECT LEAST(p_capacity, "Tokens" + GREATEST(0, EXTRACT(EPOCH FROM v_now - "UpdatedAt")) * p_tokens_per_second)
                INTO v_tokens
                FROM integration.rate_limit_buckets
                WHERE "Key" = p_key
                FOR UPDATE;

                IF v_tokens >= 1 THEN
                    UPDATE integration.rate_limit_buckets SET "Tokens" = v_tokens - 1, "UpdatedAt" = v_now WHERE "Key" = p_key;
                    RETURN 0;
                END IF;

                UPDATE integration.rate_limit_buckets SET "Tokens" = v_tokens, "UpdatedAt" = v_now WHERE "Key" = p_key;
                RETURN (1 - v_tokens) / p_tokens_per_second;
            END
            $function$;
            DROP TABLE IF EXISTS integration.rate_limit_usage;
            """);
    }
}
