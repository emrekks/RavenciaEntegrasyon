using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// RETURN-ORDER: a return's lines are listed in the order they were stored (their version 7 ids), as the return detail
// lists them, whatever order the database keeps the rows in.
[Collection("postgres-integration")]
public sealed class ReturnViewLineOrderPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    [PostgreSqlFact]
    public async Task A_returns_lines_are_listed_in_the_order_they_were_stored()
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"return-order-{Guid.NewGuid():N}", DisplayName = "return-order", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var connection = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = "TRENDYOL", Environment = "PRODUCTION", DisplayName = "Trendyol", ExternalStoreId = $"store-{Guid.NewGuid():N}", Status = "ACTIVE", ApiVersion = "V1" };
        var order = new Order { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, ExternalOrderId = "ORD-1", OrderNumber = "ORD-1", Currency = "TRY", GrossAmount = 100m, NetAmount = 100m, OrderedAt = now, LastRemoteModifiedAt = now, CustomerSnapshotJson = "{}", ShipmentAddressSnapshotJson = "{}", InvoiceAddressSnapshotJson = "{}", DerivedStatus = "DELIVERED", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var orderLines = Enumerable.Range(0, 4).Select(j => new OrderLine { Id = Guid.CreateVersion7(), TenantId = tenant.Id, OrderId = order.Id, ExternalLineId = $"L-{j}", Sku = $"SKU-{j}", Barcode = $"BC-{j}", TitleSnapshot = $"Ürün {j}", OrderedQuantity = 1, DeliveredQuantity = 1, ShippedQuantity = 1, UnitPrice = 50m, VatRate = 20m, RawStatus = "Delivered" }).ToList();
        var claim = new ReturnClaim { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, OrderId = order.Id, ExternalClaimId = "order-claim", Status = ReturnClaimStatus.ActionRequired, RawStatus = "WaitingInAction", CreatedAt = now, UpdatedAt = now, Version = 1 };
        // Ids made in the same millisecond are not ordered among themselves: sorted as the database sorts uuids.
        var lineIds = orderLines.Select(_ => Guid.CreateVersion7()).OrderBy(id => id.ToString("D"), StringComparer.Ordinal).ToList();
        var lastStored = lineIds[^1];
        try
        {
            await using (var db = fixture.CreateContext())
            {
                db.AddRange(tenant, connection, order, claim);
                db.OrderLines.AddRange(orderLines);
                await db.SaveChangesAsync();
            }
            // Stored newest id first, one save each, then the first stored row updated: the table now holds them out of id order.
            for (var j = orderLines.Count - 1; j >= 0; j--)
            {
                await using var db = fixture.CreateContext();
                db.ReturnLines.Add(new ReturnLine { Id = lineIds[j], TenantId = tenant.Id, ClaimId = claim.Id, OrderLineId = orderLines[j].Id, ExternalLineId = $"R-{j}", Quantity = 1m, RawStatus = "WaitingInAction" });
                await db.SaveChangesAsync();
            }
            await using (var db = fixture.CreateContext())
                await db.ReturnLines.Where(x => x.Id == lastStored).ExecuteUpdateAsync(set => set.SetProperty(x => x.Quantity, 1m));

            await using (var db = fixture.CreateContext())
            {
                var service = new MarketplaceSalesService(db, new CursorCodec(new EphemeralDataProtectionProvider(), TimeProvider.System), new ConfigurationBuilder().Build(), null!, null!, null!, null!, TimeProvider.System);
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    var view = (await service.ReturnViewsByIdAsync(tenant.Id, [claim.Id], CancellationToken.None))[claim.Id];
                    Assert.Equal(orderLines.Select(x => x.Id), view.Lines!.Select(x => x.Id));
                    Assert.Equal("BC-0", view.PrimaryBarcode);
                }
            }
        }
        finally
        {
            await using var db = fixture.CreateContext();
            await db.ReturnLines.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await db.ReturnClaims.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await db.OrderLines.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await db.Orders.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await db.PlatformConnections.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await db.IntegrationOutboxEvents.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await db.Tenants.Where(row => row.Id == tenant.Id).ExecuteDeleteAsync();
        }
    }
}
