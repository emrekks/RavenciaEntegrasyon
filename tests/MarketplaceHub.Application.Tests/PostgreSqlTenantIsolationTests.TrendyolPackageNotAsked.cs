using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.Trendyol;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// ADAPTOR-TY-HB-3 (16 Nov 2026): a Trendyol package outside every readable window is not asked for and the adapter says
// so with TRENDYOL_PACKAGE_OUT_OF_READ_WINDOW (class NotFound, 404). The invoice reconciliation took that class as
// "Trendyol removed the package" and closed the read issue, so the unknown state never reached the invoice screen. It now
// records the issue for this code; Trendyol's own "not found" still closes it as before.
public sealed partial class PostgreSqlTenantIsolationTests
{
    [PostgreSqlTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AdaptorTyHb3_APackageNotAskedForStaysOnTheInvoiceScreen(bool notAsked)
    {
        var (tenant, connection, _) = await SeedReadGuardConnectionAsync(notAsked ? "tyhb3-not-asked" : "tyhb3-not-found", "TRENDYOL", "ORDER_INVOICE_RECONCILIATION");
        var orderId = Guid.CreateVersion7();
        var packageId = Guid.CreateVersion7();
        await using (var seedDb = fixture.CreateContext())
        {
            var order = NewInvoiceTestOrder(tenant.Id, connection.Id, orderId, "tyhb3-order", isReturnClaim: false, fixture.Now);
            // Older than Order V2's month and its status time older than the stream's three months: nothing can be asked.
            order.OrderedAt = fixture.Now.AddMonths(-4);
            order.LastRemoteModifiedAt = fixture.Now.AddMonths(-4);
            seedDb.Orders.Add(order);
            var package = NewInvoiceTestPackage(tenant.Id, connection.Id, orderId, packageId, "tyhb3-package", fixture.Now);
            package.StatusOccurredAt = fixture.Now.AddMonths(-4);
            seedDb.ShipmentPackages.Add(package);
            // An earlier read failure of the package: Trendyol's own "not found" closes it, a read not made keeps it open.
            seedDb.OperationalIssues.Add(new OperationalIssue
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenant.Id,
                DedupeKey = $"trendyol-invoice-package-read:{connection.Id}:{packageId}",
                Code = "REMOTE_5XX",
                Summary = "Önceki okuma hatası.",
                Status = IssueStatus.Open,
                FirstSeenAt = fixture.Now.AddHours(-1),
                LastSeenAt = fixture.Now.AddHours(-1)
            });
            await seedDb.SaveChangesAsync();
        }
        var error = notAsked
            ? new AdapterError(AdapterErrorClass.NotFound, TrendyolHttpClient.PackageNotAddressableCode, "Paketin durumu bilinmiyor.", 404, null, null)
            : new AdapterError(AdapterErrorClass.NotFound, "REMOTE_PACKAGE_NOT_FOUND", "Trendyol paket bilgisi bulunamadı.", 404, null, null);
        var port = new FailingPackageReadPort(error);
        try
        {
            var result = await RunReadGuardJobAsync(tenant.Id, connection.Id, MarketplaceJobTypes.OrderInvoiceReconciliation, $$"""{"targetOrderId":"{{orderId}}"}""", orders: port);
            Assert.Equal(JobCompletionKind.Succeeded, result.Kind);
            Assert.Equal(1, port.PackageReads);
            await using var check = fixture.CreateContext();
            var issue = await check.OperationalIssues.AsNoTracking().SingleAsync(x => x.TenantId == tenant.Id && x.DedupeKey == $"trendyol-invoice-package-read:{connection.Id}:{packageId}");
            Assert.Equal(notAsked ? IssueStatus.Open : IssueStatus.Resolved, issue.Status);
            if (notAsked)
            {
                Assert.Equal(TrendyolHttpClient.PackageNotAddressableCode, issue.Code);
                Assert.True(InvoiceWorkspaceReadIssuePolicy.ShouldDisplay(issue.Code));
            }
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

    [Fact]
    public void AdaptorTyHb3_OnlyTheNotAskedCodeIsTakenAsUnknown()
    {
        Assert.True(MarketplaceJobProcessor.TrendyolPackageNotAsked(new AdapterError(AdapterErrorClass.NotFound, TrendyolHttpClient.PackageNotAddressableCode, "x", 404, null, null)));
        Assert.False(MarketplaceJobProcessor.TrendyolPackageNotAsked(new AdapterError(AdapterErrorClass.NotFound, "REMOTE_PACKAGE_NOT_FOUND", "x", 404, null, null)));
        Assert.False(MarketplaceJobProcessor.TrendyolPackageNotAsked(null));
    }

    private sealed class FailingPackageReadPort(AdapterError error) : IOrderPort
    {
        public int PackageReads { get; private set; }

        public Task<AdapterResult<RemoteOrderPackage>> GetShipmentPackageAsync(AdapterContext context, string externalPackageId, PackageLookup lookup, CancellationToken cancellationToken)
        {
            PackageReads++;
            return Task.FromResult(AdapterResult<RemoteOrderPackage>.Failure(error));
        }

        public Task<AdapterResult<RemoteOrder>> GetAsync(AdapterContext context, string externalOrderId, CancellationToken cancellationToken) => throw new NotSupportedException();
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
