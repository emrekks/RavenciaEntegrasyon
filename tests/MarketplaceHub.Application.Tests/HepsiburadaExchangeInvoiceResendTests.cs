using System.Text.Json;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.Hepsiburada;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Owner decision (6 Nov 2026): a Hepsiburada claim closed as an exchange sends the order's already delivered invoice link
// to Hepsiburada again (same document, no new invoice, no cancellation). The rules that need no database.
public sealed class ExchangeInvoiceResendPolicyTests
{
    [Theory]
    [InlineData(ReturnClaimStatus.Approved, "Accepted", "CHANGE", null, true)]
    [InlineData(ReturnClaimStatus.Completed, "Accepted", "CHANGE", null, true)]
    // The claim does not say; the panel's own approval Hepsiburada confirmed was sent as Change.
    [InlineData(ReturnClaimStatus.Approved, "Accepted", null, "Change", true)]
    // An exchange request closed with a refund is a return, never a re-send.
    [InlineData(ReturnClaimStatus.Completed, "Refunded", "CHANGE", null, false)]
    [InlineData(ReturnClaimStatus.Approved, "Accepted", "REFUND", null, false)]
    [InlineData(ReturnClaimStatus.Approved, "Accepted", null, "Refund", false)]
    // Open claims and unknown outcomes send nothing.
    [InlineData(ReturnClaimStatus.ActionRequired, "AwaitingAction", "CHANGE", null, false)]
    [InlineData(ReturnClaimStatus.Requested, "NewRequest", null, null, false)]
    [InlineData(ReturnClaimStatus.Approved, "Accepted", null, null, false)]
    [InlineData(ReturnClaimStatus.Unknown, "Whatever", "CHANGE", null, false)]
    public void OnlyAKnownExchangeOutcomeCounts(ReturnClaimStatus status, string raw, string? resolvedWith, string? panelApprovedWith, bool expected) =>
        Assert.Equal(expected, ExchangeInvoiceResendPolicy.ClosedAsExchange(status, raw, resolvedWith, panelApprovedWith));

    [Fact]
    public void TheTargetIsTheDeliveredPackageOnlyWhenItIsCertain()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        Assert.Equal(a, ExchangeInvoiceResendPolicy.TargetPackage([a], []));
        Assert.Equal(a, ExchangeInvoiceResendPolicy.TargetPackage([a, b], [a]));
        Assert.Null(ExchangeInvoiceResendPolicy.TargetPackage([a, b], []));
        Assert.Null(ExchangeInvoiceResendPolicy.TargetPackage([a], [b]));
        Assert.Null(ExchangeInvoiceResendPolicy.TargetPackage([], []));
    }

    [Fact]
    public void AHepsiburadaClaimCarriesHowItWasClosed()
    {
        const string claim = """
        {
          "claimNumber": "356189794", "type": "Return", "quantity": 1, "status": "Accepted", "finalizedWith": "Change",
          "orderNumber": "2152988946", "claimDate": "2021-10-14T11:03:17.676Z", "orderDate": "2021-10-14T11:00:20Z",
          "line": { "lineItemId": "61680dc6", "productName": "Gömlek", "merchantSku": "KIVIRCIK20", "price": { "currency": "TRY", "amount": 56.0 } }
        }
        """;
        using var change = JsonDocument.Parse(claim);
        Assert.Equal(ReturnCustomerRequestPolicy.Change, HepsiburadaJsonMapper.ReturnClaim(change.RootElement).ResolvedWith);
        using var refund = JsonDocument.Parse(claim.Replace("\"Change\"", "\"Refund\"", StringComparison.Ordinal));
        Assert.Equal(ReturnCustomerRequestPolicy.Refund, HepsiburadaJsonMapper.ReturnClaim(refund.RootElement).ResolvedWith);
        using var missing = JsonDocument.Parse(claim.Replace("\"finalizedWith\": \"Change\",", "", StringComparison.Ordinal));
        Assert.Null(HepsiburadaJsonMapper.ReturnClaim(missing.RootElement).ResolvedWith);
    }
}

