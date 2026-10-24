using System.Text;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Imports;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Deep audit 2026-10-20 S24: the Shopify CSV import never rewrites the amounts of an invoiced order,
// and a file that changes nothing leaves the orders (and their versions) as they were. An invoiced order whose
// amounts were never read (0 TL, as Shopify gave them) takes the file's amounts and prices.
[Collection("postgres-integration")]
public sealed class ShopifyOrderCsvImportPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    private const string Csv = "Name,Currency,Total,Discount Amount,Lineitem name,Lineitem price,Lineitem sku,Billing Name,Billing City\r\n"
        + "#CSV1,TRY,140.00,10.00,Elbise,150.00,SKU-A,Ada Örnek,İstanbul\r\n"
        + "#CSV2,TRY,140.00,10.00,Elbise,150.00,SKU-B,Can Örnek,Ankara\r\n"
        + "#CSV3,TRY,140.00,10.00,Elbise,150.00,SKU-C,Ece Örnek,İzmir\r\n";

    [PostgreSqlFact]
    public async Task InvoicedOrderKeepsItsAmountsAndARepeatedFileChangesNothing()
    {
        var (tenantId, userId, connectionId, plainId, invoicedId, emptyId) = await SeedAsync();
        try
        {
            await using (var db = fixture.CreateContext())
            {
                var first = await new ShopifyOrderCsvImportService(db, TimeProvider.System).ImportAsync(tenantId, userId, connectionId, new MemoryStream(Encoding.UTF8.GetBytes(Csv)), "csv-1", CancellationToken.None);
                Assert.Null(first.Error);
                Assert.Equal(3, first.Value!.UpdatedOrders);
                Assert.Contains(first.Value.Issues, issue => issue.Contains("birim fiyatları korundu", StringComparison.Ordinal));
                Assert.Contains(first.Value.Issues, issue => issue.Contains("dosyadan dolduruldu", StringComparison.Ordinal));
            }
            long plainVersion;
            await using (var verify = fixture.CreateContext())
            {
                var plain = await verify.Orders.AsNoTracking().SingleAsync(x => x.Id == plainId);
                Assert.Equal((150m, 10m, 140m), (plain.GrossAmount, plain.DiscountAmount, plain.NetAmount));
                plainVersion = plain.Version;
                var invoiced = await verify.Orders.AsNoTracking().SingleAsync(x => x.Id == invoicedId);
                Assert.Equal((55m, 5m, 50m), (invoiced.GrossAmount, invoiced.DiscountAmount, invoiced.NetAmount));
                Assert.Contains("Ankara", invoiced.InvoiceAddressSnapshotJson, StringComparison.Ordinal);
                Assert.Equal(55m, (await verify.OrderLines.AsNoTracking().SingleAsync(x => x.OrderId == invoicedId)).UnitPrice);
                var empty = await verify.Orders.AsNoTracking().SingleAsync(x => x.Id == emptyId);
                Assert.Equal((150m, 10m, 140m), (empty.GrossAmount, empty.DiscountAmount, empty.NetAmount));
                Assert.Equal(150m, (await verify.OrderLines.AsNoTracking().SingleAsync(x => x.OrderId == emptyId)).UnitPrice);
            }

            await using (var again = fixture.CreateContext())
            {
                var second = await new ShopifyOrderCsvImportService(again, TimeProvider.System).ImportAsync(tenantId, userId, connectionId, new MemoryStream(Encoding.UTF8.GetBytes(Csv)), "csv-2", CancellationToken.None);
                Assert.Null(second.Error);
                Assert.Equal(0, second.Value!.UpdatedOrders);
                Assert.Equal(0, second.Value.UpdatedLines);
            }
            await using var final = fixture.CreateContext();
            Assert.Equal(plainVersion, (await final.Orders.AsNoTracking().SingleAsync(x => x.Id == plainId)).Version);
        }
        finally
        {
            await CleanupAsync(tenantId);
        }
    }

    private async Task<(Guid TenantId, Guid UserId, Guid ConnectionId, Guid PlainId, Guid InvoicedId, Guid EmptyId)> SeedAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"csv-{Guid.NewGuid():N}"[..16], DisplayName = "csv", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var connection = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = "SHOPIFY", Environment = "PRODUCTION", DisplayName = "csv", ExternalStoreId = $"s-{Guid.NewGuid():N}"[..12], Status = "ACTIVE", ApiVersion = "2026-10" };
        Order NewOrder(string number, decimal gross, decimal discount, decimal net) => new() { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, ExternalOrderId = number, OrderNumber = number, Currency = "TRY", GrossAmount = gross, DiscountAmount = discount, NetAmount = net, OrderedAt = now.AddDays(-1), LastRemoteModifiedAt = now.AddDays(-1), CustomerSnapshotJson = "{}", ShipmentAddressSnapshotJson = "{}", InvoiceAddressSnapshotJson = "{}", DerivedStatus = "NEW", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var plain = NewOrder("#CSV1", 0m, 0m, 0m);
        var invoiced = NewOrder("#CSV2", 55m, 5m, 50m);
        var empty = NewOrder("#CSV3", 0m, 0m, 0m);
        await using var db = fixture.CreateContext();
        db.Tenants.Add(tenant);
        db.PlatformConnections.Add(connection);
        db.Orders.AddRange(plain, invoiced, empty);
        db.OrderLines.Add(new OrderLine { Id = Guid.CreateVersion7(), TenantId = tenant.Id, OrderId = plain.Id, ExternalLineId = "1", Sku = "SKU-A", TitleSnapshot = "Elbise", OrderedQuantity = 1, UnitPrice = 0m, RawStatus = "PAID" });
        db.OrderLines.Add(new OrderLine { Id = Guid.CreateVersion7(), TenantId = tenant.Id, OrderId = invoiced.Id, ExternalLineId = "2", Sku = "SKU-B", TitleSnapshot = "Elbise", OrderedQuantity = 1, UnitPrice = 55m, RawStatus = "PAID" });
        db.ShipmentPackages.Add(new ShipmentPackage { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, OrderId = invoiced.Id, ExternalPackageId = "order:2:remainder", Status = ShipmentPackageStatus.New, RawStatus = "UNFULFILLED", StatusOccurredAt = now.AddDays(-1), ManualInvoiceStatus = "UPLOADED", CreatedAt = now, UpdatedAt = now, Version = 1 });
        db.OrderLines.Add(new OrderLine { Id = Guid.CreateVersion7(), TenantId = tenant.Id, OrderId = empty.Id, ExternalLineId = "3", Sku = "SKU-C", TitleSnapshot = "Elbise", OrderedQuantity = 1, UnitPrice = 0m, RawStatus = "PAID" });
        db.ShipmentPackages.Add(new ShipmentPackage { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, OrderId = empty.Id, ExternalPackageId = "order:3:remainder", Status = ShipmentPackageStatus.New, RawStatus = "UNFULFILLED", StatusOccurredAt = now.AddDays(-1), ManualInvoiceStatus = "UPLOADED", CreatedAt = now, UpdatedAt = now, Version = 1 });
        await db.SaveChangesAsync();
        return (tenant.Id, Guid.CreateVersion7(), connection.Id, plain.Id, invoiced.Id, empty.Id);
    }

    private async Task CleanupAsync(Guid tenantId)
    {
        await using var db = fixture.CreateContext();
        await db.JobAttempts.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.IntegrationJobs.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.IntegrationOutboxEvents.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.AuditLogs.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.OrderLines.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.ShipmentPackages.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.Orders.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.PlatformConnections.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.Tenants.Where(row => row.Id == tenantId).ExecuteDeleteAsync();
    }
}
