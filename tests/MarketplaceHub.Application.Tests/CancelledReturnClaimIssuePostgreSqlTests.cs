using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Deep audit 2026-10-20 G33: the "cancelled claim kept with its history" issue is raised when the
// claim becomes cancelled, and a later read of the same cancelled claim does not reopen it.
[Collection("postgres-integration")]
public sealed class CancelledReturnClaimIssuePostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    [PostgreSqlFact]
    public async Task ARereadOfAKeptCancelledClaimDoesNotReopenItsIssue()
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"g33-{Guid.NewGuid():N}"[..16], DisplayName = "g33", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var connection = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = "TRENDYOL", Environment = "STAGE", DisplayName = "g33", ExternalStoreId = $"s-{Guid.NewGuid():N}"[..12], Status = "ACTIVE", ApiVersion = "V1" };
        var order = new Order { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, ExternalOrderId = "g33", OrderNumber = "g33", Currency = "TRY", OrderedAt = now.AddDays(-3), LastRemoteModifiedAt = now, CustomerSnapshotJson = "{}", ShipmentAddressSnapshotJson = "{}", InvoiceAddressSnapshotJson = "{}", DerivedStatus = "DELIVERED", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var claim = new ReturnClaim { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, OrderId = order.Id, ExternalClaimId = "C-33", Status = ReturnClaimStatus.ActionRequired, RawStatus = "WaitingInAction", LastRemoteModifiedAt = now.AddDays(-1), CreatedAt = now, UpdatedAt = now, Version = 1 };
        await using (var db = fixture.CreateContext())
        {
            db.Tenants.Add(tenant);
            db.PlatformConnections.Add(connection);
            db.Orders.Add(order);
            db.ReturnClaims.Add(claim);
            db.ReturnDecisions.Add(new ReturnDecision { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ClaimId = claim.Id, Action = "APPROVE", IdempotencyKey = $"g33-{Guid.NewGuid():N}", Status = "SUCCEEDED", CreatedAt = now });
            await db.SaveChangesAsync();
        }
        try
        {
            await using (var first = fixture.CreateContext())
            {
                Assert.True(await CancelledReturnClaimCleanup.RemoveOrRetainAsync(first, tenant.Id, claim.Id, "Cancelled", now, now, CancellationToken.None));
                await first.SaveChangesAsync();
            }
            await using (var operatorDb = fixture.CreateContext())
            {
                var issue = await operatorDb.OperationalIssues.SingleAsync(x => x.TenantId == tenant.Id && x.Code == CancelledReturnClaimCleanup.RetainedIssueCode);
                issue.Status = IssueStatus.Resolved;
                await operatorDb.SaveChangesAsync();
            }
            await using (var reread = fixture.CreateContext())
            {
                Assert.True(await CancelledReturnClaimCleanup.RemoveOrRetainAsync(reread, tenant.Id, claim.Id, "Cancelled", now.AddMinutes(5), now.AddMinutes(5), CancellationToken.None));
                await reread.SaveChangesAsync();
            }
            await using var verify = fixture.CreateContext();
            var kept = await verify.OperationalIssues.AsNoTracking().SingleAsync(x => x.TenantId == tenant.Id && x.Code == CancelledReturnClaimCleanup.RetainedIssueCode);
            Assert.Equal(IssueStatus.Resolved, kept.Status);
            Assert.Equal(1, kept.OccurrenceCount);
        }
        finally
        {
            await using var db = fixture.CreateContext();
            await db.JobAttempts.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await db.IntegrationJobs.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await db.IntegrationOutboxEvents.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await db.OperationalIssues.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await db.ReturnDecisions.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await db.ReturnClaims.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await db.Orders.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await db.PlatformConnections.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await db.Tenants.Where(row => row.Id == tenant.Id).ExecuteDeleteAsync();
        }
    }
}
