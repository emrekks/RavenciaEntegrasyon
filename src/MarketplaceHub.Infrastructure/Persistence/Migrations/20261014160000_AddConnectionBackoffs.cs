using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20261014160000_AddConnectionBackoffs")]
public sealed class AddConnectionBackoffs : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // A connection + lock group that hit a marketplace quota: its jobs are not leased until "Until".
        migrationBuilder.Sql("""
            CREATE TABLE integration.connection_backoffs (
                "ConnectionId" uuid NOT NULL,
                "LockGroup" varchar(64) NOT NULL,
                "Until" timestamptz NOT NULL,
                "Reason" varchar(128) NULL,
                PRIMARY KEY ("ConnectionId", "LockGroup")
            );
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP TABLE IF EXISTS integration.connection_backoffs;");
    }
}
