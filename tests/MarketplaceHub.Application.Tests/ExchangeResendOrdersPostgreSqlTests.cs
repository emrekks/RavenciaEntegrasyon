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

    private async Task<(Tenant Tenant, PlatformConnection Connection)> SeedAsync()
    {
        var now = fixture.Now;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"exr-{Guid.NewGuid():N}", DisplayName = "exr", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var connection = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = "TRENDYOL", Environment = "PRODUCTION", DisplayName = "Trendyol", ExternalStoreId = $"store-{Guid.NewGuid():N}", Status = "ACTIVE", ApiVersion = "V1", SettingsJson = "{}" };
        await using var db = fixture.CreateContext();
        db.Tenants.Add(tenant);
        db.PlatformConnections.Add(connection);
        await db.SaveChangesAsync();
        return (tenant, connection);
    }
}
