using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.Hepsiburada;
using MarketplaceHub.Infrastructure.Adapters.Shopify;
using MarketplaceHub.Infrastructure.Adapters.Trendyol;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests.Contracts;

// ISLEYICI-9 stage 5 step 1 (16 Nov 2026): the read jobs whose logic was still in the processor, pinned before they move
// to their services and run handlers: the order invoice checks (Trendyol's package reads with its sweep, batches,
// claim-order hydration and slice budget; Hepsiburada's order details and missing-invoice list; Shopify's order details),
// Trendyol's cargo-info check, the listing verification with the single-code lookups it queues, and the six connection
// tests. Each run records every marketplace call (method, idempotency key, arguments), the job result, and the tenant's
// rows afterwards (cursors, issues, packages, links, jobs, capabilities, the connection). The goldens must not change
// while the code moves.
[Collection("postgres-integration")]
public sealed class ReadJobGoldenPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    private static readonly DateTimeOffset Start = JobGolden.Start;

    private sealed class Run(PostgreSqlTenantIsolationFixture fixture, string platform, string connectionStatus = "ACTIVE")
    {
        public readonly GoldenClock Clock = new(Start);
        public readonly Tenant Tenant = new() { Id = Guid.CreateVersion7(), Code = $"rjg-{Guid.NewGuid():N}"[..30], DisplayName = "read job golden", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = Start, UpdatedAt = Start, Version = 1 };
        public PlatformConnection Connection = null!;
        public readonly Dictionary<Guid, string> Known = [];
        public readonly List<object> Runs = [];
        public Dictionary<string, string?> Settings { get; } = [];
        private int seeded;

        // Seeded rows get ids in the order they are made (each a millisecond later), so a read that walks rows by id walks
        // them in the same order on every run.
        public Guid NewId() => Guid.CreateVersion7(Start.AddMilliseconds(++seeded));

        public async Task SeedAsync(Action<AppDbContext>? seed = null)
        {
            Connection = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = Tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = platform, Environment = "PRODUCTION", DisplayName = platform, ExternalStoreId = "store-golden", Status = connectionStatus, ApiVersion = "V1" };
            Known[Tenant.Id] = "tenant";
            Known[Connection.Id] = "connection";
            Known[Connection.PublicId] = "connection-public";
            await using var db = fixture.CreateContext(new FrozenClock(Start));
            db.Tenants.Add(Tenant);
            db.PlatformConnections.Add(Connection);
            seed?.Invoke(db);
            await db.SaveChangesAsync();
        }

        public MarketplaceJobProcessor Processor(AppDbContext db, GoldenMarketplace port) =>
            new(db, port, port, port, port, port, port, port, port, null!, null!, new ConfigurationBuilder().AddInMemoryCollection(Settings).Build(), Clock);

        public async Task<JobExecutionResult> RunAsync(string label, string jobType, GoldenMarketplace port, string payloadJson = "{}", Guid? jobId = null)
        {
            JobExecutionResult result;
            await using (var db = fixture.CreateContext(new FrozenClock(Start)))
                result = await Processor(db, port).ProcessAsync(Tenant.Id, Connection.Id, jobType, payloadJson, $"rjg-{label}", CancellationToken.None, jobId);
            Runs.Add(new { Run = label, JobType = jobType, Result = result, Calls = port.Calls.ToList() });
            port.Calls.Clear();
            return result;
        }

        public async Task MatchesAsync(string goldenPath)
        {
            await using var check = fixture.CreateContext();
            var tables = await JobGolden.TablesAsync(check, Tenant.Id, Known);
            JobGolden.Matches(goldenPath, new { Runs, Tables = tables }, Known);
        }
    }

    private static Order NewOrder(Run run, string externalId, DateTimeOffset orderedAt, bool returnClaim = false)
    {
        var order = new Order
        {
            Id = run.NewId(),
            TenantId = run.Tenant.Id,
            ConnectionId = run.Connection.Id,
            ExternalOrderId = externalId,
            OrderNumber = externalId,
            Currency = "TRY",
            GrossAmount = 100m,
            NetAmount = 100m,
            OrderedAt = orderedAt,
            LastRemoteModifiedAt = orderedAt,
            CustomerSnapshotJson = returnClaim ? "{\"_ravenciaReadModelSource\":\"RETURN_CLAIM\"}" : "{\"customerFirstName\":\"Golden\"}",
            ShipmentAddressSnapshotJson = "{}",
            InvoiceAddressSnapshotJson = "{}",
            DerivedStatus = "DELIVERED",
            CreatedAt = Start,
            UpdatedAt = Start,
            Version = 1
        };
        run.Known[order.Id] = $"order-{externalId}";
        return order;
    }

    private static ShipmentPackage NewPackage(Run run, Order order, string externalId, DateTimeOffset statusAt, string? cargoProvider = null, string? tracking = null)
    {
        var package = new ShipmentPackage
        {
            Id = run.NewId(),
            TenantId = run.Tenant.Id,
            ConnectionId = run.Connection.Id,
            OrderId = order.Id,
            ExternalPackageId = externalId,
            GrossAmount = 100m,
            NetAmount = 100m,
            Status = ShipmentPackageStatus.Delivered,
            RawStatus = "Delivered",
            StatusOccurredAt = statusAt,
            MarketplaceInvoiceStatus = MarketplaceInvoiceStatus.Unknown,
            CargoProviderExternalId = cargoProvider,
            CargoTrackingNumber = tracking,
            CreatedAt = Start,
            UpdatedAt = Start,
            Version = 1
        };
        run.Known[package.Id] = $"package-{externalId}";
        return package;
    }

    private static RemotePackage RemotePackageWith(string externalPackageId, string? invoiceStatus, string? number = null, string? url = null, string? cargoProvider = null, string? tracking = null) =>
        new(externalPackageId, null, "Delivered", Start.AddDays(-1), cargoProvider, tracking, [],
            Invoice: invoiceStatus is null && number is null ? null : new RemotePackageInvoiceObservation(invoiceStatus, number, url, Start.AddHours(-2), ReferencesAreAuthoritative: number is not null && url is not null));

    private static RemoteOrder RemoteOrderWith(string externalOrderId, DateTimeOffset orderedAt, params RemotePackage[] packages) =>
        new(externalOrderId, externalOrderId, orderedAt, Start.AddHours(-2), "TRY", 100m, 0m, 100m,
            "{\"customerFirstName\":\"Remote\",\"customerLastName\":\"Buyer\"}", "{\"city\":\"İstanbul\"}", "{\"city\":\"İstanbul\"}", [], packages, "{}", StoreFrontCode: "TR");

    // ---- Order invoice checks ------------------------------------------------------------------------------------------

    public static TheoryData<string> TrendyolInvoiceScenarios => new() { "answers", "refusals", "not-asked", "slice" };

    [PostgreSqlTheory]
    [MemberData(nameof(TrendyolInvoiceScenarios))]
    public async Task TrendyolInvoiceCheckAsksAndStoresTheSameThings(string scenario)
    {
        var run = new Run(fixture, "TRENDYOL");
        await run.SeedAsync();
        await using (var db = fixture.CreateContext(new FrozenClock(Start)))
        {
            if (scenario == "slice")
            {
                for (var index = 0; index < 40; index++)
                {
                    var order = NewOrder(run, $"71{index:D2}", Start.AddDays(-60));
                    db.Orders.Add(order);
                    db.ShipmentPackages.Add(NewPackage(run, order, $"81{index:D2}", Start.AddDays(-59)));
                }
            }
            else
            {
                var recent = NewOrder(run, "7001", Start.AddDays(-2));
                var older = NewOrder(run, "7002", Start.AddDays(-60));
                var claim = NewOrder(run, "7003", Start.AddDays(-3), returnClaim: true);
                db.Orders.AddRange(recent, older, claim);
                db.ShipmentPackages.AddRange(NewPackage(run, recent, "8001", Start.AddDays(-1)), NewPackage(run, older, "8002", Start.AddDays(-59)), NewPackage(run, claim, "8003", Start.AddDays(-2)));
            }
            await db.SaveChangesAsync();
        }

        var port = new GoldenMarketplace();
        switch (scenario)
        {
            case "answers":
                port.Answers["orders.GetPackages"] = args => AdapterResult<IReadOnlyList<RemoteOrder>>.Success([RemoteOrderWith("7001", Start.AddDays(-2), RemotePackageWith("8001", "Invoiced", "GLD2030000000001", "https://invoices.example.test/1"))]);
                port.Answers["orders.GetPackageLookup"] = args => (string)args[0]! switch
                {
                    "8003" => AdapterResult<RemoteOrderPackage>.Success(new("7003", RemotePackageWith("8003", "NotInvoiced"), RemoteOrderWith("7003", Start.AddDays(-3)) with { StoreFrontCode = "AZ" })),
                    var id => AdapterResult<RemoteOrderPackage>.Success(new("7002", RemotePackageWith(id, "NotInvoiced")))
                };
                port.Answers["orders.GetHinted"] = _ => AdapterResult<RemoteOrder>.Failure(GoldenMarketplace.Error(AdapterErrorClass.NotFound, "REMOTE_ORDER_NOT_FOUND", 404));
                break;
            case "refusals":
                port.Answers["orders.GetPackages"] = _ => AdapterResult<IReadOnlyList<RemoteOrder>>.Failure(GoldenMarketplace.Error(AdapterErrorClass.Remote5xx, "REMOTE_5XX", 503));
                port.Answers["orders.GetPackageLookup"] = args => (string)args[0]! switch
                {
                    "8001" => AdapterResult<RemoteOrderPackage>.Failure(GoldenMarketplace.Error(AdapterErrorClass.Remote5xx, "REMOTE_5XX", 503)),
                    _ => AdapterResult<RemoteOrderPackage>.Failure(GoldenMarketplace.Error(AdapterErrorClass.NotFound, "REMOTE_PACKAGE_NOT_FOUND", 404))
                };
                port.Answers["orders.GetHinted"] = _ => AdapterResult<RemoteOrder>.Failure(GoldenMarketplace.Error(AdapterErrorClass.TransientNetwork, "NETWORK_TIMEOUT"));
                break;
            case "not-asked":
                port.Answers["orders.GetPackages"] = _ => AdapterResult<IReadOnlyList<RemoteOrder>>.Success([]);
                port.Answers["orders.GetPackageLookup"] = _ => AdapterResult<RemoteOrderPackage>.Failure(GoldenMarketplace.Error(AdapterErrorClass.NotFound, TrendyolHttpClient.PackageNotAddressableCode, 404));
                port.Answers["orders.GetHinted"] = _ => AdapterResult<RemoteOrder>.Failure(GoldenMarketplace.Error(AdapterErrorClass.RateLimit, "RATE_LIMITED", 429));
                break;
            default:
                port.Answers["orders.GetPackageLookup"] = args => AdapterResult<RemoteOrderPackage>.Success(new("x", RemotePackageWith((string)args[0]!, "NotInvoiced")));
                break;
        }

        var first = await run.RunAsync("first", MarketplaceJobTypes.OrderInvoiceReconciliation, port, "{}", Guid.CreateVersion7());
        if (first.Continues || scenario != "slice")
        {
            run.Clock.Advance(TimeSpan.FromSeconds(5));
            await run.RunAsync("again", MarketplaceJobTypes.OrderInvoiceReconciliation, port, "{}", Guid.CreateVersion7());
        }
        await run.MatchesAsync($"ReadJobs/trendyol-order_invoice_reconciliation-{scenario}.golden.json");
    }

    public static TheoryData<string, string> OrderDetailInvoiceScenarios => new() { { "HEPSIBURADA", "answers" }, { "HEPSIBURADA", "refusals" }, { "SHOPIFY", "answers" }, { "SHOPIFY", "refusals" } };

    [PostgreSqlTheory]
    [MemberData(nameof(OrderDetailInvoiceScenarios))]
    public async Task OrderDetailInvoiceCheckAsksAndStoresTheSameThings(string platform, string scenario)
    {
        var run = new Run(fixture, platform);
        await run.SeedAsync();
        var remotes = Fixture(platform);
        // One order at a time a few milliseconds apart: the ingestion's own ids then follow the page's order, and the
        // invoice check walks orders by id.
        foreach (var remote in remotes)
        {
            await using (var db = fixture.CreateContext(new FrozenClock(Start)))
                await new MarketplaceJobProcessor(db, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, new ConfigurationBuilder().Build(), new FrozenClock(Start))
                    .UpsertOrders(run.Tenant.Id, run.Connection.Id, [remote], CancellationToken.None);
            await Task.Delay(5);
        }
        await using (var db = fixture.CreateContext())
            foreach (var order in await db.Orders.AsNoTracking().Where(x => x.TenantId == run.Tenant.Id).ToListAsync())
                run.Known[order.Id] = $"order-{order.ExternalOrderId}";

        // Hepsiburada's paid-order page carries no packages: each order gets one, as packaging would give it.
        if (platform == "HEPSIBURADA")
        {
            await using var db = fixture.CreateContext(new FrozenClock(Start));
            foreach (var order in await db.Orders.AsNoTracking().Where(x => x.TenantId == run.Tenant.Id).ToListAsync())
                db.ShipmentPackages.Add(NewPackage(run, order, $"HBP-{order.ExternalOrderId}", Start.AddDays(-1)));
            await db.SaveChangesAsync();
            remotes = [.. remotes.Select(remote => remote with { Packages = [RemotePackageWith($"HBP-{remote.ExternalOrderId}", null)] })];
        }

        var port = new GoldenMarketplace();
        var byId = remotes.ToDictionary(order => order.ExternalOrderId, StringComparer.Ordinal);
        if (scenario == "answers")
        {
            // Every other order answers with its invoice; the others say nothing about it (unknown, not "not invoiced").
            var answered = 0;
            port.Answers["orders.Get"] = args => byId.TryGetValue((string)args[0]!, out var remote)
                ? AdapterResult<RemoteOrder>.Success(answered++ % 2 == 1 ? remote : remote with { Packages = [.. remote.Packages.Select(package => package with { Invoice = new RemotePackageInvoiceObservation(platform == "HEPSIBURADA" ? "INVOICED" : "Invoiced", "GLD-1", "https://invoices.example.test/1", Start.AddHours(-1), true) })] })
                : AdapterResult<RemoteOrder>.Failure(GoldenMarketplace.Error(AdapterErrorClass.NotFound, "REMOTE_ORDER_NOT_FOUND", 404));
            var listed = remotes.SelectMany(order => order.Packages.Select(package => new RemoteMissingInvoicePackage(order.OrderNumber, package.ExternalPackageId, "Delivered"))).Skip(1).Take(1).ToList();
            listed.Add(new RemoteMissingInvoicePackage("HB-NOT-IN-PANEL", "HB-PKG-NOT-IN-PANEL", "Delivered"));
            port.Answers["missingInvoices.List"] = _ => AdapterResult<AdapterPageResult<RemoteMissingInvoicePackage>>.Success(new(listed, null, false, listed.Count));
        }
        else
        {
            var refusals = 0;
            port.Answers["orders.Get"] = _ => AdapterResult<RemoteOrder>.Failure(refusals++ % 2 == 0
                ? GoldenMarketplace.Error(AdapterErrorClass.Remote5xx, "REMOTE_5XX", 503)
                : GoldenMarketplace.Error(AdapterErrorClass.NotFound, "REMOTE_ORDER_NOT_FOUND", 404));
            port.Answers["missingInvoices.List"] = _ => AdapterResult<AdapterPageResult<RemoteMissingInvoicePackage>>.Failure(GoldenMarketplace.Error(AdapterErrorClass.Remote5xx, "REMOTE_5XX", 503));
        }

        var jobType = MarketplaceJobTypes.ForPlatform(platform, MarketplaceJobTypes.OrderInvoiceReconciliation);
        await run.RunAsync("first", jobType, port, "{\"batchSize\":3}", Guid.CreateVersion7());
        run.Clock.Advance(TimeSpan.FromMinutes(1));
        await run.RunAsync("next", jobType, port, "{\"batchSize\":3}", Guid.CreateVersion7());
        await run.MatchesAsync($"ReadJobs/{platform.ToLowerInvariant()}-order_invoice_reconciliation-{scenario}.golden.json");
    }

    private static IReadOnlyList<RemoteOrder> Fixture(string platform)
    {
        if (platform == "HEPSIBURADA")
        {
            using var document = ContractFixture.Json("Hepsiburada/orders-paid-list.json");
            var (items, _) = HepsiburadaJsonMapper.OrderPage(document.RootElement);
            return items.Select(item => HepsiburadaJsonMapper.PaidOrderLine(item, out _)).Where(order => order is not null).Select(order => order!).ToList();
        }
        using var shopify = ContractFixture.Json("Shopify/orders-page.json");
        return shopify.RootElement.GetProperty("data").GetProperty("orders").GetProperty("nodes").EnumerateArray().Select(node => ShopifyHttpClient.MapOrder(node, "https://fake-shop.example.test")).ToList();
    }

    // ---- Trendyol cargo-info check -------------------------------------------------------------------------------------

    public static TheoryData<string, string> CargoInfoScenarios => new() { { "TRENDYOL", "answers" }, { "TRENDYOL", "refusals" }, { "HEPSIBURADA", "not-applicable" } };

    [PostgreSqlTheory]
    [MemberData(nameof(CargoInfoScenarios))]
    public async Task CargoInfoCheckAsksAndStoresTheSameThings(string platform, string scenario)
    {
        var run = new Run(fixture, platform);
        run.Settings["MarketplaceSync:TrendyolCargoInfo:BatchSize"] = "2";
        await run.SeedAsync();
        await using (var db = fixture.CreateContext(new FrozenClock(Start)))
        {
            foreach (var number in new[] { "9001", "9002", "9003" })
            {
                var order = NewOrder(run, $"6{number}", Start.AddDays(-4));
                db.Orders.Add(order);
                db.ShipmentPackages.Add(NewPackage(run, order, number, Start.AddDays(-1), cargoProvider: number == "9003" ? "ARAS" : null));
            }
            await db.SaveChangesAsync();
        }
        var port = new GoldenMarketplace();
        if (scenario == "answers")
            port.Answers["orders.GetPackageLookup"] = args => AdapterResult<RemoteOrderPackage>.Success(new($"6{args[0]}", RemotePackageWith((string)args[0]!, null, cargoProvider: "YURTICI", tracking: $"TRK{args[0]}")));
        else
        {
            port.Answers["orders.GetPackageLookup"] = args => AdapterResult<RemoteOrderPackage>.Failure((string)args[0]! == "9001"
                ? GoldenMarketplace.Error(AdapterErrorClass.Remote5xx, "REMOTE_5XX", 503)
                : GoldenMarketplace.Error(AdapterErrorClass.NotFound, "REMOTE_PACKAGE_NOT_FOUND", 404));
        }
        await run.RunAsync("first", MarketplaceJobTypes.TrendyolOrderCargoInfoReconciliation, port);
        run.Clock.Advance(TimeSpan.FromMinutes(10));
        await run.RunAsync("next", MarketplaceJobTypes.TrendyolOrderCargoInfoReconciliation, port);
        await run.MatchesAsync($"ReadJobs/{platform.ToLowerInvariant()}-trendyol_order_cargo_info_reconciliation-{scenario}.golden.json");
    }

    // ---- Listing verification and lookups ------------------------------------------------------------------------------

    public static TheoryData<string, string> ListingScenarios => new()
    {
        { "TRENDYOL", "scan-and-lookup" }, { "TRENDYOL", "scan-refused" }, { "TRENDYOL", "lookup-refused" },
        { "N11", "scan-and-lookup" }, { "PTTAVM", "scan-and-lookup" }, { "SHOPIFY", "scan-and-lookup" }
    };

    [PostgreSqlTheory]
    [MemberData(nameof(ListingScenarios))]
    public async Task ListingVerificationAsksAndStoresTheSameThings(string platform, string scenario)
    {
        var run = new Run(fixture, platform);
        await run.SeedAsync(db =>
        {
            foreach (var (sku, barcode) in new[] { ("LV-SEEN", "869100000001"), ("LV-MISSED", "869100000002"), ("LV-BROKEN", "869100000003") })
            {
                var product = new Product { Id = run.NewId(), TenantId = run.Tenant.Id, Title = $"Ürün {sku}", Description = "golden", Status = ProductStatus.Active, CreatedAt = Start, UpdatedAt = Start };
                var variant = new ProductVariant { Id = run.NewId(), TenantId = run.Tenant.Id, ProductId = product.Id, Sku = sku, SkuNormalized = sku, Barcode = barcode, BarcodeNormalized = barcode, ModelCode = $"M-{barcode}", OptionSignature = sku, CreatedAt = Start, UpdatedAt = Start };
                var link = new MarketplaceVariantLink { Id = run.NewId(), TenantId = run.Tenant.Id, ConnectionId = run.Connection.Id, VariantId = variant.Id, ExternalId = barcode, ExternalBarcode = barcode, ExternalSku = barcode, Version = 1 };
                if (sku == "LV-BROKEN")
                {
                    link.HealthStatus = MarketplaceVariantLinkHealth.NotFound;
                    link.HealthReason = MarketplaceVariantLinkHealth.NotFoundReason;
                    link.HealthChangedAt = Start.AddDays(-5);
                }
                run.Known[product.Id] = $"product-{sku}";
                run.Known[variant.Id] = $"variant-{sku}";
                run.Known[link.Id] = $"link-{sku}";
                db.Products.Add(product);
                db.ProductVariants.Add(variant);
                db.MarketplaceVariantLinks.Add(link);
            }
        });

        var port = new GoldenMarketplace();
        RemoteCatalogProduct Listed(string barcode) => new($"P-{barcode}", $"M-{barcode}", "Pazaryeri", "", null, null, null, null, [],
            [new RemoteCatalogVariant(barcode, barcode, barcode, $"M-{barcode}", new Dictionary<string, string>(), false, 120m, 150m, 20m, 7m, "TRY", "{}")], "{}");
        var catalogCalls = 0;
        port.Answers["products.ListCatalog"] = args =>
        {
            var filter = (ProductReadFilter)args[1]!;
            if (filter.Barcode is null && filter.ProductMainId is null)
            {
                if (scenario == "scan-refused" && catalogCalls++ == 1) return AdapterResult<AdapterPageResult<RemoteCatalogProduct>>.Failure(GoldenMarketplace.Error(AdapterErrorClass.Remote5xx, "REMOTE_5XX", 503));
                var page = (AdapterPageRequest)args[0]!;
                return page.Cursor is null
                    ? AdapterResult<AdapterPageResult<RemoteCatalogProduct>>.Success(new([Listed("869100000001")], "2", true))
                    : AdapterResult<AdapterPageResult<RemoteCatalogProduct>>.Success(new([Listed("869100000009")], null, false));
            }
            if (scenario == "lookup-refused") return AdapterResult<AdapterPageResult<RemoteCatalogProduct>>.Failure(GoldenMarketplace.Error(AdapterErrorClass.RateLimit, "RATE_LIMITED", 429));
            var code = filter.Barcode ?? filter.ProductMainId!;
            return AdapterResult<AdapterPageResult<RemoteCatalogProduct>>.Success(new(code.EndsWith('3') ? [Listed(code)] : [], null, false));
        };
        port.Answers["products.HoldsCode"] = args => AdapterResult<bool?>.Success(((string)args[0]!).EndsWith('3') ? true : false);

        var verifyJob = Guid.CreateVersion7();
        run.Known[verifyJob] = "verify-job";
        var verified = await run.RunAsync("scan", MarketplaceJobTypes.ListingVerify, port, "{}", verifyJob);
        if (!verified.Succeeded)
        {
            run.Clock.Advance(TimeSpan.FromMinutes(2));
            await run.RunAsync("scan-again", MarketplaceJobTypes.ListingVerify, port, "{}", verifyJob);
        }
        // The lookup job the scan queued, run as the queue would (and once more, as a retry of the same job would be).
        IntegrationJob? lookup;
        await using (var db = fixture.CreateContext())
            lookup = await db.IntegrationJobs.AsNoTracking().Where(x => x.TenantId == run.Tenant.Id && x.JobType == MarketplaceJobTypes.ListingLookup).OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).FirstOrDefaultAsync();
        if (lookup is not null)
        {
            run.Known[lookup.Id] = "lookup-job";
            run.Clock.Advance(TimeSpan.FromMinutes(1));
            await run.RunAsync("lookup", lookup.JobType, port, lookup.PayloadJson, lookup.Id);
            await run.RunAsync("lookup-again", lookup.JobType, port, lookup.PayloadJson, lookup.Id);
        }
        await run.MatchesAsync($"ReadJobs/{platform.ToLowerInvariant()}-listing_verify-{scenario}.golden.json");
    }

    // ---- Connection tests ----------------------------------------------------------------------------------------------

    public static TheoryData<string, string> ConnectionScenarios()
    {
        var data = new TheoryData<string, string>();
        foreach (var platform in new[] { "TRENDYOL", "HEPSIBURADA", "N11", "PAZARAMA", "PTTAVM", "SHOPIFY" })
            foreach (var scenario in new[] { "verified", "refused", "discovery-refused", "missing-read" })
                data.Add(platform, scenario);
        return data;
    }

    [PostgreSqlTheory]
    [MemberData(nameof(ConnectionScenarios))]
    public async Task ConnectionTestAsksAndStoresTheSameThings(string platform, string scenario)
    {
        var run = new Run(fixture, platform, connectionStatus: "DRAFT");
        await run.SeedAsync();
        var port = new GoldenMarketplace();
        port.Answers["connection.Test"] = _ => scenario == "refused"
            ? AdapterResult<ConnectionIdentity>.Failure(GoldenMarketplace.Error(AdapterErrorClass.Authentication, "AUTH_REJECTED", 401))
            : AdapterResult<ConnectionIdentity>.Success(new(platform, "PRODUCTION", "store-from-marketplace", "2026-01", "scope-golden"));
        CapabilityEvidence Evidence(string code, string level) => new(code, level, "2026-01", "PRODUCTION", "store-from-marketplace", "https://docs.example.test", "1", null, null, $"golden {code} {level}", null, Start.AddMinutes(-1));
        port.Answers["connection.Discover"] = _ => scenario switch
        {
            "discovery-refused" => AdapterResult<IReadOnlyList<CapabilityEvidence>>.Failure(GoldenMarketplace.Error(AdapterErrorClass.RateLimit, "RATE_LIMITED", 429)),
            "missing-read" => AdapterResult<IReadOnlyList<CapabilityEvidence>>.Success([Evidence(MarketplaceCapabilities.ProductRead, "SUPPORTED"), Evidence(MarketplaceCapabilities.OrderRead, "UNSUPPORTED")]),
            _ => AdapterResult<IReadOnlyList<CapabilityEvidence>>.Success([Evidence(MarketplaceCapabilities.ProductRead, "SUPPORTED"), Evidence(MarketplaceCapabilities.OrderRead, "SUPPORTED")])
        };
        var jobType = MarketplaceJobTypes.ForPlatform(platform, MarketplaceJobTypes.ConnectionTest);
        await run.RunAsync("test", jobType, port, "{}", Guid.CreateVersion7());
        run.Clock.Advance(TimeSpan.FromMinutes(1));
        await run.RunAsync("test-again", jobType, port, "{}", Guid.CreateVersion7());
        await run.MatchesAsync($"ReadJobs/{platform.ToLowerInvariant()}-connection_test-{scenario}.golden.json");
    }
}
