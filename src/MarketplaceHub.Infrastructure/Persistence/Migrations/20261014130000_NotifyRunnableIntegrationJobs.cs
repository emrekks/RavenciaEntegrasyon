using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20261014130000_NotifyRunnableIntegrationJobs")]
public sealed class NotifyRunnableIntegrationJobs : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Workers LISTEN on this channel and lease at once instead of waiting for the next poll.
        // Identical notifications inside one transaction are delivered once by PostgreSQL.
        migrationBuilder.Sql($$"""
            CREATE OR REPLACE FUNCTION integration.notify_runnable_job() RETURNS trigger
            LANGUAGE plpgsql AS $function$
            BEGIN
                IF (NEW."Status" IN ('PENDING', 'RETRY_SCHEDULED') AND NEW."AvailableAt" <= now()
                        AND (TG_OP = 'INSERT' OR OLD."Status" IS DISTINCT FROM NEW."Status" OR OLD."AvailableAt" IS DISTINCT FROM NEW."AvailableAt"))
                    OR (TG_OP = 'UPDATE' AND OLD."Status" = 'LEASED' AND NEW."Status" <> 'LEASED') THEN
                    PERFORM pg_notify('{{IntegrationJobNotifications.Channel}}', '');
                END IF;
                RETURN NULL;
            END
            $function$;

            CREATE TRIGGER trg_jobs_notify_runnable
                AFTER INSERT OR UPDATE OF "Status", "AvailableAt" ON integration.jobs
                FOR EACH ROW EXECUTE FUNCTION integration.notify_runnable_job();
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP TRIGGER IF EXISTS trg_jobs_notify_runnable ON integration.jobs;
            DROP FUNCTION IF EXISTS integration.notify_runnable_job();
            """);
    }
}
