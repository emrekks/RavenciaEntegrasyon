using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// ISLEYICI-12 (16 Nov 2026): an order a Trendyol notice could not map (here a broken order date) joins the re-read list as
// it does from an order page; the webhook path used to record only the issue, so the order waited for the stream.
public sealed partial class PostgreSqlTenantIsolationTests
{
    [PostgreSqlFact]
    public async Task Isleyici12_AnOrderAWebhookCannotMapJoinsTheRereadList()
    {
        var (tenant, connection, _) = await SeedReadGuardConnectionAsync("isleyici12-webhook", "TRENDYOL", "ORDERS_HOT");
        const string Raw = """
            {"content":[
              {"id":"pkg-2","orderNumber":"ord-2","currencyCode":"TRY","status":"Created","orderDate":99999999999999999,"lines":[
                {"lineId":"2","stockCode":"SKU-2","productName":"Bluz","quantity":1,"amount":100.00,"discount":0,"price":100.00,"vatRate":10}]}
            ]}
            """;
        var payload = JsonSerializer.Serialize(new { rawJson = Raw, externalMessageId = "isleyici12-message", resourceType = "ORDERS" });
        try
        {
            var result = await RunReadGuardJobAsync(tenant.Id, connection.Id, MarketplaceJobTypes.WebhookIngest, payload);
            Assert.Equal(JobCompletionKind.Succeeded, result.Kind);
            await using var check = fixture.CreateContext();
            var cursor = await check.SyncCursors.AsNoTracking().SingleAsync(x => x.TenantId == tenant.Id && x.ResourceType == RejectedOrderRereadPolicy.CursorResource);
            var entry = Assert.Single(RejectedOrderRereadPolicy.Parse(cursor.OpaqueCursor));
            Assert.Equal("ord-2", entry.OrderId);
            Assert.Equal(0, entry.Attempts);
        }
        finally { await DeleteReadGuardTenantAsync(tenant.Id); }
    }

    // B-10 (25 Nov 2026): an order the upsert keeps without a line it could not read joins the re-read list from a notice
    // too; the list was saved before the upsert, so this order was lost.
    [PostgreSqlFact]
    public async Task B10_AnOrderTheWebhookUpsertKeepsWithAnUnreadableLineJoinsTheRereadList()
    {
        var (tenant, connection, _) = await SeedReadGuardConnectionAsync("b10-webhook", "TRENDYOL", "ORDERS_HOT");
        const string Raw = """
            {"content":[
              {"id":"pkg-3","orderNumber":"ord-3","currencyCode":"TRY","status":"Created","orderDate":1763900000000,"totalPrice":200.00,"lines":[
                {"lineId":"31","stockCode":"SKU-31","productName":"Ceket","quantity":1,"amount":100.00,"discount":0,"price":100.00,"vatRate":10},
                {"lineId":"32","quantity":1,"amount":100.00,"discount":0,"price":100.00,"vatRate":10}]}
            ]}
            """;
        var payload = JsonSerializer.Serialize(new { rawJson = Raw, externalMessageId = "b10-message", resourceType = "ORDERS" });
        try
        {
            var result = await RunReadGuardJobAsync(tenant.Id, connection.Id, MarketplaceJobTypes.WebhookIngest, payload);
            Assert.Equal(JobCompletionKind.Succeeded, result.Kind);
            await using var check = fixture.CreateContext();
            var cursor = await check.SyncCursors.AsNoTracking().SingleAsync(x => x.TenantId == tenant.Id && x.ResourceType == RejectedOrderRereadPolicy.CursorResource);
            Assert.Equal("ord-3", Assert.Single(RejectedOrderRereadPolicy.Parse(cursor.OpaqueCursor)).OrderId);
        }
        finally
        {
            await using (var cleanup = fixture.CreateContext())
            {
                await cleanup.IntegrationOutboxEvents.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
                await cleanup.PackageLineAllocations.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
                await cleanup.OrderStatusHistory.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
                await cleanup.OrderLines.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
                await cleanup.ShipmentPackages.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
                await cleanup.Orders.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            }
            await DeleteReadGuardTenantAsync(tenant.Id);
        }
    }
}
