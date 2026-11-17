using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations;

// Audit 2026-11-15 ADAPTOR-TY-HB-4: take_rate_token read its "now" before it locked the bucket row. A caller that waited
// for the lock then wrote that older time over the newer one the lock holder had written, and the next caller counted the
// time in between twice: an extra token. The time is now read after the lock and the bucket's time never moves back.
// Function body only: same name, arguments, result and owner (CREATE OR REPLACE keeps its grants), so the running code
// calls it unchanged while the new release starts; no table or data changes.
[DbContext(typeof(AppDbContext))]
[Migration("20261115205317_RateTokenTimeAfterLock")]
public sealed class RateTokenTimeAfterLock : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
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

    // The body 20261019090000_AddRateLimitUsage wrote.
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
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
}
