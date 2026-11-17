using System.Text.Json;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Live MZ056 Mürdüm/Kahverengi (14.11.2026): a card imported from Trendyol's unapproved list is linked under its
// productMainId and its variants under their barcodes. Sent again and approved, the approval check read Trendyol's numeric
// contentIds and variantIds and stopped with PRODUCT_APPROVAL_IDENTITY_CONFLICT. Such placeholders now give way to the
// approved ids; a link that already holds a real Trendyol id still stops for review.
[Collection("postgres-integration")]
public sealed class TrendyolPlaceholderLinkApprovalTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>, IAsyncLifetime
{
    private static readonly System.Collections.Concurrent.ConcurrentBag<Guid> CreatedTenants = [];

    public Task InitializeAsync() => Task.CompletedTask;

    // The approval jobs these tests queue are removed, so the lease tests of a later class never take one.
    public async Task DisposeAsync()
    {
        var tenantIds = CreatedTenants.ToArray();
        if (tenantIds.Length == 0) return;
        await using var db = fixture.CreateContext();
        await db.JobAttempts.Where(x => tenantIds.Contains(x.TenantId)).ExecuteDeleteAsync();
        await db.IntegrationJobs.Where(x => tenantIds.Contains(x.TenantId)).ExecuteDeleteAsync();
        await db.IntegrationOutboxEvents.Where(x => tenantIds.Contains(x.TenantId)).ExecuteDeleteAsync();
    }

    [Fact]
    public void Policy_ReadsOnlyNumbersAsRealTrendyolIds()
    {
        Assert.True(ProductApprovalReconciliationPolicy.IsTrendyolPlaceholderId("MZ056"));
        Assert.True(ProductApprovalReconciliationPolicy.IsTrendyolPlaceholderId("MZ056-13"));
        Assert.False(ProductApprovalReconciliationPolicy.IsTrendyolPlaceholderId("1239063613"));
        Assert.False(ProductApprovalReconciliationPolicy.IsTrendyolPlaceholderId(" 1741768055 "));
        Assert.False(ProductApprovalReconciliationPolicy.IsTrendyolPlaceholderId(null));
        Assert.False(ProductApprovalReconciliationPolicy.IsTrendyolPlaceholderId(" "));
    }

    [PostgreSqlFact]
    public async Task APlaceholderCard_TakesTheApprovedContentsAndVariantIds_AndGoesLive()
    {
        var seed = await SeedAsync("ph-ok", productLink: "PHOK", variantLinkOf: barcode => barcode);
        // Two colours, so Trendyol files them under two contents (one per colour).
        var port = new Port(barcode => barcode.EndsWith("-0") || barcode.EndsWith("-1") ? "9100" : "9200");

        var result = await RunAsync(seed, port);

        Assert.True(result.Succeeded, result.ErrorCode);
        await using var db = fixture.CreateContext();
        var profile = await db.ChannelListingProfiles.AsNoTracking().SingleAsync(x => x.Id == seed.ProfileId);
        Assert.Equal("LIVE", profile.ActualStatus);
        Assert.NotEqual("PRODUCT_APPROVAL_IDENTITY_CONFLICT", profile.LastRejectionCode);
        var links = await db.MarketplaceVariantLinks.AsNoTracking().Where(x => x.TenantId == seed.TenantId && x.ConnectionId == seed.ConnectionId).ToListAsync();
        Assert.Equal(4, links.Count);
        Assert.All(links, link => Assert.False(ProductApprovalReconciliationPolicy.IsTrendyolPlaceholderId(link.ExternalId), link.ExternalId));
        var productLink = await db.MarketplaceProductLinks.AsNoTracking().SingleAsync(x => x.TenantId == seed.TenantId && x.ProductId == seed.ProductId);
        Assert.Contains(productLink.ExternalId, new[] { "9100", "9200" });
        Assert.All(await db.MarketplaceListingStates.AsNoTracking().Where(x => x.TenantId == seed.TenantId).ToListAsync(), state => Assert.Equal("LIVE", state.ActualStatus));
    }

