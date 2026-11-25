using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Scan 25 Nov 2026 (A-3): the Trendyol invoice check read the orders whose sale invoice was not Completed on every run,
// first 50 by id and without a cursor. Invoices that never change (Cancelled, Rejected, Draft, ManualReview…) filled
// those 50 for good: newer invoices were never confirmed and the six-hourly sweep never had room. Only invoices a read
// can still settle are taken now, and they rotate on a cursor that wraps at the end.
public sealed partial class PostgreSqlTenantIsolationTests
{
    [PostgreSqlFact]
    public async Task TrendyolInvoiceCheck_SkipsInvoicesThatNeverChange_AndLeavesTheSweepRoom()
    {
        var seed = await SeedUnresolvedInvoiceOrdersAsync("a3-stuck", stuck: 60, waiting: 5);
        var orderPort = NotInvoicedPackagePort();
        try
        {
            await RunTrendyolInvoiceCheckAsync(seed.Tenant.Id, seed.Connection.Id, orderPort, "{}", "a3-stuck-run");

            // The five waiting invoices are read; the 60 cancelled ones are not.
            Assert.Equal(seed.WaitingPackages, orderPort.PackageReadCalls);
            await using var verifyDb = fixture.CreateContext();
            var sweep = await verifyDb.SyncCursors.AsNoTracking().SingleAsync(cursor => cursor.TenantId == seed.Tenant.Id
                && cursor.ConnectionId == seed.Connection.Id
                && cursor.ResourceType == TrendyolInvoiceReconciliationPolicy.SweepCursorResource);
            // The sweep had room in the batch and finished (nothing of it was left uninvoiced).
            Assert.NotNull(sweep.LastSuccessAt);
        }
        finally
        {
            await DeleteUnresolvedInvoiceTestTenantAsync(seed.Tenant.Id);
        }
    }

    [PostgreSqlFact]
    public async Task TrendyolInvoiceCheck_WaitingInvoicesRotateOnACursorAndWrap()
    {
        var seed = await SeedUnresolvedInvoiceOrdersAsync("a3-cursor", stuck: 0, waiting: 5);
        var orderPort = NotInvoicedPackagePort();
        try
        {
            await RunTrendyolInvoiceCheckAsync(seed.Tenant.Id, seed.Connection.Id, orderPort, """{"batchSize":3}""", "a3-cursor-run-1");
            Assert.Equal(seed.WaitingPackages[..3], orderPort.PackageReadCalls);

            // The second run continues after the third order and wraps to the first.
            await RunTrendyolInvoiceCheckAsync(seed.Tenant.Id, seed.Connection.Id, orderPort, """{"batchSize":3}""", "a3-cursor-run-2");
            Assert.Equal([seed.WaitingPackages[3], seed.WaitingPackages[4], seed.WaitingPackages[0]], orderPort.PackageReadCalls.Skip(3));

            await using var verifyDb = fixture.CreateContext();
            var cursor = await verifyDb.SyncCursors.AsNoTracking().SingleAsync(row => row.TenantId == seed.Tenant.Id
                && row.ConnectionId == seed.Connection.Id
                && row.ResourceType == MarketplaceJobTypes.OrderInvoiceReconciliation);
            Assert.Equal(OrderInvoiceReconciliationBatchPolicy.WriteCursor(seed.WaitingOrderIds[0]), cursor.OpaqueCursor);
        }
        finally
        {
            await DeleteUnresolvedInvoiceTestTenantAsync(seed.Tenant.Id);
        }
    }

    private sealed record UnresolvedInvoiceSeed(Tenant Tenant, PlatformConnection Connection, Guid[] WaitingOrderIds, string[] WaitingPackages);

    private ReadOnlyOrderTestPort NotInvoicedPackagePort() => new(new RemotePackage(
        "a3-package", null, "Delivered", fixture.Now, null, null, [],
        GrossAmount: 100m, NetAmount: 100m,
        Invoice: new RemotePackageInvoiceObservation("NotInvoiced", null, null, fixture.Now)));

    private async Task RunTrendyolInvoiceCheckAsync(Guid tenantId, Guid connectionId, ReadOnlyOrderTestPort orderPort, string payloadJson, string correlationId)
    {
        await using var processorDb = fixture.CreateContext();
        var processor = new MarketplaceJobProcessor(
            processorDb,
            null!, null!, null!, null!, orderPort, null!, null!, null!, null!, null!,
            new ConfigurationBuilder().Build(), fixture.TimeProvider);
        var result = await processor.ProcessAsync(tenantId, connectionId, MarketplaceJobTypes.OrderInvoiceReconciliation, payloadJson, correlationId, CancellationToken.None);
        Assert.True(result.Succeeded, result.ErrorSummary);
    }

