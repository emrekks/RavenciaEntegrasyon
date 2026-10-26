using MarketplaceHub.Api.Marketplace;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Files;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Data resets keep issued invoices (A4), hand the files of removed rows to the purge sweep (A12), and the
// webhook gate needs the route token before a body is read (A8).
[Collection("postgres-integration")]
public sealed class PostgreSqlDataMaintenanceSecurityTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    [PostgreSqlFact]
    public async Task OrderReset_KeepsAnIssuedInvoiceWithItsOrder()
    {
        var seed = NewSeed("dm-iss");
        try
        {
            await SeedAsync(seed, db => db.Invoices.Add(NewInvoice(seed, InvoiceStatus.Completed, issued: true)));

            await using var db = fixture.CreateContext();
            var result = await new OperationalDataMaintenanceService(db, fixture.TimeProvider, new NoDashboard())
                .ResetAsync(seed.Tenant.Id, Guid.CreateVersion7(), new ResetOperationalDataCommand(["ORDERS"], "Verileri sil"), "corr", CancellationToken.None);

            // 26 Oct 2026: the reset used to be refused; it now goes ahead and keeps the fiscal record with its order.
            Assert.True(result.Succeeded, result.Error?.Message);
            Assert.Equal(1, result.Value!.KeptIssuedInvoices);
            await using var verification = fixture.CreateContext();
            Assert.Equal(1, await verification.Orders.CountAsync(x => x.TenantId == seed.Tenant.Id));
            Assert.Equal(1, await verification.Invoices.CountAsync(x => x.TenantId == seed.Tenant.Id));
        }
        finally
        {
            await DeleteTenantAsync(seed.Tenant.Id);
        }
    }

    // A connection's order reset goes ahead around its issued invoices (26 Oct 2026: 10 issued Hepsiburada invoices
    // refused the whole reset): the issued invoice and its order stay, another order and its draft invoice go.
    [PostgreSqlFact]
    public async Task ConnectionReset_KeepsIssuedInvoicesWithTheirOrdersAndRemovesTheRest()
    {
        var seed = NewSeed("dm-keep");
        var other = new Order
        {
            Id = Guid.CreateVersion7(),
            TenantId = seed.Tenant.Id,
            ConnectionId = seed.Marketplace.Id,
            ExternalOrderId = "dm-keep-other",
            OrderNumber = "dm-keep-other",
            Currency = "TRY",
            GrossAmount = 50m,
            NetAmount = 50m,
            OrderedAt = fixture.Now.AddDays(-2),
            LastRemoteModifiedAt = fixture.Now.AddDays(-2),
            CustomerSnapshotJson = "{}",
            ShipmentAddressSnapshotJson = "{}",
            InvoiceAddressSnapshotJson = "{}",
            DerivedStatus = "DELIVERED",
            CreatedAt = fixture.Now,
            UpdatedAt = fixture.Now,
            Version = 1
        };
        var issued = NewInvoice(seed, InvoiceStatus.Completed, issued: true);
        var draft = NewInvoice(seed, InvoiceStatus.Draft, issued: false);
        draft.OrderId = other.Id;
        try
        {
            await SeedAsync(seed, db => { db.Orders.Add(other); db.Invoices.AddRange(issued, draft); });

            await using var db = fixture.CreateContext();
            var result = await new OperationalDataMaintenanceService(db, fixture.TimeProvider, new NoDashboard())
                .ResetConnectionDataAsync(seed.Tenant.Id, Guid.CreateVersion7(), seed.Marketplace.Id, seed.Marketplace.Version, new ResetOperationalDataCommand(["ORDERS", "RETURNS", "INVOICES"], seed.Marketplace.DisplayName), "corr", CancellationToken.None);

            Assert.True(result.Succeeded, result.Error?.Message);
            Assert.Equal(1, result.Value!.KeptIssuedInvoices);
            await using var verification = fixture.CreateContext();
            Assert.Equal([seed.Order.Id], await verification.Orders.Where(x => x.TenantId == seed.Tenant.Id).Select(x => x.Id).ToListAsync());
            Assert.Equal([issued.Id], await verification.Invoices.Where(x => x.TenantId == seed.Tenant.Id).Select(x => x.Id).ToListAsync());
        }
        finally
        {
            await DeleteTenantAsync(seed.Tenant.Id);
        }
    }

    // A live e-invoice provider with an issued invoice is kept; a STAGE one's invoices are tests and go with it.
    [PostgreSqlFact]
    public Task ProviderDelete_KeepsALiveProvidersIssuedInvoices() => ProviderDeleteAsync("PRODUCTION", deleted: false);

    [PostgreSqlFact]
    public Task ProviderDelete_TakesATestEnvironmentsInvoicesWithIt() => ProviderDeleteAsync("STAGE", deleted: true);

    private async Task ProviderDeleteAsync(string environment, bool deleted)
    {
        var seed = NewSeed($"dm-{environment[..3].ToLowerInvariant()}");
        seed.Provider.Environment = environment;
        try
        {
            await SeedAsync(seed, db =>
            {
                var invoice = NewInvoice(seed, InvoiceStatus.Completed, issued: true);
                db.Invoices.Add(invoice);
                // 25 Oct 2026: the live STAGE invoices had a delivery state to the marketplace, which a delete must take too.
                db.MarketplaceDeliveryStates.Add(new MarketplaceDeliveryState
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = seed.Tenant.Id,
                    InvoiceId = invoice.Id,
                    ConnectionId = seed.Marketplace.Id,
                    PackageId = seed.Package.Id,
                    AttemptNumber = 1,
                    ExternalIdempotencyKey = $"delivery-{environment}-{Guid.NewGuid():N}",
                    RequestHash = "hash",
                    DeliveryType = "LINK",
                    Status = "DELIVERED",
                    CreatedAt = fixture.Now,
                    UpdatedAt = fixture.Now
                });
            });

            await using var db = fixture.CreateContext();
            var result = await new OperationalDataMaintenanceService(db, fixture.TimeProvider, new NoDashboard())
                .DeleteConnectionAsync(seed.Tenant.Id, Guid.CreateVersion7(), seed.Provider.Id, seed.Provider.Version, new DeleteConnectionCommand(seed.Provider.DisplayName), "corr", CancellationToken.None);

            Assert.Equal(deleted, result.Succeeded);
            if (!deleted) Assert.Equal("ISSUED_INVOICES_KEPT", result.Error!.Code);
            await using var verification = fixture.CreateContext();
            Assert.Equal(deleted ? 0 : 1, await verification.Invoices.CountAsync(x => x.TenantId == seed.Tenant.Id));
            Assert.Equal(1, await verification.Orders.CountAsync(x => x.TenantId == seed.Tenant.Id));
            Assert.Equal(deleted ? "DELETED" : "ACTIVE", (await verification.PlatformConnections.SingleAsync(x => x.Id == seed.Provider.Id)).Status);
        }
        finally
        {
            await DeleteTenantAsync(seed.Tenant.Id);
        }
    }

    [PostgreSqlFact]
    public async Task OrderReset_MarksTheFilesOfRemovedRowsForPurge_AndTheSweepDeletesThem()
    {
        var seed = NewSeed("dm-file");
        var label = NewFileAsset(seed.Tenant.Id, $"{seed.Tenant.Id:N}/label.pdf");
        var kept = NewFileAsset(seed.Tenant.Id, $"{seed.Tenant.Id:N}/kept.pdf");
        try
        {
            await SeedAsync(seed, db =>
            {
                db.Invoices.Add(NewInvoice(seed, InvoiceStatus.Draft, issued: false));
                db.FileAssets.AddRange(label, kept);
                db.ShipmentDocuments.Add(new ShipmentDocument { Id = Guid.CreateVersion7(), TenantId = seed.Tenant.Id, ConnectionId = seed.Marketplace.Id, PackageId = seed.Package.Id, FileAssetId = label.Id, DocumentKind = "LABEL", Format = "PDF", Source = "TEST", Checksum = new string('B', 64), CreatedAt = fixture.Now });
            });

            await using (var db = fixture.CreateContext())
            {
                var result = await new OperationalDataMaintenanceService(db, fixture.TimeProvider, new NoDashboard())
                    .ResetAsync(seed.Tenant.Id, Guid.CreateVersion7(), new ResetOperationalDataCommand(["ORDERS"], "Verileri sil"), "corr", CancellationToken.None);
                Assert.True(result.Succeeded, result.Error?.Message);
            }

            await using (var verification = fixture.CreateContext())
            {
                Assert.Equal(OperationalDataMaintenanceService.PurgePendingStatus, (await verification.FileAssets.SingleAsync(x => x.Id == label.Id)).Status);
                Assert.Equal("ACTIVE", (await verification.FileAssets.SingleAsync(x => x.Id == kept.Id)).Status);
            }

            var storage = new RecordingStorage();
            await using (var db = fixture.CreateContext())
                Assert.True(await PurgedFileRetention.SweepAsync(db, storage, DateTimeOffset.UtcNow.AddHours(1), CancellationToken.None) >= 1);

            Assert.Contains((seed.Tenant.Id, label.RelativePath), storage.Deleted);
            Assert.DoesNotContain((seed.Tenant.Id, kept.RelativePath), storage.Deleted);
            await using var after = fixture.CreateContext();
            Assert.Equal(PurgedFileRetention.DeletedStatus, (await after.FileAssets.SingleAsync(x => x.Id == label.Id)).Status);
        }
        finally
        {
            await DeleteTenantAsync(seed.Tenant.Id);
        }
    }

    [PostgreSqlFact]
    public async Task WebhookGate_NeedsTheRouteTokenOfAnActiveSubscription()
    {
        var seed = NewSeed("dm-hook");
        try
        {
            await SeedAsync(seed, db => db.WebhookSubscriptions.Add(new WebhookSubscription
            {
                Id = Guid.CreateVersion7(),
                TenantId = seed.Tenant.Id,
                ConnectionId = seed.Marketplace.Id,
                RouteTokenHash = fixture.TokenHasher.Hash("route-token-1"),
                AuthenticationType = "API_KEY",
                ProtectedVerifierSecret = "test-secret",
                Status = "ACTIVE",
                Version = 1
            }));

            await using var db = fixture.CreateContext();
            using var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 100 });
            var gate = new WebhookRouteGate(db, cache, fixture.TokenHasher);

            Assert.True(await gate.IsKnownAsync(seed.Marketplace.PublicId, "route-token-1", CancellationToken.None));
            Assert.False(await gate.IsKnownAsync(seed.Marketplace.PublicId, "guessed-token", CancellationToken.None));
            Assert.False(await gate.IsKnownAsync(Guid.CreateVersion7(), "route-token-1", CancellationToken.None));
        }
        finally
        {
            await DeleteTenantAsync(seed.Tenant.Id);
        }
    }

    private sealed record Seed(Tenant Tenant, PlatformConnection Marketplace, PlatformConnection Provider, Order Order, ShipmentPackage Package, LegalEntityProfile Profile, InvoicePolicy Policy);

    private Seed NewSeed(string code)
    {
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"{code}-{Guid.NewGuid():N}"[..20], DisplayName = code, Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = fixture.Now, UpdatedAt = fixture.Now, Version = 1 };
        var marketplace = NewConnection(tenant.Id, "TRENDYOL", $"seller-{code}");
        var provider = NewConnection(tenant.Id, "TRENDYOL_EFATURAM", $"efaturam-{code}");
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
            MarketplaceInvoiceStatus = MarketplaceInvoiceStatus.NotInvoiced,
            CreatedAt = fixture.Now,
            UpdatedAt = fixture.Now,
            Version = 1
        };
        var profile = new LegalEntityProfile { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Title = "dm-profile", ProtectedTaxId = "protected", MaskedTaxId = "E-Faturam", AddressSnapshotJson = "{}", ContactSnapshotJson = "{}", Status = "ACTIVE", CreatedAt = fixture.Now, UpdatedAt = fixture.Now };
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
        DisplayName = $"{platformCode} bakım testi",
        ExternalStoreId = storeId,
        Status = "ACTIVE",
        ApiVersion = platformCode == "TRENDYOL_EFATURAM" ? "1.0.0" : "V1"
    };

    private Invoice NewInvoice(Seed seed, InvoiceStatus status, bool issued) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = seed.Tenant.Id,
        OrderId = seed.Order.Id,
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
        Note = "test",
        IdempotencyKey = $"dm-invoice:{Guid.NewGuid():N}",
        InvoiceNumber = issued ? "GIB2026000000001" : null,
        IssuedAt = issued ? fixture.Now.AddDays(-1) : null,
        CreatedAt = fixture.Now.AddDays(-1),
        UpdatedAt = fixture.Now.AddDays(-1)
    };

    private FileAsset NewFileAsset(Guid tenantId, string path) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        Classification = "SHIPMENT_DOCUMENT",
        RelativePath = path,
        MimeType = "application/pdf",
        SizeBytes = 10,
        Sha256 = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
        Status = "ACTIVE",
        CreatedAt = fixture.Now
    };

    private async Task SeedAsync(Seed seed, Action<AppDbContext> extra)
    {
        await using var db = fixture.CreateContext();
        db.Tenants.Add(seed.Tenant);
        db.PlatformConnections.AddRange(seed.Marketplace, seed.Provider);
        db.Orders.Add(seed.Order);
        db.ShipmentPackages.Add(seed.Package);
        db.LegalEntityProfiles.Add(seed.Profile);
        db.InvoicePolicies.Add(seed.Policy);
        extra(db);
        await db.SaveChangesAsync();
    }

    private async Task DeleteTenantAsync(Guid tenantId)
    {
        await using var db = fixture.CreateContext();
        await db.IntegrationOutboxEvents.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.JobAttempts.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.IntegrationJobs.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.AuditLogs.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.DashboardSnapshots.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.WebhookSubscriptions.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.ShipmentDocuments.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.InvoiceDocuments.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.InvoiceLines.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.MarketplaceDeliveryStates.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.Invoices.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.FileAssets.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.InvoicePolicies.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.LegalEntityProfiles.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.ShipmentPackages.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.Orders.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.PlatformConnections.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.Tenants.Where(row => row.Id == tenantId).ExecuteDeleteAsync();
    }

    private sealed class NoDashboard : IDashboardReadService
    {
        public Task<DashboardBootstrapView> BootstrapAsync(Guid tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ServiceResult<DashboardPerformanceView>> PerformanceAsync(Guid tenantId, DateOnly? from, DateOnly? to, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RebuildTenantAsync(Guid tenantId, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class RecordingStorage : IPrivateFileStorage
    {
        public List<(Guid TenantId, string Path)> Deleted { get; } = [];
        public Task<string> SaveAsync(Guid tenantId, string relativeName, string mimeType, Stream content, long maximumBytes, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Stream> OpenReadAsync(Guid tenantId, string storedPath, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(Guid tenantId, string storedPath, CancellationToken cancellationToken)
        {
            Deleted.Add((tenantId, storedPath));
            return Task.FromResult(true);
        }
    }
}
