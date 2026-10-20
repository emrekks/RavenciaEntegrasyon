using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Regression tests for the invoicing findings of the 19 Oct 2026 audit that need a real database.
[Collection("postgres-integration")]
public sealed class PostgreSqlInvoicingAuditTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    [PostgreSqlFact]
    public async Task CreateDraft_RefusesEInvoiceRecipientFromFlatTrendyolSnapshotAndExportOrders()
    {
        var seed = NewSeed("audit-draft-gates");
        seed.Order.CustomerSnapshotJson = "{\"commercial\":true}";
        seed.Order.InvoiceAddressSnapshotJson = "{\"company\":\"Örnek A.Ş.\",\"taxNumber\":\"1234567890\",\"eInvoiceAvailable\":true}";
        var exportOrder = NewOrder(seed.Tenant.Id, seed.Marketplace.Id, "audit-export-order");
        exportOrder.Currency = "EUR";
        var exportPackage = NewPackage(seed.Tenant.Id, seed.Marketplace.Id, exportOrder.Id, "audit-export-package");
        await SeedAsync(seed, db => { db.Orders.Add(exportOrder); db.ShipmentPackages.Add(exportPackage); });

        try
        {
            await using var db = fixture.CreateContext();
            var service = NewBillingService(db);

            var eInvoice = await service.CreateDraftAsync(seed.Tenant.Id,
                new CreateInvoiceCommand(seed.Order.Id, seed.Package.Id, seed.Provider.Id, null, true), "audit-einvoice", CancellationToken.None);
            var export = await service.CreateDraftAsync(seed.Tenant.Id,
                new CreateInvoiceCommand(exportOrder.Id, exportPackage.Id, seed.Provider.Id, null, true), "audit-export", CancellationToken.None);

            Assert.Equal(InvoiceIssuePolicy.EInvoiceRecipientCode, eInvoice.Error?.Code);
            Assert.Equal(422, eInvoice.Error?.Status);
            Assert.Equal(InvoiceIssuePolicy.ExportFlowRequiredCode, export.Error?.Code);
            Assert.Equal(422, export.Error?.Status);
            await using var verification = fixture.CreateContext();
            Assert.False(await verification.Invoices.AnyAsync(invoice => invoice.TenantId == seed.Tenant.Id));
        }
        finally
        {
            await DeleteTenantAsync(seed.Tenant.Id);
        }
    }

    private InvoicingBillingService NewBillingService(AppDbContext db)
    {
        var dataProtection = new EphemeralDataProtectionProvider();
        return new InvoicingBillingService(db, new CursorCodec(dataProtection, fixture.TimeProvider), dataProtection, null!,
            new ConfigurationBuilder().Build(), fixture.TimeProvider);
    }

    private sealed record Seed(Tenant Tenant, PlatformConnection Marketplace, PlatformConnection Provider, Order Order, ShipmentPackage Package);

    private Seed NewSeed(string code)
    {
        var tenant = new Tenant
        {
            Id = Guid.CreateVersion7(),
            Code = $"{code}-{Guid.NewGuid():N}",
            DisplayName = code,
            Status = RecordStatus.Active,
            Timezone = "Europe/Istanbul",
            CreatedAt = fixture.Now,
            UpdatedAt = fixture.Now,
            Version = 1
        };
        var marketplace = NewConnection(tenant.Id, "TRENDYOL", "seller-audit");
        var provider = NewConnection(tenant.Id, "TRENDYOL_EFATURAM", "efaturam-audit");
        var order = NewOrder(tenant.Id, marketplace.Id, $"{code}-order");
        return new(tenant, marketplace, provider, order, NewPackage(tenant.Id, marketplace.Id, order.Id, $"{code}-package"));
    }

    private static PlatformConnection NewConnection(Guid tenantId, string platformCode, string storeId) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        PublicId = Guid.CreateVersion7(),
        PlatformCode = platformCode,
        Environment = "PRODUCTION",
        DisplayName = $"{platformCode} denetim testi",
        ExternalStoreId = storeId,
        Status = "ACTIVE",
        ApiVersion = platformCode == "TRENDYOL_EFATURAM" ? "1.0.0" : "V1"
    };

    private Order NewOrder(Guid tenantId, Guid connectionId, string externalId) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        ConnectionId = connectionId,
        ExternalOrderId = externalId,
        OrderNumber = externalId,
        Currency = "TRY",
        GrossAmount = 120m,
        NetAmount = 120m,
        OrderedAt = fixture.Now.AddDays(-3),
        LastRemoteModifiedAt = fixture.Now.AddDays(-3),
        CustomerSnapshotJson = "{\"customerFirstName\":\"Test\",\"customerLastName\":\"Müşteri\"}",
        ShipmentAddressSnapshotJson = "{}",
        InvoiceAddressSnapshotJson = "{}",
        DerivedStatus = "DELIVERED",
        CreatedAt = fixture.Now,
        UpdatedAt = fixture.Now,
        Version = 1
    };

    private ShipmentPackage NewPackage(Guid tenantId, Guid connectionId, Guid orderId, string externalId) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        ConnectionId = connectionId,
        OrderId = orderId,
        ExternalPackageId = externalId,
        GrossAmount = 120m,
        NetAmount = 120m,
        CargoProviderExternalId = "YKMP",
        Status = ShipmentPackageStatus.Delivered,
        RawStatus = "Delivered",
        StatusOccurredAt = fixture.Now.AddDays(-1),
        MarketplaceInvoiceStatus = MarketplaceInvoiceStatus.NotInvoiced,
        CreatedAt = fixture.Now,
        UpdatedAt = fixture.Now,
        Version = 1
    };

    private async Task SeedAsync(Seed seed, Action<AppDbContext>? extra = null)
    {
        await using var db = fixture.CreateContext();
        db.Tenants.Add(seed.Tenant);
        db.PlatformConnections.AddRange(seed.Marketplace, seed.Provider);
        db.Orders.Add(seed.Order);
        db.ShipmentPackages.Add(seed.Package);
        extra?.Invoke(db);
        await db.SaveChangesAsync();
    }

    private async Task DeleteTenantAsync(Guid tenantId)
    {
        await using var db = fixture.CreateContext();
        await db.IntegrationJobs.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.AuditLogs.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.OperationalIssues.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.MarketplaceDeliveries.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.MarketplaceDeliveryStates.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.InvoiceDocuments.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.InvoiceSubmissionAttempts.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.InvoicePartySnapshots.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.InvoiceLines.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.Invoices.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.InvoicePolicies.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.LegalEntityProfiles.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.ReturnClaims.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.ShipmentPackages.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.OrderLines.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.Orders.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.PlatformConnections.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.Tenants.Where(row => row.Id == tenantId).ExecuteDeleteAsync();
    }
}
