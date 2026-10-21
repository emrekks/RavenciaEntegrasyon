using System.Text.Json;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// F5a: the operator removes (Trendyol) or confirms the removal of (Hepsiburada) the link a cancelled invoice left on a package.
[Collection("postgres-integration")]
public sealed class PostgreSqlInvoiceLinkRevocationTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    private static readonly Dictionary<string, string?> OpenWrites = new()
    {
        ["FeatureFlags:ExternalWrites"] = "true",
        ["FeatureFlags:InvoiceMarketplaceDeliveryWrites"] = "true"
    };

    [PostgreSqlFact]
    public async Task TrendyolLinkIsRemovedOnRequest_TheStateEndsRemovedAndTheIssueCloses()
    {
        var seed = await SeedAsync("revoke-trendyol", "TRENDYOL");
        try
        {
            var marketplace = new FakeMarketplace { Revoke = AdapterResult<InvoiceLinkRevocationResult>.Success(new("revoke-trendyol-package", false, "NotInvoiced")) };
            await using (var db = fixture.CreateContext())
            {
                var detail = await NewService(db, marketplace, OpenWrites).GetAsync(seed.TenantId, seed.InvoiceId, CancellationToken.None);
                var pending = Assert.Single(detail.Value!.LinkRevocations!);
                Assert.Equal(MarketplaceInvoiceLinkRevocationPolicy.Automatic, pending.Mode);
                Assert.Equal(MarketplaceInvoiceLinkRevocationPolicy.Required, pending.Status);
                Assert.True(pending.CanAct, pending.BlockedReason);
            }

            await using (var db = fixture.CreateContext())
            {
                var result = await NewService(db, marketplace, OpenWrites).RevokeMarketplaceLinkAsync(seed.TenantId, seed.InvoiceId, seed.StateId, "operator-key-1", "revoke-trendyol", CancellationToken.None);
                Assert.True(result.Succeeded, result.Error?.Message);
                Assert.Equal(MarketplaceInvoiceLinkRevocationPolicy.Removed, result.Value!.Status);
            }

            var command = Assert.Single(marketplace.RevokeCommands);
            Assert.Equal("revoke-trendyol-package", command.Reference.ExternalReference);
            Assert.Equal("GIB2026000000001", command.ExpectedInvoiceNumber);
            Assert.Contains(seed.PermanentUrl, command.ExpectedInvoiceLinks);
            Assert.Contains(InvoiceDocumentLink.PdfAlias(seed.PermanentUrl), command.ExpectedInvoiceLinks);
            Assert.True(marketplace.Contexts.Single().IsExplicitManualInvoiceDelivery);

            await using var verification = fixture.CreateContext();
            Assert.Equal(MarketplaceInvoiceLinkRevocationPolicy.Removed, (await verification.MarketplaceDeliveryStates.SingleAsync(state => state.Id == seed.StateId)).Status);
            var history = await verification.MarketplaceDeliveries.Where(entry => entry.InvoiceId == seed.InvoiceId).OrderBy(entry => entry.AttemptNumber).Select(entry => entry.Status).ToListAsync();
            Assert.Equal(new[] { MarketplaceInvoiceLinkRevocationPolicy.Required, MarketplaceInvoiceLinkRevocationPolicy.Started, MarketplaceInvoiceLinkRevocationPolicy.Removed }, history.TakeLast(3).ToArray());
            var package = await verification.ShipmentPackages.SingleAsync(item => item.Id == seed.PackageId);
            Assert.Equal(MarketplaceInvoiceStatus.NotInvoiced, package.MarketplaceInvoiceStatus);
            Assert.Null(package.MarketplaceInvoiceUrl);
            Assert.True(await verification.MarketplaceInvoiceReferenceHistories.AnyAsync(row => row.PackageId == seed.PackageId && row.ChangeReason == "REMOVED:LINK_REVOCATION"));
            Assert.Equal(IssueStatus.Resolved, (await verification.OperationalIssues.SingleAsync(issue => issue.TenantId == seed.TenantId && issue.DedupeKey == $"invoice-link-revocation:{seed.InvoiceId:N}")).Status);
            // The public link of the cancelled invoice is withdrawn with it: the public route finds no stored URL.
            Assert.Null((await verification.InvoiceDocuments.SingleAsync(document => document.InvoiceId == seed.InvoiceId)).PermanentUrl);

            // A replay after the removal answers with the finished state and sends nothing.
            await using (var db = fixture.CreateContext())
                Assert.True((await NewService(db, marketplace, OpenWrites).RevokeMarketplaceLinkAsync(seed.TenantId, seed.InvoiceId, seed.StateId, "operator-key-1", "revoke-trendyol", CancellationToken.None)).Succeeded);
            Assert.Single(marketplace.RevokeCommands);
        }
        finally
        {
            await DeleteTenantAsync(seed.TenantId);
        }
    }

    [PostgreSqlFact]
    public async Task AnUnansweredDeleteLeavesTheOutcomeUnknownAndARejectedOneGoesBackToRequired()
    {
        var seed = await SeedAsync("revoke-failures", "TRENDYOL");
        try
        {
            var marketplace = new FakeMarketplace { Revoke = AdapterResult<InvoiceLinkRevocationResult>.Failure(new(AdapterErrorClass.TransientNetwork, "REMOTE_TIMEOUT", "Zaman aşımı.", null, null, null)) };
            await using (var db = fixture.CreateContext())
            {
                var timedOut = await NewService(db, marketplace, OpenWrites).RevokeMarketplaceLinkAsync(seed.TenantId, seed.InvoiceId, seed.StateId, "key-1", "revoke-failures", CancellationToken.None);
                Assert.Equal("REMOTE_TIMEOUT", timedOut.Error?.Code);
                Assert.Equal(502, timedOut.Error?.Status);
            }
            await using (var db = fixture.CreateContext())
                Assert.Equal(MarketplaceInvoiceLinkRevocationPolicy.Unknown, (await db.MarketplaceDeliveryStates.SingleAsync(state => state.Id == seed.StateId)).Status);

            marketplace.Revoke = AdapterResult<InvoiceLinkRevocationResult>.Failure(new(AdapterErrorClass.BusinessConflict, MarketplaceInvoiceLinkRevocationPolicy.NotOursCode, "Başka fatura.", 409, null, null));
            await using (var db = fixture.CreateContext())
            {
                var refused = await NewService(db, marketplace, OpenWrites).RevokeMarketplaceLinkAsync(seed.TenantId, seed.InvoiceId, seed.StateId, "key-2", "revoke-failures", CancellationToken.None);
                Assert.Equal(MarketplaceInvoiceLinkRevocationPolicy.NotOursCode, refused.Error?.Code);
                Assert.Equal(409, refused.Error?.Status);
                Assert.Contains("Trendyol Satıcı Paneli", refused.Error!.Message, StringComparison.Ordinal);
            }

            await using var verification = fixture.CreateContext();
            var state = await verification.MarketplaceDeliveryStates.SingleAsync(item => item.Id == seed.StateId);
            Assert.Equal(MarketplaceInvoiceLinkRevocationPolicy.Required, state.Status);
            Assert.Equal(MarketplaceInvoiceLinkRevocationPolicy.NotOursCode, state.ErrorCode);
            Assert.Equal(IssueStatus.Open, (await verification.OperationalIssues.SingleAsync(issue => issue.TenantId == seed.TenantId && issue.DedupeKey == $"invoice-link-revocation:{seed.InvoiceId:N}")).Status);
            Assert.Equal(2, marketplace.RevokeCommands.Count);
        }
        finally
        {
            await DeleteTenantAsync(seed.TenantId);
        }
    }

    [PostgreSqlFact]
    public async Task ClosedWriteSwitchesSendNothingAndKeepTheState()
    {
        var seed = await SeedAsync("revoke-closed", "TRENDYOL");
        try
        {
            var marketplace = new FakeMarketplace();
            await using (var db = fixture.CreateContext())
            {
                var view = Assert.Single((await NewService(db, marketplace, new Dictionary<string, string?>()).GetAsync(seed.TenantId, seed.InvoiceId, CancellationToken.None)).Value!.LinkRevocations!);
                Assert.False(view.CanAct);
                Assert.False(string.IsNullOrWhiteSpace(view.BlockedReason));

                var result = await NewService(db, marketplace, new Dictionary<string, string?>()).RevokeMarketplaceLinkAsync(seed.TenantId, seed.InvoiceId, seed.StateId, "key", "revoke-closed", CancellationToken.None);
                Assert.Equal("INVOICE_LINK_REVOCATION_WRITE_CLOSED", result.Error?.Code);
            }

            Assert.Empty(marketplace.RevokeCommands);
            await using var verification = fixture.CreateContext();
            Assert.Equal(MarketplaceInvoiceLinkRevocationPolicy.Required, (await verification.MarketplaceDeliveryStates.SingleAsync(state => state.Id == seed.StateId)).Status);
        }
        finally
        {
            await DeleteTenantAsync(seed.TenantId);
        }
    }

    [PostgreSqlFact]
    public async Task HepsiburadaIsOnlyCheckedAfterTheOperatorRemovedTheLinkInTheSellerPanel()
    {
        var seed = await SeedAsync("revoke-hepsiburada", "HEPSIBURADA");
        try
        {
            var marketplace = new FakeMarketplace { Status = AdapterResult<InvoiceDeliveryStatus>.Success(new("revoke-hepsiburada-package", "INVOICED", true)) };
            await using (var db = fixture.CreateContext())
            {
                var stillThere = await NewService(db, marketplace, new Dictionary<string, string?>()).RevokeMarketplaceLinkAsync(seed.TenantId, seed.InvoiceId, seed.StateId, "key-1", "revoke-hb", CancellationToken.None);
                Assert.Equal("INVOICE_LINK_STILL_PRESENT", stillThere.Error?.Code);
                Assert.Contains("Hepsiburada Satıcı Paneli", stillThere.Error!.Message, StringComparison.Ordinal);
            }
            // While the package still shows the invoice, the link stays: the next check names it to the marketplace.
            await using (var db = fixture.CreateContext())
                Assert.Equal(seed.PermanentUrl, (await db.InvoiceDocuments.SingleAsync(document => document.InvoiceId == seed.InvoiceId)).PermanentUrl);

            marketplace.Status = AdapterResult<InvoiceDeliveryStatus>.Success(new("revoke-hepsiburada-package", "NOT_INVOICED", true));
            await using (var db = fixture.CreateContext())
                Assert.True((await NewService(db, marketplace, new Dictionary<string, string?>()).RevokeMarketplaceLinkAsync(seed.TenantId, seed.InvoiceId, seed.StateId, "key-2", "revoke-hb", CancellationToken.None)).Succeeded);

            Assert.Empty(marketplace.RevokeCommands);
            Assert.Equal(2, marketplace.QueryCount);
            await using var verification = fixture.CreateContext();
            Assert.Equal(MarketplaceInvoiceLinkRevocationPolicy.Removed, (await verification.MarketplaceDeliveryStates.SingleAsync(state => state.Id == seed.StateId)).Status);
            Assert.Null((await verification.InvoiceDocuments.SingleAsync(document => document.InvoiceId == seed.InvoiceId)).PermanentUrl);
        }
        finally
        {
            await DeleteTenantAsync(seed.TenantId);
        }
    }

    // Only links served by our public route are withdrawn; a provider's own document address is not ours to remove.
    [PostgreSqlFact]
    public async Task AProviderDocumentAddressIsKeptWhenTheLinkIsRemoved()
    {
        var seed = await SeedAsync("revoke-provider-url", "TRENDYOL");
        const string providerUrl = "https://efaturam.example.test/documents/revoke-provider-url.pdf";
        try
        {
            await using (var db = fixture.CreateContext())
                await db.InvoiceDocuments.Where(document => document.InvoiceId == seed.InvoiceId).ExecuteUpdateAsync(setters => setters.SetProperty(document => document.PermanentUrl, providerUrl));
            var marketplace = new FakeMarketplace { Revoke = AdapterResult<InvoiceLinkRevocationResult>.Success(new("revoke-provider-url-package", false, "NotInvoiced")) };
            await using (var db = fixture.CreateContext())
                Assert.True((await NewService(db, marketplace, OpenWrites).RevokeMarketplaceLinkAsync(seed.TenantId, seed.InvoiceId, seed.StateId, "key", "revoke-provider-url", CancellationToken.None)).Succeeded);

            await using var verification = fixture.CreateContext();
            Assert.Equal(MarketplaceInvoiceLinkRevocationPolicy.Removed, (await verification.MarketplaceDeliveryStates.SingleAsync(state => state.Id == seed.StateId)).Status);
            Assert.Equal(providerUrl, (await verification.InvoiceDocuments.SingleAsync(document => document.InvoiceId == seed.InvoiceId)).PermanentUrl);
        }
        finally
        {
            await DeleteTenantAsync(seed.TenantId);
        }
    }

    [PostgreSqlFact]
    public async Task AMarketplaceWithoutADeleteServiceOnlyGetsInstructions()
    {
        var seed = await SeedAsync("revoke-n11", "N11");
        try
        {
            var marketplace = new FakeMarketplace();
            await using var db = fixture.CreateContext();
            var service = NewService(db, marketplace, OpenWrites);
            var view = Assert.Single((await service.GetAsync(seed.TenantId, seed.InvoiceId, CancellationToken.None)).Value!.LinkRevocations!);
            Assert.Equal(MarketplaceInvoiceLinkRevocationPolicy.Manual, view.Mode);
            Assert.False(view.CanAct);
            Assert.Contains("n11", view.Instructions, StringComparison.Ordinal);

            var result = await service.RevokeMarketplaceLinkAsync(seed.TenantId, seed.InvoiceId, seed.StateId, "key", "revoke-n11", CancellationToken.None);
            Assert.Equal(MarketplaceInvoiceLinkRevocationPolicy.UnsupportedCode, result.Error?.Code);
            Assert.Equal(422, result.Error?.Status);
            Assert.Empty(marketplace.RevokeCommands);
            Assert.Equal(0, marketplace.QueryCount);
        }
        finally
        {
            await DeleteTenantAsync(seed.TenantId);
        }
    }

    private InvoicingBillingService NewService(AppDbContext db, IInvoiceMarketplacePort marketplace, IDictionary<string, string?> configuration)
    {
        var dataProtection = new EphemeralDataProtectionProvider();
        return new InvoicingBillingService(db, new CursorCodec(dataProtection, fixture.TimeProvider), dataProtection, null!,
            new ConfigurationBuilder().AddInMemoryCollection(configuration).Build(), fixture.TimeProvider, null!, marketplace);
    }

    private sealed record Seeded(Guid TenantId, Guid InvoiceId, Guid PackageId, Guid StateId, string PermanentUrl);

    // A cancelled (replaced) sale invoice whose link was confirmed on the package, then marked for removal the way
    // the reconcile job and "Portalda İptal Ettim" do it.
    private async Task<Seeded> SeedAsync(string code, string platformCode)
    {
        var now = fixture.Now;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"{code}-{Guid.NewGuid():N}", DisplayName = code, Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        PlatformConnection Connection(string platform) => new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            PublicId = Guid.CreateVersion7(),
            PlatformCode = platform,
            Environment = "PRODUCTION",
            DisplayName = $"{platform} test",
            ExternalStoreId = $"{code}-store",
            Status = "ACTIVE",
            ApiVersion = "V1",
            SettingsJson = JsonSerializer.Serialize(new { ExternalWritesEnabled = true })
        };
        var marketplace = Connection(platformCode);
        var provider = Connection("TRENDYOL_EFATURAM");
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
            LastRemoteModifiedAt = now.AddDays(-1),
            CustomerSnapshotJson = "{}",
            ShipmentAddressSnapshotJson = "{}",
            InvoiceAddressSnapshotJson = "{}",
            DerivedStatus = "DELIVERED",
            CreatedAt = now,
            UpdatedAt = now,
            Version = 1
        };
        var permanentUrl = $"https://panel.example.test/api/v1/public/invoice-documents/{code}/content";
        var package = new ShipmentPackage
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            ConnectionId = marketplace.Id,
            OrderId = order.Id,
            ExternalPackageId = $"{code}-package",
            GrossAmount = 120m,
            NetAmount = 120m,
            Status = ShipmentPackageStatus.Delivered,
            RawStatus = "Delivered",
            StatusOccurredAt = now.AddDays(-1),
            MarketplaceInvoiceStatus = MarketplaceInvoiceStatus.Invoiced,
            MarketplaceInvoiceRawStatus = "Invoiced",
            MarketplaceInvoiceUrl = permanentUrl,
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
            PackageId = package.Id,
            ProviderConnectionId = provider.Id,
            LegalEntityProfileId = profile.Id,
            InvoicePolicyId = policy.Id,
            InvoiceType = "EARSIVFATURA",
            SequencePurpose = "REPLACED",
            Status = InvoiceStatus.Cancelled,
            Currency = "TRY",
            TaxExclusiveTotal = 100m,
            TaxTotal = 20m,
            PayableTotal = 120m,
            Note = InvoiceAmounts.TurkishInvoiceNote(120m),
            IdempotencyKey = $"replaced:{Guid.NewGuid():N}",
            ExternalReference = $"remote-{Guid.NewGuid():N}",
            InvoiceNumber = "GIB2026000000001",
            IssuedAt = now.AddDays(-2),
            CreatedAt = now.AddDays(-2),
            UpdatedAt = now.AddDays(-1)
        };
        var asset = new FileAsset { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Classification = "INVOICE_DOCUMENT", RelativePath = $"{code}/invoice.pdf", MimeType = "application/pdf", SizeBytes = 10, Sha256 = new string('A', 64), Status = "ACTIVE", CreatedAt = now };
        var document = new InvoiceDocument { Id = Guid.CreateVersion7(), TenantId = tenant.Id, InvoiceId = invoice.Id, DocumentType = "PDF", FileAssetId = asset.Id, Sha256 = asset.Sha256, PermanentUrl = permanentUrl, CreatedAt = now };
        var state = new MarketplaceDeliveryState
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            InvoiceId = invoice.Id,
            ConnectionId = marketplace.Id,
            PackageId = package.Id,
            AttemptNumber = 1,
            ExternalIdempotencyKey = $"delivery:{invoice.Id:N}:{package.Id:N}",
            RequestHash = "hash",
            DeliveryType = "LINK",
            Status = "CONFIRMED",
            ExternalReference = package.ExternalPackageId,
            CreatedAt = now.AddDays(-2),
            UpdatedAt = now.AddDays(-2),
            CompletedAt = now.AddDays(-2)
        };

        await using (var db = fixture.CreateContext())
        {
            db.Tenants.Add(tenant);
            db.PlatformConnections.AddRange(marketplace, provider);
            db.Orders.Add(order);
            db.ShipmentPackages.Add(package);
            db.LegalEntityProfiles.Add(profile);
            db.InvoicePolicies.Add(policy);
            db.Invoices.Add(invoice);
            db.FileAssets.Add(asset);
            db.InvoiceDocuments.Add(document);
            db.MarketplaceDeliveryStates.Add(state);
            await db.SaveChangesAsync();
        }
        await using (var db = fixture.CreateContext())
        {
            var tracked = await db.Invoices.SingleAsync(item => item.Id == invoice.Id);
            Assert.Equal(1, await InvoiceAdjustmentTracking.MarkLinkRevocationRequiredAsync(db, tracked, now, CancellationToken.None));
            await db.SaveChangesAsync();
        }
        return new(tenant.Id, invoice.Id, package.Id, state.Id, permanentUrl);
    }

    private async Task DeleteTenantAsync(Guid tenantId)
    {
        await using var db = fixture.CreateContext();
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
        await db.IntegrationOutboxEvents.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.Tenants.Where(row => row.Id == tenantId).ExecuteDeleteAsync();
    }

    private sealed class FakeMarketplace : IInvoiceMarketplacePort
    {
        public AdapterResult<InvoiceLinkRevocationResult>? Revoke { get; set; }
        public AdapterResult<InvoiceDeliveryStatus>? Status { get; set; }
        public List<InvoiceLinkRevocationCommand> RevokeCommands { get; } = [];
        public List<AdapterContext> Contexts { get; } = [];
        public int QueryCount { get; private set; }

        public Task<AdapterResult<InvoiceDeliveryResult>> DeliverAsync(AdapterContext context, InvoiceDeliveryCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<AdapterResult<InvoiceDeliveryStatus>> QueryDeliveryAsync(AdapterContext context, ExternalInvoiceDeliveryReference reference, CancellationToken cancellationToken)
        {
            QueryCount++;
            return Task.FromResult(Status ?? throw new InvalidOperationException("No status scripted."));
        }

        public Task<AdapterResult<InvoiceLinkRevocationResult>> RevokeLinkAsync(AdapterContext context, InvoiceLinkRevocationCommand command, CancellationToken cancellationToken)
        {
            RevokeCommands.Add(command);
            Contexts.Add(context);
            return Task.FromResult(Revoke ?? throw new InvalidOperationException("No removal scripted."));
        }
    }
}
