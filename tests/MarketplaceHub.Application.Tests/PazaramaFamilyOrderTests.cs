using System.Text.Json;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Card = MarketplaceHub.Application.ProductFamilyColorOrder.Card;
using RoundJob = MarketplaceHub.Infrastructure.Persistence.PazaramaFamilyOrder.RoundJob;
using VariantRow = MarketplaceHub.Application.ProductFamilyColorOrder.VariantRow;

namespace MarketplaceHub.Application.Tests;

// Pazarama lists a family's colours in the order it created them. Live 2026-10-29 (MZ035YNC, eight colour cards queued
// in two seconds) the cards went out as their jobs happened to run, so Pazarama's colours followed no order the panel
// shows. A round's cards now go out one by one in the panel's colour order.
public sealed class PazaramaFamilyOrderTests
{
    private static readonly DateTimeOffset QueuedAt = new(2026, 10, 29, 14, 29, 0, TimeSpan.Zero);

    // MZ035YNC as the editor queued it: the opened card (Kahverengi) first, then the family's own order.
    private static readonly (string Color, int FirstSortOrder)[] Mz035Ync =
    [
        ("Kahverengi", 12), ("Açık Kahverengi", 0), ("Lacivert", 15), ("Bordo", 3), ("Haki", 9), ("Siyah", 21), ("Gri", 6), ("Mürdüm", 18)
    ];

    private static Card CardOf(string? color, int firstSortOrder, Guid? id = null) =>
        ProductFamilyColorOrder.ForCard(id ?? Guid.NewGuid(), new[] { "L", "XL", "2XL" }.Select((size, index) =>
            new VariantRow(color is null ? $"Beden: {size}" : $"Renk: {color} | Beden: {size}", firstSortOrder + index)));

    private static List<RoundJob> Round() => Mz035Ync
        .Select((card, index) => new RoundJob(Guid.NewGuid(), QueuedAt.AddMilliseconds(400 * index), CardOf(card.Color, card.FirstSortOrder)))
        .ToList();

    [Fact]
    public void TheColourOrderIsThePanelsAlphabeticalOrder()
    {
        var ordered = ProductFamilyColorOrder.Order(Mz035Ync.Select(card => CardOf(card.Color, card.FirstSortOrder)));

        Assert.Equal(new string?[] { "Açık Kahverengi", "Bordo", "Gri", "Haki", "Kahverengi", "Lacivert", "Mürdüm", "Siyah" }, ordered.Select(card => card.Color));
    }

    [Fact]
    public void ColoursCompareTheTurkishWayAndColourlessCardsComeLast()
    {
        var ordered = ProductFamilyColorOrder.Order([CardOf(null, 0), CardOf("şeftali", 1), CardOf("Ilık", 2), CardOf("ÇAĞLA", 3), CardOf("İndigo", 4), CardOf("siyah", 5)]);

        Assert.Equal(new string?[] { "ÇAĞLA", "Ilık", "İndigo", "siyah", "şeftali", null }, ordered.Select(card => card.Color));
    }

    [Fact]
    public void TheSameColourFallsBackToTheFamilySortOrder()
    {
        var later = CardOf("Siyah", 9);
        var earlier = CardOf("siyah", 3);

        Assert.Equal(new[] { earlier, later }, ProductFamilyColorOrder.Order([later, earlier]));
    }

    [Fact]
    public void ARenkOptionIsTheColourBeforeAWebColour()
    {
        Assert.Equal("Bordo", ProductFamilyColorOrder.ColorOf("Renk: Bordo | Beden: L | Web Color: Kırmızı"));
        Assert.Equal("Kırmızı", ProductFamilyColorOrder.ColorOf("Beden: L | Web Color: Kırmızı"));
        Assert.Null(ProductFamilyColorOrder.ColorOf("Beden: L"));
        Assert.Null(ProductFamilyColorOrder.ColorOf(null));
    }

    [Fact]
    public void EachCardWaitsForTheCardJustBeforeItInColourOrder()
    {
        var round = Round();

        // The first colour sends at once, although it was queued second.
        Assert.Null(PazaramaFamilyOrder.Blocking(Job("Açık Kahverengi"), round));
        // The opened card (queued first) waits for Haki, the colour just before it.
        Assert.Equal("Haki", PazaramaFamilyOrder.Blocking(Job("Kahverengi"), round)!.Card.Color);
        Assert.Equal("Mürdüm", PazaramaFamilyOrder.Blocking(Job("Siyah"), round)!.Card.Color);

        RoundJob Job(string color) => round.Single(job => job.Card.Color == color);
    }

