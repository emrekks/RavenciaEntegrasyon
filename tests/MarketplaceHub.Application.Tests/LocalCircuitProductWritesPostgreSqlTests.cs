using System.Text.Json;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Scan 13 Nov 2026 #1: a product create, update or archive refused by the panel's own open circuit was never sent. It
// leaves no unclear ExternalEffect, the card keeps its state, the job keeps its attempt, and the next run sends it.
[Collection("postgres-integration")]
public sealed class LocalCircuitProductWritesPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    private static AdapterError LocalCircuit => new(AdapterErrorClass.RateLimit, LocalCircuitOpen.Code("TRENDYOL"), "Trendyol isteği gönderilmedi.", 429, TimeSpan.FromSeconds(20), null);

    [PostgreSqlFact]
    public async Task ACreateRefusedByTheOpenCircuit_IsSentAgainOnTheNextRun()
    {
        var seed = await SeedCardAsync("lc-create", profileStatus: "PENDING", listingStatus: "PENDING");
        var jobId = Guid.CreateVersion7();
        var payload = JsonSerializer.Serialize(new ProductPublicationJobPayload(jobId, seed.ProductId, seed.ProfileId, "SUBMIT", "H-LC", """{"items":[]}""", null, null));
        await AddJobAsync(seed, jobId, MarketplaceJobTypes.ProductCreate, payload);
        var port = new Port { Refuse = true };

        var refused = await RunAsync(seed, port, MarketplaceJobTypes.ProductCreate, payload, jobId);

        AssertDeferred(refused);
        await AssertNoEffectAsync(seed);
        port.Refuse = false;
        var sent = await RunAsync(seed, port, MarketplaceJobTypes.ProductCreate, payload, jobId);
        Assert.Equal("PRODUCT_BATCH_PENDING", sent.ErrorCode);
        Assert.Equal(2, port.Creates);
    }

    [PostgreSqlFact]
    public async Task AnUpdateRefusedByTheOpenCircuit_KeepsTheCardAndIsSentAgain()
    {
        var seed = await SeedCardAsync("lc-update", profileStatus: "LIVE", listingStatus: "LIVE");
        var jobId = Guid.CreateVersion7();
        var variants = $$"""{"items":[{"barcode":"lc-update-0","dimensionalWeight":2}]}""";
        var payload = JsonSerializer.Serialize(new ProductUpdateJobPayload(jobId, seed.ProductId, seed.ProfileId, "SUBMIT_VARIANTS", "APPROVED", "H-LC", "{}", """{"items":[]}""", variants, """{"items":[]}""", null, null));
        await AddJobAsync(seed, jobId, MarketplaceJobTypes.ProductUpdate, payload);
        var port = new Port { Refuse = true };

        var refused = await RunAsync(seed, port, MarketplaceJobTypes.ProductUpdate, payload, jobId);

        AssertDeferred(refused);
        await AssertNoEffectAsync(seed);
        await using (var db = fixture.CreateContext())
            Assert.Equal("LIVE", (await db.ChannelListingProfiles.AsNoTracking().SingleAsync(x => x.Id == seed.ProfileId)).ActualStatus);
        port.Refuse = false;
        Assert.Equal("PRODUCT_UPDATE_BATCH_PENDING", (await RunAsync(seed, port, MarketplaceJobTypes.ProductUpdate, payload, jobId)).ErrorCode);
        Assert.Equal(2, port.Updates);
    }

    [PostgreSqlFact]
    public async Task AnArchiveRefusedByTheOpenCircuit_KeepsTheCardAndIsSentAgain()
    {
        var seed = await SeedCardAsync("lc-archive", profileStatus: "LIVE", listingStatus: "LIVE");
        var jobId = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        var payload = JsonSerializer.Serialize(new ProductArchiveJobPayload(jobId, seed.ProductId, seed.ProfileId, true, "SUBMIT", "H-LC", """{"items":[{"barcode":"lc-archive-0","archived":true}]}""", null, now.AddMinutes(-1), now.AddHours(4)));
        await AddJobAsync(seed, jobId, MarketplaceJobTypes.ProductArchive, payload);
        var port = new Port { Refuse = true };

        var refused = await RunAsync(seed, port, MarketplaceJobTypes.ProductArchive, payload, jobId);

        AssertDeferred(refused);
        await AssertNoEffectAsync(seed);
        await using (var db = fixture.CreateContext())
            Assert.Equal("LIVE", (await db.ChannelListingProfiles.AsNoTracking().SingleAsync(x => x.Id == seed.ProfileId)).ActualStatus);
        port.Refuse = false;
        Assert.Equal("PRODUCT_ARCHIVE_BATCH_PENDING", (await RunAsync(seed, port, MarketplaceJobTypes.ProductArchive, payload, jobId)).ErrorCode);
        Assert.Equal(2, port.Archives);
    }

    private static void AssertDeferred(JobExecutionResult result)
    {
        Assert.Equal(JobCompletionKind.Retry, result.Kind);
        Assert.Equal("TRENDYOL_LOCAL_CIRCUIT_OPEN", result.ErrorCode);
        Assert.True(result.RefundAttempt);
        Assert.False(result.BackOffConnection);
    }

    private async Task AssertNoEffectAsync(Seed seed)
    {
        await using var db = fixture.CreateContext();
        Assert.False(await db.ExternalEffectRecords.AnyAsync(x => x.TenantId == seed.TenantId));
    }

    private async Task<JobExecutionResult> RunAsync(Seed seed, Port port, string jobType, string fallbackPayload, Guid jobId)
    {
        await using var db = fixture.CreateContext();
        var payload = await db.IntegrationJobs.AsNoTracking().Where(x => x.Id == jobId).Select(x => x.PayloadJson).SingleAsync();
        var processor = new MarketplaceJobProcessor(db, null!, null!, port, null!, null!, null!, null!, null!, null!, null!,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["FeatureFlags:ExternalWrites"] = "true" }).Build(), fixture.TimeProvider);
        return await processor.ProcessAsync(seed.TenantId, seed.ConnectionId, jobType, string.IsNullOrWhiteSpace(payload) ? fallbackPayload : payload, "lc", CancellationToken.None, jobId);
    }

    private async Task AddJobAsync(Seed seed, Guid jobId, string jobType, string payload)
    {
        var now = DateTimeOffset.UtcNow;
        await using var db = fixture.CreateContext();
        db.IntegrationJobs.Add(new IntegrationJob { Id = jobId, TenantId = seed.TenantId, ConnectionId = seed.ConnectionId, JobType = jobType, PayloadJson = payload, PayloadVersion = 1, PayloadHash = "h", JobDedupKey = $"lc:{jobId:N}", EffectIdempotencyKey = $"lc:{jobId:N}", Status = JobStatus.Pending, AvailableAt = now, CorrelationId = "lc", CreatedAt = now, Version = 1 });
        await db.SaveChangesAsync();
    }

    private sealed record Seed(Guid TenantId, Guid ConnectionId, Guid ProfileId, Guid ProductId);

    // A Trendyol card of one size with external writes on.
    private async Task<Seed> SeedCardAsync(string code, string profileStatus, string listingStatus)
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"{code}-{Guid.NewGuid():N}", DisplayName = code, Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var connection = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = "TRENDYOL", Environment = "PRODUCTION", DisplayName = code, ExternalStoreId = $"store-{Guid.NewGuid():N}", Status = "ACTIVE", ApiVersion = "V2", SettingsJson = "{\"ExternalWritesEnabled\":true}", Version = 1 };
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
        db.MarketplaceListingStates.Add(new MarketplaceListingState { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, VariantId = variant.Id, DesiredStatus = "LIVE", ActualStatus = listingStatus, PayloadHash = "H-LC" });
        await db.SaveChangesAsync();
        return new(tenant.Id, connection.Id, profile.Id, product.Id);
    }

    private sealed class Port : IProductPort
    {
        public bool Refuse { get; set; }
        public int Creates { get; private set; }
        public int Updates { get; private set; }
        public int Archives { get; private set; }

        private AdapterResult<RemoteOperationRef> Answer(string kind) => Refuse
            ? AdapterResult<RemoteOperationRef>.Failure(LocalCircuit)
            : AdapterResult<RemoteOperationRef>.Success(new RemoteOperationRef($"batch-{kind}", kind, DateTimeOffset.UtcNow));

        public Task<AdapterResult<RemoteOperationRef>> CreateAsync(AdapterContext context, ProductPublication publication, CancellationToken cancellationToken) { Creates++; return Task.FromResult(Answer("CREATE")); }
        public Task<AdapterResult<RemoteOperationRef>> UpdateApprovedVariantsAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) { Updates++; return Task.FromResult(Answer("PRODUCT_UPDATE_VARIANTS_V2")); }
        public Task<AdapterResult<RemoteOperationRef>> ArchiveAsync(AdapterContext context, string payloadJson, CancellationToken cancellationToken) { Archives++; return Task.FromResult(Answer("ARCHIVE")); }

        public Task<AdapterResult<RemoteOperationStatus>> GetOperationAsync(AdapterContext context, string externalOperationId, CancellationToken cancellationToken) =>
            Task.FromResult(AdapterResult<RemoteOperationStatus>.Success(new RemoteOperationStatus(externalOperationId, "IN_PROGRESS", [])));
        public Task<AdapterResult<AdapterPageResult<RemoteCatalogProduct>>> ListCatalogAsync(AdapterContext context, AdapterPageRequest page, ProductReadFilter filter, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<AdapterPageResult<RemoteProduct>>> ListAsync(AdapterContext context, AdapterPageRequest page, ProductReadFilter filter, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<RemoteOperationRef>> UpdateUnapprovedAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<RemoteOperationRef>> UpdateApprovedContentAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<RemoteOperationRef>> UpdateApprovedDeliveryAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<RemotePublicationStatus>> GetPublicationStatusAsync(AdapterContext context, string barcode, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
