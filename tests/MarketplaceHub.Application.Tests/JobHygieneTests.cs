using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Owner decisions of 7 Nov 2026: an old failed product write closes once a later write of the same type for the same
// product on the same connection succeeded (live: a Trendyol PRODUCT_UPDATE blocked on PRODUCT_CONTENT_GROUP_MISMATCH kept
// showing "Hata" after the next update went through); an invoice has at most one delivery job per package waiting,
// running or in review, and a waiting one closes when the invoice goes to review, is cancelled or its delivery confirmed.
public sealed class JobHygieneTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-07T12:00:00Z");

    [Fact]
    public void ALaterSuccessfulWriteOfTheSameTypeProductAndConnectionClosesTheOldFailure()
    {
        var connection = Guid.NewGuid();
        var product = Guid.NewGuid();
        var failed = (connection, MarketplaceJobTypes.ProductUpdate, product, JobStatus.Blocked, Now.AddHours(-9));
        var later = (connection, MarketplaceJobTypes.ProductUpdate, product, JobStatus.Succeeded, Now.AddHours(-3));

        Assert.True(ProductWriteSupersession.Supersedes(failed, later));
        Assert.True(ProductWriteSupersession.Supersedes(failed with { Item4 = JobStatus.ManualReview }, later));
        Assert.True(ProductWriteSupersession.Supersedes(failed with { Item4 = JobStatus.Dead }, later));
        Assert.True(ProductWriteSupersession.Supersedes(failed with { Item2 = MarketplaceJobTypes.ProductArchive }, later with { Item2 = MarketplaceJobTypes.ProductArchive }));
        Assert.True(ProductWriteSupersession.Supersedes(failed with { Item2 = MarketplaceJobTypes.ProductCreate }, later with { Item2 = MarketplaceJobTypes.ProductCreate }));
    }

    [Fact]
    public void NothingElseIsClosed()
    {
        var connection = Guid.NewGuid();
        var product = Guid.NewGuid();
        var failed = (connection, MarketplaceJobTypes.ProductUpdate, product, JobStatus.Blocked, Now.AddHours(-9));
        var later = (connection, MarketplaceJobTypes.ProductUpdate, product, JobStatus.Succeeded, Now.AddHours(-3));

        // Another connection, another product, an earlier or equal success, another write type.
        Assert.False(ProductWriteSupersession.Supersedes(failed, later with { Item1 = Guid.NewGuid() }));
        Assert.False(ProductWriteSupersession.Supersedes(failed, later with { Item3 = Guid.NewGuid() }));
        Assert.False(ProductWriteSupersession.Supersedes(failed with { Item3 = Guid.Empty }, later with { Item3 = Guid.Empty }));
        Assert.False(ProductWriteSupersession.Supersedes(failed, later with { Item5 = Now.AddHours(-10) }));
        Assert.False(ProductWriteSupersession.Supersedes(failed, later with { Item5 = failed.Item5 }));
        Assert.False(ProductWriteSupersession.Supersedes(failed, later with { Item2 = MarketplaceJobTypes.ProductArchive }));
        // Only a success closes, and only a stopped job is closed.
        foreach (var status in new[] { JobStatus.Pending, JobStatus.Leased, JobStatus.RetryScheduled, JobStatus.Cancelled, JobStatus.Blocked, JobStatus.ManualReview, JobStatus.Dead })
            Assert.False(ProductWriteSupersession.Supersedes(failed, later with { Item4 = status }));
        foreach (var status in new[] { JobStatus.Pending, JobStatus.Leased, JobStatus.RetryScheduled, JobStatus.Succeeded, JobStatus.Cancelled })
            Assert.False(ProductWriteSupersession.Supersedes(failed with { Item4 = status }, later));
        // Never a price or stock write, never an invoice job, never a read.
        foreach (var other in new[] { MarketplaceJobTypes.PriceInventorySync, MarketplaceJobTypes.StockProjectionDispatch, InvoicingJobTypes.MarketplaceDelivery, InvoicingJobTypes.InvoiceSubmit, MarketplaceJobTypes.ProductSync })
            Assert.False(ProductWriteSupersession.Supersedes(failed with { Item2 = other }, later with { Item2 = other }));
    }

    [Fact]
    public void ALaterUpdateClosesAnEarlierOneOnlyWhenItSentEveryPartTheEarlierOneDid()
    {
        // Scan C-7: refused pictures are not closed by a later update that sent only the description.
        static string Update(string content, string variants = """{"items":[]}""") =>
            System.Text.Json.JsonSerializer.Serialize(new { ProductId = Guid.Empty, UnapprovedPayloadJson = "{}", ApprovedContentPayloadJson = content, ApprovedVariantPayloadJson = variants, ApprovedDeliveryPayloadJson = """{"items":[]}""" });
        var pictures = Update("""{"items":[{"contentId":1,"images":[{"url":"https://img/1.jpg"}]}]}""");
        var description = Update("""{"items":[{"contentId":1,"description":"Yeni"}]}""");
        var both = Update("""{"items":[{"contentId":1,"description":"Yeni","images":[{"url":"https://img/2.jpg"}]}]}""");
        var desi = Update("""{"items":[]}""", """{"items":[{"barcode":"B1","dimensionalWeight":2}]}""");

        Assert.False(ProductWriteSupersession.CoversSentParts(MarketplaceJobTypes.ProductUpdate, pictures, description));
        Assert.True(ProductWriteSupersession.CoversSentParts(MarketplaceJobTypes.ProductUpdate, pictures, both));
        Assert.True(ProductWriteSupersession.CoversSentParts(MarketplaceJobTypes.ProductUpdate, description, both));
        Assert.False(ProductWriteSupersession.CoversSentParts(MarketplaceJobTypes.ProductUpdate, desi, both));
        // A publish or an archive sends the whole card: not compared.
        Assert.True(ProductWriteSupersession.CoversSentParts(MarketplaceJobTypes.ProductCreate, pictures, description));
    }

    [Fact]
    public void TheProductComesFromThePayloadAndTheSummaryNamesTheLaterJob()
    {
        var product = Guid.Parse("01a18ddd-78fb-780d-8a48-251fac20ed83");
        Assert.Equal(product, ProductWriteSupersession.ProductIdOf("{\"JobId\":\"01a1b13b-0487-79cb-a454-d70cce4734c1\",\"ProductId\":\"01a18ddd-78fb-780d-8a48-251fac20ed83\",\"Phase\":\"SUBMIT_CONTENT\"}"));
        Assert.Equal(Guid.Empty, ProductWriteSupersession.ProductIdOf("{\"SaleStatusBarcodes\":[\"X\"]}"));
        Assert.Equal(Guid.Empty, ProductWriteSupersession.ProductIdOf("not json"));
        Assert.Equal(Guid.Empty, ProductWriteSupersession.ProductIdOf("[]"));

        var later = Guid.Parse("01a1b290-4e62-794d-8c52-3cf73a002275");
        var summary = ProductWriteSupersession.Summary(DateTimeOffset.Parse("2026-11-06T20:32:44Z"));
        // Plain words: neither the later job id nor the earlier raw code (owner, 7 Nov 2026).
        Assert.DoesNotContain(later.ToString("D"), summary);
        // Istanbul time.
        Assert.Contains("23:32", summary);
        Assert.DoesNotContain("PRODUCT_CONTENT_GROUP_MISMATCH", summary);
    }

    [Fact]
    public void AWaitingDeliveryJobClosesForReviewCancellationOrConfirmationOnly()
    {
        Assert.Equal(InvoiceDeliveryJobHygiene.ManualReviewClosedCode, InvoiceDeliveryJobHygiene.CloseReason(InvoiceStatus.ManualReview, false)?.Code);
        Assert.Equal(InvoiceDeliveryGuardPolicy.CancelledJobCode, InvoiceDeliveryJobHygiene.CloseReason(InvoiceStatus.Cancelled, false)?.Code);
        Assert.Equal(InvoiceDeliveryGuardPolicy.CancelledJobCode, InvoiceDeliveryJobHygiene.CloseReason(InvoiceStatus.CancelledLocal, false)?.Code);
        Assert.Equal(InvoicingJobProcessor.DeliverySettledElsewhereCode, InvoiceDeliveryJobHygiene.CloseReason(InvoiceStatus.MarketplacePending, true)?.Code);
        Assert.Equal(InvoiceDeliveryJobHygiene.OwnerConfirmedCode, InvoiceDeliveryJobHygiene.CloseReason(InvoiceStatus.Completed, true, ownerConfirmed: true)?.Code);
        foreach (var status in new[] { InvoiceStatus.Accepted, InvoiceStatus.MarketplacePending, InvoiceStatus.MarketplaceFailed })
            Assert.Null(InvoiceDeliveryJobHygiene.CloseReason(status, false));
    }

    [Fact]
    public void AnAutomaticDeliveryIsHeldByAJobOfThePackageThatWaitsRunsOrSitsInReview()
    {
        var package = Guid.NewGuid();
        var invoice = Guid.NewGuid();
        var packageJob = $$"""{"invoiceId":"{{invoice}}","packageId":"{{package}}"}""";
        var otherPackageJob = $$"""{"invoiceId":"{{invoice}}","packageId":"{{Guid.NewGuid()}}"}""";
        var invoiceJob = $$"""{"invoiceId":"{{invoice}}"}""";

        foreach (var status in new[] { JobStatus.Pending, JobStatus.Leased, JobStatus.RetryScheduled, JobStatus.ManualReview })
        {
            Assert.True(InvoiceDeliveryJobHygiene.Holds(status, packageJob, package, reviewHolds: true));
            Assert.True(InvoiceDeliveryJobHygiene.Holds(status, invoiceJob, package, reviewHolds: true));
            Assert.False(InvoiceDeliveryJobHygiene.Holds(status, otherPackageJob, package, reviewHolds: true));
        }
        // The owner's own retry is held only by a job that still runs.
        Assert.False(InvoiceDeliveryJobHygiene.Holds(JobStatus.ManualReview, packageJob, package, reviewHolds: false));
        Assert.True(InvoiceDeliveryJobHygiene.Holds(JobStatus.Pending, packageJob, package, reviewHolds: false));
        foreach (var status in new[] { JobStatus.Succeeded, JobStatus.Cancelled, JobStatus.Blocked, JobStatus.Dead })
            Assert.False(InvoiceDeliveryJobHygiene.Holds(status, packageJob, package, reviewHolds: true));
    }

    // Live 7 Nov 2026: eleven delivery jobs of a cancelled invoice sat in review since 13 Oct.
    [Fact]
    public void AStoppedDeliveryJobClosesOnlyWhenItsInvoiceIsCancelledForGood()
    {
        var invoice = Guid.NewGuid();
        var key = $"{InvoicingJobTypes.MarketplaceDelivery}:{invoice}:{Guid.NewGuid()}:recovery:2:1";
        var payload = $$"""{"invoiceId":"{{invoice}}","packageId":"{{Guid.NewGuid()}}"}""";
        Dictionary<Guid, InvoiceStatus> With(InvoiceStatus status) => new() { [invoice] = status };

        foreach (var final in new[] { InvoiceStatus.Cancelled, InvoiceStatus.CancelledLocal, InvoiceStatus.Rejected })
        {
            Assert.True(InvoiceDeliveryGuardPolicy.InvoiceBlocksDelivery(final));
            foreach (var stopped in new[] { JobStatus.ManualReview, JobStatus.Blocked, JobStatus.Dead })
                Assert.True(InvoiceDeliveryJobHygiene.StoppedJobClosesForCancelledInvoice(stopped, key, payload, With(final)));
            // Waiting jobs are CloseWaitingJobsAsync's; running, finished and closed ones are never touched here.
            foreach (var other in new[] { JobStatus.Pending, JobStatus.Leased, JobStatus.RetryScheduled, JobStatus.Succeeded, JobStatus.Cancelled })
                Assert.False(InvoiceDeliveryJobHygiene.StoppedJobClosesForCancelledInvoice(other, key, payload, With(final)));
            Assert.Equal(InvoiceDeliveryGuardPolicy.CancelledJobCode, InvoiceDeliveryJobHygiene.CloseReason(final, false)?.Code);
        }
        // A cancellation still pending or refused, an invoice waiting for an adjustment, a live or reviewed one: stays.
        foreach (var status in Enum.GetValues<InvoiceStatus>().Where(status => status is not (InvoiceStatus.Cancelled or InvoiceStatus.CancelledLocal or InvoiceStatus.Rejected)))
            Assert.False(InvoiceDeliveryJobHygiene.StoppedJobClosesForCancelledInvoice(JobStatus.ManualReview, key, payload, With(status)));
        // A missing invoice is unknown, not cancelled.
        Assert.False(InvoiceDeliveryJobHygiene.StoppedJobClosesForCancelledInvoice(JobStatus.ManualReview, key, payload, new Dictionary<Guid, InvoiceStatus>()));
        // Jobs outside the invoice's key series (the exchange resend, the stage delivery) are left alone.
        Assert.False(InvoiceDeliveryJobHygiene.StoppedJobClosesForCancelledInvoice(JobStatus.ManualReview, $"hb-exchange-resend:{invoice}", payload, With(InvoiceStatus.Cancelled)));
        Assert.False(InvoiceDeliveryJobHygiene.StoppedJobClosesForCancelledInvoice(JobStatus.ManualReview, key, "{}", With(InvoiceStatus.Cancelled)));
    }
}

