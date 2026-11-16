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
                var accepted = raws.Where(ReturnQueryService.IsAcceptedSplitLineStatus).ToArray();
                var waiting = raws.Where(raw => raw is not null && ReturnQueryService.IsWaitingSplitLineStatus(raw)).ToArray();
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
                var (claims, written, _) = await ReturnCardColumns.RepairAsync(db, CancellationToken.None, batchSize: 1);
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

    // Review finding 1: a save that does not change the claim (a decision saved alone, SIPARIS-STOK-5) must not depend on the
    // claim's version: another process changing the claim meanwhile must not make it fail. The columns follow after it.
    [PostgreSqlFact]
    public async Task A_save_that_leaves_the_claim_alone_never_fails_on_the_claims_version()
    {
        var (tenantId, claimId, _) = await SeedAsync();
        try
        {
            await using var caller = fixture.CreateContext();
            var claim = await caller.ReturnClaims.SingleAsync(x => x.Id == claimId);
            await using (var other = fixture.CreateContext())
            {
                var changed = await other.ReturnClaims.SingleAsync(x => x.Id == claimId);
                changed.Status = ReturnClaimStatus.Approved;
                changed.Version++;
                await other.SaveChangesAsync();
            }
            var approvedAt = new DateTimeOffset(2026, 11, 15, 8, 0, 0, TimeSpan.Zero);
            caller.ReturnDecisions.Add(new ReturnDecision { Id = Guid.CreateVersion7(), TenantId = tenantId, ClaimId = claimId, Action = "APPROVE", IdempotencyKey = $"ver-{claimId:N}", Status = "SUCCEEDED", CreatedAt = approvedAt, CompletedAt = approvedAt });
            await caller.SaveChangesAsync();
            Assert.Equal(EntityState.Unchanged, caller.Entry(claim).State);
            await using var read = fixture.CreateContext();
            var stored = await read.ReturnClaims.AsNoTracking().SingleAsync(x => x.Id == claimId);
            Assert.Equal(("APPROVED", (DateTimeOffset?)approvedAt), (stored.TabGroup, stored.ApprovedAt));
        }
        finally
        {
            await CleanupAsync(tenantId);
        }
    }

    // Review finding 2: the hook never starts tracking a claim the caller read without tracking.
    [PostgreSqlFact]
    public async Task The_save_hook_never_starts_tracking_a_claim()
    {
        var (tenantId, claimId, _) = await SeedAsync();
        try
        {
            await using var caller = fixture.CreateContext();
            caller.ReturnDecisions.Add(new ReturnDecision { Id = Guid.CreateVersion7(), TenantId = tenantId, ClaimId = claimId, Action = "REJECT", IdempotencyKey = $"track-{claimId:N}", Status = "PENDING", CreatedAt = DateTimeOffset.UtcNow });
            await caller.SaveChangesAsync();
            Assert.Empty(caller.ChangeTracker.Entries<ReturnClaim>());
        }
        finally
        {
            await CleanupAsync(tenantId);
        }
    }

    // Review finding 3: a line the caller loaded and left unchanged reads as the database has it now, not as it was loaded.
    [PostgreSqlFact]
    public async Task An_unchanged_tracked_line_reads_as_the_database_has_it()
    {
        var (tenantId, claimId, _) = await SeedAsync();
        try
        {
            await using var caller = fixture.CreateContext();
            var claim = await caller.ReturnClaims.SingleAsync(x => x.Id == claimId);
            _ = await caller.ReturnLines.Where(x => x.ClaimId == claimId).ToListAsync();
            await using (var other = fixture.CreateContext())
                await other.ReturnLines.Where(x => x.ClaimId == claimId && x.RawStatus == "WaitingInAction").ExecuteUpdateAsync(set => set.SetProperty(x => x.RawStatus, "Accepted"));
            claim.ReasonText = "changed";
            await caller.SaveChangesAsync();
            await using var read = fixture.CreateContext();
            Assert.False((await read.ReturnClaims.AsNoTracking().SingleAsync(x => x.Id == claimId)).CardSplit);
        }
        finally
        {
            await CleanupAsync(tenantId);
        }
    }

    // Review finding 4: the hook failing outside a transaction leaves the save done (the read check falls back on the stale
    // claim); inside the caller's transaction it fails the save.
    [PostgreSqlFact]
    public async Task A_failing_hook_never_fails_a_save_outside_a_transaction()
    {
        var (tenantId, claimId, _) = await SeedAsync();
        try
        {
            var failing = new FailingReturnReads();
            AppDbContext Context() => new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(Environment.GetEnvironmentVariable("MARKETPLACEHUB_TEST_CONNECTION")).AddInterceptors(failing).Options);
            await using (var caller = Context())
            {
                caller.ReturnDecisions.Add(new ReturnDecision { Id = Guid.CreateVersion7(), TenantId = tenantId, ClaimId = claimId, Action = "REJECT", IdempotencyKey = $"fail-a-{claimId:N}", Status = "PENDING", CreatedAt = DateTimeOffset.UtcNow });
                (await caller.ReturnClaims.SingleAsync(x => x.Id == claimId)).ReasonText = "changed";
                failing.Armed = true;
                await caller.SaveChangesAsync();
                failing.Armed = false;
            }
            Assert.True(failing.Failed > 0);
            await using (var read = fixture.CreateContext())
            {
                Assert.True(await read.ReturnDecisions.AnyAsync(x => x.IdempotencyKey == $"fail-a-{claimId:N}"));
                Assert.Equal("changed", (await read.ReturnClaims.AsNoTracking().SingleAsync(x => x.Id == claimId)).ReasonText);
            }
            await using (var caller = Context())
            {
                await using var transaction = await caller.Database.BeginTransactionAsync();
                caller.ReturnDecisions.Add(new ReturnDecision { Id = Guid.CreateVersion7(), TenantId = tenantId, ClaimId = claimId, Action = "REJECT", IdempotencyKey = $"fail-b-{claimId:N}", Status = "PENDING", CreatedAt = DateTimeOffset.UtcNow });
                failing.Armed = true;
                await Assert.ThrowsAnyAsync<Exception>(() => caller.SaveChangesAsync());
                failing.Armed = false;
            }
        }
        finally
        {
            await CleanupAsync(tenantId);
        }
    }

    // Review finding 5: the repair pass says which tenants are still off the columns after it.
    [PostgreSqlFact]
    public async Task The_repair_pass_names_the_tenants_still_stale()
    {
        var (tenantId, claimId, _) = await SeedAsync();
        try
        {
            await using var db = fixture.CreateContext();
            Assert.DoesNotContain(tenantId, await ReturnCardColumns.StaleTenantsAsync(db, CancellationToken.None));
            await db.ReturnClaims.Where(x => x.Id == claimId).ExecuteUpdateAsync(set => set.SetProperty(x => x.CardSplit, false));
            Assert.Contains(tenantId, await ReturnCardColumns.StaleTenantsAsync(db, CancellationToken.None));
            var result = await ReturnCardColumns.RepairAsync(db, CancellationToken.None);
            Assert.True(result.Written >= 1 && result.Lost == 0);
            Assert.DoesNotContain(tenantId, await ReturnCardColumns.StaleTenantsAsync(db, CancellationToken.None));
        }
        finally
        {
            await CleanupAsync(tenantId);
        }
    }

    // Review finding 6: the staleness check and the counts are separate reads; a claim written without its columns in
    // between (the previous worker during a deploy) still counts in its tab: the tab counts follow the claims' statuses
    // (SIPARIS-STOK-9), never the stored tab.
    [PostgreSqlFact]
    public async Task A_claim_unfilled_after_the_check_still_counts_in_its_tab()
    {
        var (tenantId, claimId, _) = await SeedAsync();
        try
        {
            await using var db = fixture.CreateContext();
            var service = new MarketplaceSalesService(db, new CursorCodec(new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider(), TimeProvider.System), new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), null!, null!, null!, null!, TimeProvider.System);
            var fresh = await service.ReturnQueries.ReturnColumnStateAsync(tenantId, CancellationToken.None);
            Assert.True(fresh.Fresh);
            await db.ReturnClaims.Where(x => x.Id == claimId).ExecuteUpdateAsync(set => set.SetProperty(x => x.TabGroup, (string?)null));
            var counts = ReturnQueryService.ReturnTabs.ToDictionary(tab => tab, _ => 0, StringComparer.Ordinal);
            var options = new ReturnListQuery(Status: "APPROVED");
            var set = await service.ReturnQueries.ReturnCardSetAsync(tenantId, options, applyTab: false, applyRefinements: true, fresh, CancellationToken.None);
            await service.ReturnQueries.CountReturnCardsAsync(tenantId, set!, counts, CancellationToken.None);
            var expected = await LegacyReturnListQueries.BuildReturnSummaryAsync(service, db, tenantId, options, CancellationToken.None);
            Assert.Equal(System.Text.Json.JsonSerializer.Serialize(expected.TabCounts), System.Text.Json.JsonSerializer.Serialize(counts));
            Assert.True(counts["ALL"] > 0);
        }
        finally
        {
            await CleanupAsync(tenantId);
        }
    }

    // Fails the hook's own reads (the approvals it reads from sales.return_decisions) while armed.
    private sealed class FailingReturnReads : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        public volatile bool Armed;
        public int Failed;

        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(System.Data.Common.DbCommand command, Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData, Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Armed && command.CommandText.Contains("FROM sales.return_decisions", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref Failed);
                throw new InvalidOperationException("return decisions unreadable (test)");
            }
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
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
