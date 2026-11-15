using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// ISLEYICI-5 (16 Nov 2026): the open-order status scan of Shopify and n11 (and of Pazarama and PttAVM when their window
// read falls through) took the least lately read orders first. An order the marketplace no longer finds is never updated,
// so 25 of them kept the head of the list and the orders behind them were not read again. The scan now walks the open
// orders by order id with the position kept in the ORDER_LIFECYCLE cursor, and an order not found opens an issue.
public sealed partial class PostgreSqlTenantIsolationTests
{
    [PostgreSqlFact]
    public async Task Isleyici5_OpenOrdersTheMarketplaceDoesNotFindDoNotBlockTheOthers()
    {
        var (tenant, connection, _) = await SeedReadGuardConnectionAsync("isleyici5-keyset", "SHOPIFY", "ORDER_LIFECYCLE");
        await using (var seedDb = fixture.CreateContext())
        {
            for (var index = 0; index < 30; index++)
            {
                var orderId = Guid.CreateVersion7();
                seedDb.Orders.Add(NewInvoiceTestOrder(tenant.Id, connection.Id, orderId, $"k-{index:00}", isReturnClaim: false, fixture.Now));
                var package = NewInvoiceTestPackage(tenant.Id, connection.Id, orderId, Guid.CreateVersion7(), $"k-package-{index:00}", fixture.Now);
                package.Status = ShipmentPackageStatus.New;
                package.RawStatus = "New";
                seedDb.ShipmentPackages.Add(package);
            }
            await seedDb.SaveChangesAsync();
        }
        var port = new KeysetLifecycleOrderPort(missingBelow: 25);
        try
        {
            var first = await RunReadGuardJobAsync(tenant.Id, connection.Id, MarketplaceJobTypes.ShopifyOrderStatusSync, orders: port);
            Assert.Equal(JobCompletionKind.Succeeded, first.Kind);
            Assert.Equal(Enumerable.Range(0, 25).Select(index => $"k-{index:00}"), port.Requested);

            port.Requested.Clear();
            await RunReadGuardJobAsync(tenant.Id, connection.Id, MarketplaceJobTypes.ShopifyOrderStatusSync, orders: port);
            Assert.Equal(Enumerable.Range(25, 5).Select(index => $"k-{index:00}"), port.Requested);

            // The list ended, so the next run starts again from the first order.
            port.Requested.Clear();
            await RunReadGuardJobAsync(tenant.Id, connection.Id, MarketplaceJobTypes.ShopifyOrderStatusSync, orders: port);
            Assert.Equal("k-00", port.Requested[0]);

            await using var check = fixture.CreateContext();
            Assert.Equal(25, await check.OperationalIssues.CountAsync(x => x.TenantId == tenant.Id && x.Code == "ORDER_NOT_FOUND_AT_MARKETPLACE" && x.ResolvedAt == null));
            // A missing answer leaves the order's state as it was.
            Assert.All(await check.ShipmentPackages.AsNoTracking().Where(x => x.TenantId == tenant.Id).ToListAsync(), package => Assert.Equal(ShipmentPackageStatus.New, package.Status));
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

    // Orders numbered below `missingBelow` are not found; the others answer with an error the scan records and moves past.
    private sealed class KeysetLifecycleOrderPort(int missingBelow) : IOrderPort
    {
        public List<string> Requested { get; } = [];

        public Task<AdapterResult<RemoteOrder>> GetAsync(AdapterContext context, string externalOrderId, CancellationToken cancellationToken)
        {
            Requested.Add(externalOrderId);
            var number = int.Parse(externalOrderId[2..], System.Globalization.CultureInfo.InvariantCulture);
            var error = number < missingBelow
                ? new AdapterError(AdapterErrorClass.NotFound, "NOT_FOUND", "Sipariş bulunamadı.", 404, null, null)
                : new AdapterError(AdapterErrorClass.Validation, "TEST_UNREADABLE", "Okunamadı.", 400, null, null);
            return Task.FromResult(AdapterResult<RemoteOrder>.Failure(error));
        }

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
