using System.Text.Json;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// The settings window shows a Faturalama switch on every marketplace connection. Hepsiburada's used to be reported as
// off (and refused when turned on) while the invoicing flow treated it as on; the switch now says what the flow does.
public sealed class ConnectionInvoiceSwitchTests
{
    [Fact]
    public void TurningStoredWritesOffKeepsTheInvoiceSwitchAndTheOtherSettings()
    {
        var disabled = ScheduledJobProducer.DisableStoredWrites("""{"ExternalWritesEnabled":true,"InvoiceCreationEnabled":false,"N11ShipmentTemplate":"Aras","N11PreparingDay":1}""");

        using var document = JsonDocument.Parse(disabled);
        Assert.False(document.RootElement.GetProperty("ExternalWritesEnabled").GetBoolean());
        Assert.False(document.RootElement.GetProperty("InvoiceCreationEnabled").GetBoolean());
        Assert.Equal("Aras", document.RootElement.GetProperty("N11ShipmentTemplate").GetString());
        Assert.Equal(1, document.RootElement.GetProperty("N11PreparingDay").GetInt32());
        Assert.False(MarketplaceInvoiceCreationPolicy.IsEnabled("N11", disabled));
    }

    [Fact]
    public void TurningStoredWritesOffKeepsTheTrendyolUserAgent()
    {
        var disabled = ScheduledJobProducer.DisableStoredWrites("""{"UserAgentIdentity":"1 - Firma","ExternalWritesEnabled":true,"InvoiceCreationEnabled":true}""");

        using var document = JsonDocument.Parse(disabled);
        Assert.Equal("1 - Firma", document.RootElement.GetProperty("UserAgentIdentity").GetString());
        Assert.False(document.RootElement.GetProperty("ExternalWritesEnabled").GetBoolean());
        Assert.True(document.RootElement.GetProperty("InvoiceCreationEnabled").GetBoolean());
    }
}

[Collection("postgres-integration")]
public sealed class PostgreSqlConnectionInvoiceSwitchTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    [PostgreSqlFact]
    public async Task HepsiburadaInvoiceSwitchReadsAsTheFlowSeesItAndTurnsBothWays_ShopifyToo()
    {
        var tenant = new Tenant
        {
            Id = Guid.CreateVersion7(),
            Code = $"invoice-switch-{Guid.NewGuid():N}",
            DisplayName = "invoice-switch",
            Status = RecordStatus.Active,
            Timezone = "Europe/Istanbul",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Version = 1
        };
        // A Hepsiburada connection saved before the switch existed: no InvoiceCreationEnabled in its settings.
        var hepsiburada = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = "HEPSIBURADA", Environment = "PRODUCTION", DisplayName = "Hepsiburada", ExternalStoreId = $"hb-{Guid.NewGuid():N}", Status = "ACTIVE", ApiVersion = "V1", SettingsJson = """{"ExternalWritesEnabled":false}""", Version = 1 };
        var shopify = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = "SHOPIFY", Environment = "PRODUCTION", DisplayName = "Shopify", ExternalStoreId = $"shop{Guid.NewGuid():N}"[..20], Status = "ACTIVE", ApiVersion = "2026-10", SettingsJson = """{"ExternalWritesEnabled":false,"InvoiceCreationEnabled":false}""", Version = 1 };
        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.Add(tenant);
            seed.PlatformConnections.AddRange(hepsiburada, shopify);
            await seed.SaveChangesAsync();
        }

        try
        {
            await using var db = fixture.CreateContext();
            var dataProtection = new EphemeralDataProtectionProvider();
            var service = new MarketplaceConnectionService(db, new CursorCodec(dataProtection, fixture.TimeProvider), dataProtection, fixture.TokenHasher, fixture.TimeProvider, new ConfigurationBuilder().Build());

            var legacy = await service.GetAsync(tenant.Id, hepsiburada.Id, CancellationToken.None);
            Assert.True(legacy.Succeeded, legacy.Error?.Message);
            Assert.True(legacy.Value!.InvoiceCreationEnabled);

            var off = await service.UpdateAsync(tenant.Id, hepsiburada.Id, legacy.Value.Version, new UpdateConnectionCommand("Hepsiburada", null, InvoiceCreationEnabled: false), CancellationToken.None);
            Assert.True(off.Succeeded, off.Error?.Message);
            Assert.False(off.Value!.InvoiceCreationEnabled);
            var stored = await db.PlatformConnections.AsNoTracking().Where(x => x.Id == hepsiburada.Id).Select(x => x.SettingsJson).SingleAsync();
            Assert.False(MarketplaceInvoiceCreationPolicy.IsEnabled("HEPSIBURADA", stored));

            // An unrelated save (the store name) keeps the switch where the seller left it.
            var renamed = await service.UpdateAsync(tenant.Id, hepsiburada.Id, off.Value.Version, new UpdateConnectionCommand("Hepsiburada Mağaza", null), CancellationToken.None);
            Assert.True(renamed.Succeeded, renamed.Error?.Message);
            Assert.False(renamed.Value!.InvoiceCreationEnabled);

            var on = await service.UpdateAsync(tenant.Id, hepsiburada.Id, renamed.Value.Version, new UpdateConnectionCommand("Hepsiburada Mağaza", null, InvoiceCreationEnabled: true), CancellationToken.None);
            Assert.True(on.Succeeded, on.Error?.Message);
            Assert.True(on.Value!.InvoiceCreationEnabled);

            var shopifyOn = await service.UpdateAsync(tenant.Id, shopify.Id, shopify.Version, new UpdateConnectionCommand("Shopify", null, InvoiceCreationEnabled: true), CancellationToken.None);
            Assert.True(shopifyOn.Succeeded, shopifyOn.Error?.Message);
            Assert.True(shopifyOn.Value!.InvoiceCreationEnabled);
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
