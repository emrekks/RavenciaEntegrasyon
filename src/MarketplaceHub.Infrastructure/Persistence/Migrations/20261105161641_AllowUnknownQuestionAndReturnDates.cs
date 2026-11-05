using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketplaceHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AllowUnknownQuestionAndReturnDates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "LastRemoteModifiedAt",
                schema: "sales",
                table: "return_claims",
                type: "timestamp with time zone",
                nullable: true,
                oldClrType: typeof(DateTimeOffset),
                oldType: "timestamp with time zone");

            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "LastRemoteModifiedAt",
                schema: "sales",
                table: "marketplace_questions",
                type: "timestamp with time zone",
                nullable: true,
                oldClrType: typeof(DateTimeOffset),
                oldType: "timestamp with time zone");

            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "CreatedAt",
                schema: "sales",
                table: "marketplace_questions",
                type: "timestamp with time zone",
                nullable: true,
                oldClrType: typeof(DateTimeOffset),
                oldType: "timestamp with time zone");

            // A date a marketplace did not give was once stored as exactly the Unix epoch; it is unknown, not 1970.
            // Only exact-epoch values change (live had none when this was written).
            migrationBuilder.Sql("UPDATE sales.marketplace_questions SET \"CreatedAt\" = NULL WHERE \"CreatedAt\" = TIMESTAMPTZ '1970-01-01 00:00:00+00';");
            migrationBuilder.Sql("UPDATE sales.marketplace_questions SET \"LastRemoteModifiedAt\" = NULL WHERE \"LastRemoteModifiedAt\" = TIMESTAMPTZ '1970-01-01 00:00:00+00';");
            migrationBuilder.Sql("UPDATE sales.return_claims SET \"LastRemoteModifiedAt\" = NULL WHERE \"LastRemoteModifiedAt\" = TIMESTAMPTZ '1970-01-01 00:00:00+00';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "LastRemoteModifiedAt",
                schema: "sales",
                table: "return_claims",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)),
                oldClrType: typeof(DateTimeOffset),
                oldType: "timestamp with time zone",
                oldNullable: true);

            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "LastRemoteModifiedAt",
                schema: "sales",
                table: "marketplace_questions",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)),
                oldClrType: typeof(DateTimeOffset),
                oldType: "timestamp with time zone",
                oldNullable: true);

            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "CreatedAt",
                schema: "sales",
                table: "marketplace_questions",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)),
                oldClrType: typeof(DateTimeOffset),
                oldType: "timestamp with time zone",
                oldNullable: true);
        }
    }
}
