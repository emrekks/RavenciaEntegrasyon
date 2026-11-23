using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Owner, 18 Nov 2026 ("Değişim paketi neden Yeniden Gönderilecekler'de gözükmüyor?"): Trendyol's order reads never return
// an exchange's new package, so an order is listed under Siparişler › Yeniden Gönderimler when its exchange claim names
// the package (getClaims replacementOutboundpackageinfo) or the owner marked the exchange made; it waits for the carrier
// (Yeniden Gönderilecekler) until the package's tracking link is known. A plain exchange request is not listed.
[Collection("postgres-integration")]
public sealed class ExchangeResendOrdersPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    [PostgreSqlFact]
    public async Task An_exchange_with_its_package_or_the_owners_mark_is_a_resend_and_a_plain_request_is_not()
    {
        var (tenant, connection) = await SeedAsync();
        var withPackage = await SeedOrderAsync(tenant.Id, connection.Id, "ex-pkg", claim => claim.ReplacementPackageId = "28717254");
        var marked = await SeedOrderAsync(tenant.Id, connection.Id, "ex-mark", claim => { claim.ResolvedWith = ReturnCustomerRequestPolicy.Change; claim.ResolvedWithSource = ReturnCustomerRequestPolicy.ManualSource; });
        var plain = await SeedOrderAsync(tenant.Id, connection.Id, "ex-plain", _ => { });

        async Task<(IReadOnlyList<OrderListView> Resent, IReadOnlyList<OrderListView> Unshipped, int Count)> ReadAsync()
        {
            await using var db = fixture.CreateContext();
            var service = Service(db);
            var resent = await service.OrdersAsync(tenant.Id, 50, 1, null, new OrderListQuery(Status: "RESENT"), CancellationToken.None);
            var unshipped = await service.OrdersAsync(tenant.Id, 50, 1, null, new OrderListQuery(Status: "RESENT", ResentNotShipped: true), CancellationToken.None);
            var summary = await service.OrderSummaryAsync(tenant.Id, new OrderListQuery(), CancellationToken.None, ["RESENT"]);
            return (resent.Items, unshipped.Items, summary.Resent);
        }

        var first = await ReadAsync();
        Assert.Equal(new[] { marked, withPackage }.Order(), first.Resent.Select(order => order.Id).Order());
        Assert.Equal(new[] { marked, withPackage }.Order(), first.Unshipped.Select(order => order.Id).Order());
        Assert.Equal(2, first.Count);
        Assert.DoesNotContain(first.Resent, order => order.Id == plain);
        var package = Assert.Single(first.Resent, order => order.Id == withPackage).ExchangeResend!;
        Assert.Equal(("28717254", false), (package.PackageId, package.Shipped));
        Assert.Null(Assert.Single(first.Resent, order => order.Id == marked).ExchangeResend!.PackageId);

        // Its tracking link comes: handed to the carrier, so Gönderilenler only.
        await using (var db = fixture.CreateContext())
            await db.ReturnClaims.Where(x => x.TenantId == tenant.Id && x.OrderId == withPackage)
                .ExecuteUpdateAsync(x => x.SetProperty(c => c.ReplacementCargoTrackingLink, "https://tracking.example.test/exchange"));
        var shipped = await ReadAsync();
        Assert.Contains(shipped.Resent, order => order.Id == withPackage && order.ExchangeResend!.Shipped);
        Assert.DoesNotContain(shipped.Unshipped, order => order.Id == withPackage);
    }

    // Owner, 23 Nov 2026 ("yeniden gönderimi kargoya verdim ve kapanması lazım ama kapatamadım"): Trendyol names the exchange
    // package's tracking number but no link, so only the owner's "Kargoya Verildi" mark takes the row out of the count.
    [PostgreSqlFact]
    public async Task The_owners_shipped_mark_takes_an_exchange_out_of_the_count_and_removing_it_puts_it_back()
    {
        var (tenant, connection) = await SeedAsync();
        var order = await SeedOrderAsync(tenant.Id, connection.Id, "ex-ship", claim =>
        {
            claim.ReplacementPackageId = "4360616611";
            claim.ReplacementCargoProviderName = "Trendyol Express Marketplace";
            claim.ReplacementCargoTrackingNumber = "7330000000000001";
            claim.ResolvedWith = ReturnCustomerRequestPolicy.Change;
            claim.ResolvedWithSource = ReturnCustomerRequestPolicy.ManualSource;
        });
        var claimId = await ClaimIdAsync(tenant.Id, order);

        // A tracking number alone is not "handed to the carrier": it is still counted, and its store's hover says it is an exchange.
        var before = await CountsAsync(tenant.Id, order);
        Assert.Equal((1, true, false, (string?)null), (before.Count, before.Unshipped, before.Row.Shipped, before.Row.ShippedSource));
        Assert.Equal(claimId, before.Row.ClaimId);
        Assert.Equal(1, before.ExchangeStores[connection.Id.ToString("D")]);

        await using (var db = fixture.CreateContext())
        {
            var result = await Service(db).MarkReplacementShippedAsync(tenant.Id, Guid.NewGuid(), claimId, before.Row.ClaimVersion, true, "corr-ship", CancellationToken.None);
            Assert.True(result.Succeeded, result.Error?.Message);
            Assert.Equal(1, await db.AuditLogs.CountAsync(x => x.TenantId == tenant.Id && x.Action == "RETURN_REPLACEMENT_MARKED_SHIPPED" && x.TargetId == claimId.ToString("D")));
        }
        var marked = await CountsAsync(tenant.Id, order);
        Assert.Equal((0, false, true, (string?)ReturnCustomerRequestPolicy.ManualSource), (marked.Count, marked.Unshipped, marked.Row.Shipped, marked.Row.ShippedSource));
        Assert.True(marked.InResent);
        Assert.Empty(marked.ExchangeStores);

        // Marking it twice, or with an old version, is refused.
        await using (var db = fixture.CreateContext())
        {
            Assert.Equal(409, (await Service(db).MarkReplacementShippedAsync(tenant.Id, Guid.NewGuid(), claimId, marked.Row.ClaimVersion, true, "corr-again", CancellationToken.None)).Error!.Status);
            Assert.Equal(412, (await Service(db).MarkReplacementShippedAsync(tenant.Id, Guid.NewGuid(), claimId, before.Row.ClaimVersion, false, "corr-stale", CancellationToken.None)).Error!.Status);
        }

        await using (var db = fixture.CreateContext())
            Assert.True((await Service(db).MarkReplacementShippedAsync(tenant.Id, Guid.NewGuid(), claimId, marked.Row.ClaimVersion, false, "corr-unship", CancellationToken.None)).Succeeded);
        var removed = await CountsAsync(tenant.Id, order);
        Assert.Equal((1, true, false), (removed.Count, removed.Unshipped, removed.Row.Shipped));
        await using (var db = fixture.CreateContext())
        {
            var claim = await db.ReturnClaims.AsNoTracking().SingleAsync(x => x.Id == claimId);
            Assert.Equal((null, null), (claim.ReplacementShippedAt, claim.ReplacementShippedSource));
            Assert.Equal(1, await db.AuditLogs.CountAsync(x => x.TenantId == tenant.Id && x.Action == "RETURN_REPLACEMENT_SHIP_MARK_REMOVED"));
        }
    }

    [PostgreSqlFact]
    public async Task Another_tenant_cannot_mark_the_package_and_a_plain_request_takes_no_mark()
    {
        var (tenant, connection) = await SeedAsync();
        var order = await SeedOrderAsync(tenant.Id, connection.Id, "ex-foreign", claim => claim.ReplacementPackageId = "28717255");
        var plain = await SeedOrderAsync(tenant.Id, connection.Id, "ex-plain2", _ => { });
        var claimId = await ClaimIdAsync(tenant.Id, order);
        await using var db = fixture.CreateContext();
        var foreign = await Service(db).MarkReplacementShippedAsync(Guid.NewGuid(), Guid.NewGuid(), claimId, 0, true, "corr-foreign", CancellationToken.None);
        Assert.Equal(404, foreign.Error!.Status);
        var plainClaim = await db.ReturnClaims.AsNoTracking().SingleAsync(x => x.TenantId == tenant.Id && x.OrderId == plain);
        var notExchange = await Service(db).MarkReplacementShippedAsync(tenant.Id, Guid.NewGuid(), plainClaim.Id, plainClaim.Version, true, "corr-plain", CancellationToken.None);
        Assert.Equal((409, ReturnDecisionService.ReplacementShipMarkNotAllowedCode), (notExchange.Error!.Status, notExchange.Error.Code));
        Assert.Null((await db.ReturnClaims.AsNoTracking().SingleAsync(x => x.Id == claimId)).ReplacementShippedAt);
    }

    // RETURN_STATUS_SYNC reads a closed exchange claim again while its package is not known to be shipped: a tracking link
    // in the answer closes the row by itself; an answer with the tracking number only leaves it waiting.
    [PostgreSqlFact]
    public async Task A_tracking_link_from_the_reread_closes_the_row_and_a_tracking_number_alone_does_not()
    {
        var (tenant, connection) = await SeedAsync();
        var order = await SeedOrderAsync(tenant.Id, connection.Id, "ex-reread", claim =>
        {
            claim.ReplacementPackageId = "28717256";
            claim.LastRemoteModifiedAt = fixture.Now.AddDays(-5);
            // Dated, so the request-date read (which asks by claimIds too) leaves it alone.
            claim.RequestedAt = fixture.Now.AddDays(-10);
        });
        var port = new ClaimPort("claim-ex-reread", "ex-reread", null);
        await RunStatusSyncAsync(tenant.Id, connection.Id, port);
        Assert.Equal(1, port.ManyCalls);
        var numberOnly = await CountsAsync(tenant.Id, order);
        Assert.Equal((1, true, false), (numberOnly.Count, numberOnly.Unshipped, numberOnly.Row.Shipped));
        Assert.Equal("7330000000000002", numberOnly.Row.CargoTrackingNumber);

        // Within the hour no request goes out again.
        await RunStatusSyncAsync(tenant.Id, connection.Id, port);
        Assert.Equal(1, port.ManyCalls);

        await using (var db = fixture.CreateContext())
            await db.SyncCursors.Where(x => x.TenantId == tenant.Id && x.ResourceType == "RETURN_EXCHANGE_RESEND")
                .ExecuteUpdateAsync(x => x.SetProperty(c => c.LastAttemptAt, fixture.Now.AddHours(-2)));
        var linked = new ClaimPort("claim-ex-reread", "ex-reread", "https://tracking.example.test/exchange-reread");
        await RunStatusSyncAsync(tenant.Id, connection.Id, linked);
        Assert.Equal(1, linked.ManyCalls);
        var closed = await CountsAsync(tenant.Id, order);
        Assert.Equal((0, false, true, (string?)"MARKETPLACE"), (closed.Count, closed.Unshipped, closed.Row.Shipped, closed.Row.ShippedSource));

        // Closed: nothing is asked any more.
        await using (var db = fixture.CreateContext())
            await db.SyncCursors.Where(x => x.TenantId == tenant.Id && x.ResourceType == "RETURN_EXCHANGE_RESEND")
                .ExecuteUpdateAsync(x => x.SetProperty(c => c.LastAttemptAt, fixture.Now.AddHours(-2)));
        await RunStatusSyncAsync(tenant.Id, connection.Id, linked);
        Assert.Equal(1, linked.ManyCalls);
    }

    // Scan O3 (23 Nov 2026): a Hepsiburada exchange (finalizedWith Change) never left Yeniden Gönderilecekler, as its claim
    // names no exchange shipment. Hepsiburada re-creates the exchange product as an order line with creationReason
    // ClaimChangeAccepted; once that line is shipped the exchange is handed to the carrier (MARKETPLACE) and the owner's mark
    // is refused. Before that it waits, and an ordinary shipped line (creationReason OrderCreated) says nothing about it.
    [PostgreSqlFact]
    public async Task A_hepsiburada_exchange_closes_when_its_exchange_line_ships()
    {
        var (tenant, connection) = await SeedAsync("HEPSIBURADA");
        var order = await SeedOrderAsync(tenant.Id, connection.Id, "hb-ex", claim =>
        {
            claim.Status = ReturnClaimStatus.Approved;
            claim.RawStatus = "Accepted";
            claim.ReasonCode = null;
            claim.ResolvedWith = ReturnCustomerRequestPolicy.Change;
        });
        Guid exchangeLine;
        await using (var db = fixture.CreateContext())
        {
            await db.OrderLines.Where(x => x.TenantId == tenant.Id && x.OrderId == order)
                .ExecuteUpdateAsync(x => x.SetProperty(l => l.SourceSnapshotJson, "{\"lineItemId\":\"a\",\"creationReason\":\"OrderCreated\"}"));
            var line = new OrderLine { Id = Guid.CreateVersion7(), TenantId = tenant.Id, OrderId = order, ExternalLineId = "hb-ex-2", Sku = "SKU-2", TitleSnapshot = "Ürün", RawStatus = "Open", OrderedQuantity = 1, UnitPrice = 100, VatRate = 10, Version = 1, SourceSnapshotJson = "{\"lineItemId\":\"b\",\"creationReason\":\"ClaimChangeAccepted\"}" };
            db.OrderLines.Add(line);
            await db.SaveChangesAsync();
            exchangeLine = line.Id;
        }
        var claimId = await ClaimIdAsync(tenant.Id, order);

        var waiting = await CountsAsync(tenant.Id, order);
        Assert.Equal((1, true, false, (string?)null), (waiting.Count, waiting.Unshipped, waiting.Row.Shipped, waiting.Row.ShippedSource));

        await using (var db = fixture.CreateContext())
            await db.OrderLines.Where(x => x.Id == exchangeLine).ExecuteUpdateAsync(x => x.SetProperty(l => l.ShippedQuantity, 1m));
        var shipped = await CountsAsync(tenant.Id, order);
        Assert.Equal((0, true, false, true, (string?)"MARKETPLACE"), (shipped.Count, shipped.InResent, shipped.Unshipped, shipped.Row.Shipped, shipped.Row.ShippedSource));
        Assert.Empty(shipped.ExchangeStores);
        await using (var db = fixture.CreateContext())
        {
            var refused = await Service(db).MarkReplacementShippedAsync(tenant.Id, Guid.NewGuid(), claimId, shipped.Row.ClaimVersion, true, "corr-hb", CancellationToken.None);
            Assert.Equal((409, ReturnDecisionService.ReplacementShipMarkNotAllowedCode), (refused.Error!.Status, refused.Error.Code));
        }
    }

    // The mark's audit names the exchange's own marketplace (it read "Trendyol'a" on a Hepsiburada exchange).
    [PostgreSqlFact]
    public async Task The_shipped_marks_audit_names_hepsiburada()
    {
        var (tenant, connection) = await SeedAsync("HEPSIBURADA");
        var order = await SeedOrderAsync(tenant.Id, connection.Id, "hb-mark", claim => { claim.Status = ReturnClaimStatus.Approved; claim.RawStatus = "Accepted"; claim.ResolvedWith = ReturnCustomerRequestPolicy.Change; });
        var claimId = await ClaimIdAsync(tenant.Id, order);
        await using var db = fixture.CreateContext();
        var version = (await db.ReturnClaims.AsNoTracking().SingleAsync(x => x.Id == claimId)).Version;
        Assert.True((await Service(db).MarkReplacementShippedAsync(tenant.Id, Guid.NewGuid(), claimId, version, true, "corr-hb-mark", CancellationToken.None)).Succeeded);
        var reason = await db.AuditLogs.Where(x => x.TenantId == tenant.Id && x.TargetId == claimId.ToString("D")).Select(x => x.Reason).SingleAsync();
        Assert.Contains("Hepsiburada'ya bir şey gönderilmedi", reason);
        Assert.DoesNotContain("Trendyol", reason);
    }

    // Scan, 23 Nov 2026: removing the owner's "Değişim Onaylandı" mark left his "Kargoya Verildi" mark behind on a claim
    // that was no exchange resend any more: "İşareti Kaldır" refused it, and a new exchange mark brought it back unseen.
    [PostgreSqlFact]
    public async Task Removing_the_exchange_mark_takes_the_hand_made_shipped_mark_with_it()
    {
        var (tenant, connection) = await SeedAsync();
        var order = await SeedOrderAsync(tenant.Id, connection.Id, "ex-orphan", claim => { claim.ResolvedWith = ReturnCustomerRequestPolicy.Change; claim.ResolvedWithSource = ReturnCustomerRequestPolicy.ManualSource; });
        var claimId = await ClaimIdAsync(tenant.Id, order);
        long Version(AppDbContext db) => db.ReturnClaims.AsNoTracking().Single(x => x.Id == claimId).Version;
        await using (var db = fixture.CreateContext())
            Assert.True((await Service(db).MarkReplacementShippedAsync(tenant.Id, Guid.NewGuid(), claimId, Version(db), true, "corr-o1", CancellationToken.None)).Succeeded);
        await using (var db = fixture.CreateContext())
            Assert.True((await Service(db).MarkExchangeApprovedAsync(tenant.Id, Guid.NewGuid(), claimId, Version(db), false, "corr-o2", CancellationToken.None)).Succeeded);
        await using (var db = fixture.CreateContext())
        {
            var claim = await db.ReturnClaims.AsNoTracking().SingleAsync(x => x.Id == claimId);
            Assert.Equal(((string?)null, (DateTimeOffset?)null, (string?)null), (claim.ResolvedWith, claim.ReplacementShippedAt, claim.ReplacementShippedSource));
        }
        // Marked made again, it waits for the carrier: the old hand-made mark does not come back.
        await using (var db = fixture.CreateContext())
            Assert.True((await Service(db).MarkExchangeApprovedAsync(tenant.Id, Guid.NewGuid(), claimId, Version(db), true, "corr-o3", CancellationToken.None)).Succeeded);
        var again = await CountsAsync(tenant.Id, order);
        Assert.Equal((1, true, false), (again.Count, again.Unshipped, again.Row.Shipped));
    }

    // Scan, 23 Nov 2026: with more waiting exchanges than one request holds (20), the re-read asked the same first 20 every
    // hour and never the rest. They take turns: two runs ask all 25, the second one starting after the last asked.
    [PostgreSqlFact]
    public async Task More_waiting_exchanges_than_one_request_holds_take_turns()
    {
        var (tenant, connection) = await SeedAsync();
        var numbers = Enumerable.Range(1, 25).Select(i => $"ex-turn-{i:00}").ToArray();
        foreach (var number in numbers)
            await SeedOrderAsync(tenant.Id, connection.Id, number, claim => { claim.ReplacementPackageId = "28717256"; claim.LastRemoteModifiedAt = fixture.Now.AddDays(-5); claim.RequestedAt = fixture.Now.AddDays(-10); });
        var port = new RecordingClaimPort();
        await RunStatusSyncAsync(tenant.Id, connection.Id, port);
        await using (var db = fixture.CreateContext())
            await db.SyncCursors.Where(x => x.TenantId == tenant.Id && x.ResourceType == "RETURN_EXCHANGE_RESEND")
                .ExecuteUpdateAsync(x => x.SetProperty(c => c.LastAttemptAt, fixture.Now.AddHours(-2)));
        await RunStatusSyncAsync(tenant.Id, connection.Id, port);
        Assert.Equal(2, port.Rereads.Count);
        Assert.All(port.Rereads, ids => Assert.Equal(20, ids.Count));
        Assert.Equal(numbers.Select(number => $"claim-{number}").Order(), port.Rereads.SelectMany(ids => ids).Distinct().Order());
    }

    // getClaims by claimIds answering each claim as stored (Rejected, its package named, no link); it records what each
    // exchange re-read asked.
    private sealed class RecordingClaimPort : IReturnPort, IReturnBatchReadPort
    {
        public List<IReadOnlyList<string>> Rereads { get; } = [];

        private static RemoteReturnClaim Claim(string claimId)
        {
            var orderNumber = claimId["claim-".Length..];
            return new(claimId, orderNumber, "Rejected", "WRONGCHANGEREQUEST", null, null, null, [new RemoteReturnLine("ci-1", $"{orderNumber}-1", 1, RawStatus: "Rejected")], "{}",
                ReplacementPackage: new RemoteReplacementPackage("28717256", null, null, null), ReplacementInfoPresent: true);
        }

        public Task<AdapterResult<IReadOnlyList<RemoteReturnClaim>>> GetManyAsync(AdapterContext context, IReadOnlyList<string> externalReturnIds, CancellationToken cancellationToken)
        {
            if (context.IdempotencyKey.Contains("exchange-resend-reread", StringComparison.Ordinal)) Rereads.Add(externalReturnIds.ToList());
            return Task.FromResult(AdapterResult<IReadOnlyList<RemoteReturnClaim>>.Success(externalReturnIds.Select(Claim).ToList()));
        }

        public Task<AdapterResult<RemoteReturnClaim>> GetAsync(AdapterContext context, string externalReturnId, CancellationToken cancellationToken) =>
            Task.FromResult(AdapterResult<RemoteReturnClaim>.Success(Claim(externalReturnId)));

        public Task<AdapterResult<AdapterPageResult<RemoteReturnClaim>>> PollAsync(AdapterContext context, ReturnPollWindow window, AdapterPageRequest page, CancellationToken cancellationToken) =>
            Task.FromResult(AdapterResult<AdapterPageResult<RemoteReturnClaim>>.Success(new([], null, false)));

        public Task<AdapterResult<ReturnActionResult>> ExecuteAsync(AdapterContext context, ReturnActionCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<IReadOnlyList<ReturnIssueReason>>> IssueReasonsAsync(AdapterContext context, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    // Scan, 23 Nov 2026: "Bugün Onaylanan" dated an exchange the owner marked made by the day the marketplace answered the
    // claim Rejected (DecidedAt): marked today after a rejection days ago it did not count today, and a claim rejected today
    // that he marked days ago did. It counts on the Istanbul day of his mark.
    [PostgreSqlFact]
    public async Task Todays_approved_count_dates_a_marked_exchange_by_the_owners_mark()
    {
        var now = DateTimeOffset.UtcNow;
        var (late, lateConnection) = await SeedAsync();
        var lateOrder = await SeedOrderAsync(late.Id, lateConnection.Id, "ex-today-1", claim => claim.DecidedAt = now.AddDays(-3));
        var lateClaim = await ClaimIdAsync(late.Id, lateOrder);
        await using (var db = fixture.CreateContext())
        {
            var version = (await db.ReturnClaims.AsNoTracking().SingleAsync(x => x.Id == lateClaim)).Version;
            Assert.True((await Service(db).MarkExchangeApprovedAsync(late.Id, Guid.NewGuid(), lateClaim, version, true, "corr-today", CancellationToken.None)).Succeeded);
        }
        var (early, earlyConnection) = await SeedAsync();
        var earlyOrder = await SeedOrderAsync(early.Id, earlyConnection.Id, "ex-today-2", claim => { claim.DecidedAt = now; claim.ResolvedWith = ReturnCustomerRequestPolicy.Change; claim.ResolvedWithSource = ReturnCustomerRequestPolicy.ManualSource; });
        var earlyClaim = await ClaimIdAsync(early.Id, earlyOrder);
        await using (var db = fixture.CreateContext())
        {
            db.AuditLogs.Add(new AuditLog { TenantId = early.Id, ActorUserId = Guid.NewGuid(), Action = "RETURN_EXCHANGE_MARKED_APPROVED", TargetType = "ReturnClaim", TargetId = earlyClaim.ToString("D"), Reason = "test", CorrelationId = "corr-early", CreatedAt = now.AddDays(-3) });
            await db.SaveChangesAsync();
        }
        await using (var db = fixture.CreateContext())
        {
            Assert.Equal(1, (await Service(db).ReturnSummaryAsync(late.Id, new ReturnListQuery(), CancellationToken.None)).Metrics!.TodayApproved);
            Assert.Equal(0, (await Service(db).ReturnSummaryAsync(early.Id, new ReturnListQuery(), CancellationToken.None)).Metrics!.TodayApproved);
        }
    }

    private async Task RunStatusSyncAsync(Guid tenantId, Guid connectionId, IReturnPort port)
    {
        await using var db = fixture.CreateContext();
        var processor = new MarketplaceJobProcessor(db, null!, null!, null!, null!, null!, null!, null!, port, null!, null!, new ConfigurationBuilder().Build(), fixture.TimeProvider);
        var result = await processor.ProcessAsync(tenantId, connectionId, MarketplaceJobTypes.ReturnStatusSync, "{}", $"exchange-reread-{Guid.NewGuid():N}", CancellationToken.None);
        Assert.True(result.Succeeded, result.ErrorCode);
    }

    private async Task<Guid> ClaimIdAsync(Guid tenantId, Guid orderId)
    {
        await using var db = fixture.CreateContext();
        return await db.ReturnClaims.AsNoTracking().Where(x => x.TenantId == tenantId && x.OrderId == orderId).Select(x => x.Id).SingleAsync();
    }

    private async Task<(int Count, bool InResent, bool Unshipped, OrderExchangeResendView Row, IReadOnlyDictionary<string, int> ExchangeStores)> CountsAsync(Guid tenantId, Guid orderId)
    {
        await using var db = fixture.CreateContext();
        var service = Service(db);
        var resent = await service.OrdersAsync(tenantId, 50, 1, null, new OrderListQuery(Status: "RESENT"), CancellationToken.None);
        var unshipped = await service.OrdersAsync(tenantId, 50, 1, null, new OrderListQuery(Status: "RESENT", ResentNotShipped: true), CancellationToken.None);
        var summary = await service.OrderSummaryAsync(tenantId, new OrderListQuery(ResentNotShipped: true), CancellationToken.None, ["RESENT"]);
        var row = Assert.Single(resent.Items, order => order.Id == orderId).ExchangeResend!;
        return (summary.Resent, resent.Items.Any(order => order.Id == orderId), unshipped.Items.Any(order => order.Id == orderId), row, summary.ResentExchangeByConnection!);
    }

    // getClaims by claimIds answering the exchange claim, Rejected, with its package's tracking number and (when given) link.
    private sealed class ClaimPort(string claimId, string orderNumber, string? link) : IReturnPort, IReturnBatchReadPort
    {
        // The re-read requests only (the lifecycle and request-date reads ask by claimIds too).
        public int ManyCalls { get; private set; }

        private RemoteReturnClaim Claim() => new(claimId, orderNumber, "Rejected", "WRONGCHANGEREQUEST", null, null, null,
            [new RemoteReturnLine("ci-1", $"{orderNumber}-1", 1, RawStatus: "Rejected")], "{}",
            ReplacementPackage: new RemoteReplacementPackage("28717256", "Trendyol Express Marketplace", "7330000000000002", link), ReplacementInfoPresent: true);

        public Task<AdapterResult<IReadOnlyList<RemoteReturnClaim>>> GetManyAsync(AdapterContext context, IReadOnlyList<string> externalReturnIds, CancellationToken cancellationToken)
        {
            if (context.IdempotencyKey.Contains("exchange-resend-reread", StringComparison.Ordinal)) ManyCalls++;
            IReadOnlyList<RemoteReturnClaim> claims = externalReturnIds.Contains(claimId) ? [Claim()] : [];
            return Task.FromResult(AdapterResult<IReadOnlyList<RemoteReturnClaim>>.Success(claims));
        }

        public Task<AdapterResult<RemoteReturnClaim>> GetAsync(AdapterContext context, string externalReturnId, CancellationToken cancellationToken) =>
            Task.FromResult(AdapterResult<RemoteReturnClaim>.Success(Claim()));

        public Task<AdapterResult<AdapterPageResult<RemoteReturnClaim>>> PollAsync(AdapterContext context, ReturnPollWindow window, AdapterPageRequest page, CancellationToken cancellationToken) =>
            Task.FromResult(AdapterResult<AdapterPageResult<RemoteReturnClaim>>.Success(new([], null, false)));

        public Task<AdapterResult<ReturnActionResult>> ExecuteAsync(AdapterContext context, ReturnActionCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<IReadOnlyList<ReturnIssueReason>>> IssueReasonsAsync(AdapterContext context, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    [Fact]
    public void A_read_keeps_a_known_exchange_package_and_fills_only_what_it_names()
    {
        var claim = new ReturnClaim { ExternalClaimId = "c", RawStatus = "Rejected", ReplacementPackageId = "1", ReplacementCargoProviderName = "Aras" };
        Assert.True(ReturnIngestionService.ApplyReplacementPackage(claim, new RemoteReplacementPackage("2", null, "7330", null)));
        Assert.Equal(("2", "Aras", "7330", (string?)null), (claim.ReplacementPackageId, claim.ReplacementCargoProviderName, claim.ReplacementCargoTrackingNumber, claim.ReplacementCargoTrackingLink));
        Assert.False(ReturnIngestionService.ApplyReplacementPackage(claim, new RemoteReplacementPackage("2", "Aras", "7330", null)));
    }

    private MarketplaceSalesService Service(AppDbContext db) =>
        new(db, new CursorCodec(new EphemeralDataProtectionProvider(), fixture.TimeProvider), new ConfigurationBuilder().Build(), null!, null!, null!, null!, TimeProvider.System);

    private async Task<Guid> SeedOrderAsync(Guid tenantId, Guid connectionId, string number, Action<ReturnClaim> shape)
    {
        var order = new Order { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = connectionId, ExternalOrderId = number, OrderNumber = number, Currency = "TRY", OrderedAt = fixture.Now.AddDays(-5), LastRemoteModifiedAt = fixture.Now.AddDays(-5), CustomerSnapshotJson = "{}", ShipmentAddressSnapshotJson = "{}", InvoiceAddressSnapshotJson = "{}", DerivedStatus = "DELIVERED", CreatedAt = fixture.Now, UpdatedAt = fixture.Now, Version = 1 };
        var claim = new ReturnClaim { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = connectionId, OrderId = order.Id, ExternalClaimId = $"claim-{number}", Status = ReturnClaimStatus.Rejected, RawStatus = "Rejected", CustomerRequest = ReturnCustomerRequestPolicy.Change, ReasonCode = "WRONGCHANGEREQUEST", CreatedAt = fixture.Now, UpdatedAt = fixture.Now };
        shape(claim);
        await using var db = fixture.CreateContext();
        db.Orders.Add(order);
        db.OrderLines.Add(new OrderLine { Id = Guid.CreateVersion7(), TenantId = tenantId, OrderId = order.Id, ExternalLineId = $"{number}-1", Sku = "SKU", TitleSnapshot = "Ürün", RawStatus = "Delivered", OrderedQuantity = 1, ShippedQuantity = 1, DeliveredQuantity = 1, UnitPrice = 100, VatRate = 10, Version = 1 });
        db.ReturnClaims.Add(claim);
        await db.SaveChangesAsync();
        return order.Id;
    }

    private async Task<(Tenant Tenant, PlatformConnection Connection)> SeedAsync(string platformCode = "TRENDYOL")
    {
        var now = fixture.Now;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"exr-{Guid.NewGuid():N}", DisplayName = "exr", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var connection = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = platformCode, Environment = "PRODUCTION", DisplayName = platformCode, ExternalStoreId = $"store-{Guid.NewGuid():N}", Status = "ACTIVE", ApiVersion = "V1", SettingsJson = "{}" };
        await using var db = fixture.CreateContext();
        db.Tenants.Add(tenant);
        db.PlatformConnections.Add(connection);
        await db.SaveChangesAsync();
        return (tenant, connection);
    }
}
