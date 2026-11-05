using System.Reflection;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Xunit;
using Xunit.Abstractions;

namespace MarketplaceHub.Application.Tests;

// Audit 2026-11-05 RET-01 (an older read of a claim changes nothing the stored read decided), RET-02 (two decisions
// on one claim: only one reaches the marketplace) and YENİ-04 (a manual disposition and the automatic "Stoğa Ekle"
// never put more units than the line has).
[Collection("postgres-integration")]
public sealed class ReturnFreshnessAndConcurrencyPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture, ITestOutputHelper output) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    private static string ConnectionString => Environment.GetEnvironmentVariable("MARKETPLACEHUB_TEST_CONNECTION")!;

    // ---------------------------------------------------------------- RET-01

    // T2 approved (2 units) is stored; an older T1 read (waiting, 1 unit) arrives later: header and line both stay.
    [PostgreSqlFact]
    public async Task AStaleRead_KeepsTheLineQuantityAndStatus()
    {
        var t2 = DateTimeOffset.UtcNow.AddHours(-1);
        var seed = await SeedAsync("r01a", ReturnClaimStatus.Approved, "Accepted", t2, lineQuantity: 2m, lineRawStatus: "Accepted");
        try
        {
            await UpsertReturnAsync(seed, new("R01A", seed.ExternalOrderId, "WaitingInAction", null, null, null, t2.AddHours(-1),
                [new RemoteReturnLine("CI-1", "OL-1", 1m, RawStatus: "WaitingInAction")], "{}"));
            await using var verify = fixture.CreateContext();
            var claim = await verify.ReturnClaims.AsNoTracking().SingleAsync(x => x.Id == seed.ClaimId);
            var line = await verify.ReturnLines.AsNoTracking().SingleAsync(x => x.Id == seed.LineId);
            Assert.Equal(ReturnClaimStatus.Approved, claim.Status);
            Assert.Equal(t2, claim.LastRemoteModifiedAt!.Value, TimeSpan.FromMilliseconds(1));
            Assert.Equal(2m, line.Quantity);
            Assert.Equal("Accepted", line.RawStatus);
        }
        finally { await CleanupAsync(seed.TenantId); }
    }

    // A stale Rejected read neither settles the open APPROVE as a conflict nor drops its pending stock; the next
    // fresh read that shows the line approved still applies it.
    [PostgreSqlFact]
    public async Task AStaleRead_LeavesTheOpenDecisionAndPendingStock_AndTheNextFreshReadAppliesIt()
    {
        var t2 = DateTimeOffset.UtcNow.AddHours(-1);
        var seed = await SeedAsync("r01b", ReturnClaimStatus.Approved, "Accepted", t2, lineQuantity: 1m, lineRawStatus: "Accepted",
            decision: ("APPROVE", "SUBMITTED", pendingStock: true));
        try
        {
            await UpsertReturnAsync(seed, new("R01B", seed.ExternalOrderId, "Rejected", null, null, null, t2.AddHours(-1),
                [new RemoteReturnLine("CI-1", "OL-1", 1m, RawStatus: "Rejected")], "{}"));
            await using (var verify = fixture.CreateContext())
            {
                var claim = await verify.ReturnClaims.AsNoTracking().SingleAsync(x => x.Id == seed.ClaimId);
                var decision = await verify.ReturnDecisions.AsNoTracking().SingleAsync(x => x.ClaimId == seed.ClaimId);
                Assert.Equal(ReturnClaimStatus.Approved, claim.Status);
                Assert.Equal(("SUBMITTED", (string?)null), (decision.Status, decision.ErrorCode));
                Assert.Equal(ReturnPendingStockPolicy.Pending, decision.StockStatus);
                Assert.Equal(0m, (await verify.InventoryItems.AsNoTracking().SingleAsync(x => x.Id == seed.InventoryItemId)).OnHand);
            }

            await UpsertReturnAsync(seed, new("R01B", seed.ExternalOrderId, "Accepted", null, null, null, t2.AddMinutes(1),
                [new RemoteReturnLine("CI-1", "OL-1", 1m, RawStatus: "Accepted")], "{}"));
            await using (var verify = fixture.CreateContext())
            {
                var decision = await verify.ReturnDecisions.AsNoTracking().SingleAsync(x => x.ClaimId == seed.ClaimId);
                Assert.Equal("SUCCEEDED", decision.Status);
                Assert.Equal(ReturnPendingStockPolicy.Applied, decision.StockStatus);
                Assert.Equal(1m, (await verify.InventoryItems.AsNoTracking().SingleAsync(x => x.Id == seed.InventoryItemId)).OnHand);
            }
        }
        finally { await CleanupAsync(seed.TenantId); }
    }

    // Cancelled branch: an older Cancelled read does not delete a fresher claim without history.
    [PostgreSqlFact]
    public async Task AStaleCancelledRead_DoesNotDeleteAFresherClaim()
    {
        var t2 = DateTimeOffset.UtcNow.AddHours(-1);
        var seed = await SeedAsync("r01c", ReturnClaimStatus.ActionRequired, "WaitingInAction", t2, lineQuantity: 1m, lineRawStatus: "WaitingInAction");
        try
        {
            await UpsertReturnAsync(seed, new("R01C", seed.ExternalOrderId, "Cancelled", null, null, null, t2.AddDays(-2),
                [new RemoteReturnLine("CI-1", "OL-1", 1m, RawStatus: "Cancelled")], "{}"));
            await using var verify = fixture.CreateContext();
            var claim = await verify.ReturnClaims.AsNoTracking().SingleAsync(x => x.Id == seed.ClaimId);
            Assert.Equal(ReturnClaimStatus.ActionRequired, claim.Status);
        }
        finally { await CleanupAsync(seed.TenantId); }
    }

    // Cancelled branch with history: an older Cancelled read keeps a fresher Approved claim and its pending stock.
    [PostgreSqlFact]
    public async Task AStaleCancelledRead_DoesNotCancelAFresherApprovedClaimWithHistory()
    {
        var t2 = DateTimeOffset.UtcNow.AddHours(-1);
        var seed = await SeedAsync("r01d", ReturnClaimStatus.Approved, "Accepted", t2, lineQuantity: 1m, lineRawStatus: "Accepted",
            decision: ("APPROVE", "SUBMITTED", pendingStock: true));
        try
        {
            await UpsertReturnAsync(seed, new("R01D", seed.ExternalOrderId, "Cancelled", null, null, null, t2.AddDays(-2),
                [new RemoteReturnLine("CI-1", "OL-1", 1m, RawStatus: "Cancelled")], "{}"));
            await using var verify = fixture.CreateContext();
            var claim = await verify.ReturnClaims.AsNoTracking().SingleAsync(x => x.Id == seed.ClaimId);
            var decision = await verify.ReturnDecisions.AsNoTracking().SingleAsync(x => x.ClaimId == seed.ClaimId);
            Assert.Equal(ReturnClaimStatus.Approved, claim.Status);
            Assert.Equal("Accepted", claim.RawStatus);
            Assert.Equal(ReturnPendingStockPolicy.Pending, decision.StockStatus);
        }
        finally { await CleanupAsync(seed.TenantId); }
    }

    // A fresh Cancelled read still cancels a claim with history (the guard is for older reads only).
    [PostgreSqlFact]
    public async Task AFreshCancelledRead_StillCancelsAClaimWithHistory()
    {
        var t2 = DateTimeOffset.UtcNow.AddHours(-1);
        var seed = await SeedAsync("r01e", ReturnClaimStatus.Approved, "Accepted", t2, lineQuantity: 1m, lineRawStatus: "Accepted",
            decision: ("APPROVE", "SUBMITTED", pendingStock: true));
        try
        {
            await UpsertReturnAsync(seed, new("R01E", seed.ExternalOrderId, "Cancelled", null, null, null, t2.AddMinutes(5),
                [new RemoteReturnLine("CI-1", "OL-1", 1m, RawStatus: "Cancelled")], "{}"));
            await using var verify = fixture.CreateContext();
            var claim = await verify.ReturnClaims.AsNoTracking().SingleAsync(x => x.Id == seed.ClaimId);
            var decision = await verify.ReturnDecisions.AsNoTracking().SingleAsync(x => x.ClaimId == seed.ClaimId);
            Assert.Equal(ReturnClaimStatus.Cancelled, claim.Status);
            Assert.Equal(ReturnPendingStockPolicy.Dropped, decision.StockStatus);
        }
        finally { await CleanupAsync(seed.TenantId); }
    }

    // ---------------------------------------------------------------- RET-02

    // Deterministic interleaving: both requests pass every check and meet right before saving their PENDING decision.
    // Only one is reserved and calls the marketplace; the other gets a 409 without calling it.
    [PostgreSqlFact]
    public async Task TwoConcurrentDecisions_OnlyOneReachesTheMarketplace_TheOtherGetsAConflict()
    {
        var seed = await SeedAsync("r02f", ReturnClaimStatus.ActionRequired, "WaitingInAction", DateTimeOffset.UtcNow.AddDays(-1), lineQuantity: 1m, lineRawStatus: "WaitingInAction");
        try
        {
            var port = new RecordingReturnPort("R02F", ["CI-1"]);
            var rendezvous = new Rendezvous(2, TimeSpan.FromSeconds(5));
            var interceptor = new SavingInterceptor(context => context.ChangeTracker.Entries<ReturnDecision>().Any(e => e.State == EntityState.Added) ? rendezvous.ArriveAsync() : Task.CompletedTask);
            var results = await Task.WhenAll(
                Task.Run(() => DecideAsync(seed, port, "r02f:a", interceptor)),
                Task.Run(() => DecideAsync(seed, port, "r02f:b", interceptor)));
            output.WriteLine($"results: {string.Join(" | ", results)}; provider calls={port.Commands.Count}");
            Assert.True(rendezvous.Met);
            Assert.Single(port.Commands);
            Assert.Single(results, result => result.EndsWith(": OK", StringComparison.Ordinal));
            var loser = Assert.Single(results, result => !result.EndsWith(": OK", StringComparison.Ordinal));
            Assert.True(loser.EndsWith(": RETURN_DECISION_IN_PROGRESS/409", StringComparison.Ordinal) || loser.EndsWith(": CONCURRENCY_CONFLICT/409", StringComparison.Ordinal), loser);
            await using var verify = fixture.CreateContext();
            Assert.Single(await verify.ReturnDecisions.AsNoTracking().Where(x => x.ClaimId == seed.ClaimId).ToListAsync());
        }
        finally { await CleanupAsync(seed.TenantId); }
    }

    // No forced interleaving: two requests released together never both reach the marketplace.
    [PostgreSqlFact]
    public async Task TwoConcurrentDecisions_ReleasedTogether_NeverBothReachTheMarketplace()
    {
        const int runs = 10;
        for (var run = 0; run < runs; run++)
        {
            var seed = await SeedAsync($"r02n{run}", ReturnClaimStatus.ActionRequired, "WaitingInAction", DateTimeOffset.UtcNow.AddDays(-1), lineQuantity: 1m, lineRawStatus: "WaitingInAction");
            try
            {
                var port = new RecordingReturnPort($"R02N{run}", ["CI-1"]);
                var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var a = Task.Run(async () => { await gate.Task; return await DecideAsync(seed, port, $"r02n{run}:a", null); });
                var b = Task.Run(async () => { await gate.Task; return await DecideAsync(seed, port, $"r02n{run}:b", null); });
                gate.SetResult();
                var results = await Task.WhenAll(a, b);
                Assert.True(port.Commands.Count == 1, $"run {run}: {string.Join(" | ", results)}; provider calls={port.Commands.Count}");
            }
            finally { await CleanupAsync(seed.TenantId); }
        }
    }

    // A call that throws after the reservation leaves the decision waiting for the effect check, not stuck PENDING.
    [PostgreSqlFact]
    public async Task AProviderCallThatThrows_LeavesTheReservedDecisionWaitingForTheEffectCheck()
    {
        var seed = await SeedAsync("r02x", ReturnClaimStatus.ActionRequired, "WaitingInAction", DateTimeOffset.UtcNow.AddDays(-1), lineQuantity: 1m, lineRawStatus: "WaitingInAction");
        try
        {
            var port = new RecordingReturnPort("R02X", ["CI-1"], throwOnExecute: true);
            await using (var db = NewContext(null))
            {
                var result = await SalesService(db, port).ProcessReturnActionInstantAsync(seed.TenantId, Guid.NewGuid(), seed.ClaimId, 1, new("APPROVE", null, null, [], [seed.LineId]), "r02x:a", "r02x", CancellationToken.None);
                Assert.Equal("EXTERNAL_EFFECT_VERIFYING", result.Error?.Code);
            }
            await using var verify = fixture.CreateContext();
            var decision = await verify.ReturnDecisions.AsNoTracking().SingleAsync(x => x.ClaimId == seed.ClaimId);
            Assert.Equal(("MANUAL_REVIEW", "EXTERNAL_EFFECT_VERIFYING"), (decision.Status, decision.ErrorCode));
            var effect = await verify.ExternalEffectRecords.AsNoTracking().SingleAsync(x => x.TenantId == seed.TenantId && x.IdempotencyKey == "r02x:a");
            Assert.Null(effect.CompletedAt);
            Assert.True(await verify.IntegrationJobs.AsNoTracking().AnyAsync(x => x.TenantId == seed.TenantId && x.JobType == MarketplaceJobTypes.ExternalEffectReconcile));
        }
        finally { await CleanupAsync(seed.TenantId); }
    }

    private async Task<string> DecideAsync(ReturnSeed seed, IReturnPort port, string key, IInterceptor? interceptor)
    {
        await using var db = NewContext(interceptor);
        var result = await SalesService(db, port).ProcessReturnActionInstantAsync(seed.TenantId, Guid.NewGuid(), seed.ClaimId, 1, new("APPROVE", null, null, [], [seed.LineId]), key, key, CancellationToken.None);
        return $"{key}: {(result.Error is null ? "OK" : $"{result.Error.Code}/{result.Error.Status}")}";
    }

    // ---------------------------------------------------------------- YENİ-04

    // An approved 1-unit line whose "Stoğa Ekle" is still pending. The manual QUARANTINE reaches its save first; the
    // automatic settlement of a fresh read runs meanwhile and waits for the line, then finds the unit already placed.
    [PostgreSqlFact]
    public async Task AManualDispositionFirst_TheAutomaticStoğaEkleWaitsAndDoesNotPlaceTheUnitAgain()
    {
        var seed = await SeedAsync("y04m", ReturnClaimStatus.Approved, "Accepted", DateTimeOffset.UtcNow.AddHours(-1), lineQuantity: 1m, lineRawStatus: "Accepted",
            decision: ("APPROVE", "SUCCEEDED", pendingStock: true));
        try
        {
            var manualReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var interceptor = new SavingInterceptor(async context =>
            {
                if (!context.ChangeTracker.Entries<ReturnStockDisposition>().Any(e => e.State == EntityState.Added)) return;
                manualReady.TrySetResult();
                // Hold the line while the automatic settlement runs; without the lock it would finish in this time.
                await Task.Delay(TimeSpan.FromSeconds(1.5));
            });
            var manual = Task.Run(async () =>
            {
                await using var db = NewContext(interceptor);
                var result = await SalesService(db, new RecordingReturnPort("Y04M", ["CI-1"])).ApplyDispositionAsync(seed.TenantId, Guid.NewGuid(), seed.ClaimId,
                    new(seed.LineId, "QUARANTINE", 1m, "Karantina"), "y04m:manual", "y04m", CancellationToken.None);
                return result.Error is null ? "OK" : result.Error.Code;
            });
            await Task.WhenAny(manualReady.Task, Task.Delay(TimeSpan.FromSeconds(10)));
            Assert.True(manualReady.Task.IsCompleted, "manual disposition did not reach its save");
            var auto = Task.Run(() => UpsertReturnAsync(seed, AcceptedRead("Y04M", seed)));
            Assert.Equal("OK", await manual);
            await auto;
            await using var verify = fixture.CreateContext();
            var dispositions = await verify.ReturnStockDispositions.AsNoTracking().Where(x => x.ReturnLineId == seed.LineId).ToListAsync();
            var decision = await verify.ReturnDecisions.AsNoTracking().SingleAsync(x => x.ClaimId == seed.ClaimId);
            output.WriteLine($"dispositions: {string.Join(", ", dispositions.Select(d => $"{d.Disposition}:{d.Quantity}"))}; stock={decision.StockStatus}");
            Assert.Equal(1m, dispositions.Sum(d => d.Quantity));
            Assert.Equal(ReturnStockDispositionKind.Quarantine, Assert.Single(dispositions).Disposition);
            Assert.Equal(ReturnPendingStockPolicy.Dropped, decision.StockStatus);
            Assert.Equal(0m, (await verify.InventoryItems.AsNoTracking().SingleAsync(x => x.Id == seed.InventoryItemId)).OnHand);
        }
        finally { await CleanupAsync(seed.TenantId); }
    }

    // The other order: the automatic settlement holds the line; the manual QUARANTINE waits for it, then is refused.
    [PostgreSqlFact]
    public async Task TheAutomaticStoğaEkleFirst_AManualDispositionWaitsAndIsRefusedForTheFullLine()
    {
        var seed = await SeedAsync("y04a", ReturnClaimStatus.Approved, "Accepted", DateTimeOffset.UtcNow.AddHours(-1), lineQuantity: 1m, lineRawStatus: "Accepted",
            decision: ("APPROVE", "SUCCEEDED", pendingStock: true));
        try
        {
            var autoReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var interceptor = new SavingInterceptor(async context =>
            {
                if (!context.ChangeTracker.Entries<ReturnStockDisposition>().Any(e => e.State == EntityState.Added)) return;
                autoReady.TrySetResult();
                // Hold the line while the manual disposition runs; without the lock it would finish in this time.
                await Task.Delay(TimeSpan.FromSeconds(1.5));
            });
            var auto = Task.Run(async () =>
            {
                await using var db = NewContext(interceptor);
                await UpsertReturnAsync(db, seed, AcceptedRead("Y04A", seed));
            });
            await Task.WhenAny(autoReady.Task, Task.Delay(TimeSpan.FromSeconds(10)));
            Assert.True(autoReady.Task.IsCompleted, "automatic settlement did not reach its save");
            var manual = Task.Run(async () =>
            {
                await using var db = NewContext(null);
                var result = await SalesService(db, new RecordingReturnPort("Y04A", ["CI-1"])).ApplyDispositionAsync(seed.TenantId, Guid.NewGuid(), seed.ClaimId,
                    new(seed.LineId, "QUARANTINE", 1m, "Karantina"), "y04a:manual", "y04a", CancellationToken.None);
                return result.Error is null ? "OK" : result.Error.Code;
            });
            await auto;
            Assert.Equal("RETURN_QUANTITY_EXCEEDED", await manual);
            await using var verify = fixture.CreateContext();
            var dispositions = await verify.ReturnStockDispositions.AsNoTracking().Where(x => x.ReturnLineId == seed.LineId).ToListAsync();
            Assert.Equal(ReturnStockDispositionKind.Pass, Assert.Single(dispositions).Disposition);
            Assert.Equal(1m, (await verify.InventoryItems.AsNoTracking().SingleAsync(x => x.Id == seed.InventoryItemId)).OnHand);
        }
        finally { await CleanupAsync(seed.TenantId); }
    }

    private static RemoteReturnClaim AcceptedRead(string claimId, ReturnSeed seed) =>
        new(claimId, seed.ExternalOrderId, "Accepted", null, null, null, DateTimeOffset.UtcNow, [new RemoteReturnLine("CI-1", "OL-1", 1m, RawStatus: "Accepted")], "{}");

    // ---------------------------------------------------------------- helpers

    private sealed record ReturnSeed(Guid TenantId, Guid ConnectionId, Guid ClaimId, Guid LineId, Guid InventoryItemId, string ExternalOrderId);

    private async Task<ReturnSeed> SeedAsync(string code, ReturnClaimStatus status, string rawStatus, DateTimeOffset lastRemoteModifiedAt, decimal lineQuantity, string lineRawStatus,
        (string Action, string Status, bool pendingStock)? decision = null)
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"{code}-{Guid.NewGuid():N}"[..24], DisplayName = code, Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var connection = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = "TRENDYOL", Environment = "STAGE", DisplayName = code, ExternalStoreId = $"s-{Guid.NewGuid():N}"[..12], Status = "ACTIVE", ApiVersion = "V1" };
        var order = new Order { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, ExternalOrderId = $"{code}-order", OrderNumber = $"{code}-order", Currency = "TRY", GrossAmount = 100m, NetAmount = 100m, OrderedAt = now.AddDays(-5), LastRemoteModifiedAt = now.AddDays(-5), CustomerSnapshotJson = "{}", ShipmentAddressSnapshotJson = "{}", InvoiceAddressSnapshotJson = "{}", DerivedStatus = "DELIVERED", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var product = new Product { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Title = "İade ürünü", Description = "", Status = ProductStatus.Active, CreatedAt = now, UpdatedAt = now };
        var sku = $"{code}-SKU".ToUpperInvariant();
        var variant = new ProductVariant { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProductId = product.Id, SortOrder = 1, Sku = sku, SkuNormalized = sku, OptionSignature = "Beden=1", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var orderLine = new OrderLine { Id = Guid.CreateVersion7(), TenantId = tenant.Id, OrderId = order.Id, VariantId = variant.Id, ExternalLineId = "OL-1", Sku = sku, TitleSnapshot = "Ürün", OrderedQuantity = 2m, UnitPrice = 50m, VatRate = 20m, RawStatus = "Delivered" };
        var item = new InventoryItem { Id = Guid.CreateVersion7(), TenantId = tenant.Id, VariantId = variant.Id, LocationCode = "MAIN", OnHand = 0m, Available = 0m };
        var claim = new ReturnClaim { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, OrderId = order.Id, ExternalClaimId = code.ToUpperInvariant(), Status = status, RawStatus = rawStatus, LastRemoteModifiedAt = lastRemoteModifiedAt, CreatedAt = now, UpdatedAt = now, Version = 1 };
        var line = new ReturnLine { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ClaimId = claim.Id, OrderLineId = orderLine.Id, ExternalLineId = "CI-1", Quantity = lineQuantity, RawStatus = lineRawStatus };
        await using var db = fixture.CreateContext();
        db.Tenants.Add(tenant);
        db.PlatformConnections.Add(connection);
        db.ConnectionSyncPolicies.Add(new ConnectionSyncPolicy { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, ResourceType = MarketplaceExternalWritePolicies.Return, IntervalSeconds = 0, Enabled = true, Version = 1 });
        db.Orders.Add(order);
        db.Products.Add(product);
        db.ProductVariants.Add(variant);
        db.OrderLines.Add(orderLine);
        db.InventoryItems.Add(item);
        db.ReturnClaims.Add(claim);
        db.ReturnLines.Add(line);
        if (decision is { } d)
            db.ReturnDecisions.Add(new ReturnDecision
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenant.Id,
                ClaimId = claim.Id,
                Action = d.Action,
                IdempotencyKey = $"{code}:decision:{Guid.NewGuid():N}",
                Status = d.Status,
                ActorUserId = Guid.NewGuid(),
                CreatedAt = now.AddHours(-3),
                ReturnLineIds = [line.Id],
                StockLinesJson = d.pendingStock ? ReturnPendingStock.Serialize([new ReturnPendingStockLine(line.Id, null)]) : null,
                StockStatus = d.pendingStock ? ReturnPendingStockPolicy.Pending : null
            });
        await db.SaveChangesAsync();
        return new(tenant.Id, connection.Id, claim.Id, line.Id, item.Id, order.ExternalOrderId);
    }

    private async Task UpsertReturnAsync(ReturnSeed seed, RemoteReturnClaim remote)
    {
        await using var db = fixture.CreateContext();
        await UpsertReturnAsync(db, seed, remote);
    }

    private async Task UpsertReturnAsync(AppDbContext db, ReturnSeed seed, RemoteReturnClaim remote)
    {
        var processor = new MarketplaceJobProcessor(db, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, new ConfigurationBuilder().Build(), fixture.TimeProvider);
        var method = typeof(MarketplaceJobProcessor).GetMethod("UpsertReturn", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)method.Invoke(processor, [seed.TenantId, seed.ConnectionId, remote, CancellationToken.None])!;
    }

    private static AppDbContext NewContext(IInterceptor? interceptor)
    {
        var builder = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(ConnectionString);
        if (interceptor is not null) builder.AddInterceptors(interceptor);
        return new AppDbContext(builder.Options);
    }

    private MarketplaceSalesService SalesService(AppDbContext db, IReturnPort port) =>
        new(db, new CursorCodec(new EphemeralDataProtectionProvider(), fixture.TimeProvider), new ConfigurationBuilder().Build(), null!, null!, port, null!, TimeProvider.System);

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

    private sealed class Rendezvous(int parties, TimeSpan timeout)
    {
        private int arrived;
        private readonly TaskCompletionSource all = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Met => all.Task.IsCompleted;
        public async Task ArriveAsync()
        {
            if (Interlocked.Increment(ref arrived) >= parties) all.TrySetResult();
            await Task.WhenAny(all.Task, Task.Delay(timeout));
        }
    }

    private sealed class SavingInterceptor(Func<DbContext, Task> beforeSave) : SaveChangesInterceptor
    {
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context is { } context) await beforeSave(context);
            return result;
        }
    }

    private sealed class RecordingReturnPort(string claimId, IReadOnlyList<string> externalLineIds, bool throwOnExecute = false) : IReturnPort
    {
        private readonly object gate = new();
        private readonly List<ReturnActionCommand> commands = [];
        public IReadOnlyList<ReturnActionCommand> Commands { get { lock (gate) return commands.ToList(); } }

        public async Task<AdapterResult<ReturnActionResult>> ExecuteAsync(AdapterContext context, ReturnActionCommand command, CancellationToken cancellationToken)
        {
            lock (gate) commands.Add(command);
            await Task.Delay(50, cancellationToken);
            if (throwOnExecute) throw new HttpRequestException("connection reset");
            return AdapterResult<ReturnActionResult>.Success(new(command.ExternalClaimId, "Accepted", $"op-{Guid.NewGuid():N}"));
        }

        public Task<AdapterResult<RemoteReturnClaim>> GetAsync(AdapterContext context, string externalReturnId, CancellationToken cancellationToken) =>
            Task.FromResult(AdapterResult<RemoteReturnClaim>.Success(new(claimId, "order", "Accepted", null, null, null, DateTimeOffset.UtcNow,
                externalLineIds.Select(id => new RemoteReturnLine(id, id.Replace("CI-", "OL-", StringComparison.Ordinal), 1m, RawStatus: "Accepted")).ToList(), "{}")));

        public Task<AdapterResult<IReadOnlyList<ReturnIssueReason>>> IssueReasonsAsync(AdapterContext context, CancellationToken cancellationToken) =>
            Task.FromResult(AdapterResult<IReadOnlyList<ReturnIssueReason>>.Success([]));

        public Task<AdapterResult<AdapterPageResult<RemoteReturnClaim>>> PollAsync(AdapterContext context, ReturnPollWindow window, AdapterPageRequest page, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
