using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// ISLEYICI-11 (15 Nov 2026) characterization of Pazarama's order page walk: a window is read to its last page; a list
// that never ends is closed at PazaramaOrderPageCap pages with a PAZARAMA_ORDER_PAGE_CAP issue. J3 (15 Nov 2026): the run
// then stops for review with the same code and no longer moves its watermark past the unread rest of the window.
public sealed partial class PostgreSqlTenantIsolationTests
{
    [PostgreSqlTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PazaramaOrderPages_AWindowIsReadToItsEndOrStopsTheRunAtTheCap(bool endless)
    {
        var tenant = NewTenant(endless ? "pazarama-pages-cap" : "pazarama-pages-end");
        var connection = NewQuestionConnection(tenant);
        connection.PlatformCode = "PAZARAMA";
        connection.Environment = "PRODUCTION";
        var watermark = DateTimeOffset.FromUnixTimeSeconds(fixture.Now.AddHours(-1).ToUnixTimeSeconds());
        await using (var seedDb = fixture.CreateContext())
        {
            seedDb.Tenants.Add(tenant);
            seedDb.PlatformConnections.Add(connection);
            seedDb.SyncCursors.Add(new SyncCursor { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, ResourceType = "ORDERS_HOT", LastModifiedWatermark = watermark, Version = 1 });
            await seedDb.SaveChangesAsync();
        }
        var port = new PagingOrderPort(endless ? int.MaxValue : 3);
        try
        {
            await using (var db = fixture.CreateContext())
            {
                var processor = new MarketplaceJobProcessor(db, null!, null!, null!, null!, port, null!, null!, null!, null!, null!, new ConfigurationBuilder().Build(), fixture.TimeProvider);
                var result = await processor.ProcessAsync(tenant.Id, connection.Id, MarketplaceJobTypes.PazaramaOrderSync, "{}", "pazarama-pages", CancellationToken.None);
                Assert.Equal(endless ? JobCompletionKind.ManualReview : JobCompletionKind.Succeeded, result.Kind);
                Assert.Equal(endless ? "PAZARAMA_ORDER_PAGE_CAP" : null, result.ErrorCode);
            }
            var windows = port.Calls.Select(call => call.Window).Distinct().Count();
            Assert.True(windows >= 1);
            // The run stops in the first window that reaches the cap.
            if (endless) Assert.Equal(1, windows);
            // Each window starts at page one (no cursor) and follows the cursor each page gives.
            foreach (var window in port.Calls.GroupBy(call => call.Window))
            {
                var cursors = window.Select(call => call.Cursor).ToList();
                Assert.Null(cursors[0]);
                Assert.Equal(endless ? OrderReadService.PazaramaOrderPageCap : 3, cursors.Count);
                Assert.Equal(Enumerable.Range(2, cursors.Count - 1).Select(page => (string?)page.ToString(System.Globalization.CultureInfo.InvariantCulture)), cursors.Skip(1));
            }
            await using var check = fixture.CreateContext();
            var issues = await check.OperationalIssues.AsNoTracking().Where(x => x.TenantId == tenant.Id).Select(x => x.Code).ToListAsync();
            Assert.Equal(endless ? Enumerable.Repeat("PAZARAMA_ORDER_PAGE_CAP", windows).ToList() : [], issues);
            var cursor = await check.SyncCursors.AsNoTracking().SingleAsync(x => x.TenantId == tenant.Id && x.ResourceType == "ORDERS_HOT");
            if (endless) Assert.Equal(watermark, cursor.LastModifiedWatermark);
            else Assert.True(cursor.LastModifiedWatermark > watermark);
        }
        finally
        {
            await using var cleanupDb = fixture.CreateContext();
            await cleanupDb.OperationalIssues.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await cleanupDb.SyncCursors.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await DeleteQuestionSyncTestTenantAsync(tenant.Id);
        }
    }

    // Empty pages numbered 1, 2, 3, ... ; the page `lastPage` says there is no next one.
    private sealed class PagingOrderPort(int lastPage) : IOrderPort
    {
        public List<(DateTimeOffset? Window, string? Cursor)> Calls { get; } = [];

        public Task<AdapterResult<AdapterPageResult<RemoteOrder>>> PollAsync(AdapterContext context, OrderPollWindow window, AdapterPageRequest page, CancellationToken cancellationToken)
        {
            Calls.Add((window.ModifiedAfter, page.Cursor));
            var number = page.Cursor is null ? 1 : int.Parse(page.Cursor, System.Globalization.CultureInfo.InvariantCulture);
            var more = number < lastPage;
            return Task.FromResult(AdapterResult<AdapterPageResult<RemoteOrder>>.Success(new AdapterPageResult<RemoteOrder>([], more ? (number + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) : null, more)));
        }

        public Task<AdapterResult<RemoteOrder>> GetAsync(AdapterContext context, string externalOrderId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<IReadOnlyList<RemoteCargoCompany>>> GetChangeableCargoCompaniesAsync(AdapterContext context, string externalPackageId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<IReadOnlyList<RemotePackageableLine>>> GetPackageableLineItemsAsync(AdapterContext context, string externalLineItemId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<PackageActionResult>> ExecutePackageActionAsync(AdapterContext context, PackageActionCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<CreateOrderPackageResult>> CreateOrderPackageAsync(AdapterContext context, CreateOrderPackageCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<bool>> CreateCommonLabelAsync(AdapterContext context, CommonLabelRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<CommonLabelDocument>> GetCommonLabelAsync(AdapterContext context, string cargoTrackingNumber, CancellationToken cancellationToken, string format = "ZPL") => throw new NotSupportedException();
        public Task<AdapterResult<StageTestOrderResult>> CreateStageTestOrderAsync(AdapterContext context, string barcode, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
