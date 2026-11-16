using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations;

// Audit 2026-11-15 KUYRUK-6: every job notification woke all ~19 worker lane slots, and each ran the lease query. The
// trigger now names the job that became runnable ("job:<type>:<priority>"), so the worker wakes one slot of each lane
// that can lease it, and sends "freed" when a leased job ends (a connection lock group may have opened for a job of any
// lane: one slot per lane). Function body only: same name, result and owner, the trigger is untouched. The running
// worker ignores the payload (it wakes every slot on any notification), so it keeps working while the new one starts;
// the new worker wakes every slot on a payload it does not know (the old trigger's empty one).
// KUYRUK-2: external_effect_records.SentAt (nullable) records when the marketplace answered the write, so the sweep
// measures an open record's age from the send instead of from the record's creation (before a quota wait).
public partial class JobNotifyNamesLaneAndEffectSentAt : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "SentAt",
            schema: "integration",
            table: "external_effect_records",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.Sql($$"""
            CREATE OR REPLACE FUNCTION integration.notify_runnable_job() RETURNS trigger
            LANGUAGE plpgsql AS $function$
            BEGIN
                IF NEW."Status" IN ('PENDING', 'RETRY_SCHEDULED') AND NEW."AvailableAt" <= now()
                        AND (TG_OP = 'INSERT' OR OLD."Status" IS DISTINCT FROM NEW."Status" OR OLD."AvailableAt" IS DISTINCT FROM NEW."AvailableAt") THEN
                    PERFORM pg_notify('{{IntegrationJobNotifications.Channel}}', 'job:' || NEW."JobType" || ':' || NEW."Priority"::text);
                END IF;
                IF TG_OP = 'UPDATE' AND OLD."Status" = 'LEASED' AND NEW."Status" <> 'LEASED' THEN
                    PERFORM pg_notify('{{IntegrationJobNotifications.Channel}}', 'freed');
                END IF;
                RETURN NULL;
            END
            $function$;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // The body of 20261014130000_NotifyRunnableIntegrationJobs.
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
            """);

        // The SentAt values written meanwhile are dropped with the column and not restored by a later Up.
        migrationBuilder.DropColumn(
            name: "SentAt",
            schema: "integration",
            table: "external_effect_records");
    }
}
