using System.Text.Json;
using System.Text.Json.Nodes;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarketplaceHub.Application.Tests.Contracts;

// Owner decision of 27 Nov 2026 (TrendyolPriceRefusal): a Trendyol row whose PRICE Trendyol refused goes with its stock alone
// on the next send that happens anyway, while the price to send is the refused one. The scripted Trendyol below refuses the
// price of one barcode every time a line carries it and takes everything else, as a marketplace that keeps refusing a price
// would. All values are made up; nothing is sent to a marketplace.
[Collection("postgres-integration")]
public sealed class TrendyolPriceRefusalStockOnlyPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    private static readonly DateTimeOffset Start = JobGolden.Start;
    private const string WritesOn = "{\"ExternalWritesEnabled\":true}";
    private const string Refused = "869200000002";
    private const string Kept = "869200000001";
    // A made-up refusal that names the price alone.
    private const string PriceReason = "Girilen satış fiyatı geçersiz.";

    private sealed class ScriptedTrendyol
    {
        private readonly Dictionary<string, string> payloads = new(StringComparer.Ordinal);
        public List<string> Pushes { get; } = [];
        public string Reason { get; set; } = PriceReason;
        public bool RefusePrice { get; set; } = true;
        // Only this sale price is refused; null refuses any price of the refused barcode.
        public decimal? RefusedSalePrice { get; set; }
        // The send time the answer names (the adapter's clock); the run's clock when given.
        public GoldenClock? Clock { get; set; }

        public GoldenMarketplace Port()
        {
            var port = new GoldenMarketplace();
            port.Answers["inventory.Push"] = args =>
            {
                var json = (string)args[0]!;
                Pushes.Add(json);
                var id = $"batch-{Pushes.Count}";
                payloads[id] = json;
                return AdapterResult<RemoteOperationRef>.Success(new(id, "PRICE_INVENTORY", Clock?.GetUtcNow() ?? Start.AddMinutes(1)));
            };
            port.Answers["products.GetOperation"] = args =>
            {
                var id = (string)args[0]!;
                var lines = JsonNode.Parse(payloads[id])!["items"]!.AsArray().Select(item =>
                {
                    var barcode = item!["barcode"]!.GetValue<string>();
                    var refused = RefusePrice && barcode == Refused && item["salePrice"] is { } sale && (RefusedSalePrice is null || sale.GetValue<decimal>() == RefusedSalePrice);
                    // As TrendyolJsonMapper.Batch reads a FAILED item: the first reason as the error code, every reason kept.
                    return refused ? new RemoteOperationLine(barcode, false, null, Reason, false, Reasons: [Reason]) : new RemoteOperationLine(barcode, true, null, null, false);
                }).ToList();
                return AdapterResult<RemoteOperationStatus>.Success(new(id, "COMPLETED", lines));
            };
            return port;
        }
    }

    private async Task<(GoldenRun Run, Guid ProductId, Guid[] Variants)> SeedAsync(bool writesOn = true, params string[] policies)
    {
        var run = new GoldenRun(fixture, "TRENDYOL") { ConnectionSettingsJson = writesOn ? WritesOn : null };
        run.Settings["FeatureFlags:ExternalWrites"] = "true";
        var productId = Guid.Empty;
        var variants = new List<Guid>();
        await run.SeedAsync(db =>
        {
            var product = new Product { Id = run.NewId(), TenantId = run.Tenant.Id, Title = "Fiyat reddi ürünü", Description = "golden", Status = ProductStatus.Active, CreatedAt = Start, UpdatedAt = Start };
            productId = product.Id;
            run.Known[product.Id] = "product";
            db.Products.Add(product);
            foreach (var (sku, barcode, size) in new[] { ("PR-1", Kept, "S"), ("PR-2", Refused, "M") })
            {
                var variant = new ProductVariant { Id = run.NewId(), TenantId = run.Tenant.Id, ProductId = product.Id, Sku = sku, SkuNormalized = sku, Barcode = barcode, BarcodeNormalized = barcode, ModelCode = "PR-MODEL", OptionSignature = $"Renk=Siyah|Beden={size}", CreatedAt = Start, UpdatedAt = Start };
                run.Known[variant.Id] = $"variant-{sku}";
                variants.Add(variant.Id);
                db.ProductVariants.Add(variant);
                var inventory = new InventoryItem { Id = run.NewId(), TenantId = run.Tenant.Id, VariantId = variant.Id, LocationCode = "MAIN", OnHand = 5m, Available = 5m };
                var offer = new ChannelOffer { Id = run.NewId(), TenantId = run.Tenant.Id, ConnectionId = run.Connection.Id, VariantId = variant.Id, ListPrice = 150m, SalePrice = 120m, Currency = "TRY", VatRate = 20m, VatInclusion = "INCLUDED", RoundingMode = "HALF_EVEN", Status = "ACTIVE" };
                var link = new MarketplaceVariantLink { Id = run.NewId(), TenantId = run.Tenant.Id, ConnectionId = run.Connection.Id, VariantId = variant.Id, ExternalId = barcode, ExternalBarcode = barcode, ExternalSku = sku, Version = 1 };
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

    // A user's "Fiyat ve Stok Gönder" for the product (or one variant), built by the panel's own composer, run until it ends.
    private async Task<JobExecutionResult> SendFromPanelAsync(GoldenRun run, GoldenMarketplace port, Guid productId, string label, Guid? variantId = null) =>
        await RunToEndAsync(run, port, await QueueFromPanelAsync(run, productId, label, variantId), label);

    private async Task<Guid> QueueFromPanelAsync(GoldenRun run, Guid productId, string label, Guid? variantId = null)
    {
        var jobId = run.NewId();
        run.Known[jobId] = $"job-{label}";
        await using (var db = fixture.CreateContext(new FrozenClock(Start)))
        {
            var draft = await new PriceInventoryComposer(db).BuildAsync(run.Tenant.Id, run.Connection.Id, CancellationToken.None, variantId: variantId, productId: variantId is null ? productId : null);
            Assert.True(draft.Succeeded, draft.Error?.Code);
            var payload = JsonSerializer.Serialize(new PriceInventoryJobPayload(jobId, run.Connection.Id, "SUBMIT", draft.Value!.PayloadHash, draft.Value.PayloadJson, draft.Value.Lines, null, null, VariantId: variantId, ProductId: variantId is null ? productId : null, Manual: true));
            db.IntegrationJobs.Add(new IntegrationJob { Id = jobId, TenantId = run.Tenant.Id, ConnectionId = run.Connection.Id, JobType = MarketplaceJobTypes.PriceInventorySync, PayloadJson = payload, PayloadVersion = 1, PayloadHash = "h", JobDedupKey = $"golden-pi:{jobId:N}", EffectIdempotencyKey = $"price-inventory:{label}", Status = JobStatus.Leased, AvailableAt = Start, CorrelationId = "golden", CreatedAt = Start, Version = 1, TriggerType = "user" });
            await db.SaveChangesAsync();
        }
        return jobId;
    }

    // The job run by the queue, each time with the payload its last run left on it, until it ends (or after maxRuns).
    private async Task<JobExecutionResult> RunToEndAsync(GoldenRun run, GoldenMarketplace port, Guid jobId, string label, int maxRuns = 4)
    {
        JobExecutionResult result = null!;
        for (var index = 0; index < maxRuns; index++)
        {
            string payloadJson;
            await using (var db = fixture.CreateContext())
                payloadJson = await db.IntegrationJobs.Where(x => x.Id == jobId).Select(x => x.PayloadJson).SingleAsync();
            run.Clock.Advance(TimeSpan.FromSeconds(30));
            result = await run.RunAsync($"{label}-{index}", MarketplaceJobTypes.PriceInventorySync, port, payloadJson, jobId);
            if (result.Kind != JobCompletionKind.Retry) break;
        }
        if (result.Kind != JobCompletionKind.Retry)
        {
            await using var db = fixture.CreateContext();
            await db.IntegrationJobs.Where(x => x.Id == jobId).ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Status, result.Kind == JobCompletionKind.Succeeded ? JobStatus.Succeeded : JobStatus.ManualReview));
        }
        return result;
    }

    // A sale (or any stock change) of every variant: one unit less, a new stock projection.
    private async Task StockChangesAsync(GoldenRun run)
    {
        await using var db = fixture.CreateContext();
        await db.InventoryItems.Where(x => x.TenantId == run.Tenant.Id).ExecuteUpdateAsync(setters => setters
            .SetProperty(x => x.Available, x => x.Available - 1).SetProperty(x => x.OnHand, x => x.OnHand - 1).SetProperty(x => x.ProjectionVersion, x => x.ProjectionVersion + 1));
    }

    private async Task<OperationalIssue?> RefusalIssueAsync(GoldenRun run, Guid variantId)
    {
        await using var db = fixture.CreateContext();
        return await db.OperationalIssues.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == run.Tenant.Id && x.DedupeKey == TrendyolPriceRefusal.Key(run.Connection.Id, variantId));
    }

    private static JsonObject Item(string payload, string barcode) =>
        JsonNode.Parse(payload)!["items"]!.AsArray().Single(item => item!["barcode"]!.GetValue<string>() == barcode)!.AsObject();

    private static bool Carries(string payload, string barcode) =>
        JsonNode.Parse(payload)!["items"]!.AsArray().Any(item => item!["barcode"]!.GetValue<string>() == barcode);

    [PostgreSqlFact]
    public async Task ARefusedPriceGoesStockOnlyWithTheNextSend_AndTheOtherRowsAreUnchanged()
    {
        using var culture = JobGolden.InvariantCulture();
        var (run, productId, variants) = await SeedAsync(true, MarketplaceExternalWritePolicies.Manual);
        var trendyol = new ScriptedTrendyol();
        var port = trendyol.Port();

        await SendFromPanelAsync(run, port, productId, "first");
        var issue = await RefusalIssueAsync(run, variants[1]);
        Assert.NotNull(issue);
        Assert.Equal(IssueStatus.Open, issue!.Status);
        Assert.Equal(TrendyolPriceRefusal.Code, issue.Code);
        Assert.Contains("Stok fiyatsız gönderilmeye devam ediyor", issue.Summary, StringComparison.Ordinal);
        Assert.Contains(PriceReason, issue.Summary, StringComparison.Ordinal);
        Assert.Null(await RefusalIssueAsync(run, variants[0]));

        await StockChangesAsync(run);
        await SendFromPanelAsync(run, port, productId, "second");

        Assert.Equal(2, trendyol.Pushes.Count);
        var second = trendyol.Pushes[1];
        // The refused row carries its stock alone; the other row is sent as before the change (price with its stock).
        Assert.Equal(4, Item(second, Refused)["quantity"]!.GetValue<int>());
        Assert.Null(Item(second, Refused)["salePrice"]);
        Assert.Null(Item(second, Refused)["listPrice"]);
        Assert.Equal(4, Item(second, Kept)["quantity"]!.GetValue<int>());
        Assert.Equal(120m, Item(second, Kept)["salePrice"]!.GetValue<decimal>());
        Assert.Equal(150m, Item(second, Kept)["listPrice"]!.GetValue<decimal>());

        // The owner still sees the refused price; the stock Trendyol took is recorded, the price is not.
        Assert.Equal(IssueStatus.Open, (await RefusalIssueAsync(run, variants[1]))!.Status);
        await using (var db = fixture.CreateContext())
        {
            var offer = await db.ChannelOffers.AsNoTracking().SingleAsync(x => x.TenantId == run.Tenant.Id && x.VariantId == variants[1]);
            Assert.Equal(4m, offer.LastSentQuantity);
            Assert.Null(offer.LastSentSalePrice);
            Assert.Null(offer.LastPriceHash);
            Assert.Equal(2, offer.LastStockProjectionVersion);
        }
        await run.MatchesAsync("PriceStockJobs/trendyol-price_inventory_sync-price-refused-stock-only.golden.json");
    }

    [PostgreSqlFact]
    public async Task AChangedPriceGoesWithTheStockAgain_AndAnAcceptedPriceClearsTheMark()
    {
        var (run, productId, variants) = await SeedAsync(true, MarketplaceExternalWritePolicies.Manual);
        var trendyol = new ScriptedTrendyol { Clock = run.Clock };
        var port = trendyol.Port();
        await SendFromPanelAsync(run, port, productId, "first");
        Assert.Equal(IssueStatus.Open, (await RefusalIssueAsync(run, variants[1]))!.Status);

        // The owner edits the price (a new price version, as a panel or Özel Fiyat save makes it); Trendyol takes it.
        await using (var db = fixture.CreateContext())
            await db.ChannelOffers.Where(x => x.TenantId == run.Tenant.Id && x.VariantId == variants[1]).ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.SalePrice, 110m).SetProperty(x => x.PriceVersion, x => x.PriceVersion + 1));
        trendyol.RefusePrice = false;
        await SendFromPanelAsync(run, port, productId, "second");

        var second = trendyol.Pushes[1];
        Assert.Equal(110m, Item(second, Refused)["salePrice"]!.GetValue<decimal>());
        Assert.Equal(5, Item(second, Refused)["quantity"]!.GetValue<int>());
        var issue = await RefusalIssueAsync(run, variants[1]);
        Assert.Equal(IssueStatus.Resolved, issue!.Status);

        // Cleared: the next stock change carries the price with the stock again (rule of 18 Nov 2026).
        await StockChangesAsync(run);
        await SendFromPanelAsync(run, port, productId, "third");
        Assert.Equal(110m, Item(trendyol.Pushes[2], Refused)["salePrice"]!.GetValue<decimal>());
    }

    [PostgreSqlFact]
    public async Task ARefusedPriceTriedAgainFromThePanelForThatVariantClearsTheMarkWhenTaken()
    {
        var (run, productId, variants) = await SeedAsync(true, MarketplaceExternalWritePolicies.Manual);
        var trendyol = new ScriptedTrendyol();
        var port = trendyol.Port();
        await SendFromPanelAsync(run, port, productId, "first");
        trendyol.RefusePrice = false;
        await SendFromPanelAsync(run, port, productId, "variant", variantId: variants[1]);
        Assert.Equal(120m, Item(trendyol.Pushes[1], Refused)["salePrice"]!.GetValue<decimal>());
        Assert.Equal(IssueStatus.Resolved, (await RefusalIssueAsync(run, variants[1]))!.Status);
    }

    // Review ORTA-1: the 30-day stale pass and the retention's delete must not end an open mark.
    [PostgreSqlFact]
    public async Task AMarkOlderThanTheStaleWindowSurvivesRetention_AndTheThirdSendIsStillStockOnly()
    {
        var (run, productId, variants) = await SeedAsync(true, MarketplaceExternalWritePolicies.Manual);
        var trendyol = new ScriptedTrendyol();
        var port = trendyol.Port();
        await SendFromPanelAsync(run, port, productId, "first");
        await SendFromPanelAsync(run, port, productId, "second");
        Assert.Null(Item(trendyol.Pushes[1], Refused)["salePrice"]);

        var key = TrendyolPriceRefusal.Key(run.Connection.Id, variants[1]);
        await using (var db = fixture.CreateContext())
            await db.OperationalIssues.Where(x => x.TenantId == run.Tenant.Id && x.DedupeKey == key).ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.FirstSeenAt, Start.AddDays(-45)).SetProperty(x => x.LastSeenAt, Start.AddDays(-40)));
        await using (var db = fixture.CreateContext())
        {
            var retention = new OperationalIssueRetentionService(db, new FrozenClock(Start.AddDays(1)));
            await retention.ResolveStaleAsync(TimeSpan.FromDays(30), CancellationToken.None);
            await retention.DeleteResolvedAsync(TimeSpan.FromDays(30), CancellationToken.None);
        }
        Assert.Equal(IssueStatus.Open, (await RefusalIssueAsync(run, variants[1]))!.Status);

        await StockChangesAsync(run);
        await SendFromPanelAsync(run, port, productId, "third");
        Assert.Equal(4, Item(trendyol.Pushes[2], Refused)["quantity"]!.GetValue<int>());
        Assert.Null(Item(trendyol.Pushes[2], Refused)["salePrice"]);
    }

    // Review: the late answer of an older send (its price no longer the offer's) must not end the mark of a newer refused price.
    [PostgreSqlFact]
    public async Task TheLateAcceptanceOfAnOlderPriceDoesNotEndTheMarkOfANewerRefusedPrice()
    {
        var (run, productId, variants) = await SeedAsync(true, MarketplaceExternalWritePolicies.Manual);
        var trendyol = new ScriptedTrendyol { RefusedSalePrice = 110m, Clock = run.Clock };
        var port = trendyol.Port();
        // Send A (120) goes out and its answer is still pending.
        var older = await QueueFromPanelAsync(run, productId, "older");
        Assert.Equal(JobCompletionKind.Retry, (await RunToEndAsync(run, port, older, "older", maxRuns: 1)).Kind);
        // The owner sets 110; send B carries it and Trendyol refuses that price.
        await using (var db = fixture.CreateContext())
            await db.ChannelOffers.Where(x => x.TenantId == run.Tenant.Id && x.VariantId == variants[1]).ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.SalePrice, 110m).SetProperty(x => x.PriceVersion, x => x.PriceVersion + 1));
        await SendFromPanelAsync(run, port, productId, "newer");
        Assert.Equal(IssueStatus.Open, (await RefusalIssueAsync(run, variants[1]))!.Status);
        // A's answer arrives: 120 accepted. The refused 110 still waits.
        await RunToEndAsync(run, port, older, "older-poll");
        Assert.Equal(IssueStatus.Open, (await RefusalIssueAsync(run, variants[1]))!.Status);
        await StockChangesAsync(run);
        await SendFromPanelAsync(run, port, productId, "after");
        Assert.Null(Item(trendyol.Pushes[^1], Refused)["salePrice"]);
    }

    public static TheoryData<string> AmbiguousReasons => new()
    {
        "Stok ve fiyat bilgisi hatalı.",
        "Aynı barkod için dakikada çok fazla fiyat isteği gönderildi.",
        "Ürün bulunamadı.",
        "İşlem başarısız."
    };

    [PostgreSqlTheory]
    [MemberData(nameof(AmbiguousReasons))]
    public async Task ARefusalNotClearlyAboutThePriceKeepsTodaysBehaviour(string reason)
    {
        var (run, productId, variants) = await SeedAsync(true, MarketplaceExternalWritePolicies.Manual);
        var trendyol = new ScriptedTrendyol { Reason = reason };
        var port = trendyol.Port();
        await SendFromPanelAsync(run, port, productId, "first");
        Assert.Null(await RefusalIssueAsync(run, variants[1]));
        await using (var db = fixture.CreateContext())
            Assert.True(await db.OperationalIssues.AnyAsync(x => x.TenantId == run.Tenant.Id && x.DedupeKey.StartsWith($"price-inventory:{run.Connection.Id}:{Refused}:") && x.Status == IssueStatus.Open));

        await SendFromPanelAsync(run, port, productId, "second");
        // As before: the refused row goes again with its price and stock.
        Assert.Equal(120m, Item(trendyol.Pushes[1], Refused)["salePrice"]!.GetValue<decimal>());
        Assert.Equal(5, Item(trendyol.Pushes[1], Refused)["quantity"]!.GetValue<int>());
    }

    public static TheoryData<string, string> SwitchesOff => new()
    {
        { MarketplaceJobTypes.StockProjectionDispatch, "writes-off" }, { MarketplaceJobTypes.StockProjectionDispatch, "policies-off" },
        { MarketplaceJobTypes.StockReconciliation, "writes-off" }, { MarketplaceJobTypes.StockReconciliation, "policies-off" }
    };

    [PostgreSqlTheory]
    [MemberData(nameof(SwitchesOff))]
    public async Task WithTheAutomaticSwitchOff_ARefusedRowSendsNothing(string jobType, string scenario)
    {
        var (run, productId, variants) = await SeedAsync(true, MarketplaceExternalWritePolicies.Manual);
        var trendyol = new ScriptedTrendyol();
        var port = trendyol.Port();
        await SendFromPanelAsync(run, port, productId, "first");
        await StockChangesAsync(run);
        if (scenario == "writes-off")
        {
            await using var db = fixture.CreateContext();
            await db.PlatformConnections.Where(x => x.Id == run.Connection.Id).ExecuteUpdateAsync(setters => setters.SetProperty(x => x.SettingsJson, "{}"));
        }
        var payload = jobType == MarketplaceJobTypes.StockProjectionDispatch ? $$"""{"variantId":"{{variants[1]}}","sourceEventId":"stock:golden"}""" : "{}";
        var result = await run.RunAsync("automatic", jobType, port, payload, run.NewId());
        Assert.Equal(JobCompletionKind.Succeeded, result.Kind);
        Assert.Single(trendyol.Pushes);
        await using (var check = fixture.CreateContext())
            Assert.False(await check.IntegrationJobs.AnyAsync(x => x.TenantId == run.Tenant.Id && x.JobType == MarketplaceJobTypes.PriceInventorySync && x.TriggerType != "user"));
    }

    [PostgreSqlFact]
    public async Task OnceItsStockWent_ARefusedRowQueuesNoFurtherSendOfItsOwn_EvenWithTheAutomaticWritesOn()
    {
        var (run, productId, variants) = await SeedAsync(true, MarketplaceExternalWritePolicies.Manual, MarketplaceExternalWritePolicies.Stock, MarketplaceExternalWritePolicies.Price);
        var trendyol = new ScriptedTrendyol();
        var port = trendyol.Port();
        await SendFromPanelAsync(run, port, productId, "first");
        await SendFromPanelAsync(run, port, productId, "second");
        Assert.Equal(2, trendyol.Pushes.Count);

        // Nothing changed since: the catch-up pass (what runs on its own, after a deploy too) has nothing to send.
        var result = await run.RunAsync("reconcile", MarketplaceJobTypes.StockReconciliation, port, "{}", run.NewId());
        Assert.Equal(JobCompletionKind.Succeeded, result.Kind);
        await using var check = fixture.CreateContext();
        Assert.False(await check.IntegrationJobs.AnyAsync(x => x.TenantId == run.Tenant.Id && x.JobType == MarketplaceJobTypes.PriceInventorySync && x.TriggerType != "user"));
        Assert.Equal(2, trendyol.Pushes.Count);
        Assert.Equal(IssueStatus.Open, (await RefusalIssueAsync(run, variants[1]))!.Status);
    }

    [PostgreSqlFact]
    public async Task TheMarkLeavesNoOrphanWhenItsOfferVariantCodeOrConnectionIsGone()
    {
        var seeds = new List<(GoldenRun Run, Guid ProductId, Guid[] Variants)>();
        for (var index = 0; index < 4; index++)
        {
            var seed = await SeedAsync(true, MarketplaceExternalWritePolicies.Manual);
            await SendFromPanelAsync(seed.Run, new ScriptedTrendyol().Port(), seed.ProductId, "first");
            seeds.Add(seed);
        }
        var kept = await SeedAsync(true, MarketplaceExternalWritePolicies.Manual);
        await SendFromPanelAsync(kept.Run, new ScriptedTrendyol().Port(), kept.ProductId, "first");
        await using (var db = fixture.CreateContext())
        {
            var (offerRun, _, offerVariants) = seeds[0];
            await db.ChannelOffers.Where(x => x.TenantId == offerRun.Tenant.Id && x.VariantId == offerVariants[1]).ExecuteDeleteAsync();
            var (codeRun, _, codeVariants) = seeds[1];
            await db.MarketplaceVariantLinks.Where(x => x.TenantId == codeRun.Tenant.Id && x.VariantId == codeVariants[1]).ExecuteDeleteAsync();
            var (connectionRun, _, _) = seeds[2];
            await db.PlatformConnections.Where(x => x.Id == connectionRun.Connection.Id).ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Status, "DELETED"));
            var (variantRun, _, variantIds) = seeds[3];
            await db.ChannelOffers.Where(x => x.TenantId == variantRun.Tenant.Id && x.VariantId == variantIds[1]).ExecuteDeleteAsync();
            await db.InventoryItems.Where(x => x.TenantId == variantRun.Tenant.Id && x.VariantId == variantIds[1]).ExecuteDeleteAsync();
            await db.MarketplaceVariantLinks.Where(x => x.TenantId == variantRun.Tenant.Id && x.VariantId == variantIds[1]).ExecuteDeleteAsync();
            await db.ProductVariants.Where(x => x.TenantId == variantRun.Tenant.Id && x.Id == variantIds[1]).ExecuteDeleteAsync();
        }
        foreach (var (seedRun, _, _) in seeds.Append(kept))
        {
            await using var db = fixture.CreateContext();
            await OrphanedOperationalIssues.ResolveAsync(db, seedRun.Tenant.Id, Start, 100, 2, OrphanedOperationalIssues.MaintenanceActor, CancellationToken.None);
        }
        foreach (var (seedRun, _, seedVariants) in seeds)
            Assert.Equal(IssueStatus.Resolved, (await RefusalIssueAsync(seedRun, seedVariants[1]))!.Status);
        Assert.Equal(IssueStatus.Open, (await RefusalIssueAsync(kept.Run, kept.Variants[1]))!.Status);
    }
}

