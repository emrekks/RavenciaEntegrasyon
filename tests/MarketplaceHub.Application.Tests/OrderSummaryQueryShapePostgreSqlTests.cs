using System.Data.Common;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// O10 (scan of 23 Nov 2026): the order summary took 9-12 s right after a deploy. Its 16 tab queries were one query joined
// by UNION ALL, which EF compiles on first use far more slowly than the tab queries one by one (18 s against under 0.5 s
// on a seeded copy). The counters stay the tab lists' totals.
[Collection("postgres-integration")]
public sealed class OrderSummaryQueryShapePostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    private static readonly string[] Tabs = ["ALL", "NEW", "PROCESSING", "SHIPPED", "DELIVERED", "RESENT", "ON_HOLD", "CANCELLED", "RETURNED", "RETURN_IN_TRANSIT", "PARTIALLY_CANCELLED", "MANUAL_REVIEW", "PENDING", "UNVERIFIED", "PAYMENT_PENDING", "UNDELIVERED", "LATE"];

    [PostgreSqlFact]
    public async Task EachTabIsCountedByItsOwnQuery_AndEveryCounterIsItsListTotal()
    {
        var seed = await SeedAsync();
        try
        {
            var capture = new CommandCapture();
            await using var db = Context(capture);
            var service = Service(db);
            var summary = await service.OrderSummaryAsync(seed.TenantId, new OrderListQuery(), CancellationToken.None);

            Assert.DoesNotContain(capture.Commands, command => Occurrences(command, "UNION ALL") > 0);
            await AssertCountersAreListTotals(service, seed.TenantId, new OrderListQuery(), summary);
            Assert.True(summary.All > 0 && summary.New > 0 && summary.Delivered > 0 && summary.Cancelled > 0 && summary.Resent > 0 && summary.Late > 0);
        }
        finally { await CleanAsync(seed); }
    }

    // A search term is matched once per summary (the customer-snapshot pattern, ~*, ran over every order once per tab),
    // and every counter stays its list's total under the same search, the filtered cards' figures included.
    [PostgreSqlFact]
    public async Task ASearchIsMatchedOnce_AndTheCountersStayTheListTotals()
    {
        var seed = await SeedAsync();
        try
        {
            foreach (var query in new[] { new OrderListQuery(Search: "Ayşe"), new OrderListQuery(Search: "elbise", Cargo: "ARAS"), new OrderListQuery(Search: "Mehmet", Platform: "TRENDYOL"), new OrderListQuery(Search: "Ayşe", ResentNotShipped: true) })
            {
                var capture = new CommandCapture();
                await using var db = Context(capture);
                var service = Service(db);
                var summary = await service.OrderSummaryAsync(seed.TenantId, query, CancellationToken.None);
                Assert.Single(capture.Commands, command => command.Contains("~*", StringComparison.Ordinal));
                await AssertCountersAreListTotals(service, seed.TenantId, query, summary);
                // The cards' store figures under the same search are the tab lists' orders per store.
                var expected = await ExpectedCardsAsync(service, seed.TenantId, query);
                Assert.Equal(expected.NewByConnection, summary.NewByConnection);
                if (query.ResentNotShipped == true) Assert.Equal(expected.ResentByConnection, summary.ResentByConnection);
            }
        }
        finally { await CleanAsync(seed); }
    }

    // Per store, the orders each tab lists under the query: what the cards show.
    private static async Task<(IReadOnlyDictionary<string, int> NewByConnection, IReadOnlyDictionary<string, int> ResentByConnection)> ExpectedCardsAsync(MarketplaceSalesService service, Guid tenantId, OrderListQuery query)
    {
        async Task<IReadOnlyDictionary<string, int>> PerStore(string tab) =>
            (await service.OrdersAsync(tenantId, 100, 1, null, query with { Status = tab }, CancellationToken.None)).Items
                .GroupBy(order => order.ConnectionId).ToDictionary(group => group.Key.ToString()!, group => group.Count());
        return (await PerStore("NEW"), await PerStore("RESENT"));
    }

    private static async Task AssertCountersAreListTotals(MarketplaceSalesService service, Guid tenantId, OrderListQuery query, OrderSummaryView summary)
    {
        var byTab = new Dictionary<string, int>
        {
            ["ALL"] = summary.All,
            ["NEW"] = summary.New,
            ["PROCESSING"] = summary.Processing,
            ["SHIPPED"] = summary.Shipped,
            ["DELIVERED"] = summary.Delivered,
            ["RESENT"] = summary.Resent,
            ["ON_HOLD"] = summary.OnHold,
            ["CANCELLED"] = summary.Cancelled,
            ["RETURNED"] = summary.Returned,
            ["RETURN_IN_TRANSIT"] = summary.ReturnInTransit,
            ["PARTIALLY_CANCELLED"] = summary.PartiallyCancelled,
            ["MANUAL_REVIEW"] = summary.ManualReview,
            ["PENDING"] = summary.Pending,
            ["UNVERIFIED"] = summary.Unverified,
            ["PAYMENT_PENDING"] = summary.PaymentPending,
            ["UNDELIVERED"] = summary.Undelivered,
            ["LATE"] = summary.Late
        };
        foreach (var tab in Tabs)
            Assert.True(byTab[tab] == (await service.OrdersAsync(tenantId, 100, 1, null, query with { Status = tab }, CancellationToken.None)).TotalCount, $"{tab} counter differs from its list total");
    }

    private static int Occurrences(string text, string part)
    {
        var count = 0;
        for (var index = text.IndexOf(part, StringComparison.Ordinal); index >= 0; index = text.IndexOf(part, index + part.Length, StringComparison.Ordinal)) count++;
        return count;
    }

    internal sealed record Seeded(Guid TenantId);

    // Orders of two stores in most tabs: new (one late), shipped, delivered, cancelled, returned, a resend, and a partly
    // cancelled one; some named "Ayşe" and some with an "ELBISE" product, for the search.
    private async Task<Seeded> SeedAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"sq-{Guid.NewGuid():N}"[..16], DisplayName = "sq", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var stores = new[] { "TRENDYOL", "HEPSIBURADA" }.Select(code => new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = code, Environment = "STAGE", DisplayName = code, ExternalStoreId = $"s-{Guid.NewGuid():N}"[..12], Status = "ACTIVE", ApiVersion = "V1" }).ToArray();
        await using var db = fixture.CreateContext();
        db.Tenants.Add(tenant);
        db.PlatformConnections.AddRange(stores);
        var index = 0;
        void Add(PlatformConnection store, string derived, ShipmentPackageStatus status, string name, string title, bool late = false, bool resend = false, bool partlyCancelled = false)
        {
            index++;
            var order = new Order
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenant.Id,
                ConnectionId = store.Id,
                ExternalOrderId = $"sq-{index}",
                OrderNumber = $"SQ{index}",
                Currency = "TRY",
                OrderedAt = now.AddHours(-index),
                LastRemoteModifiedAt = now,
                ShipmentDueAt = late ? now.AddHours(-1) : derived == "NEW" ? now.AddDays(1) : null,
                CustomerSnapshotJson = $"{{\"customerFirstName\":\"{name}\"}}",
                ShipmentAddressSnapshotJson = $"{{\"fullName\":\"{name}\"}}",
                InvoiceAddressSnapshotJson = $"{{\"fullName\":\"{name}\"}}",
                DerivedStatus = derived,
                CreatedAt = now,
                UpdatedAt = now,
                Version = 1
            };
            db.Orders.Add(order);
            db.OrderLines.Add(new OrderLine { Id = Guid.CreateVersion7(), TenantId = tenant.Id, OrderId = order.Id, ExternalLineId = "1", Sku = $"SQ-{index}", TitleSnapshot = title, OrderedQuantity = 2, CancelledQuantity = partlyCancelled ? 1 : derived == "CANCELLED" ? 2 : 0, UnitPrice = 10m, VatRate = 20m, RawStatus = derived });
            db.ShipmentPackages.Add(new ShipmentPackage { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = store.Id, OrderId = order.Id, ExternalPackageId = $"sq-{index}", Status = status, RawStatus = status.ToString(), CargoProviderExternalId = index % 2 == 0 ? "ARAS" : "YURTICI", StatusOccurredAt = now, MarketplaceInvoiceStatus = MarketplaceInvoiceStatus.Unknown, CreatedAt = now, UpdatedAt = now, Version = 1 });
            if (resend)
                db.ShipmentPackages.Add(new ShipmentPackage { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = store.Id, OrderId = order.Id, ExternalPackageId = $"sq-{index}-r", OriginExternalPackageId = $"sq-{index}", CreatedBy = "resend", Status = ShipmentPackageStatus.New, RawStatus = "Created", StatusOccurredAt = now, MarketplaceInvoiceStatus = MarketplaceInvoiceStatus.Unknown, CreatedAt = now, UpdatedAt = now, Version = 1 });
        }
        foreach (var store in stores)
        {
            Add(store, "NEW", ShipmentPackageStatus.New, "Ayşe", "Kadın Elbise");
            Add(store, "NEW", ShipmentPackageStatus.New, "Mehmet", "Gömlek", late: true);
            Add(store, "NEW", ShipmentPackageStatus.New, "Mehmet", "Kadın Elbise", partlyCancelled: true);
            Add(store, "SHIPPED", ShipmentPackageStatus.Shipped, "Ayşe", "Gömlek");
            Add(store, "DELIVERED", ShipmentPackageStatus.Delivered, "Zeynep", "Kadın Elbise");
            Add(store, "DELIVERED", ShipmentPackageStatus.Delivered, "Ayşe", "Pantolon", resend: true);
            Add(store, "CANCELLED", ShipmentPackageStatus.Cancelled, "Ayşe", "Kadın Elbise");
            Add(store, "RETURNED", ShipmentPackageStatus.Returned, "Zeynep", "Gömlek");
        }
        await db.SaveChangesAsync();
        return new(tenant.Id);
    }

    private async Task CleanAsync(Seeded seed)
    {
        await using var db = fixture.CreateContext();
        await db.IntegrationOutboxEvents.Where(row => row.TenantId == seed.TenantId).ExecuteDeleteAsync();
        await db.ShipmentPackages.Where(row => row.TenantId == seed.TenantId).ExecuteDeleteAsync();
        await db.OrderLines.Where(row => row.TenantId == seed.TenantId).ExecuteDeleteAsync();
        await db.Orders.Where(row => row.TenantId == seed.TenantId).ExecuteDeleteAsync();
        await db.PlatformConnections.Where(row => row.TenantId == seed.TenantId).ExecuteDeleteAsync();
        await db.Tenants.Where(row => row.Id == seed.TenantId).ExecuteDeleteAsync();
    }

    private static AppDbContext Context(CommandCapture capture) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(Environment.GetEnvironmentVariable("MARKETPLACEHUB_TEST_CONNECTION")).AddInterceptors(capture).Options);

    private MarketplaceSalesService Service(AppDbContext db) =>
        new(db, new CursorCodec(new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider(), fixture.TimeProvider), new ConfigurationBuilder().Build(), null!, null!, null!, null!, TimeProvider.System);

    private sealed class CommandCapture : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
