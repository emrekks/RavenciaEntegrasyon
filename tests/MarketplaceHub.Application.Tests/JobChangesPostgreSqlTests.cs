using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// İş detayı › Gidenler ve Değişenler on PostgreSQL: a send job lists its own send log lines with their answers, a stock
// scan lists the lines of the send job it queued (and only that one), a scan that queued nothing says why from what it
// recorded, a product import lists the stock and price changes of its connection within its run, every read stays in
// its tenant, and the list pages and searches.
[Collection("postgres-integration")]
public sealed class JobChangesPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>, IAsyncLifetime
{
    private static readonly System.Collections.Concurrent.ConcurrentBag<Guid> CreatedTenants = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        var tenantIds = CreatedTenants.ToArray();
        if (tenantIds.Length == 0) return;
        await using var db = fixture.CreateContext();
        await db.PriceStockSendLogs.Where(x => tenantIds.Contains(x.TenantId)).ExecuteDeleteAsync();
        await db.StockLedgerEntries.Where(x => tenantIds.Contains(x.TenantId)).ExecuteDeleteAsync();
        await db.ChannelPriceHistory.Where(x => tenantIds.Contains(x.TenantId)).ExecuteDeleteAsync();
        await db.OperationalIssues.Where(x => x.TenantId != null && tenantIds.Contains(x.TenantId.Value)).ExecuteDeleteAsync();
        await db.JobAttempts.Where(x => tenantIds.Contains(x.TenantId)).ExecuteDeleteAsync();
        await db.IntegrationJobs.Where(x => tenantIds.Contains(x.TenantId)).ExecuteDeleteAsync();
    }

    [PostgreSqlFact]
    public async Task A_send_job_lists_its_lines_with_each_answer_and_counts_them()
    {
        var seed = await Seed();
        var now = DateTimeOffset.UtcNow;
        var job = await AddJob(seed, MarketplaceJobTypes.PriceInventorySync, JobStatus.ManualReview, now.AddMinutes(-5), now.AddMinutes(-1), "corr-send");
        await AddLines(seed, job.Id, now.AddMinutes(-4),
            (0, PriceStockSendResults.Applied, null),
            (1, PriceStockSendResults.Rejected, "Fiyat aralık dışında"),
            (2, PriceStockSendResults.Unknown, null),
            (3, PriceStockSendResults.Submitted, null));
        await using var db = fixture.CreateContext();
        var service = Service(db);

        var summary = (await service.SummaryAsync(seed.Tenant.Id, job.Id, CancellationToken.None)).Value!;
        var page = (await service.PageAsync(seed.Tenant.Id, job.Id, new JobChangesQuery(), CancellationToken.None)).Value!;

        Assert.Equal((JobChangeModes.SendLog, (string?)null, PriceStockSendTriggers.Order), (summary.Mode, summary.Outcome, summary.SourceTrigger));
        var counts = summary.Counts;
        Assert.Equal((4, 1, 1, 1, 1, 0), (counts.Sent, counts.Applied, counts.Rejected, counts.Uncertain, counts.Pending, counts.Unchanged));
        // Not recorded by a send: never shown as zero.
        Assert.Null(counts.Scanned);
        Assert.Equal(4, page.Items.Count);
        var refused = page.Items.Single(x => x.Result == PriceStockSendResults.Rejected);
        Assert.Equal(("Fiyat aralık dışında", JobChangeLineKinds.StockAndPrice, job.Id), (refused.ErrorMessage, refused.Kind, refused.JobId));
        Assert.Equal((6m, 5m, 130m, 120m), (refused.PreviousQuantity!.Value, refused.NewQuantity!.Value, refused.PreviousSalePrice!.Value, refused.NewSalePrice!.Value));
        Assert.Equal(($"Ürün 2 {seed.Tag}", "Kırmızı / M"), (refused.ProductTitle, refused.OptionText));
    }

    [PostgreSqlFact]
    public async Task The_lines_page_by_cursor_and_search_by_barcode()
    {
        var seed = await Seed();
        var now = DateTimeOffset.UtcNow;
        var job = await AddJob(seed, MarketplaceJobTypes.PriceInventorySync, JobStatus.Succeeded, now.AddMinutes(-5), now.AddMinutes(-1), "corr-page");
        await AddLines(seed, job.Id, now.AddMinutes(-4), (0, PriceStockSendResults.Applied, null), (1, PriceStockSendResults.Applied, null), (2, PriceStockSendResults.Applied, null));
        await using var db = fixture.CreateContext();
        var service = Service(db);

        var first = (await service.PageAsync(seed.Tenant.Id, job.Id, new JobChangesQuery(Limit: 2), CancellationToken.None)).Value!;
        var second = (await service.PageAsync(seed.Tenant.Id, job.Id, new JobChangesQuery(Limit: 2, After: first.NextCursor), CancellationToken.None)).Value!;
        var found = (await service.PageAsync(seed.Tenant.Id, job.Id, new JobChangesQuery(Search: seed.Variants[2].Barcode), CancellationToken.None)).Value!;
        var capped = (await service.PageAsync(seed.Tenant.Id, job.Id, new JobChangesQuery(Limit: 500), CancellationToken.None)).Value!;

        Assert.Equal((2, true), (first.Items.Count, first.HasMore));
        Assert.Equal((1, false), (second.Items.Count, second.HasMore));
        Assert.Empty(first.Items.Select(x => x.Id).Intersect(second.Items.Select(x => x.Id)));
        Assert.Equal(seed.Variants[2].Barcode, Assert.Single(found.Items).Barcode);
        Assert.Equal(3, capped.Items.Count);
        Assert.Equal(50, new JobChangesQuery(Limit: 500).EffectiveLimit);
    }

    [PostgreSqlFact]
    public async Task A_scan_lists_the_lines_of_the_send_job_it_queued_and_no_other()
    {
        var seed = await Seed();
        var now = DateTimeOffset.UtcNow;
        var scan = await AddJob(seed, MarketplaceJobTypes.StockReconciliation, JobStatus.Succeeded, now.AddMinutes(-10), now.AddMinutes(-9), "scheduler-scan-1");
        var spawned = await AddJob(seed, MarketplaceJobTypes.PriceInventorySync, JobStatus.Succeeded, now.AddMinutes(-9.5), now.AddMinutes(-8), "scheduler-scan-1", created: now.AddMinutes(-9.5));
        // Another send of the same tenant (a manual one) and a later send under the same correlation id after the scan ended.
        var other = await AddJob(seed, MarketplaceJobTypes.PriceInventorySync, JobStatus.Succeeded, now.AddMinutes(-7), now.AddMinutes(-6), "corr-other");
        var later = await AddJob(seed, MarketplaceJobTypes.PriceInventorySync, JobStatus.Succeeded, now.AddMinutes(-3), now.AddMinutes(-2), "scheduler-scan-1", created: now.AddMinutes(-3));
        await AddLines(seed, spawned.Id, now.AddMinutes(-9), (0, PriceStockSendResults.Applied, null), (1, PriceStockSendResults.Unchanged, null));
        await AddLines(seed, other.Id, now.AddMinutes(-7), (2, PriceStockSendResults.Applied, null));
        await AddLines(seed, later.Id, now.AddMinutes(-3), (3, PriceStockSendResults.Rejected, null));
        await using var db = fixture.CreateContext();
        var service = Service(db);

        var summary = (await service.SummaryAsync(seed.Tenant.Id, scan.Id, CancellationToken.None)).Value!;
        var page = (await service.PageAsync(seed.Tenant.Id, scan.Id, new JobChangesQuery(), CancellationToken.None)).Value!;

        Assert.Equal(spawned.Id, Assert.Single(summary.SpawnedJobs).Id);
        Assert.Equal((PriceStockSendTriggers.Reconcile, 2, 1, 1, 0), (summary.SourceTrigger, summary.Counts.Sent, summary.Counts.Applied, summary.Counts.Unchanged, summary.Counts.Rejected));
        Assert.Null(summary.Outcome);
        Assert.Equal(new[] { seed.Variants[0].Barcode, seed.Variants[1].Barcode }.Order(), page.Items.Select(x => x.Barcode).Order());
        Assert.All(page.Items, line => Assert.Equal(spawned.Id, line.JobId));
    }

    [PostgreSqlFact]
    public async Task A_scan_that_queued_nothing_says_why_from_its_own_record()
    {
        var seed = await Seed();
        var now = DateTimeOffset.UtcNow;
        var writesOff = await AddJob(seed, MarketplaceJobTypes.StockReconciliation, JobStatus.Succeeded, now.AddMinutes(-30), now.AddMinutes(-29), "s-off", lastErrorCode: "AUTOMATIC_WRITES_OFF");
        var waiting = await AddJob(seed, MarketplaceJobTypes.StockReconciliation, JobStatus.RetryScheduled, now.AddMinutes(-20), null, "s-wait", lastErrorCode: OrderFreshnessGatePolicy.WaitCode);
        var nothing = await AddJob(seed, MarketplaceJobTypes.StockReconciliation, JobStatus.Succeeded, now.AddMinutes(-15), now.AddMinutes(-14), "s-none");
        var held = await AddJob(seed, MarketplaceJobTypes.StockReconciliation, JobStatus.Succeeded, now.AddMinutes(-12), now.AddMinutes(-11), "s-held");
        var inFlight = await AddJob(seed, MarketplaceJobTypes.StockReconciliation, JobStatus.Succeeded, now.AddMinutes(-10), now.AddMinutes(-9), "s-flight", lastErrorCode: "PRICE_INVENTORY_PUSH_IN_FLIGHT");
        var refusedSend = await AddJob(seed, MarketplaceJobTypes.PriceInventorySync, JobStatus.Blocked, now.AddMinutes(-8), now.AddMinutes(-7), "s-send", lastErrorCode: "EXTERNAL_WRITE_POLICY_DISABLED");
        await using (var write = fixture.CreateContext())
        {
            write.OperationalIssues.Add(new OperationalIssue { Id = Guid.CreateVersion7(), TenantId = seed.Tenant.Id, DedupeKey = $"stock-projection-price-policy:{seed.Connection.Id:N}", Code = "STOCK_WRITE_HELD_PRICE_POLICY_DISABLED", Summary = "held", Status = IssueStatus.Open, FirstSeenAt = now.AddMinutes(-11.5), LastSeenAt = now.AddMinutes(-11.5), OccurrenceCount = 1 });
            await write.SaveChangesAsync();
        }
        await using var db = fixture.CreateContext();
        var service = Service(db);
        async Task<JobChangesSummaryView> Summary(IntegrationJob job) => (await service.SummaryAsync(seed.Tenant.Id, job.Id, CancellationToken.None)).Value!;

        Assert.Equal(JobChangeOutcomes.AutomaticWritesOff, (await Summary(writesOff)).Outcome);
        Assert.Equal(JobChangeOutcomes.OrderReadPending, (await Summary(waiting)).Outcome);
        Assert.Equal(JobChangeOutcomes.NoChanges, (await Summary(nothing)).Outcome);
        Assert.Equal(JobChangeOutcomes.PricePolicyHeld, (await Summary(held)).Outcome);
        Assert.Equal(JobChangeOutcomes.PushInFlight, (await Summary(inFlight)).Outcome);
        Assert.Equal(JobChangeOutcomes.AutomaticWritesOff, (await Summary(refusedSend)).Outcome);
        var none = await Summary(nothing);
        Assert.Equal((0, (int?)null, (int?)null), (none.Counts.Sent, none.Counts.Applied, none.Counts.Scanned));
        Assert.Empty(none.SpawnedJobs);
        Assert.Empty((await service.PageAsync(seed.Tenant.Id, nothing.Id, new JobChangesQuery(), CancellationToken.None)).Value!.Items);
    }

    [PostgreSqlFact]
    public async Task A_product_import_lists_its_connections_stock_and_price_changes_within_its_run()
    {
        var seed = await Seed();
        var now = DateTimeOffset.UtcNow;
        var job = await AddJob(seed, MarketplaceJobTypes.ProductSync, JobStatus.Succeeded, now.AddMinutes(-20), now.AddMinutes(-10), "import-1");
        var inventory = seed.Inventory[0];
        await using (var write = fixture.CreateContext())
        {
            StockLedgerEntry Ledger(decimal delta, DateTimeOffset at) => new()
            {
                Id = Guid.CreateVersion7(),
                TenantId = seed.Tenant.Id,
                InventoryItemId = inventory.Id,
                MovementType = MarketplaceStockImportPolicy.MovementType,
                QuantityDelta = delta,
                SourceType = MarketplaceStockImportPolicy.SourceType,
                SourceId = seed.Connection.Id.ToString("D"),
                SourceEventId = $"catalog-stock:{Guid.NewGuid():N}",
                IdempotencyKey = $"catalog-stock:{Guid.NewGuid():N}",
                OccurredAt = at,
                RecordedAt = at,
                CorrelationId = $"catalog-import:{seed.Connection.Id:N}"
            };
            ChannelPriceHistory Price(long version, decimal sale, DateTimeOffset at) => new()
            {
                Id = Guid.CreateVersion7(),
                TenantId = seed.Tenant.Id,
                OfferId = seed.Offers[1].Id,
                PriceVersion = version,
                ListPrice = 150m,
                SalePrice = sale,
                Currency = "TRY",
                Reason = "N11_CATALOG_IMPORT",
                ActorSource = "SYSTEM:N11",
                EffectiveAt = at
            };
            // Before the run (not this job's), within it (3 → 5 on hand: OnHand is 5 now) and the price 120 → 110.
            write.StockLedgerEntries.AddRange(Ledger(-2m, now.AddHours(-2)), Ledger(2m, now.AddMinutes(-15)));
            write.ChannelPriceHistory.AddRange(Price(1, 120m, now.AddHours(-3)), Price(2, 110m, now.AddMinutes(-12)));
            await write.SaveChangesAsync();
        }
        await using var db = fixture.CreateContext();
        var service = Service(db);

        var summary = (await service.SummaryAsync(seed.Tenant.Id, job.Id, CancellationToken.None)).Value!;
        var page = (await service.PageAsync(seed.Tenant.Id, job.Id, new JobChangesQuery(), CancellationToken.None)).Value!;

        Assert.Equal((JobChangeModes.ProductImport, (string?)null, 1, 1), (summary.Mode, summary.Outcome, summary.Counts.StockChanged!.Value, summary.Counts.PriceChanged!.Value));
        Assert.Null(summary.Counts.Sent);
        Assert.Equal(2, page.Items.Count);
        var stock = page.Items.Single(x => x.Kind == JobChangeLineKinds.Stock);
        Assert.Equal((3m, 5m, JobChangeResults.Changed, ProductActivitySources.MarketplaceImport), (stock.PreviousQuantity!.Value, stock.NewQuantity!.Value, stock.Result, stock.Source));
        var price = page.Items.Single(x => x.Kind == JobChangeLineKinds.Price);
        Assert.Equal((120m, 110m), (price.PreviousSalePrice!.Value, price.NewSalePrice!.Value));
        Assert.Equal(seed.Variants[1].Barcode, price.Barcode);
    }

    [PostgreSqlFact]
    public async Task Another_tenant_reads_neither_the_job_nor_its_lines()
    {
        var seed = await Seed();
        var stranger = await Seed();
        var now = DateTimeOffset.UtcNow;
        var job = await AddJob(seed, MarketplaceJobTypes.PriceInventorySync, JobStatus.Succeeded, now.AddMinutes(-5), now.AddMinutes(-1), "corr-tenant");
        await AddLines(seed, job.Id, now.AddMinutes(-4), (0, PriceStockSendResults.Applied, null));
        await using var db = fixture.CreateContext();
        var service = Service(db);

        var summary = await service.SummaryAsync(stranger.Tenant.Id, job.Id, CancellationToken.None);
        var page = await service.PageAsync(stranger.Tenant.Id, job.Id, new JobChangesQuery(), CancellationToken.None);

        Assert.Equal(("JOB_NOT_FOUND", 404), (summary.Error!.Code, summary.Error.Status));
        Assert.Equal(("JOB_NOT_FOUND", 404), (page.Error!.Code, page.Error.Status));
        // The send log service itself keeps another tenant's rows out even when it names the job.
        var lines = await new PriceStockSendLogService(db).PageAsync(stranger.Tenant.Id, new PriceStockSendLogQuery(JobIds: [job.Id]), CancellationToken.None);
        Assert.Empty(lines.Value!.Items);
    }

    private JobChangesService Service(AppDbContext db) => new(db, new PriceStockSendLogService(db), new ProductActivityService(db), fixture.TimeProvider);

    private sealed record Seeded(Tenant Tenant, PlatformConnection Connection, string Tag, IReadOnlyList<ProductVariant> Variants, IReadOnlyList<InventoryItem> Inventory, IReadOnlyList<ChannelOffer> Offers);

    // An N11 connection with four variants, each on hand 5 with an offer at 120/150 whose marketplace last held 6 at 130.
    private async Task<Seeded> Seed()
    {
        var now = DateTimeOffset.UtcNow;
        var tag = Guid.NewGuid().ToString("N")[..8];
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"job-changes-{Guid.NewGuid():N}", DisplayName = "job-changes", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        CreatedTenants.Add(tenant.Id);
        var connection = new PlatformConnection
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            PublicId = Guid.CreateVersion7(),
            PlatformCode = "N11",
            Environment = "PRODUCTION",
            DisplayName = "N11 Mağaza",
            ExternalStoreId = $"store-{Guid.NewGuid():N}",
            Status = "ACTIVE",
            ApiVersion = "V1",
            SettingsJson = "{}"
        };
        await using var db = fixture.CreateContext();
        db.Tenants.Add(tenant);
        db.PlatformConnections.Add(connection);
        var variants = new List<ProductVariant>();
        var inventory = new List<InventoryItem>();
        var offers = new List<ChannelOffer>();
        for (var index = 1; index <= 4; index++)
        {
            var product = new Product { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Title = $"Ürün {index} {tag}", Description = "İş detayı", Status = ProductStatus.Active, CreatedAt = now, UpdatedAt = now };
            var barcode = $"869{tag}{index}";
            var variant = new ProductVariant { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProductId = product.Id, Sku = $"SKU-{tag}-{index}", SkuNormalized = $"SKU-{tag}-{index}", Barcode = barcode, BarcodeNormalized = barcode, ModelCode = $"MDL-{tag}-{index}", OptionSignature = index == 2 ? "Kırmızı / M" : $"Renk {index}", CreatedAt = now, UpdatedAt = now };
            var item = new InventoryItem { Id = Guid.CreateVersion7(), TenantId = tenant.Id, VariantId = variant.Id, LocationCode = "MAIN", OnHand = 5m, Available = 5m };
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
                LastSentCode = barcode,
                LastSentQuantity = 6m,
                LastSentSalePrice = 130m,
                LastSentListPrice = 150m
            };
            db.Products.Add(product);
            db.ProductVariants.Add(variant);
            db.InventoryItems.Add(item);
            db.ChannelOffers.Add(offer);
            variants.Add(variant);
            inventory.Add(item);
            offers.Add(offer);
        }
        await db.SaveChangesAsync();
        return new(tenant, connection, tag, variants, inventory, offers);
    }

    // A job of the seed's connection that first ran at startedAt (one attempt) and finished at completedAt (null: still due).
    private async Task<IntegrationJob> AddJob(Seeded seed, string jobType, JobStatus status, DateTimeOffset startedAt, DateTimeOffset? completedAt, string correlationId,
        DateTimeOffset? created = null, string? lastErrorCode = null)
    {
        var id = Guid.CreateVersion7();
        var payload = jobType == MarketplaceJobTypes.PriceInventorySync ? $"{{\"Trigger\":\"{PriceStockSendTriggers.Order}\"}}" : "{}";
        var job = new IntegrationJob
        {
            Id = id,
            TenantId = seed.Tenant.Id,
            ConnectionId = seed.Connection.Id,
            JobType = jobType,
            PayloadJson = payload,
            PayloadVersion = 1,
            PayloadHash = "h",
            JobDedupKey = $"job-changes:{id:N}",
            EffectIdempotencyKey = $"job-changes:{id:N}",
            Status = status,
            AvailableAt = startedAt,
            CorrelationId = correlationId,
            CreatedAt = created ?? startedAt.AddSeconds(-5),
            StartedAt = startedAt,
            CompletedAt = completedAt,
            LastErrorCode = lastErrorCode,
            AttemptCount = 1,
            Version = 1
        };
        await using var db = fixture.CreateContext();
        db.IntegrationJobs.Add(job);
        db.JobAttempts.Add(new JobAttempt { Id = Guid.CreateVersion7(), TenantId = seed.Tenant.Id, JobId = id, AttemptNumber = 1, StartedAt = startedAt, CompletedAt = completedAt, Succeeded = status == JobStatus.Succeeded, ErrorCode = lastErrorCode });
        await db.SaveChangesAsync();
        return job;
    }

    // Send log lines of a job: the seed's variant at each index, 5 pieces at 120/150 against the confirmed 6 at 130/150.
    private async Task AddLines(Seeded seed, Guid jobId, DateTimeOffset at, params (int Index, string Result, string? Message)[] lines)
    {
        await using var db = fixture.CreateContext();
        foreach (var (index, result, message) in lines)
        {
            var variant = seed.Variants[index];
            var offer = seed.Offers[index];
            var line = new PriceInventoryPushLine(variant.Id, offer.Id, variant.Barcode!, 5m, 150m, 120m, "TRY", 1, 1, $"hash-{index}", variant.Sku, null);
            db.PriceStockSendLogs.Add(PriceStockSendLogPolicy.Row(seed.Tenant.Id, seed.Connection.Id, "N11", jobId, line, new PriceStockConfirmedValues(variant.Barcode, 6m, 130m, 150m),
                PriceStockSendTriggers.Order, "REQ-1", at.AddSeconds(index), result, result == PriceStockSendResults.Rejected ? "N11_LINE_REJECTED" : null, message));
        }
        await db.SaveChangesAsync();
    }
}