public sealed class TrendyolPriceRefusalClassificationTests
{
    [Theory]
    [InlineData("Girilen satış fiyatı geçersiz.")]
    [InlineData("salePrice must be greater than zero")]
    [InlineData("listPrice cannot be lower than salePrice")]
    [InlineData("PSF TSF'den küçük olamaz")]
    [InlineData("SATIŞ FİYATI GEÇERSİZ")]
    [InlineData("Satış fiyatı, liste fiyatından büyük olamaz")]
    public void APriceAloneIsAPriceRefusal(string reason) => Assert.True(TrendyolPriceRefusal.IsPriceRefusal([reason]));

    [Theory]
    [InlineData("Stok ve fiyat bilgisi hatalı.")]
    [InlineData("Aynı barkod için dakikada çok fazla fiyat isteği gönderildi.")]
    [InlineData("15 dakika boyunca aynı isteği tekrarlı olarak atamazsınız!")]
    [InlineData("Ürün bulunamadı.")]
    [InlineData("Barkod kayıtlı değil, fiyat güncellenemedi.")]
    [InlineData("Ürün arşivde olduğu için fiyat güncellenemez.")]
    [InlineData("Kilitli ürünün fiyatı değiştirilemez.")]
    [InlineData("quantity must be at most 20000")]
    [InlineData("İşlem başarısız.")]
    [InlineData("")]
    // Review ORTA-2: a limit, a throttle or an error is about the request, in any casing.
    [InlineData("Fiyat güncelleme sınırı aşıldı.")]
    [InlineData("Price update limit exceeded, please try later")]
    [InlineData("Too frequent price updates")]
    [InlineData("Bu ürün için çok sık fiyat güncellemesi yapılamaz.")]
    [InlineData("SİSTEM HATASI, fiyat güncellenemedi")]
    [InlineData("FİYAT GÜNCELLEME SINIRI AŞILDI")]
    public void AnythingElseIsNot(string reason) => Assert.False(TrendyolPriceRefusal.IsPriceRefusal([reason]));

