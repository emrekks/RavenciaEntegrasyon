using System.Text.Json;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// The price/stock send log on PostgreSQL: the submit records one row per line of the request with the values the
// marketplace had confirmed, the poll settles every row in its own save (partial failure, superseded, a refusal of the
// whole request), the page reads it back filtered, and the retention removes rows older than 90 days.
[Collection("postgres-integration")]
public sealed class PriceStockSendLogPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>, IAsyncLifetime
{
    private const string OperationId = "LISTING_INVENTORY:81";
    private static readonly System.Collections.Concurrent.ConcurrentBag<Guid> CreatedTenants = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        var tenantIds = CreatedTenants.ToArray();
        if (tenantIds.Length == 0) return;
        await using var db = fixture.CreateContext();
        await db.PriceStockSendLogs.Where(x => tenantIds.Contains(x.TenantId)).ExecuteDeleteAsync();
        await db.JobAttempts.Where(x => tenantIds.Contains(x.TenantId)).ExecuteDeleteAsync();
        await db.IntegrationJobs.Where(x => tenantIds.Contains(x.TenantId)).ExecuteDeleteAsync();
    }

    [PostgreSqlFact]
    public async Task The_submit_records_each_line_against_the_confirmed_values()
    {
        var seed = await Seed();

        await Submit(seed);

        await using var check = fixture.CreateContext();
        var rows = await Rows(check, seed);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row =>
        {
            Assert.Equal(PriceStockSendResults.Submitted, row.Result);
            Assert.Equal(PriceStockSendTriggers.PanelSend, row.Trigger);
            Assert.Equal((6m, 5m), (row.PreviousQuantity, row.NewQuantity));
            Assert.Equal((130m, 120m), (row.PreviousSalePrice, row.NewSalePrice));
            Assert.Equal((OperationId, "HEPSIBURADA", seed.Job.Id), (row.ExternalRequestId, row.PlatformCode, row.JobId));
            Assert.Null(row.CompletedAt);
        });
    }

    [PostgreSqlFact]
    public async Task A_partial_failure_settles_the_refused_line_and_the_applied_one()
    {
        var seed = await Seed();
        await Submit(seed);

        var result = await Poll(seed, new RemoteOperationStatus(OperationId, "COMPLETED",
            [new RemoteOperationLine("HB-SKU-1", false, "HBV00001", "OutOfPriceRange", false, "Fiyat aralık dışında")]));

        Assert.Equal("PRICE_INVENTORY_PARTIAL_FAILURE", result.ErrorCode);
        await using var check = fixture.CreateContext();
        var rows = await Rows(check, seed);
        var refused = rows.Single(x => x.OfferId == seed.Lines[0].OfferId);
        var applied = rows.Single(x => x.OfferId == seed.Lines[1].OfferId);
        Assert.Equal((PriceStockSendResults.Rejected, "OutOfPriceRange", "Fiyat aralık dışında"), (refused.Result, refused.ErrorCode, refused.ErrorMessage));
        Assert.Equal(PriceStockSendResults.Applied, applied.Result);
        Assert.All(rows, row => Assert.NotNull(row.CompletedAt));
    }

    [PostgreSqlFact]
    public async Task A_failed_upload_marks_the_unanswered_line_failed()
    {
        var seed = await Seed();
        await Submit(seed);

        await Poll(seed, new RemoteOperationStatus(OperationId, "COMPLETED",
            [new RemoteOperationLine("HB-SKU-1", false, "HBV00001", "OutOfPriceRange", false, "REJECTED")], UnlistedLinesUnconfirmed: true));

        await using var check = fixture.CreateContext();
        var rows = await Rows(check, seed);
        Assert.Equal((PriceStockSendResults.Rejected, (string?)null), rows.Where(x => x.OfferId == seed.Lines[0].OfferId).Select(x => (x.Result, x.ErrorMessage)).Single());
        Assert.Equal((PriceStockSendResults.Failed, "PRICE_INVENTORY_UPLOAD_FAILED"), rows.Where(x => x.OfferId == seed.Lines[1].OfferId).Select(x => (x.Result, x.ErrorCode)).Single());
    }

    [PostgreSqlFact]
    public async Task A_request_refused_whole_settles_every_row()
    {
        var seed = await Seed();
        await Submit(seed);

        await Poll(seed, new RemoteOperationStatus(OperationId, "REJECTED", []));

        await using var check = fixture.CreateContext();
        Assert.All(await Rows(check, seed), row => Assert.Equal((PriceStockSendResults.Rejected, "PRICE_INVENTORY_REJECTED"), (row.Result, row.ErrorCode)));
    }

    [PostgreSqlFact]
    public async Task A_newer_price_before_the_answer_marks_every_row_superseded()
    {
        var seed = await Seed();
        await Submit(seed);
        await using (var change = fixture.CreateContext())
            await change.ChannelOffers.Where(x => x.Id == seed.Lines[0].OfferId).ExecuteUpdateAsync(set => set.SetProperty(x => x.PriceVersion, x => x.PriceVersion + 1));

        var result = await Poll(seed, new RemoteOperationStatus(OperationId, "COMPLETED", []));

        Assert.Equal("PRICE_INVENTORY_SUPERSEDED", result.ErrorCode);
        await using var check = fixture.CreateContext();
        Assert.All(await Rows(check, seed), row => Assert.Equal(PriceStockSendResults.Superseded, row.Result));
        // The answer was not applied: no offer is marked as sent.
        Assert.All(await check.ChannelOffers.AsNoTracking().Where(x => x.TenantId == seed.Tenant.Id).ToListAsync(), offer => Assert.Null(offer.LastPriceHash));
    }

    [PostgreSqlFact]
    public async Task The_page_filters_by_product_and_result_and_pages_by_cursor()
    {
        var seed = await Seed();
        await Submit(seed);
        await Poll(seed, new RemoteOperationStatus(OperationId, "COMPLETED",
            [new RemoteOperationLine("HB-SKU-1", false, "HBV00001", "OutOfPriceRange", false, "REJECTED")]));
        await using var db = fixture.CreateContext();
        var service = new PriceStockSendLogService(db);

        var byTitle = await service.PageAsync(seed.Tenant.Id, new PriceStockSendLogQuery(Search: $"Ürün 2 {seed.Tag}"), CancellationToken.None);
        var refused = await service.PageAsync(seed.Tenant.Id, new PriceStockSendLogQuery(Results: ["rejected"], Platforms: ["HEPSIBURADA"]), CancellationToken.None);
        var first = await service.PageAsync(seed.Tenant.Id, new PriceStockSendLogQuery(Limit: 1), CancellationToken.None);
        var second = await service.PageAsync(seed.Tenant.Id, new PriceStockSendLogQuery(Limit: 1, After: first.Value!.NextCursor), CancellationToken.None);
        var unknownResult = await service.PageAsync(seed.Tenant.Id, new PriceStockSendLogQuery(Results: ["NOPE"]), CancellationToken.None);

        var titled = Assert.Single(byTitle.Value!.Items);
        Assert.Equal((seed.Lines[1].VariantId, $"Ürün 2 {seed.Tag}", "Hepsiburada"), (titled.VariantId, titled.ProductTitle, titled.StoreName));
        Assert.Equal(seed.Lines[0].VariantId, Assert.Single(refused.Value!.Items).VariantId);
        Assert.True(first.Value.HasMore);
        Assert.False(second.Value!.HasMore);
        Assert.NotEqual(first.Value.Items[0].Id, Assert.Single(second.Value.Items).Id);
        Assert.Empty(unknownResult.Value!.Items);
    }

    [PostgreSqlFact]
    public async Task The_retention_removes_rows_older_than_ninety_days_only()
    {
        var seed = await Seed();
        var now = DateTimeOffset.UtcNow;
        await using (var db = fixture.CreateContext())
        {
            db.PriceStockSendLogs.Add(PriceStockSendLogPolicy.Row(seed.Tenant.Id, seed.Connection.Id, "HEPSIBURADA", Guid.CreateVersion7(), seed.Lines[0], null, PriceStockSendTriggers.Order, "old", now.AddDays(-91), PriceStockSendResults.Applied));
            db.PriceStockSendLogs.Add(PriceStockSendLogPolicy.Row(seed.Tenant.Id, seed.Connection.Id, "HEPSIBURADA", Guid.CreateVersion7(), seed.Lines[0], null, PriceStockSendTriggers.Order, "recent", now.AddDays(-89), PriceStockSendResults.Applied));
            await db.SaveChangesAsync();
        }

        await using (var db = fixture.CreateContext())
            Assert.True(await new JobRetentionService(db, fixture.TimeProvider).PurgePriceStockSendLogAsync(PriceStockSendLogPolicy.Retention, 5, CancellationToken.None) >= 1);

        await using var check = fixture.CreateContext();
        Assert.Equal(["recent"], await check.PriceStockSendLogs.Where(x => x.TenantId == seed.Tenant.Id).Select(x => x.ExternalRequestId!).ToListAsync());
    }

    private sealed record Seeded(Tenant Tenant, PlatformConnection Connection, IntegrationJob Job, string Tag, IReadOnlyList<PriceInventoryPushLine> Lines);

    // A Hepsiburada connection with two offers whose price and stock are current for the job's lines, each with values
    // the marketplace confirmed before (6 pieces at 130/150).
    private async Task<Seeded> Seed()
    {
        var now = DateTimeOffset.UtcNow;
        var tag = Guid.NewGuid().ToString("N")[..8];
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"send-log-{Guid.NewGuid():N}", DisplayName = "send-log", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
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
            var product = new Product { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Title = $"Ürün {index} {tag}", Description = "Gönderim geçmişi", Status = ProductStatus.Active, CreatedAt = now, UpdatedAt = now };
            var variant = new ProductVariant { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProductId = product.Id, Sku = $"HB-SKU-{index}", SkuNormalized = $"HB-SKU-{index}", Barcode = $"86920000000{index}", BarcodeNormalized = $"86920000000{index}", OptionSignature = $"HB-SKU-{index}", CreatedAt = now, UpdatedAt = now };
            var inventory = new InventoryItem { Id = Guid.CreateVersion7(), TenantId = tenant.Id, VariantId = variant.Id, LocationCode = "MAIN", OnHand = 5m, Available = 5m };
            var offer = new ChannelOffer
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenant.Id,
                ConnectionId = connection.Id,
                VariantId = variant.Id,
                ListPrice = 150m,
                SalePrice = 120m,
                Currency = "TRY",
                VatRate = 20m,
                VatInclusion = "INCLUDED",
                RoundingMode = "HALF_EVEN",
                Status = "ACTIVE",
                LastSentCode = variant.Barcode,
                LastSentQuantity = 6m,
                LastSentSalePrice = 130m,
                LastSentListPrice = 150m
            };
            db.Products.Add(product);
            db.ProductVariants.Add(variant);
            db.InventoryItems.Add(inventory);
            db.ChannelOffers.Add(offer);
            sent.Add((variant, inventory, offer));
        }
        var jobId = Guid.CreateVersion7();
        var job = new IntegrationJob { Id = jobId, TenantId = tenant.Id, ConnectionId = connection.Id, JobType = MarketplaceJobTypes.PriceInventorySync, PayloadJson = "{}", PayloadVersion = 1, PayloadHash = "h", JobDedupKey = $"pi:{jobId:N}", EffectIdempotencyKey = $"pi:{jobId:N}", TriggerType = "user", Status = JobStatus.Leased, AvailableAt = now, CorrelationId = "send-log", CreatedAt = now, Version = 1 };
        db.IntegrationJobs.Add(job);
        await db.SaveChangesAsync();
        var lines = sent.Select((item, index) => new PriceInventoryPushLine(item.Variant.Id, item.Offer.Id, item.Variant.Barcode!, 5m, 150m, 120m, "TRY",
            item.Inventory.ProjectionVersion, item.Offer.PriceVersion, $"hash-{index + 1}", item.Variant.Sku, $"HBV0000{index + 1}")).ToList();
        return new(tenant, connection, job, tag, lines);
    }

    private PriceInventoryJobPayload Payload(Seeded seed) =>
        new(seed.Job.Id, seed.Connection.Id, "POLL", "hash", "{}", seed.Lines, OperationId, DateTimeOffset.UtcNow.AddMinutes(-1), Trigger: PriceStockSendTriggers.PanelSend);

    // What the submit writes once the marketplace took the request.
    private async Task Submit(Seeded seed)
    {
        await using var db = fixture.CreateContext();
        await PriceStockSendLogRecorder.AddAsync(db, seed.Tenant.Id, seed.Connection.Id, seed.Connection.PlatformCode, seed.Job, Payload(seed), OperationId, DateTimeOffset.UtcNow, PriceStockSendResults.Submitted, null, null, CancellationToken.None);
        await db.SaveChangesAsync();
    }

    private async Task<JobExecutionResult> Poll(Seeded seed, RemoteOperationStatus operation)
    {
        var payloadJson = JsonSerializer.Serialize(Payload(seed));
        await using var db = fixture.CreateContext();
        var processor = new MarketplaceJobProcessor(db, null!, null!, new OperationPort(operation), null!, null!, null!, null!, null!, null!, null!,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["FeatureFlags:ExternalWrites"] = "true" }).Build(), fixture.TimeProvider);
        return await processor.ProcessAsync(seed.Tenant.Id, seed.Connection.Id, MarketplaceJobTypes.PriceInventorySync, payloadJson, "send-log", CancellationToken.None, seed.Job.Id);
    }

    private static Task<List<PriceStockSendLog>> Rows(AppDbContext db, Seeded seed) =>
        db.PriceStockSendLogs.AsNoTracking().Where(x => x.TenantId == seed.Tenant.Id && x.JobId == seed.Job.Id).ToListAsync();

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