    [Fact]
    public void ACardWhoseEarlierColoursHaveTheirAnswerSendsItsCreate()
    {
        var round = Round();
        var gri = round.Single(job => job.Card.Color == "Gri");
        // Only unfinished jobs are passed: Açık Kahverengi and Bordo were answered (or refused, or stopped).
        var unfinished = round.Where(job => job.Card.Color is not ("Açık Kahverengi" or "Bordo")).ToList();

        Assert.Null(PazaramaFamilyOrder.Blocking(gri, unfinished));
        Assert.Equal("Gri", PazaramaFamilyOrder.Blocking(round.Single(job => job.Card.Color == "Haki"), unfinished)!.Card.Color);
    }

    [Fact]
    public void TwoJobsOfOneCardKeepTheirQueueOrder()
    {
        var card = CardOf("Bordo", 3);
        var first = new RoundJob(Guid.NewGuid(), QueuedAt, card);
        var second = new RoundJob(Guid.NewGuid(), QueuedAt.AddSeconds(2), card with { });

        Assert.Same(first, PazaramaFamilyOrder.Blocking(second, [first]));
        Assert.Null(PazaramaFamilyOrder.Blocking(first, [second]));
    }

    [Fact]
    public void TheGroupCodeComesFromTheCreateBody()
    {
        var body = PazaramaProductPublicationComposer.Payload([
            new("Bluz", "Bluz", "Penye bluz", Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), "MZ035YNC", "MZ035YNC-01", "MZ035YNC-01", 1, 3, 10, 499m, 399m, ["https://cdn.example.com/a.jpg"], [])
        ]);

        Assert.Equal("MZ035YNC", PazaramaFamilyOrder.GroupCode(body));
        Assert.Null(PazaramaFamilyOrder.GroupCode("{\"products\":[]}"));
        Assert.Null(PazaramaFamilyOrder.GroupCode("not json"));
    }

    [Fact]
    public void TheWaitIsADeferralThatDoesNotSlowDownLikeAWaitingCheck()
    {
        // A waiting check is asked less often after a few checks; a card waiting for the colour before it is not.
        Assert.False(JobWaitingPolicy.IsWaitingCode(PazaramaFamilyOrder.WaitCode));
        Assert.True(PazaramaFamilyOrder.RoundWindow >= TimeSpan.FromMinutes(5));
        Assert.True(PazaramaFamilyOrder.SettleWindow < PazaramaFamilyOrder.RoundWindow);
    }
}

