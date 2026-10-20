using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// İ6: the connection view says when its refused-credential code was last seen and when it was disabled.
[Collection("postgres-integration")]
public sealed class PostgreSqlConnectionErrorViewTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    [PostgreSqlFact]
    public async Task ConnectionView_CarriesTheLastTimeItsErrorCodeFailedAJobAndTheDisableTime()
    {
        var tenant = new Tenant
        {
            Id = Guid.CreateVersion7(),
            Code = $"connection-error-view-{Guid.NewGuid():N}",
            DisplayName = "connection-error-view",
            Status = RecordStatus.Active,
            Timezone = "Europe/Istanbul",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Version = 1
        };
        var now = new DateTimeOffset(DateTimeOffset.UtcNow.UtcTicks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero);
        var failing = NewConnection(tenant, "PAZARAMA");
        failing.LastErrorCode = "PAZARAMA_CREDENTIALS_REJECTED";
        failing.LastTestedAt = now.AddDays(-2);
        failing.DisabledAt = now.AddHours(-1);
        failing.Status = "DISABLED";
        var healthy = NewConnection(tenant, "TRENDYOL");
        // Never PENDING: the lease tests in the same database take any pending job.
        var job = new IntegrationJob
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            ConnectionId = failing.Id,
            JobType = "TEST_CONNECTION_ERROR_VIEW",
            PayloadJson = "{}",
            PayloadVersion = 1,
            PayloadHash = "test-payload-hash",
            JobDedupKey = $"connection-error-view-{Guid.NewGuid():N}",
            EffectIdempotencyKey = $"effect-{Guid.NewGuid():N}",
            Priority = 1,
            Status = JobStatus.Blocked,
            AvailableAt = now.AddHours(1),
            AttemptCount = 3,
            MaxAttempts = 5,
            CorrelationId = $"corr-{Guid.NewGuid():N}",
            CreatedAt = now.AddHours(-5),
            Version = 1
        };
        JobAttempt Attempt(int number, DateTimeOffset completedAt, string code) => new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            JobId = job.Id,
            AttemptNumber = number,
            StartedAt = completedAt.AddSeconds(-2),
            CompletedAt = completedAt,
            Succeeded = false,
            ErrorCode = code
        };

        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.Add(tenant);
            seed.PlatformConnections.AddRange(failing, healthy);
            seed.IntegrationJobs.Add(job);
            seed.JobAttempts.AddRange(
                Attempt(1, now.AddHours(-4), "PAZARAMA_CREDENTIALS_REJECTED"),
                Attempt(2, now.AddHours(-3), "PAZARAMA_CREDENTIALS_REJECTED"),
                // A later failure with another code does not move the time of the current one.
                Attempt(3, now.AddHours(-2), "REMOTE_TIMEOUT"));
            await seed.SaveChangesAsync();
        }

        try
        {
            await using var db = fixture.CreateContext();
            var dataProtection = new EphemeralDataProtectionProvider();
            var service = new MarketplaceConnectionService(db, new CursorCodec(dataProtection, fixture.TimeProvider), dataProtection, fixture.TokenHasher, fixture.TimeProvider, new ConfigurationBuilder().Build());

            var detail = await service.GetAsync(tenant.Id, failing.Id, CancellationToken.None);
            Assert.True(detail.Succeeded, detail.Error?.Message);
            Assert.Equal("PAZARAMA_CREDENTIALS_REJECTED", detail.Value!.LastErrorCode);
            Assert.Equal(now.AddHours(-3), detail.Value.LastErrorAt);
            Assert.Equal(now.AddHours(-1), detail.Value.DisabledAt);

            var list = await service.ListAsync(tenant.Id, 50, null, CancellationToken.None);
            Assert.Equal(now.AddHours(-3), Assert.Single(list.Items, view => view.Id == failing.Id).LastErrorAt);
            var healthyView = Assert.Single(list.Items, view => view.Id == healthy.Id);
            Assert.Null(healthyView.LastErrorAt);
            Assert.Null(healthyView.DisabledAt);
        }
        finally
        {
            await using var cleanup = fixture.CreateContext();
            await cleanup.JobAttempts.Where(x => x.TenantId == tenant.Id).ExecuteDeleteAsync();
            await cleanup.IntegrationJobs.Where(x => x.TenantId == tenant.Id).ExecuteDeleteAsync();
            await cleanup.PlatformConnections.Where(x => x.TenantId == tenant.Id).ExecuteDeleteAsync();
            await cleanup.Tenants.Where(x => x.Id == tenant.Id).ExecuteDeleteAsync();
        }
    }

    private static PlatformConnection NewConnection(Tenant tenant, string platformCode) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenant.Id,
        PublicId = Guid.CreateVersion7(),
        PlatformCode = platformCode,
        Environment = "PRODUCTION",
        DisplayName = $"{platformCode} hata görünümü testi",
        ExternalStoreId = $"store-{Guid.NewGuid():N}",
        Status = "ACTIVE",
        ApiVersion = "V1",
        SettingsJson = "{}"
    };
}
