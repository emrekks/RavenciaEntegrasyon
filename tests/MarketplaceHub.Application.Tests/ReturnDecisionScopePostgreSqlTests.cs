using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Report item 8: Shopify decides a whole return like Hepsiburada (the line choice is locked and a stuck partial decision
// follows Shopify's status on the next read); n11 denials go through ClaimReturnDeny with the return shipment; an order
// whose one-product claims were decided both ways shows "Kısmen Onaylandı".
[Collection("postgres-integration")]
public sealed class ReturnDecisionScopePostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    [PostgreSqlFact]
    public async Task Shopify_LocksTheLineChoice_AndSendsTheWholeReturn()
    {
        var seed = await SeedAsync("shp-whole", "SHOPIFY", [3]);
        var port = new RecordingReturnPort();
        try
        {
            await using var db = fixture.CreateContext();
            var detail = (await Service(db, port).ReturnAsync(seed.TenantId, seed.ClaimIds[0], CancellationToken.None)).Value!;
            Assert.True(detail.WholeClaimDecision);
            Assert.False(detail.RejectNeedsReturnShipment);

            var partial = await Service(db, port).ProcessReturnActionInstantAsync(seed.TenantId, Guid.NewGuid(), seed.ClaimIds[0], detail.Version,
                new("APPROVE", null, null, [], [seed.LineIds[0][0]]), "shp-whole:partial", "shp-whole", CancellationToken.None);
            Assert.True(partial.Error?.FieldErrors?.ContainsKey("returnLineIds"));
            Assert.Empty(port.Commands);

            var whole = await Service(db, port).ProcessReturnActionInstantAsync(seed.TenantId, Guid.NewGuid(), seed.ClaimIds[0], detail.Version,
                new("APPROVE", null, null, []), "shp-whole:all", "shp-whole", CancellationToken.None);
            Assert.Null(whole.Error);
            Assert.Equal(3, Assert.Single(port.Commands).ExternalLineItemIds.Count);
            Assert.NotEqual("PARTIAL_LINE_DECISION_PENDING", (await db.ReturnClaims.AsNoTracking().SingleAsync(x => x.Id == seed.ClaimIds[0])).RawStatus);
        }
        finally
        {
            await CleanupAsync(seed.TenantId);
        }
    }

    [PostgreSqlFact]
    public async Task Shopify_StuckPartialDecision_FollowsShopifyOnTheNextRead()
    {
        var seed = await SeedAsync("shp-stuck", "SHOPIFY", [2]);
        try
        {
            await using (var db = fixture.CreateContext())
                await db.ReturnClaims.Where(x => x.Id == seed.ClaimIds[0]).ExecuteUpdateAsync(set => set.SetProperty(x => x.RawStatus, "PARTIAL_LINE_DECISION_PENDING"));
            await using (var db = fixture.CreateContext())
            {
                var port = new RecordingReturnPort { Listed = new("shp-stuck-claim-0", "shp-stuck-order", "OPEN", null, null, null, DateTimeOffset.UtcNow, [], "{}") };
                var processor = new MarketplaceJobProcessor(db, null!, null!, null!, null!, null!, null!, null!, port, null!, null!, new ConfigurationBuilder().Build(), fixture.TimeProvider);
                var result = await processor.ProcessAsync(seed.TenantId, seed.ConnectionId, MarketplaceJobTypes.ReturnSync, "{\"forceFull\":true}", "shp-stuck", CancellationToken.None);
                Assert.True(result.Succeeded, result.ErrorSummary);
            }
            await using var verify = fixture.CreateContext();
            var claim = await verify.ReturnClaims.AsNoTracking().SingleAsync(x => x.Id == seed.ClaimIds[0]);
            Assert.Equal(ReturnClaimStatus.Approved, claim.Status);
            Assert.Equal("OPEN", claim.RawStatus);
        }
        finally
        {
            await CleanupAsync(seed.TenantId);
        }
    }

    [PostgreSqlFact]
    public async Task N11_Deny_IsSentWithTheReturnShipment_OnlyWhenComplete()
    {
        var seed = await SeedAsync("n11-deny", "N11", [1]);
        var port = new RecordingReturnPort();
        try
        {
            await using var db = fixture.CreateContext();
            var detail = (await Service(db, port).ReturnAsync(seed.TenantId, seed.ClaimIds[0], CancellationToken.None)).Value!;
            Assert.Contains("REJECT", detail.AllowedActions);
            Assert.True(detail.RejectNeedsReturnShipment);
            Assert.False(detail.WholeClaimDecision);

            var missing = await Service(db, port).ProcessReturnActionInstantAsync(seed.TenantId, Guid.NewGuid(), seed.ClaimIds[0], detail.Version,
                new("REJECT", "21", "Ürün kullanılmış", []), "n11-deny:missing", "n11-deny", CancellationToken.None);
            Assert.True(missing.Error?.FieldErrors?.ContainsKey("returnShipment"));
            Assert.Empty(port.Commands);

            var sent = await Service(db, port).ProcessReturnActionInstantAsync(seed.TenantId, Guid.NewGuid(), seed.ClaimIds[0], detail.Version,
                new("REJECT", "21", "Ürün kullanılmış", [], ReturnShipment: new("7", "TRK-1")), "n11-deny:sent", "n11-deny", CancellationToken.None);
            Assert.Null(sent.Error);
            var command = Assert.Single(port.Commands);
            Assert.Equal(("REJECT", "21", "7", "TRK-1"), (command.Action, command.ReasonCode, command.ReturnShipment?.ShipmentCompanyId, command.ReturnShipment?.TrackingNumber));
            Assert.Equal(ReturnClaimStatus.Rejected, (await db.ReturnClaims.AsNoTracking().SingleAsync(x => x.Id == seed.ClaimIds[0])).Status);
        }
        finally
        {
            await CleanupAsync(seed.TenantId);
        }
    }

    [PostgreSqlTheory]
    [InlineData("N11")]
    [InlineData("PAZARAMA")]
    public async Task OrderWhoseOneProductClaimsWereDecidedBothWays_IsPartiallyApproved(string platformCode)
    {
        var seed = await SeedAsync($"opa-{platformCode.ToLowerInvariant()}", platformCode, [1, 1]);
        try
        {
            await using (var db = fixture.CreateContext())
            {
                await db.ReturnClaims.Where(x => x.Id == seed.ClaimIds[0]).ExecuteUpdateAsync(set => set.SetProperty(x => x.Status, ReturnClaimStatus.Approved).SetProperty(x => x.RawStatus, "Accepted"));
                await db.ReturnClaims.Where(x => x.Id == seed.ClaimIds[1]).ExecuteUpdateAsync(set => set.SetProperty(x => x.Status, ReturnClaimStatus.Rejected).SetProperty(x => x.RawStatus, "Rejected"));
            }
            await using var read = fixture.CreateContext();
            var page = await Service(read, new RecordingReturnPort()).ReturnsAsync(seed.TenantId, 50, null, new ReturnListQuery(), false, CancellationToken.None);
            Assert.Equal(2, page.Items.Count);
            Assert.All(page.Items, item => Assert.Equal(ReturnClaimPartialApprovalPolicy.PartiallyApproved, item.OrderReturnStatus));
        }
        finally
        {
            await CleanupAsync(seed.TenantId);
        }
    }

    private sealed record Seed(Guid TenantId, Guid ConnectionId, Guid[] ClaimIds, Guid[][] LineIds);

    private MarketplaceSalesService Service(AppDbContext db, IReturnPort port) =>
        new(db, new CursorCodec(new EphemeralDataProtectionProvider(), fixture.TimeProvider), new ConfigurationBuilder().Build(), null!, null!, port, null!, TimeProvider.System);

    // One order with a claim per entry of claimLineCounts, each claim with that many waiting lines.
    private async Task<Seed> SeedAsync(string code, string platformCode, int[] claimLineCounts)
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"{code}-{Guid.NewGuid():N}"[..20], DisplayName = code, Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var connection = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = platformCode, Environment = "STAGE", DisplayName = code, ExternalStoreId = $"s-{Guid.NewGuid():N}"[..12], Status = "ACTIVE", ApiVersion = "V1" };
        var order = new Order { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, ExternalOrderId = $"{code}-order", OrderNumber = $"{code}-order", Currency = "TRY", GrossAmount = 300m, NetAmount = 300m, OrderedAt = now.AddDays(-5), LastRemoteModifiedAt = now.AddDays(-5), CustomerSnapshotJson = "{}", ShipmentAddressSnapshotJson = "{}", InvoiceAddressSnapshotJson = "{}", DerivedStatus = "DELIVERED", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var orderLines = new List<OrderLine>();
        var claims = new List<ReturnClaim>();
        var returnLines = new List<List<ReturnLine>>();
        for (var claimIndex = 0; claimIndex < claimLineCounts.Length; claimIndex++)
        {
            var claim = new ReturnClaim { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, OrderId = order.Id, ExternalClaimId = $"{code}-claim-{claimIndex}", Status = ReturnClaimStatus.ActionRequired, RawStatus = "WaitingInAction", LastRemoteModifiedAt = now.AddDays(-1), CreatedAt = now, UpdatedAt = now, Version = 1 };
            var lines = new List<ReturnLine>();
            for (var lineIndex = 0; lineIndex < claimLineCounts[claimIndex]; lineIndex++)
            {
                var orderLine = new OrderLine { Id = Guid.CreateVersion7(), TenantId = tenant.Id, OrderId = order.Id, ExternalLineId = $"OL-{claimIndex}-{lineIndex}", Sku = $"{code}-SKU-{claimIndex}-{lineIndex}".ToUpperInvariant(), TitleSnapshot = "Ürün", OrderedQuantity = 1m, UnitPrice = 50m, VatRate = 20m, RawStatus = "Delivered" };
                orderLines.Add(orderLine);
                lines.Add(new ReturnLine { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ClaimId = claim.Id, OrderLineId = orderLine.Id, ExternalLineId = $"CI-{claimIndex}-{lineIndex}", Quantity = 1m });
            }
            claims.Add(claim);
            returnLines.Add(lines);
        }
        await using var db = fixture.CreateContext();
        db.Tenants.Add(tenant);
        db.PlatformConnections.Add(connection);
        db.ConnectionSyncPolicies.Add(new ConnectionSyncPolicy { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, ResourceType = MarketplaceExternalWritePolicies.Return, IntervalSeconds = 0, Enabled = true, Version = 1 });
        db.Orders.Add(order);
        db.OrderLines.AddRange(orderLines);
        db.ReturnClaims.AddRange(claims);
        db.ReturnLines.AddRange(returnLines.SelectMany(x => x));
        await db.SaveChangesAsync();
        return new(tenant.Id, connection.Id, claims.Select(x => x.Id).ToArray(), returnLines.Select(x => x.Select(line => line.Id).ToArray()).ToArray());
    }

    private async Task CleanupAsync(Guid tenantId)
    {
        await using var db = fixture.CreateContext();
        await db.JobAttempts.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.IntegrationJobs.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.IntegrationOutboxEvents.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.AuditLogs.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.SyncCursors.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.ReturnEvidence.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.ReturnDecisions.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.ExternalEffectRecords.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.OperationalIssues.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.ReturnLines.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.ReturnClaims.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.OrderLines.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.Orders.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.ConnectionSyncPolicies.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.PlatformConnections.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.Tenants.Where(row => row.Id == tenantId).ExecuteDeleteAsync();
    }

    private sealed class RecordingReturnPort : IReturnPort
    {
        public List<ReturnActionCommand> Commands { get; } = [];
        public RemoteReturnClaim? Listed { get; init; }

        public Task<AdapterResult<ReturnActionResult>> ExecuteAsync(AdapterContext context, ReturnActionCommand command, CancellationToken cancellationToken)
        {
            Commands.Add(command);
            return Task.FromResult(AdapterResult<ReturnActionResult>.Success(new(command.ExternalClaimId, command.Action == "APPROVE" ? "Accepted" : "Rejected", null)));
        }

        public Task<AdapterResult<RemoteReturnClaim>> GetAsync(AdapterContext context, string externalReturnId, CancellationToken cancellationToken) =>
            Task.FromResult(AdapterResult<RemoteReturnClaim>.Failure(new(AdapterErrorClass.NotSupported, "NO_READBACK", "no readback", 501, null, null)));

        public Task<AdapterResult<IReadOnlyList<ReturnIssueReason>>> IssueReasonsAsync(AdapterContext context, CancellationToken cancellationToken) =>
            Task.FromResult(AdapterResult<IReadOnlyList<ReturnIssueReason>>.Success([new("21", "Ürün kullanılmış", false), new("41", "İade Ürün Mağazaya Ulaşmadı", false)]));

        public Task<AdapterResult<AdapterPageResult<RemoteReturnClaim>>> PollAsync(AdapterContext context, ReturnPollWindow window, AdapterPageRequest page, CancellationToken cancellationToken) =>
            Task.FromResult(AdapterResult<AdapterPageResult<RemoteReturnClaim>>.Success(new(Listed is null ? [] : [Listed], null, false)));
    }
}
