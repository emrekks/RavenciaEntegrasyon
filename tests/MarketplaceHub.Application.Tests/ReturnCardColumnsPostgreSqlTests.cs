using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// SIPARIS-STOK-9: a return claim's card columns (TabGroup, CardSplit, ApprovedAt) follow every save of the claim, its lines
// and its decisions, the repair pass fills rows written without them, and the staleness check sees any row that differs.
[Collection("postgres-integration")]
public sealed class ReturnCardColumnsPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    // The database's check reads the tab from the claim's status alone: the lines can only turn an approved or completed
    // claim into PARTIALLY_APPROVED, which is the same tab.
    [Fact]
    public void The_tab_follows_the_status_alone()
    {
        foreach (var status in Enum.GetValues<ReturnClaimStatus>())
        {
            Assert.Equal(ReturnCardColumns.TabGroup(status, []), ReturnCardColumns.TabGroup(status, ["Accepted", "Rejected"]));
            Assert.Equal(ReturnCardColumns.TabGroup(status, []), ReturnCardColumns.TabGroup(status, ["Accepted", null, "WaitingInAction"]));
        }
        Assert.Equal("APPROVED", ReturnCardColumns.TabGroup(ReturnClaimStatus.Completed, ["Accepted", "Rejected"]));
        Assert.Equal("SHIPPING", ReturnCardColumns.TabGroup(ReturnClaimStatus.AwaitingShipment, []));
    }

    [PostgreSqlFact]
    public async Task Every_save_of_a_claim_its_lines_or_its_decisions_keeps_the_columns_true()
    {
        var (tenantId, claimId, orderLineIds) = await SeedAsync();
        try
        {
            async Task<ReturnClaim> ReadAsync()
            {
                await using var read = fixture.CreateContext();
                return await read.ReturnClaims.AsNoTracking().SingleAsync(x => x.Id == claimId);
            }
            async Task AssertFreshAsync()
            {
                await using var read = fixture.CreateContext();
                var raws = await read.ReturnLines.Where(x => x.TenantId == tenantId).Select(x => x.RawStatus).Distinct().ToListAsync();
                var accepted = raws.Where(MarketplaceSalesService.IsAcceptedSplitLineStatus).ToArray();
                var waiting = raws.Where(raw => raw is not null && MarketplaceSalesService.IsWaitingSplitLineStatus(raw)).ToArray();
                Assert.False(await ReturnCardColumns.Stale(read, tenantId, accepted, waiting).AnyAsync());
            }

            // Created with an accepted and a waiting line: two cards, waiting for the seller.
            var claim = await ReadAsync();
            Assert.Equal(("ACTION_REQUIRED", true, (DateTimeOffset?)null), (claim.TabGroup, claim.CardSplit, claim.ApprovedAt));
            await AssertFreshAsync();

            // A line decided (tracked change of the line and the claim).
            var changedAt = new DateTimeOffset(2026, 11, 14, 9, 30, 0, TimeSpan.Zero);
            await using (var db = fixture.CreateContext())
            {
                var tracked = await db.ReturnClaims.SingleAsync(x => x.Id == claimId);
                var waitingLine = await db.ReturnLines.SingleAsync(x => x.ClaimId == claimId && x.RawStatus == "WaitingInAction");
                waitingLine.RawStatus = "Rejected";
                tracked.Status = ReturnClaimStatus.Approved;
                tracked.LastRemoteModifiedAt = changedAt;
                await db.SaveChangesAsync();
            }
            claim = await ReadAsync();
            Assert.Equal(("APPROVED", false, (DateTimeOffset?)changedAt), (claim.TabGroup, claim.CardSplit, claim.ApprovedAt));
            await AssertFreshAsync();

            // A successful approval saved alone (the claim not loaded): its date becomes the approval date.
            var approvedAt = new DateTimeOffset(2026, 11, 14, 11, 0, 0, TimeSpan.Zero);
            await using (var db = fixture.CreateContext())
            {
                db.ReturnDecisions.Add(new ReturnDecision { Id = Guid.CreateVersion7(), TenantId = tenantId, ClaimId = claimId, Action = "APPROVE", IdempotencyKey = $"cols-{claimId:N}", Status = "SUCCEEDED", CreatedAt = approvedAt, CompletedAt = approvedAt });
                await db.SaveChangesAsync();
            }
            Assert.Equal(approvedAt, (await ReadAsync()).ApprovedAt);
            await AssertFreshAsync();

            // A new waiting line saved alone splits the claim again; removing it joins it back.
            var lineId = Guid.CreateVersion7();
            await using (var db = fixture.CreateContext())
            {
                db.ReturnLines.Add(new ReturnLine { Id = lineId, TenantId = tenantId, ClaimId = claimId, OrderLineId = orderLineIds[1], ExternalLineId = "cols-extra", Quantity = 1m, RawStatus = null });
                await db.SaveChangesAsync();
            }
            Assert.True((await ReadAsync()).CardSplit);
            await AssertFreshAsync();
            await using (var db = fixture.CreateContext())
            {
                db.ReturnLines.Remove(await db.ReturnLines.SingleAsync(x => x.Id == lineId));
                await db.SaveChangesAsync();
            }
            Assert.False((await ReadAsync()).CardSplit);
            await AssertFreshAsync();

            // The decision failing afterwards drops its date again.
            await using (var db = fixture.CreateContext())
            {
                (await db.ReturnDecisions.SingleAsync(x => x.ClaimId == claimId)).Status = "FAILED";
                await db.SaveChangesAsync();
            }
            Assert.Equal(changedAt, (await ReadAsync()).ApprovedAt);
            await AssertFreshAsync();
        }
        finally
        {
            await CleanupAsync(tenantId);
        }
    }

    [PostgreSqlFact]
    public async Task The_repair_pass_fills_and_corrects_rows_and_the_check_sees_every_difference()
    {
        var (tenantId, claimId, _) = await SeedAsync();
        try
        {
            await using var db = fixture.CreateContext();
            string?[] accepted = ["Accepted"];
            string?[] waiting = ["WaitingInAction"];
            Task<bool> StaleAsync() => ReturnCardColumns.Stale(db, tenantId, accepted, waiting).AnyAsync();
            Assert.False(await StaleAsync());

            var breakages = new Func<Task>[]
            {
                () => db.ReturnClaims.Where(x => x.Id == claimId).ExecuteUpdateAsync(set => set.SetProperty(x => x.TabGroup, (string?)null).SetProperty(x => x.CardSplit, (bool?)null).SetProperty(x => x.ApprovedAt, (DateTimeOffset?)null)),
                () => db.ReturnClaims.Where(x => x.Id == claimId).ExecuteUpdateAsync(set => set.SetProperty(x => x.TabGroup, "APPROVED")),
                () => db.ReturnClaims.Where(x => x.Id == claimId).ExecuteUpdateAsync(set => set.SetProperty(x => x.CardSplit, false)),
                () => db.ReturnClaims.Where(x => x.Id == claimId).ExecuteUpdateAsync(set => set.SetProperty(x => x.ApprovedAt, DateTimeOffset.UtcNow)),
                // Written behind the save hook's back: the line is no longer waiting, the stored split is stale.
                () => db.ReturnLines.Where(x => x.ClaimId == claimId && x.RawStatus == "WaitingInAction").ExecuteUpdateAsync(set => set.SetProperty(x => x.RawStatus, "Accepted")),
                () => db.ReturnClaims.Where(x => x.Id == claimId).ExecuteUpdateAsync(set => set.SetProperty(x => x.Status, ReturnClaimStatus.Completed)),
            };
            foreach (var breakage in breakages)
            {
                await breakage();
                Assert.True(await StaleAsync());
                var (claims, written) = await ReturnCardColumns.RepairAsync(db, CancellationToken.None, batchSize: 1);
                Assert.True(claims >= 1 && written >= 1);
                Assert.False(await StaleAsync());
                Assert.Equal(0, (await ReturnCardColumns.RepairAsync(db, CancellationToken.None)).Written);
            }
            var repaired = await db.ReturnClaims.AsNoTracking().SingleAsync(x => x.Id == claimId);
            Assert.Equal(("APPROVED", false), (repaired.TabGroup, repaired.CardSplit));
        }
        finally
        {
            await CleanupAsync(tenantId);
        }
    }

    private async Task<(Guid TenantId, Guid ClaimId, Guid[] OrderLineIds)> SeedAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"return-cols-{Guid.NewGuid():N}", DisplayName = "return-cols", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var connection = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = "TRENDYOL", Environment = "PRODUCTION", DisplayName = "Trendyol", ExternalStoreId = $"store-{Guid.NewGuid():N}", Status = "ACTIVE", ApiVersion = "V1" };
        var order = new Order { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, ExternalOrderId = "COLS-1", OrderNumber = "COLS-1", Currency = "TRY", GrossAmount = 100m, NetAmount = 100m, OrderedAt = now, LastRemoteModifiedAt = now, CustomerSnapshotJson = "{}", ShipmentAddressSnapshotJson = "{}", InvoiceAddressSnapshotJson = "{}", DerivedStatus = "DELIVERED", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var orderLines = Enumerable.Range(0, 2).Select(j => new OrderLine { Id = Guid.CreateVersion7(), TenantId = tenant.Id, OrderId = order.Id, ExternalLineId = $"COLS-{j}", Sku = $"COLS-{j}", TitleSnapshot = "Ürün", OrderedQuantity = 1, DeliveredQuantity = 1, ShippedQuantity = 1, UnitPrice = 50m, VatRate = 20m, RawStatus = "Delivered" }).ToList();
        var claim = new ReturnClaim { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, OrderId = order.Id, ExternalClaimId = "cols-claim", Status = ReturnClaimStatus.ActionRequired, RawStatus = "WaitingInAction", LastRemoteModifiedAt = now, CreatedAt = now, UpdatedAt = now, Version = 1 };
        await using var db = fixture.CreateContext();
        db.Tenants.Add(tenant);
        db.PlatformConnections.Add(connection);
        db.Orders.Add(order);
        db.OrderLines.AddRange(orderLines);
        db.ReturnClaims.Add(claim);
        db.ReturnLines.Add(new ReturnLine { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ClaimId = claim.Id, OrderLineId = orderLines[0].Id, ExternalLineId = "cols-0", Quantity = 1m, RawStatus = "Accepted" });
        db.ReturnLines.Add(new ReturnLine { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ClaimId = claim.Id, OrderLineId = orderLines[1].Id, ExternalLineId = "cols-1", Quantity = 1m, RawStatus = "WaitingInAction" });
        await db.SaveChangesAsync();
        return (tenant.Id, claim.Id, orderLines.Select(x => x.Id).ToArray());
    }

    private async Task CleanupAsync(Guid tenantId)
    {
        await using var db = fixture.CreateContext();
        await db.ReturnDecisions.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.ReturnLines.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.ReturnClaims.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.OrderLines.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.Orders.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.PlatformConnections.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.IntegrationOutboxEvents.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.Tenants.Where(row => row.Id == tenantId).ExecuteDeleteAsync();
    }
}
