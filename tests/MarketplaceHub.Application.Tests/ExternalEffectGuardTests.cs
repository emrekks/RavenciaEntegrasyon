using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// ISLEYICI-10 (15 Nov 2026): the guard every marketplace write of the job processor goes through.
[Collection("postgres-integration")]
public sealed class ExternalEffectGuardTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    [Theory]
    [InlineData(AdapterErrorClass.TransientNetwork, "X_TIMEOUT", "Uncertain")]
    [InlineData(AdapterErrorClass.Remote5xx, "X_5XX", "Uncertain")]
    [InlineData(AdapterErrorClass.ContractViolation, "X_CONTRACT", "Uncertain")]
    [InlineData(AdapterErrorClass.InternalBug, "X_BUG", "Uncertain")]
    [InlineData(AdapterErrorClass.RateLimit, "TRENDYOL_LOCAL_CIRCUIT_OPEN", "LocalCircuit")]
    [InlineData(AdapterErrorClass.RateLimit, "TRENDYOL_RATE_LIMITED", "Retryable")]
    [InlineData(AdapterErrorClass.Validation, "X_INVALID", "Refused")]
    [InlineData(AdapterErrorClass.NotFound, "X_MISSING", "Refused")]
    [InlineData(AdapterErrorClass.Authentication, "X_AUTH", "Refused")]
    public void Classify_SortsEachAnswer(AdapterErrorClass errorClass, string code, string expected) =>
        Assert.Equal(expected, ExternalEffectGuard.Classify(new AdapterError(errorClass, code, "m", null, null, null)).ToString());

    [Fact]
    public void Gate_ReadsAnExistingRecord()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(ExternalEffectGate.None, ExternalEffectGuard.Gate(null));
        Assert.Equal(ExternalEffectGate.Open, ExternalEffectGuard.Gate(new ExternalEffectRecord { EffectType = "T", IdempotencyKey = "k" }));
        Assert.Equal(ExternalEffectGate.Unresolved, ExternalEffectGuard.Gate(new ExternalEffectRecord { EffectType = "T", IdempotencyKey = "k", CompletedAt = now, Outcome = ExternalEffectReconcilePolicy.UnresolvedOutcome }));
        Assert.Equal(ExternalEffectGate.Done, ExternalEffectGuard.Gate(new ExternalEffectRecord { EffectType = "T", IdempotencyKey = "k", CompletedAt = now }));
        Assert.Equal(ExternalEffectGate.Done, ExternalEffectGuard.Gate(new ExternalEffectRecord { EffectType = "T", IdempotencyKey = "k", CompletedAt = now, Outcome = ExternalEffectReconcilePolicy.AppliedOutcome }));
    }

    [PostgreSqlFact]
    public async Task SendAsync_SavesTheRecordBeforeTheSend_AndCompletesItOnlyWhenAccepted()
    {
        var tenant = await SeedTenantAsync("guard-send");
        try
        {
            await using var db = fixture.CreateContext();
            var guard = new ExternalEffectGuard(db, fixture.TimeProvider);
            var accepted = guard.New(tenant, "GUARD_TEST", "accepted", action: "PICKING");
            var sent = await guard.SendAsync(accepted, async () =>
            {
                // The record is in the database while the request is out.
                await using var other = fixture.CreateContext();
                var stored = await other.ExternalEffectRecords.AsNoTracking().SingleAsync(x => x.Id == accepted.Id);
                Assert.Null(stored.CompletedAt);
                return AdapterResult<bool>.Success(true);
            }, CancellationToken.None);
            Assert.True(sent.Accepted);
            Assert.NotNull(accepted.CompletedAt);

            var unclear = guard.New(tenant, "GUARD_TEST", "unclear");
            var unclearSend = await guard.SendAsync(unclear, () => Task.FromResult(AdapterResult<bool>.Failure(new AdapterError(AdapterErrorClass.Remote5xx, "X_5XX", "m", 500, null, null))), CancellationToken.None);
            Assert.Equal(ExternalEffectSendOutcome.Uncertain, unclearSend.Outcome);
            Assert.Null(unclear.CompletedAt);

            var refused = guard.New(tenant, "GUARD_TEST", "refused");
            var refusedSend = await guard.SendAsync(refused, () => Task.FromResult(AdapterResult<bool>.Failure(new AdapterError(AdapterErrorClass.Validation, "X_INVALID", "m", 400, null, null))), CancellationToken.None);
            Assert.Equal(ExternalEffectSendOutcome.Refused, refusedSend.Outcome);
            await guard.ReleaseAsync(refused, CancellationToken.None);
            await db.SaveChangesAsync();

            await using var check = fixture.CreateContext();
            var rows = await check.ExternalEffectRecords.AsNoTracking().Where(x => x.TenantId == tenant).ToDictionaryAsync(x => x.IdempotencyKey);
            Assert.Equal(["accepted", "unclear"], rows.Keys.Order());
            Assert.NotNull(rows["accepted"].CompletedAt);
            Assert.Equal("PICKING", rows["accepted"].Action);
            Assert.Null(rows["unclear"].CompletedAt);
            Assert.Equal(ExternalEffectGate.Open, ExternalEffectGuard.Gate(await guard.FindAsync(tenant, "GUARD_TEST", "unclear", CancellationToken.None)));
            Assert.True(await guard.ExistsAsync(tenant, "GUARD_TEST", "accepted", CancellationToken.None));
            Assert.False(await guard.ExistsAsync(tenant, "GUARD_TEST", "refused", CancellationToken.None));
        }
        finally { await CleanupAsync(tenant); }
    }

    [PostgreSqlFact]
    public async Task SendAsync_AThrownSendLeavesTheRecordOpen_AndAGroupedSendCompletesEveryRecord()
    {
        var tenant = await SeedTenantAsync("guard-group");
        try
        {
            await using (var db = fixture.CreateContext())
            {
                var guard = new ExternalEffectGuard(db, fixture.TimeProvider);
                var cut = guard.New(tenant, "GUARD_TEST", "cut");
                await Assert.ThrowsAsync<OperationCanceledException>(() => guard.SendAsync<bool>(cut, () => throw new OperationCanceledException(), CancellationToken.None));
                var group = new[] { guard.New(tenant, "GUARD_TEST", "g1"), guard.New(tenant, "GUARD_TEST", "g2") };
                var sent = await guard.SendAsync(group, () => Task.FromResult(AdapterResult<bool>.Success(true)), CancellationToken.None);
                Assert.True(sent.Accepted);
                Assert.Equal(group[0].CompletedAt, group[1].CompletedAt);
                await db.SaveChangesAsync();
            }
            await using var check = fixture.CreateContext();
            var rows = await check.ExternalEffectRecords.AsNoTracking().Where(x => x.TenantId == tenant).ToDictionaryAsync(x => x.IdempotencyKey);
            Assert.Null(rows["cut"].CompletedAt);
            Assert.NotNull(rows["g1"].CompletedAt);
            Assert.NotNull(rows["g2"].CompletedAt);
        }
        finally { await CleanupAsync(tenant); }
    }

    private async Task<Guid> SeedTenantAsync(string code)
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"{code}-{Guid.NewGuid():N}", DisplayName = code, Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        await using var db = fixture.CreateContext();
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        return tenant.Id;
    }

    private async Task CleanupAsync(Guid tenantId)
    {
        await using var db = fixture.CreateContext();
        await db.ExternalEffectRecords.Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
        await db.Tenants.Where(x => x.Id == tenantId).ExecuteDeleteAsync();
    }
}
