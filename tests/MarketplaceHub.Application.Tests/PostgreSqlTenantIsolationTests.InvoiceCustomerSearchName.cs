using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// SIPARIS-STOK-9 (16 Nov 2026): the invoice check writes a claim-built order's customer snapshots outside the change
// tracker; the database trigger empties the customer search name of a snapshot write without a new stamp, so the order
// could not be found by its customer until the hourly backfill. The check now writes the name with a new stamp.
public sealed partial class PostgreSqlTenantIsolationTests
{
    [PostgreSqlFact]
    public async Task SiparisStok9_TheInvoiceCheckKeepsTheCustomerSearchNameOfTheSnapshotsItWrites()
    {
        var (tenant, connection, _) = await SeedReadGuardConnectionAsync("siparis-stok9-name", "TRENDYOL", "ORDER_INVOICE_RECONCILIATION");
        var orderId = Guid.CreateVersion7();
        var packageId = Guid.CreateVersion7();
        await using (var seedDb = fixture.CreateContext())
        {
            var order = NewInvoiceTestOrder(tenant.Id, connection.Id, orderId, "ss9-order", isReturnClaim: true, fixture.Now);
            order.OrderedAt = fixture.Now.AddDays(-2);
            seedDb.Orders.Add(order);
            seedDb.ShipmentPackages.Add(NewInvoiceTestPackage(tenant.Id, connection.Id, orderId, packageId, "ss9-package", fixture.Now));
            await seedDb.SaveChangesAsync();
        }
        var remotePackage = new RemotePackage("ss9-package", null, "Delivered", fixture.Now.AddDays(-1), null, null, [], 100m, 0m, 100m, null, null);
        var snapshot = new RemoteOrder("ss9-order", "ss9-order", fixture.Now.AddDays(-2), fixture.Now.AddDays(-1), "TRY", 100m, 0m, 100m,
            """{"customerFirstName":"Ayşe","customerLastName":"Yılmaz"}""", """{"firstName":"Ayşe","lastName":"Yılmaz","city":"İzmir"}""", "{}", [], [remotePackage], "{}");
        var port = new SnapshotPackagePort(new RemoteOrderPackage("ss9-order", remotePackage, snapshot));
        try
        {
            var result = await RunReadGuardJobAsync(tenant.Id, connection.Id, MarketplaceJobTypes.OrderInvoiceReconciliation, $$"""{"targetOrderId":"{{orderId}}"}""", orders: port);
            Assert.True(result.Succeeded, result.ErrorSummary);
            Assert.Equal(1, port.PackageReads);
            await using var check = fixture.CreateContext();
            var saved = await check.Orders.AsNoTracking().SingleAsync(x => x.Id == orderId);
            Assert.Contains("customerFirstName", saved.CustomerSnapshotJson, StringComparison.Ordinal);
            Assert.Equal("Ayşe Yılmaz", saved.CustomerSearchName);
            Assert.NotNull(saved.CustomerSearchStamp);
        }
        finally
        {
            await using (var cleanupDb = fixture.CreateContext())
            {
                await cleanupDb.ShipmentPackages.Where(x => x.TenantId == tenant.Id).ExecuteDeleteAsync();
                await cleanupDb.Orders.Where(x => x.TenantId == tenant.Id).ExecuteDeleteAsync();
            }
            await DeleteReadGuardTenantAsync(tenant.Id);
        }
    }

    // The order read the hydration asks for fails (the order is left claim-built); the package read brings the customer.
    private sealed class SnapshotPackagePort(RemoteOrderPackage package) : IOrderPort
    {
        public int PackageReads { get; private set; }

        public Task<AdapterResult<RemoteOrderPackage>> GetShipmentPackageAsync(AdapterContext context, string externalPackageId, PackageLookup lookup, CancellationToken cancellationToken)
        {
            PackageReads++;
            return Task.FromResult(AdapterResult<RemoteOrderPackage>.Success(package));
        }

        public Task<AdapterResult<RemoteOrder>> GetAsync(AdapterContext context, string externalOrderId, CancellationToken cancellationToken) =>
            Task.FromResult(AdapterResult<RemoteOrder>.Failure(new AdapterError(AdapterErrorClass.Validation, "TEST_ORDER_UNREAD", "okunamadı", 400, null, null)));
        public Task<AdapterResult<AdapterPageResult<RemoteOrder>>> PollAsync(AdapterContext context, OrderPollWindow window, AdapterPageRequest page, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<IReadOnlyList<RemoteCargoCompany>>> GetChangeableCargoCompaniesAsync(AdapterContext context, string externalPackageId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<IReadOnlyList<RemotePackageableLine>>> GetPackageableLineItemsAsync(AdapterContext context, string externalLineItemId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<PackageActionResult>> ExecutePackageActionAsync(AdapterContext context, PackageActionCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<CreateOrderPackageResult>> CreateOrderPackageAsync(AdapterContext context, CreateOrderPackageCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<bool>> CreateCommonLabelAsync(AdapterContext context, CommonLabelRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<CommonLabelDocument>> GetCommonLabelAsync(AdapterContext context, string cargoTrackingNumber, CancellationToken cancellationToken, string format = "ZPL") => throw new NotSupportedException();
        public Task<AdapterResult<StageTestOrderResult>> CreateStageTestOrderAsync(AdapterContext context, string barcode, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
