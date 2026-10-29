using System.Text.Json;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Audit 29 Oct 2026, T-1: a Hepsiburada inventory upload that failed must not record the prices and stock it carried as
// sent. Done lists the refused SKUs alone (a SKU it leaves out was applied); Failed proves nothing of the SKUs it leaves out.
[Collection("postgres-integration")]
public sealed class HepsiburadaPriceInventoryResultPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>, IAsyncLifetime
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
    public async Task An_upload_refused_whole_records_no_line_as_sent()
    {
        var seed = await Seed();

        var result = await Poll(seed, new RemoteOperationStatus("LISTING_INVENTORY:71", "REJECTED", []));

        Assert.Equal(JobCompletionKind.Blocked, result.Kind);
        Assert.Equal("PRICE_INVENTORY_REJECTED", result.ErrorCode);
        await using var check = fixture.CreateContext();
        Assert.All(await Offers(check, seed), offer => Assert.Null(offer.LastPriceHash));
    }

    [PostgreSqlFact]
    public async Task A_failed_upload_naming_one_sku_leaves_the_other_unconfirmed()
    {
        var seed = await Seed();

        var result = await Poll(seed, new RemoteOperationStatus("LISTING_INVENTORY:71", "COMPLETED",
            [new RemoteOperationLine("HB-SKU-1", false, "HBV00001", "OutOfPriceRange", false, "REJECTED")], UnlistedLinesUnconfirmed: true));

        Assert.Equal(JobCompletionKind.Blocked, result.Kind);
        Assert.Equal("PRICE_INVENTORY_REJECTED", result.ErrorCode);
        await using var check = fixture.CreateContext();
        Assert.All(await Offers(check, seed), offer => Assert.Null(offer.LastPriceHash));
        Assert.True(await check.OperationalIssues.AnyAsync(x => x.TenantId == seed.Tenant.Id && x.Code == "PRICE_INVENTORY_UPLOAD_FAILED"));
    }

    [PostgreSqlFact]
    public async Task A_done_upload_naming_one_sku_records_the_other_as_sent()
    {
        var seed = await Seed();

        var result = await Poll(seed, new RemoteOperationStatus("LISTING_INVENTORY:71", "COMPLETED",
            [new RemoteOperationLine("HB-SKU-1", false, "HBV00001", "OutOfPriceRange", false, "REJECTED")]));

        Assert.Equal(JobCompletionKind.ManualReview, result.Kind);
        Assert.Equal("PRICE_INVENTORY_PARTIAL_FAILURE", result.ErrorCode);
        await using var check = fixture.CreateContext();
        var offers = await Offers(check, seed);
        Assert.Null(offers.Single(x => x.Id == seed.Lines[0].OfferId).LastPriceHash);
        Assert.Equal("hash-2", offers.Single(x => x.Id == seed.Lines[1].OfferId).LastPriceHash);
    }

    private sealed record Seeded(Tenant Tenant, PlatformConnection Connection, IReadOnlyList<PriceInventoryPushLine> Lines);

    // A Hepsiburada connection with two offers whose price and stock are current for the job's lines.
    private async Task<Seeded> Seed()
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"hb-price-result-{Guid.NewGuid():N}", DisplayName = "hb-price-result", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        CreatedTenants.Add(tenant.Id);
        var connection = new PlatformConnection
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            PublicId = Guid.CreateVersion7(),
            PlatformCode = "HEPSIBURADA",
            Environment = "PRODUCTION",
            DisplayName = "Hepsiburada",
            ExternalStoreId = $"merchant-{Guid.NewGuid():N}",
            Status = "ACTIVE",
            ApiVersion = "V1",
            SettingsJson = "{\"ExternalWritesEnabled\":true}"
        };
        await using var db = fixture.CreateContext();
        db.Tenants.Add(tenant);
        db.PlatformConnections.Add(connection);
        var sent = new List<(ProductVariant Variant, InventoryItem Inventory, ChannelOffer Offer)>();
        for (var index = 1; index <= 2; index++)
        {
            var product = new Product { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Title = $"Ürün {index}", Description = "Fiyat-stok sonucu", Status = ProductStatus.Active, CreatedAt = now, UpdatedAt = now };
            var variant = new ProductVariant { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProductId = product.Id, Sku = $"HB-SKU-{index}", SkuNormalized = $"HB-SKU-{index}", Barcode = $"86910000000{index}", BarcodeNormalized = $"86910000000{index}", OptionSignature = $"HB-SKU-{index}", CreatedAt = now, UpdatedAt = now };
            var inventory = new InventoryItem { Id = Guid.CreateVersion7(), TenantId = tenant.Id, VariantId = variant.Id, LocationCode = "MAIN", OnHand = 5m, Available = 5m };
            var offer = new ChannelOffer { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = connection.Id, VariantId = variant.Id, ListPrice = 150m, SalePrice = 120m, Currency = "TRY", VatRate = 20m, VatInclusion = "INCLUDED", RoundingMode = "HALF_EVEN", Status = "ACTIVE" };
            db.Products.Add(product);
            db.ProductVariants.Add(variant);
            db.InventoryItems.Add(inventory);
            db.ChannelOffers.Add(offer);
            sent.Add((variant, inventory, offer));
        }
        await db.SaveChangesAsync();
        // The versions as stored, so the lines are current when the poll checks them.
        var lines = sent.Select((item, index) => new PriceInventoryPushLine(item.Variant.Id, item.Offer.Id, item.Variant.Barcode!, 5m, 150m, 120m, "TRY",
            item.Inventory.ProjectionVersion, item.Offer.PriceVersion, $"hash-{index + 1}", item.Variant.Sku, $"HBV0000{index + 1}")).ToList();
        return new(tenant, connection, lines);
    }

    private async Task<JobExecutionResult> Poll(Seeded seed, RemoteOperationStatus operation)
    {
        var jobId = Guid.CreateVersion7();
        var payloadJson = JsonSerializer.Serialize(new PriceInventoryJobPayload(jobId, seed.Connection.Id, "POLL", "hash", "{}", seed.Lines, operation.ExternalOperationId, DateTimeOffset.UtcNow.AddMinutes(-1)));
        await using (var setup = fixture.CreateContext())
        {
            setup.IntegrationJobs.Add(new IntegrationJob { Id = jobId, TenantId = seed.Tenant.Id, ConnectionId = seed.Connection.Id, JobType = MarketplaceJobTypes.PriceInventorySync, PayloadJson = payloadJson, PayloadVersion = 1, PayloadHash = "h", JobDedupKey = $"pi:{jobId:N}", EffectIdempotencyKey = $"pi:{jobId:N}", Status = JobStatus.Leased, AvailableAt = DateTimeOffset.UtcNow, CorrelationId = "pi", CreatedAt = DateTimeOffset.UtcNow, Version = 1 });
            await setup.SaveChangesAsync();
        }
        await using var db = fixture.CreateContext();
        var processor = new MarketplaceJobProcessor(db, null!, null!, new OperationPort(operation), null!, null!, null!, null!, null!, null!, null!,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["FeatureFlags:ExternalWrites"] = "true" }).Build(), fixture.TimeProvider);
        return await processor.ProcessAsync(seed.Tenant.Id, seed.Connection.Id, MarketplaceJobTypes.PriceInventorySync, payloadJson, "pi", CancellationToken.None, jobId);
    }

    private static Task<List<ChannelOffer>> Offers(AppDbContext db, Seeded seed) =>
        db.ChannelOffers.AsNoTracking().Where(x => x.TenantId == seed.Tenant.Id && x.ConnectionId == seed.Connection.Id).ToListAsync();

    // The marketplace: a price/stock poll answers the given operation; nothing else is asked.
    private sealed class OperationPort(RemoteOperationStatus operation) : IProductPort
    {
        public Task<AdapterResult<RemoteOperationStatus>> GetOperationAsync(AdapterContext context, string externalOperationId, CancellationToken cancellationToken) =>
            Task.FromResult(AdapterResult<RemoteOperationStatus>.Success(operation));

        public Task<AdapterResult<AdapterPageResult<RemoteCatalogProduct>>> ListCatalogAsync(AdapterContext context, AdapterPageRequest page, ProductReadFilter filter, CancellationToken cancellationToken) => throw new NotSupportedException();
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
