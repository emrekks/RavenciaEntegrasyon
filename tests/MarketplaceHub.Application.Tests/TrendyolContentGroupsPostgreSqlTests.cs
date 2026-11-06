using System.Text.Json;
using System.Text.Json.Nodes;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// The content phase of a Trendyol product update against PostgreSQL (live 6.11.2026, RYP006: one card, one Trendyol
// content per colour). The job reads each barcode's content, then sends each content once with its own colour's data;
// a content shared with a barcode the card does not have stops the update before any write.
[Collection("postgres-integration")]
public sealed class TrendyolContentGroupsPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>, IAsyncLifetime
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
    public async Task ATwoColourCard_GoesOncePerTrendyolContent_WithEachColoursOwnImages()
    {
        var seed = await SeedAsync("content-groups");
        var port = new ContentPort(
            Content("1228880360", "RYP006-01", "RYP006-02"),
            Content("1228880361", "RYP006-03", "RYP006-04"));

        var result = await RunAsync(seed, port);

        Assert.Equal(JobCompletionKind.Retry, result.Kind);
        Assert.Equal("PRODUCT_UPDATE_BATCH_PENDING", result.ErrorCode);
        // One read per content: the sibling size came with the first answer.
        Assert.Equal(["RYP006-01", "RYP006-03"], port.Lookups);
        var sent = Assert.Single(port.Sent);
        var items = JsonNode.Parse(sent.ApprovedContentPayloadJson)!["items"]!.AsArray();
        Assert.Equal([1228880360L, 1228880361L], items.Select(item => item!["contentId"]!.GetValue<long>()));
        Assert.Equal("https://img/haki/1.jpg", items[0]!["images"]![0]!["url"]!.GetValue<string>());
        Assert.Equal("https://img/siyah/1.jpg", items[1]!["images"]![0]!["url"]!.GetValue<string>());
        Assert.All(items, item => Assert.False(item!.AsObject().ContainsKey("barcode")));

        await using var db = fixture.CreateContext();
        var job = await db.IntegrationJobs.AsNoTracking().SingleAsync(x => x.Id == seed.JobId);
        var payload = JsonSerializer.Deserialize<ProductUpdateJobPayload>(job.PayloadJson)!;
        Assert.Equal("POLL_CONTENT", payload.Phase);
        // The job keeps the request it sent, and the listings' payload hash is unchanged.
        Assert.Equal(sent.ApprovedContentPayloadJson, payload.ApprovedContentPayloadJson);
        Assert.Equal("H-CONTENT", payload.PayloadHash);
        Assert.Equal("UPDATE_CONTENT_SUBMITTED", (await db.ChannelListingProfiles.AsNoTracking().SingleAsync(x => x.Id == seed.ProfileId)).ActualStatus);
    }

    [PostgreSqlFact]
    public async Task AContentSharedWithABarcodeTheCardDoesNotHave_StopsTheUpdateBeforeAnyWrite()
    {
        var seed = await SeedAsync("content-shared");
        var port = new ContentPort(
            Content("1228880360", "RYP006-01", "RYP006-02", "OTHER-01"),
            Content("1228880361", "RYP006-03", "RYP006-04"));

        var result = await RunAsync(seed, port);

        Assert.Equal(JobCompletionKind.Blocked, result.Kind);
        Assert.Equal("PRODUCT_CONTENT_SHARED", result.ErrorCode);
        Assert.Empty(port.Sent);
        await using var db = fixture.CreateContext();
        var profile = await db.ChannelListingProfiles.AsNoTracking().SingleAsync(x => x.Id == seed.ProfileId);
        Assert.Equal("UPDATE_BLOCKED", profile.ActualStatus);
        Assert.Equal("PRODUCT_CONTENT_SHARED", profile.LastRejectionCode);
        Assert.False(await db.ExternalEffectRecords.AnyAsync(x => x.TenantId == seed.TenantId));
    }

    private async Task<JobExecutionResult> RunAsync(Seeded seed, ContentPort port)
    {
        await using var db = fixture.CreateContext();
        var job = await db.IntegrationJobs.AsNoTracking().SingleAsync(x => x.Id == seed.JobId);
        var processor = new MarketplaceJobProcessor(db, null!, null!, port, null!, null!, null!, null!, null!, null!, null!,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["FeatureFlags:ExternalWrites"] = "true" }).Build(), fixture.TimeProvider);
        return await processor.ProcessAsync(seed.TenantId, seed.ConnectionId, MarketplaceJobTypes.ProductUpdate, job.PayloadJson, job.CorrelationId, CancellationToken.None, job.Id);
    }

    private sealed record Seeded(Guid TenantId, Guid ConnectionId, Guid ProfileId, Guid JobId);

    // One card, Haki and Siyah, two sizes each; its content request is per barcode, as the update composer writes it.
    private async Task<Seeded> SeedAsync(string code)
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"{code}-{Guid.NewGuid():N}", DisplayName = code, Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        CreatedTenants.Add(tenant.Id);
        var connection = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = "TRENDYOL", Environment = "PRODUCTION", DisplayName = "Trendyol içerik testi", ExternalStoreId = $"store-{Guid.NewGuid():N}", Status = "ACTIVE", ApiVersion = "V2", SettingsJson = "{\"ExternalWritesEnabled\":true}", Version = 1 };
        var product = new Product { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Title = "Kazak", Description = "Triko", Status = ProductStatus.Active, CreatedAt = now, UpdatedAt = now };
        var profile = new ChannelListingProfile { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, ProductId = product.Id, Enabled = true, DesiredStatus = "LIVE", ActualStatus = "LIVE", LastRejectionCode = "PRODUCT_APPROVAL_CONTENT_SPLIT" };
        var barcodes = new[] { ("RYP006-01", "haki"), ("RYP006-02", "haki"), ("RYP006-03", "siyah"), ("RYP006-04", "siyah") };
        var contentItems = new JsonArray();
        await using var db = fixture.CreateContext();
        db.Tenants.Add(tenant);
        db.PlatformConnections.Add(connection);
        db.Products.Add(product);
        db.ChannelListingProfiles.Add(profile);
        foreach (var (barcode, colour) in barcodes)
        {
            var variant = new ProductVariant { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProductId = product.Id, Sku = barcode, SkuNormalized = barcode, Barcode = barcode, BarcodeNormalized = barcode, ModelCode = "RYP006", OptionSignature = barcode, CreatedAt = now, UpdatedAt = now };
            db.ProductVariants.Add(variant);
            db.ChannelListingVariants.Add(new ChannelListingVariant { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProfileId = profile.Id, VariantId = variant.Id, ExternalSku = barcode, ExternalBarcode = barcode, DesiredStatus = "LIVE", ActualStatus = "LIVE" });
            db.MarketplaceListingStates.Add(new MarketplaceListingState { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, VariantId = variant.Id, DesiredStatus = "LIVE", ActualStatus = "LIVE", PayloadHash = "H-CONTENT" });
            contentItems.Add(new JsonObject
            {
                ["barcode"] = barcode,
                ["title"] = "Kazak",
                ["description"] = "Triko",
                ["images"] = new JsonArray(new JsonObject { ["url"] = $"https://img/{colour}/1.jpg" }),
                ["attributes"] = new JsonArray(new JsonObject { ["attributeId"] = 47, ["customAttributeValue"] = colour })
            });
        }
        var jobId = Guid.CreateVersion7();
        var payload = new ProductUpdateJobPayload(jobId, product.Id, profile.Id, "SUBMIT_CONTENT", "APPROVED", "H-CONTENT", "{}",
            new JsonObject { ["items"] = contentItems }.ToJsonString(), """{"items":[]}""", """{"items":[]}""", null, null);
        var payloadJson = JsonSerializer.Serialize(payload);
        db.IntegrationJobs.Add(new IntegrationJob { Id = jobId, TenantId = tenant.Id, ConnectionId = connection.Id, JobType = MarketplaceJobTypes.ProductUpdate, PayloadJson = payloadJson, PayloadVersion = 1, PayloadHash = "h", JobDedupKey = $"pu:{jobId:N}", EffectIdempotencyKey = $"pu:{jobId:N}", Status = JobStatus.Leased, AvailableAt = now, CorrelationId = "pu", CreatedAt = now, Version = 1 });
        await db.SaveChangesAsync();
        return new(tenant.Id, connection.Id, profile.Id, jobId);
    }

    private static RemoteCatalogProduct Content(string contentId, params string[] barcodes) => new(
        contentId, "RYP006", "Kazak", "Triko", null, null, null, null, [],
        [.. barcodes.Select(barcode => new RemoteCatalogVariant(barcode, barcode, barcode, "RYP006", new Dictionary<string, string>(), false, null, null, null, null, null, "{}"))],
        "{}");

    // Trendyol's approved list answers a barcode with the content holding it; the content request is recorded, not sent.
    private sealed class ContentPort(params RemoteCatalogProduct[] contents) : IProductPort
    {
        public List<string> Lookups { get; } = [];
        public List<ProductUpdatePublication> Sent { get; } = [];

        public Task<AdapterResult<AdapterPageResult<RemoteCatalogProduct>>> ListCatalogAsync(AdapterContext context, AdapterPageRequest page, ProductReadFilter filter, CancellationToken cancellationToken)
        {
            Lookups.Add(filter.Barcode!);
            var answer = contents.Where(content => content.Variants.Any(variant => variant.Barcode == filter.Barcode)).ToList();
            return Task.FromResult(AdapterResult<AdapterPageResult<RemoteCatalogProduct>>.Success(new(answer, null, false, answer.Count)));
        }

        public Task<AdapterResult<RemoteOperationRef>> UpdateApprovedContentAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken)
        {
            Sent.Add(publication);
            return Task.FromResult(AdapterResult<RemoteOperationRef>.Success(new RemoteOperationRef("batch-content-1", "PRODUCT_UPDATE_CONTENT_V2", DateTimeOffset.UtcNow)));
        }

        public Task<AdapterResult<AdapterPageResult<RemoteProduct>>> ListAsync(AdapterContext context, AdapterPageRequest page, ProductReadFilter filter, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<RemoteOperationRef>> CreateAsync(AdapterContext context, ProductPublication publication, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<RemoteOperationRef>> UpdateUnapprovedAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<RemoteOperationRef>> UpdateApprovedVariantsAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<RemoteOperationRef>> UpdateApprovedDeliveryAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<RemoteOperationStatus>> GetOperationAsync(AdapterContext context, string externalOperationId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<RemotePublicationStatus>> GetPublicationStatusAsync(AdapterContext context, string barcode, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<RemoteOperationRef>> ArchiveAsync(AdapterContext context, string payloadJson, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
