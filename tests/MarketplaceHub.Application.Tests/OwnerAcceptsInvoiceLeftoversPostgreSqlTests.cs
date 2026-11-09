using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// OwnerAcceptsInvoiceLeftovers on PostgreSQL: the live issue and the three INVOICE_SUBMIT jobs (same ids and tenant) are
// closed with the owner's note and audited once; a job of the same tenant with another id, and a second run, change nothing.
[Collection("postgres-integration")]
public sealed class OwnerAcceptsInvoiceLeftoversPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    private static readonly Guid TenantId = Guid.Parse("0fcd3304-4bd4-4cfb-bbed-a2c197b9d5b1");
    private static readonly Guid IssueId = Guid.Parse("01a15917-ec34-7b89-b408-c86ca123c5cf");
    private static readonly Guid UnknownJobId = Guid.Parse("01a135af-3bb2-7e59-ba6d-1001ba31d196");
    private static readonly Guid RejectedJobA = Guid.Parse("01a135f7-e9b1-7d46-8f2c-199a43ae96ef");
    private static readonly Guid RejectedJobB = Guid.Parse("01a135fe-75cc-76ea-8434-c54bdebd7ee7");

    [PostgreSqlFact]
    public async Task Closes_exactly_the_four_rows_once()
    {
        var tenant = new Tenant { Id = TenantId, Code = $"owner-accept-{Guid.NewGuid():N}", DisplayName = "owner-accept", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = fixture.Now, UpdatedAt = fixture.Now, Version = 1 };
        IntegrationJob Job(Guid id, JobStatus status, string code) => new()
        {
            Id = id,
            TenantId = TenantId,
            JobType = "INVOICE_SUBMIT",
            PayloadJson = "{}",
            PayloadVersion = 1,
            PayloadHash = "h",
            JobDedupKey = $"owner-accept-{id:N}",
            EffectIdempotencyKey = $"owner-accept-{id:N}",
            Priority = 1,
            Status = status,
            AvailableAt = fixture.Now,
            MaxAttempts = 3,
            CorrelationId = $"owner-accept-{id:N}",
            CreatedAt = fixture.Now,
            CompletedAt = fixture.Now,
            LastErrorCode = code,
            LastErrorSummary = "önce",
            Version = 2
        };
        var other = Job(Guid.CreateVersion7(), JobStatus.Blocked, "EFATURAM_REQUEST_REJECTED");

        await using (var db = fixture.CreateContext())
        {
            db.Tenants.Add(tenant);
            db.OperationalIssues.Add(new OperationalIssue { Id = IssueId, TenantId = TenantId, DedupeKey = "invoice-adjustment:01a121b028bf7bc79e3e0c1ca397c0ae", Code = "INVOICE_ADJUSTMENT_REQUIRED", Summary = "DAP2026000000015 faturası için e-Arşiv iptali veya iade faturası gerekli.", Status = IssueStatus.Open, FirstSeenAt = fixture.Now, LastSeenAt = fixture.Now, OccurrenceCount = 1 });
            db.IntegrationJobs.AddRange(
                Job(UnknownJobId, JobStatus.ManualReview, "INVOICE_SUBMISSION_OUTCOME_UNKNOWN"),
                Job(RejectedJobA, JobStatus.Blocked, "EFATURAM_REQUEST_REJECTED"),
                Job(RejectedJobB, JobStatus.Blocked, "EFATURAM_REQUEST_REJECTED"),
                other);
            await db.SaveChangesAsync();
        }

        try
        {
            var migration = new OwnerAcceptsInvoiceLeftovers();
            for (var run = 0; run < 2; run++)
            {
                await using var db = fixture.CreateContext();
                foreach (var operation in migration.UpOperations.OfType<SqlOperation>())
                    await db.Database.ExecuteSqlRawAsync(operation.Sql);
            }

            await using var check = fixture.CreateContext();
            var issue = await check.OperationalIssues.AsNoTracking().SingleAsync(x => x.Id == IssueId);
            Assert.Equal(IssueStatus.Resolved, issue.Status);
            Assert.NotNull(issue.ResolvedAt);
            Assert.EndsWith("Sahip kararı 9 Kas 2026: başarılı sayıldı.", issue.Summary, StringComparison.Ordinal);

            var jobs = await check.IntegrationJobs.AsNoTracking().Where(x => x.TenantId == TenantId).ToDictionaryAsync(x => x.Id);
            foreach (var id in new[] { UnknownJobId, RejectedJobA, RejectedJobB })
            {
                Assert.Equal(JobStatus.Succeeded, jobs[id].Status);
                Assert.Equal("OWNER_ACCEPTED", jobs[id].LastErrorCode);
                Assert.StartsWith("Sahip kararı 9 Kas 2026", jobs[id].LastErrorSummary, StringComparison.Ordinal);
                Assert.Equal(3L, jobs[id].Version);
            }
            Assert.Equal(JobStatus.Blocked, jobs[other.Id].Status);
            Assert.Equal(2L, jobs[other.Id].Version);

            var audits = await check.AuditLogs.AsNoTracking().Where(x => x.TenantId == TenantId && x.Action == "OWNER_ACCEPTED").Select(x => x.TargetId).ToListAsync();
            Assert.Equal(new[] { IssueId, UnknownJobId, RejectedJobA, RejectedJobB }.Select(x => x.ToString()).Order(), audits.Order());
        }
        finally
        {
            await using var db = fixture.CreateContext();
            await db.AuditLogs.Where(x => x.TenantId == TenantId).ExecuteDeleteAsync();
            await db.IntegrationOutboxEvents.Where(x => x.TenantId == TenantId).ExecuteDeleteAsync();
            await db.IntegrationJobs.Where(x => x.TenantId == TenantId).ExecuteDeleteAsync();
            await db.OperationalIssues.Where(x => x.TenantId == TenantId).ExecuteDeleteAsync();
            await db.Tenants.Where(x => x.Id == TenantId).ExecuteDeleteAsync();
        }
    }
}
