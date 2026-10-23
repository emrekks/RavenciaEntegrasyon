using System.Text.Json;
using MarketplaceHub.Api.Invoicing;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Audit 24 Oct 2026: a hand-set invoice status reads the same on every screen (S2), a package marked "Faturası
// yüklendi" gets no second fiscal invoice and no link (S3), and a manual delivery retried after a failed job is
// queued again instead of answering with the finished job (W2).
[Collection("postgres-integration")]
public sealed class PostgreSqlInvoicingManualStatusTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    // S2: the invoice page, the orders list and the order detail give one label for the same package.
    [PostgreSqlFact]
    public async Task S2_InvoicePageAndOrdersShowTheSameHandSetInvoiceLabel()
    {
        foreach (var (platformCode, manualStatus, expected) in new[]
        {
            ("N11", "UPLOADED", "FATURA_KONTROLDE"),
            ("SHOPIFY", "UPLOADED", "FATURA_KONTROLDE"),
            ("PAZARAMA", "UPLOADED", "FATURA_KONTROLDE"),
            ("PTTAVM", "PENDING", "FATURA_BEKLIYOR")
        })
            await AssertSameLabelAsync(platformCode, manualStatus, expected);
    }

    private async Task AssertSameLabelAsync(string platformCode, string manualStatus, string expected)
    {
        var seed = NewSeed("s2-label", platformCode, manualStatus);
        await SeedAsync(seed);
        try
        {
            await using var db = fixture.CreateContext();
            var workspace = await NewBillingService(db).WorkspacePageAsync(seed.Tenant.Id, new InvoiceWorkspacePageQuery(PageNumber: 1, PageSize: 20, Tab: "ALL"), CancellationToken.None);
            var sales = NewSalesService(db);
            var list = await sales.OrdersAsync(seed.Tenant.Id, 50, 1, null, new OrderListQuery(), CancellationToken.None);
            var detail = await sales.OrderAsync(seed.Tenant.Id, seed.Order.Id, CancellationToken.None);

            var pageLabel = Assert.Single(workspace.Items, item => item.PackageId == seed.Package.Id).InvoiceStatus;
            Assert.Equal(expected, pageLabel);
            Assert.Equal(pageLabel, Assert.Single(list.Items, item => item.Id == seed.Order.Id).InvoiceStatus);
            Assert.True(detail.Succeeded, detail.Error?.Message);
            Assert.Equal(pageLabel, detail.Value!.InvoiceStatus);

            // The orders invoice filter lists the order under the same label, and not under the other one.
            var other = expected == "FATURA_KONTROLDE" ? "FATURA_BEKLIYOR" : "FATURA_KONTROLDE";
            var filtered = await sales.OrdersAsync(seed.Tenant.Id, 50, 1, null, new OrderListQuery(Invoice: expected), CancellationToken.None);
            var otherFiltered = await sales.OrdersAsync(seed.Tenant.Id, 50, 1, null, new OrderListQuery(Invoice: other), CancellationToken.None);
            Assert.Contains(filtered.Items, item => item.Id == seed.Order.Id);
            Assert.DoesNotContain(otherFiltered.Items, item => item.Id == seed.Order.Id);
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
            new ConfigurationBuilder().Build(), fixture.TimeProvider, null!);
    }

    private MarketplaceSalesService NewSalesService(AppDbContext db) =>
        new(db, new CursorCodec(new EphemeralDataProtectionProvider(), fixture.TimeProvider), new ConfigurationBuilder().Build(), null!, null!, null!, null!, fixture.TimeProvider);

    private InvoicingJobProcessor NewProcessor(AppDbContext db, IInvoiceMarketplacePort marketplace) =>
        new(db, null!, marketplace, null!, fixture.TimeProvider, new ConfigurationBuilder().Build(), new EphemeralDataProtectionProvider());

    private sealed record Seed(Tenant Tenant, PlatformConnection Marketplace, PlatformConnection Provider, Order Order, ShipmentPackage Package, LegalEntityProfile Profile, InvoicePolicy Policy);

    private Seed NewSeed(string code, string platformCode, string? manualInvoiceStatus)
    {
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"{code}-{Guid.NewGuid():N}"[..40], DisplayName = code, Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = fixture.Now, UpdatedAt = fixture.Now, Version = 1 };
        var marketplace = NewConnection(tenant.Id, platformCode, $"{code}-store");
        var provider = NewConnection(tenant.Id, "TRENDYOL_EFATURAM", $"{code}-efaturam");
        var order = new Order
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            ConnectionId = marketplace.Id,
            ExternalOrderId = $"{code}-order",
            OrderNumber = $"{code}-order",
            Currency = "TRY",
            GrossAmount = 120m,
            NetAmount = 120m,
            OrderedAt = fixture.Now.AddDays(-3),
            LastRemoteModifiedAt = fixture.Now.AddDays(-3),
            CustomerSnapshotJson = "{}",
            ShipmentAddressSnapshotJson = "{}",
            InvoiceAddressSnapshotJson = "{}",
            DerivedStatus = "DELIVERED",
            CreatedAt = fixture.Now,
            UpdatedAt = fixture.Now,
            Version = 1
        };
        var package = new ShipmentPackage
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            ConnectionId = marketplace.Id,
            OrderId = order.Id,
            ExternalPackageId = $"{code}-package",
            GrossAmount = 120m,
            NetAmount = 120m,
            CargoProviderExternalId = "YKMP",
            Status = ShipmentPackageStatus.Delivered,
            RawStatus = "Delivered",
            StatusOccurredAt = fixture.Now.AddDays(-1),
            MarketplaceInvoiceStatus = MarketplaceInvoiceStatus.Unknown,
            ManualInvoiceStatus = manualInvoiceStatus,
            CreatedAt = fixture.Now,
            UpdatedAt = fixture.Now,
            Version = 1
        };
        var profile = new LegalEntityProfile { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Title = "profile", ProtectedTaxId = "protected", MaskedTaxId = "E-Faturam", AddressSnapshotJson = "{}", ContactSnapshotJson = "{}", Status = "ACTIVE", CreatedAt = fixture.Now, UpdatedAt = fixture.Now };
        var policy = new InvoicePolicy { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProviderConnectionId = provider.Id, TriggerState = "MANUAL_CONFIRMED", PackageScope = "SHIPMENT_PACKAGE", DueRule = "IMMEDIATE", RoundingRule = "LINE_HALF_AWAY_FROM_ZERO", AdjustmentRule = "REJECT_OVER_ONE_KURUS", CreatedAt = fixture.Now, UpdatedAt = fixture.Now };
        return new(tenant, marketplace, provider, order, package, profile, policy);
    }

    private static PlatformConnection NewConnection(Guid tenantId, string platformCode, string storeId) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        PublicId = Guid.CreateVersion7(),
        PlatformCode = platformCode,
        Environment = "PRODUCTION",
        DisplayName = $"{platformCode} test",
        ExternalStoreId = storeId,
        Status = "ACTIVE",
        ApiVersion = platformCode == "TRENDYOL_EFATURAM" ? "1.0.0" : "V1"
    };

    private Invoice NewInvoice(Seed seed, InvoiceStatus status) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = seed.Tenant.Id,
        OrderId = seed.Order.Id,
        PackageId = seed.Package.Id,
        OrderFiscalGuarded = true,
        ProviderConnectionId = seed.Provider.Id,
        LegalEntityProfileId = seed.Profile.Id,
        InvoicePolicyId = seed.Policy.Id,
        InvoiceType = "EARSIVFATURA",
        SequencePurpose = "SALE",
        Status = status,
        Currency = "TRY",
        TaxExclusiveTotal = 100m,
        TaxTotal = 20m,
        PayableTotal = 120m,
        Note = InvoiceAmounts.TurkishInvoiceNote(120m),
        IdempotencyKey = $"manual-status:{Guid.NewGuid():N}",
        ExternalReference = status == InvoiceStatus.Submitting ? null : $"remote-{Guid.NewGuid():N}",
        InvoiceNumber = status == InvoiceStatus.Submitting ? null : "GIB2026000000001",
        IssuedAt = fixture.Now.AddDays(-1),
        CreatedAt = fixture.Now.AddDays(-1),
        UpdatedAt = fixture.Now.AddDays(-1),
        Version = 1
    };

    private async Task SeedAsync(Seed seed, Action<AppDbContext>? extra = null)
    {
        await using var db = fixture.CreateContext();
        db.Tenants.Add(seed.Tenant);
        db.PlatformConnections.AddRange(seed.Marketplace, seed.Provider);
        db.Orders.Add(seed.Order);
        db.OrderLines.Add(new OrderLine
        {
            Id = Guid.CreateVersion7(),
            TenantId = seed.Tenant.Id,
            OrderId = seed.Order.Id,
            ExternalLineId = $"{seed.Order.ExternalOrderId}-line",
            Sku = "SKU-1",
            Barcode = "BARCODE-1",
            TitleSnapshot = "Ürün",
            OrderedQuantity = 1,
            UnitPrice = 120m,
            VatRate = 20m,
            RawStatus = "Delivered",
            Version = 1
        });
        db.ShipmentPackages.Add(seed.Package);
        db.LegalEntityProfiles.Add(seed.Profile);
        db.InvoicePolicies.Add(seed.Policy);
        extra?.Invoke(db);
        await db.SaveChangesAsync();
    }

    private async Task DeleteTenantAsync(Guid tenantId)
    {
        await using var db = fixture.CreateContext();
        var jobIds = await db.IntegrationJobs.Where(row => row.TenantId == tenantId).Select(row => row.Id).ToListAsync();
        await db.JobAttempts.Where(row => row.TenantId == tenantId || jobIds.Contains(row.JobId)).ExecuteDeleteAsync();
        await db.IntegrationJobs.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.IntegrationOutboxEvents.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.AuditLogs.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.OperationalIssues.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.ManualOrderInvoiceDeliveryStates.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.ManualOrderInvoiceDocuments.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.MarketplaceInvoiceReferenceHistories.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.MarketplaceDeliveries.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.MarketplaceDeliveryStates.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.InvoiceDocuments.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.InvoiceSubmissionAttempts.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.InvoicePartySnapshots.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.InvoiceLines.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.Invoices.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.InvoicePolicies.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.LegalEntityProfiles.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.ShipmentPackages.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.OrderLines.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.Orders.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.PlatformConnections.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.Tenants.Where(row => row.Id == tenantId).ExecuteDeleteAsync();
    }

    // Answers every read as Pazarama now does before a delivery (UNVERIFIED) and refuses any write.
    private sealed class UnverifiedMarketplace : IInvoiceMarketplacePort
    {
        public int Calls { get; private set; }

        public Task<AdapterResult<InvoiceDeliveryResult>> DeliverAsync(AdapterContext context, InvoiceDeliveryCommand command, CancellationToken cancellationToken)
        {
            Calls++;
            throw new InvalidOperationException("A delivery must not be sent.");
        }

        public Task<AdapterResult<InvoiceDeliveryStatus>> QueryDeliveryAsync(AdapterContext context, ExternalInvoiceDeliveryReference reference, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(AdapterResult<InvoiceDeliveryStatus>.Success(new(reference.ExternalReference, MarketplaceInvoiceStatePolicy.Unverified, true)));
        }
    }
}
