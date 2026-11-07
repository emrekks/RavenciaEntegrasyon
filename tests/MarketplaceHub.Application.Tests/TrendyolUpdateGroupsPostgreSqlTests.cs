using System.Text.Json;
using System.Text.Json.Nodes;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// The grouped Trendyol send (owner, 7 Nov 2026: RYP003T's six colour cards went in six content requests). The cards of
// one "Platformlarda Güncelle" share one content request and one poll of its batch id; each card's job gets its own
// answer, a refused content stops only its card, and after an unclear answer no card is sent again.
[Collection("postgres-integration")]
public sealed class TrendyolUpdateGroupsPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>, IAsyncLifetime
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
        await db.ExternalEffectRecords.Where(x => tenantIds.Contains(x.TenantId)).ExecuteDeleteAsync();
    }

    [PostgreSqlFact]
    public async Task ThreeColourCards_GoInOneContentRequest_PolledOnce_AndARefusedContentStopsOnlyItsCard()
    {
        var seed = await SeedAsync("group-content", 3);
        var port = new GroupPort();
        port.Answer = new RemoteOperationStatus("batch-group-1", "COMPLETED",
        [
            new RemoteOperationLine("9000000001", true, null, null, false),
            new RemoteOperationLine("9000000002", false, null, "Görsel adresi geçersiz", false),
            new RemoteOperationLine("9000000003", true, null, null, false)
        ]);

        // The first two cards are ready but wait for the third; the third sends for all three.
        var first = await RunAsync(seed, 0, port);
        Assert.Equal("PRODUCT_UPDATE_GROUP_PENDING", first.ErrorCode);
        Assert.True(JobWaitingPolicy.IsWaiting(first));
        Assert.Equal("PRODUCT_UPDATE_GROUP_PENDING", (await RunAsync(seed, 1, port)).ErrorCode);
        Assert.Empty(port.Sent);
        var third = await RunAsync(seed, 2, port);
        Assert.Equal("PRODUCT_UPDATE_BATCH_PENDING", third.ErrorCode);
        var sent = Assert.Single(port.Sent);
        var items = JsonNode.Parse(sent.ApprovedContentPayloadJson)!["items"]!.AsArray();
        Assert.Equal([9000000003L, 9000000001L, 9000000002L], items.Select(item => item!["contentId"]!.GetValue<long>()));

        await using (var db = fixture.CreateContext())
        {
            foreach (var jobId in seed.JobIds)
            {
                var payload = await PayloadAsync(db, jobId);
                Assert.Equal("POLL_CONTENT", payload.Phase);
                Assert.Equal("batch-group-1", payload.ExternalOperationId);
                Assert.Equal(seed.JobIds[2], payload.GroupLeaderJobId);
                var shared = Assert.Single(payload.SharedRequests!);
                Assert.Equal(("CONTENT", 3, 3), (shared.Part, shared.Cards, shared.Items));
                // Every card holds its own effect record before the request.
                var job = await db.IntegrationJobs.AsNoTracking().SingleAsync(x => x.Id == jobId);
                Assert.True(await db.ExternalEffectRecords.AnyAsync(x => x.TenantId == seed.TenantId && x.IdempotencyKey == job.EffectIdempotencyKey + ":SUBMIT_CONTENT" && x.CompletedAt != null));
            }
            // The drawer shows the shared request on each card.
            var write = JobWritePayloadReader.Read(MarketplaceJobTypes.ProductUpdate, (await db.IntegrationJobs.AsNoTracking().SingleAsync(x => x.Id == seed.JobIds[0])).PayloadJson)!;
            var contentPart = Assert.Single(write.Parts, part => part.Key == "CONTENT");
            Assert.Equal((1, 3, 3), (contentPart.ContentCount, contentPart.SharedCards, contentPart.SharedItems));
        }

        // A card that did not send waits for the sender's poll without asking Trendyol itself.
        var waiting = await RunAsync(seed, 0, port);
        Assert.Equal("PRODUCT_UPDATE_BATCH_PENDING", waiting.ErrorCode);
        Assert.Equal(0, port.Polls);

        // The sender polls once and answers every card.
        var polled = await RunAsync(seed, 2, port);
        Assert.Equal(1, port.Polls);
        Assert.Equal("PRODUCT_UPDATE_NEXT_PHASE", polled.ErrorCode);
        await using (var db = fixture.CreateContext())
        {
            Assert.Equal("SUBMIT_VARIANTS", (await PayloadAsync(db, seed.JobIds[0])).Phase);
            var refused = await PayloadAsync(db, seed.JobIds[1]);
            Assert.Equal(TrendyolUpdateGroups.StoppedPhase, refused.Phase);
            Assert.Equal("Görsel adresi geçersiz", refused.GroupOutcome!.RejectionCode);
        }

        // Each card finishes on its own run: the refused one stops, the others complete (their variant and delivery
        // steps carry nothing, so no request).
        var stopped = await RunAsync(seed, 1, port);
        Assert.Equal(JobCompletionKind.Blocked, stopped.Kind);
        Assert.Equal("PRODUCT_UPDATE_CONTENT_REJECTED", stopped.ErrorCode);
        Assert.Equal(JobCompletionKind.Retry, (await RunAsync(seed, 0, port)).Kind);
        Assert.True((await RunAsync(seed, 0, port)).Succeeded);
        Assert.Equal(JobCompletionKind.Retry, (await RunAsync(seed, 2, port)).Kind);
        Assert.True((await RunAsync(seed, 2, port)).Succeeded);
        Assert.Single(port.Sent);
        Assert.Equal(1, port.Polls);

        await using (var db = fixture.CreateContext())
        {
            Assert.Equal("UPDATE_REJECTED", (await db.ChannelListingProfiles.AsNoTracking().SingleAsync(x => x.Id == seed.ProfileIds[1])).ActualStatus);
            Assert.Equal("APPROVAL_PENDING", (await db.ChannelListingProfiles.AsNoTracking().SingleAsync(x => x.Id == seed.ProfileIds[0])).ActualStatus);
            Assert.Equal("APPROVAL_PENDING", (await db.ChannelListingProfiles.AsNoTracking().SingleAsync(x => x.Id == seed.ProfileIds[2])).ActualStatus);
            // Each card's variant results are its own job's.
            var results = await db.JobVariantResults.AsNoTracking().Where(x => x.TenantId == seed.TenantId).ToListAsync();
            Assert.Equal(2, results.Select(x => x.JobId).Distinct().Count());
            Assert.All(results, result => Assert.Contains(result.JobId, new[] { seed.JobIds[0], seed.JobIds[2] }));
        }
    }

    [PostgreSqlFact]
    public async Task AnUnclearAnswerToTheSharedRequest_StopsEveryCardOfIt_WithoutSendingAgain()
    {
        var seed = await SeedAsync("group-ambiguous", 2);
        var port = new GroupPort { SubmitError = new AdapterError(AdapterErrorClass.TransientNetwork, "TIMEOUT", "Zaman aşımı", null, null, null) };

        Assert.Equal("PRODUCT_UPDATE_GROUP_PENDING", (await RunAsync(seed, 0, port)).ErrorCode);
        var sender = await RunAsync(seed, 1, port);
        Assert.Equal(JobCompletionKind.ManualReview, sender.Kind);
        Assert.Equal("EXTERNAL_EFFECT_AMBIGUOUS", sender.ErrorCode);
        var other = await RunAsync(seed, 0, port);
        Assert.Equal(JobCompletionKind.ManualReview, other.Kind);
        Assert.Equal("EXTERNAL_EFFECT_AMBIGUOUS", other.ErrorCode);
        Assert.Single(port.Sent);
        await using var db = fixture.CreateContext();
        Assert.Equal("MANUAL_REVIEW", (await db.ChannelListingProfiles.AsNoTracking().SingleAsync(x => x.Id == seed.ProfileIds[0])).ActualStatus);
    }

    [PostgreSqlFact]
    public async Task WhenTheSenderStopsBeforeItsPoll_AnotherCardPollsInItsPlace()
    {
        var seed = await SeedAsync("group-takeover", 2);
        var port = new GroupPort
        {
            Answer = new RemoteOperationStatus("batch-group-1", "COMPLETED", [new RemoteOperationLine("9000000001", true, null, null, false), new RemoteOperationLine("9000000002", true, null, null, false)])
        };
        Assert.Equal("PRODUCT_UPDATE_GROUP_PENDING", (await RunAsync(seed, 0, port)).ErrorCode);
        Assert.Equal("PRODUCT_UPDATE_BATCH_PENDING", (await RunAsync(seed, 1, port)).ErrorCode);
        await using (var db = fixture.CreateContext())
            await db.IntegrationJobs.Where(x => x.Id == seed.JobIds[1]).ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Status, JobStatus.Cancelled));

        var result = await RunAsync(seed, 0, port);

        Assert.Equal("PRODUCT_UPDATE_NEXT_PHASE", result.ErrorCode);
        Assert.Equal(1, port.Polls);
        await using var check = fixture.CreateContext();
        Assert.Equal("SUBMIT_VARIANTS", (await PayloadAsync(check, seed.JobIds[0])).Phase);
        // The stopped sender is left as it was.
        Assert.Equal("POLL_CONTENT", (await PayloadAsync(check, seed.JobIds[1])).Phase);
    }

    [PostgreSqlFact]
    public async Task ACardAloneInItsGroup_GoesOnTheUsualPath()
    {
        var seed = await SeedAsync("group-alone", 1);
        var port = new GroupPort();

        var result = await RunAsync(seed, 0, port);

        Assert.Equal("PRODUCT_UPDATE_BATCH_PENDING", result.ErrorCode);
        Assert.Single(port.Sent);
        await using var db = fixture.CreateContext();
        var payload = await PayloadAsync(db, seed.JobIds[0]);
        Assert.Null(payload.GroupLeaderJobId);
        Assert.Null(payload.SharedRequests);
    }

    private async Task<JobExecutionResult> RunAsync(Seeded seed, int card, GroupPort port)
    {
        await using var db = fixture.CreateContext();
        var job = await db.IntegrationJobs.AsNoTracking().SingleAsync(x => x.Id == seed.JobIds[card]);
        var processor = new MarketplaceJobProcessor(db, null!, null!, port, null!, null!, null!, null!, null!, null!, null!,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["FeatureFlags:ExternalWrites"] = "true" }).Build(), fixture.TimeProvider);
        var result = await processor.ProcessAsync(seed.TenantId, seed.ConnectionId, MarketplaceJobTypes.ProductUpdate, job.PayloadJson, job.CorrelationId, CancellationToken.None, job.Id);
        // A finished job leaves the running set, as its runner would record it.
        if (result.Kind != JobCompletionKind.Retry)
            await db.IntegrationJobs.Where(x => x.Id == job.Id).ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Status, result.Succeeded ? JobStatus.Succeeded : JobStatus.Blocked));
        return result;
    }

    private static async Task<ProductUpdateJobPayload> PayloadAsync(AppDbContext db, Guid jobId) =>
        JsonSerializer.Deserialize<ProductUpdateJobPayload>((await db.IntegrationJobs.AsNoTracking().SingleAsync(x => x.Id == jobId)).PayloadJson)!;

    private sealed record Seeded(Guid TenantId, Guid ConnectionId, IReadOnlyList<Guid> ProfileIds, IReadOnlyList<Guid> JobIds);

    // Colour cards of one model, one Trendyol content each (already resolved to its contentId), sent by one action.
    private async Task<Seeded> SeedAsync(string code, int cards)
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"{code}-{Guid.NewGuid():N}", DisplayName = code, Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        CreatedTenants.Add(tenant.Id);
        var connection = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = "TRENDYOL", Environment = "PRODUCTION", DisplayName = "Trendyol grup testi", ExternalStoreId = $"store-{Guid.NewGuid():N}", Status = "ACTIVE", ApiVersion = "V2", SettingsJson = "{\"ExternalWritesEnabled\":true}", Version = 1 };
        var groupId = Guid.NewGuid();
        var profiles = new List<Guid>();
        var jobs = new List<Guid>();
        await using var db = fixture.CreateContext();
        db.Tenants.Add(tenant);
        db.PlatformConnections.Add(connection);
        for (var card = 0; card < cards; card++)
        {
            var product = new Product { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Title = $"Kazak {card}", Description = "Triko", Status = ProductStatus.Active, CreatedAt = now, UpdatedAt = now };
            var profile = new ChannelListingProfile { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, ProductId = product.Id, Enabled = true, DesiredStatus = "LIVE", ActualStatus = "LIVE" };
            db.Products.Add(product);
            db.ChannelListingProfiles.Add(profile);
            var hash = $"H-{card}";
            for (var size = 0; size < 3; size++)
            {
                var barcode = $"{code}-{card}-{size}";
                var variant = new ProductVariant { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProductId = product.Id, Sku = barcode, SkuNormalized = barcode, Barcode = barcode, BarcodeNormalized = barcode, ModelCode = "RYP003T", OptionSignature = barcode, CreatedAt = now, UpdatedAt = now };
                db.ProductVariants.Add(variant);
                db.ChannelListingVariants.Add(new ChannelListingVariant { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProfileId = profile.Id, VariantId = variant.Id, ExternalSku = barcode, ExternalBarcode = barcode, DesiredStatus = "LIVE", ActualStatus = "LIVE" });
                db.MarketplaceListingStates.Add(new MarketplaceListingState { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, VariantId = variant.Id, DesiredStatus = "LIVE", ActualStatus = "LIVE", PayloadHash = hash });
            }
            var content = new JsonObject { ["items"] = new JsonArray(new JsonObject { ["contentId"] = 9000000001L + card, ["images"] = new JsonArray(new JsonObject { ["url"] = $"https://img/{card}/1.jpg" }) }) }.ToJsonString();
            var jobId = Guid.CreateVersion7();
            var payload = new ProductUpdateJobPayload(jobId, product.Id, profile.Id, "SUBMIT_CONTENT", "APPROVED", hash, "{}", content, """{"items":[]}""", """{"items":[]}""", null, null, SendGroupId: groupId);
            var payloadJson = JsonSerializer.Serialize(payload);
            db.IntegrationJobs.Add(new IntegrationJob { Id = jobId, TenantId = tenant.Id, ConnectionId = connection.Id, JobType = MarketplaceJobTypes.ProductUpdate, PayloadJson = payloadJson, PayloadVersion = 1, PayloadHash = "h", JobDedupKey = $"pu:{jobId:N}", EffectIdempotencyKey = $"pu:{jobId:N}", Status = JobStatus.Pending, AvailableAt = now, CorrelationId = "pu", CreatedAt = now.AddMilliseconds(card), Version = 1 });
            profiles.Add(profile.Id);
            jobs.Add(jobId);
        }
        await db.SaveChangesAsync();
        return new(tenant.Id, connection.Id, profiles, jobs);
    }

    // Trendyol's content request and batch result, recorded instead of sent.
    private sealed class GroupPort : IProductPort
    {
        public List<ProductUpdatePublication> Sent { get; } = [];
        public int Polls { get; private set; }
        public AdapterError? SubmitError { get; init; }
        public RemoteOperationStatus? Answer { get; set; }

        public Task<AdapterResult<RemoteOperationRef>> UpdateApprovedContentAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken)
        {
            Sent.Add(publication);
            return Task.FromResult(SubmitError is null
                ? AdapterResult<RemoteOperationRef>.Success(new RemoteOperationRef("batch-group-1", "PRODUCT_UPDATE_CONTENT_V2", DateTimeOffset.UtcNow.AddMinutes(-1)))
                : AdapterResult<RemoteOperationRef>.Failure(SubmitError));
        }

        public Task<AdapterResult<RemoteOperationStatus>> GetOperationAsync(AdapterContext context, string externalOperationId, CancellationToken cancellationToken)
        {
            Polls++;
            return Task.FromResult(AdapterResult<RemoteOperationStatus>.Success(Answer ?? new RemoteOperationStatus(externalOperationId, "IN_PROGRESS", [])));
        }

        public Task<AdapterResult<AdapterPageResult<RemoteCatalogProduct>>> ListCatalogAsync(AdapterContext context, AdapterPageRequest page, ProductReadFilter filter, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<AdapterPageResult<RemoteProduct>>> ListAsync(AdapterContext context, AdapterPageRequest page, ProductReadFilter filter, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<RemoteOperationRef>> CreateAsync(AdapterContext context, ProductPublication publication, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<RemoteOperationRef>> UpdateUnapprovedAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<RemoteOperationRef>> UpdateApprovedVariantsAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<RemoteOperationRef>> UpdateApprovedDeliveryAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<RemotePublicationStatus>> GetPublicationStatusAsync(AdapterContext context, string barcode, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<RemoteOperationRef>> ArchiveAsync(AdapterContext context, string payloadJson, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
