using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Live 13 Oct 2026: 15 INVOICE_MARKETPLACE_DELIVERY jobs of order 4623308608 stopped in MANUAL_REVIEW with
// DELIVERY_RETRY_PAYLOAD_CHANGED, because the delivery record was keyed by a hash of its whole payload (the latest
// document link among it) and the invoice row was left as it was, so the panel showed it on its way. A delivery is now
// keyed by its identity; a real identity change stops it with the invoice in review until the owner's "Tekrar Dene".
// A manual-order delivery that timed out or was refused no longer ends its job as SUCCEEDED.
[Collection("postgres-integration")]
public sealed class PostgreSqlInvoiceDeliveryIdentityTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    private static readonly DateTimeOffset IssuedAt = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);

    [PostgreSqlFact]
    public async Task ARetryWhosePayloadChangedButNotItsIdentityIsSentUnderTheSameKey()
    {
        var seed = NewSeed("identity-same");
        var state = NewDeliveryState(seed, "RETRYABLE_FAILURE", Hash(InvoiceMarketplaceRetryPolicy.DeliveryIdentity(seed.Invoice.Id, seed.Package.ExternalPackageId, seed.Invoice.InvoiceNumber)));
        await SeedAsync(seed, db => db.MarketplaceDeliveryStates.Add(state));
        try
        {
            var marketplace = new ScriptedMarketplace();
            await using (var db = fixture.CreateContext())
            {
                var result = await NewProcessor(db, marketplace).ProcessAsync(seed.Tenant.Id, seed.Marketplace.Id, InvoicingJobTypes.MarketplaceDelivery,
                    DeliveryPayload(seed), "identity-same", CancellationToken.None);
                // The scripted marketplace refuses with a rate limit: the attempt was made and is retried.
                Assert.Equal(JobCompletionKind.Retry, result.Kind);
            }

            var command = Assert.Single(marketplace.Deliveries);
            Assert.Equal(state.RequestHash, command.RequestHash);
            await using var verification = fixture.CreateContext();
            var invoice = await verification.Invoices.SingleAsync(item => item.Id == seed.Invoice.Id);
            Assert.NotEqual(InvoiceStatus.ManualReview, invoice.Status);
            Assert.NotEqual(InvoiceMarketplaceRetryPolicy.DeliveryIdentityChanged, invoice.LastErrorCode);
        }
        finally
        {
            await DeleteTenantAsync(seed.Tenant.Id);
        }
    }

    // A state written before the identity key holds the hash of its payload: the same payload is still the same delivery.
    [PostgreSqlFact]
    public async Task AStateKeyedByTheSamePayloadIsTakenOverByTheIdentity()
    {
        var seed = NewSeed("identity-legacy");
        var legacyPayload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["shipmentPackageId"] = seed.Package.ExternalPackageId,
            ["invoiceLink"] = new Uri(DocumentUrl).AbsoluteUri,
            ["invoiceDateTime"] = IssuedAt.ToUnixTimeMilliseconds(),
            ["invoiceNumber"] = seed.Invoice.InvoiceNumber,
            ["micro"] = false
        });
        var state = NewDeliveryState(seed, "RETRYABLE_FAILURE", Hash(legacyPayload));
        await SeedAsync(seed, db => db.MarketplaceDeliveryStates.Add(state));
        try
        {
            var marketplace = new ScriptedMarketplace();
            await using (var db = fixture.CreateContext())
                await NewProcessor(db, marketplace).ProcessAsync(seed.Tenant.Id, seed.Marketplace.Id, InvoicingJobTypes.MarketplaceDelivery,
                    DeliveryPayload(seed), "identity-legacy", CancellationToken.None);

            var identity = Hash(InvoiceMarketplaceRetryPolicy.DeliveryIdentity(seed.Invoice.Id, seed.Package.ExternalPackageId, seed.Invoice.InvoiceNumber));
            Assert.Equal(identity, Assert.Single(marketplace.Deliveries).RequestHash);
            await using var verification = fixture.CreateContext();
            Assert.Equal(identity, (await verification.MarketplaceDeliveryStates.SingleAsync(item => item.Id == state.Id)).RequestHash);
        }
        finally
        {
            await DeleteTenantAsync(seed.Tenant.Id);
        }
    }

    // Another identity on record: nothing is sent, the invoice shows the stop, and only the owner's retry sends it.
    [PostgreSqlFact]
    public async Task AnotherIdentityOnRecordStopsInReviewUntilTheOwnerSendsItAgain()
    {
        var seed = NewSeed("identity-changed");
        var state = NewDeliveryState(seed, "RETRYABLE_FAILURE", Hash("delivery-identity:v1:another-invoice-number"));
        await SeedAsync(seed, db => db.MarketplaceDeliveryStates.Add(state));
        try
        {
            var marketplace = new ScriptedMarketplace();
            await using (var db = fixture.CreateContext())
            {
                var result = await NewProcessor(db, marketplace).ProcessAsync(seed.Tenant.Id, seed.Marketplace.Id, InvoicingJobTypes.MarketplaceDelivery,
                    DeliveryPayload(seed), "identity-changed", CancellationToken.None);
                Assert.Equal(JobCompletionKind.ManualReview, result.Kind);
                Assert.Equal(InvoiceMarketplaceRetryPolicy.DeliveryIdentityChanged, result.ErrorCode);
            }

            Assert.Empty(marketplace.Deliveries);
            await using (var db = fixture.CreateContext())
            {
                var invoice = await db.Invoices.SingleAsync(item => item.Id == seed.Invoice.Id);
                Assert.Equal(InvoiceStatus.ManualReview, invoice.Status);
                Assert.Equal(InvoiceMarketplaceRetryPolicy.DeliveryIdentityChanged, invoice.LastErrorCode);
                Assert.True(InvoiceMarketplaceRetryPolicy.CanRetryDelivery(invoice.Status, invoice.LastErrorCode));
                Assert.Equal(state.RequestHash, (await db.MarketplaceDeliveryStates.SingleAsync(item => item.Id == state.Id)).RequestHash);

                // A job that runs again on its own (not the owner's retry) still sends nothing: the invoice is in review
                // and only the owner takes it out (owner rule 5, 5 Nov 2026), so the job stops before reading anything
                // and the stop stays as it was.
                var again = await NewProcessor(db, marketplace).ProcessAsync(seed.Tenant.Id, seed.Marketplace.Id, InvoicingJobTypes.MarketplaceDelivery,
                    DeliveryPayload(seed), "identity-changed-again", CancellationToken.None);
                Assert.Equal(JobCompletionKind.Blocked, again.Kind);
                Assert.Equal(InvoiceDeliveryGuardPolicy.ManualReviewOwnerOnlyCode, again.ErrorCode);
                Assert.Empty(marketplace.Deliveries);
            }
            await using (var db = fixture.CreateContext())
            {
                var invoice = await db.Invoices.AsNoTracking().SingleAsync(item => item.Id == seed.Invoice.Id);
                Assert.Equal((InvoiceStatus.ManualReview, InvoiceMarketplaceRetryPolicy.DeliveryIdentityChanged), (invoice.Status, invoice.LastErrorCode));
            }

            // The owner's "Tekrar Dene" (EnqueueDeliveryAsync) puts the invoice back on its way with the stop's code and
            // queues the delivery with manualDeliveryRetry; that attempt takes the current identity and is sent.
            await using (var db = fixture.CreateContext())
                await db.Invoices.Where(item => item.Id == seed.Invoice.Id).ExecuteUpdateAsync(setter => setter.SetProperty(item => item.Status, InvoiceStatus.MarketplacePending));
            await using (var db = fixture.CreateContext())
                await NewProcessor(db, marketplace).ProcessAsync(seed.Tenant.Id, seed.Marketplace.Id, InvoicingJobTypes.MarketplaceDelivery,
                    JsonSerializer.Serialize(new { invoiceId = seed.Invoice.Id, packageId = seed.Package.Id, manualDeliveryRetry = true }), "identity-owner-retry", CancellationToken.None);

            var identity = Hash(InvoiceMarketplaceRetryPolicy.DeliveryIdentity(seed.Invoice.Id, seed.Package.ExternalPackageId, seed.Invoice.InvoiceNumber));
            Assert.Equal(identity, Assert.Single(marketplace.Deliveries).RequestHash);
        }
        finally
        {
            await DeleteTenantAsync(seed.Tenant.Id);
        }
    }

    [PostgreSqlFact]
    public async Task AManualOrderDeliveryThatTimedOutEndsInReviewNotAsSucceeded()
    {
        var seed = NewSeed("manual-timeout");
        var (document, state) = NewManualDocument(seed);
        await SeedAsync(seed, db => { db.ManualOrderInvoiceDocuments.Add(document); db.ManualOrderInvoiceDeliveryStates.Add(state); });
        try
        {
            var marketplace = new ScriptedMarketplace { DeliveryThrows = new OperationCanceledException("timeout") };
            await using (var db = fixture.CreateContext())
            {
                var result = await NewProcessor(db, marketplace).ProcessAsync(seed.Tenant.Id, seed.Marketplace.Id, InvoicingJobTypes.ManualOrderInvoiceDelivery,
                    ManualPayload(document, seed), "manual-timeout", CancellationToken.None);
                Assert.False(result.Succeeded);
                Assert.Equal(JobCompletionKind.ManualReview, result.Kind);
                Assert.Equal("MANUAL_INVOICE_DELIVERY_TIMEOUT", result.ErrorCode);
            }

            await using var verification = fixture.CreateContext();
            var stored = await verification.ManualOrderInvoiceDeliveryStates.SingleAsync(item => item.Id == state.Id);
            Assert.Equal(("UNKNOWN", "MANUAL_INVOICE_DELIVERY_TIMEOUT"), (stored.Status, stored.ErrorCode));
        }
        finally
        {
            await DeleteTenantAsync(seed.Tenant.Id);
        }
    }

    [PostgreSqlFact]
    public async Task AManualOrderDeliveryTheMarketplaceRefusedIsBlockedNotSucceeded()
    {
        var seed = NewSeed("manual-refused");
        var (document, state) = NewManualDocument(seed);
        await SeedAsync(seed, db => { db.ManualOrderInvoiceDocuments.Add(document); db.ManualOrderInvoiceDeliveryStates.Add(state); });
        try
        {
            var marketplace = new ScriptedMarketplace { DeliveryError = new AdapterError(AdapterErrorClass.Validation, "TRENDYOL_INVOICE_LINK_REJECTED", "refused", 400, null, null) };
            await using (var db = fixture.CreateContext())
            {
                var result = await NewProcessor(db, marketplace).ProcessAsync(seed.Tenant.Id, seed.Marketplace.Id, InvoicingJobTypes.ManualOrderInvoiceDelivery,
                    ManualPayload(document, seed), "manual-refused", CancellationToken.None);
                Assert.Equal(JobCompletionKind.Blocked, result.Kind);
                Assert.Equal("TRENDYOL_INVOICE_LINK_REJECTED", result.ErrorCode);
            }

            await using var verification = fixture.CreateContext();
            Assert.Equal("FAILED", (await verification.ManualOrderInvoiceDeliveryStates.SingleAsync(item => item.Id == state.Id)).Status);
        }
        finally
        {
            await DeleteTenantAsync(seed.Tenant.Id);
        }
    }

    private const string DocumentUrl = "https://documents.example.test/identity/invoice.pdf";

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private InvoicingJobProcessor NewProcessor(AppDbContext db, IInvoiceMarketplacePort marketplace) =>
        new(db, null!, marketplace, null!, fixture.TimeProvider, new ConfigurationBuilder().Build(), new EphemeralDataProtectionProvider());

    private static string DeliveryPayload(Seed seed) => JsonSerializer.Serialize(new { invoiceId = seed.Invoice.Id, packageId = seed.Package.Id });

    private static string ManualPayload(ManualOrderInvoiceDocument document, Seed seed) =>
        JsonSerializer.Serialize(new { manualOrderInvoiceDocumentId = document.Id, packageId = seed.Package.Id, inspectionOnly = false });

    private sealed record Seed(Tenant Tenant, PlatformConnection Marketplace, PlatformConnection Provider, Order Order, ShipmentPackage Package,
        LegalEntityProfile Profile, InvoicePolicy Policy, Invoice Invoice, FileAsset Asset, InvoiceDocument Document);

    private Seed NewSeed(string code)
    {
        var now = fixture.Now;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"{code}-{Guid.NewGuid():N}"[..40], DisplayName = code, Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
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
            IdempotencyKey = $"identity:{Guid.NewGuid():N}",
            ExternalReference = $"remote-{Guid.NewGuid():N}",
            InvoiceNumber = "DAP2026000000999",
            IssuedAt = IssuedAt,
            CreatedAt = now.AddDays(-1),
            UpdatedAt = now.AddDays(-1),
            Version = 1
        };
        var asset = new FileAsset { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Classification = "INVOICE_DOCUMENT", RelativePath = "identity/invoice.pdf", MimeType = "application/pdf", SizeBytes = 10, Sha256 = new string('B', 64), Status = "ACTIVE", CreatedAt = now };
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

    private MarketplaceDeliveryState NewDeliveryState(Seed seed, string status, string requestHash) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = seed.Tenant.Id,
        InvoiceId = seed.Invoice.Id,
        ConnectionId = seed.Marketplace.Id,
        PackageId = seed.Package.Id,
        AttemptNumber = 1,
        ExternalIdempotencyKey = $"delivery:{seed.Invoice.Id:N}:{seed.Package.Id:N}",
        RequestHash = requestHash,
        DeliveryType = "LINK",
        Status = status,
        ExternalReference = seed.Package.ExternalPackageId,
        CreatedAt = fixture.Now.AddDays(-1),
        UpdatedAt = fixture.Now.AddDays(-1),
        Version = 1
    };

    private (ManualOrderInvoiceDocument Document, ManualOrderInvoiceDeliveryState State) NewManualDocument(Seed seed)
    {
        var document = new ManualOrderInvoiceDocument
        {
            Id = Guid.CreateVersion7(),
            TenantId = seed.Tenant.Id,
            OrderId = seed.Order.Id,
            ConnectionId = seed.Marketplace.Id,
            InvoiceNumber = "W2A2026000000099",
            IssuedAt = IssuedAt,
            IdempotencyKey = $"manual:{Guid.NewGuid():N}",
            DocumentType = "URL",
            DocumentUrl = "https://example.com/manual/invoice.pdf",
            Status = "ACTIVE",
            CreatedAt = fixture.Now,
            UpdatedAt = fixture.Now
        };
        var state = new ManualOrderInvoiceDeliveryState
        {
            Id = Guid.CreateVersion7(),
            TenantId = seed.Tenant.Id,
            DocumentId = document.Id,
            ConnectionId = seed.Marketplace.Id,
            PackageId = seed.Package.Id,
            IdempotencyKey = $"manual-invoice-delivery:{document.Id:N}:{seed.Package.Id:N}",
            RequestHash = Hash($"{document.DocumentUrl}\n{document.InvoiceNumber}\n{document.IssuedAt:O}"),
            Status = "QUEUED",
            CreatedAt = fixture.Now,
            UpdatedAt = fixture.Now,
            Version = 1
        };
        return (document, state);
    }

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
        await db.ManualOrderInvoiceDeliveryStates.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.ManualOrderInvoiceDocuments.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
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

    // Reads every package as explicitly not invoiced and answers a delivery with a rate limit (the request was refused
    // before it ran, so it is retried), a scripted error or a timeout. Every delivery command is kept.
    private sealed class ScriptedMarketplace : IInvoiceMarketplacePort
    {
        public List<InvoiceDeliveryCommand> Deliveries { get; } = [];
        public AdapterError? DeliveryError { get; init; }
        public Exception? DeliveryThrows { get; init; }

        public Task<AdapterResult<InvoiceDeliveryResult>> DeliverAsync(AdapterContext context, InvoiceDeliveryCommand command, CancellationToken cancellationToken)
        {
            Deliveries.Add(command);
            if (DeliveryThrows is not null) throw DeliveryThrows;
            return Task.FromResult(AdapterResult<InvoiceDeliveryResult>.Failure(DeliveryError
                ?? new AdapterError(AdapterErrorClass.RateLimit, "TRENDYOL_RATE_LIMITED", "rate limited", 429, TimeSpan.FromSeconds(30), null)));
        }

        public Task<AdapterResult<InvoiceDeliveryStatus>> QueryDeliveryAsync(AdapterContext context, ExternalInvoiceDeliveryReference reference, CancellationToken cancellationToken) =>
            Task.FromResult(AdapterResult<InvoiceDeliveryStatus>.Success(new(reference.ExternalReference, "NotInvoiced", true)));
    }
}
