using System.Text.Json;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// A marketplace delivery of an order-level invoice targets the order's current packages (CurrentShipmentPackages, the
// set the issuing step checks): a package Trendyol replaced by a resend is never sent the invoice link again, and the
// invoice does not wait for its confirmation.
[Collection("postgres-integration")]
public sealed class InvoiceDeliveryCurrentPackageTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    private static readonly DateTimeOffset IssuedAt = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);
    private const string DocumentUrl = "https://documents.example.test/resend/invoice.pdf";

    [PostgreSqlFact]
    public async Task OrderWideDeliveryLeavesTheResentOriginalOut()
    {
        var (seed, resent) = NewResendSeed("resend-a");
        await SeedAsync(seed, db => db.ShipmentPackages.Add(resent));
        try
        {
            var marketplace = new ScriptedMarketplace();
            await using (var db = fixture.CreateContext())
            {
                await NewProcessor(db, marketplace).ProcessAsync(seed.Tenant.Id, seed.Marketplace.Id, InvoicingJobTypes.MarketplaceDelivery,
                    JsonSerializer.Serialize(new { invoiceId = seed.Invoice.Id }), "resend-a", CancellationToken.None);
            }

            Assert.Equal([resent.ExternalPackageId], marketplace.Deliveries.Select(d => d.ExternalPackageId).ToArray());
        }
        finally
        {
            await DeleteTenantAsync(seed.Tenant.Id);
        }
    }

    [PostgreSqlFact]
    public async Task OrderWideDeliverySendsOnlyToTheResentPackageAndCompletes()
    {
        var (seed, resent) = NewResendSeed("resend-b");
        await SeedAsync(seed, db => db.ShipmentPackages.Add(resent));
        try
        {
            var marketplace = new ScriptedMarketplace { Succeed = true, InvoiceNumber = seed.Invoice.InvoiceNumber };
            await using (var db = fixture.CreateContext())
            {
                await NewProcessor(db, marketplace).ProcessAsync(seed.Tenant.Id, seed.Marketplace.Id, InvoicingJobTypes.MarketplaceDelivery,
                    JsonSerializer.Serialize(new { invoiceId = seed.Invoice.Id }), "resend-b", CancellationToken.None);
            }

            Assert.Equal([resent.ExternalPackageId], marketplace.Deliveries.Select(d => d.ExternalPackageId).ToArray());
            await using var verification = fixture.CreateContext();
            Assert.Equal(InvoiceStatus.Completed, (await verification.Invoices.AsNoTracking().SingleAsync(x => x.Id == seed.Invoice.Id)).Status);
        }
        finally
        {
            await DeleteTenantAsync(seed.Tenant.Id);
        }
    }

    // The delivery outcome counts the same targets: a confirmed link on the resent package completes the invoice
    // without waiting for the replaced original.
    [PostgreSqlFact]
    public async Task PackageDeliveryOfTheResentPackageCompletesTheInvoice()
    {
        var (seed, resent) = NewResendSeed("resend-c");
        await SeedAsync(seed, db => db.ShipmentPackages.Add(resent));
        try
        {
            var marketplace = new ScriptedMarketplace { Succeed = true, InvoiceNumber = seed.Invoice.InvoiceNumber };
            await using (var db = fixture.CreateContext())
            {
                await NewProcessor(db, marketplace).ProcessAsync(seed.Tenant.Id, seed.Marketplace.Id, InvoicingJobTypes.MarketplaceDelivery,
                    JsonSerializer.Serialize(new { invoiceId = seed.Invoice.Id, packageId = resent.Id }), "resend-c", CancellationToken.None);
            }

            Assert.Equal([resent.ExternalPackageId], marketplace.Deliveries.Select(d => d.ExternalPackageId).ToArray());
            await using var verification = fixture.CreateContext();
            Assert.Equal(InvoiceStatus.Completed, (await verification.Invoices.AsNoTracking().SingleAsync(x => x.Id == seed.Invoice.Id)).Status);
        }
        finally
        {
            await DeleteTenantAsync(seed.Tenant.Id);
        }
    }

    // The shared target set (also the delivery queued after the document, the never-delivered recovery and the owner's
    // resend): the current package for an order-level invoice, the invoice's own package for a package-level one.
    [PostgreSqlFact]
    public async Task DeliveryTargetsAreTheCurrentPackagesOrTheInvoicesOwnPackage()
    {
        var (seed, resent) = NewResendSeed("resend-d");
        await SeedAsync(seed, db => db.ShipmentPackages.Add(resent));
        try
        {
            await using var db = fixture.CreateContext();
            var orderLevel = await InvoicingJobProcessor.DeliveryTargetPackages(db, seed.Tenant.Id, seed.Invoice).Select(p => p.ExternalPackageId).ToListAsync();
            Assert.Equal([resent.ExternalPackageId], orderLevel);
            var packageLevel = new Invoice
            {
                Id = seed.Invoice.Id,
                TenantId = seed.Tenant.Id,
                OrderId = seed.Order.Id,
                PackageId = seed.Package.Id,
                InvoiceType = seed.Invoice.InvoiceType,
                SequencePurpose = seed.Invoice.SequencePurpose,
                Currency = seed.Invoice.Currency,
                Note = seed.Invoice.Note,
                IdempotencyKey = seed.Invoice.IdempotencyKey
            };
            var own = await InvoicingJobProcessor.DeliveryTargetPackages(db, seed.Tenant.Id, packageLevel).Select(p => p.ExternalPackageId).ToListAsync();
            Assert.Equal([seed.Package.ExternalPackageId], own);
        }
        finally
        {
            await DeleteTenantAsync(seed.Tenant.Id);
        }
    }

    private (Seed Seed, ShipmentPackage Resent) NewResendSeed(string code)
    {
        var seed = NewSeed(code);
        // The original parcel could not be delivered (not Cancelled); Trendyol resent it as a new package naming it as origin.
        seed.Package.Status = ShipmentPackageStatus.Undelivered;
        seed.Package.RawStatus = "UnDelivered";
        var resent = new ShipmentPackage
        {
            // Created after the original (later v7 timestamp), as in reality; the job orders packages by Id.
            Id = Guid.CreateVersion7(DateTimeOffset.UtcNow.AddMinutes(5)),
            TenantId = seed.Tenant.Id,
            ConnectionId = seed.Marketplace.Id,
            OrderId = seed.Order.Id,
            ExternalPackageId = $"{code}-resent",
            OriginExternalPackageId = seed.Package.ExternalPackageId,
            GrossAmount = 120m,
            NetAmount = 120m,
            CargoProviderExternalId = "YKMP",
            Status = ShipmentPackageStatus.Delivered,
            RawStatus = "Delivered",
            StatusOccurredAt = fixture.Now,
            MarketplaceInvoiceStatus = MarketplaceInvoiceStatus.NotInvoiced,
            CreatedAt = fixture.Now,
            UpdatedAt = fixture.Now,
            Version = 1
        };
        return (seed, resent);
    }

    private InvoicingJobProcessor NewProcessor(AppDbContext db, IInvoiceMarketplacePort marketplace) =>
        new(db, null!, marketplace, null!, fixture.TimeProvider, new ConfigurationBuilder().Build(), new EphemeralDataProtectionProvider());

    private sealed record Seed(Tenant Tenant, PlatformConnection Marketplace, PlatformConnection Provider, Order Order, ShipmentPackage Package,
        LegalEntityProfile Profile, InvoicePolicy Policy, Invoice Invoice, FileAsset Asset, InvoiceDocument Document);

    private Seed NewSeed(string code)
    {
        var now = fixture.Now;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"{code}-{Guid.NewGuid():N}{Guid.NewGuid():N}"[..40], DisplayName = code, Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var marketplace = NewConnection(tenant.Id, "TRENDYOL", $"{code}-store");
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
            OrderedAt = now.AddDays(-3),
            LastRemoteModifiedAt = now.AddDays(-3),
            CustomerSnapshotJson = "{}",
            ShipmentAddressSnapshotJson = "{}",
            InvoiceAddressSnapshotJson = "{}",
            DerivedStatus = "DELIVERED",
            CreatedAt = now,
            UpdatedAt = now,
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
            StatusOccurredAt = now.AddDays(-1),
            MarketplaceInvoiceStatus = MarketplaceInvoiceStatus.NotInvoiced,
            CreatedAt = now,
            UpdatedAt = now,
            Version = 1
        };
        var profile = new LegalEntityProfile { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Title = "profile", ProtectedTaxId = "protected", MaskedTaxId = "E-Faturam", AddressSnapshotJson = "{}", ContactSnapshotJson = "{}", Status = "ACTIVE", CreatedAt = now, UpdatedAt = now };
        var policy = new InvoicePolicy { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProviderConnectionId = provider.Id, TriggerState = "MANUAL_CONFIRMED", PackageScope = "SHIPMENT_PACKAGE", DueRule = "IMMEDIATE", RoundingRule = "LINE_HALF_AWAY_FROM_ZERO", AdjustmentRule = "REJECT_OVER_ONE_KURUS", CreatedAt = now, UpdatedAt = now };
        var invoice = new Invoice
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            OrderId = order.Id,
            OrderFiscalGuarded = true,
            ProviderConnectionId = provider.Id,
            LegalEntityProfileId = profile.Id,
            InvoicePolicyId = policy.Id,
            InvoiceType = "EARSIVFATURA",
            SequencePurpose = "SALE",
            Status = InvoiceStatus.MarketplacePending,
            Currency = "TRY",
            TaxExclusiveTotal = 100m,
            TaxTotal = 20m,
            PayableTotal = 120m,
            Note = InvoiceAmounts.TurkishInvoiceNote(120m),
            IdempotencyKey = $"resend:{Guid.NewGuid():N}",
            ExternalReference = $"remote-{Guid.NewGuid():N}",
            InvoiceNumber = "DAP2026000000998",
            IssuedAt = IssuedAt,
            CreatedAt = now.AddDays(-1),
            UpdatedAt = now.AddDays(-1),
            Version = 1
        };
        var asset = new FileAsset { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Classification = "INVOICE_DOCUMENT", RelativePath = "resend/invoice.pdf", MimeType = "application/pdf", SizeBytes = 10, Sha256 = new string('C', 64), Status = "ACTIVE", CreatedAt = now };
        var document = new InvoiceDocument { Id = Guid.CreateVersion7(), TenantId = tenant.Id, InvoiceId = invoice.Id, DocumentType = "PDF", FileAssetId = asset.Id, Sha256 = asset.Sha256, PermanentUrl = DocumentUrl, CreatedAt = now };
        return new(tenant, marketplace, provider, order, package, profile, policy, invoice, asset, document);
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

    private async Task SeedAsync(Seed seed, Action<AppDbContext>? extra = null)
    {
        await using var db = fixture.CreateContext();
        db.Tenants.Add(seed.Tenant);
        db.PlatformConnections.AddRange(seed.Marketplace, seed.Provider);
        db.Orders.Add(seed.Order);
        db.ShipmentPackages.Add(seed.Package);
        db.LegalEntityProfiles.Add(seed.Profile);
        db.InvoicePolicies.Add(seed.Policy);
        db.Invoices.Add(seed.Invoice);
        db.FileAssets.Add(seed.Asset);
        db.InvoiceDocuments.Add(seed.Document);
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
        await db.MarketplaceInvoiceReferenceHistories.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.MarketplaceDeliveries.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.MarketplaceDeliveryStates.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.InvoiceDocuments.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.FileAssets.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.InvoiceSubmissionAttempts.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.InvoicePartySnapshots.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.InvoiceLines.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.Invoices.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.InvoicePolicies.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.LegalEntityProfiles.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.ShipmentPackages.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.Orders.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.PlatformConnections.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.Tenants.Where(row => row.Id == tenantId).ExecuteDeleteAsync();
    }

    private sealed class ScriptedMarketplace : IInvoiceMarketplacePort
    {
        public List<InvoiceDeliveryCommand> Deliveries { get; } = [];
        public bool Succeed { get; init; }

        public Task<AdapterResult<InvoiceDeliveryResult>> DeliverAsync(AdapterContext context, InvoiceDeliveryCommand command, CancellationToken cancellationToken)
        {
            Deliveries.Add(command);
            return Task.FromResult(Succeed
                ? AdapterResult<InvoiceDeliveryResult>.Success(new(command.ExternalPackageId, "OK"))
                : AdapterResult<InvoiceDeliveryResult>.Failure(new AdapterError(AdapterErrorClass.RateLimit, "TRENDYOL_RATE_LIMITED", "rate limited", 429, TimeSpan.FromSeconds(30), null)));
        }

        public string? InvoiceNumber { get; init; }

        // Before a link was sent to a package it reads not invoiced; afterwards it reads the sent invoice number.
        public Task<AdapterResult<InvoiceDeliveryStatus>> QueryDeliveryAsync(AdapterContext context, ExternalInvoiceDeliveryReference reference, CancellationToken cancellationToken) =>
            Task.FromResult(AdapterResult<InvoiceDeliveryStatus>.Success(Succeed && Deliveries.Any(d => d.ExternalPackageId == reference.ExternalReference)
                ? new(reference.ExternalReference, "Invoiced", true, InvoiceNumber, reference.InvoiceLink)
                : new(reference.ExternalReference, "NotInvoiced", true)));
    }
}
