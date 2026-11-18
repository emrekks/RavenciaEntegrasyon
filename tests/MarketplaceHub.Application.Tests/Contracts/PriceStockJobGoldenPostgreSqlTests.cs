using System.Text.Json;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.PttAvm;
using MarketplaceHub.Infrastructure.Adapters.Trendyol;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarketplaceHub.Application.Tests.Contracts;

// ISLEYICI-9 stage 5 step 3c (16 Nov 2026): the price and stock writes (PRICE_INVENTORY_SYNC) and the automatic stock
// paths (STOCK_PROJECTION_DISPATCH, STOCK_RECONCILIATION), pinned before they move out of the processor. Automatic price
// and stock writes are off on every live connection by the owner's choice: the goldens pin that nothing is sent and no
// send is queued while the switches are off, as well as the exact body sent when a user sends by hand, the poll of its
// batch, an unclear answer sent again, the repeat-request refusals of Trendyol and PTT AVM, Pazarama's own record, a
// batch that outlives its window, Shopify's NOT_SENT and SHOPIFY_PRICE_ONLY_APPLIED lines, a refused unknown code and the
// automatic send queue when an owner has turned the switches on.
[Collection("postgres-integration")]
public sealed class PriceStockJobGoldenPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    private static readonly DateTimeOffset Start = JobGolden.Start;
    private const string WritesOn = "{\"ExternalWritesEnabled\":true}";

    private async Task<(GoldenRun Run, Guid ProductId, Guid[] Variants)> SeedAsync(string platform, bool writesOn, params string[] policies)
    {
        var run = new GoldenRun(fixture, platform) { ConnectionSettingsJson = writesOn ? WritesOn : null };
        run.Settings["FeatureFlags:ExternalWrites"] = "true";
        var productId = Guid.Empty;
        var variants = new List<Guid>();
        await run.SeedAsync(db =>
        {
            var product = new Product { Id = run.NewId(), TenantId = run.Tenant.Id, Title = "Fiyat stok ürünü", Description = "golden", Status = ProductStatus.Active, CreatedAt = Start, UpdatedAt = Start };
            productId = product.Id;
            run.Known[product.Id] = "product";
            db.Products.Add(product);
            // PTT AVM prices a product by the product code its import linked.
            if (platform == "PTTAVM")
            {
                var productLink = new MarketplaceProductLink { Id = run.NewId(), TenantId = run.Tenant.Id, ConnectionId = run.Connection.Id, ProductId = product.Id, ExternalId = "PS-MODEL", ExternalCode = "PS-MODEL" };
                run.Known[productLink.Id] = "product-link";
                db.MarketplaceProductLinks.Add(productLink);
            }
            foreach (var (sku, barcode, size) in new[] { ("PS-1", "869200000001", "S"), ("PS-2", "869200000002", "M") })
            {
                var variant = new ProductVariant { Id = run.NewId(), TenantId = run.Tenant.Id, ProductId = product.Id, Sku = sku, SkuNormalized = sku, Barcode = barcode, BarcodeNormalized = barcode, ModelCode = "PS-MODEL", OptionSignature = $"Renk=Siyah|Beden={size}", CreatedAt = Start, UpdatedAt = Start };
                run.Known[variant.Id] = $"variant-{sku}";
                variants.Add(variant.Id);
                db.ProductVariants.Add(variant);
                var inventory = new InventoryItem { Id = run.NewId(), TenantId = run.Tenant.Id, VariantId = variant.Id, LocationCode = "MAIN", OnHand = 5m, Available = 5m };
                var offer = new ChannelOffer { Id = run.NewId(), TenantId = run.Tenant.Id, ConnectionId = run.Connection.Id, VariantId = variant.Id, ListPrice = 150m, SalePrice = 120m, Currency = "TRY", VatRate = 20m, VatInclusion = "INCLUDED", RoundingMode = "HALF_EVEN", Status = "ACTIVE" };
                var externalId = platform switch { "HEPSIBURADA" => $"HBV0000{sku[^1]}GOLDEN", "SHOPIFY" => $"gid://shopify/ProductVariant/7000{sku[^1]}", "PTTAVM" => $"{barcode}", _ => barcode };
                var link = new MarketplaceVariantLink { Id = run.NewId(), TenantId = run.Tenant.Id, ConnectionId = run.Connection.Id, VariantId = variant.Id, ExternalId = externalId, ExternalBarcode = barcode, ExternalSku = sku, Version = 1 };
                run.Known[inventory.Id] = $"inventory-{sku}";
                run.Known[offer.Id] = $"offer-{sku}";
                run.Known[link.Id] = $"link-{sku}";
                db.InventoryItems.Add(inventory);
                db.ChannelOffers.Add(offer);
                db.MarketplaceVariantLinks.Add(link);
            }
            foreach (var policy in policies)
            {
                var row = new ConnectionSyncPolicy { Id = run.NewId(), TenantId = run.Tenant.Id, ConnectionId = run.Connection.Id, ResourceType = policy, IntervalSeconds = 0, Enabled = true, UserDefined = true };
                run.Known[row.Id] = $"policy-{policy}";
                db.ConnectionSyncPolicies.Add(row);
            }
        });
        return (run, productId, [.. variants]);
    }

    // The job a user's "Fiyat ve Stok Gönder" queues: its payload built by the panel's own composer, as the send screen builds it.
    private async Task<(Guid JobId, string PayloadJson)> QueueManualSendAsync(GoldenRun run, Guid productId)
    {
        var jobId = run.NewId();
        run.Known[jobId] = "job";
        await using var db = fixture.CreateContext(new FrozenClock(Start));
        var draft = await new PriceInventoryComposer(db).BuildAsync(run.Tenant.Id, run.Connection.Id, CancellationToken.None, productId: productId);
        Assert.True(draft.Succeeded, draft.Error?.Code);
        var payload = JsonSerializer.Serialize(new PriceInventoryJobPayload(jobId, run.Connection.Id, "SUBMIT", draft.Value!.PayloadHash, draft.Value.PayloadJson, draft.Value.Lines, null, null, ProductId: productId, Manual: true));
        db.IntegrationJobs.Add(new IntegrationJob { Id = jobId, TenantId = run.Tenant.Id, ConnectionId = run.Connection.Id, JobType = MarketplaceJobTypes.PriceInventorySync, PayloadJson = payload, PayloadVersion = 1, PayloadHash = "h", JobDedupKey = $"golden-pi:{jobId:N}", EffectIdempotencyKey = $"price-inventory:{jobId:N}", Status = JobStatus.Leased, AvailableAt = Start, CorrelationId = "golden", CreatedAt = Start, Version = 1 });
        await db.SaveChangesAsync();
        return (jobId, payload);
    }

    // The same job run again by the queue, each time with the payload its last run left on it, until it ends.
    private async Task RunUntilDoneAsync(GoldenRun run, GoldenMarketplace port, Guid jobId, int runs, TimeSpan? beforePoll = null)
    {
        for (var index = 0; index < runs; index++)
        {
            string payloadJson;
            await using (var db = fixture.CreateContext())
                payloadJson = await db.IntegrationJobs.Where(x => x.Id == jobId).Select(x => x.PayloadJson).SingleAsync();
            if (index > 0) run.Clock.Advance(beforePoll is { } wait && payloadJson.Contains("\"POLL\"", StringComparison.Ordinal) ? wait : TimeSpan.FromSeconds(30));
            var result = await run.RunAsync($"run-{index}", MarketplaceJobTypes.PriceInventorySync, port, payloadJson, jobId);
            if (result.Kind != JobCompletionKind.Retry) break;
        }
    }

    private static RemoteOperationLine Line(string key, bool succeeded, string? errorCode = null, bool retryable = false, string? status = null) => new(key, succeeded, null, errorCode, retryable, status);

    public static TheoryData<string, string> SendScenarios => new()
    {
        { "TRENDYOL", "accepted" }, { "TRENDYOL", "uncertain-then-accepted" }, { "TRENDYOL", "repeat-request" }, { "TRENDYOL", "expired" },
        { "TRENDYOL", "rejected-unknown-code" }, { "TRENDYOL", "line-unconfirmed" }, { "TRENDYOL", "writes-off" }, { "TRENDYOL", "automatic-policies-off" },
        { "N11", "accepted" }, { "PAZARAMA", "accepted" }, { "PTTAVM", "repeat-request" }, { "SHOPIFY", "not-sent-and-price-only" }, { "SHOPIFY", "price-refused-stock-written" }, { "HEPSIBURADA", "accepted" }
    };

    [PostgreSqlTheory]
    [MemberData(nameof(SendScenarios))]
    public async Task APriceStockSendSendsAndStoresTheSameThings(string platform, string scenario)
    {
        using var culture = JobGolden.InvariantCulture();
        var (run, productId, _) = await SeedAsync(platform, writesOn: true, MarketplaceExternalWritePolicies.Manual);
        var (jobId, payloadJson) = await QueueManualSendAsync(run, productId);
        if (scenario == "writes-off")
        {
            await using var db = fixture.CreateContext();
            await db.PlatformConnections.Where(x => x.Id == run.Connection.Id).ExecuteUpdateAsync(setters => setters.SetProperty(x => x.SettingsJson, "{}"));
        }
        if (scenario == "automatic-policies-off")
        {
            payloadJson = payloadJson.Replace("\"Automatic\":false", "\"Automatic\":true", StringComparison.Ordinal).Replace("\"Manual\":true", "\"Manual\":false", StringComparison.Ordinal);
            await using var db = fixture.CreateContext();
            await db.IntegrationJobs.Where(x => x.Id == jobId).ExecuteUpdateAsync(setters => setters.SetProperty(x => x.PayloadJson, payloadJson));
        }

        var port = new GoldenMarketplace();
        var pushes = 0;
        port.Answers["inventory.Push"] = _ =>
        {
            pushes++;
            return scenario switch
            {
                "uncertain-then-accepted" when pushes == 1 => AdapterResult<RemoteOperationRef>.Failure(GoldenMarketplace.Error(AdapterErrorClass.Remote5xx, "REMOTE_5XX", 503)),
                "repeat-request" => AdapterResult<RemoteOperationRef>.Failure(GoldenMarketplace.Error(AdapterErrorClass.Validation, platform == "PTTAVM" ? PttAvmProductPayloadRules.RepeatRequestWaitCode : TrendyolStockRules.RepeatRequestWaitCode, 400)),
                _ => AdapterResult<RemoteOperationRef>.Success(new(platform == "PAZARAMA" ? "s:id-1:1:0" : "batch-golden-1", "PRICE_INVENTORY", Start.AddMinutes(1)))
            };
        };
        var polls = 0;
        port.Answers["products.GetOperation"] = _ =>
        {
            polls++;
            IReadOnlyList<RemoteOperationLine> lines = scenario switch
            {
                "rejected-unknown-code" => [Line("869200000001", true), Line("869200000002", false, "TRENDYOL_BARCODE_NOT_FOUND", status: "Ürün bulunamadı")],
                "line-unconfirmed" => [Line("869200000001", true)],
                "not-sent-and-price-only" => [Line("869200000001", false, "PRICE_INVENTORY_NOT_SENT", retryable: true, status: PriceStockService.NotSentLineStatus), Line("869200000002", false, PriceStockService.PriceOnlyAppliedCode, retryable: true)],
                // Review of 18 Nov 2026: Shopify refused the price but wrote the stock; the stock mark moves, the price goes again.
                "price-refused-stock-written" => [Line("869200000001", true), Line("869200000002", false, PriceStockService.StockOnlyAppliedCode, status: "Price must be greater than 0")],
                "accepted" when platform == "HEPSIBURADA" => [],
                _ => [Line("869200000001", true), Line("869200000002", true)]
            };
            return polls == 1 && scenario is not ("not-sent-and-price-only" or "price-refused-stock-written")
                ? AdapterResult<RemoteOperationStatus>.Success(new("batch-golden-1", "IN_PROGRESS", []))
                : AdapterResult<RemoteOperationStatus>.Success(new("batch-golden-1", "COMPLETED", lines));
        };

        await RunUntilDoneAsync(run, port, jobId, runs: 5, beforePoll: scenario == "expired" ? TimeSpan.FromHours(9) : null);
        await run.MatchesAsync($"PriceStockJobs/{platform.ToLowerInvariant()}-price_inventory_sync-{scenario}.golden.json");
    }

    public static TheoryData<string, string> AutomaticScenarios => new()
    {
        { "STOCK_PROJECTION_DISPATCH", "writes-off" }, { "STOCK_PROJECTION_DISPATCH", "policies-off" }, { "STOCK_PROJECTION_DISPATCH", "price-policy-off" }, { "STOCK_PROJECTION_DISPATCH", "owner-turned-on" },
        { "STOCK_RECONCILIATION", "writes-off" }, { "STOCK_RECONCILIATION", "policies-off" }, { "STOCK_RECONCILIATION", "owner-turned-on" }
    };

    [PostgreSqlTheory]
    [MemberData(nameof(AutomaticScenarios))]
    public async Task AnAutomaticStockPathSendsNothingWhileItsSwitchesAreOff(string jobType, string scenario)
    {
        using var culture = JobGolden.InvariantCulture();
        string[] policies = scenario switch
        {
            "owner-turned-on" => [MarketplaceExternalWritePolicies.Stock, MarketplaceExternalWritePolicies.Price],
            "price-policy-off" => [MarketplaceExternalWritePolicies.Stock],
            _ => []
        };
        var (run, _, variants) = await SeedAsync("TRENDYOL", writesOn: scenario != "writes-off", policies);
        var port = new GoldenMarketplace();
        var payload = jobType == MarketplaceJobTypes.StockProjectionDispatch ? $$"""{"variantId":"{{variants[0]}}","sourceEventId":"stock:golden"}""" : "{}";
        await run.RunAsync("automatic", jobType, port, payload, run.NewId());
        run.Clock.Advance(TimeSpan.FromMinutes(1));
        await run.RunAsync("automatic-again", jobType, port, payload, run.NewId());
        await run.MatchesAsync($"PriceStockJobs/trendyol-{jobType.ToLowerInvariant()}-{scenario}.golden.json");
    }
}
