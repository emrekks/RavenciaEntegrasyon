using System.Text.Json;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// ISLEYICI-10 (15 Nov 2026) characterization of the product writes' ExternalEffect records: create, update, archive and
// Pazarama's sale status change, for each answer of the marketplace (accepted, unclear, quota, refused), and a second run
// after an unclear answer (nothing is sent again) or a quota answer (sent again). Written against the code before
// ExternalEffectGuard, so moving the sites onto the guard must keep every line of it.
[Collection("postgres-integration")]
public sealed class ExternalEffectProductSitesPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    [PostgreSqlTheory]
    [InlineData("success", JobCompletionKind.Retry, "PRODUCT_BATCH_PENDING", true, "BATCH_SUBMITTED")]
    [InlineData("uncertain", JobCompletionKind.ManualReview, "EXTERNAL_EFFECT_AMBIGUOUS", false, "MANUAL_REVIEW")]
    [InlineData("quota", JobCompletionKind.Retry, "TRENDYOL_RATE_LIMITED", null, "RETRY_SCHEDULED")]
    [InlineData("refused", JobCompletionKind.Blocked, "TRENDYOL_VALIDATION", null, "BLOCKED")]
    public async Task Create(string outcome, JobCompletionKind kind, string code, bool? effectCompleted, string profileStatus)
    {
        var seed = await SeedCardAsync($"ef-create-{outcome}", "TRENDYOL", "PENDING", "PENDING");
        var jobId = Guid.CreateVersion7();
        var payload = JsonSerializer.Serialize(new ProductPublicationJobPayload(jobId, seed.ProductId, seed.ProfileId, "SUBMIT", "H-EF", """{"items":[]}""", null, null));
        await AddJobAsync(seed, jobId, MarketplaceJobTypes.ProductCreate, payload);
        var port = new Port(PostgreSqlTenantIsolationTests.EffectAnswer(outcome));

        var result = await RunAsync(seed, port, MarketplaceJobTypes.ProductCreate, payload, jobId);

        Assert.Equal(kind, result.Kind);
        Assert.Equal(code, result.ErrorCode);
        Assert.Equal(1, port.Creates);
        await AssertEffectAsync(seed, MarketplaceJobTypes.ProductCreate, $"ef:{jobId:N}", effectCompleted);
        await AssertProfileAsync(seed, profileStatus);
        await AssertSecondRunAsync(outcome, () => RunAsync(seed, port, MarketplaceJobTypes.ProductCreate, payload, jobId), () => port.Creates);
    }

    [PostgreSqlTheory]
    [InlineData("success", JobCompletionKind.Retry, "PRODUCT_UPDATE_BATCH_PENDING", true, "UPDATE_VARIANTS_SUBMITTED")]
    [InlineData("uncertain", JobCompletionKind.ManualReview, "EXTERNAL_EFFECT_AMBIGUOUS", false, "MANUAL_REVIEW")]
    // J2 (15 Nov 2026): a quota answer leaves the card as it was while the job sends the update again.
    [InlineData("quota", JobCompletionKind.Retry, "TRENDYOL_RATE_LIMITED", null, "LIVE")]
    [InlineData("refused", JobCompletionKind.Blocked, "TRENDYOL_VALIDATION", null, "UPDATE_BLOCKED")]
    public async Task Update(string outcome, JobCompletionKind kind, string code, bool? effectCompleted, string profileStatus)
    {
        var seed = await SeedCardAsync($"ef-update-{outcome}", "TRENDYOL", "LIVE", "LIVE");
        var jobId = Guid.CreateVersion7();
        var variants = $$"""{"items":[{"barcode":"ef-update-{{outcome}}-0","dimensionalWeight":2}]}""";
        var payload = JsonSerializer.Serialize(new ProductUpdateJobPayload(jobId, seed.ProductId, seed.ProfileId, "SUBMIT_VARIANTS", "APPROVED", "H-EF", "{}", """{"items":[]}""", variants, """{"items":[]}""", null, null));
        await AddJobAsync(seed, jobId, MarketplaceJobTypes.ProductUpdate, payload);
        var port = new Port(PostgreSqlTenantIsolationTests.EffectAnswer(outcome));

        var result = await RunAsync(seed, port, MarketplaceJobTypes.ProductUpdate, payload, jobId);

        Assert.Equal(kind, result.Kind);
        Assert.Equal(code, result.ErrorCode);
        Assert.Equal(1, port.Updates);
        await AssertEffectAsync(seed, $"{MarketplaceJobTypes.ProductUpdate}:SUBMIT_VARIANTS", $"ef:{jobId:N}:SUBMIT_VARIANTS", effectCompleted);
        await AssertProfileAsync(seed, profileStatus);
        await AssertSecondRunAsync(outcome, () => RunAsync(seed, port, MarketplaceJobTypes.ProductUpdate, payload, jobId), () => port.Updates);
    }

    [PostgreSqlTheory]
    [InlineData("success", JobCompletionKind.Retry, "PRODUCT_ARCHIVE_BATCH_PENDING", true, "ARCHIVE_BATCH_SUBMITTED")]
    [InlineData("uncertain", JobCompletionKind.ManualReview, "EXTERNAL_EFFECT_AMBIGUOUS", false, "MANUAL_REVIEW")]
    [InlineData("quota", JobCompletionKind.Retry, "TRENDYOL_RATE_LIMITED", null, "LIVE")]
    [InlineData("refused", JobCompletionKind.Blocked, "TRENDYOL_VALIDATION", null, "ARCHIVE_BLOCKED")]
    public async Task Archive(string outcome, JobCompletionKind kind, string code, bool? effectCompleted, string profileStatus)
    {
        var seed = await SeedCardAsync($"ef-archive-{outcome}", "TRENDYOL", "LIVE", "LIVE");
        var jobId = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        var payload = JsonSerializer.Serialize(new ProductArchiveJobPayload(jobId, seed.ProductId, seed.ProfileId, true, "SUBMIT", "H-EF", $$"""{"items":[{"barcode":"ef-archive-{{outcome}}-0","archived":true}]}""", null, now.AddMinutes(-1), now.AddHours(4)));
        await AddJobAsync(seed, jobId, MarketplaceJobTypes.ProductArchive, payload);
        var port = new Port(PostgreSqlTenantIsolationTests.EffectAnswer(outcome));

        var result = await RunAsync(seed, port, MarketplaceJobTypes.ProductArchive, payload, jobId);

        Assert.Equal(kind, result.Kind);
        Assert.Equal(code, result.ErrorCode);
        Assert.Equal(1, port.Archives);
        await AssertEffectAsync(seed, MarketplaceJobTypes.ProductArchive, $"ef:{jobId:N}", effectCompleted);
        await AssertProfileAsync(seed, profileStatus);
        await AssertSecondRunAsync(outcome, () => RunAsync(seed, port, MarketplaceJobTypes.ProductArchive, payload, jobId), () => port.Archives);
    }

    [PostgreSqlTheory]
    [InlineData("success", JobCompletionKind.Succeeded, null, true)]
    [InlineData("uncertain", JobCompletionKind.ManualReview, "EXTERNAL_EFFECT_AMBIGUOUS", false)]
    [InlineData("quota", JobCompletionKind.Retry, "TRENDYOL_RATE_LIMITED", null)]
    [InlineData("refused", JobCompletionKind.Blocked, "TRENDYOL_VALIDATION", null)]
    public async Task PazaramaSaleStatus(string outcome, JobCompletionKind kind, string? code, bool? effectCompleted)
    {
        var seed = await SeedCardAsync($"ef-pzr-{outcome}", "PAZARAMA", "LIVE", "LIVE");
        var jobId = Guid.CreateVersion7();
        var payload = JsonSerializer.Serialize(new PazaramaSaleStatusJobPayload(jobId, [$"ef-pzr-{outcome}-0"], true, DateTimeOffset.UtcNow));
        await AddJobAsync(seed, jobId, MarketplaceJobTypes.ProductArchive, payload);
        var port = new Port(PostgreSqlTenantIsolationTests.EffectAnswer(outcome));

        var result = await RunAsync(seed, port, MarketplaceJobTypes.ProductArchive, payload, jobId);

        Assert.Equal(kind, result.Kind);
        Assert.Equal(code, result.ErrorCode);
        Assert.Equal(1, port.Archives);
        await AssertEffectAsync(seed, MarketplaceJobTypes.ProductArchive, $"ef:{jobId:N}", effectCompleted);
        await AssertSecondRunAsync(outcome, () => RunAsync(seed, port, MarketplaceJobTypes.ProductArchive, payload, jobId), () => port.Archives);
    }

    // After an unclear answer the record blocks a resend; after a quota answer the request goes again.
    private static async Task AssertSecondRunAsync(string outcome, Func<Task<JobExecutionResult>> run, Func<int> calls)
    {
        if (outcome is not ("uncertain" or "quota")) return;
        var again = await run();
        if (outcome == "uncertain")
        {
            Assert.Equal(JobCompletionKind.ManualReview, again.Kind);
            Assert.Equal("EXTERNAL_EFFECT_AMBIGUOUS", again.ErrorCode);
            Assert.Equal(1, calls());
        }
        else Assert.Equal(2, calls());
    }

    private async Task AssertEffectAsync(Seed seed, string effectType, string key, bool? completed)
    {
        await using var db = fixture.CreateContext();
        var effects = await db.ExternalEffectRecords.AsNoTracking().Where(x => x.TenantId == seed.TenantId).ToListAsync();
        if (completed is null) { Assert.Empty(effects); return; }
        var effect = Assert.Single(effects);
        Assert.Equal(effectType, effect.EffectType);
        Assert.Equal(key, effect.IdempotencyKey);
        Assert.Null(effect.ConnectionId);
        Assert.Null(effect.TargetId);
        Assert.Null(effect.Action);
        Assert.Null(effect.Outcome);
        Assert.Equal(completed.Value, effect.CompletedAt is not null);
    }

    private async Task AssertProfileAsync(Seed seed, string status)
    {
        await using var db = fixture.CreateContext();
        Assert.Equal(status, (await db.ChannelListingProfiles.AsNoTracking().SingleAsync(x => x.Id == seed.ProfileId)).ActualStatus);
    }

    private async Task<JobExecutionResult> RunAsync(Seed seed, Port port, string jobType, string fallbackPayload, Guid jobId)
    {
        await using var db = fixture.CreateContext();
        var payload = await db.IntegrationJobs.AsNoTracking().Where(x => x.Id == jobId).Select(x => x.PayloadJson).SingleAsync();
        var processor = new MarketplaceJobProcessor(db, null!, null!, port, null!, null!, null!, null!, null!, null!, null!,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["FeatureFlags:ExternalWrites"] = "true" }).Build(), fixture.TimeProvider);
        return await processor.ProcessAsync(seed.TenantId, seed.ConnectionId, jobType, string.IsNullOrWhiteSpace(payload) ? fallbackPayload : payload, "ef", CancellationToken.None, jobId);
    }

    private async Task AddJobAsync(Seed seed, Guid jobId, string jobType, string payload)
    {
        var now = DateTimeOffset.UtcNow;
        await using var db = fixture.CreateContext();
        db.IntegrationJobs.Add(new IntegrationJob { Id = jobId, TenantId = seed.TenantId, ConnectionId = seed.ConnectionId, JobType = jobType, PayloadJson = payload, PayloadVersion = 1, PayloadHash = "h", JobDedupKey = $"ef:{jobId:N}", EffectIdempotencyKey = $"ef:{jobId:N}", Status = JobStatus.Pending, AvailableAt = now, CorrelationId = "ef", CreatedAt = now, Version = 1 });
        await db.SaveChangesAsync();
    }

    private sealed record Seed(Guid TenantId, Guid ConnectionId, Guid ProfileId, Guid ProductId);

    // A card of one size with external writes on.
    private async Task<Seed> SeedCardAsync(string code, string platformCode, string profileStatus, string listingStatus)
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"{code}-{Guid.NewGuid():N}", DisplayName = code, Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var connection = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = platformCode, Environment = "PRODUCTION", DisplayName = code, ExternalStoreId = $"store-{Guid.NewGuid():N}", Status = "ACTIVE", ApiVersion = "V2", SettingsJson = "{\"ExternalWritesEnabled\":true}", Version = 1 };
        var product = new Product { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Title = "Kazak", Description = "Triko", Status = ProductStatus.Active, CreatedAt = now, UpdatedAt = now };
        var profile = new ChannelListingProfile { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, ProductId = product.Id, Enabled = true, DesiredStatus = "LIVE", ActualStatus = profileStatus };
        var barcode = $"{code}-0";
        var variant = new ProductVariant { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProductId = product.Id, Sku = barcode, SkuNormalized = barcode, Barcode = barcode, BarcodeNormalized = barcode, ModelCode = code, OptionSignature = barcode, CreatedAt = now, UpdatedAt = now };
        await using var db = fixture.CreateContext();
        db.Tenants.Add(tenant);
        db.PlatformConnections.Add(connection);
        db.Products.Add(product);
        db.ChannelListingProfiles.Add(profile);
        db.ProductVariants.Add(variant);
        db.ChannelListingVariants.Add(new ChannelListingVariant { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProfileId = profile.Id, VariantId = variant.Id, ExternalSku = barcode, ExternalBarcode = barcode, DesiredStatus = "LIVE", ActualStatus = listingStatus });
        db.MarketplaceListingStates.Add(new MarketplaceListingState { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, VariantId = variant.Id, DesiredStatus = "LIVE", ActualStatus = listingStatus, PayloadHash = "H-EF" });
        await db.SaveChangesAsync();
        return new(tenant.Id, connection.Id, profile.Id, product.Id);
    }

    private sealed class Port(AdapterError? answer) : IProductPort
    {
        public int Creates { get; private set; }
        public int Updates { get; private set; }
        public int Archives { get; private set; }

        private AdapterResult<RemoteOperationRef> Answer(string kind) => answer is not null
            ? AdapterResult<RemoteOperationRef>.Failure(answer)
            : AdapterResult<RemoteOperationRef>.Success(new RemoteOperationRef($"batch-{kind}", kind, DateTimeOffset.UtcNow));

        public Task<AdapterResult<RemoteOperationRef>> CreateAsync(AdapterContext context, ProductPublication publication, CancellationToken cancellationToken) { Creates++; return Task.FromResult(Answer("CREATE")); }
        public Task<AdapterResult<RemoteOperationRef>> UpdateApprovedVariantsAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) { Updates++; return Task.FromResult(Answer("PRODUCT_UPDATE_VARIANTS_V2")); }
        public Task<AdapterResult<RemoteOperationRef>> ArchiveAsync(AdapterContext context, string payloadJson, CancellationToken cancellationToken) { Archives++; return Task.FromResult(Answer("ARCHIVE")); }

        public Task<AdapterResult<RemoteOperationStatus>> GetOperationAsync(AdapterContext context, string externalOperationId, CancellationToken cancellationToken) =>
            Task.FromResult(AdapterResult<RemoteOperationStatus>.Success(new RemoteOperationStatus(externalOperationId, "COMPLETED", [])));
        public Task<AdapterResult<AdapterPageResult<RemoteCatalogProduct>>> ListCatalogAsync(AdapterContext context, AdapterPageRequest page, ProductReadFilter filter, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<AdapterPageResult<RemoteProduct>>> ListAsync(AdapterContext context, AdapterPageRequest page, ProductReadFilter filter, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<RemoteOperationRef>> UpdateUnapprovedAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<RemoteOperationRef>> UpdateApprovedContentAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<RemoteOperationRef>> UpdateApprovedDeliveryAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<RemotePublicationStatus>> GetPublicationStatusAsync(AdapterContext context, string barcode, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
