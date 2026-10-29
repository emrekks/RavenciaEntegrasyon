using System.Text.Json;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.Pazarama;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// The nightly link verification and its single-code lookups against the real schema.
[Collection("postgres-integration")]
public sealed class ListingVerificationPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>, IAsyncLifetime
{
    private static readonly System.Collections.Concurrent.ConcurrentBag<Guid> CreatedTenants = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        var tenantIds = CreatedTenants.ToArray();
        if (tenantIds.Length == 0) return;
        await using var db = fixture.CreateContext();
        await db.JobAttempts.Where(x => tenantIds.Contains(x.TenantId)).ExecuteDeleteAsync();
        await db.IntegrationJobs.Where(x => tenantIds.Contains(x.TenantId)).ExecuteDeleteAsync();
    }

    [PostgreSqlFact]
    public async Task A_verification_run_changes_link_health_only_and_never_product_content_prices_or_stock()
    {
        var seed = await Seed("TRENDYOL");
        var port = new ScriptedProductPort { Catalog = [Remote("Pazaryeri Başlığı", "869100000001", salePrice: 999m)] };

        var result = await Verify(seed, port);

        Assert.True(result.Succeeded, result.ErrorSummary);
        await using var db = fixture.CreateContext();
        var product = await db.Products.AsNoTracking().SingleAsync(x => x.Id == seed.Seen.ProductId);
        Assert.Equal("Panel Başlığı", product.Title);
        var offer = await db.ChannelOffers.AsNoTracking().SingleAsync(x => x.ConnectionId == seed.Connection.Id && x.VariantId == seed.Seen.Id);
        Assert.Equal(120m, offer.SalePrice);
        Assert.Equal(150m, offer.ListPrice);
        Assert.Equal(5m, (await db.InventoryItems.AsNoTracking().SingleAsync(x => x.VariantId == seed.Seen.Id)).OnHand);
        Assert.False(await db.ProductImportSessions.AnyAsync(x => x.TenantId == seed.Tenant.Id));
        Assert.Equal(1, await db.Products.CountAsync(x => x.TenantId == seed.Tenant.Id && x.Id == seed.Seen.ProductId));
        var seen = await Link(db, seed.Connection.Id, seed.Seen.Id);
        Assert.Null(seen.HealthStatus);
        Assert.NotNull(seen.LastSeenAt);
        // The missed code is suspect now and goes to a single-code lookup the same night.
        var missed = await Link(db, seed.Connection.Id, seed.Missed.Id);
        Assert.Equal(MarketplaceVariantLinkHealth.Suspect, missed.HealthStatus);
        var lookup = await db.IntegrationJobs.AsNoTracking().SingleAsync(x => x.TenantId == seed.Tenant.Id && x.JobType == MarketplaceJobTypes.ListingLookup);
        Assert.Contains(seed.Missed.Id.ToString(), lookup.PayloadJson);
        // The run's state: one completed run.
        var state = ListingVerificationState.Parse(await db.SyncCursors.AsNoTracking().Where(x => x.ConnectionId == seed.Connection.Id && x.ResourceType == ListingVerificationPolicy.StateCursorResource).Select(x => x.OpaqueCursor).SingleAsync());
        Assert.Equal(1, state.CompletedRuns);
        Assert.NotNull(state.LastCompletedAt);
    }

    [PostgreSqlFact]
    public async Task A_well_formed_answer_without_the_code_marks_it_not_found_at_once_and_leaves_one_summary()
    {
        var seed = await Seed("TRENDYOL");
        var port = new ScriptedProductPort { Catalog = [Remote("Ürün", "869100000001")] };
        Assert.True((await Verify(seed, port)).Succeeded);

        var result = await RunLookupJob(seed, port);

        Assert.True(result.Succeeded, result.ErrorSummary);
        await using var db = fixture.CreateContext();
        var missed = await Link(db, seed.Connection.Id, seed.Missed.Id);
        Assert.Equal(MarketplaceVariantLinkHealth.NotFound, missed.HealthStatus);
        Assert.Equal(MarketplaceListingLookupOutcome.NotFound, missed.LastLookupOutcome);
        Assert.Contains(port.LookupFilters, filter => filter.Barcode == "869100000002");
        var issue = await db.OperationalIssues.AsNoTracking().SingleAsync(x => x.TenantId == seed.Tenant.Id && x.Code == "LISTING_NOT_FOUND");
        Assert.Equal("Trendyol: 1 ürün pazaryerinde bulunamadı.", issue.Summary);
    }

    [PostgreSqlFact]
    public async Task A_lookup_that_fails_leaves_the_code_suspect_and_one_that_finds_it_heals_it()
    {
        var failing = await Seed("TRENDYOL");
        var failingPort = new ScriptedProductPort { Catalog = [Remote("Ürün", "869100000001")], LookupError = new AdapterError(AdapterErrorClass.Remote5xx, "TRENDYOL_502", "Bad gateway", 502, null, null) };
        Assert.True((await Verify(failing, failingPort)).Succeeded);
        Assert.True((await RunLookupJob(failing, failingPort)).Succeeded);

        var finding = await Seed("TRENDYOL");
        var findingPort = new ScriptedProductPort { Catalog = [Remote("Ürün", "869100000001")], LookupAnswer = [Remote("Ürün", "869100000002")] };
        Assert.True((await Verify(finding, findingPort)).Succeeded);
        Assert.True((await RunLookupJob(finding, findingPort)).Succeeded);

        await using var db = fixture.CreateContext();
        var unknown = await Link(db, failing.Connection.Id, failing.Missed.Id);
        Assert.Equal(MarketplaceVariantLinkHealth.Suspect, unknown.HealthStatus);
        Assert.Equal(MarketplaceListingLookupOutcome.Unknown, unknown.LastLookupOutcome);
        Assert.False(await db.OperationalIssues.AnyAsync(x => x.TenantId == failing.Tenant.Id && x.Code == "LISTING_NOT_FOUND"));
        var healed = await Link(db, finding.Connection.Id, finding.Missed.Id);
        Assert.Null(healed.HealthStatus);
        Assert.Equal(MarketplaceListingLookupOutcome.Found, healed.LastLookupOutcome);
    }

    [PostgreSqlFact]
    public async Task A_suspect_code_missed_again_after_an_undecided_lookup_breaks_on_the_second_scan()
    {
        var seed = await Seed("TRENDYOL");
        var port = new ScriptedProductPort { Catalog = [Remote("Ürün", "869100000001")], LookupError = new AdapterError(AdapterErrorClass.TransientNetwork, "TIMEOUT", "timeout", null, null, null) };
        Assert.True((await Verify(seed, port)).Succeeded);
        Assert.True((await RunLookupJob(seed, port)).Succeeded);

        Assert.True((await Verify(seed, port)).Succeeded);

        await using var db = fixture.CreateContext();
        Assert.Equal(MarketplaceVariantLinkHealth.NotFound, (await Link(db, seed.Connection.Id, seed.Missed.Id)).HealthStatus);
        Assert.True(await db.OperationalIssues.AnyAsync(x => x.TenantId == seed.Tenant.Id && x.Code == "LISTING_NOT_FOUND"));
    }

    [PostgreSqlFact]
    public async Task A_flat_catalog_only_records_its_first_two_runs_and_enforces_from_the_third()
    {
        var seed = await Seed("N11");
        var port = new ScriptedProductPort { Catalog = [Remote("Ürün", "869100000001")] };

        Assert.True((await Verify(seed, port)).Succeeded);
        Assert.True((await RunLookupJob(seed, port)).Succeeded);
        await using (var db = fixture.CreateContext())
        {
            var observed = await Link(db, seed.Connection.Id, seed.Missed.Id);
            Assert.Null(observed.HealthStatus);
            Assert.Equal(MarketplaceListingLookupOutcome.NotFound, observed.LastLookupOutcome);
            var issue = await db.OperationalIssues.AsNoTracking().SingleAsync(x => x.TenantId == seed.Tenant.Id && x.Code == "LISTING_VERIFY_OBSERVE");
            Assert.Contains("Hiçbir durum değiştirilmedi", issue.Summary);
            Assert.Contains("1 kod bulunamadı", issue.Summary);
            Assert.False(await db.OperationalIssues.AnyAsync(x => x.TenantId == seed.Tenant.Id && x.Code == "LISTING_NOT_FOUND"));
        }

        Assert.True((await Verify(seed, port)).Succeeded);
        await using (var db = fixture.CreateContext())
            Assert.Null((await Link(db, seed.Connection.Id, seed.Missed.Id)).HealthStatus);

        Assert.True((await Verify(seed, port)).Succeeded);
        await using (var db = fixture.CreateContext())
            Assert.Equal(MarketplaceVariantLinkHealth.Suspect, (await Link(db, seed.Connection.Id, seed.Missed.Id)).HealthStatus);
    }

    [PostgreSqlFact]
    public async Task Archived_products_and_publications_on_their_way_are_never_marked_missing()
    {
        var seed = await Seed("TRENDYOL", missedArchived: true);
        var inFlight = NewVariant(seed.Tenant, "LV-FLIGHT", "869100000003");
        await using (var db = fixture.CreateContext())
        {
            db.Products.Add(inFlight.Product);
            db.ProductVariants.Add(inFlight.Variant);
            db.MarketplaceVariantLinks.Add(NewLink(seed.Tenant, seed.Connection, inFlight.Variant, "869100000003"));
            db.MarketplaceListingStates.Add(new MarketplaceListingState { Id = Guid.CreateVersion7(), TenantId = seed.Tenant.Id, ConnectionId = seed.Connection.Id, VariantId = inFlight.Variant.Id, DesiredStatus = "LIVE", ActualStatus = "APPROVAL_PENDING" });
            await db.SaveChangesAsync();
        }
        var port = new ScriptedProductPort { Catalog = [Remote("Ürün", "869100000001")] };

        Assert.True((await Verify(seed, port)).Succeeded);

        await using var check = fixture.CreateContext();
        Assert.Null((await Link(check, seed.Connection.Id, seed.Missed.Id)).HealthStatus);
        Assert.Null((await Link(check, seed.Connection.Id, inFlight.Variant.Id)).HealthStatus);
        Assert.False(await check.IntegrationJobs.AnyAsync(x => x.TenantId == seed.Tenant.Id && x.JobType == MarketplaceJobTypes.ListingLookup));
    }

    [PostgreSqlFact]
    public async Task A_short_scan_marks_nothing_and_asks_nothing()
    {
        var seed = await Seed("TRENDYOL");
        await using (var db = fixture.CreateContext())
        {
            for (var i = 0; i < 3; i++)
            {
                var extra = NewVariant(seed.Tenant, $"LV-X{i}", $"86910000009{i}");
                db.Products.Add(extra.Product);
                db.ProductVariants.Add(extra.Variant);
                db.MarketplaceVariantLinks.Add(NewLink(seed.Tenant, seed.Connection, extra.Variant, $"86910000009{i}"));
            }
            await db.SaveChangesAsync();
        }
        var port = new ScriptedProductPort { Catalog = [Remote("Ürün", "869100000001")] };

        Assert.True((await Verify(seed, port)).Succeeded);

        await using var check = fixture.CreateContext();
        Assert.Null((await Link(check, seed.Connection.Id, seed.Missed.Id)).HealthStatus);
        Assert.True(await check.OperationalIssues.AnyAsync(x => x.TenantId == seed.Tenant.Id && x.Code == "VARIANT_LINK_SCAN_SHORT"));
        Assert.False(await check.IntegrationJobs.AnyAsync(x => x.TenantId == seed.Tenant.Id && x.JobType == MarketplaceJobTypes.ListingLookup));
    }

    [PostgreSqlFact]
    public async Task A_pazarama_stock_line_refused_for_an_unknown_barcode_makes_the_link_suspect_and_asks_about_it()
    {
        var seed = await Seed("PAZARAMA");
        var barcode = "869100000002";
        long projection;
        long priceVersion;
        await using (var db = fixture.CreateContext())
        {
            projection = (await db.InventoryItems.SingleAsync(x => x.VariantId == seed.Missed.Id)).ProjectionVersion;
            priceVersion = (await db.ChannelOffers.SingleAsync(x => x.ConnectionId == seed.Connection.Id && x.VariantId == seed.Missed.Id)).PriceVersion;
        }
        await using var setup = fixture.CreateContext();
        var offer = await setup.ChannelOffers.AsNoTracking().SingleAsync(x => x.ConnectionId == seed.Connection.Id && x.VariantId == seed.Missed.Id);
        var line = new PriceInventoryPushLine(seed.Missed.Id, offer.Id, barcode, 5m, 150m, 120m, "TRY", projection, priceVersion, "hash");
        var body = PazaramaPriceInventoryRules.Payload([(line, false, true)]);
        var jobId = Guid.CreateVersion7();
        var payload = new PriceInventoryJobPayload(jobId, seed.Connection.Id, "POLL", "hash", body, [line], "s:id-1:1:0", DateTimeOffset.UtcNow.AddMinutes(-1));
        var payloadJson = JsonSerializer.Serialize(payload);
        setup.IntegrationJobs.Add(new IntegrationJob { Id = jobId, TenantId = seed.Tenant.Id, ConnectionId = seed.Connection.Id, JobType = MarketplaceJobTypes.PriceInventorySync, PayloadJson = payloadJson, PayloadVersion = 1, PayloadHash = "h", JobDedupKey = $"pi:{jobId:N}", EffectIdempotencyKey = $"pi:{jobId:N}", Status = JobStatus.Leased, AvailableAt = DateTimeOffset.UtcNow, CorrelationId = "pi", CreatedAt = DateTimeOffset.UtcNow, Version = 1 });
        await setup.SaveChangesAsync();
        var port = new ScriptedProductPort
        {
            Operation = new RemoteOperationStatus("s:id-1:1:0", "COMPLETED", [new RemoteOperationLine(barcode, false, null, "PAZARAMA_STOCK_REJECTED", false, "Ürün bulunamadı")])
        };

        await using var db2 = fixture.CreateContext();
        var result = await Processor(db2, port).ProcessAsync(seed.Tenant.Id, seed.Connection.Id, MarketplaceJobTypes.PriceInventorySync, payloadJson, "pi", CancellationToken.None, jobId);

        Assert.False(result.Succeeded);
        await using var check = fixture.CreateContext();
        Assert.Equal(MarketplaceVariantLinkHealth.Suspect, (await Link(check, seed.Connection.Id, seed.Missed.Id)).HealthStatus);
        var lookup = await check.IntegrationJobs.AsNoTracking().SingleAsync(x => x.TenantId == seed.Tenant.Id && x.JobType == MarketplaceJobTypes.ListingLookup);
        Assert.StartsWith($"listing-lookup:{seed.Connection.Id:N}:push:{seed.Missed.Id:N}:", lookup.JobDedupKey);
    }

    [PostgreSqlFact]
    public async Task A_code_the_marketplace_no_longer_knows_gets_no_price_or_stock_even_for_the_variant_alone()
    {
        var seed = await Seed("N11");
        await using (var db = fixture.CreateContext())
            await db.MarketplaceVariantLinks.Where(x => x.ConnectionId == seed.Connection.Id && x.VariantId == seed.Missed.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.HealthStatus, MarketplaceVariantLinkHealth.NotFound));

        await using var check = fixture.CreateContext();
        var draft = await new PriceInventoryComposer(check).BuildAsync(seed.Tenant.Id, seed.Connection.Id, CancellationToken.None, seed.Missed.Id);

        Assert.False(draft.Succeeded);
        Assert.Equal("NO_EXTERNAL_CHANGES", draft.Error!.Code);
        var bulk = await new PriceInventoryComposer(check).BuildAsync(seed.Tenant.Id, seed.Connection.Id, CancellationToken.None);
        Assert.True(bulk.Succeeded);
        Assert.DoesNotContain(bulk.Value!.Lines, x => x.VariantId == seed.Missed.Id);
        Assert.Contains(bulk.Value.Lines, x => x.VariantId == seed.Seen.Id);
    }

    private sealed record Seeded(Tenant Tenant, PlatformConnection Connection, ProductVariant Seen, ProductVariant Missed);

    // A connection with two linked variants of two products: one the catalog lists (…001), one it does not (…002).
    private async Task<Seeded> Seed(string platform, bool missedArchived = false)
    {
        var tenant = NewTenant($"listing-verify-{platform.ToLowerInvariant()}");
        var connection = new PlatformConnection
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            PublicId = Guid.CreateVersion7(),
            PlatformCode = platform,
            Environment = "PRODUCTION",
            DisplayName = $"{platform} doğrulama",
            ExternalStoreId = $"store-{Guid.NewGuid():N}",
            Status = "ACTIVE",
            ApiVersion = "V1",
            SettingsJson = "{\"ExternalWritesEnabled\":true}"
        };
        var seen = NewVariant(tenant, "LV-SEEN", "869100000001");
        var missed = NewVariant(tenant, "LV-MISSED", "869100000002");
        if (missedArchived) missed.Product.Status = ProductStatus.Archived;
        seen.Product.Title = "Panel Başlığı";
        await using var db = fixture.CreateContext();
        db.Tenants.Add(tenant);
        db.PlatformConnections.Add(connection);
        db.Products.AddRange(seen.Product, missed.Product);
        db.ProductVariants.AddRange(seen.Variant, missed.Variant);
        foreach (var variant in new[] { seen.Variant, missed.Variant })
        {
            db.InventoryItems.Add(new InventoryItem { Id = Guid.CreateVersion7(), TenantId = tenant.Id, VariantId = variant.Id, LocationCode = "MAIN", OnHand = 5m, Available = 5m });
            db.ChannelOffers.Add(new ChannelOffer { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, VariantId = variant.Id, ListPrice = 150m, SalePrice = 120m, Currency = "TRY", VatRate = 20m, VatInclusion = "INCLUDED", RoundingMode = "HALF_EVEN", Status = "ACTIVE" });
            db.MarketplaceVariantLinks.Add(NewLink(tenant, connection, variant, variant.Barcode!));
        }
        await db.SaveChangesAsync();
        return new(tenant, connection, seen.Variant, missed.Variant);
    }

    private static MarketplaceVariantLink NewLink(Tenant tenant, PlatformConnection connection, ProductVariant variant, string code) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenant.Id,
        ConnectionId = connection.Id,
        VariantId = variant.Id,
        ExternalId = code,
        ExternalBarcode = code,
        ExternalSku = code,
        Version = 1
    };

    private async Task<JobExecutionResult> Verify(Seeded seed, ScriptedProductPort port)
    {
        await using var db = fixture.CreateContext();
        var jobId = Guid.CreateVersion7();
        return await Processor(db, port).ProcessAsync(seed.Tenant.Id, seed.Connection.Id, MarketplaceJobTypes.ListingVerify, JsonSerializer.Serialize(new { connectionId = seed.Connection.Id }), $"verify-{jobId:N}", CancellationToken.None, jobId);
    }

    private async Task<JobExecutionResult> RunLookupJob(Seeded seed, ScriptedProductPort port)
    {
        await using var db = fixture.CreateContext();
        var job = await db.IntegrationJobs.AsNoTracking().Where(x => x.TenantId == seed.Tenant.Id && x.JobType == MarketplaceJobTypes.ListingLookup && x.Status == JobStatus.Pending).OrderByDescending(x => x.CreatedAt).FirstAsync();
        var result = await Processor(db, port).ProcessAsync(seed.Tenant.Id, seed.Connection.Id, job.JobType, job.PayloadJson, job.CorrelationId, CancellationToken.None, job.Id);
        await db.IntegrationJobs.Where(x => x.Id == job.Id).ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Status, JobStatus.Succeeded));
        return result;
    }

    private MarketplaceJobProcessor Processor(AppDbContext db, ScriptedProductPort port) =>
        new(db, null!, null!, port, null!, null!, null!, null!, null!, null!, null!, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["FeatureFlags:ExternalWrites"] = "true" }).Build(), fixture.TimeProvider);

    private static Task<MarketplaceVariantLink> Link(AppDbContext db, Guid connectionId, Guid variantId) =>
        db.MarketplaceVariantLinks.AsNoTracking().SingleAsync(x => x.ConnectionId == connectionId && x.VariantId == variantId);

    private static RemoteCatalogProduct Remote(string title, string barcode, decimal salePrice = 120m) =>
        new($"P-{barcode}", $"M-{barcode}", title, "Pazaryeri açıklaması", null, null, null, null, [],
            [new RemoteCatalogVariant(barcode, barcode, barcode, $"M-{barcode}", new Dictionary<string, string>(), false, salePrice, salePrice, 20m, 77m, "TRY", "{}")], "{}");

    private static Tenant NewTenant(string code)
    {
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"{code}-{Guid.NewGuid():N}", DisplayName = code, Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, Version = 1 };
        CreatedTenants.Add(tenant.Id);
        return tenant;
    }

    private static (Product Product, ProductVariant Variant) NewVariant(Tenant tenant, string sku, string barcode)
    {
        var now = DateTimeOffset.UtcNow;
        var product = new Product { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Title = $"Ürün {sku}", Description = "Doğrulama testi", Status = ProductStatus.Active, CreatedAt = now, UpdatedAt = now };
        var variant = new ProductVariant { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProductId = product.Id, Sku = sku, SkuNormalized = sku.ToUpperInvariant(), Barcode = barcode, BarcodeNormalized = barcode, OptionSignature = sku, CreatedAt = now, UpdatedAt = now };
        return (product, variant);
    }

    // The marketplace: a whole-catalog read (no code filter) answers Catalog; a filtered lookup answers LookupAnswer or
    // LookupError; a price/stock poll answers Operation.
    private sealed class ScriptedProductPort : IProductPort
    {
        public IReadOnlyList<RemoteCatalogProduct> Catalog { get; init; } = [];
        public IReadOnlyList<RemoteCatalogProduct> LookupAnswer { get; init; } = [];
        public AdapterError? LookupError { get; init; }
        public RemoteOperationStatus? Operation { get; init; }
        public List<ProductReadFilter> LookupFilters { get; } = [];

        public Task<AdapterResult<AdapterPageResult<RemoteCatalogProduct>>> ListCatalogAsync(AdapterContext context, AdapterPageRequest page, ProductReadFilter filter, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(filter.Barcode) && string.IsNullOrWhiteSpace(filter.ProductMainId))
                return Task.FromResult(AdapterResult<AdapterPageResult<RemoteCatalogProduct>>.Success(new(Catalog, null, false, Catalog.Count)));
            lock (LookupFilters) LookupFilters.Add(filter);
            return Task.FromResult(LookupError is { } error
                ? AdapterResult<AdapterPageResult<RemoteCatalogProduct>>.Failure(error)
                : AdapterResult<AdapterPageResult<RemoteCatalogProduct>>.Success(new(LookupAnswer, null, false, LookupAnswer.Count)));
        }

        public Task<AdapterResult<RemoteOperationStatus>> GetOperationAsync(AdapterContext context, string externalOperationId, CancellationToken cancellationToken) =>
            Task.FromResult(AdapterResult<RemoteOperationStatus>.Success(Operation!));

        public Task<AdapterResult<AdapterPageResult<RemoteProduct>>> ListAsync(AdapterContext context, AdapterPageRequest page, ProductReadFilter filter, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<RemoteOperationRef>> CreateAsync(AdapterContext context, ProductPublication publication, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<RemoteOperationRef>> UpdateUnapprovedAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<RemoteOperationRef>> UpdateApprovedContentAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<RemoteOperationRef>> UpdateApprovedVariantsAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<RemoteOperationRef>> UpdateApprovedDeliveryAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<RemotePublicationStatus>> GetPublicationStatusAsync(AdapterContext context, string barcode, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<RemoteOperationRef>> ArchiveAsync(AdapterContext context, string payloadJson, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
