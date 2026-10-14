using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260918100000_AllowImmediateExternalWritePolicies")]
public partial class AllowImmediateExternalWritePolicies : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(
            name: "ck_connection_sync_policy_intervals",
            schema: "integration",
            table: "connection_sync_policies");

        migrationBuilder.AddCheckConstraint(
            name: "ck_connection_sync_policy_intervals",
            schema: "integration",
            table: "connection_sync_policies",
            sql: "\"IntervalSeconds\" >= 0 AND \"OverlapSeconds\" >= 0 AND \"JitterSeconds\" >= 0");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(
            name: "ck_connection_sync_policy_intervals",
            schema: "integration",
            table: "connection_sync_policies");

        migrationBuilder.AddCheckConstraint(
            name: "ck_connection_sync_policy_intervals",
            schema: "integration",
            table: "connection_sync_policies",
            sql: "\"IntervalSeconds\" > 0 AND \"OverlapSeconds\" >= 0 AND \"JitterSeconds\" >= 0");
    }
}
