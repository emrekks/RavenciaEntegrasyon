using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// "API Sağlığı": how the stored attempt codes, cursors and backoffs read as signals.
public sealed class ApiHealthSignalPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("N11_CREDENTIALS_REJECTED", true)]
    [InlineData("PTTAVM_CREDENTIALS_REJECTED", true)]
    [InlineData("PAZARAMA_TOKEN_INVALID", false)]
    [InlineData("N11_REQUEST_REJECTED", false)]
    public void AuthRefusal_IsTheCredentialsRefusalCodeOnly(string code, bool expected) =>
        Assert.Equal(expected, ApiHealthSignalPolicy.IsAuthRefusal(code));

    [Fact]
    public void ErrorRate_SplitsFailuresBySide_AndKeepsFlowWaitsOutOfTheRate()
    {
        var signal = ApiHealthSignalPolicy.ErrorRate([
            (true, null), (true, null), (true, null),
            (false, "N11_REMOTE_ERROR"), (false, "REMOTE_TIMEOUT"), (false, "PAZARAMA_NETWORK_ERROR"),
            (false, "N11_REQUEST_REJECTED"), (false, "N11_CREDENTIALS_REJECTED"),
            (false, "HEPSIBURADA_RATE_LIMITED"),
            (false, "PRODUCT_APPROVAL_PENDING"), (false, null)
        ]);
        Assert.Equal(new ApiHealthErrorRateSignal(11, 8, 3, 2, 1, 2), signal);
        Assert.Equal(6d / 11, ApiHealthSignalPolicy.FailureRate(signal));
        Assert.Null(ApiHealthSignalPolicy.FailureRate(ApiHealthSignalPolicy.ErrorRate([])));
    }

    [Theory]
    [InlineData(120, 120, "ok")]
    [InlineData(360, 120, "ok")]
    [InlineData(361, 120, "warn")]
    [InlineData(1200, 120, "warn")]
    [InlineData(1201, 120, "fault")]
    public void LagLevel_WarnsAbove3x_AndFaultsAbove10x(long lag, int expected, string level) =>
        Assert.Equal(level, ApiHealthSignalPolicy.LagLevel(lag, expected));

    [Fact]
    public void WorstLag_PicksTheStreamFurthestBehindItsInterval_AndSkipsUnmeasuredOnes()
    {
        var worst = ApiHealthSignalPolicy.WorstLag([
            ("ORDERS", Now.AddMinutes(-40), 120),
            ("RETURNS", Now.AddMinutes(-20), 600),
            ("QUESTIONS", null, 120)
        ], Now);
        Assert.Equal(new ApiHealthLagSignal("ORDERS", 2400, 120, "fault"), worst);
        Assert.Null(ApiHealthSignalPolicy.WorstLag([("ORDERS", null, 120), ("RETURNS", Now, 0)], Now));
    }

    [Fact]
    public void Auth_IsRejectedOnlyWhileNoSuccessFollowsTheRefusal()
    {
        Assert.Equal(ApiHealthSignalPolicy.AuthRejected, ApiHealthSignalPolicy.Auth(Now.AddHours(-2), "N11_CREDENTIALS_REJECTED", Now.AddHours(-5), null, Now).State);
        Assert.Equal(ApiHealthSignalPolicy.AuthValid, ApiHealthSignalPolicy.Auth(Now.AddHours(-2), "N11_CREDENTIALS_REJECTED", Now.AddHours(-1), null, Now).State);
        Assert.Equal(ApiHealthSignalPolicy.AuthRejected, ApiHealthSignalPolicy.Auth(null, null, Now.AddHours(-1), Now.AddMinutes(10), Now).State);
        Assert.Equal(ApiHealthSignalPolicy.AuthUnknown, ApiHealthSignalPolicy.Auth(null, null, null, null, Now).State);
        Assert.Equal(ApiHealthSignalPolicy.AuthValid, ApiHealthSignalPolicy.Auth(null, null, Now.AddHours(-1), null, Now).State);
    }
}

[Collection("postgres-integration")]
public sealed class ApiHealthSignalsPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    [PostgreSqlFact]
    public async Task Signals_ReadAttemptsQueueAndRefusalsOfOneConnection()
    {
        var tenantId = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        var connection = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = tenantId, PublicId = Guid.CreateVersion7(), PlatformCode = "N11", Environment = "PRODUCTION", DisplayName = "N11 sinyal testi", ExternalStoreId = $"store-{Guid.NewGuid():N}", Status = "ACTIVE", ApiVersion = "V1", SettingsJson = "{}", Version = 1 };
        IntegrationJob Job(JobStatus status, DateTimeOffset available) => new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            ConnectionId = connection.Id,
            JobType = MarketplaceJobTypes.PriceInventorySync,
            PayloadJson = "{}",
            PayloadVersion = 1,
            PayloadHash = "h",
            JobDedupKey = $"sig:{Guid.NewGuid():N}",
            EffectIdempotencyKey = $"sig:{Guid.NewGuid():N}",
            Status = status,
            AvailableAt = available,
            CorrelationId = "test",
            CreatedAt = available,
            Version = 1
        };
        var finished = Job(JobStatus.Succeeded, now.AddHours(-3));
        var waiting = Job(JobStatus.RetryScheduled, now.AddMinutes(-30));
        waiting.LastErrorCode = "N11_RATE_LIMITED";
        JobAttempt Attempt(IntegrationJob job, int number, DateTimeOffset at, bool ok, string? code) => new() { Id = Guid.CreateVersion7(), TenantId = tenantId, JobId = job.Id, AttemptNumber = number, StartedAt = at, CompletedAt = at.AddSeconds(1), Succeeded = ok, ErrorCode = code };
        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.Add(new Tenant { Id = tenantId, Code = $"signals-{Guid.NewGuid():N}", DisplayName = "signals", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 });
            seed.PlatformConnections.Add(connection);
            seed.IntegrationJobs.AddRange(finished, waiting);
            await seed.SaveChangesAsync();
            seed.JobAttempts.AddRange(
                Attempt(finished, 1, now.AddHours(-3), false, "N11_CREDENTIALS_REJECTED"),
                Attempt(finished, 2, now.AddHours(-2), true, null),
                Attempt(waiting, 1, now.AddHours(-1), false, "N11_REMOTE_ERROR"),
                Attempt(waiting, 2, now.AddMinutes(-30), false, "N11_RATE_LIMITED"));
            await seed.SaveChangesAsync();
        }
        try
        {
            await using var db = fixture.CreateContext();
            var view = await new ApiHealthSignalsService(db, TimeProvider.System).SignalsAsync(tenantId, CancellationToken.None);
            var row = Assert.Single(view.Connections);
            Assert.Equal(ApiHealthSignalPolicy.AuthValid, row.Auth.State);
            Assert.NotNull(row.Auth.LastRejectedAt);
            Assert.Equal(new ApiHealthErrorRateSignal(4, 3, 1, 1, 1, 0), row.ErrorRate);
            Assert.Equal(1, row.RateLimit.Refused24h);
            Assert.Equal(1, row.RateLimit.WaitingJobs);
            Assert.NotNull(row.Queue);
            Assert.InRange(row.Queue!.AgeSeconds, 29 * 60, 32 * 60);
            Assert.Null(row.Lag);
            Assert.False(row.Webhook.Used);
        }
        finally
        {
            await using var cleanup = fixture.CreateContext();
            await cleanup.JobAttempts.Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
            await cleanup.IntegrationJobs.Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
        }
    }
}
