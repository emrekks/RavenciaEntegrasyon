using System.Globalization;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// SIPARIS-STOK-8 safety net: what each place that moves the panel's own stock writes (on-hand, reserved, available,
// versions, the ledger row, rounding, the stock-shortfall notice, a repeated idempotency key). The copies differ on
// purpose or by drift; these tests pin each one as it is, so moving them behind one writer changes none of it.
[Collection("postgres-integration")]
public sealed class StockMovementCharacterizationPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    private static readonly Guid Actor = Guid.Parse("0196f000-0000-7000-8000-000000000001");

    // Manual adjustment: the new on-hand and the ledger delta are both rounded to 4 places (to even), the open shortfall
    // notice of the item closes, and the same key again changes nothing.
    [PostgreSqlFact]
    public async Task ManualAdjust_RoundsOnHandAndLedgerToEven_ClosesTheShortfallNotice_AndARepeatedKeyChangesNothing()
    {
        var seed = await SeedAsync("sm-adjust");
        try
        {
            var key = $"sm-adjust-{Guid.NewGuid():N}";
            for (var attempt = 0; attempt < 2; attempt++)
            {
                await using var db = fixture.CreateContext();
                var result = await Inventory(db).AdjustAsync(seed.TenantId, Actor, seed.VariantId, new StockAdjustmentCommand(1.00005m, " Sayım ", " ev-1 "), key, "corr-adjust", CancellationToken.None);
                Assert.True(result.Succeeded, result.Error?.Message);
                Assert.Equal(6m, result.Value!.OnHand);
            }
            await using var verify = fixture.CreateContext();
            var item = await verify.InventoryItems.AsNoTracking().SingleAsync(x => x.Id == seed.ItemId);
            Assert.Equal((6.0000m, 0m, 6.0000m, 2L, 2L), (item.OnHand, item.Reserved, item.Available, item.ProjectionVersion, item.Version));
            var row = Assert.Single(await MovementsAsync(verify, seed));
            Assert.Equal(("MANUAL_ADJUSTMENT", 1.0000m, "MANUAL", "Sayım", "ev-1", key, (Guid?)Actor, "corr-adjust"),
                (row.MovementType, row.QuantityDelta, row.SourceType, row.SourceId, row.SourceEventId, row.IdempotencyKey, row.ActorUserId, row.CorrelationId));
            Assert.Equal(IssueStatus.Resolved, (await ShortfallIssueAsync(verify, seed)).Status);
        }
        finally
        {
            await CleanupAsync(seed.TenantId);
        }
    }

    // Stock count set by hand: the ledger takes the difference, the reserved units are recounted from the active
    // reservations, the shortfall notice closes, and the same key again changes nothing.
    [PostgreSqlFact]
    public async Task SetOnHand_WritesTheDifference_RecountsReserved_ClosesTheShortfallNotice_AndARepeatedKeyChangesNothing()
    {
        var seed = await SeedAsync("sm-set");
        try
        {
            var key = $"sm-set-{Guid.NewGuid():N}";
            for (var attempt = 0; attempt < 2; attempt++)
            {
                await using var db = fixture.CreateContext();
                var result = await Inventory(db).SetOnHandAsync(seed.TenantId, Actor, seed.VariantId, new StockSetCommand(8m, 5m, "Sayım", "ev-set"), key, "corr-set", CancellationToken.None);
                Assert.True(result.Succeeded, result.Error?.Message);
            }
            await using var verify = fixture.CreateContext();
            var item = await verify.InventoryItems.AsNoTracking().SingleAsync(x => x.Id == seed.ItemId);
            Assert.Equal((8m, 0m, 8m, 2L, 2L), (item.OnHand, item.Reserved, item.Available, item.ProjectionVersion, item.Version));
            var row = Assert.Single(await MovementsAsync(verify, seed));
            Assert.Equal(("MANUAL_ADJUSTMENT", 3m, "MANUAL", "Sayım", "ev-set", key, (Guid?)Actor, "corr-set"),
                (row.MovementType, row.QuantityDelta, row.SourceType, row.SourceId, row.SourceEventId, row.IdempotencyKey, row.ActorUserId, row.CorrelationId));
            Assert.Equal(IssueStatus.Resolved, (await ShortfallIssueAsync(verify, seed)).Status);
        }
        finally
        {
            await CleanupAsync(seed.TenantId);
        }
    }

    // "Stoğa Ekle" on one return line by hand: the quantity goes in as given (no rounding in the code; PostgreSQL rounds
    // half away from zero when it stores 4 places), the ledger row carries the request's key, the shortfall notice
    // stays open, and the same key again changes nothing.
    [PostgreSqlFact]
    public async Task ManualDisposition_Pass_IsNotRoundedInCode_LeavesTheShortfallNoticeOpen_AndARepeatedKeyChangesNothing()
    {
        var seed = await SeedAsync("sm-pass");
        try
        {
            var key = $"sm-pass-{Guid.NewGuid():N}";
            for (var attempt = 0; attempt < 2; attempt++)
            {
                await using var db = fixture.CreateContext();
                var result = await Sales(db).ApplyDispositionAsync(seed.TenantId, Actor, seed.ClaimId, new(seed.ReturnLineId, "PASS", 1.00005m, " Kontrol edildi "), key, "corr-pass", CancellationToken.None);
                Assert.Null(result.Error);
            }
            await using var verify = fixture.CreateContext();
            var item = await verify.InventoryItems.AsNoTracking().SingleAsync(x => x.Id == seed.ItemId);
            Assert.Equal((6.0001m, 0m, 6.0001m, 2L, 2L), (item.OnHand, item.Reserved, item.Available, item.ProjectionVersion, item.Version));
            var row = Assert.Single(await MovementsAsync(verify, seed));
            Assert.Equal(("RETURN_PASS", 1.0001m, "RETURN_CLAIM", "SM-CLAIM", key, key, (Guid?)Actor, "corr-pass"),
                (row.MovementType, row.QuantityDelta, row.SourceType, row.SourceId, row.SourceEventId, row.IdempotencyKey, row.ActorUserId, row.CorrelationId));
            var disposition = Assert.Single(await verify.ReturnStockDispositions.AsNoTracking().Where(x => x.TenantId == seed.TenantId).ToListAsync());
            Assert.Equal((ReturnStockDispositionKind.Pass, 1.0001m, key, "Kontrol edildi", seed.ItemId), (disposition.Disposition, disposition.Quantity, disposition.IdempotencyKey, disposition.Reason, disposition.InventoryItemId));
            Assert.Equal(IssueStatus.Open, (await ShortfallIssueAsync(verify, seed)).Status);
        }
        finally
        {
            await CleanupAsync(seed.TenantId);
        }
    }

    [PostgreSqlFact]
    public async Task ManualDisposition_Quarantine_MovesNoStockAndWritesNoLedgerRow()
    {
        var seed = await SeedAsync("sm-quarantine");
        try
        {
            await using (var db = fixture.CreateContext())
            {
                var result = await Sales(db).ApplyDispositionAsync(seed.TenantId, Actor, seed.ClaimId, new(seed.ReturnLineId, "QUARANTINE", 1m, "Hasarlı olabilir"), $"sm-q-{Guid.NewGuid():N}", "corr-q", CancellationToken.None);
                Assert.Null(result.Error);
            }
            await using var verify = fixture.CreateContext();
            var item = await verify.InventoryItems.AsNoTracking().SingleAsync(x => x.Id == seed.ItemId);
            Assert.Equal((5m, 0m, 5m, 1L, 1L), (item.OnHand, item.Reserved, item.Available, item.ProjectionVersion, item.Version));
            Assert.Empty(await MovementsAsync(verify, seed));
            Assert.Equal(1, await verify.ReturnStockDispositions.CountAsync(x => x.TenantId == seed.TenantId && x.Disposition == ReturnStockDispositionKind.Quarantine));
        }
        finally
        {
            await CleanupAsync(seed.TenantId);
        }
    }

    // "Stoğa Ekle" kept with an approval and settled: one RETURN_PASS row under "{decision key}:stock:{line}", the
    // decision's actor, the units planned; the shortfall notice stays open; settling again adds nothing.
    [PostgreSqlFact]
    public async Task PendingStock_Settled_WritesOneRowUnderTheLineKey_LeavesTheShortfallNoticeOpen_AndSettlingAgainAddsNothing()
    {
        var seed = await SeedAsync("sm-pending");
        try
        {
            await using (var db = fixture.CreateContext())
            {
                db.ReturnDecisions.Add(new ReturnDecision { Id = Guid.CreateVersion7(), TenantId = seed.TenantId, ClaimId = seed.ClaimId, Action = "APPROVE", IdempotencyKey = "sm-pending:approve", Status = "SUCCEEDED", ActorUserId = Actor, CreatedAt = DateTimeOffset.UtcNow, ReturnLineIds = [seed.ReturnLineId], StockLinesJson = ReturnPendingStock.Serialize([new(seed.ReturnLineId, 1m)]), StockStatus = ReturnPendingStockPolicy.Pending });
                await db.SaveChangesAsync();
            }
            for (var attempt = 0; attempt < 2; attempt++)
            {
                await using var db = fixture.CreateContext();
                var decision = await db.ReturnDecisions.SingleAsync(x => x.TenantId == seed.TenantId);
                decision.StockStatus = ReturnPendingStockPolicy.Pending;
                await ReturnPendingStock.SettleAndSaveAsync(db, () => ReturnPendingStock.SettleAsync(db, decision, force: true, "corr-pending", DateTimeOffset.UtcNow, CancellationToken.None), CancellationToken.None);
            }
            await using var verify = fixture.CreateContext();
            var item = await verify.InventoryItems.AsNoTracking().SingleAsync(x => x.Id == seed.ItemId);
            Assert.Equal((6m, 0m, 6m, 2L, 2L), (item.OnHand, item.Reserved, item.Available, item.ProjectionVersion, item.Version));
            var stockKey = ReturnPendingStock.StockKey("sm-pending:approve", seed.ReturnLineId);
            var row = Assert.Single(await MovementsAsync(verify, seed));
            Assert.Equal(("RETURN_PASS", 1m, "RETURN_CLAIM", "SM-CLAIM", stockKey, stockKey, (Guid?)Actor, "corr-pending"),
                (row.MovementType, row.QuantityDelta, row.SourceType, row.SourceId, row.SourceEventId, row.IdempotencyKey, row.ActorUserId, row.CorrelationId));
            Assert.Equal(ReturnPendingStockPolicy.Applied, (await verify.ReturnDecisions.AsNoTracking().SingleAsync(x => x.TenantId == seed.TenantId)).StockStatus);
            Assert.Equal(IssueStatus.Open, (await ShortfallIssueAsync(verify, seed)).Status);
        }
        finally
        {
            await CleanupAsync(seed.TenantId);
        }
    }

    // The order projection, read by read: a reservation an earlier projection left is released into the shortfall, a
    // new line takes its baseline, units leave on-hand (rounded to 4 places), what stock could not give is the
    // shortfall, a cancellation gives back, shipment progress is recorded, and a read again writes nothing new.
    [PostgreSqlFact]
    public async Task OrderProjection_WritesTheSameRowsAndStockReadByRead()
    {
        var seed = await SeedAsync("sm-order", onHand: 2m, reserved: 1m);
        var line = new OrderLine { Id = seed.OrderLineId, TenantId = seed.TenantId, OrderId = seed.OrderId, VariantId = seed.VariantId, ExternalLineId = "OL-1", Sku = "SM-ORDER", TitleSnapshot = "Ürün", OrderedQuantity = 3m, RawStatus = "Created" };
        var at = new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero);
        var lineKey = seed.OrderLineId.ToString("D");
        try
        {
            await using (var db = fixture.CreateContext())
            {
                db.StockReservations.Add(new StockReservation { Id = seed.ReservationId, TenantId = seed.TenantId, InventoryItemId = seed.ItemId, SourceType = "ORDER_LINE", SourceId = lineKey, Quantity = 1m, Status = ReservationStatus.Active, Version = 1 });
                await db.SaveChangesAsync();
            }
            // Read 1: three ordered, two on hand, one held by an old reservation.
            await ProjectAsync(seed, line, at);
            // Read 2: the same read again.
            await ProjectAsync(seed, line, at);
            // Read 3: one unit cancelled.
            line.CancelledQuantity = 1m; line.RawStatus = "Cancelled";
            await ProjectAsync(seed, line, at.AddMinutes(1));
            // Read 4: the two left are shipped.
            line.ShippedQuantity = 2m; line.RawStatus = "Shipped";
            await ProjectAsync(seed, line, at.AddMinutes(2));

            await using var verify = fixture.CreateContext();
            var item = await verify.InventoryItems.AsNoTracking().SingleAsync(x => x.Id == seed.ItemId);
            Assert.Equal((0m, 0m, 0m), (item.OnHand, item.Reserved, item.Available));
            var rows = (await MovementsAsync(verify, seed)).Select(Describe).ToList();
            var n = seed.OrderLineId.ToString("N");
            var i = seed.ItemId.ToString("N");
            var r = seed.ReservationId.ToString("N");
            var t0 = at.ToUnixTimeMilliseconds();
            var t1 = at.AddMinutes(1).ToUnixTimeMilliseconds();
            var t2 = at.AddMinutes(2).ToUnixTimeMilliseconds();
            Assert.Equal(new[]
            {
                $"ORDER_RESERVATION_RELEASED|1|ORDER_LINE|{lineKey}|{n}:{t0}:reservation-to-shortfall:{r}|order-reservation:{n}:{t0}:reservation-to-shortfall:{r}|{at:O}",
                $"ORDER_STOCK_SHORTFALL|1|ORDER_LINE|{lineKey}|{n}:{t0}:reservation-to-shortfall:{r}|order-stock-shortfall:{i}:{n}:{t0}:reservation-to-shortfall:{r}|{at:O}",
                $"ORDER_STOCK_BASELINE|0|ORDER_LINE|{lineKey}|baseline:{lineKey}|order-stock-baseline:{i}:{lineKey}|{at:O}",
                $"ORDER_RECEIVED|-2|ORDER_LINE|{lineKey}|{n}:{t0}:committed:1:3|order-stock-received:{i}:{n}:{t0}:committed:1:3|{at:O}",
                $"ORDER_STOCK_SHORTFALL|-1|ORDER_LINE|{lineKey}|{n}:{t1}:shortfall:1:0|order-stock-shortfall:{i}:{n}:{t1}:shortfall:1:0|{at.AddMinutes(1):O}",
                $"ORDER_SHIPMENT_PROGRESS|-2|ORDER_LINE|{lineKey}|{n}:shipment-progress:2|order-shipment-progress:{i}:{n}:shipment-progress:2|{at.AddMinutes(2):O}",
            }.Order(StringComparer.Ordinal), rows.Order(StringComparer.Ordinal));
            Assert.Equal(ReservationStatus.Released, (await verify.StockReservations.AsNoTracking().SingleAsync(x => x.Id == seed.ReservationId)).Status);
        }
        finally
        {
            await CleanupAsync(seed.TenantId);
        }
    }

    // A catalog read that sets the panel's stock: the ledger row takes the difference under the item's next projection
    // version, the version moves once; a read of the same number only marks the item reconciled; a new variant gets its
    // opening row.
    [PostgreSqlFact]
    public async Task CatalogStock_WritesTheDifferenceUnderTheNextProjectionVersion_AndAnOpeningRowForANewItem()
    {
        var seed = await SeedAsync("sm-catalog", onHand: 4m);
        try
        {
            RemoteCatalogVariant Remote(decimal stock) => new("sm-ext", "SM-CATALOG", null, null, new Dictionary<string, string>(), false, null, null, null, stock, "TRY", "{}");
            await UpsertCatalogAsync(seed, seed.VariantId, Remote(9m));
            await UpsertCatalogAsync(seed, seed.VariantId, Remote(9m));
            Guid newVariantId;
            await using (var db = fixture.CreateContext())
            {
                var variant = new ProductVariant { Id = Guid.CreateVersion7(), TenantId = seed.TenantId, ProductId = seed.ProductId, Sku = "SM-CATALOG-2", SkuNormalized = "SM-CATALOG-2", OptionSignature = "Beden=2", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, Version = 1 };
                db.ProductVariants.Add(variant);
                await db.SaveChangesAsync();
                newVariantId = variant.Id;
            }
            await UpsertCatalogAsync(seed, newVariantId, Remote(3m));

            await using var verify = fixture.CreateContext();
            var item = await verify.InventoryItems.AsNoTracking().SingleAsync(x => x.Id == seed.ItemId);
            Assert.Equal((9m, 0m, 9m, 2L, 2L), (item.OnHand, item.Reserved, item.Available, item.ProjectionVersion, item.Version));
            var opening = await verify.InventoryItems.AsNoTracking().SingleAsync(x => x.VariantId == newVariantId);
            Assert.Equal((3m, 3m, 1L, 1L), (opening.OnHand, opening.Available, opening.ProjectionVersion, opening.Version));
            var c = seed.ConnectionId.ToString("N");
            var rows = (await verify.StockLedgerEntries.AsNoTracking().Where(x => x.TenantId == seed.TenantId && x.MovementType != OrderInventoryStockPolicy.ShortfallMovementType).ToListAsync())
                .Select(row => $"{row.MovementType}|{row.QuantityDelta.ToString("0.####", CultureInfo.InvariantCulture)}|{row.SourceType}|{row.SourceId}|{row.SourceEventId}|{row.IdempotencyKey}|{row.CorrelationId}")
                .Order(StringComparer.Ordinal).ToList();
            Assert.Equal(new[]
            {
                $"{MarketplaceStockImportPolicy.MovementType}|5|MARKETPLACE_CATALOG|{seed.ConnectionId:D}|catalog-stock:{c}:{seed.ItemId:N}:v2|catalog-stock:{c}:{seed.ItemId:N}:v2|catalog-import:{c}",
                $"{ProductActivityTypes.ImportOpening}|3|MARKETPLACE_CATALOG|{seed.ConnectionId:D}|catalog-opening:{c}:{opening.Id:N}|catalog-opening:{c}:{opening.Id:N}|catalog-import:{c}",
            }.Order(StringComparer.Ordinal), rows);
        }
        finally
        {
            await CleanupAsync(seed.TenantId);
        }
    }

    // SIPARIS-STOK-8 (bug): the manual stock writes took any ledger row with the request's key as "already done", so a
    // key a return restock had used made the stock count answer 200 without changing the stock. Only an earlier manual
    // adjustment under the key is a replay now; a key of another movement is refused and the stock stays as it was.
    [PostgreSqlFact]
    public async Task ManualStockWrite_WithTheKeyOfAReturnRestock_IsRefusedInsteadOfReportedDone()
    {
        var seed = await SeedAsync("sm-key-reuse");
        try
        {
            var key = $"sm-key-reuse-{Guid.NewGuid():N}";
            await using (var db = fixture.CreateContext())
                Assert.Null((await Sales(db).ApplyDispositionAsync(seed.TenantId, Actor, seed.ClaimId, new(seed.ReturnLineId, "PASS", 1m, "Kontrol edildi"), key, "corr", CancellationToken.None)).Error);
            await using (var db = fixture.CreateContext())
            {
                var set = await Inventory(db).SetOnHandAsync(seed.TenantId, Actor, seed.VariantId, new StockSetCommand(10m, 6m, "Sayım", "ev-reuse"), key, "corr", CancellationToken.None);
                Assert.Equal("IDEMPOTENCY_KEY_REUSED", set.Error?.Code);
                Assert.Equal(409, set.Error!.Status);
                var adjust = await Inventory(db).AdjustAsync(seed.TenantId, Actor, seed.VariantId, new StockAdjustmentCommand(4m, "Sayım", "ev-reuse"), key, "corr", CancellationToken.None);
                Assert.Equal("IDEMPOTENCY_KEY_REUSED", adjust.Error?.Code);
            }
            await using var verify = fixture.CreateContext();
            Assert.Equal(6m, (await verify.InventoryItems.AsNoTracking().SingleAsync(x => x.Id == seed.ItemId)).OnHand);
            Assert.Equal(["RETURN_PASS"], (await MovementsAsync(verify, seed)).Select(x => x.MovementType));
        }
        finally
        {
            await CleanupAsync(seed.TenantId);
        }
    }

    // SIPARIS-STOK-6: one order line of two units in two claims. What the claims put back in stock together never
    // exceeds the units sold; a restock by hand that would is refused with its reason, not cut down.
    [PostgreSqlFact]
    public async Task ManualRestock_AcrossTwoClaimsOfOneOrderLine_NeverPutsBackMoreThanWasSold()
    {
        var seed = await SeedAsync("sm-cap-manual");
        try
        {
            var second = await AddClaimAsync(seed, "SM-CLAIM-2");
            await using (var db = fixture.CreateContext())
                Assert.Null((await Sales(db).ApplyDispositionAsync(seed.TenantId, Actor, seed.ClaimId, new(seed.ReturnLineId, "PASS", 1m, "Kontrol edildi"), $"cap-a-{Guid.NewGuid():N}", "corr", CancellationToken.None)).Error);
            await using (var db = fixture.CreateContext())
            {
                var refused = await Sales(db).ApplyDispositionAsync(seed.TenantId, Actor, second.ClaimId, new(second.ReturnLineId, "PASS", 2m, "Kontrol edildi"), $"cap-b-{Guid.NewGuid():N}", "corr", CancellationToken.None);
                Assert.Equal("RETURN_ORDER_LINE_QUANTITY_EXCEEDED", refused.Error?.Code);
                Assert.Equal(409, refused.Error!.Status);
                Assert.Contains("stoğa", refused.Error.Message, StringComparison.Ordinal);
            }
            await using (var db = fixture.CreateContext())
            {
                // The units not restocked can still go elsewhere (quarantine), and the one unit left can go back.
                Assert.Null((await Sales(db).ApplyDispositionAsync(seed.TenantId, Actor, second.ClaimId, new(second.ReturnLineId, "QUARANTINE", 1m, "Kontrol"), $"cap-q-{Guid.NewGuid():N}", "corr", CancellationToken.None)).Error);
                Assert.Null((await Sales(db).ApplyDispositionAsync(seed.TenantId, Actor, second.ClaimId, new(second.ReturnLineId, "PASS", 1m, "Kontrol edildi"), $"cap-c-{Guid.NewGuid():N}", "corr", CancellationToken.None)).Error);
            }
            await using var verify = fixture.CreateContext();
            Assert.Equal(7m, (await verify.InventoryItems.AsNoTracking().SingleAsync(x => x.Id == seed.ItemId)).OnHand);
            Assert.Equal(2m, (await MovementsAsync(verify, seed)).Sum(x => x.QuantityDelta));
        }
        finally
        {
            await CleanupAsync(seed.TenantId);
        }
    }

    // SIPARIS-STOK-6: a "Stoğa Ekle" kept with the second claim's approval that would put back more than was sold is not
    // applied; the owner sees an open issue naming the claim instead of a silent cut.
    [PostgreSqlFact]
    public async Task SettledRestock_BeyondTheUnitsSold_IsRefusedWithAnOpenIssue()
    {
        var seed = await SeedAsync("sm-cap-pending");
        try
        {
            var second = await AddClaimAsync(seed, "SM-CLAIM-2");
            await using (var db = fixture.CreateContext())
                Assert.Null((await Sales(db).ApplyDispositionAsync(seed.TenantId, Actor, seed.ClaimId, new(seed.ReturnLineId, "PASS", 2m, "Kontrol edildi"), $"cap-a-{Guid.NewGuid():N}", "corr", CancellationToken.None)).Error);
            await using (var db = fixture.CreateContext())
            {
                var decision = new ReturnDecision { Id = Guid.CreateVersion7(), TenantId = seed.TenantId, ClaimId = second.ClaimId, Action = "APPROVE", IdempotencyKey = "sm-cap:approve", Status = "SUCCEEDED", ActorUserId = Actor, CreatedAt = DateTimeOffset.UtcNow, ReturnLineIds = [second.ReturnLineId], StockLinesJson = ReturnPendingStock.Serialize([new(second.ReturnLineId, null)]), StockStatus = ReturnPendingStockPolicy.Pending };
                db.ReturnDecisions.Add(decision);
                await db.SaveChangesAsync();
                await ReturnPendingStock.SettleAndSaveAsync(db, () => ReturnPendingStock.SettleAsync(db, decision, force: true, "corr-cap", DateTimeOffset.UtcNow, CancellationToken.None), CancellationToken.None);
            }
            await using var verify = fixture.CreateContext();
            Assert.Equal(7m, (await verify.InventoryItems.AsNoTracking().SingleAsync(x => x.Id == seed.ItemId)).OnHand);
            Assert.False(await verify.ReturnStockDispositions.AnyAsync(x => x.TenantId == seed.TenantId && x.ReturnLineId == second.ReturnLineId));
            var issue = await verify.OperationalIssues.AsNoTracking().SingleAsync(x => x.TenantId == seed.TenantId && x.Code == "RETURN_RESTOCK_EXCEEDS_SOLD");
            Assert.Equal(IssueStatus.Open, issue.Status);
            Assert.Contains("SM-CLAIM-2", issue.Summary, StringComparison.Ordinal);
        }
        finally
        {
            await CleanupAsync(seed.TenantId);
        }
    }

    // A second approved claim with one accepted return line of two units on the seed's order line.
    private async Task<(Guid ClaimId, Guid ReturnLineId)> AddClaimAsync(Seed seed, string externalClaimId)
    {
        var now = DateTimeOffset.UtcNow;
        var claim = new ReturnClaim { Id = Guid.CreateVersion7(), TenantId = seed.TenantId, ConnectionId = seed.ConnectionId, OrderId = seed.OrderId, ExternalClaimId = externalClaimId, Status = ReturnClaimStatus.Approved, RawStatus = "Accepted", LastRemoteModifiedAt = now, CreatedAt = now, UpdatedAt = now, Version = 1 };
        var line = new ReturnLine { Id = Guid.CreateVersion7(), TenantId = seed.TenantId, ClaimId = claim.Id, OrderLineId = seed.OrderLineId, ExternalLineId = "CI-2", Quantity = 2m, RawStatus = "Accepted" };
        await using var db = fixture.CreateContext();
        db.ReturnClaims.Add(claim);
        db.ReturnLines.Add(line);
        await db.SaveChangesAsync();
        return (claim.Id, line.Id);
    }

    private sealed record Seed(Guid TenantId, Guid ConnectionId, Guid ProductId, Guid VariantId, Guid ItemId, Guid OrderId, Guid OrderLineId, Guid ClaimId, Guid ReturnLineId, Guid ShortfallLineId, Guid ReservationId);

    private static string Describe(StockLedgerEntry row) =>
        $"{row.MovementType}|{row.QuantityDelta.ToString("0.####", CultureInfo.InvariantCulture)}|{row.SourceType}|{row.SourceId}|{row.SourceEventId}|{row.IdempotencyKey}|{row.OccurredAt.ToUniversalTime():O}";

    private InventoryService Inventory(AppDbContext db) =>
        new(db, new CursorCodec(new EphemeralDataProtectionProvider(), TimeProvider.System), fixture.TimeProvider, new ConfigurationBuilder().Build());

    private MarketplaceSalesService Sales(AppDbContext db) =>
        new(db, new CursorCodec(new EphemeralDataProtectionProvider(), fixture.TimeProvider), new ConfigurationBuilder().Build(), null!, null!, null!, null!, TimeProvider.System);

    private async Task ProjectAsync(Seed seed, OrderLine line, DateTimeOffset modifiedAt)
    {
        await using var db = fixture.CreateContext();
        var processor = new MarketplaceJobProcessor(db, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, new ConfigurationBuilder().Build(), fixture.TimeProvider);
        var project = typeof(MarketplaceJobProcessor).GetMethod("ProjectOrderReservations", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        await (Task)project.Invoke(processor, [seed.TenantId, seed.ConnectionId, new List<(OrderLine, DateTimeOffset)> { (line, modifiedAt) }, CancellationToken.None])!;
        await db.SaveChangesAsync();
    }

    private async Task UpsertCatalogAsync(Seed seed, Guid variantId, RemoteCatalogVariant remote)
    {
        await using var db = fixture.CreateContext();
        var variant = await db.ProductVariants.SingleAsync(x => x.Id == variantId);
        var processor = new MarketplaceJobProcessor(db, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, new ConfigurationBuilder().Build(), fixture.TimeProvider);
        var upsert = typeof(MarketplaceJobProcessor).GetMethod("UpsertCatalogOfferAndInventory", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        await (Task)upsert.Invoke(processor, [seed.TenantId, seed.ConnectionId, variant, remote, null, DateTimeOffset.UtcNow, CancellationToken.None, false, false, true, true])!;
        await db.SaveChangesAsync();
    }

    // The stock movements of the seed item, without the shortfall marker the seed wrote.
    private static async Task<List<StockLedgerEntry>> MovementsAsync(AppDbContext db, Seed seed) =>
        await db.StockLedgerEntries.AsNoTracking().Where(x => x.TenantId == seed.TenantId && x.InventoryItemId == seed.ItemId && x.SourceId != seed.ShortfallLineId.ToString("D")).OrderBy(x => x.Id).ToListAsync();

    private static Task<OperationalIssue> ShortfallIssueAsync(AppDbContext db, Seed seed) =>
        db.OperationalIssues.AsNoTracking().SingleAsync(x => x.TenantId == seed.TenantId && x.DedupeKey == StockShortfallNotices.DedupeKey(seed.ShortfallLineId));

    // A stock item (5 on hand by default) with an open shortfall notice of another order line, an approved claim with one
    // accepted return line of two units on a delivered order line of two.
    private async Task<Seed> SeedAsync(string code, decimal onHand = 5m, decimal reserved = 0m)
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"{code}-{Guid.NewGuid():N}"[..20], DisplayName = code, Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var connection = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = "TRENDYOL", Environment = "STAGE", DisplayName = code, ExternalStoreId = $"s-{Guid.NewGuid():N}"[..12], Status = "ACTIVE", ApiVersion = "V1" };
        var product = new Product { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Title = "Stok ürünü", Description = "", Status = ProductStatus.Active, CreatedAt = now, UpdatedAt = now };
        var sku = code.ToUpperInvariant();
        var variant = new ProductVariant { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProductId = product.Id, Sku = sku, SkuNormalized = sku, OptionSignature = "Beden=1", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var item = new InventoryItem { Id = Guid.CreateVersion7(), TenantId = tenant.Id, VariantId = variant.Id, LocationCode = "MAIN", OnHand = onHand, Reserved = reserved, Available = InventoryProjection.Available(onHand, reserved), ReconciledAt = now, ProjectionVersion = 1, Version = 1 };
        var order = new Order { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, ExternalOrderId = $"{code}-order", OrderNumber = $"{code}-order", Currency = "TRY", GrossAmount = 100m, NetAmount = 100m, OrderedAt = now.AddDays(-5), LastRemoteModifiedAt = now.AddDays(-5), CustomerSnapshotJson = "{}", ShipmentAddressSnapshotJson = "{}", InvoiceAddressSnapshotJson = "{}", DerivedStatus = "DELIVERED", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var orderLine = new OrderLine { Id = Guid.CreateVersion7(), TenantId = tenant.Id, OrderId = order.Id, VariantId = variant.Id, ExternalLineId = "OL-1", Sku = sku, TitleSnapshot = "Ürün", OrderedQuantity = 2m, ShippedQuantity = 2m, DeliveredQuantity = 2m, UnitPrice = 50m, VatRate = 20m, RawStatus = "Delivered" };
        var claim = new ReturnClaim { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, OrderId = order.Id, ExternalClaimId = "SM-CLAIM", Status = ReturnClaimStatus.Approved, RawStatus = "Accepted", LastRemoteModifiedAt = now.AddDays(-1), CreatedAt = now, UpdatedAt = now, Version = 1 };
        var returnLine = new ReturnLine { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ClaimId = claim.Id, OrderLineId = orderLine.Id, ExternalLineId = "CI-1", Quantity = 2m, RawStatus = "Accepted" };
        var shortfallLineId = Guid.CreateVersion7();
        await using var db = fixture.CreateContext();
        db.Tenants.Add(tenant);
        db.PlatformConnections.Add(connection);
        db.Products.Add(product);
        db.ProductVariants.Add(variant);
        db.InventoryItems.Add(item);
        db.Orders.Add(order);
        db.OrderLines.Add(orderLine);
        db.ReturnClaims.Add(claim);
        db.ReturnLines.Add(returnLine);
        db.StockLedgerEntries.Add(new StockLedgerEntry { Id = Guid.CreateVersion7(), TenantId = tenant.Id, InventoryItemId = item.Id, MovementType = OrderInventoryStockPolicy.ShortfallMovementType, QuantityDelta = 2m, SourceType = "ORDER_LINE", SourceId = shortfallLineId.ToString("D"), SourceEventId = $"seed:{shortfallLineId:N}", IdempotencyKey = $"order-stock-shortfall:{item.Id:N}:seed:{shortfallLineId:N}", OccurredAt = now, RecordedAt = now, CorrelationId = "seed" });
        db.OperationalIssues.Add(new OperationalIssue { Id = Guid.CreateVersion7(), TenantId = tenant.Id, DedupeKey = StockShortfallNotices.DedupeKey(shortfallLineId), Code = StockShortfallNotices.IssueCode, Summary = "2 adet stoktan düşülemedi.", Status = IssueStatus.Open, FirstSeenAt = now, LastSeenAt = now, OccurrenceCount = 1 });
        await db.SaveChangesAsync();
        return new(tenant.Id, connection.Id, product.Id, variant.Id, item.Id, order.Id, orderLine.Id, claim.Id, returnLine.Id, shortfallLineId, Guid.CreateVersion7());
    }

    private async Task CleanupAsync(Guid tenantId)
    {
        await using var db = fixture.CreateContext();
        await db.JobAttempts.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.IntegrationJobs.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.IntegrationOutboxEvents.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.AuditLogs.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.StockLedgerEntries.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.StockReservations.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.ReturnStockDispositions.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.ReturnDecisions.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.OperationalIssues.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.ReturnLines.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.ReturnClaims.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.InventoryItems.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.OrderLines.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.ChannelOffers.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.ProductVariants.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.Products.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.Orders.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.PlatformConnections.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.Tenants.Where(row => row.Id == tenantId).ExecuteDeleteAsync();
    }
}
