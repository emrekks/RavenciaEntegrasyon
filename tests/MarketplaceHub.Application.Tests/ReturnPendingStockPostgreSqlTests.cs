using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// "Stoğa Ekle" on an approval whose marketplace answer was unclear (timeout, 5xx): the choice is kept with the
// decision and applied once a later read of the claim confirms the approval, exactly once; a rejection drops it.
[Collection("postgres-integration")]
public sealed class ReturnPendingStockPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    [PostgreSqlFact]
    public async Task AnUnclearApproval_KeepsStoğaEkle_AndAppliesItOnceWhenTheApprovalIsConfirmedLater()
    {
        var seed = await SeedAsync("ps-later");
        var port = new UnclearReturnPort("PS-CLAIM", "ps-later-order", seed.ExternalLineIds);
        try
        {
            // The approve call times out: the decision waits for verification, the choice is kept, no stock yet.
            await using (var db = fixture.CreateContext())
            {
                var result = await Service(db, port).ProcessReturnActionInstantAsync(seed.TenantId, seed.UserId, seed.ClaimId, await VersionAsync(seed.ClaimId),
                    new("APPROVE", null, null, [], seed.LineIds, StockLines: [new(seed.LineIds[0], 2m), new(seed.LineIds[1], 1m)]), "ps-later:approve", "ps-later", CancellationToken.None);
                Assert.NotNull(result.Error);
            }
            await using (var verify = fixture.CreateContext())
            {
                var decision = await verify.ReturnDecisions.AsNoTracking().SingleAsync(x => x.TenantId == seed.TenantId && x.IdempotencyKey == "ps-later:approve");
                Assert.Equal("MANUAL_REVIEW", decision.Status);
                Assert.Equal(ReturnPendingStockPolicy.Pending, decision.StockStatus);
                Assert.Equal([0m, 0m], await OnHandAsync(verify, seed));
                var detail = (await Service(verify, port).ReturnAsync(seed.TenantId, seed.ClaimId, CancellationToken.None)).Value!;
                Assert.All(detail.Lines!, line => Assert.True(line.StockPending));
            }

            // The marketplace did take the approval; the effect check reads the claim and the stock goes back.
            port.Accept();
            await RunEffectCheckAsync(seed, port);
            await using (var verify = fixture.CreateContext())
            {
                var decision = await verify.ReturnDecisions.AsNoTracking().SingleAsync(x => x.TenantId == seed.TenantId && x.IdempotencyKey == "ps-later:approve");
                Assert.Equal(ReturnPendingStockPolicy.Applied, decision.StockStatus);
                Assert.NotNull(decision.StockSettledAt);
                Assert.Equal([2m, 1m], await OnHandAsync(verify, seed));
                var ledger = await verify.StockLedgerEntries.AsNoTracking().Where(x => x.TenantId == seed.TenantId).ToListAsync();
                Assert.Equal(2, ledger.Count);
                Assert.All(ledger, row => { Assert.Equal("RETURN_PASS", row.MovementType); Assert.Equal("RETURN_CLAIM", row.SourceType); Assert.Equal("PS-CLAIM", row.SourceId); Assert.Equal(seed.UserId, row.ActorUserId); });
                Assert.Equal(2, await verify.ReturnStockDispositions.CountAsync(x => x.TenantId == seed.TenantId && x.Disposition == ReturnStockDispositionKind.Pass));
            }

            // Exactly once: another read, and even a choice marked pending again, add nothing more.
            await using (var db = fixture.CreateContext())
            {
                await ReturnPendingStock.SettleClaimAsync(db, seed.TenantId, seed.ClaimId, "ps-later:again", DateTimeOffset.UtcNow, CancellationToken.None);
                await db.SaveChangesAsync();
                await db.ReturnDecisions.Where(x => x.TenantId == seed.TenantId).ExecuteUpdateAsync(x => x.SetProperty(d => d.StockStatus, ReturnPendingStockPolicy.Pending));
            }
            await using (var db = fixture.CreateContext())
            {
                await ReturnPendingStock.SettleClaimAsync(db, seed.TenantId, seed.ClaimId, "ps-later:replay", DateTimeOffset.UtcNow, CancellationToken.None);
                await db.SaveChangesAsync();
            }
            await using (var verify = fixture.CreateContext())
            {
                Assert.Equal([2m, 1m], await OnHandAsync(verify, seed));
                Assert.Equal(2, await verify.StockLedgerEntries.CountAsync(x => x.TenantId == seed.TenantId));
                Assert.Equal(ReturnPendingStockPolicy.Applied, (await verify.ReturnDecisions.AsNoTracking().SingleAsync(x => x.TenantId == seed.TenantId)).StockStatus);
            }
        }
        finally
        {
            await CleanupAsync(seed.TenantId);
        }
    }

    [PostgreSqlFact]
    public async Task AnUnclearApproval_ThatEndsRejected_DropsStoğaEkle_WithoutStock()
    {
        var seed = await SeedAsync("ps-reject");
        var port = new UnclearReturnPort("PS-CLAIM", "ps-reject-order", seed.ExternalLineIds);
        try
        {
            await using (var db = fixture.CreateContext())
            {
                var result = await Service(db, port).ProcessReturnActionInstantAsync(seed.TenantId, seed.UserId, seed.ClaimId, await VersionAsync(seed.ClaimId),
                    new("APPROVE", null, null, [], seed.LineIds, AddToStock: true), "ps-reject:approve", "ps-reject", CancellationToken.None);
                Assert.NotNull(result.Error);
            }

            port.Reject();
            await RunEffectCheckAsync(seed, port);
            await using var verify = fixture.CreateContext();
            var decision = await verify.ReturnDecisions.AsNoTracking().SingleAsync(x => x.TenantId == seed.TenantId && x.IdempotencyKey == "ps-reject:approve");
            Assert.Equal(ReturnPendingStockPolicy.Dropped, decision.StockStatus);
            Assert.Equal([0m, 0m], await OnHandAsync(verify, seed));
            Assert.False(await verify.StockLedgerEntries.AnyAsync(x => x.TenantId == seed.TenantId));
            Assert.False(await verify.ReturnStockDispositions.AnyAsync(x => x.TenantId == seed.TenantId));
        }
        finally
        {
            await CleanupAsync(seed.TenantId);
        }
    }

    [PostgreSqlFact]
    public async Task AnApprovalTheMarketplaceAccepts_StocksAtOnce_AsBefore()
    {
        var seed = await SeedAsync("ps-now");
        var port = new UnclearReturnPort("PS-CLAIM", "ps-now-order", seed.ExternalLineIds, answers: true);
        try
        {
            await using (var db = fixture.CreateContext())
            {
                var result = await Service(db, port).ProcessReturnActionInstantAsync(seed.TenantId, seed.UserId, seed.ClaimId, await VersionAsync(seed.ClaimId),
                    new("APPROVE", null, null, [], seed.LineIds, StockLineIds: [seed.LineIds[1]]), "ps-now:approve", "ps-now", CancellationToken.None);
                Assert.Null(result.Error);
                Assert.All(result.Value!.Lines!, line => Assert.False(line.StockPending));
            }
            await using var verify = fixture.CreateContext();
            Assert.Equal(ReturnPendingStockPolicy.Applied, (await verify.ReturnDecisions.AsNoTracking().SingleAsync(x => x.TenantId == seed.TenantId)).StockStatus);
            Assert.Equal([0m, 2m], await OnHandAsync(verify, seed));
        }
        finally
        {
            await CleanupAsync(seed.TenantId);
        }
    }

    private sealed record Seed(Guid TenantId, Guid ConnectionId, Guid ClaimId, Guid UserId, Guid[] LineIds, string[] ExternalLineIds, Guid[] InventoryItemIds);

    private MarketplaceSalesService Service(AppDbContext db, IReturnPort port) =>
        new(db, new CursorCodec(new EphemeralDataProtectionProvider(), fixture.TimeProvider), new ConfigurationBuilder().Build(), null!, null!, port, null!, TimeProvider.System);

    // The verification job the unclear approval queued, run now.
    private async Task RunEffectCheckAsync(Seed seed, IReturnPort port)
    {
        Guid effectId;
        await using (var db = fixture.CreateContext())
            effectId = await db.ExternalEffectRecords.AsNoTracking().Where(x => x.TenantId == seed.TenantId).Select(x => x.Id).SingleAsync();
        await using var processorDb = fixture.CreateContext();
        var processor = new MarketplaceJobProcessor(processorDb, null!, null!, null!, null!, null!, null!, null!, port, null!, null!, new ConfigurationBuilder().Build(), TimeProvider.System);
        var result = await processor.ProcessAsync(seed.TenantId, seed.ConnectionId, MarketplaceJobTypes.ExternalEffectReconcile, $$"""{"effectId":"{{effectId}}"}""", "pending-stock-check", CancellationToken.None);
        Assert.True(result.Succeeded, result.ErrorSummary);
    }


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
        var order = new Order { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, ExternalOrderId = $"{code}-order", OrderNumber = $"{code}-order", Currency = "TRY", GrossAmount = 200m, NetAmount = 200m, OrderedAt = now.AddDays(-5), LastRemoteModifiedAt = now.AddDays(-5), CustomerSnapshotJson = "{}", ShipmentAddressSnapshotJson = "{}", InvoiceAddressSnapshotJson = "{}", DerivedStatus = "DELIVERED", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var claim = new ReturnClaim { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, OrderId = order.Id, ExternalClaimId = "PS-CLAIM", Status = ReturnClaimStatus.ActionRequired, RawStatus = "WaitingInAction", LastRemoteModifiedAt = now.AddDays(-1), CreatedAt = now, UpdatedAt = now, Version = 1 };
        var product = new Product { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Title = "İade ürünü", Description = "", Status = ProductStatus.Active, CreatedAt = now, UpdatedAt = now };
        var variants = new List<ProductVariant>(); var orderLines = new List<OrderLine>(); var returnLines = new List<ReturnLine>(); var items = new List<InventoryItem>();
        for (var index = 1; index <= 2; index++)
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
        return new(tenant.Id, connection.Id, claim.Id, Guid.NewGuid(), returnLines.Select(x => x.Id).ToArray(), returnLines.Select(x => x.ExternalLineId).ToArray(), items.Select(x => x.Id).ToArray());
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

    // Trendyol whose approve call times out. With answers, the call goes through at once. Each claim item has its own
    // status, and the claim's status is its first item's.
    private sealed class UnclearReturnPort(string claimId, string orderId, IReadOnlyList<string> externalLineIds, bool answers = false) : IReturnPort
    {
        private readonly Dictionary<string, string> statuses = externalLineIds.ToDictionary(id => id, _ => "WaitingInAction", StringComparer.Ordinal);

        public void Accept() { foreach (var id in externalLineIds) statuses[id] = "Accepted"; }
        public void Reject() { foreach (var id in externalLineIds) statuses[id] = "Rejected"; }

        public Task<AdapterResult<ReturnActionResult>> ExecuteAsync(AdapterContext context, ReturnActionCommand command, CancellationToken cancellationToken)
        {
            if (!answers)
                return Task.FromResult(AdapterResult<ReturnActionResult>.Failure(new AdapterError(AdapterErrorClass.TransientNetwork, "TRENDYOL_TIMEOUT", "Zaman aşımı", null, null, null)));
            foreach (var lineId in command.ExternalLineItemIds) statuses[lineId] = command.Action == "APPROVE" ? "Accepted" : "Rejected";
            return Task.FromResult(AdapterResult<ReturnActionResult>.Success(new(command.ExternalClaimId, statuses[command.ExternalLineItemIds[0]], "op-1")));
        }

        public Task<AdapterResult<RemoteReturnClaim>> GetAsync(AdapterContext context, string externalReturnId, CancellationToken cancellationToken) =>
            Task.FromResult(AdapterResult<RemoteReturnClaim>.Success(new(claimId, orderId, statuses[externalLineIds[0]], null, null, null, DateTimeOffset.UtcNow,
                externalLineIds.Select(id => new RemoteReturnLine(id, id.Replace("CI-", "OL-", StringComparison.Ordinal), 2m, RawStatus: statuses[id])).ToList(), "{}")));

        public Task<AdapterResult<IReadOnlyList<ReturnIssueReason>>> IssueReasonsAsync(AdapterContext context, CancellationToken cancellationToken) =>
            Task.FromResult(AdapterResult<IReadOnlyList<ReturnIssueReason>>.Success([]));

        public Task<AdapterResult<AdapterPageResult<RemoteReturnClaim>>> PollAsync(AdapterContext context, ReturnPollWindow window, AdapterPageRequest page, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
