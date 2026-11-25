using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Scan 25 Nov 2026 (E-5): an UNKNOWN idempotency record had its window pushed on by seven days at every sweep, so it was
// never removed. It now goes once it is older than 30 days; a younger one is still kept as UNKNOWN.
[Collection("postgres-integration")]
public sealed class ApiIdempotencyRetentionPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    [PostgreSqlFact]
    public async Task AnUnknownRecord_IsKeptForAWhile_ButRemovedOnceOlderThan30Days()
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"idem-{Guid.NewGuid():N}", DisplayName = "idem", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        ApiIdempotencyRecord Record(string state, int ageDays) => new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            RouteTemplate = "/api/v1/test",
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            RequestHash = "hash",
            State = state,
            CreatedAt = now.AddDays(-ageDays),
            ExpiresAt = now.AddMinutes(-1)
        };
        var oldUnknown = Record("UNKNOWN", 40);
        var youngUnknown = Record("UNKNOWN", 10);
        var inProgress = Record("IN_PROGRESS", 1);
        await using (var setup = fixture.CreateContext())
        {
            setup.Tenants.Add(tenant);
            setup.ApiIdempotencyRecords.AddRange(oldUnknown, youngUnknown, inProgress);
            await setup.SaveChangesAsync();
        }

        await using (var db = fixture.CreateContext())
            await ApiIdempotencyRetention.SweepAsync(db, now, CancellationToken.None);

        await using var check = fixture.CreateContext();
        var rows = await check.ApiIdempotencyRecords.AsNoTracking().Where(x => x.TenantId == tenant.Id).ToDictionaryAsync(x => x.Id);
        Assert.False(rows.ContainsKey(oldUnknown.Id));
        Assert.Equal("UNKNOWN", rows[youngUnknown.Id].State);
        Assert.Equal("UNKNOWN", rows[inProgress.Id].State);
        Assert.True(rows[youngUnknown.Id].ExpiresAt > now);
    }
}