    // Stuck orders have lower ids than the waiting ones, so an id-ordered first batch is all stuck. Their packages read
    // as invoiced on the platform, so the sweep has nothing of them to read.
    private async Task<UnresolvedInvoiceSeed> SeedUnresolvedInvoiceOrdersAsync(string code, int stuck, int waiting)
    {
        var tenant = NewTenant(code);
        var connection = NewQuestionConnection(tenant);
        connection.PlatformCode = "TRENDYOL";
        var provider = NewQuestionConnection(tenant);
        provider.PlatformCode = "TRENDYOL_EFATURAM";
        provider.ExternalStoreId = $"{code}-efaturam";
        var now = fixture.Now;
        var profile = new LegalEntityProfile { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Title = "profile", ProtectedTaxId = "protected", MaskedTaxId = "E-Faturam", AddressSnapshotJson = "{}", ContactSnapshotJson = "{}", Status = "ACTIVE", CreatedAt = now, UpdatedAt = now };
        var policy = new InvoicePolicy { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProviderConnectionId = provider.Id, TriggerState = "MANUAL_CONFIRMED", PackageScope = "SHIPMENT_PACKAGE", DueRule = "IMMEDIATE", RoundingRule = "LINE_HALF_AWAY_FROM_ZERO", AdjustmentRule = "REJECT_OVER_ONE_KURUS", CreatedAt = now, UpdatedAt = now };
        InvoiceStatus[] neverChanging = [InvoiceStatus.Cancelled, InvoiceStatus.CancelledLocal, InvoiceStatus.Rejected, InvoiceStatus.ValidationFailed, InvoiceStatus.Draft, InvoiceStatus.ManualReview, InvoiceStatus.AdjustmentRequired];
        var orders = new List<Order>();
        var packages = new List<ShipmentPackage>();
        var invoices = new List<Invoice>();
        var waitingIds = new List<Guid>();
        var waitingPackages = new List<string>();
        var prefix = Guid.CreateVersion7().ToString("N")[..8];
        for (var index = 0; index < stuck + waiting; index++)
        {
            var isStuck = index < stuck;
            var orderId = Guid.Parse($"{prefix}-1c00-7000-8000-{index + 1:D12}");
            var order = NewInvoiceTestOrder(tenant.Id, connection.Id, orderId, $"{code}-order-{index}", isReturnClaim: false, now);
            var package = NewInvoiceTestPackage(tenant.Id, connection.Id, orderId, Guid.CreateVersion7(), $"{code}-package-{index}", now);
            if (isStuck) package.MarketplaceInvoiceStatus = MarketplaceInvoiceStatus.Invoiced;
            else { waitingIds.Add(orderId); waitingPackages.Add(package.ExternalPackageId); }
            orders.Add(order);
            packages.Add(package);
            invoices.Add(new Invoice
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenant.Id,
                OrderId = orderId,
                ProviderConnectionId = provider.Id,
                LegalEntityProfileId = profile.Id,
                InvoicePolicyId = policy.Id,
                InvoiceType = "EARSIVFATURA",
                SequencePurpose = "SALE",
                Status = isStuck ? neverChanging[index % neverChanging.Length] : InvoiceStatus.MarketplacePending,
                Currency = "TRY",
                TaxExclusiveTotal = 100m,
                TaxTotal = 20m,
                PayableTotal = 120m,
                Note = InvoiceAmounts.TurkishInvoiceNote(120m),
                IdempotencyKey = $"{code}:{index}:{Guid.NewGuid():N}",
                CreatedAt = now,
                UpdatedAt = now,
                Version = 1
            });
        }

        await using (var seedDb = fixture.CreateContext())
        {
            seedDb.Tenants.Add(tenant);
            seedDb.PlatformConnections.AddRange(connection, provider);
            seedDb.LegalEntityProfiles.Add(profile);
            seedDb.InvoicePolicies.Add(policy);
            seedDb.Orders.AddRange(orders);
            seedDb.ShipmentPackages.AddRange(packages);
            seedDb.Invoices.AddRange(invoices);
            await seedDb.SaveChangesAsync();
        }
        return new(tenant, connection, [.. waitingIds], [.. waitingPackages]);
    }

    private async Task DeleteUnresolvedInvoiceTestTenantAsync(Guid tenantId)
    {
        await using (var db = fixture.CreateContext())
        {
            await db.MarketplaceInvoiceReferenceHistories.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
            await db.MarketplaceDeliveries.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
            await db.MarketplaceDeliveryStates.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
            await db.Invoices.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
            await db.InvoicePolicies.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
            await db.LegalEntityProfiles.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
            await db.IntegrationJobs.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        }
        await DeleteInvoiceTestTenantAsync(tenantId);
    }
}
