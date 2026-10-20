using System.Text.Json;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Deep audit 2026-10-20 S5, S6, S16: package line allocations are one row per sync event (only the
// latest counts), a settled Hepsiburada package reservation must not block the next package, and a
// resent successful picking answers with the package instead of an error.
[Collection("postgres-integration")]
public sealed class OrderPackageDeepAuditPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    [PostgreSqlFact]
    public async Task HepsiburadaPackage_CountsOnlyTheLatestAllocationOfLivePackages()
    {
        var seed = await SeedAsync("oa-hb", "HEPSIBURADA", lineQuantity: 3);
        try
        {
            await using (var db = fixture.CreateContext())
            {
                // Two reads of the same package: 2 units, then 1 unit; and a cancelled package that held 2.
                db.PackageLineAllocations.Add(new PackageLineAllocation { Id = Guid.CreateVersion7(), TenantId = seed.TenantId, PackageId = seed.PackageId, OrderLineId = seed.LineId, AllocatedQuantity = 2, SourceEventId = "HB1:1760000000000" });
                db.PackageLineAllocations.Add(new PackageLineAllocation { Id = Guid.CreateVersion7(), TenantId = seed.TenantId, PackageId = seed.PackageId, OrderLineId = seed.LineId, AllocatedQuantity = 1, SourceEventId = "HB1:1760000600000" });
                var cancelled = Package(seed, "HB-CANCELLED", ShipmentPackageStatus.Cancelled, "Cancelled");
                db.ShipmentPackages.Add(cancelled);
                db.PackageLineAllocations.Add(new PackageLineAllocation { Id = Guid.CreateVersion7(), TenantId = seed.TenantId, PackageId = cancelled.Id, OrderLineId = seed.LineId, AllocatedQuantity = 2, SourceEventId = "HB-CANCELLED:1760000000000" });
                await db.SaveChangesAsync();
            }
            var port = new RecordingOrderPort();
            await using var service = fixture.CreateContext();

            var result = await NewService(service, port).CreateOrderPackageInstantAsync(seed.TenantId, seed.OrderId, 1, Request(seed.LineId, 2), "oa-hb:create", "oa-hb", CancellationToken.None);

            Assert.Null(result.Error);
            Assert.Equal(2, Assert.Single(port.CreatedPackages).LineItems.Single().Quantity);
            await using var verify = fixture.CreateContext();
            var detail = await NewService(verify, port).OrderAsync(seed.TenantId, seed.OrderId, CancellationToken.None);
            Assert.Equal(1m, Assert.Single(detail.Value!.Lines).AllocatedQuantity);
        }
        finally
        {
            await CleanupAsync(seed.TenantId);
        }
    }

    [PostgreSqlFact]
    public async Task HepsiburadaPackage_ASettledReservationNoLongerBlocksTheNextPackage()
    {
        var seed = await SeedAsync("oa-res", "HEPSIBURADA", lineQuantity: 2);
        var prefix = $"HEPSIBURADA_PACKAGE_CREATE:{seed.ConnectionId:N}:{seed.OrderId:N}";
        Guid reservationId;
        try
        {
            await using (var db = fixture.CreateContext())
            {
                var reservation = new ExternalEffectRecord { Id = Guid.CreateVersion7(), TenantId = seed.TenantId, EffectType = prefix, IdempotencyKey = prefix, CreatedAt = DateTimeOffset.UtcNow.AddHours(-1), CompletedAt = DateTimeOffset.UtcNow.AddMinutes(-20), Outcome = "UNRESOLVED", ConnectionId = seed.ConnectionId, TargetId = seed.OrderId, Action = "CREATE_PACKAGE", ExpectedValue = "1" };
                reservationId = reservation.Id;
                db.ExternalEffectRecords.Add(reservation);
                await db.SaveChangesAsync();
            }
            var port = new RecordingOrderPort();
            await using var service = fixture.CreateContext();

            var result = await NewService(service, port).CreateOrderPackageInstantAsync(seed.TenantId, seed.OrderId, 1, Request(seed.LineId, 1), "oa-res:create", "oa-res", CancellationToken.None);

            Assert.Null(result.Error);
            await using var verify = fixture.CreateContext();
            var settled = await verify.ExternalEffectRecords.AsNoTracking().SingleAsync(x => x.Id == reservationId);
            Assert.Equal(MarketplaceSalesService.HepsiburadaPackageReservationSettledKey(prefix, reservationId), settled.IdempotencyKey);
            Assert.True(await verify.ExternalEffectRecords.AnyAsync(x => x.TenantId == seed.TenantId && x.EffectType == $"{prefix}:PKG-NEW" && x.CompletedAt != null));
        }
        finally
        {
            await CleanupAsync(seed.TenantId);
        }
    }

    [PostgreSqlFact]
    public async Task InstantPicking_SendsTheLatestAllocationAndAnswersAResendWithThePackage()
    {
        var seed = await SeedAsync("oa-pick", "TRENDYOL", lineQuantity: 2);
        try
        {
            await using (var db = fixture.CreateContext())
            {
                db.PackageLineAllocations.Add(new PackageLineAllocation { Id = Guid.CreateVersion7(), TenantId = seed.TenantId, PackageId = seed.PackageId, OrderLineId = seed.LineId, AllocatedQuantity = 2, SourceEventId = "TY1:1760000000000" });
                db.PackageLineAllocations.Add(new PackageLineAllocation { Id = Guid.CreateVersion7(), TenantId = seed.TenantId, PackageId = seed.PackageId, OrderLineId = seed.LineId, AllocatedQuantity = 1, SourceEventId = "TY1:1760000600000" });
                await db.SaveChangesAsync();
            }
            var port = new RecordingOrderPort();
            await using (var first = fixture.CreateContext())
            {
                var result = await NewService(first, port).ProcessShipmentInstantAsync(seed.TenantId, seed.PackageId, 1, "oa-pick:key", "oa-pick", CancellationToken.None);
                Assert.Null(result.Error);
            }
            using (var payload = JsonDocument.Parse(Assert.Single(port.Actions).PayloadJson))
            {
                var line = Assert.Single(payload.RootElement.GetProperty("lines").EnumerateArray());
                Assert.Equal(1, line.GetProperty("quantity").GetInt32());
            }

            // The same request again, with the version the first one answered before it changed the package.
            await using var resend = fixture.CreateContext();
            var replay = await NewService(resend, port).ProcessShipmentInstantAsync(seed.TenantId, seed.PackageId, 1, "oa-pick:key", "oa-pick", CancellationToken.None);

            Assert.Null(replay.Error);
            Assert.Equal("PROCESSING", replay.Value!.Status);
            Assert.Single(port.Actions);
        }
        finally
        {
            await CleanupAsync(seed.TenantId);
        }
    }

    private static OrderPackageCreateRequest Request(Guid lineId, int quantity) =>
        new("PKG-BARCODE", "HX", "HepsiJet", "Panel", 1, 1, "Depo", "Standart", [new(lineId, quantity)]);

    private MarketplaceSalesService NewService(AppDbContext db, IOrderPort port) =>
        new(db, null!, new ConfigurationBuilder().Build(), null!, port, null!, null!, TimeProvider.System);

    private sealed record Seed(Guid TenantId, Guid ConnectionId, Guid OrderId, Guid PackageId, Guid LineId);

    private static ShipmentPackage Package(Seed seed, string externalId, ShipmentPackageStatus status, string rawStatus) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = seed.TenantId,
        ConnectionId = seed.ConnectionId,
        OrderId = seed.OrderId,
        ExternalPackageId = externalId,
        Status = status,
        RawStatus = rawStatus,
        StatusOccurredAt = DateTimeOffset.UtcNow.AddHours(-2),
        MarketplaceInvoiceStatus = MarketplaceInvoiceStatus.Unknown,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        Version = 1
    };

    private async Task<Seed> SeedAsync(string code, string platform, decimal lineQuantity)
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"{code}-{Guid.NewGuid():N}"[..20], DisplayName = code, Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var connection = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = platform, Environment = "STAGE", DisplayName = code, ExternalStoreId = $"s-{Guid.NewGuid():N}"[..12], Status = "ACTIVE", ApiVersion = "V1" };
        var order = new Order
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            ConnectionId = connection.Id,
            ExternalOrderId = $"{code}-order",
            OrderNumber = $"{code}-order",
            Currency = "TRY",
            GrossAmount = 100m,
            NetAmount = 100m,
            OrderedAt = now.AddHours(-3),
            LastRemoteModifiedAt = now.AddHours(-3),
            CustomerSnapshotJson = "{}",
            ShipmentAddressSnapshotJson = "{}",
            InvoiceAddressSnapshotJson = "{}",
            DerivedStatus = "NEW",
            CreatedAt = now,
            UpdatedAt = now,
            Version = 1
        };
        var line = new OrderLine { Id = Guid.CreateVersion7(), TenantId = tenant.Id, OrderId = order.Id, ExternalLineId = "4242", Sku = "OA-1", TitleSnapshot = "Derin inceleme", OrderedQuantity = lineQuantity, UnitPrice = 50m, VatRate = 20m, RawStatus = "Open" };
        var seed = new Seed(tenant.Id, connection.Id, order.Id, Guid.Empty, line.Id);
        var package = Package(seed, $"{code}-pkg", ShipmentPackageStatus.New, "Created");
        await using var db = fixture.CreateContext();
        db.Tenants.Add(tenant);
        db.PlatformConnections.Add(connection);
        db.ConnectionSyncPolicies.Add(new ConnectionSyncPolicy { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, ResourceType = MarketplaceExternalWritePolicies.Shipment, IntervalSeconds = 0, Enabled = true, Version = 1 });
        db.Orders.Add(order);
        db.OrderLines.Add(line);
        db.ShipmentPackages.Add(package);
        await db.SaveChangesAsync();
        return seed with { PackageId = package.Id };
    }

    // Jobs, attempts and outbox rows go before the tenant: the lease tests in the same database take any pending job.
    private async Task CleanupAsync(Guid tenantId)
    {
        await using var db = fixture.CreateContext();
        await db.JobAttempts.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.IntegrationJobs.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.IntegrationOutboxEvents.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.ExternalEffectRecords.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.ConnectionSyncPolicies.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.OperationalIssues.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.OrderStatusHistory.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.PackageLineAllocations.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.OrderLines.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.ShipmentPackages.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.Orders.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.PlatformConnections.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.Tenants.Where(row => row.Id == tenantId).ExecuteDeleteAsync();
    }

    private sealed class RecordingOrderPort : IOrderPort
    {
        public List<PackageActionCommand> Actions { get; } = [];
        public List<CreateOrderPackageCommand> CreatedPackages { get; } = [];

        public Task<AdapterResult<PackageActionResult>> ExecutePackageActionAsync(AdapterContext context, PackageActionCommand command, CancellationToken cancellationToken)
        {
            Actions.Add(command);
            return Task.FromResult(AdapterResult<PackageActionResult>.Success(new(command.ExternalPackageId, "Picking", null)));
        }
        public Task<AdapterResult<IReadOnlyList<RemotePackageableLine>>> GetPackageableLineItemsAsync(AdapterContext context, string externalLineItemId, CancellationToken cancellationToken) =>
            Task.FromResult(AdapterResult<IReadOnlyList<RemotePackageableLine>>.Success([new(externalLineItemId, 3)]));
        public Task<AdapterResult<CreateOrderPackageResult>> CreateOrderPackageAsync(AdapterContext context, CreateOrderPackageCommand command, CancellationToken cancellationToken)
        {
            CreatedPackages.Add(command);
            return Task.FromResult(AdapterResult<CreateOrderPackageResult>.Success(new("PKG-NEW")));
        }
        public Task<AdapterResult<AdapterPageResult<RemoteOrder>>> PollAsync(AdapterContext context, OrderPollWindow window, AdapterPageRequest page, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<RemoteOrder>> GetAsync(AdapterContext context, string externalOrderId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<IReadOnlyList<RemoteCargoCompany>>> GetChangeableCargoCompaniesAsync(AdapterContext context, string externalPackageId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<bool>> CreateCommonLabelAsync(AdapterContext context, CommonLabelRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<CommonLabelDocument>> GetCommonLabelAsync(AdapterContext context, string cargoTrackingNumber, CancellationToken cancellationToken, string format = "ZPL") => throw new NotSupportedException();
        public Task<AdapterResult<StageTestOrderResult>> CreateStageTestOrderAsync(AdapterContext context, string barcode, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
