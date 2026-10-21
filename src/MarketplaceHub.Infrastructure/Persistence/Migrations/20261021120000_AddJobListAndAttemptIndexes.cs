using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddJobListAndAttemptIndexes : Migration
    {
        // Built CONCURRENTLY: integration.jobs and job_attempts are written all the time and the previous API and
        // Worker keep running during the migration. CREATE INDEX CONCURRENTLY never blocks reads or writes, so the
        // migrate session's lock_timeout (DatabaseMigrator) is lifted for these statements while statement_timeout
        // still bounds them. Each index is dropped first in case an earlier, interrupted run left an INVALID one.
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("SET lock_timeout = 0", suppressTransaction: true);

            migrationBuilder.Sql(
                "DROP INDEX CONCURRENTLY IF EXISTS integration.\"IX_jobs_TenantId_CreatedAt_Id\"",
                suppressTransaction: true);
            migrationBuilder.Sql(
                "CREATE INDEX CONCURRENTLY \"IX_jobs_TenantId_CreatedAt_Id\" ON integration.jobs (\"TenantId\", \"CreatedAt\" DESC, \"Id\" DESC)",
                suppressTransaction: true);

            migrationBuilder.Sql(
                "DROP INDEX CONCURRENTLY IF EXISTS integration.\"IX_job_attempts_TenantId_StartedAt\"",
                suppressTransaction: true);
            migrationBuilder.Sql(
                "CREATE INDEX CONCURRENTLY \"IX_job_attempts_TenantId_StartedAt\" ON integration.job_attempts (\"TenantId\", \"StartedAt\") INCLUDE (\"JobId\")",
                suppressTransaction: true);

            // The new index leads with TenantId, so it serves the tenant foreign key as well.
            migrationBuilder.Sql(
                "DROP INDEX CONCURRENTLY IF EXISTS integration.\"IX_job_attempts_TenantId\"",
                suppressTransaction: true);

            migrationBuilder.Sql("RESET lock_timeout", suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "CREATE INDEX CONCURRENTLY IF NOT EXISTS \"IX_job_attempts_TenantId\" ON integration.job_attempts (\"TenantId\")",
                suppressTransaction: true);
            migrationBuilder.Sql(
                "DROP INDEX CONCURRENTLY IF EXISTS integration.\"IX_job_attempts_TenantId_StartedAt\"",
                suppressTransaction: true);
            migrationBuilder.Sql(
                "DROP INDEX CONCURRENTLY IF EXISTS integration.\"IX_jobs_TenantId_CreatedAt_Id\"",
                suppressTransaction: true);
        }
    }
}
