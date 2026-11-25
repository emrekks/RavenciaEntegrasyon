using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Owner, 25 Nov 2026: a Hepsiburada return on the İadeler page read "İade Tutarı —" although Hepsiburada's claim list
// carries refundAmount; the claim scan read it nowhere. A later read of a claim stored without it fills it, a read without
// it keeps it, and the Returns list shows it as the marketplace's refund amount.
[Collection("postgres-integration")]
public sealed class HepsiburadaRefundAmountPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    [PostgreSqlFact]
    public async Task A_later_read_fills_the_refund_amount_and_the_returns_list_shows_it()
    {
        var (tenant, connection) = await SeedAsync();
        await SeedOrderAsync(tenant.Id, connection.Id, "hb-refund-1");

        // Stored before the fix: no refund amount.
        await using (var db = fixture.CreateContext())
            await Processor(db).UpsertReturn(tenant.Id, connection.Id, Claim(fixture.Now.AddHours(-2), null), CancellationToken.None);
        await using (var db = fixture.CreateContext())
            Assert.Null((await db.ReturnClaims.AsNoTracking().SingleAsync(x => x.TenantId == tenant.Id)).RefundAmount);

        // The next scan of the same claim (unchanged status and change time) brings refundAmount.
        await using (var db = fixture.CreateContext())
            await Processor(db).UpsertReturn(tenant.Id, connection.Id, Claim(fixture.Now.AddHours(-2), 865.72m), CancellationToken.None);
        // A read without it keeps the known amount.
        await using (var db = fixture.CreateContext())
            await Processor(db).UpsertReturn(tenant.Id, connection.Id, Claim(fixture.Now.AddHours(-1), null), CancellationToken.None);

        await using (var db = fixture.CreateContext())
        {
            Assert.Equal(865.72m, (await db.ReturnClaims.AsNoTracking().SingleAsync(x => x.TenantId == tenant.Id)).RefundAmount);
            var page = await Service(db).ReturnsAsync(tenant.Id, 20, null, new ReturnListQuery(), false, CancellationToken.None);
            var row = Assert.Single(page.Items);
            Assert.Equal(new ReturnAmountsView(null, null, null, 865.72m), row.ReturnAmounts);
        }
    }

    private RemoteReturnClaim Claim(DateTimeOffset modified, decimal? refund) =>
        new("hb-claim-refund", "hb-refund-1", "Refunded", "Return", null, null, modified,
            [new RemoteReturnLine("ol-1", "ol-1", 1)], "{}", RequestedAt: fixture.Now.AddDays(-3), RefundAmount: refund);

    private MarketplaceJobProcessor Processor(AppDbContext db) =>
        new(db, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, new ConfigurationBuilder().Build(), fixture.TimeProvider);

    private MarketplaceSalesService Service(AppDbContext db) =>
        new(db, new CursorCodec(new EphemeralDataProtectionProvider(), fixture.TimeProvider), new ConfigurationBuilder().Build(), null!, null!, null!, null!, TimeProvider.System);

    private async Task SeedOrderAsync(Guid tenantId, Guid connectionId, string number)
    {
        var order = new Order { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = connectionId, ExternalOrderId = number, OrderNumber = number, Currency = "TRY", OrderedAt = fixture.Now.AddDays(-5), LastRemoteModifiedAt = fixture.Now.AddDays(-5), CustomerSnapshotJson = "{}", ShipmentAddressSnapshotJson = "{}", InvoiceAddressSnapshotJson = "{}", DerivedStatus = "DELIVERED", CreatedAt = fixture.Now, UpdatedAt = fixture.Now, Version = 1 };
        await using var db = fixture.CreateContext();
        db.Orders.Add(order);
        // An order line as the order read stores it: Hepsiburada's order line, not the claim.
        db.OrderLines.Add(new OrderLine { Id = Guid.CreateVersion7(), TenantId = tenantId, OrderId = order.Id, ExternalLineId = "ol-1", Sku = "SKU", TitleSnapshot = "Ürün", RawStatus = "Delivered", OrderedQuantity = 1, ShippedQuantity = 1, DeliveredQuantity = 1, UnitPrice = 865.72m, VatRate = 10, SourceSnapshotJson = """{"id":"ol-1","unitPrice":{"amount":865.72},"totalPrice":{"amount":865.72}}""", Version = 1 });
        await db.SaveChangesAsync();
    }

    private async Task<(Tenant Tenant, PlatformConnection Connection)> SeedAsync()
    {
        var now = fixture.Now;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"hbrf-{Guid.NewGuid():N}", DisplayName = "hbrf", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var connection = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = "HEPSIBURADA", Environment = "PRODUCTION", DisplayName = "HEPSIBURADA", ExternalStoreId = $"store-{Guid.NewGuid():N}", Status = "ACTIVE", ApiVersion = "V1", SettingsJson = "{}" };
        await using var db = fixture.CreateContext();
        db.Tenants.Add(tenant);
        db.PlatformConnections.Add(connection);
        await db.SaveChangesAsync();
        return (tenant, connection);
    }
}
