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
        await db.ConnectionSyncPolicies.Where(x => tenantIds.Contains(x.TenantId)).ExecuteDeleteAsync();
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

    // T2-D30: the scan records what the marketplace says about the sale of each code it sees, beside the health; an
    // answer that does not say leaves it unknown, and the publication state is never touched.
    [PostgreSqlFact]
    public async Task A_verification_run_records_the_sale_state_the_marketplace_gives_and_unknown_when_it_does_not_say()
    {
        var seed = await Seed("TRENDYOL");
        var closed = Remote("Ürün", "869100000001");
        closed = closed with { Variants = [closed.Variants[0] with { SaleState = ListingSaleState.OffSale, SaleStateReason = ListingSaleState.BySeller }] };
        var port = new ScriptedProductPort { Catalog = [closed] };

        Assert.True((await Verify(seed, port)).Succeeded);

        await using (var db = fixture.CreateContext())
        {
            var seen = await Link(db, seed.Connection.Id, seed.Seen.Id);
            Assert.Equal((ListingSaleState.OffSale, ListingSaleState.BySeller), (seen.SaleState, seen.SaleStateReason));
            Assert.NotNull(seen.SaleStateReadAt);
            // A code the scan did not see keeps the unknown it had.
            Assert.Null((await Link(db, seed.Connection.Id, seed.Missed.Id)).SaleState);
            Assert.False(await db.MarketplaceListingStates.AnyAsync(x => x.ConnectionId == seed.Connection.Id && x.ActualStatus == ListingSaleState.OffSale));
        }

        // The next answer has no sale flag: the state is unknown again, never guessed from the last one.
        Assert.True((await Verify(seed, new ScriptedProductPort { Catalog = [Remote("Ürün", "869100000001")] })).Succeeded);
        await using var after = fixture.CreateContext();
        var again = await Link(after, seed.Connection.Id, seed.Seen.Id);
        Assert.Equal(((string?)null, (string?)null, (DateTimeOffset?)null), (again.SaleState, again.SaleStateReason, again.SaleStateReadAt));
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

    [PostgreSqlFact]
    public async Task A_not_found_code_is_asked_again_each_night_and_heals_when_the_marketplace_lists_it_again()
    {
        var seed = await Seed("TRENDYOL");
        await using (var db = fixture.CreateContext())
            await db.MarketplaceVariantLinks.Where(x => x.ConnectionId == seed.Connection.Id && x.VariantId == seed.Missed.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.HealthStatus, MarketplaceVariantLinkHealth.NotFound).SetProperty(x => x.HealthChangedAt, DateTimeOffset.UtcNow.AddDays(-2)));
        var port = new ScriptedProductPort { Catalog = [Remote("Ürün", "869100000001")], LookupAnswer = [Remote("Ürün", "869100000002")] };

        Assert.True((await Verify(seed, port)).Succeeded);
        Assert.True((await RunLookupJob(seed, port)).Succeeded);

        await using var check = fixture.CreateContext();
        var healed = await Link(check, seed.Connection.Id, seed.Missed.Id);
        Assert.Null(healed.HealthStatus);
        Assert.Equal(MarketplaceListingLookupOutcome.Found, healed.LastLookupOutcome);
    }

    [PostgreSqlFact]
    public async Task A_ptt_avm_code_breaks_only_after_two_nights_of_not_held_answers()
    {
        var seed = await Seed("PTTAVM");
        await using (var db = fixture.CreateContext())
        {
            db.SyncCursors.Add(new SyncCursor { Id = Guid.CreateVersion7(), TenantId = seed.Tenant.Id, ConnectionId = seed.Connection.Id, ResourceType = ListingVerificationPolicy.StateCursorResource, OpaqueCursor = new ListingVerificationState(CompletedRuns: 2).ToJson(), Version = 1 });
            await db.SaveChangesAsync();
        }
        var port = new ScriptedProductPort { Catalog = [Remote("Ürün", "869100000001")], Held = false };

        Assert.True((await Verify(seed, port)).Succeeded);
        Assert.True((await RunLookupJob(seed, port)).Succeeded);
        await using (var db = fixture.CreateContext())
        {
            var first = await Link(db, seed.Connection.Id, seed.Missed.Id);
            Assert.Equal(MarketplaceVariantLinkHealth.Suspect, first.HealthStatus);
            Assert.Equal(MarketplaceListingLookupOutcome.NotFound, first.LastLookupOutcome);
            // The next night.
            var night = DateTimeOffset.UtcNow.AddHours(-21);
            await db.MarketplaceVariantLinks.Where(x => x.Id == first.Id).ExecuteUpdateAsync(setters => setters.SetProperty(x => x.LastLookupAt, night).SetProperty(x => x.HealthChangedAt, night));
        }

        Assert.True((await Verify(seed, port)).Succeeded);
        Assert.True((await RunLookupJob(seed, port)).Succeeded);

        await using var check = fixture.CreateContext();
        Assert.Equal(MarketplaceVariantLinkHealth.NotFound, (await Link(check, seed.Connection.Id, seed.Missed.Id)).HealthStatus);
        Assert.True(await check.OperationalIssues.AnyAsync(x => x.TenantId == seed.Tenant.Id && x.Code == "LISTING_NOT_FOUND"));
    }

    [PostgreSqlFact]
    public async Task A_lookup_answer_older_than_a_later_sighting_does_not_break_the_code()
    {
        var seed = await Seed("TRENDYOL");
        var port = new ScriptedProductPort { Catalog = [Remote("Ürün", "869100000001")] };
        Assert.True((await Verify(seed, port)).Succeeded);
        await using (var db = fixture.CreateContext())
            await db.MarketplaceVariantLinks.Where(x => x.ConnectionId == seed.Connection.Id && x.VariantId == seed.Missed.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.LastSeenAt, DateTimeOffset.UtcNow.AddMinutes(5)));

        Assert.True((await RunLookupJob(seed, port)).Succeeded);

        await using var check = fixture.CreateContext();
        Assert.Equal(MarketplaceVariantLinkHealth.Suspect, (await Link(check, seed.Connection.Id, seed.Missed.Id)).HealthStatus);
        Assert.False(await check.OperationalIssues.AnyAsync(x => x.TenantId == seed.Tenant.Id && x.Code == "LISTING_NOT_FOUND"));
    }

    [PostgreSqlFact]
    public async Task A_refused_code_on_an_observing_connection_is_only_recorded()
    {
        var seed = await Seed("PAZARAMA");
        await using (var db = fixture.CreateContext())
            await Processor(db, new ScriptedProductPort()).QueueListingLookupsForRefusedCodes(seed.Tenant.Id, seed.Connection.Id, [seed.Missed.Id], CancellationToken.None);
        var port = new ScriptedProductPort();

        Assert.True((await RunLookupJob(seed, port)).Succeeded);

        await using var check = fixture.CreateContext();
        var job = await check.IntegrationJobs.AsNoTracking().SingleAsync(x => x.TenantId == seed.Tenant.Id && x.JobType == MarketplaceJobTypes.ListingLookup);
        Assert.Contains("\"ObserveOnly\":true", job.PayloadJson);
        var link = await Link(check, seed.Connection.Id, seed.Missed.Id);
        Assert.Null(link.HealthStatus);
        Assert.Equal(MarketplaceListingLookupOutcome.NotFound, link.LastLookupOutcome);
    }

    [PostgreSqlFact]
    public async Task A_shopify_import_gives_a_publication_placeholder_link_the_variant_id_it_reads()
    {
        var seed = await Seed("SHOPIFY");
        await using (var db = fixture.CreateContext())
        {
            await db.MarketplaceVariantLinks.Where(x => x.ConnectionId == seed.Connection.Id && x.VariantId == seed.Seen.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.ExternalId, "listing:LV-SEEN").SetProperty(x => x.ExternalSku, "LV-SEEN"));
            db.MarketplaceProductLinks.Add(new MarketplaceProductLink { Id = Guid.CreateVersion7(), TenantId = seed.Tenant.Id, ConnectionId = seed.Connection.Id, ProductId = seed.Seen.ProductId, ExternalId = "P-869100000001", SyncStatus = "SYNCED", Version = 1 });
            await db.SaveChangesAsync();
        }
        var remote = new RemoteCatalogProduct("P-869100000001", null, "Ürün", "", null, null, null, null, [],
            [new RemoteCatalogVariant("44791234567890", "LV-SEEN", "869100000001", null, new Dictionary<string, string>(), false, 120m, 150m, 20m, 5m, "TRY", "{}")], "{}");
        var port = new ScriptedProductPort { Catalog = [remote] };

        await using (var db = fixture.CreateContext())
        {
            var result = await Processor(db, port).ProcessAsync(seed.Tenant.Id, seed.Connection.Id, MarketplaceJobTypes.ShopifyProductSync, "{\"full\":true,\"updateExistingProducts\":false}", "shopify-placeholder", CancellationToken.None);
            Assert.True(result.Succeeded, result.ErrorSummary);
        }

        await using var check = fixture.CreateContext();
        var link = await Link(check, seed.Connection.Id, seed.Seen.Id);
        Assert.Equal("44791234567890", link.ExternalId);
        Assert.False(await check.OperationalIssues.AnyAsync(x => x.TenantId == seed.Tenant.Id && x.Code == "PRODUCT_VARIANT_LINK_CONFLICT"));
    }

    [PostgreSqlFact]
    public async Task A_connection_is_queued_at_most_three_times_a_cycle_and_a_blocked_run_ends_the_cycle()
    {
        var seed = await Seed("TRENDYOL");
        var cycleStart = ListingVerificationPolicy.CycleStart(DateTimeOffset.UtcNow);
        async Task<(int Queued, bool Blocked)> Attempts()
        {
            await using var db = fixture.CreateContext();
            return await new ScheduledJobProducer(db, fixture.TimeProvider, new ConfigurationBuilder().Build()).ListingVerifyCycleAttemptsAsync(seed.Tenant.Id, seed.Connection.Id, cycleStart, CancellationToken.None);
        }
        async Task AddRun(JobStatus status, DateTimeOffset createdAt)
        {
            await using var db = fixture.CreateContext();
            var id = Guid.CreateVersion7();
            db.IntegrationJobs.Add(new IntegrationJob { Id = id, TenantId = seed.Tenant.Id, ConnectionId = seed.Connection.Id, JobType = MarketplaceJobTypes.ListingVerify, PayloadJson = "{}", PayloadVersion = 1, PayloadHash = "h", JobDedupKey = $"scheduled:listing-verify:{id:N}", EffectIdempotencyKey = $"lv:{id:N}", Status = status, AvailableAt = createdAt, CorrelationId = "lv", CreatedAt = createdAt, Version = 1 });
            await db.SaveChangesAsync();
        }

        await AddRun(JobStatus.Dead, cycleStart.AddMinutes(-30));
        await AddRun(JobStatus.Dead, cycleStart.AddMinutes(10));
        await AddRun(JobStatus.Dead, cycleStart.AddMinutes(80));
        var twoFailed = await Attempts();
        Assert.Equal((2, false), twoFailed);
        Assert.True(ListingVerificationPolicy.MayQueueAgain(twoFailed.Queued, twoFailed.Blocked));

        await AddRun(JobStatus.Dead, cycleStart.AddMinutes(150));
        var threeFailed = await Attempts();
        Assert.False(ListingVerificationPolicy.MayQueueAgain(threeFailed.Queued, threeFailed.Blocked));

        var blockedSeed = await Seed("TRENDYOL");
        await using (var db = fixture.CreateContext())
        {
            var id = Guid.CreateVersion7();
            db.IntegrationJobs.Add(new IntegrationJob { Id = id, TenantId = blockedSeed.Tenant.Id, ConnectionId = blockedSeed.Connection.Id, JobType = MarketplaceJobTypes.ListingVerify, PayloadJson = "{}", PayloadVersion = 1, PayloadHash = "h", JobDedupKey = $"scheduled:listing-verify:{id:N}", EffectIdempotencyKey = $"lv:{id:N}", Status = JobStatus.Blocked, AvailableAt = cycleStart.AddMinutes(5), CorrelationId = "lv", CreatedAt = cycleStart.AddMinutes(5), Version = 1 });
            await db.SaveChangesAsync();
            var blocked = await new ScheduledJobProducer(db, fixture.TimeProvider, new ConfigurationBuilder().Build()).ListingVerifyCycleAttemptsAsync(blockedSeed.Tenant.Id, blockedSeed.Connection.Id, cycleStart, CancellationToken.None);
            Assert.False(ListingVerificationPolicy.MayQueueAgain(blocked.Queued, blocked.Blocked));
        }
    }

    // K6 (2026-11-01): Pazarama's approval checks, waiting days for their next try, kept the nightly verification out of
    // the queue every night. It is queued now, and the lease still runs the two one at a time.
    [PostgreSqlFact]
    public async Task An_approval_check_waiting_for_its_next_try_lets_the_verification_queue_but_never_run_beside_it()
    {
        var seed = await Seed("PAZARAMA");
        var now = ListingVerificationPolicy.CycleStart(DateTimeOffset.UtcNow).AddMinutes(10);
        var approvalId = Guid.CreateVersion7();
        await using (var db = fixture.CreateContext())
        {
            db.ConnectionSyncPolicies.Add(new ConnectionSyncPolicy { Id = Guid.CreateVersion7(), TenantId = seed.Tenant.Id, ConnectionId = seed.Connection.Id, ResourceType = ListingVerificationPolicy.ResourceType, IntervalSeconds = ListingVerificationPolicy.DefaultIntervalSeconds, OverlapSeconds = 0, JitterSeconds = 0, Enabled = true, Version = 1 });
            db.IntegrationJobs.Add(new IntegrationJob { Id = approvalId, TenantId = seed.Tenant.Id, ConnectionId = seed.Connection.Id, JobType = MarketplaceJobTypes.ProductApprovalReconcile, PayloadJson = "{}", PayloadVersion = 1, PayloadHash = "h", JobDedupKey = $"product-approval:{approvalId:N}", EffectIdempotencyKey = $"pa:{approvalId:N}", Status = JobStatus.RetryScheduled, AttemptCount = 3, MaxAttempts = 50, AvailableAt = now.AddHours(2), CorrelationId = "pa", CreatedAt = now.AddDays(-3), Version = 1 });
            await db.SaveChangesAsync();
        }

        await using (var db = fixture.CreateContext())
            await new ScheduledJobProducer(db, new FixedTimeProvider(now), new ConfigurationBuilder().Build()).EnqueueDueAsync(CancellationToken.None);

        await using var check = fixture.CreateContext();
        var verify = await check.IntegrationJobs.AsNoTracking().SingleAsync(x => x.TenantId == seed.Tenant.Id && x.ConnectionId == seed.Connection.Id && x.JobType == MarketplaceJobTypes.ListingVerify);
        Assert.Equal(now, (await check.ConnectionSyncPolicies.AsNoTracking().SingleAsync(x => x.ConnectionId == seed.Connection.Id && x.ResourceType == ListingVerificationPolicy.ResourceType)).LastScheduledAt);

        // Both due now: the verification runs, the approval check waits for the products lane.
        var due = DateTimeOffset.UtcNow.AddMinutes(-1);
        await check.IntegrationJobs.Where(x => x.Id == verify.Id || x.Id == approvalId).ExecuteUpdateAsync(setters => setters.SetProperty(x => x.AvailableAt, due));
        var leases = new JobLeaseService(check, fixture.TokenHasher, fixture.TimeProvider);
        Assert.Equal(verify.Id, (await leases.TryLeaseAsync(TimeSpan.FromMinutes(2), null, null, CancellationToken.None, onlyJobId: verify.Id))?.Id);
        Assert.Null(await leases.TryLeaseAsync(TimeSpan.FromMinutes(2), null, null, CancellationToken.None, onlyJobId: approvalId));
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [PostgreSqlFact]
    public async Task A_connection_outside_production_and_stage_is_refused_as_blocked()
    {
        var seed = await Seed("TRENDYOL");
        await using (var db = fixture.CreateContext())
            await db.PlatformConnections.Where(x => x.Id == seed.Connection.Id).ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Environment, "SANDBOX"));

        var result = await Verify(seed, new ScriptedProductPort { Catalog = [Remote("Ürün", "869100000001")] });

        Assert.Equal(JobCompletionKind.Blocked, result.Kind);
    }

    // Live 2026-11-02: Trendyol answered a 28-barcode create batch COMPLETED with 21 items 16 s after the send, and the
    // job stopped in manual review with an internal text and no per-variant rows. A short answer is a result still being
    // written: the job asks again; past the grace the listed item gets its outcome and the missing one stays unknown.
    [PostgreSqlFact]
    public async Task A_create_batch_result_missing_a_sent_barcode_waits_then_records_it_as_unknown()
    {
        var seed = await Seed("TRENDYOL");
        var productId = seed.Seen.ProductId;
        var profileId = Guid.CreateVersion7();
        var jobId = Guid.CreateVersion7();
        await using (var setup = fixture.CreateContext())
        {
            await setup.ProductVariants.Where(x => x.Id == seed.Missed.Id).ExecuteUpdateAsync(setters => setters.SetProperty(x => x.ProductId, productId));
            setup.ChannelListingProfiles.Add(new ChannelListingProfile { Id = profileId, TenantId = seed.Tenant.Id, ConnectionId = seed.Connection.Id, ProductId = productId, Enabled = true, DesiredStatus = "LIVE", ActualStatus = "BATCH_SUBMITTED" });
            foreach (var variant in new[] { seed.Seen, seed.Missed })
            {
                setup.ChannelListingVariants.Add(new ChannelListingVariant { Id = Guid.CreateVersion7(), TenantId = seed.Tenant.Id, ProfileId = profileId, VariantId = variant.Id, ExternalSku = variant.Barcode, ExternalBarcode = variant.Barcode, DesiredStatus = "LIVE", ActualStatus = "BATCH_SUBMITTED" });
                setup.MarketplaceListingStates.Add(new MarketplaceListingState { Id = Guid.CreateVersion7(), TenantId = seed.Tenant.Id, ConnectionId = seed.Connection.Id, VariantId = variant.Id, DesiredStatus = "LIVE", ActualStatus = "BATCH_SUBMITTED", PayloadHash = "create-hash" });
            }
            await setup.SaveChangesAsync();
        }
        string Payload(DateTimeOffset submittedAt) => JsonSerializer.Serialize(new ProductPublicationJobPayload(jobId, productId, profileId, "POLL", "create-hash", "{\"items\":[]}", "batch-1", submittedAt));
        await using (var setup = fixture.CreateContext())
        {
            setup.IntegrationJobs.Add(new IntegrationJob { Id = jobId, TenantId = seed.Tenant.Id, ConnectionId = seed.Connection.Id, JobType = MarketplaceJobTypes.ProductCreate, PayloadJson = Payload(DateTimeOffset.UtcNow.AddSeconds(-16)), PayloadVersion = 1, PayloadHash = "h", JobDedupKey = $"product-create:{seed.Connection.Id:N}:{productId:N}:x", EffectIdempotencyKey = $"pc:{jobId:N}", Status = JobStatus.Leased, AvailableAt = DateTimeOffset.UtcNow, CorrelationId = "pc", CreatedAt = DateTimeOffset.UtcNow, Version = 1 });
            await setup.SaveChangesAsync();
        }
        var port = new ScriptedProductPort { Operation = new RemoteOperationStatus("batch-1", "COMPLETED", [new RemoteOperationLine("869100000001", true, null, null, false)], ReportedItemCount: 2) };

        await using (var db = fixture.CreateContext())
        {
            var early = await Processor(db, port).ProcessAsync(seed.Tenant.Id, seed.Connection.Id, MarketplaceJobTypes.ProductCreate, Payload(DateTimeOffset.UtcNow.AddSeconds(-16)), "pc", CancellationToken.None, jobId);
            Assert.Equal(JobCompletionKind.Retry, early.Kind);
            Assert.Equal("PRODUCT_BATCH_PENDING", early.ErrorCode);
            Assert.True(JobWaitingPolicy.IsWaiting(early));
        }
        await using (var check = fixture.CreateContext())
        {
            Assert.Equal("BATCH_IN_PROGRESS", (await check.ChannelListingProfiles.AsNoTracking().SingleAsync(x => x.Id == profileId)).ActualStatus);
            Assert.False(await check.JobVariantResults.AnyAsync(x => x.JobId == jobId));
        }

        await using (var db = fixture.CreateContext())
        {
            var settled = await Processor(db, port).ProcessAsync(seed.Tenant.Id, seed.Connection.Id, MarketplaceJobTypes.ProductCreate, Payload(DateTimeOffset.UtcNow - ProductBatchResultPolicy.IncompleteGrace - TimeSpan.FromMinutes(1)), "pc", CancellationToken.None, jobId);
            Assert.Equal(JobCompletionKind.Succeeded, settled.Kind);
            Assert.Equal(ProductBatchResultPolicy.IncompleteResultCode, settled.ErrorCode);
        }
        await using var after = fixture.CreateContext();
        Assert.Equal("APPROVAL_PENDING", (await after.ChannelListingProfiles.AsNoTracking().SingleAsync(x => x.Id == profileId)).ActualStatus);
        var listings = await after.ChannelListingVariants.AsNoTracking().Where(x => x.ProfileId == profileId).ToDictionaryAsync(x => x.VariantId);
        Assert.Equal("CREATE_ACCEPTED", listings[seed.Seen.Id].ActualStatus);
        Assert.Equal(ProductBatchResultPolicy.ResultUnknownStatus, listings[seed.Missed.Id].ActualStatus);
        var rows = await after.JobVariantResults.AsNoTracking().Where(x => x.JobId == jobId).ToDictionaryAsync(x => x.VariantId!.Value);
        Assert.Equal(JobVariantResultPolicy.Sent, rows[seed.Seen.Id].Status);
        Assert.Equal(JobVariantResultPolicy.Pending, rows[seed.Missed.Id].Status);
        Assert.Equal(ProductBatchResultPolicy.MissingReason("Trendyol"), rows[seed.Missed.Id].Reason);
        Assert.True(await after.IntegrationJobs.AnyAsync(x => x.TenantId == seed.Tenant.Id && x.JobType == MarketplaceJobTypes.ProductApprovalReconcile));
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
    private sealed class ScriptedProductPort : IProductPort, IListingCodeCheckPort
    {
        // PTT AVM's BarkodKontrol answer: true held, false not held (nil), null unclear.
        public bool? Held { get; init; }

        public Task<AdapterResult<bool?>> HoldsCodeAsync(AdapterContext context, string code, CancellationToken cancellationToken) =>
            Task.FromResult(AdapterResult<bool?>.Success(Held));

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
