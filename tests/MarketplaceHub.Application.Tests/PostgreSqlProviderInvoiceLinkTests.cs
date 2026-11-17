using System.Text.Json;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Owner decision 17 Nov 2026 (MarketplaceInvoiceLinkPolicy): a new delivery sends E-Faturam's permanent document URL, the
// one the PDF was downloaded from and checked; n11 and Pazarama, whose link rule wants a document extension, keep the panel
// link; the Panel setting brings the panel link back everywhere; a delivery keeps the link it carried; a missing or
// unusable provider URL stops the delivery instead of falling back. Fakes only: no provider or marketplace is called.
[Collection("postgres-integration")]
public sealed class PostgreSqlProviderInvoiceLinkTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    private const string ProviderUrl = "https://apigateway.trendyolecozum.com/api/invoice/documents/download/5072747";
    private const string PublicBaseUrl = "https://panel.ravencia.com/";
    private static readonly DateTimeOffset IssuedAt = new(2026, 11, 17, 9, 0, 0, TimeSpan.Zero);

    [PostgreSqlTheory]
    [InlineData("TRENDYOL")]
    [InlineData("HEPSIBURADA")]
    [InlineData("PTTAVM")]
    [InlineData("SHOPIFY")]
    public async Task ANewDeliverySendsTheProviderUrlAndRecordsIt(string platformCode)
    {
        var seed = NewSeed($"prov-{platformCode}", platformCode);
        await SeedAsync(seed);
        try
        {
            var marketplace = new RecordingMarketplace();
            await using (var db = fixture.CreateContext())
                await NewProcessor(db, marketplace).ProcessAsync(seed.Tenant.Id, seed.Marketplace.Id, InvoicingJobTypes.MarketplaceDelivery, DeliveryPayload(seed), $"prov-{platformCode}", CancellationToken.None);

            Assert.Equal(ProviderUrl, SentLink(Assert.Single(marketplace.Deliveries)));
            await using var verification = fixture.CreateContext();
            Assert.Equal(ProviderUrl, (await verification.MarketplaceDeliveryStates.AsNoTracking().SingleAsync(x => x.InvoiceId == seed.Invoice.Id)).InvoiceLink);
            Assert.False(await verification.InvoiceDocuments.AnyAsync(x => x.InvoiceId == seed.Invoice.Id && x.DocumentType == InvoiceDeliveredLinks.MarketplaceDeliveryDocumentType));
        }
        finally
        {
            await DeleteTenantAsync(seed.Tenant.Id);
        }
    }

    [PostgreSqlTheory]
    [InlineData("N11", null)]
    [InlineData("PAZARAMA", null)]
    [InlineData("TRENDYOL", "Panel")]
    [InlineData("PTTAVM", "panel")]
    public async Task NElevenPazaramaAndThePanelSettingSendThePanelLink(string platformCode, string? setting)
    {
        var seed = NewSeed($"panel-{platformCode}", platformCode);
        await SeedAsync(seed);
        try
        {
            var marketplace = new RecordingMarketplace();
            await using (var db = fixture.CreateContext())
                await NewProcessor(db, marketplace, setting).ProcessAsync(seed.Tenant.Id, seed.Marketplace.Id, InvoicingJobTypes.MarketplaceDelivery, DeliveryPayload(seed), $"panel-{platformCode}", CancellationToken.None);

            var sent = SentLink(Assert.Single(marketplace.Deliveries));
            Assert.StartsWith($"{PublicBaseUrl}api/v1/public/invoice-documents/", sent);
            Assert.EndsWith("/content", sent);
            await using var verification = fixture.CreateContext();
            var panelRow = await verification.InvoiceDocuments.AsNoTracking().SingleAsync(x => x.InvoiceId == seed.Invoice.Id && x.DocumentType == InvoiceDeliveredLinks.MarketplaceDeliveryDocumentType);
            Assert.Equal(sent, panelRow.PermanentUrl);
            Assert.Equal(sent, (await verification.MarketplaceDeliveryStates.AsNoTracking().SingleAsync(x => x.InvoiceId == seed.Invoice.Id)).InvoiceLink);
        }
        finally
        {
            await DeleteTenantAsync(seed.Tenant.Id);
        }
    }

    // An invoice delivered before the change (no recorded link) is never sent again because of it: a confirmed delivery
    // sends nothing, whatever kind its link was.
    [PostgreSqlTheory]
    [InlineData("PTTAVM")]
    [InlineData("TRENDYOL")]
    public async Task AnAlreadyDeliveredInvoiceIsNotSentAgain(string platformCode)
    {
        var seed = NewSeed($"done-{platformCode}", platformCode, InvoiceStatus.Completed);
        await SeedAsync(seed, db =>
        {
            db.InvoiceDocuments.Add(PanelRow(seed, "https://panel.ravencia.com/api/v1/public/invoice-documents/old-token/content"));
            db.MarketplaceDeliveryStates.Add(NewState(seed, "CONFIRMED", invoiceLink: null));
        });
        try
        {
            var marketplace = new RecordingMarketplace();
            await using (var db = fixture.CreateContext())
                await NewProcessor(db, marketplace).ProcessAsync(seed.Tenant.Id, seed.Marketplace.Id, InvoicingJobTypes.MarketplaceDelivery, DeliveryPayload(seed), $"done-{platformCode}", CancellationToken.None);

            Assert.Empty(marketplace.Deliveries);
            await using var verification = fixture.CreateContext();
            var state = await verification.MarketplaceDeliveryStates.AsNoTracking().SingleAsync(x => x.InvoiceId == seed.Invoice.Id);
            Assert.Equal(("CONFIRMED", (string?)null), (state.Status, state.InvoiceLink));
            Assert.Equal(InvoiceStatus.Completed, (await verification.Invoices.AsNoTracking().SingleAsync(x => x.Id == seed.Invoice.Id)).Status);
        }
        finally
        {
            await DeleteTenantAsync(seed.Tenant.Id);
        }
    }

    // A PTT AVM delivery made with the panel link before the change and sent again (a retryable failure) keeps the panel
    // link; one that recorded its link sends exactly that link.
    [PostgreSqlTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AResendKeepsTheLinkTheDeliveryCarried(bool recorded)
    {
        const string earlierPanelLink = "https://panel.ravencia.com/api/v1/public/invoice-documents/earlier/content";
        var seed = NewSeed(recorded ? "resend-rec" : "resend-old", "PTTAVM");
        await SeedAsync(seed, db =>
        {
            if (!recorded) db.InvoiceDocuments.Add(PanelRow(seed, earlierPanelLink));
            db.MarketplaceDeliveryStates.Add(NewState(seed, "RETRYABLE_FAILURE", recorded ? earlierPanelLink : null));
        });
        try
        {
            var marketplace = new RecordingMarketplace();
            await using (var db = fixture.CreateContext())
                await NewProcessor(db, marketplace).ProcessAsync(seed.Tenant.Id, seed.Marketplace.Id, InvoicingJobTypes.MarketplaceDelivery, DeliveryPayload(seed), "resend", CancellationToken.None);

            Assert.Equal(earlierPanelLink, SentLink(Assert.Single(marketplace.Deliveries)));
            await using var verification = fixture.CreateContext();
            Assert.Equal(earlierPanelLink, (await verification.MarketplaceDeliveryStates.AsNoTracking().SingleAsync(x => x.InvoiceId == seed.Invoice.Id)).InvoiceLink);
        }
        finally
        {
            await DeleteTenantAsync(seed.Tenant.Id);
        }
    }

    // No verified provider URL (the PDF row has none) or an unusable one: nothing is sent, the panel link is not made in
    // its place, and the invoice shows the reason and can be sent again with Tekrar Gönder.
    [PostgreSqlTheory]
    [InlineData(null, MarketplaceInvoiceLinkPolicy.ProviderLinkMissingCode)]
    [InlineData("http://apigateway.trendyolecozum.com/api/invoice/documents/download/1", MarketplaceInvoiceLinkPolicy.ProviderLinkInvalidCode)]
    public async Task AMissingOrUnusableProviderUrlStopsTheDeliveryWithTheReason(string? providerUrl, string expectedCode)
    {
        var seed = NewSeed(providerUrl is null ? "stop-missing" : "stop-invalid", "TRENDYOL", providerUrl: providerUrl);
        await SeedAsync(seed);
        try
        {
            var marketplace = new RecordingMarketplace();
            JobExecutionResult result;
            await using (var db = fixture.CreateContext())
                result = await NewProcessor(db, marketplace).ProcessAsync(seed.Tenant.Id, seed.Marketplace.Id, InvoicingJobTypes.MarketplaceDelivery, DeliveryPayload(seed), "stop", CancellationToken.None);

            Assert.Equal(JobCompletionKind.Blocked, result.Kind);
            Assert.Equal(expectedCode, result.ErrorCode);
            Assert.Empty(marketplace.Deliveries);
            await using var verification = fixture.CreateContext();
            var invoice = await verification.Invoices.AsNoTracking().SingleAsync(x => x.Id == seed.Invoice.Id);
            Assert.Equal((InvoiceStatus.MarketplaceFailed, expectedCode), (invoice.Status, invoice.LastErrorCode));
            Assert.True(InvoiceMarketplaceRetryPolicy.CanRetryDelivery(invoice.Status, invoice.LastErrorCode));
            Assert.False(await verification.InvoiceDocuments.AnyAsync(x => x.InvoiceId == seed.Invoice.Id && x.DocumentType == InvoiceDeliveredLinks.MarketplaceDeliveryDocumentType));
            Assert.False(await verification.MarketplaceDeliveryStates.AnyAsync(x => x.InvoiceId == seed.Invoice.Id));
        }
        finally
        {
            await DeleteTenantAsync(seed.Tenant.Id);
        }
    }

    // The marketplace already shows the invoice with its older panel link (or its provider URL): either is this invoice's
    // own link, so the delivery is confirmed from the read and nothing is sent.
    [PostgreSqlTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AMarketplaceShowingEitherOwnLinkConfirmsWithoutSending(bool panelLinkShown)
    {
        const string olderPanelLink = "https://panel.ravencia.com/api/v1/public/invoice-documents/older/content";
        var seed = NewSeed(panelLinkShown ? "own-panel" : "own-provider", "TRENDYOL");
        await SeedAsync(seed, db => db.InvoiceDocuments.Add(PanelRow(seed, olderPanelLink, createdAt: fixture.Now.AddMinutes(-5))));
        try
        {
            var marketplace = new RecordingMarketplace { Shown = (seed.Invoice.InvoiceNumber!, panelLinkShown ? olderPanelLink : ProviderUrl) };
            await using (var db = fixture.CreateContext())
                await NewProcessor(db, marketplace).ProcessAsync(seed.Tenant.Id, seed.Marketplace.Id, InvoicingJobTypes.MarketplaceDelivery, DeliveryPayload(seed), "own", CancellationToken.None);

            Assert.Empty(marketplace.Deliveries);
            await using var verification = fixture.CreateContext();
            Assert.Equal("CONFIRMED", (await verification.MarketplaceDeliveryStates.AsNoTracking().SingleAsync(x => x.InvoiceId == seed.Invoice.Id)).Status);
        }
        finally
        {
            await DeleteTenantAsync(seed.Tenant.Id);
        }
    }

    private static string SentLink(InvoiceDeliveryCommand command)
    {
        using var payload = JsonDocument.Parse(command.PayloadJson);
        return payload.RootElement.GetProperty("invoiceLink").GetString()!;
    }

    private InvoicingJobProcessor NewProcessor(AppDbContext db, IInvoiceMarketplacePort marketplace, string? setting = null)
    {
        var values = new Dictionary<string, string?> { ["Marketplace:PublicBaseUrl"] = PublicBaseUrl };
        if (setting is not null) values[MarketplaceInvoiceLinkPolicy.SettingKey] = setting;
        return new(db, null!, marketplace, null!, fixture.TimeProvider, new ConfigurationBuilder().AddInMemoryCollection(values).Build(), new EphemeralDataProtectionProvider());
    }

    private static string DeliveryPayload(Seed seed) => JsonSerializer.Serialize(new { invoiceId = seed.Invoice.Id, packageId = seed.Package.Id });

    private sealed record Seed(Tenant Tenant, PlatformConnection Marketplace, PlatformConnection Provider, Order Order, ShipmentPackage Package,
        LegalEntityProfile Profile, InvoicePolicy Policy, Invoice Invoice, FileAsset Asset, InvoiceDocument Document);

    private Seed NewSeed(string code, string platformCode, InvoiceStatus status = InvoiceStatus.Accepted, string? providerUrl = ProviderUrl)
    {
        var now = fixture.Now;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"{code}-{Guid.NewGuid():N}"[..40], DisplayName = code, Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var marketplace = NewConnection(tenant.Id, platformCode, $"{code}-store");
        var provider = NewConnection(tenant.Id, "TRENDYOL_EFATURAM", $"{code}-efaturam");
        var order = new Order
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            ConnectionId = marketplace.Id,
            ExternalOrderId = platformCode == "SHOPIFY" ? "5550001112223" : $"{code}-order",
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
            Status = status,
            Currency = "TRY",
            TaxExclusiveTotal = 100m,
            TaxTotal = 20m,
            PayableTotal = 120m,
            Note = InvoiceAmounts.TurkishInvoiceNote(120m),
            IdempotencyKey = $"provlink:{Guid.NewGuid():N}",
            ExternalReference = $"remote-{Guid.NewGuid():N}",
            InvoiceNumber = "DAP2026000000777",
            IssuedAt = IssuedAt,
            CreatedAt = now.AddDays(-1),
            UpdatedAt = now.AddDays(-1),
            Version = 1
        };
        var asset = new FileAsset { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Classification = "INVOICE_DOCUMENT", RelativePath = "provlink/invoice.pdf", MimeType = "application/pdf", SizeBytes = 10, Sha256 = new string('C', 64), Status = "ACTIVE", CreatedAt = now };
        var document = new InvoiceDocument { Id = Guid.CreateVersion7(), TenantId = tenant.Id, InvoiceId = invoice.Id, DocumentType = "PDF", FileAssetId = asset.Id, Sha256 = asset.Sha256, PermanentUrl = providerUrl, CreatedAt = now };
        return new(tenant, marketplace, provider, order, package, profile, policy, invoice, asset, document);
    }

    private InvoiceDocument PanelRow(Seed seed, string link, DateTimeOffset? createdAt = null) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = seed.Tenant.Id,
        InvoiceId = seed.Invoice.Id,
        DocumentType = InvoiceDeliveredLinks.MarketplaceDeliveryDocumentType,
        FileAssetId = seed.Asset.Id,
        Sha256 = seed.Asset.Sha256,
        PermanentUrl = link,
        CreatedAt = createdAt ?? fixture.Now.AddMinutes(1)
    };

    private MarketplaceDeliveryState NewState(Seed seed, string status, string? invoiceLink) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = seed.Tenant.Id,
        InvoiceId = seed.Invoice.Id,
        ConnectionId = seed.Marketplace.Id,
        PackageId = seed.Package.Id,
        AttemptNumber = 1,
        ExternalIdempotencyKey = $"delivery:{seed.Invoice.Id:N}:{seed.Package.Id:N}",
        RequestHash = Hash(InvoiceMarketplaceRetryPolicy.DeliveryIdentity(seed.Invoice.Id, seed.Package.ExternalPackageId, seed.Invoice.InvoiceNumber)),
        DeliveryType = "LINK",
        Status = status,
        ExternalReference = seed.Package.ExternalPackageId,
        InvoiceLink = invoiceLink,
        CreatedAt = fixture.Now.AddDays(-1),
        UpdatedAt = fixture.Now.AddDays(-1),
        Version = 1
    };

    private static string Hash(string value) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));

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
        await db.Invoices.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.InvoicePolicies.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.LegalEntityProfiles.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.ShipmentPackages.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.Orders.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.PlatformConnections.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.Tenants.Where(row => row.Id == tenantId).ExecuteDeleteAsync();
    }

    // Reads the package as not invoiced (or as showing the given number and link) and answers a delivery with a rate limit,
    // so the attempt is made, its link recorded and nothing confirmed. Every delivery command is kept.
    private sealed class RecordingMarketplace : IInvoiceMarketplacePort
    {
        public List<InvoiceDeliveryCommand> Deliveries { get; } = [];
        public (string Number, string Link)? Shown { get; init; }

        public Task<AdapterResult<InvoiceDeliveryResult>> DeliverAsync(AdapterContext context, InvoiceDeliveryCommand command, CancellationToken cancellationToken)
        {
            Deliveries.Add(command);
            return Task.FromResult(AdapterResult<InvoiceDeliveryResult>.Failure(new AdapterError(AdapterErrorClass.RateLimit, "RATE_LIMITED", "rate limited", 429, TimeSpan.FromSeconds(30), null)));
        }

        public Task<AdapterResult<InvoiceDeliveryStatus>> QueryDeliveryAsync(AdapterContext context, ExternalInvoiceDeliveryReference reference, CancellationToken cancellationToken) =>
            Task.FromResult(AdapterResult<InvoiceDeliveryStatus>.Success(Shown is { } shown
                ? new(reference.ExternalReference, "Invoiced", true, shown.Number, shown.Link, ReferencesAreAuthoritative: true)
                : new(reference.ExternalReference, "NotInvoiced", true)));
    }
}
