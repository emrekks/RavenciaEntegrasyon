using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// The automatic delivery retry pages through its candidates: issued invoices that need nothing (a delivery skipped on
// purpose, a stored document) never hold back one that needs a retry, and the per-scan cap counts only retries queued.
[Collection("postgres-integration")]
public sealed class InvoiceDeliveryRetryPagingTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    [PostgreSqlTheory]
    [InlineData(49)]
    [InlineData(50)]
    [InlineData(250)]
    public async Task AnInvoiceThatNeedsARetryIsQueuedBehindOnesThatNeedNone(int blockers)
    {
        var queued = await RunRetryScenario(blockers, needyCount: 1);
        Assert.Equal(1, queued);
    }

    [PostgreSqlFact]
    public async Task OneScanQueuesAtMostFiftyRetries()
    {
        var queued = await RunRetryScenario(blockers: 10, needyCount: 60);
        Assert.Equal(50, queued);
    }

    // Seeds older invoices that need no retry and newer ones that need a document fetch, runs one due scan and returns
    // how many document fetch jobs it queued for the latter.
    private async Task<int> RunRetryScenario(int blockers, int needyCount)
    {
        var now = fixture.Now;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"retry-page-{Guid.NewGuid():N}"[..30], DisplayName = "retry-page", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var marketplace = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = "TRENDYOL", Environment = "PRODUCTION", DisplayName = "Trendyol", ExternalStoreId = $"s-{Guid.NewGuid():N}", Status = "ACTIVE", ApiVersion = "V1" };
        var provider = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = "TRENDYOL_EFATURAM", Environment = "PRODUCTION", DisplayName = "E-Faturam", ExternalStoreId = $"e-{Guid.NewGuid():N}", Status = "ACTIVE", ApiVersion = "1.0.0" };
        var profile = new LegalEntityProfile { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Title = "profile", ProtectedTaxId = "protected", MaskedTaxId = "E-Faturam", AddressSnapshotJson = "{}", ContactSnapshotJson = "{}", Status = "ACTIVE", CreatedAt = now, UpdatedAt = now };
        var policy = new InvoicePolicy { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProviderConnectionId = provider.Id, TriggerState = "MANUAL_CONFIRMED", PackageScope = "SHIPMENT_PACKAGE", DueRule = "IMMEDIATE", RoundingRule = "LINE_HALF_AWAY_FROM_ZERO", AdjustmentRule = "REJECT_OVER_ONE_KURUS", CreatedAt = now, UpdatedAt = now };
        var orders = new List<Order>();
        var invoices = new List<Invoice>();
        var assets = new List<FileAsset>();
        var documents = new List<InvoiceDocument>();
        Invoice NewInvoice(int index, DateTimeOffset updatedAt, string? lastError)
        {
            var order = new Order
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenant.Id,
                ConnectionId = marketplace.Id,
                ExternalOrderId = $"retry-page-{index}",
                OrderNumber = $"retry-page-{index}",
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
            orders.Add(order);
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
                Status = InvoiceStatus.Accepted,
                Currency = "TRY",
                TaxExclusiveTotal = 100m,
                TaxTotal = 20m,
                PayableTotal = 120m,
                Note = InvoiceAmounts.TurkishInvoiceNote(120m),
                IdempotencyKey = $"retry-page:{Guid.NewGuid():N}",
                ExternalReference = $"remote-{Guid.NewGuid():N}",
                InvoiceNumber = $"GIB2026{index:000000000}",
                LastErrorCode = lastError,
                IssuedAt = now.AddDays(-3),
                CreatedAt = now.AddDays(-3),
                UpdatedAt = updatedAt,
                Version = 1
            };
            invoices.Add(invoice);
            return invoice;
        }
        // The blockers come first (older, lower ids): delivery skipped on purpose (an INVOICE_AUTO_DELIVERY_* code),
        // document stored, so they need no retry.
        for (var i = 0; i < blockers; i++)
        {
            var invoice = NewInvoice(i, now.AddDays(-2).AddMinutes(i), "INVOICE_AUTO_DELIVERY_SWITCH_OFF");
            var asset = new FileAsset { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Classification = "INVOICE_DOCUMENT", RelativePath = $"retry-page/{i}.pdf", MimeType = "application/pdf", SizeBytes = 10, Sha256 = new string('A', 64), Status = "ACTIVE", CreatedAt = now };
            assets.Add(asset);
            documents.Add(new InvoiceDocument { Id = Guid.CreateVersion7(), TenantId = tenant.Id, InvoiceId = invoice.Id, DocumentType = "PDF", FileAssetId = asset.Id, Sha256 = asset.Sha256, PermanentUrl = $"https://documents.example.test/{i}.pdf", CreatedAt = now });
        }
        // The needy ones: newer, Accepted, their document never stored, so each needs a document fetch.
        var needy = Enumerable.Range(0, needyCount).Select(i => NewInvoice(1000 + i, now.AddHours(-1), null)).ToList();
        try
        {
            await using (var seed = fixture.CreateContext())
            {
                seed.Tenants.Add(tenant);
                seed.PlatformConnections.AddRange(marketplace, provider);
                seed.LegalEntityProfiles.Add(profile);
                seed.InvoicePolicies.Add(policy);
                seed.Orders.AddRange(orders);
                seed.Invoices.AddRange(invoices);
                seed.FileAssets.AddRange(assets);
                seed.InvoiceDocuments.AddRange(documents);
                await seed.SaveChangesAsync();
            }
            // The reader agrees on the need of each kind.
            await using (var db = fixture.CreateContext())
            {
                var entries = await InvoiceDeliveryRetryReader.LoadAsync(db, tenant.Id, invoices.Select(item => item.Id).ToArray(), now, CancellationToken.None);
                Assert.All(needy, item =>
                {
                    Assert.Equal(InvoiceDeliveryRetryNeed.FetchDocument, entries[item.Id].Need);
                    Assert.Equal(InvoiceDeliveryAutoRetryPolicy.Scheduled, entries[item.Id].View.State);
                });
                var needyIds = needy.Select(item => item.Id).ToHashSet();
                Assert.All(invoices.Where(item => !needyIds.Contains(item.Id)), item => Assert.False(entries.TryGetValue(item.Id, out var entry) && entry.Need != InvoiceDeliveryRetryNeed.None));
            }
            await using (var db = fixture.CreateContext())
            {
                var processor = new InvoicingJobProcessor(db, null!, null!, null!, fixture.TimeProvider,
                    new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build(), new EphemeralDataProtectionProvider());
                Assert.True((await processor.ProcessAsync(tenant.Id, null, InvoicingJobTypes.InvoiceDueScan, "{}", "retry-page-scan", CancellationToken.None)).Succeeded);
            }
            await using var verification = fixture.CreateContext();
            var fetchKeys = await verification.IntegrationJobs.AsNoTracking()
                .Where(job => job.TenantId == tenant.Id && job.JobType == InvoicingJobTypes.InvoiceDocumentFetch)
                .Select(job => job.JobDedupKey).ToListAsync();
            return needy.Count(item => fetchKeys.Any(key => key.StartsWith($"{InvoicingJobTypes.InvoiceDocumentFetch}:{item.Id}:", StringComparison.Ordinal)));
        }
        finally
        {
            await using var db = fixture.CreateContext();
            var jobIds = await db.IntegrationJobs.Where(row => row.TenantId == tenant.Id).Select(row => row.Id).ToListAsync();
            await db.JobAttempts.Where(row => row.TenantId == tenant.Id || jobIds.Contains(row.JobId)).ExecuteDeleteAsync();
            await db.IntegrationJobs.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await db.IntegrationOutboxEvents.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await db.AuditLogs.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await db.OperationalIssues.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await db.InvoiceDocuments.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await db.FileAssets.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await db.Invoices.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await db.InvoicePolicies.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await db.LegalEntityProfiles.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await db.Orders.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await db.PlatformConnections.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await db.Tenants.Where(row => row.Id == tenant.Id).ExecuteDeleteAsync();
        }
    }
}