    [Fact]
    public void EveryReasonMustBeAboutThePrice_AndNoReasonIsNoRefusal()
    {
        Assert.False(TrendyolPriceRefusal.IsPriceRefusal(["Girilen satış fiyatı geçersiz.", "Stok adedi geçersiz."]));
        Assert.True(TrendyolPriceRefusal.IsPriceRefusal(["Girilen satış fiyatı geçersiz.", "Liste fiyatı satış fiyatından düşük olamaz."]));
        Assert.False(TrendyolPriceRefusal.IsPriceRefusal([]));
        Assert.False(TrendyolPriceRefusal.IsPriceRefusal(null));
    }

    [Fact]
    public void TheMarkFollowsTheCodeAndThePrices()
    {
        var mark = TrendyolPriceRefusal.Fingerprint("X1", "H", 120m, 150m);
        Assert.Equal(mark, TrendyolPriceRefusal.Fingerprint(" X1 ", "H", 120.00m, 150.0m));
        Assert.NotEqual(mark, TrendyolPriceRefusal.Fingerprint("X1", "H", 110m, 150m));
        Assert.NotEqual(mark, TrendyolPriceRefusal.Fingerprint("X1", "H2", 120m, 150m));
        Assert.NotEqual(mark, TrendyolPriceRefusal.Fingerprint("X2", "H", 120m, 150m));
        Assert.Equal(mark, TrendyolPriceRefusal.FingerprintOf(TrendyolPriceRefusal.Summary("X1", 120m, 150m, "r", mark)));
        // The owner reads Turkish amounts; the fingerprint stays on invariant values whatever the thread's culture.
        Assert.Contains("satış 1.120,50 TL, liste 1.150,00 TL", TrendyolPriceRefusal.Summary("X1", 1120.5m, 1150m, "r", mark), StringComparison.Ordinal);
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("tr-TR");
            Assert.Equal(mark, TrendyolPriceRefusal.Fingerprint("X1", "H", 120m, 150m));
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = culture; }
    }
}