// The round a card's create waits for, read from the job table: the same connection and groupCode, queued within the
// round window and not yet answered.
[Collection("postgres-integration")]
public sealed class PazaramaFamilyOrderPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    [PostgreSqlFact]
    public async Task ACardWaitsForTheUnansweredEarlierColoursOfItsRoundOnly()
    {
        var now = DateTimeOffset.UtcNow;
        var queuedAt = now.AddMinutes(-1);
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"pzo-{Guid.NewGuid():N}"[..16], DisplayName = "pzo", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        var connection = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = "PAZARAMA", Environment = "PRODUCTION", DisplayName = "pzo", ExternalStoreId = $"s-{Guid.NewGuid():N}"[..12], Status = "ACTIVE", ApiVersion = "V1" };
        var products = new List<Product>();
        var variants = new List<ProductVariant>();
        Guid Card(string color, int sortOrder, string modelCode = "MZ035YNC")
        {
            var product = new Product { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Title = "Bluz", Description = "", Status = ProductStatus.Active, CreatedAt = now, UpdatedAt = now, Version = 1 };
            products.Add(product);
            var sku = $"{modelCode}-{Guid.NewGuid():N}"[..30];
            variants.Add(new ProductVariant { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProductId = product.Id, Sku = sku, SkuNormalized = sku.ToUpperInvariant(), ModelCode = modelCode, SortOrder = sortOrder, OptionSignature = $"Renk: {color} | Beden: L", CreatedAt = now, UpdatedAt = now, Version = 1 });
            return product.Id;
        }
        var kahve = Card("Kahverengi", 12);
        var acik = Card("Açık Kahverengi", 0);
        var bordo = Card("Bordo", 3);
        var haki = Card("Haki", 9);
        var other = Card("Beyaz", 0, "MZ099YNC");
        var jobs = new List<IntegrationJob>();
        IntegrationJob Job(Guid productId, DateTimeOffset createdAt, JobStatus status, string groupCode = "MZ035YNC", string phase = "SUBMIT")
        {
            var id = Guid.CreateVersion7();
            var code = $"{id:N}"[..12];
            var body = PazaramaProductPublicationComposer.Payload([new("Bluz", "Bluz", "Penye", Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), groupCode, code, code, 1, 1, 10, 2m, 1m, ["https://cdn.example.com/a.jpg"], [])]);
            var payload = new ProductPublicationJobPayload(id, productId, Guid.NewGuid(), phase, "hash", body, phase == "POLL" ? "b:x" : null, phase == "POLL" ? createdAt : null);
            var suffix = Guid.NewGuid().ToString("N");
            var job = new IntegrationJob
            {
                Id = id,
                TenantId = tenant.Id,
                ConnectionId = connection.Id,
                JobType = MarketplaceJobTypes.ProductCreate,
                PayloadJson = JsonSerializer.Serialize(payload),
                PayloadVersion = 1,
                PayloadHash = "h",
                JobDedupKey = $"pzo-{suffix}",
                EffectIdempotencyKey = $"pzo-{suffix}",
                Priority = 1,
                Status = status,
                // Never due: the lease tests sharing this database take any due job.
                AvailableAt = now.AddDays(1),
                MaxAttempts = 3,
                CorrelationId = $"pzo-{suffix}",
                CreatedAt = createdAt,
                Version = 1
            };
            jobs.Add(job);
            return job;
        }
        var kahveJob = Job(kahve, queuedAt, JobStatus.RetryScheduled);
        var acikJob = Job(acik, queuedAt.AddSeconds(1), JobStatus.RetryScheduled);
        Job(bordo, queuedAt.AddSeconds(2), JobStatus.Succeeded, phase: "POLL");
        Job(haki, queuedAt.AddSeconds(3), JobStatus.RetryScheduled, phase: "POLL");
        // Not of this round, family or still unanswered: an earlier round, another groupCode, a stopped card.
        Job(acik, queuedAt.AddMinutes(-10), JobStatus.RetryScheduled);
        Job(other, queuedAt.AddSeconds(4), JobStatus.RetryScheduled, groupCode: "MZ099YNC");
        Job(bordo, queuedAt.AddSeconds(5), JobStatus.Cancelled);

        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.Add(tenant);
            seed.PlatformConnections.Add(connection);
            seed.Products.AddRange(products);
            seed.ProductVariants.AddRange(variants);
            seed.IntegrationJobs.AddRange(jobs);
            await seed.SaveChangesAsync();
        }
        try
        {
            await using var db = fixture.CreateContext();
            Task<JobExecutionResult?> WaitOf(IntegrationJob job, DateTimeOffset at) => PazaramaFamilyOrder.WaitAsync(db, tenant.Id, connection.Id, job,
                JsonSerializer.Deserialize<ProductPublicationJobPayload>(job.PayloadJson)!, at, CancellationToken.None);

            // Açık Kahverengi is first in colour order: it sends although Kahverengi was queued before it.
            Assert.Null(await WaitOf(acikJob, now));
            // Kahverengi waits for Haki, whose batch is still being polled (Bordo has its answer).
            var kahveWait = await WaitOf(kahveJob, now);
            Assert.NotNull(kahveWait);
            Assert.Equal(PazaramaFamilyOrder.WaitCode, kahveWait.ErrorCode);
            Assert.True(kahveWait.RefundAttempt);
            Assert.Contains("Haki", kahveWait.ErrorSummary);
            // A young card of a family whose round may still be queueing waits until the round has settled.
            var young = await WaitOf(acikJob, queuedAt.AddSeconds(3));
            Assert.NotNull(young);
            Assert.Equal(PazaramaFamilyOrder.WaitCode, young.ErrorCode);
            Assert.Contains("kuyruğa", young.ErrorSummary);
        }
        finally
        {
            await using var db = fixture.CreateContext();
            await db.IntegrationJobs.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            // Adding a job writes its outbox event.
            await db.IntegrationOutboxEvents.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await db.ProductVariants.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await db.Products.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await db.PlatformConnections.Where(row => row.TenantId == tenant.Id).ExecuteDeleteAsync();
            await db.Tenants.Where(row => row.Id == tenant.Id).ExecuteDeleteAsync();
        }
    }
}
