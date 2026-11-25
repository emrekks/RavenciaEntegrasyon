using System.Text.Json;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// İşlem Takibi's grouped list (JobListQuery.GroupByModel): one model's colour-card jobs of one send are one row.
[Collection("postgres-integration")]
public sealed class PostgreSqlJobModelGroupTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    private sealed record Card(Product Product, List<ProductVariant> Variants);

    [PostgreSqlFact]
    public async Task GroupedPage_KeepsRunsModelsAndBrandsApartAndShowsEveryCardsStatus()
    {
        var seed = await SeedAsync("job-model-group");
        try
        {
            await using var db = fixture.CreateContext();
            var service = new JobOperationsService(db, fixture.TimeProvider);
            var page = await service.PageAsync(seed.Tenant.Id, new JobListQuery(Limit: 100, GroupByModel: true), CancellationToken.None);
            Assert.True(page.Succeeded, page.Error?.Message);
            var rows = page.Value!.Items;

            // The two sends of model A stay two rows; model B and the same code under another brand are rows of their own.
            var groups = rows.Where(row => row.ModelGroup is not null).ToList();
            Assert.Equal(4, groups.Count);
            var firstA = Assert.Single(groups, row => row.CorrelationId == "run-1" && row.ModelGroup!.ModelCode == "MZ900" && row.ModelGroup.Cards.All(card => seed.ModelA.Any(a => a.Product.Id == card.ProductId)));
            var secondA = Assert.Single(groups, row => row.CorrelationId == "run-2");
            var modelB = Assert.Single(groups, row => row.ModelGroup!.ModelCode == "MZ901");
            var otherBrand = Assert.Single(groups, row => row.ModelGroup!.Cards.All(card => seed.OtherBrand.Any(c => c.Product.Id == card.ProductId)));
            Assert.Equal(3, firstA.ModelGroup!.CardCount);
            Assert.Equal(6, firstA.ModelGroup.VariantCount);
            Assert.All(firstA.ModelGroup.Cards, card => Assert.Equal(2, card.VariantCount));
            Assert.Equal(3, secondA.ModelGroup!.CardCount);
            Assert.Equal(2, modelB.ModelGroup!.CardCount);
            Assert.Equal(2, otherBrand.ModelGroup!.CardCount);
            // Mixed statuses: the failed card is in the group, so the row cannot read as all succeeded.
            Assert.Equal(["SUCCEEDED", "DEAD", "SUCCEEDED"], firstA.ModelGroup.Cards.Select(card => card.Job.Status));
            // The row is the newest job of the group; the cards come oldest first.
            Assert.Equal(firstA.ModelGroup.Cards[^1].Job.Id, firstA.Id);
            Assert.True(firstA.ModelGroup.Cards.Zip(firstA.ModelGroup.Cards.Skip(1)).All(pair => pair.First.Job.CreatedAt <= pair.Second.Job.CreatedAt));

            // The approval check of the same send is another job type: a row of its own. Jobs older than the shared id
            // (one correlation id each) and other job types stay one row each.
            Assert.Contains(rows, row => row.Id == seed.Approval.Id && row.ModelGroup is null);
            foreach (var single in seed.Singles) Assert.Contains(rows, row => row.Id == single.Id && row.ModelGroup is null);
            Assert.Equal(seed.AllJobs.Count, rows.Sum(row => row.ModelGroup?.Cards.Count ?? 1));

            // Without the grouping the list is unchanged: one row per job.
            var plain = await service.PageAsync(seed.Tenant.Id, new JobListQuery(Limit: 100), CancellationToken.None);
            Assert.Equal(seed.AllJobs.Count, plain.Value!.Items.Count);
            Assert.All(plain.Value.Items, row => Assert.Null(row.ModelGroup));
        }
        finally { await CleanupAsync(seed.Tenant.Id); }
    }

    [PostgreSqlFact]
    public async Task GroupedPage_PagesByRowAndNeverSplitsAGroup()
    {
        var seed = await SeedAsync("job-model-group-paging");
        try
        {
            await using var db = fixture.CreateContext();
            var service = new JobOperationsService(db, fixture.TimeProvider);
            var all = (await service.PageAsync(seed.Tenant.Id, new JobListQuery(Limit: 100, GroupByModel: true), CancellationToken.None)).Value!.Items;
            foreach (var limit in new[] { 1, 2, 3 })
            {
                var paged = new List<JobSummaryView>();
                string? after = null;
                for (var guard = 0; guard < 100; guard++)
                {
                    var page = await service.PageAsync(seed.Tenant.Id, new JobListQuery(Limit: limit, After: after, GroupByModel: true), CancellationToken.None);
                    Assert.True(page.Succeeded, page.Error?.Message);
                    // The limit counts rows: a group of three jobs is one.
                    Assert.True(page.Value!.Items.Count <= limit);
                    paged.AddRange(page.Value.Items);
                    if (!page.Value.HasMore) break;
                    after = page.Value.NextCursor;
                }
                // The same rows in the same order as one big page: no group split, none shown twice.
                Assert.Equal(all.Select(Shape), paged.Select(Shape));
                var jobIds = paged.SelectMany(row => row.ModelGroup?.Cards.Select(card => card.Job.Id) ?? [row.Id]).ToList();
                Assert.Equal(jobIds.Count, jobIds.Distinct().Count());
                Assert.Equal(seed.AllJobs.Select(job => job.Id).Order(), jobIds.Order());
            }

            // A status filter chooses jobs: the group with a failed card shows, whole.
            var dead = (await service.PageAsync(seed.Tenant.Id, new JobListQuery(Status: "DEAD", Limit: 1, GroupByModel: true), CancellationToken.None)).Value!;
            var row = Assert.Single(dead.Items);
            Assert.Equal(3, row.ModelGroup!.Cards.Count);
            // The row is sorted by its failed job and shows that job's time and attempts, not the newest card's.
            Assert.Equal("DEAD", row.Status);
            Assert.Equal(row.ModelGroup.Cards.Single(card => card.Job.Status == "DEAD").Job.Id, row.Id);
            Assert.NotEqual(row.ModelGroup.Cards[^1].Job.Id, row.Id);
            Assert.False(dead.HasMore);
        }
        finally { await CleanupAsync(seed.Tenant.Id); }

        static string Shape(JobSummaryView row) => row.ModelGroup is { } group ? $"{group.Key}:{string.Join(',', group.Cards.Select(card => card.Job.Id))}" : row.Id.ToString();
    }

    // Seeds: four random worlds of 90 jobs (keys recurring over four hours, shared instants, jobs without a connection).
    [PostgreSqlTheory]
    [InlineData(20261125)]
    [InlineData(7)]
    [InlineData(42)]
    [InlineData(1001)]
    public async Task GroupedPage_NeverLosesOrRepeatsAJob_WhateverTheLimitFilterOrSpread(int seed)
    {
        var (tenant, connection, cards, baseTime) = await WorldAsync("job-model-group-random", 5);
        var random = new Random(seed);
        var types = new[] { "PRODUCT_CREATE", "PRODUCT_UPDATE", "PRODUCT_APPROVAL_RECONCILE", "TEST_LIST_PAGE" };
        var statuses = new[] { JobStatus.Succeeded, JobStatus.Succeeded, JobStatus.Dead, JobStatus.Cancelled };
        var jobs = Enumerable.Range(0, 90).Select(index =>
        {
            var correlation = random.Next(6) switch { 0 => "send-a", 1 => "send-b", 2 => "send-c", 3 => $"publication-status:{index % 3}", 4 => "send-a", _ => $"own-{index}" };
            // 300 possible seconds over four hours: jobs share instants, keys recur hours apart.
            var createdAt = baseTime.AddSeconds(random.Next(300) * 48);
            return JobOf(tenant.Id, types[random.Next(types.Length)], cards[random.Next(cards.Count)], correlation, statuses[random.Next(statuses.Length)], random.Next(10) == 0 ? null : connection.Id, createdAt);
        }).ToList();
        await SaveJobsAsync(jobs);
        try
        {
            await using var db = fixture.CreateContext();
            var service = new JobOperationsService(db, fixture.TimeProvider);
            var since = baseTime.AddHours(2);
            var queries = new (JobListQuery Query, Func<IntegrationJob, bool> Matches)[]
            {
                (new JobListQuery(GroupByModel: true), _ => true),
                (new JobListQuery(Status: "DEAD", GroupByModel: true), job => job.Status == JobStatus.Dead),
                (new JobListQuery(Since: since, GroupByModel: true), job => JobOperationsService.IsInWindow(job, since))
            };
            foreach (var (query, matches) in queries)
                foreach (var limit in new[] { 1, 2, 3, 5, 8, 200 })
                {
                    var seen = new List<Guid>();
                    string? after = null;
                    for (var guard = 0; ; guard++)
                    {
                        Assert.True(guard < 500, "the list did not reach its last page");
                        var page = await service.PageAsync(tenant.Id, query with { Limit = limit, After = after }, CancellationToken.None);
                        Assert.True(page.Succeeded, page.Error?.Message);
                        Assert.True(page.Value!.Items.Count <= limit);
                        seen.AddRange(page.Value.Items.SelectMany(row => row.ModelGroup?.Cards.Select(card => card.Job.Id) ?? [row.Id]));
                        if (!page.Value.HasMore) break;
                        after = page.Value.NextCursor;
                    }
                    // No job twice, every matching job once; without a filter exactly the tenant's jobs.
                    Assert.Equal(seen.Count, seen.Distinct().Count());
                    Assert.Empty(jobs.Where(matches).Select(job => job.Id).Except(seen));
                    if (query.Status is null && query.Since is null) Assert.Equal(jobs.Select(job => job.Id).Order(), seen.Order());
                }
        }
        finally { await CleanupAsync(tenant.Id); }
    }

    [PostgreSqlFact]
    public async Task GroupedPage_SplitsAKeyReusedHoursLaterAndShowsTheJobsOfAGroupThatGrewAfterTheFirstPage()
    {
        var (tenant, connection, cards, baseTime) = await WorldAsync("job-model-group-spread", 3);
        // One key: two cards of one send within seconds, the same key two hours later (an approval check that carries its
        // send's id, a fixed id reused): two rows, never one, and neither job dropped.
        var early = new[] { JobOf(tenant.Id, "PRODUCT_CREATE", cards[0], "send-x", JobStatus.Dead, connection.Id, baseTime.AddSeconds(10)), JobOf(tenant.Id, "PRODUCT_CREATE", cards[1], "send-x", JobStatus.Succeeded, connection.Id, baseTime.AddSeconds(11)) };
        var late = JobOf(tenant.Id, "PRODUCT_CREATE", cards[2], "send-x", JobStatus.Succeeded, connection.Id, baseTime.AddHours(2));
        var refreshes = new[] { JobOf(tenant.Id, "PRODUCT_APPROVAL_RECONCILE", cards[0], "publication-status:card", JobStatus.Dead, connection.Id, baseTime.AddMinutes(30)), JobOf(tenant.Id, "PRODUCT_APPROVAL_RECONCILE", cards[0], "publication-status:card", JobStatus.Succeeded, connection.Id, baseTime.AddMinutes(31)) };
        var older = Enumerable.Range(1, 3).Select(index => JobOf(tenant.Id, "TEST_LIST_PAGE", null, $"own-{index}", JobStatus.Succeeded, connection.Id, baseTime.AddSeconds(-index))).ToList();
        await SaveJobsAsync([.. early, late, .. refreshes, .. older]);
        try
        {
            await using var db = fixture.CreateContext();
            var service = new JobOperationsService(db, fixture.TimeProvider);
            var all = (await service.PageAsync(tenant.Id, new JobListQuery(Limit: 100, GroupByModel: true), CancellationToken.None)).Value!.Items;
            Assert.Contains(all, row => row.Id == late.Id && row.ModelGroup is null);
            var group = Assert.Single(all, row => row.ModelGroup is not null);
            Assert.Equal(early.Select(job => job.Id), group.ModelGroup!.Cards.Select(card => card.Job.Id));
            // Each "Durumu Yenile" check is a row of its own: its fixed id names no send.
            Assert.All(refreshes, refresh => Assert.Contains(all, row => row.Id == refresh.Id && row.ModelGroup is null));

            // The first page holds the group; then another card of the same send is queued. The next page (old cursor)
            // leaves out the group's jobs the first page showed and loses nothing; the refreshed first page has the new card.
            var first = (await service.PageAsync(tenant.Id, new JobListQuery(Limit: 4, GroupByModel: true), CancellationToken.None)).Value!;
            Assert.Contains(first.Items, row => row.ModelGroup is not null);
            var grown = JobOf(tenant.Id, "PRODUCT_CREATE", cards[2], "send-x", JobStatus.Succeeded, connection.Id, baseTime.AddSeconds(12));
            await SaveJobsAsync([grown]);
            var seen = first.Items.SelectMany(Ids).ToList();
            string? after = first.NextCursor;
            while (after is not null)
            {
                var page = (await service.PageAsync(tenant.Id, new JobListQuery(Limit: 4, After: after, GroupByModel: true), CancellationToken.None)).Value!;
                Assert.DoesNotContain(page.Items.SelectMany(Ids), id => early.Any(job => job.Id == id));
                seen.AddRange(page.Items.SelectMany(Ids));
                after = page.NextCursor;
            }
            var refreshed = (await service.PageAsync(tenant.Id, new JobListQuery(Limit: 4, GroupByModel: true), CancellationToken.None)).Value!;
            Assert.Contains(refreshed.Items, row => row.ModelGroup is { } model && model.Cards.Any(card => card.Job.Id == grown.Id) && model.Cards.Count == 3);
            seen.AddRange(refreshed.Items.SelectMany(Ids));
            Assert.Equal(early.Append(late).Concat(refreshes).Concat(older).Append(grown).Select(job => job.Id).Order(), seen.Distinct().Order());
        }
        finally { await CleanupAsync(tenant.Id); }

        static IEnumerable<Guid> Ids(JobSummaryView row) => row.ModelGroup?.Cards.Select(card => card.Job.Id) ?? [row.Id];
    }

    // A tenant with a Hepsiburada connection and colour cards of one model (with more than three cards, the last is another model); the time is four hours back, on whole seconds.
    private async Task<(Tenant Tenant, PlatformConnection Connection, List<Card> Cards, DateTimeOffset BaseTime)> WorldAsync(string code, int colours)
    {
        var now = DateTimeOffset.UtcNow;
        var baseTime = new DateTimeOffset(now.UtcTicks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero).AddHours(-4);
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"{code}-{Guid.NewGuid():N}", DisplayName = code, Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var connection = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = "HEPSIBURADA", Environment = "PRODUCTION", DisplayName = "Grup testi", ExternalStoreId = $"store-{Guid.NewGuid():N}", Status = "ACTIVE", ApiVersion = "V1", SettingsJson = "{}" };
        var cards = Enumerable.Range(1, colours).Select(index => CardOf(tenant.Id, null, index == colours && colours > 3 ? "MZ950" : "MZ951", $"Renk{index}", now)).ToList();
        await using var db = fixture.CreateContext();
        db.Tenants.Add(tenant);
        db.PlatformConnections.Add(connection);
        foreach (var card in cards) { db.Products.Add(card.Product); db.ProductVariants.AddRange(card.Variants); }
        await db.SaveChangesAsync();
        return (tenant, connection, cards, baseTime);
    }

    private async Task SaveJobsAsync(IEnumerable<IntegrationJob> jobs)
    {
        await using var db = fixture.CreateContext();
        db.IntegrationJobs.AddRange(jobs);
        await db.SaveChangesAsync();
    }

    private sealed record Seeded(Tenant Tenant, List<Card> ModelA, List<Card> OtherBrand, IntegrationJob Approval, List<IntegrationJob> Singles, List<IntegrationJob> AllJobs);

    private async Task<Seeded> SeedAsync(string code)
    {
        var now = DateTimeOffset.UtcNow;
        var baseTime = new DateTimeOffset(now.UtcTicks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero).AddMinutes(-30);
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"{code}-{Guid.NewGuid():N}", DisplayName = code, Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var connection = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = "HEPSIBURADA", Environment = "PRODUCTION", DisplayName = "Grup testi", ExternalStoreId = $"store-{Guid.NewGuid():N}", Status = "ACTIVE", ApiVersion = "V1", SettingsJson = "{}" };
        var brand = new Brand { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Name = "Örnek", NormalizedName = $"ORNEK-{Guid.NewGuid():N}", IsActive = true, CreatedAt = now, UpdatedAt = now, Version = 1 };
        var otherBrandRow = new Brand { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Name = "Başka", NormalizedName = $"BASKA-{Guid.NewGuid():N}", IsActive = true, CreatedAt = now, UpdatedAt = now, Version = 1 };
        Card NewCard(Guid brandId, string modelCode, string colour) => CardOf(tenant.Id, brandId, modelCode, colour, now);
        var modelA = new[] { "Siyah", "Beyaz", "Kırmızı" }.Select(colour => NewCard(brand.Id, "MZ900", colour)).ToList();
        var modelB = new[] { "Mavi", "Yeşil" }.Select(colour => NewCard(brand.Id, "MZ901", colour)).ToList();
        var otherBrand = new[] { "Gri", "Lacivert" }.Select(colour => NewCard(otherBrandRow.Id, "MZ900", colour)).ToList();

        var seconds = 0;
        IntegrationJob Job(string jobType, Card? card, string correlation, JobStatus status, Guid? connectionId) => JobOf(tenant.Id, jobType, card, correlation, status, connectionId, baseTime.AddSeconds(seconds++));

        var singles = new List<IntegrationJob>();
        var jobs = new List<IntegrationJob>();
        // Run 1: model A (one card failed), model B and the same model code under another brand, other jobs between.
        jobs.Add(Job("PRODUCT_CREATE", modelA[0], "run-1", JobStatus.Succeeded, connection.Id));
        singles.Add(Job("TEST_LIST_PAGE", null, $"other-{Guid.NewGuid():N}", JobStatus.Succeeded, connection.Id));
        jobs.Add(Job("PRODUCT_CREATE", modelA[1], "run-1", JobStatus.Dead, connection.Id));
        jobs.Add(Job("PRODUCT_CREATE", modelB[0], "run-1", JobStatus.Succeeded, connection.Id));
        jobs.Add(Job("PRODUCT_CREATE", modelA[2], "run-1", JobStatus.Succeeded, connection.Id));
        jobs.Add(Job("PRODUCT_CREATE", modelB[1], "run-1", JobStatus.Succeeded, connection.Id));
        jobs.Add(Job("PRODUCT_CREATE", otherBrand[0], "run-1", JobStatus.Succeeded, connection.Id));
        jobs.Add(Job("PRODUCT_CREATE", otherBrand[1], "run-1", JobStatus.Succeeded, connection.Id));
        var approval = Job("PRODUCT_APPROVAL_RECONCILE", modelA[0], "run-1", JobStatus.Succeeded, connection.Id);
        // Older sends: one correlation id per card, as every job before the shared id.
        singles.Add(Job("PRODUCT_CREATE", modelB[0], $"old-{Guid.NewGuid():N}", JobStatus.Succeeded, connection.Id));
        singles.Add(Job("PRODUCT_CREATE", modelB[1], $"old-{Guid.NewGuid():N}", JobStatus.Succeeded, connection.Id));
        // Run 2: model A again.
        jobs.AddRange(modelA.Select(card => Job("PRODUCT_CREATE", card, "run-2", JobStatus.Succeeded, connection.Id)));
        singles.Add(Job("TEST_LIST_PAGE", null, $"other-{Guid.NewGuid():N}", JobStatus.Succeeded, null));

        await using var db = fixture.CreateContext();
        db.Tenants.Add(tenant);
        db.PlatformConnections.Add(connection);
        db.Brands.AddRange(brand, otherBrandRow);
        foreach (var card in modelA.Concat(modelB).Concat(otherBrand)) { db.Products.Add(card.Product); db.ProductVariants.AddRange(card.Variants); }
        var all = jobs.Concat(singles).Append(approval).ToList();
        db.IntegrationJobs.AddRange(all);
        await db.SaveChangesAsync();
        return new(tenant, modelA, otherBrand, approval, singles, all);
    }

    private static Card CardOf(Guid tenantId, Guid? brandId, string modelCode, string colour, DateTimeOffset now)
    {
        var product = new Product { Id = Guid.CreateVersion7(), TenantId = tenantId, Title = $"Örnek {modelCode} Elbise", Description = "Test", BrandId = brandId, Status = ProductStatus.Active, CreatedAt = now, UpdatedAt = now, Version = 1 };
        var variants = new[] { "S", "M" }.Select((size, index) =>
        {
            var sku = $"{modelCode}-{colour}-{size}-{Guid.NewGuid():N}"[..30];
            return new ProductVariant { Id = Guid.CreateVersion7(), TenantId = tenantId, ProductId = product.Id, Sku = sku, SkuNormalized = sku.ToUpperInvariant(), Barcode = $"B{Guid.NewGuid():N}"[..14], ModelCode = modelCode, OptionSignature = $"Renk: {colour} | Beden: {size}", Status = ProductStatus.Active, SortOrder = index, CreatedAt = now, UpdatedAt = now, Version = 1 };
        }).ToList();
        return new(product, variants);
    }

    private static IntegrationJob JobOf(Guid tenantId, string jobType, Card? card, string correlation, JobStatus status, Guid? connectionId, DateTimeOffset createdAt)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var items = card is null ? "[]" : JsonSerializer.Serialize(card.Variants.Select(variant => new { barcode = variant.Barcode, merchantSku = variant.Sku }));
        var payload = card is null ? "{}" : JsonSerializer.Serialize(new { JobId = Guid.NewGuid(), ProductId = card.Product.Id, ProfileId = Guid.NewGuid(), Phase = "SUBMIT", PayloadHash = "h", PayloadJson = $$"""{"items":{{items}}}""" });
        return new IntegrationJob
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            ConnectionId = connectionId,
            JobType = jobType,
            PayloadJson = payload,
            PayloadVersion = 1,
            PayloadHash = "test",
            JobDedupKey = $"model-group-{suffix}",
            EffectIdempotencyKey = $"effect-{suffix}",
            Priority = 1,
            Status = status,
            AvailableAt = createdAt,
            MaxAttempts = 3,
            CorrelationId = correlation,
            CreatedAt = createdAt,
            CompletedAt = createdAt.AddSeconds(1),
            Version = 1
        };
    }

    private async Task CleanupAsync(Guid tenantId)
    {
        await using var cleanup = fixture.CreateContext();
        await cleanup.JobAttempts.Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
        await cleanup.IntegrationJobs.Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
        await cleanup.IntegrationOutboxEvents.Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
        await cleanup.ProductVariants.Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
        await cleanup.Products.Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
        await cleanup.Brands.Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
        await cleanup.PlatformConnections.Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
        await cleanup.Tenants.Where(x => x.Id == tenantId).ExecuteDeleteAsync();
    }
}
