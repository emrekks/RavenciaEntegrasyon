using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// CloseZeroPackageConflictIssue on PostgreSQL: the live issue (same id and tenant) is resolved with the owner's note and
// audited once; another open issue of the same code stays open; a second run changes nothing.
[Collection("postgres-integration")]
public sealed class CloseZeroPackageConflictIssuePostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    private static readonly Guid TenantId = Guid.Parse("0fcd3304-4bd4-4cfb-bbed-a2c197b9d5b1");
    private static readonly Guid IssueId = Guid.Parse("01a14a69-f804-728c-a908-fbb302c95f3b");

    [PostgreSqlFact]
    public async Task Resolves_only_that_issue_once()
    {
        var tenant = new Tenant { Id = TenantId, Code = $"zero-pkg-{Guid.NewGuid():N}", DisplayName = "zero-pkg", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = fixture.Now, UpdatedAt = fixture.Now, Version = 1 };
        OperationalIssue Issue(Guid id, string key) => new() { Id = id, TenantId = TenantId, DedupeKey = key, Code = "ORDER_PACKAGE_ID_CONFLICT", Summary = "Paket kimliği çakışıyor.", Status = IssueStatus.Open, FirstSeenAt = fixture.Now, LastSeenAt = fixture.Now, OccurrenceCount = 6 };
        var other = Issue(Guid.CreateVersion7(), "order-package-identity:01a05a69-86cd-7a57-af4f-d9d7276a5437:123");

        await using (var db = fixture.CreateContext())
        {
            db.Tenants.Add(tenant);
            db.OperationalIssues.AddRange(Issue(IssueId, "order-package-identity:01a05a69-86cd-7a57-af4f-d9d7276a5437:0"), other);
            await db.SaveChangesAsync();
        }

        try
        {
            var migration = new CloseZeroPackageConflictIssue();
            for (var run = 0; run < 2; run++)
            {
                await using var db = fixture.CreateContext();
                foreach (var operation in migration.UpOperations.OfType<SqlOperation>())
                    await db.Database.ExecuteSqlRawAsync(operation.Sql);
            }

            await using var check = fixture.CreateContext();
            var issues = await check.OperationalIssues.AsNoTracking().Where(x => x.TenantId == TenantId).ToDictionaryAsync(x => x.Id);
            Assert.Equal(IssueStatus.Resolved, issues[IssueId].Status);
            Assert.NotNull(issues[IssueId].ResolvedAt);
            Assert.EndsWith("Sahip kararı 9 Kas 2026: '0' paket kimliği artık çakışma sayılmıyor, kapatıldı.", issues[IssueId].Summary, StringComparison.Ordinal);
            Assert.Equal(IssueStatus.Open, issues[other.Id].Status);
            Assert.Equal("Paket kimliği çakışıyor.", issues[other.Id].Summary);

            var audits = await check.AuditLogs.AsNoTracking().Where(x => x.TenantId == TenantId && x.Action == "OWNER_ACCEPTED").Select(x => x.TargetId).ToListAsync();
            Assert.Equal([IssueId.ToString()], audits);
        }
        finally
        {
            await using var db = fixture.CreateContext();
            await db.AuditLogs.Where(x => x.TenantId == TenantId).ExecuteDeleteAsync();
            await db.IntegrationOutboxEvents.Where(x => x.TenantId == TenantId).ExecuteDeleteAsync();
            await db.OperationalIssues.Where(x => x.TenantId == TenantId).ExecuteDeleteAsync();
            await db.Tenants.Where(x => x.Id == TenantId).ExecuteDeleteAsync();
        }
    }
}
