using System.Net;
using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.PttAvm;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Audit of 14 Nov 2026 (operations), against PostgreSQL: refused new orders are read again until imported, and a return
// line's "not linked" notice closes once the line is linked.
[Collection("postgres-integration")]
public sealed class AuditOpsFixes20261114PostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    // Live, 14 Nov 2026: PTT-1C2OSLF2R-120926 was refused at its first import (a cancelled line made the package check
    // fail) and PttAVM's two-day reads never brought it back. After the fix an order read of this connection reads it
    // again by number from its open notice, imports it and closes the notice; a window read of an order with a
    // cancelled line imports it too.
    [PostgreSqlFact]
    public async Task ThePttAvmOrderRefusedAtItsImportIsReadAgainFromItsNoticeAndImported()
    {
        var (tenant, connection) = await SeedAsync("PTTAVM");
        const string lost = "PTT-1C2OSLF2R-120926";
        var notice = $"package-quantity:{connection.Id}:order:{lost}:1789229541000";
        await using (var db = fixture.CreateContext())
        {
            db.OperationalIssues.Add(Issue(tenant.Id, notice, "PACKAGE_QUANTITY_INVARIANT_REJECTED"));
            await db.SaveChangesAsync();
        }
        var read = new List<string>();
        await using (var db = fixture.CreateContext())
        {
            // The job's own order is the documented partly cancelled one; the lost order answers with the same shape.
            var port = new PttOrderPort(read, id => Renumbered(SampleOrder("PTT-A1712HP2E-071024"), id));
            var result = await Processor(db, port).ProcessAsync(tenant.Id, connection.Id, MarketplaceJobTypes.PttAvmOrderSync,
                JsonSerializer.Serialize(new { connectionId = connection.Id, externalOrderId = "PTT-A1712HP2E-071024" }), "audit-1114-ptt", CancellationToken.None);
            Assert.True(result.Succeeded, result.ErrorSummary);
        }
        Assert.Equal(["PTT-A1712HP2E-071024", lost], read);
        await using var verification = fixture.CreateContext();
        foreach (var number in new[] { "PTT-A1712HP2E-071024", lost })
        {
            var order = await verification.Orders.AsNoTracking().SingleAsync(x => x.TenantId == tenant.Id && x.ExternalOrderId == number);
            var lines = await verification.OrderLines.AsNoTracking().Where(x => x.OrderId == order.Id).OrderBy(x => x.ExternalLineId).ToListAsync();
            Assert.Equal(2, lines.Count);
            Assert.All(lines, line => Assert.Equal(1m, line.OrderedQuantity));
        }
        Assert.Equal(IssueStatus.Resolved, (await verification.OperationalIssues.AsNoTracking().SingleAsync(x => x.TenantId == tenant.Id && x.DedupeKey == notice)).Status);
        Assert.False(await verification.OperationalIssues.AnyAsync(x => x.TenantId == tenant.Id && x.Status != IssueStatus.Resolved && (x.Code == OperationalIssueRetentionService.OrderNotImportedCode || x.Code == "PACKAGE_QUANTITY_INVARIANT_REJECTED")));
    }

    // Every marketplace, not only Trendyol: a new order the integrity check refuses joins the re-read list and leaves an
    // ORDER_NOT_IMPORTED notice that the stale pass does not close; the read that imports it closes both.
    [PostgreSqlFact]
    public async Task ARefusedNewPttAvmOrderJoinsTheRereadListAndItsNoticeStaysUntilImported()
    {
        var (tenant, connection) = await SeedAsync("PTTAVM");
        const string number = "PTT-OPEN12345-211026";
        var sample = SampleOrder(number);
        // A line no package holds: ORDER_LINE_COVERAGE_INVALID.
        var broken = sample with { Packages = [sample.Packages[0] with { Allocations = sample.Packages[0].Allocations.Take(1).ToList() }] };
        var read = new List<string>();
        await using (var db = fixture.CreateContext())
        {
            var result = await Processor(db, new PttOrderPort(read, _ => broken)).ProcessAsync(tenant.Id, connection.Id, MarketplaceJobTypes.PttAvmOrderSync,
                JsonSerializer.Serialize(new { connectionId = connection.Id, externalOrderId = number }), "audit-1114-refused", CancellationToken.None);
            Assert.True(result.Succeeded, result.ErrorSummary);
        }
        await using (var db = fixture.CreateContext())
        {
            Assert.False(await db.Orders.AnyAsync(x => x.TenantId == tenant.Id));
            var marker = await db.OperationalIssues.AsNoTracking().SingleAsync(x => x.TenantId == tenant.Id && x.DedupeKey == MarketplaceJobProcessor.OrderNotImportedKey(connection.Id, number));
            Assert.Equal((OperationalIssueRetentionService.OrderNotImportedCode, IssueStatus.Open), (marker.Code, marker.Status));
            var cursor = await db.SyncCursors.AsNoTracking().SingleAsync(x => x.TenantId == tenant.Id && x.ConnectionId == connection.Id && x.ResourceType == RejectedOrderRereadPolicy.CursorResource);
            Assert.Equal(number, Assert.Single(RejectedOrderRereadPolicy.Parse(cursor.OpaqueCursor)).OrderId);
            // The 30-day stale pass leaves it open.
            await db.OperationalIssues.Where(x => x.Id == marker.Id).ExecuteUpdateAsync(x => x.SetProperty(i => i.LastSeenAt, fixture.Now.AddDays(-60)));
            await new OperationalIssueRetentionService(db, fixture.TimeProvider).ResolveStaleAsync(TimeSpan.FromDays(30), CancellationToken.None);
            Assert.Equal(IssueStatus.Open, (await db.OperationalIssues.AsNoTracking().SingleAsync(x => x.Id == marker.Id)).Status);
        }
        // A later read brings the complete order: it is imported and every notice of it closes.
        await using (var db = fixture.CreateContext())
        {
            var result = await Processor(db, new PttOrderPort(read, _ => sample)).ProcessAsync(tenant.Id, connection.Id, MarketplaceJobTypes.PttAvmOrderSync,
                JsonSerializer.Serialize(new { connectionId = connection.Id, externalOrderId = number }), "audit-1114-imported", CancellationToken.None);
            Assert.True(result.Succeeded, result.ErrorSummary);
        }
        await using var verification = fixture.CreateContext();
        Assert.True(await verification.Orders.AnyAsync(x => x.TenantId == tenant.Id && x.ExternalOrderId == number));
        Assert.False(await verification.OperationalIssues.AnyAsync(x => x.TenantId == tenant.Id && x.Status != IssueStatus.Resolved && (x.Code == OperationalIssueRetentionService.OrderNotImportedCode || x.Code == "ORDER_LINE_COVERAGE_INVALID")));
    }

    // Live, 14 Nov 2026: five RETURN_LINE_ORDER_LINE_NOT_FOUND notices stayed open although every line was linked to its
    // order line by a later read. The read that links the line closes its notice, and the retention pass closes one
    // whose claim is not read again.
    [PostgreSqlFact]
    public async Task AReturnLineNoticeClosesOnceTheLineIsLinked()
    {
        var (tenant, connection) = await SeedAsync("TRENDYOL");
        var order = new Order { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, ExternalOrderId = "ord-ret-1", OrderNumber = "ord-ret-1", Currency = "TRY", OrderedAt = fixture.Now.AddDays(-5), LastRemoteModifiedAt = fixture.Now.AddDays(-5), CustomerSnapshotJson = "{}", ShipmentAddressSnapshotJson = "{}", InvoiceAddressSnapshotJson = "{}", DerivedStatus = "DELIVERED", CreatedAt = fixture.Now, UpdatedAt = fixture.Now, Version = 1 };
        await using (var db = fixture.CreateContext())
        {
            db.Orders.Add(order);
            db.OrderLines.Add(Line(tenant.Id, order.Id, "ol-1"));
            await db.SaveChangesAsync();
        }
        RemoteReturnClaim Claim(DateTimeOffset modified) => new("claim-1", "ord-ret-1", "Accepted", null, null, null, modified,
            [new RemoteReturnLine("claim-line-1", "ol-2", 1, RawStatus: "Accepted")], "{}");
        var key = MarketplaceJobProcessor.ReturnLineUnlinkedIssueKey(connection.Id, "claim-1", "claim-line-1");

        // The claim arrives before the order line it names.
        await using (var db = fixture.CreateContext())
            await Processor(db, null!).UpsertReturn(tenant.Id, connection.Id, Claim(fixture.Now.AddMinutes(-10)), CancellationToken.None);
        await using (var db = fixture.CreateContext())
        {
            Assert.Equal(IssueStatus.Open, (await db.OperationalIssues.AsNoTracking().SingleAsync(x => x.TenantId == tenant.Id && x.DedupeKey == key)).Status);
            Assert.False(await db.ReturnLines.AnyAsync(x => x.TenantId == tenant.Id));
            db.OrderLines.Add(Line(tenant.Id, order.Id, "ol-2"));
            await db.SaveChangesAsync();
        }
        // The next read links it and closes the notice.
        await using (var db = fixture.CreateContext())
            await Processor(db, null!).UpsertReturn(tenant.Id, connection.Id, Claim(fixture.Now.AddMinutes(-5)), CancellationToken.None);
        await using (var db = fixture.CreateContext())
        {
            Assert.True(await db.ReturnLines.AnyAsync(x => x.TenantId == tenant.Id && x.ExternalLineId == "claim-line-1"));
            Assert.Equal(IssueStatus.Resolved, (await db.OperationalIssues.AsNoTracking().SingleAsync(x => x.TenantId == tenant.Id && x.DedupeKey == key)).Status);

            // A notice left open by the code before this one (its line already linked) is closed by the retention pass;
            // one whose line is still missing stays.
            await db.OperationalIssues.Where(x => x.TenantId == tenant.Id && x.DedupeKey == key).ExecuteUpdateAsync(x => x.SetProperty(i => i.Status, IssueStatus.Open));
            var missing = MarketplaceJobProcessor.ReturnLineUnlinkedIssueKey(connection.Id, "claim-1", "claim-line-9");
            db.OperationalIssues.Add(Issue(tenant.Id, missing, "RETURN_LINE_ORDER_LINE_NOT_FOUND"));
            await db.SaveChangesAsync();
            Assert.True(await new OperationalIssueRetentionService(db, fixture.TimeProvider).ResolveLinkedReturnLineIssuesAsync(CancellationToken.None) >= 1);
            Assert.Equal(IssueStatus.Resolved, (await db.OperationalIssues.AsNoTracking().SingleAsync(x => x.TenantId == tenant.Id && x.DedupeKey == key)).Status);
            Assert.Equal(IssueStatus.Open, (await db.OperationalIssues.AsNoTracking().SingleAsync(x => x.TenantId == tenant.Id && x.DedupeKey == missing)).Status);
        }
    }

    private MarketplaceJobProcessor Processor(AppDbContext db, IOrderPort orders) =>
        new(db, null!, null!, null!, null!, orders, null!, null!, null!, null!, null!, new ConfigurationBuilder().Build(), fixture.TimeProvider);

    private static RemoteOrder Renumbered(RemoteOrder order, string number) =>
        order with { ExternalOrderId = number, OrderNumber = number, Packages = order.Packages.Select(package => package with { ExternalPackageId = $"order:{number}" }).ToList() };

    private static RemoteOrder SampleOrder(string number)
    {
        var (result, _) = PttAvmHttpClient.SoapResponse(HttpStatusCode.OK, PttAvmAdapterTests.OrderListAnswer, PttAvmHttpClient.Orders);
        return PttAvmOrderMapper.Page(result!).Items.Single(x => x.ExternalOrderId == number);
    }

    private OperationalIssue Issue(Guid tenantId, string key, string code) => new() { Id = Guid.CreateVersion7(), TenantId = tenantId, DedupeKey = key, Code = code, Summary = "Test", Status = IssueStatus.Open, FirstSeenAt = fixture.Now.AddHours(-1), LastSeenAt = fixture.Now.AddHours(-1), OccurrenceCount = 1 };

    private static OrderLine Line(Guid tenantId, Guid orderId, string externalLineId) => new() { Id = Guid.CreateVersion7(), TenantId = tenantId, OrderId = orderId, ExternalLineId = externalLineId, Sku = "SKU", TitleSnapshot = "Ürün", RawStatus = "Delivered", OrderedQuantity = 1, ShippedQuantity = 1, DeliveredQuantity = 1, UnitPrice = 100, VatRate = 10, Version = 1 };

    private async Task<(Tenant Tenant, PlatformConnection Connection)> SeedAsync(string platformCode)
    {
        var now = fixture.Now;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"audit-1114-{Guid.NewGuid():N}", DisplayName = "audit-1114", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var connection = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = platformCode, Environment = "PRODUCTION", DisplayName = platformCode, ExternalStoreId = $"store-{Guid.NewGuid():N}", Status = "ACTIVE", ApiVersion = "V1", SettingsJson = "{}" };
        await using var db = fixture.CreateContext();
        db.Tenants.Add(tenant);
        db.PlatformConnections.Add(connection);
        await db.SaveChangesAsync();
        return (tenant, connection);
    }

    // Answers an order read by number with the order the test gives, and records which orders were asked for.
    private sealed class PttOrderPort(List<string> read, Func<string, RemoteOrder> answer) : IOrderPort
    {
        public Task<AdapterResult<AdapterPageResult<RemoteOrder>>> PollAsync(AdapterContext context, OrderPollWindow window, AdapterPageRequest page, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<RemoteOrder>> GetAsync(AdapterContext context, string externalOrderId, CancellationToken cancellationToken)
        {
            read.Add(externalOrderId);
            return Task.FromResult(AdapterResult<RemoteOrder>.Success(answer(externalOrderId), null));
        }
        public Task<AdapterResult<PackageActionResult>> ExecutePackageActionAsync(AdapterContext context, PackageActionCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<IReadOnlyList<RemoteCargoCompany>>> GetChangeableCargoCompaniesAsync(AdapterContext context, string externalPackageId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<IReadOnlyList<RemotePackageableLine>>> GetPackageableLineItemsAsync(AdapterContext context, string externalLineItemId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<CreateOrderPackageResult>> CreateOrderPackageAsync(AdapterContext context, CreateOrderPackageCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<bool>> CreateCommonLabelAsync(AdapterContext context, CommonLabelRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<CommonLabelDocument>> GetCommonLabelAsync(AdapterContext context, string cargoTrackingNumber, CancellationToken cancellationToken, string format = "ZPL") => throw new NotSupportedException();
        public Task<AdapterResult<StageTestOrderResult>> CreateStageTestOrderAsync(AdapterContext context, string barcode, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
