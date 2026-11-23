using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Order findings S-2: on a three-line Trendyol return, approving two lines leaves the third waiting for its own
// decision (no 409), stock goes up only for the chosen lines and units, and an open decision holds only its own lines.
[Collection("postgres-integration")]
public sealed class ReturnLineDecisionPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    [PostgreSqlFact]
    public async Task ApprovingTwoOfThreeLines_LeavesTheThirdOpenAndStocksOnlyTheChosenUnits()
    {
        var seed = await SeedAsync("rl-two");
        var port = new LineReturnPort("RL-CLAIM", seed.ExternalLineIds);
        try
        {
            // Lines 2 and 3 are approved, one unit of line 2 goes back to stock. Line 1, Trendyol's first item, still waits.
            await using (var db = fixture.CreateContext())
            {
                var approved = await Service(db, port).ProcessReturnActionInstantAsync(seed.TenantId, Guid.NewGuid(), seed.ClaimId, await VersionAsync(seed.ClaimId),
                    new("APPROVE", null, null, [], [seed.LineIds[1], seed.LineIds[2]], StockLines: [new(seed.LineIds[1], 1m)]), "rl-two:approve", "rl-two", CancellationToken.None);
                Assert.Null(approved.Error);
                var detail = approved.Value!;
                Assert.False(detail.DecisionPending);
                Assert.Contains("APPROVE", detail.AllowedActions);
                Assert.Equal("ACTION_REQUIRED", detail.Lines!.Single(line => line.Id == seed.LineIds[0]).Status);
                Assert.All(detail.Lines!.Where(line => line.Id != seed.LineIds[0]), line => Assert.Equal("APPROVED", line.Status));
            }
            await using (var verify = fixture.CreateContext())
            {
                var decision = await verify.ReturnDecisions.AsNoTracking().SingleAsync(x => x.TenantId == seed.TenantId && x.IdempotencyKey == "rl-two:approve");
                Assert.Equal("SUCCEEDED", decision.Status);
                Assert.Equal(new[] { seed.LineIds[1], seed.LineIds[2] }.Order(), decision.ReturnLineIds!.Order());
                Assert.Equal([0m, 1m, 0m], await OnHandAsync(verify, seed));
            }

            // Stock can be decided on the approved third line while the claim still waits; the waiting line takes none.
            await using (var db = fixture.CreateContext())
            {
                var stocked = await Service(db, port).ApplyDispositionAsync(seed.TenantId, Guid.NewGuid(), seed.ClaimId, new(seed.LineIds[2], "PASS", 1m, "Kontrol edildi"), "rl-two:stock-3", "rl-two", CancellationToken.None);
                Assert.Null(stocked.Error);
                var refused = await Service(db, port).ApplyDispositionAsync(seed.TenantId, Guid.NewGuid(), seed.ClaimId, new(seed.LineIds[0], "PASS", 1m, "Kontrol edildi"), "rl-two:stock-1", "rl-two", CancellationToken.None);
                Assert.Equal("RETURN_DISPOSITION_NOT_ALLOWED", refused.Error!.Code);
            }

            // The third line takes its own decision.
            await using (var db = fixture.CreateContext())
            {
                var last = await Service(db, port).ProcessReturnActionInstantAsync(seed.TenantId, Guid.NewGuid(), seed.ClaimId, await VersionAsync(seed.ClaimId),
                    new("APPROVE", null, null, [], [seed.LineIds[0]]), "rl-two:approve-last", "rl-two", CancellationToken.None);
                Assert.Null(last.Error);
            }
            Assert.Equal([seed.ExternalLineIds[0]], port.Commands[^1].ExternalLineItemIds);
            await using (var verify = fixture.CreateContext())
                Assert.Equal([0m, 1m, 1m], await OnHandAsync(verify, seed));
        }
        finally
        {
            await CleanupAsync(seed.TenantId);
        }
    }

    [PostgreSqlFact]
    public async Task AnOpenDecision_HoldsItsOwnLinesOnly()
    {
        var seed = await SeedAsync("rl-open");
        var port = new LineReturnPort("RL-CLAIM", seed.ExternalLineIds);
        try
        {
            await using (var db = fixture.CreateContext())
            {
                // A decision on line 1 whose request has not been settled yet.
                var now = DateTimeOffset.UtcNow;
                db.ReturnDecisions.Add(new ReturnDecision { Id = Guid.CreateVersion7(), TenantId = seed.TenantId, ClaimId = seed.ClaimId, Action = "APPROVE", IdempotencyKey = "rl-open:first", Status = "SUBMITTED", ActorUserId = Guid.NewGuid(), CreatedAt = now, ReturnLineIds = [seed.LineIds[0]] });
                db.ExternalEffectRecords.Add(new ExternalEffectRecord { Id = Guid.CreateVersion7(), TenantId = seed.TenantId, EffectType = "TRENDYOL_INSTANT_RETURN_ACTION", IdempotencyKey = "rl-open:first", CreatedAt = now, ConnectionId = seed.ConnectionId, TargetId = seed.ClaimId, Action = "RETURN_APPROVE" });
                await db.SaveChangesAsync();
            }
            await using (var db = fixture.CreateContext())
            {
                var version = await VersionAsync(seed.ClaimId);
                var same = await Service(db, port).ProcessReturnActionInstantAsync(seed.TenantId, Guid.NewGuid(), seed.ClaimId, version, new("APPROVE", null, null, [], [seed.LineIds[0], seed.LineIds[1]]), "rl-open:same", "rl-open", CancellationToken.None);
                Assert.Equal("RETURN_DECISION_IN_PROGRESS", same.Error!.Code);
                Assert.Equal(409, same.Error.Status);

                var detail = (await Service(db, port).ReturnAsync(seed.TenantId, seed.ClaimId, CancellationToken.None)).Value!;
                Assert.False(detail.DecisionPending);
                Assert.True(detail.Lines!.Single(line => line.Id == seed.LineIds[0]).DecisionPending);
                Assert.False(detail.Lines!.Single(line => line.Id == seed.LineIds[1]).DecisionPending);

                var other = await Service(db, port).ProcessReturnActionInstantAsync(seed.TenantId, Guid.NewGuid(), seed.ClaimId, version, new("APPROVE", null, null, [], [seed.LineIds[1]]), "rl-open:other", "rl-open", CancellationToken.None);
                Assert.Null(other.Error);
            }
            Assert.Equal([seed.ExternalLineIds[1]], Assert.Single(port.Commands).ExternalLineItemIds);
        }
        finally
        {
            await CleanupAsync(seed.TenantId);
        }
    }

    // Owner, 18 Nov 2026 (1000000001): a Trendyol exchange request is approved with "Değişim Yap" in Trendyol's seller
    // panel; the API documents approveClaimLineItems for returns only. The detail offers no approval, a direct approval
    // is refused with its own code and nothing reaches Trendyol; the rejection (createClaimIssue) stays.
    [PostgreSqlFact]
    public async Task A_Trendyol_exchange_request_is_never_approved_from_the_panel()
    {
        var seed = await SeedAsync("rl-exch");
        var port = new LineReturnPort("RL-CLAIM", seed.ExternalLineIds);
        try
        {
            await using (var db = fixture.CreateContext())
                await db.ReturnClaims.Where(x => x.Id == seed.ClaimId).ExecuteUpdateAsync(x => x.SetProperty(c => c.CustomerRequest, ReturnCustomerRequestPolicy.Change));
            await using (var db = fixture.CreateContext())
            {
                var detail = (await Service(db, port).ReturnAsync(seed.TenantId, seed.ClaimId, CancellationToken.None)).Value!;
                Assert.DoesNotContain("APPROVE", detail.AllowedActions);
                Assert.Contains("REJECT", detail.AllowedActions);
                var refused = await Service(db, port).ProcessReturnActionInstantAsync(seed.TenantId, Guid.NewGuid(), seed.ClaimId, await VersionAsync(seed.ClaimId),
                    new("APPROVE", null, null, [], null), "rl-exch:approve", "rl-exch", CancellationToken.None);
                Assert.Equal(ReturnCustomerRequestPolicy.TrendyolExchangeApproveBlockedCode, refused.Error!.Code);
            }
            Assert.Empty(port.Commands);
            await using (var verify = fixture.CreateContext())
            {
                Assert.False(await verify.ReturnDecisions.AnyAsync(x => x.TenantId == seed.TenantId));
                Assert.Equal(ReturnClaimStatus.ActionRequired, (await verify.ReturnClaims.AsNoTracking().SingleAsync(x => x.Id == seed.ClaimId)).Status);
            }

            // Owner, 18 Nov 2026 ("Stoğa Ekle kalsın"): once Trendyol approves the exchange, the units can still go back
            // to stock from the panel.
            await using (var db = fixture.CreateContext())
            {
                await db.ReturnLines.Where(x => x.ClaimId == seed.ClaimId).ExecuteUpdateAsync(x => x.SetProperty(l => l.RawStatus, "Accepted"));
                await db.ReturnClaims.Where(x => x.Id == seed.ClaimId).ExecuteUpdateAsync(x => x.SetProperty(c => c.Status, ReturnClaimStatus.Approved).SetProperty(c => c.RawStatus, "Accepted"));
            }
            await using (var db = fixture.CreateContext())
            {
                Assert.True((await Service(db, port).ReturnAsync(seed.TenantId, seed.ClaimId, CancellationToken.None)).Value!.StockDispositionAvailable);
                var stocked = await Service(db, port).ApplyDispositionAsync(seed.TenantId, Guid.NewGuid(), seed.ClaimId, new(seed.LineIds[0], "PASS", 1m, "Kontrol edildi"), "rl-exch:stock", "rl-exch", CancellationToken.None);
                Assert.Null(stocked.Error);
            }
            await using (var verify = fixture.CreateContext())
                Assert.Equal([1m, 0m, 0m], await OnHandAsync(verify, seed));
        }
        finally
        {
            await CleanupAsync(seed.TenantId);
        }
    }

    // Owner, 18 Nov 2026: 1000000001's exchange, made with "Değişim Yap", came back as Rejected. With Trendyol's own word
    // that the exchange was made (ResolvedWith CHANGE) it reads "Değişim Onaylandı" and raises no refused-return count;
    // without it, a rejected exchange request stays a rejection.
    [PostgreSqlFact]
    public async Task A_Trendyol_exchange_made_reads_approved_and_is_no_refused_return()
    {
        var seed = await SeedAsync("rl-made");
        var port = new LineReturnPort("RL-CLAIM", seed.ExternalLineIds);
        try
        {
            async Task<(ReturnListView Row, ReturnSummaryView Summary, ReturnDetailView Detail)> ReadAsync()
            {
                await using var db = fixture.CreateContext();
                var service = Service(db, port);
                var rows = await service.ReturnsAsync(seed.TenantId, 20, null, new ReturnListQuery(), false, CancellationToken.None);
                return (Assert.Single(rows.Items), await service.ReturnSummaryAsync(seed.TenantId, new ReturnListQuery(), CancellationToken.None),
                    (await service.ReturnAsync(seed.TenantId, seed.ClaimId, CancellationToken.None)).Value!);
            }
            await using (var db = fixture.CreateContext())
            {
                await db.ReturnLines.Where(x => x.ClaimId == seed.ClaimId).ExecuteUpdateAsync(x => x.SetProperty(l => l.RawStatus, "Rejected"));
                await db.ReturnClaims.Where(x => x.Id == seed.ClaimId).ExecuteUpdateAsync(x => x.SetProperty(c => c.Status, ReturnClaimStatus.Rejected).SetProperty(c => c.RawStatus, "Rejected")
                    .SetProperty(c => c.CustomerRequest, ReturnCustomerRequestPolicy.Change).SetProperty(c => c.RejectedPackageState, ReturnRejectedPackageStates.AwaitingCargo)
                    .SetProperty(c => c.DecidedAt, DateTimeOffset.UtcNow));
            }
            await using (var db = fixture.CreateContext())
                await ReturnCardColumns.RepairAsync(db, CancellationToken.None);
            var unknown = await ReadAsync();
            Assert.False(unknown.Row.ExchangeApproved);
            Assert.Equal("REJECTED", unknown.Row.Status);
            Assert.Equal((0, 1), (unknown.Summary.TabCounts["APPROVED"], unknown.Summary.TabCounts["REJECTED"]));
            Assert.Equal((1, 1), (unknown.Summary.Metrics!.RejectedAwaitingCargo, unknown.Summary.Metrics.TodayRejected));
            // A plain rejection takes no stock; the owner may mark it (owners and admins, checked by the endpoint).
            Assert.False(unknown.Detail.StockDispositionAvailable);
            Assert.True(unknown.Detail.ExchangeMarkAvailable);

            // Owner, 18 Nov 2026: the panel-only mark, through a tracked save (its card columns follow at once).
            await using (var db = fixture.CreateContext())
            {
                var marked = await Service(db, port).MarkExchangeApprovedAsync(seed.TenantId, Guid.NewGuid(), seed.ClaimId, await VersionAsync(seed.ClaimId), true, "rl-made", CancellationToken.None);
                Assert.Null(marked.Error);
                Assert.True(marked.Value!.ExchangeMarkedByOwner);
            }
            Assert.Empty(port.Commands);
            var made = await ReadAsync();
            Assert.True(made.Row.ExchangeApproved);
            Assert.True(made.Detail.ExchangeApproved);
            // The approved tab, never the rejected one, on the stored card columns (fresh) and the rule alike.
            Assert.Equal("EXCHANGE_APPROVED", made.Row.Status);
            Assert.Equal((1, 0), (made.Summary.TabCounts["APPROVED"], made.Summary.TabCounts["REJECTED"]));
            await using (var db = fixture.CreateContext())
            {
                Assert.Equal("APPROVED", (await db.ReturnClaims.AsNoTracking().SingleAsync(x => x.Id == seed.ClaimId)).TabGroup);
                Assert.False(await ReturnCardColumns.Stale(db, seed.TenantId, [], []).AnyAsync());
                var approvedTab = await Service(db, port).ReturnsAsync(seed.TenantId, 20, null, new ReturnListQuery(Status: "APPROVED"), false, CancellationToken.None);
                Assert.Single(approvedTab.Items);
                Assert.Empty((await Service(db, port).ReturnsAsync(seed.TenantId, 20, null, new ReturnListQuery(Status: "REJECTED"), false, CancellationToken.None)).Items);
                Assert.Equal(1, await db.AuditLogs.CountAsync(x => x.TenantId == seed.TenantId && x.Action == "RETURN_EXCHANGE_MARKED_APPROVED"));
            }
            Assert.Equal((0, 0), (made.Summary.Metrics!.RejectedAwaitingCargo, made.Summary.Metrics.TodayRejected));
            // Decision B: Stoğa Ekle stays open on the exchange made, once per unit as always.
            Assert.True(made.Detail.StockDispositionAvailable);
            await using (var db = fixture.CreateContext())
            {
                Assert.Null((await Service(db, port).ApplyDispositionAsync(seed.TenantId, Guid.NewGuid(), seed.ClaimId, new(seed.LineIds[0], "PASS", 2m, "Değişimde geri gelen"), "rl-made:stock", "rl-made", CancellationToken.None)).Error);
                Assert.Equal("RETURN_QUANTITY_EXCEEDED", (await Service(db, port).ApplyDispositionAsync(seed.TenantId, Guid.NewGuid(), seed.ClaimId, new(seed.LineIds[0], "PASS", 1m, "Tekrar"), "rl-made:stock-2", "rl-made", CancellationToken.None)).Error!.Code);
            }

            // A later read that names no outcome keeps the mark; the mark comes off with İşareti Kaldır.
            await using (var db = fixture.CreateContext())
                await new MarketplaceJobProcessor(db, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, new ConfigurationBuilder().Build(), fixture.TimeProvider)
                    .UpsertReturn(seed.TenantId, seed.ConnectionId, new RemoteReturnClaim("RL-CLAIM", "rl-made-order", "Rejected", "WRONGCHANGEREQUEST", null, null, DateTimeOffset.UtcNow,
                        seed.ExternalLineIds.Select(id => new RemoteReturnLine(id, id.Replace("CI-", "OL-", StringComparison.Ordinal), 2m, RawStatus: "Rejected")).ToList(), "{}", CustomerRequest: ReturnCustomerRequestPolicy.Change), CancellationToken.None);
            await using (var db = fixture.CreateContext())
            {
                var kept = await db.ReturnClaims.AsNoTracking().SingleAsync(x => x.Id == seed.ClaimId);
                Assert.Equal((ReturnCustomerRequestPolicy.Change, ReturnCustomerRequestPolicy.ManualSource), (kept.ResolvedWith, kept.ResolvedWithSource));
                // Another tenant cannot reach the claim (M11): not found, nothing changes.
                var foreign = await Service(db, port).MarkExchangeApprovedAsync(Guid.NewGuid(), Guid.NewGuid(), seed.ClaimId, kept.Version, false, "rl-made", CancellationToken.None);
                Assert.Equal(404, foreign.Error!.Status);
                Assert.Equal(ReturnCustomerRequestPolicy.ManualSource, (await db.ReturnClaims.AsNoTracking().SingleAsync(x => x.Id == seed.ClaimId)).ResolvedWithSource);
                var removed = await Service(db, port).MarkExchangeApprovedAsync(seed.TenantId, Guid.NewGuid(), seed.ClaimId, kept.Version, false, "rl-made", CancellationToken.None);
                Assert.Null(removed.Error);
                Assert.False(removed.Value!.ExchangeApproved);
                Assert.Equal("REJECTED", (await db.ReturnClaims.AsNoTracking().SingleAsync(x => x.Id == seed.ClaimId)).TabGroup);
            }

            // Ö4: the mark stands only on a rejected claim. Marked again, then read as accepted by Trendyol: it lapses and the
            // owner is told.
            await using (var db = fixture.CreateContext())
                Assert.Null((await Service(db, port).MarkExchangeApprovedAsync(seed.TenantId, Guid.NewGuid(), seed.ClaimId, await VersionAsync(seed.ClaimId), true, "rl-made", CancellationToken.None)).Error);
            await using (var db = fixture.CreateContext())
                await new MarketplaceJobProcessor(db, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, new ConfigurationBuilder().Build(), fixture.TimeProvider)
                    .UpsertReturn(seed.TenantId, seed.ConnectionId, new RemoteReturnClaim("RL-CLAIM", "rl-made-order", "Accepted", "WRONGCHANGEREQUEST", null, null, DateTimeOffset.UtcNow.AddSeconds(5),
                        seed.ExternalLineIds.Select(id => new RemoteReturnLine(id, id.Replace("CI-", "OL-", StringComparison.Ordinal), 2m, RawStatus: "Accepted")).ToList(), "{}", CustomerRequest: ReturnCustomerRequestPolicy.Change), CancellationToken.None);
            await using (var db = fixture.CreateContext())
            {
                var lapsed = await db.ReturnClaims.AsNoTracking().SingleAsync(x => x.Id == seed.ClaimId);
                Assert.Equal((ReturnClaimStatus.Approved, (string?)null, (string?)null), (lapsed.Status, lapsed.ResolvedWith, lapsed.ResolvedWithSource));
                Assert.True(await db.OperationalIssues.AnyAsync(x => x.TenantId == seed.TenantId && x.Code == "RETURN_EXCHANGE_MARK_LAPSED"));
            }
        }
        finally
        {
            await CleanupAsync(seed.TenantId);
        }
    }

    private sealed record Seed(Guid TenantId, Guid ConnectionId, Guid ClaimId, Guid[] LineIds, string[] ExternalLineIds, Guid[] InventoryItemIds);

    private MarketplaceSalesService Service(AppDbContext db, IReturnPort port) =>
        new(db, new CursorCodec(new EphemeralDataProtectionProvider(), fixture.TimeProvider), new ConfigurationBuilder().Build(), null!, null!, port, null!, TimeProvider.System);

    private async Task<long> VersionAsync(Guid claimId)
    {
        await using var db = fixture.CreateContext();
        return await db.ReturnClaims.AsNoTracking().Where(x => x.Id == claimId).Select(x => x.Version).SingleAsync();
    }

    private static async Task<decimal[]> OnHandAsync(AppDbContext db, Seed seed)
    {
        var items = await db.InventoryItems.AsNoTracking().Where(x => x.TenantId == seed.TenantId).ToDictionaryAsync(x => x.Id, x => x.OnHand);
        return seed.InventoryItemIds.Select(id => items[id]).ToArray();
    }

    private async Task<Seed> SeedAsync(string code)
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"{code}-{Guid.NewGuid():N}"[..20], DisplayName = code, Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var connection = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = "TRENDYOL", Environment = "STAGE", DisplayName = code, ExternalStoreId = $"s-{Guid.NewGuid():N}"[..12], Status = "ACTIVE", ApiVersion = "V1" };
        var order = new Order { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, ExternalOrderId = $"{code}-order", OrderNumber = $"{code}-order", Currency = "TRY", GrossAmount = 300m, NetAmount = 300m, OrderedAt = now.AddDays(-5), LastRemoteModifiedAt = now.AddDays(-5), CustomerSnapshotJson = "{}", ShipmentAddressSnapshotJson = "{}", InvoiceAddressSnapshotJson = "{}", DerivedStatus = "DELIVERED", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var claim = new ReturnClaim { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, OrderId = order.Id, ExternalClaimId = "RL-CLAIM", Status = ReturnClaimStatus.ActionRequired, RawStatus = "WaitingInAction", LastRemoteModifiedAt = now.AddDays(-1), CreatedAt = now, UpdatedAt = now, Version = 1 };
        var product = new Product { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Title = "İade ürünü", Description = "", Status = ProductStatus.Active, CreatedAt = now, UpdatedAt = now };
        var variants = new List<ProductVariant>(); var orderLines = new List<OrderLine>(); var returnLines = new List<ReturnLine>(); var items = new List<InventoryItem>();
        for (var index = 1; index <= 3; index++)
        {
            var sku = $"{code}-SKU-{index}".ToUpperInvariant();
            var variant = new ProductVariant { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProductId = product.Id, SortOrder = index, Sku = sku, SkuNormalized = sku, OptionSignature = $"Beden={index}", CreatedAt = now, UpdatedAt = now, Version = 1 };
            var orderLine = new OrderLine { Id = Guid.CreateVersion7(), TenantId = tenant.Id, OrderId = order.Id, VariantId = variant.Id, ExternalLineId = $"OL-{index}", Sku = sku, TitleSnapshot = $"Ürün {index}", OrderedQuantity = 2m, UnitPrice = 50m, VatRate = 20m, RawStatus = "Delivered" };
            variants.Add(variant); orderLines.Add(orderLine);
            returnLines.Add(new ReturnLine { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ClaimId = claim.Id, OrderLineId = orderLine.Id, ExternalLineId = $"CI-{index}", Quantity = 2m, RawStatus = "WaitingInAction" });
            items.Add(new InventoryItem { Id = Guid.CreateVersion7(), TenantId = tenant.Id, VariantId = variant.Id, LocationCode = "MAIN", OnHand = 0m, Available = 0m });
        }
        await using var db = fixture.CreateContext();
        db.Tenants.Add(tenant);
        db.PlatformConnections.Add(connection);
        db.ConnectionSyncPolicies.Add(new ConnectionSyncPolicy { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, ResourceType = MarketplaceExternalWritePolicies.Return, IntervalSeconds = 0, Enabled = true, Version = 1 });
        db.Orders.Add(order);
        db.Products.Add(product);
        db.ProductVariants.AddRange(variants);
        db.OrderLines.AddRange(orderLines);
        db.InventoryItems.AddRange(items);
        db.ReturnClaims.Add(claim);
        db.ReturnLines.AddRange(returnLines);
        await db.SaveChangesAsync();
        return new(tenant.Id, connection.Id, claim.Id, returnLines.Select(x => x.Id).ToArray(), returnLines.Select(x => x.ExternalLineId).ToArray(), items.Select(x => x.Id).ToArray());
    }

    private async Task CleanupAsync(Guid tenantId)
    {
        await using var db = fixture.CreateContext();
        await db.JobAttempts.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.IntegrationJobs.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.IntegrationOutboxEvents.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.AuditLogs.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.StockLedgerEntries.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.ReturnStockDispositions.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.ReturnEvidence.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.ReturnDecisions.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.ExternalEffectRecords.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.OperationalIssues.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.ReturnLines.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.ReturnClaims.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.InventoryItems.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.OrderLines.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.ProductVariants.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.Products.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.Orders.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.ConnectionSyncPolicies.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.PlatformConnections.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.Tenants.Where(row => row.Id == tenantId).ExecuteDeleteAsync();
    }

    // Trendyol as the return decision sees it: each claim item has its own status, and the claim's status is its
    // first item's.
    private sealed class LineReturnPort(string claimId, IReadOnlyList<string> externalLineIds) : IReturnPort
    {
        private readonly Dictionary<string, string> statuses = externalLineIds.ToDictionary(id => id, _ => "WaitingInAction", StringComparer.Ordinal);
        public List<ReturnActionCommand> Commands { get; } = [];

        public Task<AdapterResult<ReturnActionResult>> ExecuteAsync(AdapterContext context, ReturnActionCommand command, CancellationToken cancellationToken)
        {
            Commands.Add(command);
            foreach (var lineId in command.ExternalLineItemIds) statuses[lineId] = command.Action == "APPROVE" ? "Accepted" : "Rejected";
            return Task.FromResult(AdapterResult<ReturnActionResult>.Success(new(command.ExternalClaimId, statuses[command.ExternalLineItemIds[0]], $"op-{Commands.Count}")));
        }

        public Task<AdapterResult<RemoteReturnClaim>> GetAsync(AdapterContext context, string externalReturnId, CancellationToken cancellationToken) =>
            Task.FromResult(AdapterResult<RemoteReturnClaim>.Success(new(claimId, "rl-order", statuses[externalLineIds[0]], null, null, null, DateTimeOffset.UtcNow,
                externalLineIds.Select(id => new RemoteReturnLine(id, id.Replace("CI-", "OL-", StringComparison.Ordinal), 2m, RawStatus: statuses[id])).ToList(), "{}")));

        public Task<AdapterResult<IReadOnlyList<ReturnIssueReason>>> IssueReasonsAsync(AdapterContext context, CancellationToken cancellationToken) =>
            Task.FromResult(AdapterResult<IReadOnlyList<ReturnIssueReason>>.Success([]));

        public Task<AdapterResult<AdapterPageResult<RemoteReturnClaim>>> PollAsync(AdapterContext context, ReturnPollWindow window, AdapterPageRequest page, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
