using System.Text.Json;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Scan A-1 / A-2 (13 Nov 2026): a variant's LIVE listing state is what its stock and price sends go by. A product
// update (refused, blocked, unclear or accepted) and a Hepsiburada send of only a card's new sizes must not take the
// variants already on sale out of LIVE.
[Collection("postgres-integration")]
public sealed class ListingSaleStatePostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    [Fact]
    public void Policy_KeepsLiveThroughAnUpdate_AndDropsItOnlyForAnIdentityConflict()
    {
        Assert.Equal("LIVE", ListingSaleStatePolicy.AfterUpdate("LIVE", "UPDATE_REJECTED"));
        Assert.Equal("LIVE", ListingSaleStatePolicy.AfterUpdate("LIVE", "APPROVAL_PENDING"));
        Assert.Equal("UPDATE_REJECTED", ListingSaleStatePolicy.AfterUpdate("APPROVAL_PENDING", "UPDATE_REJECTED"));
        Assert.Equal("LIVE", ListingSaleStatePolicy.AfterUnsettledApproval("LIVE", "MANUAL_REVIEW", "PRODUCT_APPROVAL_DEADLINE_EXPIRED"));
        Assert.Equal("MANUAL_REVIEW", ListingSaleStatePolicy.AfterUnsettledApproval("LIVE", "MANUAL_REVIEW", "HEPSIBURADA_PRODUCT_IDENTITY_CONFLICT"));
        Assert.Equal("MANUAL_REVIEW", ListingSaleStatePolicy.AfterUnsettledApproval("APPROVAL_PENDING", "MANUAL_REVIEW", null));
        Assert.Equal("LIVE", ListingSaleStatePolicy.AfterImportResult("LIVE", "APPROVAL_PENDING"));
        Assert.Equal("LIVE", ListingSaleStatePolicy.AfterImportResult("LIVE", "REJECTED"));
        Assert.Equal("MANUAL_REVIEW", ListingSaleStatePolicy.AfterImportResult("LIVE", "MANUAL_REVIEW"));
    }

    [PostgreSqlFact]
    public async Task ARefusedUpdate_KeepsTheCardsSizesLive_AndShowsTheRefusalOnTheRows()
    {
        var seed = await SeedTrendyolAsync("a1-refused", writesEnabled: true);
        var port = new Port
        {
            Answer = new RemoteOperationStatus("batch-a1", "COMPLETED",
            [
                new RemoteOperationLine("a1-refused-0", true, null, null, false),
                new RemoteOperationLine("a1-refused-1", false, null, "INVALID_DESI", false),
                new RemoteOperationLine("a1-refused-2", true, null, null, false)
            ])
        };

        Assert.Equal("PRODUCT_UPDATE_BATCH_PENDING", (await RunUpdateAsync(seed, port)).ErrorCode);
        var done = await RunToEndAsync(seed, port);

        Assert.Equal("PRODUCT_UPDATE_PARTIAL_FAILURE", done.ErrorCode);
        await using var db = fixture.CreateContext();
        var states = await db.MarketplaceListingStates.AsNoTracking().Where(x => x.TenantId == seed.TenantId).ToListAsync();
        Assert.All(states, state => Assert.Equal("LIVE", state.ActualStatus));
        Assert.Contains(states, state => state.LastRejectionCode == "INVALID_DESI");
        var rows = await db.ChannelListingVariants.AsNoTracking().Where(x => x.TenantId == seed.TenantId).ToListAsync();
        Assert.Single(rows, row => row.ActualStatus == "UPDATE_REJECTED");
        Assert.Equal("UPDATE_PARTIAL_FAILURE", (await db.ChannelListingProfiles.AsNoTracking().SingleAsync(x => x.Id == seed.ProfileId)).ActualStatus);
    }

    [PostgreSqlFact]
    public async Task ABlockedUpdate_KeepsTheCardsSizesLive()
    {
        var seed = await SeedTrendyolAsync("a1-blocked", writesEnabled: false);

        var result = await RunUpdateAsync(seed, new Port());

        Assert.Equal("EXTERNAL_WRITES_DISABLED", result.ErrorCode);
        await using var db = fixture.CreateContext();
        Assert.All(await db.MarketplaceListingStates.AsNoTracking().Where(x => x.TenantId == seed.TenantId).ToListAsync(), state => Assert.Equal("LIVE", state.ActualStatus));
        Assert.Equal("UPDATE_BLOCKED", (await db.ChannelListingProfiles.AsNoTracking().SingleAsync(x => x.Id == seed.ProfileId)).ActualStatus);
    }

    [PostgreSqlFact]
    public async Task AnAcceptedUpdate_WaitsForApprovalOnTheCard_WhileItsSizesStayLive()
    {
        var seed = await SeedTrendyolAsync("a1-accepted", writesEnabled: true);
        var port = new Port
        {
            Answer = new RemoteOperationStatus("batch-a1", "COMPLETED",
                [.. Enumerable.Range(0, 3).Select(size => new RemoteOperationLine($"a1-accepted-{size}", true, null, null, false))])
        };

        await RunUpdateAsync(seed, port);
        var done = await RunToEndAsync(seed, port);
        Assert.True(done.Succeeded, done.ErrorCode);

        await using var db = fixture.CreateContext();
        Assert.All(await db.MarketplaceListingStates.AsNoTracking().Where(x => x.TenantId == seed.TenantId).ToListAsync(), state => Assert.Equal("LIVE", state.ActualStatus));
        Assert.All(await db.ChannelListingVariants.AsNoTracking().Where(x => x.TenantId == seed.TenantId).ToListAsync(), row => Assert.Equal("APPROVAL_PENDING", row.ActualStatus));
        Assert.Equal("APPROVAL_PENDING", (await db.ChannelListingProfiles.AsNoTracking().SingleAsync(x => x.Id == seed.ProfileId)).ActualStatus);
    }

    [PostgreSqlFact]
    public async Task AHepsiburadaSendOfOnlyANewSize_SettlesThatSize_AndLeavesTheSizesOnSaleAlone()
    {
        var now = DateTimeOffset.UtcNow;
        var (tenantId, connectionId, profileId, productId, variantIds) = await SeedCardAsync("a2-new-size", "HEPSIBURADA", writesEnabled: true, sizes: 3, hashOf: size => size < 2 ? "H-OLD" : "H-NEW",
            listingStatusOf: size => size < 2 ? "LIVE" : "IMPORT_ACCEPTED");
        var jobId = Guid.CreateVersion7();
        var payloadJson = JsonSerializer.Serialize(new ProductApprovalReconciliationJobPayload(jobId, productId, profileId, "H-NEW", now.AddMinutes(-5), now.AddDays(7), "tracking-a2"));
        await using (var db = fixture.CreateContext())
        {
            db.IntegrationJobs.Add(new IntegrationJob { Id = jobId, TenantId = tenantId, ConnectionId = connectionId, JobType = MarketplaceJobTypes.ProductApprovalReconcile, PayloadJson = payloadJson, PayloadVersion = 1, PayloadHash = "h", JobDedupKey = $"pa:{jobId:N}", EffectIdempotencyKey = $"pa:{jobId:N}", Status = JobStatus.Pending, AvailableAt = now, CorrelationId = "pa", CreatedAt = now, Version = 1 });
            await db.SaveChangesAsync();
        }
        // The trackingId names only the size this file carried.
        var port = new Port { Answer = new RemoteOperationStatus("tracking-a2", "COMPLETED", [new RemoteOperationLine("a2-new-size-2", true, "HBV-2", null, false, "MATCHED")]) };

        await using var run = fixture.CreateContext();
        var processor = Processor(run, port);
        var result = await processor.ProcessAsync(tenantId, connectionId, MarketplaceJobTypes.ProductApprovalReconcile, payloadJson, "pa", CancellationToken.None, jobId);

        Assert.True(result.Succeeded, result.ErrorCode);
        await using var check = fixture.CreateContext();
        Assert.All(await check.MarketplaceListingStates.AsNoTracking().Where(x => x.TenantId == tenantId).ToListAsync(), state => Assert.Equal("LIVE", state.ActualStatus));
        Assert.Equal("LIVE", (await check.ChannelListingProfiles.AsNoTracking().SingleAsync(x => x.Id == profileId)).ActualStatus);
        Assert.Equal("HBV-2", (await check.MarketplaceVariantLinks.AsNoTracking().SingleAsync(x => x.TenantId == tenantId && x.VariantId == variantIds[2])).ExternalId);
    }

    private async Task<JobExecutionResult> RunUpdateAsync(TrendyolSeed seed, Port port)
    {
        await using var db = fixture.CreateContext();
        var job = await db.IntegrationJobs.AsNoTracking().SingleAsync(x => x.Id == seed.JobId);
        return await Processor(db, port).ProcessAsync(seed.TenantId, seed.ConnectionId, MarketplaceJobTypes.ProductUpdate, job.PayloadJson, job.CorrelationId, CancellationToken.None, job.Id);
    }

    // The job's later steps (the empty delivery step is skipped) until it settles.
    private async Task<JobExecutionResult> RunToEndAsync(TrendyolSeed seed, Port port)
    {
        for (var step = 0; ; step++)
        {
            var result = await RunUpdateAsync(seed, port);
            if (result.ErrorCode != "PRODUCT_UPDATE_NEXT_PHASE" || step > 5) return result;
        }
    }

    private MarketplaceJobProcessor Processor(AppDbContext db, Port port) =>
        new(db, null!, null!, port, null!, null!, null!, null!, null!, null!, null!,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["FeatureFlags:ExternalWrites"] = "true" }).Build(), fixture.TimeProvider);

    private sealed record TrendyolSeed(Guid TenantId, Guid ConnectionId, Guid ProfileId, Guid JobId);

    // A Trendyol card of three sizes, all on sale, and its "Platformlarda Güncelle" job (the variant step: desi).
    private async Task<TrendyolSeed> SeedTrendyolAsync(string code, bool writesEnabled)
    {
        var (tenantId, connectionId, profileId, productId, _) = await SeedCardAsync(code, "TRENDYOL", writesEnabled, sizes: 3, hashOf: _ => "H-UPDATE", listingStatusOf: _ => "LIVE");
        var now = DateTimeOffset.UtcNow;
        var jobId = Guid.CreateVersion7();
        var variants = $$"""{"items":[{{string.Join(",", Enumerable.Range(0, 3).Select(size => $$"""{"barcode":"{{code}}-{{size}}","dimensionalWeight":2}"""))}}]}""";
        var payload = new ProductUpdateJobPayload(jobId, productId, profileId, "SUBMIT_VARIANTS", "APPROVED", "H-UPDATE", "{}", """{"items":[]}""", variants, """{"items":[]}""", null, null);
        await using var db = fixture.CreateContext();
        db.IntegrationJobs.Add(new IntegrationJob { Id = jobId, TenantId = tenantId, ConnectionId = connectionId, JobType = MarketplaceJobTypes.ProductUpdate, PayloadJson = JsonSerializer.Serialize(payload), PayloadVersion = 1, PayloadHash = "h", JobDedupKey = $"pu:{jobId:N}", EffectIdempotencyKey = $"pu:{jobId:N}", Status = JobStatus.Pending, AvailableAt = now, CorrelationId = "pu", CreatedAt = now, Version = 1 });
        await db.SaveChangesAsync();
        return new(tenantId, connectionId, profileId, jobId);
    }

    private async Task<(Guid TenantId, Guid ConnectionId, Guid ProfileId, Guid ProductId, IReadOnlyList<Guid> VariantIds)> SeedCardAsync(string code, string platform, bool writesEnabled, int sizes, Func<int, string> hashOf, Func<int, string> listingStatusOf)
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"{code}-{Guid.NewGuid():N}", DisplayName = code, Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var connection = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = platform, Environment = "PRODUCTION", DisplayName = code, ExternalStoreId = $"store-{Guid.NewGuid():N}", Status = "ACTIVE", ApiVersion = "V2", SettingsJson = writesEnabled ? "{\"ExternalWritesEnabled\":true}" : "{\"ExternalWritesEnabled\":false}", Version = 1 };
        var product = new Product { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Title = "Kazak", Description = "Triko", Status = ProductStatus.Active, CreatedAt = now, UpdatedAt = now };
        var profile = new ChannelListingProfile { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, ProductId = product.Id, Enabled = true, DesiredStatus = "LIVE", ActualStatus = "LIVE" };
        await using var db = fixture.CreateContext();
        db.Tenants.Add(tenant);
        db.PlatformConnections.Add(connection);
        db.Products.Add(product);
        db.ChannelListingProfiles.Add(profile);
        var variantIds = new List<Guid>();
        for (var size = 0; size < sizes; size++)
        {
            var barcode = $"{code}-{size}";
            var variant = new ProductVariant { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProductId = product.Id, Sku = barcode, SkuNormalized = barcode, Barcode = barcode, BarcodeNormalized = barcode, ModelCode = code, OptionSignature = barcode, CreatedAt = now, UpdatedAt = now };
            db.ProductVariants.Add(variant);
            variantIds.Add(variant.Id);
            var status = listingStatusOf(size);
            db.ChannelListingVariants.Add(new ChannelListingVariant { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProfileId = profile.Id, VariantId = variant.Id, ExternalSku = barcode, ExternalBarcode = barcode, DesiredStatus = "LIVE", ActualStatus = status });
            db.MarketplaceListingStates.Add(new MarketplaceListingState { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, VariantId = variant.Id, DesiredStatus = "LIVE", ActualStatus = status, PayloadHash = hashOf(size) });
        }
        await db.SaveChangesAsync();
        return (tenant.Id, connection.Id, profile.Id, product.Id, variantIds);
    }

    private sealed class Port : IProductPort
    {
        public RemoteOperationStatus? Answer { get; set; }

        public Task<AdapterResult<RemoteOperationRef>> UpdateApprovedVariantsAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) =>
            Task.FromResult(AdapterResult<RemoteOperationRef>.Success(new RemoteOperationRef("batch-a1", "PRODUCT_UPDATE_VARIANTS_V2", DateTimeOffset.UtcNow.AddMinutes(-1))));

        public Task<AdapterResult<RemoteOperationStatus>> GetOperationAsync(AdapterContext context, string externalOperationId, CancellationToken cancellationToken) =>
            Task.FromResult(AdapterResult<RemoteOperationStatus>.Success(Answer ?? new RemoteOperationStatus(externalOperationId, "IN_PROGRESS", [])));

        public Task<AdapterResult<AdapterPageResult<RemoteCatalogProduct>>> ListCatalogAsync(AdapterContext context, AdapterPageRequest page, ProductReadFilter filter, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<AdapterPageResult<RemoteProduct>>> ListAsync(AdapterContext context, AdapterPageRequest page, ProductReadFilter filter, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<RemoteOperationRef>> CreateAsync(AdapterContext context, ProductPublication publication, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<RemoteOperationRef>> UpdateUnapprovedAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<RemoteOperationRef>> UpdateApprovedContentAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<RemoteOperationRef>> UpdateApprovedDeliveryAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<RemotePublicationStatus>> GetPublicationStatusAsync(AdapterContext context, string barcode, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdapterResult<RemoteOperationRef>> ArchiveAsync(AdapterContext context, string payloadJson, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
