using System.Text.Json;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Audit 29 Oct 2026, S-4: a Hepsiburada address event whose order read failed runs again after a later address event of
// the same order; the retry must not write its older address over the newer one. Runs inside a rolled-back transaction.
[Collection("postgres-integration")]
public sealed class HepsiburadaAddressEventPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    [PostgreSqlFact]
    public async Task A_retried_older_address_event_keeps_the_newer_address()
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"hb-address-{Guid.NewGuid():N}", DisplayName = "hb-address", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var connection = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = "HEPSIBURADA", Environment = "PRODUCTION", DisplayName = "Hepsiburada", ExternalStoreId = $"merchant-{Guid.NewGuid():N}", Status = "ACTIVE", ApiVersion = "V1", SettingsJson = "{}" };
        var order = new Order { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, ExternalOrderId = "4001", OrderNumber = "4001", Currency = "TRY", OrderedAt = now.AddDays(-1), LastRemoteModifiedAt = now.AddDays(-1), CustomerSnapshotJson = "{}", ShipmentAddressSnapshotJson = "{}", InvoiceAddressSnapshotJson = "{}", DerivedStatus = "CREATED", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var eventA = $"{connection.Id:N}:order_event:orders/4001/shippingaddress:AAAA";
        var eventB = $"{connection.Id:N}:order_event:orders/4001/shippingaddress:BBBB";
        using var bodyA = JsonDocument.Parse("""{"orderNumber":"4001","address":"A Sokak 1","city":"İstanbul"}""");
        using var bodyB = JsonDocument.Parse("""{"orderNumber":"4001","address":"B Sokak 2","city":"İzmir"}""");

        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        db.Tenants.Add(tenant);
        db.PlatformConnections.Add(connection);
        db.Orders.Add(order);
        db.InboxMessages.Add(Inbox(tenant.Id, eventA, now.AddMinutes(-10)));
        await db.SaveChangesAsync();
        var processor = new MarketplaceJobProcessor(db, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, new ConfigurationBuilder().Build(), fixture.TimeProvider);

        // A arrives and is written; its order read then fails, so the event stays to be retried.
        await processor.ApplyHepsiburadaShippingAddressAsync(tenant.Id, connection.Id, "4001", bodyA.RootElement, eventA, CancellationToken.None);
        Assert.Contains("A Sokak 1", await Address(db, order.Id));

        // B arrives meanwhile and is written.
        db.InboxMessages.Add(Inbox(tenant.Id, eventB, now.AddMinutes(-5)));
        await db.SaveChangesAsync();
        await processor.ApplyHepsiburadaShippingAddressAsync(tenant.Id, connection.Id, "4001", bodyB.RootElement, eventB, CancellationToken.None);
        Assert.Contains("B Sokak 2", await Address(db, order.Id));

        // The retry of A finds the later B and leaves the address alone.
        await processor.ApplyHepsiburadaShippingAddressAsync(tenant.Id, connection.Id, "4001", bodyA.RootElement, eventA, CancellationToken.None);
        var address = await Address(db, order.Id);
        Assert.Contains("B Sokak 2", address);
        Assert.Contains("İzmir", address);
        Assert.DoesNotContain("A Sokak 1", address);
        await transaction.RollbackAsync();
    }

    private static InboxMessage Inbox(Guid tenantId, string externalMessageId, DateTimeOffset receivedAt) =>
        new() { Id = Guid.CreateVersion7(), TenantId = tenantId, Source = "HEPSIBURADA_WEBHOOK", ExternalMessageId = externalMessageId, PayloadHash = Guid.NewGuid().ToString("N"), ReceivedAt = receivedAt };

    // The stored address and city, decoded.
    private static async Task<string> Address(AppDbContext db, Guid orderId)
    {
        using var snapshot = JsonDocument.Parse(await db.Orders.AsNoTracking().Where(x => x.Id == orderId).Select(x => x.ShipmentAddressSnapshotJson).SingleAsync());
        return $"{snapshot.RootElement.GetProperty("address").GetString()} / {snapshot.RootElement.GetProperty("city").GetString()}";
    }
}