[Collection("postgres-integration")]
public sealed class ProductWriteSupersessionPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>, IAsyncLifetime
{
    private static readonly System.Collections.Concurrent.ConcurrentBag<Guid> CreatedTenants = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        var tenantIds = CreatedTenants.ToArray();
        if (tenantIds.Length == 0) return;
        await using var db = fixture.CreateContext();
        await db.IntegrationJobs.Where(x => tenantIds.Contains(x.TenantId)).ExecuteDeleteAsync();
    }

    [PostgreSqlFact]
    public async Task A_later_successful_product_update_closes_the_blocked_one_of_the_same_product_and_connection()
    {
        var now = fixture.Now;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"write-supersede-{Guid.NewGuid():N}", DisplayName = "write-supersede", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        CreatedTenants.Add(tenant.Id);
        var connection = new PlatformConnection { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = "TRENDYOL", Environment = "PRODUCTION", DisplayName = "Trendyol", ExternalStoreId = $"store-{Guid.NewGuid():N}", Status = "ACTIVE", ApiVersion = "V2", SettingsJson = "{}" };
        var product = Guid.CreateVersion7();
        var otherProduct = Guid.CreateVersion7();
        var blocked = Job(tenant, connection, MarketplaceJobTypes.ProductUpdate, product, JobStatus.Blocked, "PRODUCT_CONTENT_GROUP_MISMATCH", now.AddHours(-9));
        var later = Job(tenant, connection, MarketplaceJobTypes.ProductUpdate, product, JobStatus.Succeeded, null, now.AddHours(-3));
        var otherBlocked = Job(tenant, connection, MarketplaceJobTypes.ProductUpdate, otherProduct, JobStatus.Blocked, "PRODUCT_CONTENT_GROUP_MISMATCH", now.AddHours(-9));
        var archiveDead = Job(tenant, connection, MarketplaceJobTypes.ProductArchive, product, JobStatus.Dead, "REMOTE_5XX", now.AddHours(-8));
        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.Add(tenant);
            seed.PlatformConnections.Add(connection);
            seed.IntegrationJobs.AddRange(blocked, later, otherBlocked, archiveDead);
            await seed.SaveChangesAsync();
        }

        await using (var db = fixture.CreateContext())
            await ProductWriteSupersession.CloseAsync(db, now, CancellationToken.None);

        await using var verify = fixture.CreateContext();
        var closed = await verify.IntegrationJobs.AsNoTracking().SingleAsync(x => x.Id == blocked.Id);
        Assert.Equal(JobStatus.Cancelled, closed.Status);
        Assert.Equal(ProductWriteSupersession.Code, closed.LastErrorCode);
        // Plain words (owner, 7 Nov 2026): the later update and its time, no job id and no raw code.
        Assert.DoesNotContain(later.Id.ToString("D"), closed.LastErrorSummary);
        Assert.DoesNotContain("PRODUCT_CONTENT_GROUP_MISMATCH", closed.LastErrorSummary);
        Assert.Contains("sonraki güncellemesi", closed.LastErrorSummary);
        // No later success of the other product; an update's success does not close an archive.
        Assert.Equal(JobStatus.Blocked, (await verify.IntegrationJobs.AsNoTracking().SingleAsync(x => x.Id == otherBlocked.Id)).Status);
        Assert.Equal(JobStatus.Dead, (await verify.IntegrationJobs.AsNoTracking().SingleAsync(x => x.Id == archiveDead.Id)).Status);
        Assert.Equal(JobStatus.Succeeded, (await verify.IntegrationJobs.AsNoTracking().SingleAsync(x => x.Id == later.Id)).Status);
    }

    private static IntegrationJob Job(Tenant tenant, PlatformConnection connection, string jobType, Guid productId, JobStatus status, string? errorCode, DateTimeOffset createdAt)
    {
        var id = Guid.CreateVersion7();
        var key = $"product-write:{connection.Id:N}:{productId:N}:{id:N}";
        return new IntegrationJob
        {
            Id = id,
            TenantId = tenant.Id,
            ConnectionId = connection.Id,
            JobType = jobType,
            PayloadJson = $$"""{"JobId":"{{id}}","ProductId":"{{productId}}","ProfileId":"{{Guid.CreateVersion7()}}"}""",
            PayloadVersion = 1,
            PayloadHash = "h",
            JobDedupKey = key,
            EffectIdempotencyKey = key,
            Status = status,
            LastErrorCode = errorCode,
            AvailableAt = createdAt,
            CorrelationId = "write",
            CreatedAt = createdAt,
            Version = 1
        };
    }
}
