using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20261014150000_AddSharedRateLimitBuckets")]
public sealed class AddSharedRateLimitBuckets : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Token buckets shared by every API and worker process, so marketplace quotas are counted
        // once per seller instead of once per process.
        migrationBuilder.Sql("""
            CREATE TABLE integration.rate_limit_buckets (
                "Key" varchar(256) PRIMARY KEY,
                "Tokens" double precision NOT NULL,
                "UpdatedAt" timestamptz NOT NULL
            );

            -- Takes one token. Returns 0 when it was taken, otherwise the seconds to wait before
            -- the next token exists (nothing is taken then).
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
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP FUNCTION IF EXISTS integration.take_rate_token(varchar, double precision, double precision);
            DROP TABLE IF EXISTS integration.rate_limit_buckets;
            """);
    }
}
