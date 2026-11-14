using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// A return detail line without a photo asks /orders/product-image with its barcode and the claim's store
// (MarketplaceSalesService.ReturnDetailLineImageUrls). An n11, Pazarama or PTT AVM store's lookup was refused (404) before
// anything was searched; it now finds the photo on that store's order lines and in the catalog like any other store.
[Collection("postgres-integration")]
public sealed class ReturnLineImageLookupPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    [Theory]
    [InlineData("N11")]
    [InlineData("PAZARAMA")]
    [InlineData("PTTAVM")]
    public void Every_marketplace_store_can_carry_a_return_lines_photo_lookup(string platform)
    {
        var (url, _) = MarketplaceSalesService.ReturnDetailLineImageUrls(null, null, "BRK-1", Guid.CreateVersion7());
        Assert.Contains("connectionId=", url, StringComparison.Ordinal);
        // n11 and Pazarama cannot be asked for a photo by barcode; PTT AVM can.
        Assert.Equal(platform == "PTTAVM", MarketplaceSalesService.HasLiveImageLookup(platform));
    }

    [PostgreSqlFact]
    public async Task An_n11_pazarama_or_pttavm_store_finds_the_photo_on_its_order_lines()
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"ri-{Guid.NewGuid():N}"[..16], DisplayName = "ri", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var connections = new[] { "N11", "PAZARAMA", "PTTAVM" }
            .Select(code => new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = code, Environment = "STAGE", DisplayName = code, ExternalStoreId = $"s-{Guid.NewGuid():N}"[..12], Status = "ACTIVE", ApiVersion = "V1" })
            .ToList();
        var orders = connections.Select((connection, index) => new Order
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            ConnectionId = connection.Id,
            ExternalOrderId = $"ri-{index}",
            OrderNumber = $"RI{index}",
            Currency = "TRY",
            OrderedAt = now,
            LastRemoteModifiedAt = now,
            CustomerSnapshotJson = "{}",
            ShipmentAddressSnapshotJson = "{}",
            InvoiceAddressSnapshotJson = "{}",
            DerivedStatus = "DELIVERED",
            CreatedAt = now,
            UpdatedAt = now,
            Version = 1
        }).ToList();
        var lines = orders.Select((order, index) => new OrderLine
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            OrderId = order.Id,
            ExternalLineId = $"l-{index}",
            Sku = $"RI-SKU-{index}",
            TitleSnapshot = "İade ürünü",
            OrderedQuantity = 1m,
            UnitPrice = 100m,
            VatRate = 10m,
            RawStatus = "Delivered",
            Version = 1,
            SourceSnapshotJson = $$"""{"imageUrl":"https://cdn.example.com/ri-{{index}}.jpg"}"""
        }).ToList();
        await using (var db = fixture.CreateContext())
        {
            db.Tenants.Add(tenant);
            db.PlatformConnections.AddRange(connections);
            db.Orders.AddRange(orders);
            db.OrderLines.AddRange(lines);
            await db.SaveChangesAsync();
        }

        await using var context = fixture.CreateContext();
        var sales = new MarketplaceSalesService(context, null!, new ConfigurationBuilder().Build(), null!, null!, null!, null!, TimeProvider.System);
        for (var index = 0; index < connections.Count; index++)
        {
            var image = await sales.ProductImageAsync(tenant.Id, $"RI-SKU-{index}", "test", CancellationToken.None, connections[index].Id);
            Assert.True(image.Succeeded, connections[index].PlatformCode);
            Assert.Equal($"https://cdn.example.com/ri-{index}.jpg", image.Value);
        }
    }
}