// The queue and the re-send on a real database, with a fake marketplace (no Hepsiburada call).
[Collection("postgres-integration")]
public sealed class HepsiburadaExchangeInvoiceResendPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    private const string DocumentUrl = "https://documents.example.test/exchange/invoice.pdf";
    private static readonly DateTimeOffset IssuedAt = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);

    [PostgreSqlFact]
    public async Task AnApprovedExchangeOfAnInvoicedOrderSendsTheSameLinkOnceToTheSamePackage()
    {
        var seed = NewSeed("exch-ok", ReturnClaimStatus.Approved, "Accepted", ReturnCustomerRequestPolicy.Change);
        await SeedAsync(seed);
        try
        {
            await using (var db = fixture.CreateContext())
            {
                var claim = await db.ReturnClaims.SingleAsync(x => x.Id == seed.Claim.Id);
                Assert.True(await HepsiburadaExchangeInvoiceResend.QueueAsync(db, seed.Tenant.Id, claim, true, true, fixture.Now, CancellationToken.None));
                await db.SaveChangesAsync();
            }
            // The next claim sync queues nothing more.
            await using (var db = fixture.CreateContext())
            {
                var claim = await db.ReturnClaims.SingleAsync(x => x.Id == seed.Claim.Id);
                Assert.False(await HepsiburadaExchangeInvoiceResend.QueueAsync(db, seed.Tenant.Id, claim, true, true, fixture.Now, CancellationToken.None));
            }

            IntegrationJob job;
            await using (var db = fixture.CreateContext())
                job = await db.IntegrationJobs.AsNoTracking().SingleAsync(x => x.TenantId == seed.Tenant.Id);
            Assert.Equal(InvoicingJobTypes.MarketplaceDelivery, job.JobType);
            Assert.Equal(ExchangeInvoiceResendPolicy.JobKey(seed.Claim.Id), job.JobDedupKey);
            Assert.Equal(seed.Marketplace.Id, job.ConnectionId);

            var marketplace = new FakeMarketplace();
            JobExecutionResult result;
            await using (var db = fixture.CreateContext())
                result = await NewProcessor(db, marketplace, writes: true).ProcessAsync(seed.Tenant.Id, job.ConnectionId, job.JobType, job.PayloadJson, "exch-ok", CancellationToken.None);

            Assert.Equal(JobCompletionKind.Succeeded, result.Kind);
            Assert.Equal(ExchangeInvoiceResendPolicy.ResentCode, result.ErrorCode);
            var (context, command) = Assert.Single(marketplace.Deliveries);
            Assert.True(context.IsExchangeInvoiceResend);
            Assert.True(context.IsAutomaticInvoiceMarketplaceDelivery);
            Assert.Equal(seed.Package.ExternalPackageId, command.ExternalPackageId);
            using (var payload = JsonDocument.Parse(command.PayloadJson))
            {
                Assert.Equal(DocumentUrl, payload.RootElement.GetProperty("invoiceLink").GetString());
                Assert.Equal(seed.Invoice.InvoiceNumber, payload.RootElement.GetProperty("invoiceNumber").GetString());
                Assert.Equal(seed.Order.OrderNumber, payload.RootElement.GetProperty("orderNumber").GetString());
            }

            await using var verification = fixture.CreateContext();
            // The invoice and its delivery record stay as they were: no new invoice, no cancellation.
            var invoice = await verification.Invoices.AsNoTracking().SingleAsync(x => x.Id == seed.Invoice.Id);
            Assert.Equal(InvoiceStatus.Completed, invoice.Status);
            Assert.Equal(1, await verification.Invoices.CountAsync(x => x.TenantId == seed.Tenant.Id));
            Assert.Equal("CONFIRMED", (await verification.MarketplaceDeliveryStates.AsNoTracking().SingleAsync(x => x.InvoiceId == seed.Invoice.Id)).Status);
            var note = await verification.OperationalIssues.AsNoTracking().SingleAsync(x => x.TenantId == seed.Tenant.Id);
            Assert.Equal(ExchangeInvoiceResendPolicy.ResentCode, note.Code);
            Assert.Contains("Değişim onaylandı; mevcut fatura Hepsiburada'ya yeniden gönderildi", note.Summary);
            Assert.Contains(seed.Order.OrderNumber, note.Summary);
            Assert.Contains(seed.Invoice.InvoiceNumber!, note.Summary);
            Assert.Contains(seed.Package.ExternalPackageId, note.Summary);
        }
        finally
        {
            await DeleteTenantAsync(seed.Tenant.Id);
        }
    }

    [PostgreSqlTheory]
    // An exchange request closed with a refund.
    [InlineData(ReturnClaimStatus.Completed, "Refunded", ReturnCustomerRequestPolicy.Refund)]
    // Still open.
    [InlineData(ReturnClaimStatus.ActionRequired, "AwaitingAction", null)]
    // Approved, but how it was closed is unknown.
    [InlineData(ReturnClaimStatus.Approved, "Accepted", null)]
    public async Task NoExchangeOutcomeQueuesNothing(ReturnClaimStatus status, string rawStatus, string? resolvedWith)
    {
        var seed = NewSeed($"exch-no-{status}", status, rawStatus, resolvedWith);
        await SeedAsync(seed);
        try
        {
            await using var db = fixture.CreateContext();
            var claim = await db.ReturnClaims.SingleAsync(x => x.Id == seed.Claim.Id);
            Assert.False(await HepsiburadaExchangeInvoiceResend.QueueAsync(db, seed.Tenant.Id, claim, true, true, fixture.Now, CancellationToken.None));
            Assert.False(await db.IntegrationJobs.AnyAsync(x => x.TenantId == seed.Tenant.Id));
            Assert.False(await db.OperationalIssues.AnyAsync(x => x.TenantId == seed.Tenant.Id));
        }
        finally
        {
            await DeleteTenantAsync(seed.Tenant.Id);
        }
    }

    [PostgreSqlFact]
    public async Task AnExchangeWithoutAnInvoiceQueuesNothing()
    {
        var seed = NewSeed("exch-noinv", ReturnClaimStatus.Approved, "Accepted", ReturnCustomerRequestPolicy.Change);
        await SeedAsync(seed, withInvoice: false);
        try
        {
            await using var db = fixture.CreateContext();
            var claim = await db.ReturnClaims.SingleAsync(x => x.Id == seed.Claim.Id);
            Assert.False(await HepsiburadaExchangeInvoiceResend.QueueAsync(db, seed.Tenant.Id, claim, true, true, fixture.Now, CancellationToken.None));
            Assert.False(await db.IntegrationJobs.AnyAsync(x => x.TenantId == seed.Tenant.Id));
        }
        finally
        {
            await DeleteTenantAsync(seed.Tenant.Id);
        }
    }

    // Two packages hold the delivered invoice and the claim's line is not tied to either: no send, a note asks the owner.
    [PostgreSqlFact]
    public async Task AnUnknownPackageRaisesANoteAndSendsNothing()
    {
        var seed = NewSeed("exch-unknown", ReturnClaimStatus.Approved, "Accepted", ReturnCustomerRequestPolicy.Change);
        var second = NewPackage(seed, $"{seed.Package.ExternalPackageId}-2");
        await SeedAsync(seed, allocate: false, extra: db =>
        {
            db.ShipmentPackages.Add(second);
            db.MarketplaceDeliveryStates.Add(NewState(seed, second));
        });
        try
        {
            await using (var db = fixture.CreateContext())
            {
                var claim = await db.ReturnClaims.SingleAsync(x => x.Id == seed.Claim.Id);
                Assert.True(await HepsiburadaExchangeInvoiceResend.QueueAsync(db, seed.Tenant.Id, claim, true, true, fixture.Now, CancellationToken.None));
                await db.SaveChangesAsync();
            }
            await using var verification = fixture.CreateContext();
            Assert.False(await verification.IntegrationJobs.AnyAsync(x => x.TenantId == seed.Tenant.Id));
            var note = await verification.OperationalIssues.AsNoTracking().SingleAsync(x => x.TenantId == seed.Tenant.Id);
            Assert.Equal((ExchangeInvoiceResendPolicy.PackageUnknownCode, IssueStatus.Open), (note.Code, note.Status));
        }
        finally
        {
            await DeleteTenantAsync(seed.Tenant.Id);
        }
    }

    // Invoice delivery to the marketplace switched off: nothing is queued, the owner gets a note.
    [PostgreSqlFact]
    public async Task AClosedDeliverySwitchRaisesANoteAndSendsNothing()
    {
        var seed = NewSeed("exch-off", ReturnClaimStatus.Approved, "Accepted", ReturnCustomerRequestPolicy.Change);
        await SeedAsync(seed);
        try
        {
            await using (var db = fixture.CreateContext())
            {
                var claim = await db.ReturnClaims.SingleAsync(x => x.Id == seed.Claim.Id);
                Assert.True(await HepsiburadaExchangeInvoiceResend.QueueAsync(db, seed.Tenant.Id, claim, true, false, fixture.Now, CancellationToken.None));
                await db.SaveChangesAsync();
            }
            await using var verification = fixture.CreateContext();
            Assert.False(await verification.IntegrationJobs.AnyAsync(x => x.TenantId == seed.Tenant.Id));
            Assert.Equal(ExchangeInvoiceResendPolicy.DeliveryDisabledCode, (await verification.OperationalIssues.AsNoTracking().SingleAsync(x => x.TenantId == seed.Tenant.Id)).Code);
        }
        finally
        {
            await DeleteTenantAsync(seed.Tenant.Id);
        }
    }

    // A refused re-send fails the job (İşler) and leaves a note.
    [PostgreSqlFact]
    public async Task ARefusedResendLeavesANote()
    {
        var seed = NewSeed("exch-fail", ReturnClaimStatus.Approved, "Accepted", ReturnCustomerRequestPolicy.Change);
        await SeedAsync(seed);
        try
        {
            await using (var db = fixture.CreateContext())
            {
                var claim = await db.ReturnClaims.SingleAsync(x => x.Id == seed.Claim.Id);
                await HepsiburadaExchangeInvoiceResend.QueueAsync(db, seed.Tenant.Id, claim, true, true, fixture.Now, CancellationToken.None);
                await db.SaveChangesAsync();
            }
            IntegrationJob job;
            await using (var db = fixture.CreateContext())
                job = await db.IntegrationJobs.AsNoTracking().SingleAsync(x => x.TenantId == seed.Tenant.Id);
            var marketplace = new FakeMarketplace { Error = new AdapterError(AdapterErrorClass.BusinessConflict, "HEPSIBURADA_CONFLICT", "refused", 409, null, null) };
            JobExecutionResult result;
            await using (var db = fixture.CreateContext())
                result = await NewProcessor(db, marketplace, writes: true).ProcessAsync(seed.Tenant.Id, job.ConnectionId, job.JobType, job.PayloadJson, "exch-fail", CancellationToken.None);
            Assert.False(result.Succeeded);
            await using var verification = fixture.CreateContext();
            var note = await verification.OperationalIssues.AsNoTracking().SingleAsync(x => x.TenantId == seed.Tenant.Id);
            Assert.Equal((ExchangeInvoiceResendPolicy.FailedCode, IssueStatus.Open), (note.Code, note.Status));
            Assert.Equal(InvoiceStatus.Completed, (await verification.Invoices.AsNoTracking().SingleAsync(x => x.Id == seed.Invoice.Id)).Status);
        }
        finally
        {
            await DeleteTenantAsync(seed.Tenant.Id);
        }
    }

    private InvoicingJobProcessor NewProcessor(AppDbContext db, IInvoiceMarketplacePort marketplace, bool writes) =>
        new(db, null!, marketplace, null!, fixture.TimeProvider,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FeatureFlags:ExternalWrites"] = writes ? "true" : "false",
                ["FeatureFlags:InvoiceMarketplaceDeliveryWrites"] = writes ? "true" : "false"
            }).Build(),
            new EphemeralDataProtectionProvider());

    private sealed record Seed(Tenant Tenant, PlatformConnection Marketplace, PlatformConnection Provider, Order Order, ShipmentPackage Package,
        OrderLine Line, ReturnClaim Claim, LegalEntityProfile Profile, InvoicePolicy Policy, Invoice Invoice, FileAsset Asset, InvoiceDocument Document);

    private Seed NewSeed(string code, ReturnClaimStatus status, string rawStatus, string? resolvedWith)
    {
        var now = fixture.Now;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"{code}-{Guid.NewGuid():N}"[..40], DisplayName = code, Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var marketplace = NewConnection(tenant.Id, "HEPSIBURADA", $"{code}-store");
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
            OrderedAt = now.AddDays(-6),
            LastRemoteModifiedAt = now.AddDays(-6),
            CustomerSnapshotJson = "{}",
            ShipmentAddressSnapshotJson = "{}",
            InvoiceAddressSnapshotJson = "{}",
            DerivedStatus = "DELIVERED",
            CreatedAt = now,
            UpdatedAt = now,
            Version = 1
        };
        var partial = new Seed(tenant, marketplace, provider, order, null!, null!, null!, null!, null!, null!, null!, null!);
        var package = NewPackage(partial, $"{code}-package");
        var line = new OrderLine
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            OrderId = order.Id,
            ExternalLineId = $"line-{Guid.NewGuid():N}",
            Sku = $"SKU-{Guid.NewGuid():N}"[..12],
            TitleSnapshot = "Gömlek",
            OrderedQuantity = 1m,
            UnitPrice = 120m,
            VatRate = 20m,
            SourceSnapshotJson = """{"vatRate":20}""",
            RawStatus = "Delivered"
        };
        var claim = new ReturnClaim
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            ConnectionId = marketplace.Id,
            OrderId = order.Id,
            ExternalClaimId = $"claim-{Guid.NewGuid():N}"[..20],
            Status = status,
            RawStatus = rawStatus,
            CustomerRequest = ReturnCustomerRequestPolicy.Change,
            ResolvedWith = resolvedWith,
            LastRemoteModifiedAt = now.AddDays(-1),
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
            Status = InvoiceStatus.Completed,
            Currency = "TRY",
            TaxExclusiveTotal = 100m,
            TaxTotal = 20m,
            PayableTotal = 120m,
            Note = InvoiceAmounts.TurkishInvoiceNote(120m),
            IdempotencyKey = $"exchange:{Guid.NewGuid():N}",
            ExternalReference = $"remote-{Guid.NewGuid():N}",
            InvoiceNumber = "DAP2026000000777",
            IssuedAt = IssuedAt,
            CreatedAt = now.AddDays(-4),
            UpdatedAt = now.AddDays(-4),
            Version = 1
        };
        var asset = new FileAsset { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Classification = "INVOICE_DOCUMENT", RelativePath = "exchange/invoice.pdf", MimeType = "application/pdf", SizeBytes = 10, Sha256 = new string('C', 64), Status = "ACTIVE", CreatedAt = now };
        var document = new InvoiceDocument { Id = Guid.CreateVersion7(), TenantId = tenant.Id, InvoiceId = invoice.Id, DocumentType = "PDF", FileAssetId = asset.Id, Sha256 = asset.Sha256, PermanentUrl = DocumentUrl, CreatedAt = now };
        return new(tenant, marketplace, provider, order, package, line, claim, profile, policy, invoice, asset, document);
    }

    private static PlatformConnection NewConnection(Guid tenantId, string platformCode, string storeId) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        PublicId = Guid.CreateVersion7(),
        PlatformCode = platformCode,
        Environment = "PRODUCTION",
        DisplayName = $"{platformCode} değişim testi",
        ExternalStoreId = storeId,
        Status = "ACTIVE",
        ApiVersion = platformCode == "TRENDYOL_EFATURAM" ? "1.0.0" : "V1"
    };

    private ShipmentPackage NewPackage(Seed seed, string externalId) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = seed.Tenant.Id,
        ConnectionId = seed.Marketplace.Id,
        OrderId = seed.Order.Id,
        ExternalPackageId = externalId,
        GrossAmount = 120m,
        NetAmount = 120m,
        CargoProviderExternalId = "HBJ",
        Status = ShipmentPackageStatus.Delivered,
        RawStatus = "Delivered",
        StatusOccurredAt = fixture.Now.AddDays(-3),
        MarketplaceInvoiceStatus = MarketplaceInvoiceStatus.Invoiced,
        CreatedAt = fixture.Now,
        UpdatedAt = fixture.Now,
        Version = 1
    };

    private MarketplaceDeliveryState NewState(Seed seed, ShipmentPackage package) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = seed.Tenant.Id,
        InvoiceId = seed.Invoice.Id,
        ConnectionId = seed.Marketplace.Id,
        PackageId = package.Id,
        AttemptNumber = 1,
        ExternalIdempotencyKey = $"delivery:{seed.Invoice.Id:N}:{package.Id:N}",
        RequestHash = "hash",
        DeliveryType = "LINK",
        Status = "CONFIRMED",
        ExternalReference = package.ExternalPackageId,
        CreatedAt = fixture.Now.AddDays(-3),
        UpdatedAt = fixture.Now.AddDays(-3),
        CompletedAt = fixture.Now.AddDays(-3),
        Version = 1
    };

    private async Task SeedAsync(Seed seed, bool withInvoice = true, bool allocate = true, Action<AppDbContext>? extra = null)
    {
        await using var db = fixture.CreateContext();
        db.Tenants.Add(seed.Tenant);
        db.PlatformConnections.AddRange(seed.Marketplace, seed.Provider);
        db.Orders.Add(seed.Order);
        db.ShipmentPackages.Add(seed.Package);
        db.OrderLines.Add(seed.Line);
        if (allocate)
            db.PackageLineAllocations.Add(new PackageLineAllocation { Id = Guid.CreateVersion7(), TenantId = seed.Tenant.Id, PackageId = seed.Package.Id, OrderLineId = seed.Line.Id, AllocatedQuantity = 1m, SourceEventId = $"exchange:{seed.Package.Id:N}:1" });
        db.ReturnClaims.Add(seed.Claim);
        db.ReturnLines.Add(new ReturnLine { Id = Guid.CreateVersion7(), TenantId = seed.Tenant.Id, ClaimId = seed.Claim.Id, OrderLineId = seed.Line.Id, ExternalLineId = $"claim-line-{Guid.NewGuid():N}", Quantity = 1m, RawStatus = seed.Claim.RawStatus });
        if (withInvoice)
        {
            db.LegalEntityProfiles.Add(seed.Profile);
            db.InvoicePolicies.Add(seed.Policy);
            db.Invoices.Add(seed.Invoice);
            db.FileAssets.Add(seed.Asset);
            db.InvoiceDocuments.Add(seed.Document);
            db.MarketplaceDeliveryStates.Add(NewState(seed, seed.Package));
        }
        extra?.Invoke(db);
        await db.SaveChangesAsync();
    }

    private async Task DeleteTenantAsync(Guid tenantId)
    {
        await using var db = fixture.CreateContext();
        var jobIds = await db.IntegrationJobs.Where(row => row.TenantId == tenantId).Select(row => row.Id).ToListAsync();
        await db.JobAttempts.Where(row => row.TenantId == tenantId || jobIds.Contains(row.JobId)).ExecuteDeleteAsync();
        await db.IntegrationOutboxEvents.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.IntegrationJobs.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.AuditLogs.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.OperationalIssues.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.MarketplaceInvoiceReferenceHistories.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.MarketplaceDeliveries.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.MarketplaceDeliveryStates.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.InvoiceDocuments.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.FileAssets.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.Invoices.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.InvoicePolicies.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.LegalEntityProfiles.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.ReturnDecisions.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.ReturnLines.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.ReturnClaims.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.PackageLineAllocations.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.ShipmentPackages.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.OrderLines.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.Orders.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.PlatformConnections.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.Tenants.Where(row => row.Id == tenantId).ExecuteDeleteAsync();
    }

    // Answers every re-send with success (or the scripted error) and keeps each request; never reads.
    private sealed class FakeMarketplace : IInvoiceMarketplacePort
    {
        public List<(AdapterContext Context, InvoiceDeliveryCommand Command)> Deliveries { get; } = [];
        public AdapterError? Error { get; init; }

        public Task<AdapterResult<InvoiceDeliveryResult>> DeliverAsync(AdapterContext context, InvoiceDeliveryCommand command, CancellationToken cancellationToken)
        {
            Deliveries.Add((context, command));
            return Task.FromResult(Error is null
                ? AdapterResult<InvoiceDeliveryResult>.Success(new(command.ExternalPackageId, "INVOICED"))
                : AdapterResult<InvoiceDeliveryResult>.Failure(Error));
        }

        public Task<AdapterResult<InvoiceDeliveryStatus>> QueryDeliveryAsync(AdapterContext context, ExternalInvoiceDeliveryReference reference, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The exchange re-send must not read the package first.");
    }
}
