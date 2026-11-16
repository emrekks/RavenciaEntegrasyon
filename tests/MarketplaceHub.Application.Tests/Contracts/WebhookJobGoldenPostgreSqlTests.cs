using System.Text.Json;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.Shopify;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests.Contracts;

// ISLEYICI-9 stage 5 step 3e (16 Nov 2026): the three webhook ingests (WEBHOOK_INGEST, SHOPIFY_WEBHOOK_INGEST,
// HEPSIBURADA_WEBHOOK_INGEST) pinned before they move out of the processor: what a notice makes the panel read, what it
// stores, the inbox row it closes, the targeted order reads it queues (an unpack of more orders than one run reads) and
// the notices it cannot read. Reads only: a notice never sends anything to a marketplace.
[Collection("postgres-integration")]
public sealed class WebhookJobGoldenPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    private static readonly DateTimeOffset Start = JobGolden.Start;

    public static TheoryData<string, string> Scenarios => new()
    {
        { "TRENDYOL", "order-package" }, { "TRENDYOL", "unreadable" },
        { "SHOPIFY", "order" }, { "SHOPIFY", "refund" }, { "SHOPIFY", "order-read-refused" }, { "SHOPIFY", "no-order-number" },
        { "HEPSIBURADA", "order-event" }, { "HEPSIBURADA", "package-event-unknown" }, { "HEPSIBURADA", "unpack-many" }, { "HEPSIBURADA", "address" },
        { "HEPSIBURADA", "claim" }, { "HEPSIBURADA", "unreadable" }
    };

    private static (string ResourceType, string Raw, string MessageId) Notice(string platform, string scenario) => (platform, scenario) switch
    {
        ("TRENDYOL", "order-package") => ("ORDER", ContractFixture.Read("Trendyol/shipmentPackages-docs-sample.json"), "ty-msg-1"),
        ("TRENDYOL", _) => ("ORDER", "{not json", "ty-msg-2"),
        ("SHOPIFY", "refund") => ("REFUNDS", "{\"id\":991,\"order_id\":1001}", "sh-msg-2"),
        ("SHOPIFY", "no-order-number") => ("ORDERS", "{\"name\":\"#1001\"}", "sh-msg-4"),
        ("SHOPIFY", _) => ("ORDERS", "{\"id\":1001}", "sh-msg-1"),
        ("HEPSIBURADA", "order-event") => ("ORDER_EVENT", "{\"orderNumber\":\"4000000001\"}", "connection:order_event:orders/4000000001/cancel:h1"),
        ("HEPSIBURADA", "package-event-unknown") => ("ORDER_EVENT", "{}", "connection:order_event:packages/PKG-UNKNOWN/deliver:h2"),
        ("HEPSIBURADA", "unpack-many") => ("ORDER_EVENT", JsonSerializer.Serialize(new { packageNumber = "PKG-UNPACK", orderNumbers = Enumerable.Range(1, 12).Select(index => $"40000001{index:D2}").ToArray(), unpackedDate = "2029-12-31T10:00:00" }), "connection:order_event:packages/PKG-UNPACK/unpack:h3"),
        ("HEPSIBURADA", "address") => ("ORDER_EVENT", "{\"orderNumber\":\"4000000001\",\"address\":\"Yeni Mahalle 1\",\"city\":\"İzmir\"}", "connection:order_event:orders/4000000001/shippingaddress:h4"),
        ("HEPSIBURADA", "claim") => ("CLAIM", ContractFixture.Read("Hepsiburada/claim-awaitingaction-docs-sample.json"), "hb-claim-1"),
        _ => ("CLAIM", "{not json", "hb-msg-bad")
    };

    [PostgreSqlTheory]
    [MemberData(nameof(Scenarios))]
    public async Task AWebhookReadsAndStoresTheSameThings(string platform, string scenario)
    {
        var run = new GoldenRun(fixture, platform);
        var (resourceType, raw, messageId) = Notice(platform, scenario);
        messageId = messageId.Replace("connection:", "golden-connection:", StringComparison.Ordinal);
        await run.SeedAsync(db =>
        {
            var inbox = new InboxMessage { Id = run.NewId(), TenantId = run.Tenant.Id, Source = $"{platform}_WEBHOOK", ExternalMessageId = messageId, PayloadHash = "h", ReceivedAt = Start.AddMinutes(-1) };
            run.Known[inbox.Id] = "inbox";
            db.InboxMessages.Add(inbox);
            if (platform == "HEPSIBURADA" && scenario is "address" or "order-event")
            {
                var order = GoldenSeeds.NewOrder(run, "4000000001", Start.AddDays(-1));
                db.Orders.Add(order);
                db.ShipmentPackages.Add(GoldenSeeds.NewPackage(run, order, "PKG-UNPACK", Start.AddHours(-5)));
            }
        });

        var port = new GoldenMarketplace();
        IReadOnlyList<RemoteOrder> shopifyOrders;
        using (var shopify = ContractFixture.Json("Shopify/orders-page.json"))
            shopifyOrders = shopify.RootElement.GetProperty("data").GetProperty("orders").GetProperty("nodes").EnumerateArray().Select(node => ShopifyHttpClient.MapOrder(node, "https://fake-shop.example.test")).ToList();
        port.Answers["orders.Get"] = args => scenario == "order-read-refused"
            ? AdapterResult<RemoteOrder>.Failure(GoldenMarketplace.Error(AdapterErrorClass.Remote5xx, "REMOTE_5XX", 503))
            : shopifyOrders.FirstOrDefault(order => order.ExternalOrderId == (string)args[0]!) is { } found
                ? AdapterResult<RemoteOrder>.Success(found)
                : AdapterResult<RemoteOrder>.Failure(GoldenMarketplace.Error(AdapterErrorClass.NotFound, "REMOTE_ORDER_NOT_FOUND", 404));
        port.Answers["packages.Tracking"] = _ => AdapterResult<PackageTrackingStatusSnapshot>.Failure(GoldenMarketplace.Error(AdapterErrorClass.NotFound, "REMOTE_PACKAGE_NOT_FOUND", 404));

        var jobType = MarketplaceJobTypes.ForPlatform(platform, MarketplaceJobTypes.WebhookIngest);
        var payload = JsonSerializer.Serialize(new { rawJson = raw, externalMessageId = messageId, resourceType });
        await run.RunAsync("ingest", jobType, port, payload, run.NewId());
        run.Clock.Advance(TimeSpan.FromMinutes(1));
        await run.RunAsync("ingest-again", jobType, port, payload, run.NewId());
        await run.MatchesAsync($"WebhookJobs/{platform.ToLowerInvariant()}-webhook_ingest-{scenario}.golden.json");
    }
}
