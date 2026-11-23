using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.Trendyol.Mapping;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Owner, 18 Nov 2026: Trendyol claim of order 1000000001 (reason code WRONGCHANGEREQUEST) is an exchange, yet the
// panel showed it as a plain return: the Trendyol mapper never filled CustomerRequest. A later read of a claim stored
// before the fix fills it and leaves the outcome (ResolvedWith, status) alone; the invoice's return rule counts an accepted
// Trendyol exchange request as a return as before (only Hepsiburada's exchanges take no unit back; review 18 Nov 2026).
[Collection("postgres-integration")]
public sealed class TrendyolExchangeRequestPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    [PostgreSqlFact]
    public async Task A_later_read_fills_the_exchange_request_and_keeps_the_outcome_and_the_invoice_rule()
    {
        var (tenant, connection) = await SeedAsync("TRENDYOL");
        var order = await SeedOrderAsync(tenant.Id, connection.Id, "ty-exch-1");

        // The claim as the code before the fix stored it: no customer request.
        await using (var db = fixture.CreateContext())
            await Processor(db).UpsertReturn(tenant.Id, connection.Id, new RemoteReturnClaim("claim-ty-exch", "ty-exch-1", "Accepted", "WRONGCHANGEREQUEST", null, null, fixture.Now.AddHours(-2),
                [new RemoteReturnLine("ci-1", "ol-1", 1, RawStatus: "Accepted")], "{}"), CancellationToken.None);
        await using (var db = fixture.CreateContext())
        {
            var stored = await db.ReturnClaims.SingleAsync(x => x.TenantId == tenant.Id);
            Assert.Null(stored.CustomerRequest);
            // An outcome already recorded on the claim (as the owner's decision would leave it).
            await db.ReturnClaims.Where(x => x.Id == stored.Id).ExecuteUpdateAsync(x => x.SetProperty(c => c.ResolvedWith, ReturnCustomerRequestPolicy.Refund));
        }

        // The next RETURN_SYNC / RETURN_STATUS_SYNC read of the same claim.
        var read = Assert.Single(TrendyolJsonMapper.Returns(ClaimPage("claim-ty-exch", "ty-exch-1", "WRONGCHANGEREQUEST", fixture.Now.AddHours(-1))).Items);
        Assert.Equal(ReturnCustomerRequestPolicy.Change, read.CustomerRequest);
        Assert.Null(read.ResolvedWith);
        await using (var db = fixture.CreateContext())
            await Processor(db).UpsertReturn(tenant.Id, connection.Id, read, CancellationToken.None);

        await using (var db = fixture.CreateContext())
        {
            var claim = await db.ReturnClaims.AsNoTracking().SingleAsync(x => x.TenantId == tenant.Id);
            Assert.Equal(ReturnCustomerRequestPolicy.Change, claim.CustomerRequest);
            Assert.Equal(ReturnCustomerRequestPolicy.Refund, claim.ResolvedWith);
            Assert.Equal(ReturnClaimStatus.Approved, claim.Status);
            // Review, 18 Nov 2026: a Trendyol exchange request Trendyol accepted may be a refund (its claim does not say), so
            // its accepted unit counts as returned, as on main; only Hepsiburada's exchanges take none back.
            Assert.True(ReturnCustomerRequestPolicy.CountsAsReturn(claim.CustomerRequest, claim.RawStatus, "TRENDYOL"));
            await db.ReturnClaims.Where(x => x.Id == claim.Id).ExecuteUpdateAsync(x => x.SetProperty(c => c.ResolvedWith, (string?)null));
            var returned = await InvoiceSaleReturns.ApprovedClaimQuantitiesAsync(db, tenant.Id, [order.Id], CancellationToken.None);
            Assert.Equal(1m, Assert.Single(returned).Value);
        }

        // A read that does not say it (another reason) leaves the known request as it is.
        await using (var db = fixture.CreateContext())
            await Processor(db).UpsertReturn(tenant.Id, connection.Id, Assert.Single(TrendyolJsonMapper.Returns(ClaimPage("claim-ty-exch", "ty-exch-1", "WRONGITEM", fixture.Now.AddMinutes(-30))).Items), CancellationToken.None);
        await using (var db = fixture.CreateContext())
            Assert.Equal(ReturnCustomerRequestPolicy.Change, (await db.ReturnClaims.AsNoTracking().SingleAsync(x => x.TenantId == tenant.Id)).CustomerRequest);
    }

    [PostgreSqlFact]
    public async Task A_Hepsiburada_exchange_still_takes_no_units_back()
    {
        var (tenant, connection) = await SeedAsync("HEPSIBURADA");
        var order = await SeedOrderAsync(tenant.Id, connection.Id, "hb-exch-1");
        await using (var db = fixture.CreateContext())
            await Processor(db).UpsertReturn(tenant.Id, connection.Id, new RemoteReturnClaim("claim-hb-exch", "hb-exch-1", "Accepted", null, null, null, fixture.Now.AddHours(-1),
                [new RemoteReturnLine("hb-ci-1", "ol-1", 1, RawStatus: "Accepted")], "{}", CustomerRequest: ReturnCustomerRequestPolicy.Change), CancellationToken.None);
        await using (var db = fixture.CreateContext())
        {
            Assert.Equal(ReturnClaimStatus.Approved, (await db.ReturnClaims.AsNoTracking().SingleAsync(x => x.TenantId == tenant.Id)).Status);
            Assert.Empty(await InvoiceSaleReturns.ApprovedClaimQuantitiesAsync(db, tenant.Id, [order.Id], CancellationToken.None));
        }
    }

    private static string ClaimPage(string claimId, string orderNumber, string reasonCode, DateTimeOffset modified) => $$"""
        {
          "content": [
            {
              "claimId": "{{claimId}}", "orderNumber": "{{orderNumber}}", "orderDate": 1782998302000, "claimDate": 1782998402000,
              "lastModifiedDate": {{modified.ToUnixTimeMilliseconds()}},
              "items": [
                {
                  "orderLine": { "id": "ol-1", "price": 100, "productName": "Ürün" },
                  "claimItems": [
                    { "id": "ci-1", "customerClaimItemReason": { "id": "0", "name": "Sebep", "code": "{{reasonCode}}" }, "claimItemStatus": { "name": "Accepted" } }
                  ]
                }
              ]
            }
          ],
          "page": 0, "totalPages": 1
        }
        """;

    private MarketplaceJobProcessor Processor(AppDbContext db) =>
        new(db, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, new ConfigurationBuilder().Build(), fixture.TimeProvider);

    private async Task<Order> SeedOrderAsync(Guid tenantId, Guid connectionId, string number)
    {
        var order = new Order { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = connectionId, ExternalOrderId = number, OrderNumber = number, Currency = "TRY", OrderedAt = fixture.Now.AddDays(-5), LastRemoteModifiedAt = fixture.Now.AddDays(-5), CustomerSnapshotJson = "{}", ShipmentAddressSnapshotJson = "{}", InvoiceAddressSnapshotJson = "{}", DerivedStatus = "DELIVERED", CreatedAt = fixture.Now, UpdatedAt = fixture.Now, Version = 1 };
        await using var db = fixture.CreateContext();
        db.Orders.Add(order);
        db.OrderLines.Add(new OrderLine { Id = Guid.CreateVersion7(), TenantId = tenantId, OrderId = order.Id, ExternalLineId = "ol-1", Sku = "SKU", TitleSnapshot = "Ürün", RawStatus = "Delivered", OrderedQuantity = 1, ShippedQuantity = 1, DeliveredQuantity = 1, UnitPrice = 100, VatRate = 10, Version = 1 });
        await db.SaveChangesAsync();
        return order;
    }

    private async Task<(Tenant Tenant, PlatformConnection Connection)> SeedAsync(string platformCode)
    {
        var now = fixture.Now;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"exch-{Guid.NewGuid():N}", DisplayName = "exch", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var connection = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = platformCode, Environment = "PRODUCTION", DisplayName = platformCode, ExternalStoreId = $"store-{Guid.NewGuid():N}", Status = "ACTIVE", ApiVersion = "V1", SettingsJson = "{}" };
        await using var db = fixture.CreateContext();
        db.Tenants.Add(tenant);
        db.PlatformConnections.Add(connection);
        await db.SaveChangesAsync();
        return (tenant, connection);
    }
}