    [PostgreSqlFact]
    public async Task APlaceholderCard_WhoseColourTrendyolFiledIntoASiblingsContent_GoesLiveWithoutTheModelCodeLink()
    {
        var seed = await SeedAsync("ph-merge", productLink: "PHMERGE", variantLinkOf: barcode => barcode, siblingContent: "9300");
        var port = new Port(_ => "9300");

        var result = await RunAsync(seed, port);

        Assert.True(result.Succeeded, result.ErrorCode);
        await using var db = fixture.CreateContext();
        Assert.Equal("LIVE", (await db.ChannelListingProfiles.AsNoTracking().SingleAsync(x => x.Id == seed.ProfileId)).ActualStatus);
        Assert.False(await db.MarketplaceProductLinks.AsNoTracking().AnyAsync(x => x.TenantId == seed.TenantId && x.ProductId == seed.ProductId));
        Assert.All(await db.MarketplaceVariantLinks.AsNoTracking().Where(x => x.TenantId == seed.TenantId && x.VariantId != seed.SiblingVariantId).ToListAsync(), link => Assert.False(ProductApprovalReconciliationPolicy.IsTrendyolPlaceholderId(link.ExternalId), link.ExternalId));
    }

    [PostgreSqlFact]
    public async Task ACardLinkedToAnotherRealContent_StillStopsForReview()
    {
        var seed = await SeedAsync("ph-real", productLink: "5555", variantLinkOf: barcode => barcode);
        var port = new Port(_ => "9400");

        var result = await RunAsync(seed, port);

        Assert.Equal("PRODUCT_APPROVAL_IDENTITY_CONFLICT", result.ErrorCode);
        await using var db = fixture.CreateContext();
        Assert.Equal("5555", (await db.MarketplaceProductLinks.AsNoTracking().SingleAsync(x => x.TenantId == seed.TenantId && x.ProductId == seed.ProductId)).ExternalId);
    }

    [PostgreSqlFact]
    public async Task AVariantLinkedToAnotherRealVariantId_StillStopsForReview()
    {
        var seed = await SeedAsync("ph-variant", productLink: "PHVAR", variantLinkOf: barcode => barcode.EndsWith("-0") ? "777" : barcode);
        var port = new Port(_ => "9500");

        var result = await RunAsync(seed, port);

        Assert.Equal("PRODUCT_APPROVAL_IDENTITY_CONFLICT", result.ErrorCode);
        await using var db = fixture.CreateContext();
        Assert.Equal("PHVAR", (await db.MarketplaceProductLinks.AsNoTracking().SingleAsync(x => x.TenantId == seed.TenantId && x.ProductId == seed.ProductId)).ExternalId);
    }

