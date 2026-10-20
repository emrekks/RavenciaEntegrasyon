using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Owner decision: a Trendyol User-Agent outside "SatıcıID - FirmaAdı" is only a warning, but an identity that cannot
// be sent as a header at all (Turkish letters, control characters) is refused on save.
[Collection("postgres-integration")]
public sealed class PostgreSqlTrendyolUserAgentTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    [PostgreSqlFact]
    public async Task AnUnsendableIdentityIsRefusedOnCreateAndUpdateWhileAnotherFormatIsSaved()
    {
        var tenant = new Tenant
        {
            Id = Guid.CreateVersion7(),
            Code = $"trendyol-user-agent-{Guid.NewGuid():N}",
            DisplayName = "trendyol-user-agent",
            Status = RecordStatus.Active,
            Timezone = "Europe/Istanbul",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Version = 1
        };
        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.Add(tenant);
            await seed.SaveChangesAsync();
        }

        try
        {
            await using var db = fixture.CreateContext();
            var dataProtection = new EphemeralDataProtectionProvider();
            var service = new MarketplaceConnectionService(db, new CursorCodec(dataProtection, fixture.TimeProvider), dataProtection, fixture.TokenHasher, fixture.TimeProvider, new ConfigurationBuilder().Build());
            var store = $"{Random.Shared.Next(100000, 999999)}";

            var refused = await service.CreateAsync(tenant.Id, new CreateConnectionCommand("Trendyol", "STAGE", store, "V2", $"{store} - Gümüş", "TRENDYOL"), CancellationToken.None);
            Assert.False(refused.Succeeded);
            Assert.Equal("VALIDATION_FAILED", refused.Error!.Code);
            Assert.Contains("Türkçe karakter", refused.Error.Message, StringComparison.Ordinal);
            Assert.Equal(0, await db.PlatformConnections.CountAsync(x => x.TenantId == tenant.Id));

            var saved = await service.CreateAsync(tenant.Id, new CreateConnectionCommand("Trendyol", "STAGE", store, "V2", "Firma Entegrasyon", "TRENDYOL"), CancellationToken.None);
            Assert.True(saved.Succeeded, saved.Error?.Message);

            var refusedUpdate = await service.UpdateAsync(tenant.Id, saved.Value!.Id, saved.Value.Version, new UpdateConnectionCommand("Trendyol", "Ravencİa"), CancellationToken.None);
            Assert.False(refusedUpdate.Succeeded);
            Assert.Equal("VALIDATION_FAILED", refusedUpdate.Error!.Code);
        }
        finally
        {
            await using var cleanup = fixture.CreateContext();
            await cleanup.PlatformCapabilities.Where(x => x.TenantId == tenant.Id).ExecuteDeleteAsync();
            await cleanup.IntegrationJobs.Where(x => x.TenantId == tenant.Id).ExecuteDeleteAsync();
            await cleanup.PlatformConnections.Where(x => x.TenantId == tenant.Id).ExecuteDeleteAsync();
            await cleanup.IntegrationOutboxEvents.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await cleanup.AuditLogs.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await cleanup.Tenants.Where(x => x.Id == tenant.Id).ExecuteDeleteAsync();
        }
    }
}
