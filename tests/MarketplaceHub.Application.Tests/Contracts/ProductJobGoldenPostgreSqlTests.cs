using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests.Contracts;

// ISLEYICI-9 stage 5 step 3d (16 Nov 2026): the product writes (PRODUCT_CREATE with the approval check it queues,
// PRODUCT_UPDATE, PRODUCT_ARCHIVE as Pazarama's sale-status switch) pinned before they move out of the processor together
// with the publication-side link writes (ProductLinks). The cards are the model-composer golden's (one model in three
// colours, publishable on every marketplace); the jobs are queued the way the panel queues them (CatalogService), so the
// exact body each marketplace receives is the composer's. For every marketplace: an accepted send read to its end, a
// refused send, a batch that stays pending, and the same job run again after it was accepted; the update accepted and
// refused; Pazarama's sale-status switch accepted and refused. Each records every call, every result and the tenant's rows.
[Collection("postgres-integration")]
public sealed class ProductJobGoldenPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    private static readonly DateTimeOffset Start = JobGolden.Start;
    private static readonly string[] Platforms = ["TRENDYOL", "HEPSIBURADA", "N11", "PAZARAMA", "PTTAVM", "SHOPIFY"];

    private sealed class ProductRun(PostgreSqlTenantIsolationFixture fixture)
    {
        public readonly GoldenClock Clock = new(Start);
        public readonly Dictionary<Guid, string> Known = [];
        public readonly List<object> Runs = [];
        public ModelComposerGoldenPostgreSqlTests.Seeded Seeded = null!;
        public PlatformConnection Connection = null!;

        public IConfiguration Configuration => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["FeatureFlags:ExternalWrites"] = "true",
            ["Hepsiburada:AuthenticationMode"] = "BASIC"
        }).Build();

        public async Task SeedAsync(string platform)
        {
            Seeded = await ModelComposerGoldenPostgreSqlTests.SeedAsync(fixture, "same");
            Connection = Seeded.Connections.Single(x => x.PlatformCode == platform);
            Known[Seeded.Tenant.Id] = "tenant";
            foreach (var connection in Seeded.Connections) { Known[connection.Id] = $"connection-{connection.PlatformCode}"; Known[connection.PublicId] = $"connection-public-{connection.PlatformCode}"; }
            foreach (var (card, index) in Seeded.Cards.Select((card, index) => (card, index))) Known[card.Id] = $"card-{index}";
            await using var db = fixture.CreateContext();
            // The owner turns the writes on for this connection (they are off on every live connection by default).
            await db.PlatformConnections.Where(x => x.Id == Connection.Id).ExecuteUpdateAsync(setters => setters.SetProperty(x => x.SettingsJson, platform == "N11" ? """{"N11ShipmentTemplate":"Standart","ExternalWritesEnabled":true}""" : """{"ExternalWritesEnabled":true}"""));
            if (platform == "PTTAVM")
                db.ConnectionSyncPolicies.Add(new ConnectionSyncPolicy { Id = Guid.CreateVersion7(), TenantId = Seeded.Tenant.Id, ConnectionId = Connection.Id, ResourceType = MarketplaceExternalWritePolicies.Manual, Enabled = true, UserDefined = true });
            // The write evidence a connection test leaves (Hepsiburada's price and stock writes, Shopify's publication scopes).
            string[] evidence = platform switch
            {
                "HEPSIBURADA" => [MarketplaceCapabilities.PriceWrite, MarketplaceCapabilities.InventoryWrite],
                "SHOPIFY" => [MarketplaceCapabilities.ProductWrite, MarketplaceCapabilities.InventoryWrite, MarketplaceCapabilities.PublicationRead, MarketplaceCapabilities.PublicationWrite],
                _ => []
            };
            foreach (var code in evidence)
            {
                var capability = new PlatformCapability { Id = Guid.CreateVersion7(), TenantId = Seeded.Tenant.Id, ConnectionId = Connection.Id, Code = code, SupportLevel = CapabilitySupportLevel.Supported, ApiVersion = Connection.ApiVersion, Environment = Connection.Environment, StoreScope = Connection.ExternalStoreId, SourceUrl = $"https://{CapabilityEvidencePolicy.OfficialDocumentationHost(platform)}/docs", SourceVersion = "V1", VerifiedAt = Start.AddDays(-1), Version = 1 };
                Known[capability.Id] = $"capability-{code}";
                db.PlatformCapabilities.Add(capability);
            }
            await db.SaveChangesAsync();
            foreach (var variant in await db.ProductVariants.AsNoTracking().Where(x => x.TenantId == Seeded.Tenant.Id).ToListAsync()) Known[variant.Id] = $"variant-{variant.Sku}";
            foreach (var profile in await db.ChannelListingProfiles.AsNoTracking().Where(x => x.TenantId == Seeded.Tenant.Id).ToListAsync()) Known[profile.Id] = $"profile-{Known[profile.ConnectionId]}-{Known[profile.ProductId]}";
            foreach (var listing in await db.ChannelListingVariants.AsNoTracking().Where(x => x.TenantId == Seeded.Tenant.Id).ToListAsync()) Known[listing.Id] = $"listing-{Known[listing.ProfileId]}-{listing.ExternalSku}";
            foreach (var offer in await db.ChannelOffers.AsNoTracking().Where(x => x.TenantId == Seeded.Tenant.Id).ToListAsync()) Known[offer.Id] = $"offer-{Known[offer.ConnectionId]}-{Known[offer.VariantId]}";
            foreach (var link in await db.MarketplaceVariantLinks.AsNoTracking().Where(x => x.TenantId == Seeded.Tenant.Id).ToListAsync()) Known[link.Id] = $"link-{Known[link.ConnectionId]}-{Known[link.VariantId]}";
        }

        public CatalogService Catalog(AppDbContext db) => new(db, null!, Configuration, new FrozenClock(Start), new MemoryCache(new MemoryCacheOptions()));

        public async Task<JobExecutionResult> RunAsync(string label, IntegrationJob job, GoldenMarketplace port)
        {
            JobExecutionResult result;
            await using (var db = fixture.CreateContext(new FrozenClock(Start)))
                result = await new MarketplaceJobProcessor(db, port, port, port, port, port, port, port, port, null!, port, Configuration, Clock)
                    .ProcessAsync(Seeded.Tenant.Id, Connection.Id, job.JobType, job.PayloadJson, $"pjg-{label}", CancellationToken.None, job.Id);
            Runs.Add(new { Run = label, job.JobType, Result = result, Calls = port.Calls.ToList() });
            port.Calls.Clear();
            return result;
        }

        public async Task<IntegrationJob> JobAsync(Guid id)
        {
            await using var db = fixture.CreateContext();
            return await db.IntegrationJobs.AsNoTracking().SingleAsync(x => x.Id == id);
        }

        // The job run again by the queue with the payload its last run left, until it stops retrying.
        public async Task<JobExecutionResult> RunUntilDoneAsync(string label, Guid jobId, GoldenMarketplace port, int runs = 4)
        {
            JobExecutionResult result = JobExecutionResult.Success();
            for (var index = 0; index < runs; index++)
            {
                if (index > 0) Clock.Advance(TimeSpan.FromMinutes(2));
                result = await RunAsync($"{label}-{index}", await JobAsync(jobId), port);
                if (result.Kind != JobCompletionKind.Retry) break;
            }
            return result;
        }

        public async Task MatchesAsync(string goldenPath)
        {
            await using var check = fixture.CreateContext();
            foreach (var job in await check.IntegrationJobs.AsNoTracking().Where(x => x.TenantId == Seeded.Tenant.Id).OrderBy(x => x.JobType).ThenBy(x => x.Id).ToListAsync())
                Known.TryAdd(job.Id, $"job-{job.JobType}");
            var tables = await JobGolden.TablesAsync(check, Seeded.Tenant.Id, Known);
            JobGolden.Matches(goldenPath, new { Runs, Tables = tables }, Known, maskPayloadHashes: true);
        }
    }

    private static GoldenMarketplace Port(string scenario, IReadOnlyList<string> barcodes)
    {
        var port = new GoldenMarketplace();
        var refused = GoldenMarketplace.Error(AdapterErrorClass.Validation, "REMOTE_VALIDATION", 400);
        var accepted = AdapterResult<RemoteOperationRef>.Success(new("product-op-1", "PRODUCT", Start.AddMinutes(1)));
        port.Answers["products.Create"] = _ => scenario == "refused" ? AdapterResult<RemoteOperationRef>.Failure(refused) : accepted;
        foreach (var method in new[] { "products.UpdateUnapproved", "products.UpdateApprovedContent", "products.UpdateApprovedVariants", "products.UpdateApprovedDelivery", "products.Archive" })
            port.Answers[method] = _ => scenario == "refused" ? AdapterResult<RemoteOperationRef>.Failure(refused) : accepted;
        port.Answers["products.GetOperation"] = _ => scenario == "pending"
            ? AdapterResult<RemoteOperationStatus>.Success(new("product-op-1", "IN_PROGRESS", []))
            : AdapterResult<RemoteOperationStatus>.Success(new("product-op-1", "COMPLETED", [.. barcodes.Select(code => new RemoteOperationLine(code, true, $"remote-{code}", null, false))]));
        port.Answers["products.GetPublicationStatus"] = args => AdapterResult<RemotePublicationStatus>.Success(new((string)args[0]!, "APPROVED", "remote-product-1", $"remote-{args[0]}", null, "{}"));
        return port;
    }

    public static TheoryData<string, string> CreateScenarios()
    {
        var data = new TheoryData<string, string>();
        foreach (var platform in Platforms)
            foreach (var scenario in new[] { "accepted", "refused", "pending", "repeat" })
                data.Add(platform, scenario);
        return data;
    }

    [PostgreSqlTheory]
    [MemberData(nameof(CreateScenarios))]
    public async Task AProductCreateSendsAndStoresTheSameThings(string platform, string scenario)
    {
        using var culture = JobGolden.InvariantCulture();
        var run = new ProductRun(fixture);
        await run.SeedAsync(platform);
        var card = run.Seeded.Cards[0];
        await using (var db = fixture.CreateContext())
        {
            // Not on the marketplace yet: the card's listing rows are drafts.
            var profileIds = await db.ChannelListingProfiles.Where(x => x.TenantId == run.Seeded.Tenant.Id && x.ConnectionId == run.Connection.Id).Select(x => x.Id).ToListAsync();
            await db.ChannelListingVariants.Where(x => x.TenantId == run.Seeded.Tenant.Id && profileIds.Contains(x.ProfileId)).ExecuteUpdateAsync(setters => setters.SetProperty(x => x.ActualStatus, "DRAFT"));
        }
        Guid jobId;
        await using (var db = fixture.CreateContext(new FrozenClock(Start)))
        {
            var queued = await run.Catalog(db).EnqueuePublicationAsync(run.Seeded.Tenant.Id, card.Id, run.Connection.Id, "golden-publish", "golden", CancellationToken.None);
            if (!queued.Succeeded)
            {
                run.Runs.Add(new { Run = "enqueue", Refused = queued.Error!.Code });
                await run.MatchesAsync($"ProductJobs/{platform.ToLowerInvariant()}-product_create-{scenario}.golden.json");
                return;
            }
            jobId = queued.Value;
        }
        run.Known[jobId] = "create-job";
        var barcodes = (await ListingBarcodes(run, card.Id)).ToList();
        var port = Port(scenario, barcodes);
        var submitted = await run.JobAsync(jobId);
        await run.RunUntilDoneAsync("create", jobId, port);
        if (scenario == "repeat")
        {
            run.Clock.Advance(TimeSpan.FromMinutes(5));
            await run.RunAsync("create-again", submitted, port);
        }
        // The approval check the send queued, run once as the queue would.
        IntegrationJob? approval;
        await using (var db = fixture.CreateContext())
            approval = await db.IntegrationJobs.AsNoTracking().Where(x => x.TenantId == run.Seeded.Tenant.Id && x.JobType == MarketplaceJobTypes.ProductApprovalReconcile).OrderBy(x => x.Id).FirstOrDefaultAsync();
        if (approval is not null)
        {
            run.Known[approval.Id] = "approval-job";
            run.Clock.Advance(TimeSpan.FromMinutes(5));
            await run.RunAsync("approval", approval, port);
        }
        await run.MatchesAsync($"ProductJobs/{platform.ToLowerInvariant()}-product_create-{scenario}.golden.json");
    }

    public static TheoryData<string, string> UpdateScenarios()
    {
        var data = new TheoryData<string, string>();
        foreach (var platform in Platforms)
            foreach (var scenario in new[] { "accepted", "refused" })
                data.Add(platform, scenario);
        return data;
    }

    [PostgreSqlTheory]
    [MemberData(nameof(UpdateScenarios))]
    public async Task AProductUpdateSendsAndStoresTheSameThings(string platform, string scenario)
    {
        using var culture = JobGolden.InvariantCulture();
        var run = new ProductRun(fixture);
        await run.SeedAsync(platform);
        var card = run.Seeded.Cards[0];
        await using (var db = fixture.CreateContext())
        {
            // The card is live on the marketplace: its publication was accepted earlier.
            await db.ChannelListingProfiles.Where(x => x.TenantId == run.Seeded.Tenant.Id && x.ConnectionId == run.Connection.Id).ExecuteUpdateAsync(setters => setters.SetProperty(x => x.ActualStatus, "LIVE"));
            var variantIds = await db.ProductVariants.Where(x => x.TenantId == run.Seeded.Tenant.Id).Select(x => x.Id).ToListAsync();
            foreach (var variantId in variantIds)
                db.MarketplaceListingStates.Add(new MarketplaceListingState { Id = Guid.CreateVersion7(), TenantId = run.Seeded.Tenant.Id, ConnectionId = run.Connection.Id, VariantId = variantId, DesiredStatus = "ACTIVE", ActualStatus = "LIVE", Version = 1 });
            await db.SaveChangesAsync();
        }
        Guid jobId;
        await using (var db = fixture.CreateContext(new FrozenClock(Start)))
        {
            var queued = await run.Catalog(db).EnqueueProductUpdateWithNoticesAsync(run.Seeded.Tenant.Id, card.Id, run.Connection.Id, "golden-update", "golden", CancellationToken.None);
            if (!queued.Succeeded)
            {
                run.Runs.Add(new { Run = "enqueue", Refused = queued.Error!.Code });
                await run.MatchesAsync($"ProductJobs/{platform.ToLowerInvariant()}-product_update-{scenario}.golden.json");
                return;
            }
            jobId = queued.Value!.JobId;
        }
        run.Known[jobId] = "update-job";
        var port = Port(scenario, [.. await ListingBarcodes(run, card.Id)]);
        await run.RunUntilDoneAsync("update", jobId, port, runs: 6);
        await run.MatchesAsync($"ProductJobs/{platform.ToLowerInvariant()}-product_update-{scenario}.golden.json");
    }

    [PostgreSqlTheory]
    [InlineData("accepted")]
    [InlineData("refused")]
    public async Task APazaramaSaleStatusSwitchSendsAndStoresTheSameThings(string scenario)
    {
        using var culture = JobGolden.InvariantCulture();
        var run = new ProductRun(fixture);
        await run.SeedAsync("PAZARAMA");
        var barcodes = await ListingBarcodes(run, run.Seeded.Cards[0].Id);
        await using (var db = fixture.CreateContext())
        {
            // Pazarama addresses a product by its barcode: the links an import would have made.
            foreach (var variant in await db.ProductVariants.AsNoTracking().Where(x => x.TenantId == run.Seeded.Tenant.Id && x.ProductId == run.Seeded.Cards[0].Id).ToListAsync())
                db.MarketplaceVariantLinks.Add(new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = run.Seeded.Tenant.Id, ConnectionId = run.Connection.Id, VariantId = variant.Id, ExternalId = variant.Barcode!, ExternalBarcode = variant.Barcode, ExternalSku = variant.Sku, Version = 1 });
            await db.SaveChangesAsync();
        }
        Guid jobId;
        await using (var db = fixture.CreateContext(new FrozenClock(Start)))
        {
            var queued = await run.Catalog(db).EnqueuePazaramaSaleStatusAsync(run.Seeded.Tenant.Id, run.Connection.Id, barcodes, close: true, "golden-sale", "golden", CancellationToken.None);
            Assert.True(queued.Succeeded, queued.Error?.Code);
            jobId = queued.Value;
        }
        run.Known[jobId] = "archive-job";
        var port = Port(scenario, barcodes);
        await run.RunUntilDoneAsync("archive", jobId, port);
        await run.MatchesAsync($"ProductJobs/pazarama-product_archive-{scenario}.golden.json");
    }

    private async Task<IReadOnlyList<string>> ListingBarcodes(ProductRun run, Guid productId)
    {
        await using var db = fixture.CreateContext();
        return [.. (await db.ProductVariants.AsNoTracking().Where(x => x.TenantId == run.Seeded.Tenant.Id && x.ProductId == productId).Select(x => x.Barcode!).ToListAsync()).Order(StringComparer.Ordinal)];
    }
}