    private async Task<JobExecutionResult> RunAsync(Seed seed, Port port)
    {
        await using var db = fixture.CreateContext();
        var processor = new MarketplaceJobProcessor(db, null!, null!, port, null!, null!, null!, null!, null!, null!, null!,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["FeatureFlags:ExternalWrites"] = "true" }).Build(), fixture.TimeProvider);
        return await processor.ProcessAsync(seed.TenantId, seed.ConnectionId, MarketplaceJobTypes.ProductApprovalReconcile, seed.PayloadJson, "pa", CancellationToken.None, seed.JobId);
    }

    private sealed record Seed(Guid TenantId, Guid ConnectionId, Guid ProfileId, Guid ProductId, Guid JobId, string PayloadJson, Guid? SiblingVariantId);

    // A Trendyol card of four variants sent again and waiting for approval, linked as the unapproved-list import links
    // it; optionally a sibling card of the same model already holding a content.
    private async Task<Seed> SeedAsync(string code, string productLink, Func<string, string> variantLinkOf, string? siblingContent = null)
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"{code}-{Guid.NewGuid():N}", DisplayName = code, Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var connection = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = "TRENDYOL", Environment = "PRODUCTION", DisplayName = code, ExternalStoreId = $"store-{Guid.NewGuid():N}", Status = "ACTIVE", ApiVersion = "V2", SettingsJson = "{\"ExternalWritesEnabled\":true}", Version = 1 };
        var product = new Product { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Title = "Bluz", Description = "Penye", Status = ProductStatus.Active, CreatedAt = now, UpdatedAt = now };
        var profile = new ChannelListingProfile { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, ProductId = product.Id, Enabled = true, DesiredStatus = "LIVE", ActualStatus = "APPROVAL_PENDING" };
        CreatedTenants.Add(tenant.Id);
        await using var db = fixture.CreateContext();
        db.Tenants.Add(tenant);
        db.PlatformConnections.Add(connection);
        db.Products.Add(product);
        db.ChannelListingProfiles.Add(profile);
        db.MarketplaceProductLinks.Add(new MarketplaceProductLink { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, ProductId = product.Id, ExternalId = productLink, Version = 1 });
        for (var size = 0; size < 4; size++)
        {
            var barcode = $"{code}-{size}";
            var variant = new ProductVariant { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProductId = product.Id, Sku = barcode, SkuNormalized = barcode, Barcode = barcode, BarcodeNormalized = barcode, ModelCode = code, OptionSignature = barcode, CreatedAt = now, UpdatedAt = now };
            db.ProductVariants.Add(variant);
            db.ChannelListingVariants.Add(new ChannelListingVariant { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProfileId = profile.Id, VariantId = variant.Id, ExternalSku = barcode, ExternalBarcode = barcode, DesiredStatus = "LIVE", ActualStatus = "APPROVAL_PENDING" });
            db.MarketplaceListingStates.Add(new MarketplaceListingState { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, VariantId = variant.Id, DesiredStatus = "LIVE", ActualStatus = "APPROVAL_PENDING", PayloadHash = "H-SENT" });
            db.MarketplaceVariantLinks.Add(new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, VariantId = variant.Id, ExternalId = variantLinkOf(barcode), ExternalBarcode = barcode, ExternalSku = barcode, Version = 1 });
        }
        Guid? siblingVariantId = null;
        if (siblingContent is not null)
        {
            var sibling = new Product { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Title = "Bluz", Description = "Penye", Status = ProductStatus.Active, CreatedAt = now, UpdatedAt = now };
            var siblingBarcode = $"{code}-S";
            var siblingVariant = new ProductVariant { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProductId = sibling.Id, Sku = siblingBarcode, SkuNormalized = siblingBarcode, Barcode = siblingBarcode, BarcodeNormalized = siblingBarcode, ModelCode = code, OptionSignature = siblingBarcode, CreatedAt = now, UpdatedAt = now };
            siblingVariantId = siblingVariant.Id;
            db.Products.Add(sibling);
            db.ProductVariants.Add(siblingVariant);
            db.MarketplaceProductLinks.Add(new MarketplaceProductLink { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, ProductId = sibling.Id, ExternalId = siblingContent, Version = 1 });
            db.MarketplaceVariantLinks.Add(new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, VariantId = siblingVariant.Id, ExternalId = "1", Version = 1 });
        }
        var jobId = Guid.CreateVersion7();
        var payloadJson = JsonSerializer.Serialize(new ProductApprovalReconciliationJobPayload(jobId, product.Id, profile.Id, "H-SENT", now.AddMinutes(-10), now.AddDays(7), null));
        db.IntegrationJobs.Add(new IntegrationJob { Id = jobId, TenantId = tenant.Id, ConnectionId = connection.Id, JobType = MarketplaceJobTypes.ProductApprovalReconcile, PayloadJson = payloadJson, PayloadVersion = 1, PayloadHash = "h", JobDedupKey = $"pa:{jobId:N}", EffectIdempotencyKey = $"pa:{jobId:N}", Status = JobStatus.Pending, AvailableAt = now, CorrelationId = "pa", CreatedAt = now, Version = 1 });
        await db.SaveChangesAsync();
        return new(tenant.Id, connection.Id, profile.Id, product.Id, jobId, payloadJson, siblingVariantId);
    }

    // Trendyol's approved-list answer per barcode: the content the colour was filed under and a numeric variant id.
    private sealed class Port(Func<string, string> contentOf) : IProductPort
    {
        public Task<AdapterResult<RemotePublicationStatus>> GetPublicationStatusAsync(AdapterContext context, string barcode, CancellationToken cancellationToken) =>
            Task.FromResult(AdapterResult<RemotePublicationStatus>.Success(new RemotePublicationStatus(barcode, "APPROVED", contentOf(barcode), $"{contentOf(barcode)}{barcode[^1]}", null, "{}")));

        public Task<AdapterResult<RemoteOperationRef>> UpdateApprovedVariantsAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<RemoteOperationStatus>> GetOperationAsync(AdapterContext context, string externalOperationId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<AdapterPageResult<RemoteCatalogProduct>>> ListCatalogAsync(AdapterContext context, AdapterPageRequest page, ProductReadFilter filter, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<AdapterPageResult<RemoteProduct>>> ListAsync(AdapterContext context, AdapterPageRequest page, ProductReadFilter filter, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<RemoteOperationRef>> CreateAsync(AdapterContext context, ProductPublication publication, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<RemoteOperationRef>> UpdateUnapprovedAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<RemoteOperationRef>> UpdateApprovedContentAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<RemoteOperationRef>> UpdateApprovedDeliveryAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<RemoteOperationRef>> ArchiveAsync(AdapterContext context, string payloadJson, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
