using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// İşlem Takibi detail windows (19 Nov 2026): the lists behind the monitor's and the API health cells' numbers. Each list's
// total is the cell's own number (same rows, same rule); another tenant's connection is not found.
[Collection("postgres-integration")]
public sealed class MonitorDetailListsPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>, IAsyncLifetime
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
    public async Task InvalidOffers_TotalIsTheMonitorsNumber_WithSearchReasonAndPages()
    {
        var tenant = NewTenant("invalid-offers");
        var other = NewTenant("invalid-offers-other");
        var n11 = NewConnection(tenant, "N11");
        var pazarama = NewConnection(tenant, "PAZARAMA");
        var foreign = NewConnection(other, "N11");
        var (product, zero) = NewVariant(tenant, "IO-ZERO", "Bluz Mavi");
        var noOffer = NewVariant(tenant, "IO-NOOFFER", "Gömlek").Variant;
        var third = NewVariant(tenant, "IO-THIRD", "Etek").Variant;
        var fixedNow = fixture.TimeProvider.GetUtcNow();
        var offer = new ChannelOffer { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = n11.Id, VariantId = zero.Id, ListPrice = 0m, SalePrice = 0m, Currency = "TRY", VatRate = 20m, VatInclusion = "INCLUDED", RoundingMode = "HALF_EVEN", Status = "ACTIVE" };
        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.AddRange(tenant, other);
            seed.PlatformConnections.AddRange(n11, pazarama, foreign);
            seed.Products.AddRange(product, Card(tenant, noOffer, "Gömlek"), Card(tenant, third, "Etek"));
            seed.ProductVariants.AddRange(zero, noOffer, third);
            seed.ChannelOffers.Add(offer);
            seed.OperationalIssues.AddRange(
                Issue(tenant, n11, zero, PriceStockOfferIssueCodes.ChannelOfferInvalid, "Satış fiyatı 0; teklif gönderilmedi.", fixedNow.AddMinutes(-1)),
                Issue(tenant, n11, noOffer, PriceStockOfferIssueCodes.ChannelOfferInvalid, "Teklif yok.", fixedNow.AddMinutes(-2)),
                Issue(tenant, n11, third, PriceStockOfferIssueCodes.PazaramaBarcodeInvalid, "Barkod geçersiz.", fixedNow.AddMinutes(-3)),
                Issue(tenant, n11, Guid.CreateVersion7(), PriceStockOfferIssueCodes.ChannelOfferInvalid, "Kapandı.", fixedNow, IssueStatus.Resolved),
                Issue(tenant, pazarama, zero, PriceStockOfferIssueCodes.PazaramaBarcodeInvalid, "Başka bağlantı.", fixedNow),
                Issue(other, foreign, zero, PriceStockOfferIssueCodes.ChannelOfferInvalid, "Başka kiracı.", fixedNow));
            await seed.SaveChangesAsync();
        }

        await using var db = fixture.CreateContext();
        var service = new PriceStockMonitorService(db, new ConfigurationBuilder().Build(), fixture.TimeProvider);
        var monitorRow = (await service.MonitorAsync(tenant.Id, CancellationToken.None)).Connections.Single(x => x.ConnectionId == n11.Id);

        var all = (await service.InvalidOffersAsync(tenant.Id, n11.Id, null, null, 1, 50, CancellationToken.None)).Value!;
        Assert.Equal(3, monitorRow.InvalidOffers);
        Assert.Equal(monitorRow.InvalidOffers, all.TotalCount);
        Assert.Equal(3, all.FilteredCount);
        Assert.Equal(PriceStockOfferIssueCodes.All, all.ReasonCodes);
        Assert.Equal([new PriceStockReasonCountView(PriceStockOfferIssueCodes.ChannelOfferInvalid, 2), new PriceStockReasonCountView(PriceStockOfferIssueCodes.PazaramaBarcodeInvalid, 1)], all.Reasons);
        var first = all.Items[0];
        Assert.Equal((offer.Id, zero.Id, product.Id, "IO-ZERO", "Bluz Mavi", "Satış fiyatı 0; teklif gönderilmedi."), (first.OfferId!.Value, first.VariantId, first.ProductId!.Value, first.Sku!, first.ProductTitle!, first.ReasonSummary));
        Assert.Equal((0m, 0m, "TRY", 150m, 120m), (first.OfferListPrice!.Value, first.OfferSalePrice!.Value, first.Currency!, first.PanelListPrice!.Value, first.PanelSalePrice!.Value));
        Assert.Equal("Mavi / M", first.VariantLabel);
        var withoutOffer = all.Items.Single(x => x.VariantId == noOffer.Id);
        Assert.Null(withoutOffer.OfferId);
        Assert.Null(withoutOffer.OfferSalePrice);

        var searched = (await service.InvalidOffersAsync(tenant.Id, n11.Id, "gömlek", null, 1, 50, CancellationToken.None)).Value!;
        Assert.Equal(noOffer.Id, Assert.Single(searched.Items).VariantId);
        Assert.Equal(3, searched.TotalCount);
        Assert.Equal(1, searched.FilteredCount);
        var bySku = (await service.InvalidOffersAsync(tenant.Id, n11.Id, "io-third", null, 1, 50, CancellationToken.None)).Value!;
        Assert.Equal(third.Id, Assert.Single(bySku.Items).VariantId);
        var byBarcode = (await service.InvalidOffersAsync(tenant.Id, n11.Id, zero.Barcode, null, 1, 50, CancellationToken.None)).Value!;
        Assert.Equal(zero.Id, Assert.Single(byBarcode.Items).VariantId);
        var byReason = (await service.InvalidOffersAsync(tenant.Id, n11.Id, null, PriceStockOfferIssueCodes.PazaramaBarcodeInvalid, 1, 50, CancellationToken.None)).Value!;
        Assert.Equal(third.Id, Assert.Single(byReason.Items).VariantId);
        var secondPage = (await service.InvalidOffersAsync(tenant.Id, n11.Id, null, null, 2, 2, CancellationToken.None)).Value!;
        Assert.Equal(third.Id, Assert.Single(secondPage.Items).VariantId);
        Assert.Equal((2, 2, 3), (secondPage.Page, secondPage.PageSize, secondPage.FilteredCount));

        var foreignAnswer = await service.InvalidOffersAsync(tenant.Id, foreign.Id, null, null, 1, 50, CancellationToken.None);
        Assert.Equal(("CONNECTION_NOT_FOUND", 404), (foreignAnswer.Error!.Code, foreignAnswer.Error.Status));
    }

    [PostgreSqlFact]
    public async Task FailedPushesAndLostLinks_TotalsAreTheMonitorsNumbers()
    {
        var tenant = NewTenant("failed-pushes");
        var other = NewTenant("failed-pushes-other");
        var shopify = NewConnection(tenant, "SHOPIFY");
        var foreign = NewConnection(other, "SHOPIFY");
        var (product, lost) = NewVariant(tenant, "LL-1", "Kayıp Ürün");
        var refused = NewVariant(tenant, "LL-2", "Reddedilen").Variant;
        var now = fixture.TimeProvider.GetUtcNow();
        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.AddRange(tenant, other);
            seed.PlatformConnections.AddRange(shopify, foreign);
            seed.Products.AddRange(product, Card(tenant, refused, "Reddedilen"));
            seed.ProductVariants.AddRange(lost, refused);
            seed.MarketplaceVariantLinks.AddRange(
                new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = shopify.Id, VariantId = lost.Id, ExternalId = "LL-EXT-1", HealthStatus = MarketplaceVariantLinkHealth.NotFound, HealthChangedAt = now.AddHours(-2) },
                new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ConnectionId = shopify.Id, VariantId = refused.Id, ExternalId = "LL-EXT-2", HealthStatus = MarketplaceVariantLinkHealth.Rejected, HealthChangedAt = now.AddHours(-1) });
            seed.IntegrationJobs.AddRange(
                Job(tenant, shopify, MarketplaceJobTypes.PriceInventorySync, JobStatus.Blocked, now.AddDays(-1), "SHOPIFY_REQUEST_REJECTED"),
                Job(tenant, shopify, MarketplaceJobTypes.PriceInventorySync, JobStatus.Dead, now.AddDays(-2), "SHOPIFY_REMOTE_ERROR"),
                Job(tenant, shopify, MarketplaceJobTypes.PriceInventorySync, JobStatus.Dead, now.AddDays(-9), "SHOPIFY_REMOTE_ERROR"),
                Job(tenant, shopify, MarketplaceJobTypes.PriceInventorySync, JobStatus.ManualReview, now.AddDays(-20), "EXTERNAL_EFFECT_UNKNOWN"),
                Job(tenant, shopify, MarketplaceJobTypes.OrderSync, JobStatus.Dead, now.AddDays(-1), "SHOPIFY_REMOTE_ERROR"));
            await seed.SaveChangesAsync();
        }

        await using var db = fixture.CreateContext();
        var service = new PriceStockMonitorService(db, new ConfigurationBuilder().Build(), fixture.TimeProvider);
        var row = Assert.Single((await service.MonitorAsync(tenant.Id, CancellationToken.None)).Connections);

        var failed = (await service.FailedPushesAsync(tenant.Id, shopify.Id, null, 1, 50, CancellationToken.None)).Value!;
        Assert.Equal((2, row.FailedPushes7d), (failed.TotalCount, failed.TotalCount));
        Assert.Equal("SHOPIFY_REQUEST_REJECTED", failed.Items[0].ErrorCode);
        var review = (await service.FailedPushesAsync(tenant.Id, shopify.Id, "manual-review", 1, 50, CancellationToken.None)).Value!;
        Assert.Equal((1, row.ManualReviewPushes), (review.TotalCount, review.TotalCount));
        Assert.Single((await service.FailedPushesAsync(tenant.Id, shopify.Id, null, 2, 1, CancellationToken.None)).Value!.Items);
        await Assert.ThrowsAsync<ArgumentException>(() => service.FailedPushesAsync(tenant.Id, shopify.Id, "other", 1, 50, CancellationToken.None));

        var notFound = (await service.LostLinksAsync(tenant.Id, shopify.Id, "not_found", 1, 50, CancellationToken.None)).Value!;
        Assert.Equal((1, row.NotFoundLinks), (notFound.TotalCount, notFound.TotalCount));
        Assert.Equal(("LL-1", "Kayıp Ürün", product.Id), (notFound.Items[0].Sku!, notFound.Items[0].ProductTitle!, notFound.Items[0].ProductId!.Value));
        var rejected = (await service.LostLinksAsync(tenant.Id, shopify.Id, MarketplaceVariantLinkHealth.Rejected, 1, 50, CancellationToken.None)).Value!;
        Assert.Equal((1, row.RejectedLinks), (rejected.TotalCount, rejected.TotalCount));
        Assert.Equal(row.NotFoundLinks + row.RejectedLinks, (await service.LostLinksAsync(tenant.Id, shopify.Id, null, 1, 50, CancellationToken.None)).Value!.TotalCount);

        Assert.Equal(404, (await service.FailedPushesAsync(tenant.Id, foreign.Id, null, 1, 50, CancellationToken.None)).Error!.Status);
        Assert.Equal(404, (await service.LostLinksAsync(tenant.Id, foreign.Id, null, 1, 50, CancellationToken.None)).Error!.Status);
    }

    [PostgreSqlFact]
    public async Task ApiHealthAttempts_TotalsAreTheErrorRateCell_WithClassAndPages()
    {
        var tenant = NewTenant("api-health-attempts");
        var other = NewTenant("api-health-attempts-other");
        var pazarama = NewConnection(tenant, "PAZARAMA");
        var foreign = NewConnection(other, "PAZARAMA");
        var now = fixture.TimeProvider.GetUtcNow();
        var codes = new (string? Code, bool Succeeded, int MinutesAgo)[]
        {
            (null, true, 5), (null, true, 6),
            ("PAZARAMA_TIMEOUT", false, 10), ("PAZARAMA_REMOTE_ERROR", false, 11), ("PAZARAMA_NETWORK_ERROR", false, 12),
            ("PAZARAMA_REQUEST_REJECTED", false, 13),
            ("PAZARAMA_RATE_LIMITED", false, 14),
            ("PAZARAMA_TOKEN_INVALID", false, 15),
            ("PAZARAMA_REMOTE_ERROR", false, 60 * 30)
        };
        await using (var seed = fixture.CreateContext())
        {
            seed.Tenants.AddRange(tenant, other);
            seed.PlatformConnections.AddRange(pazarama, foreign);
            foreach (var (code, succeeded, minutesAgo) in codes)
            {
                var job = Job(tenant, pazarama, MarketplaceJobTypes.PazaramaOrderSync, succeeded ? JobStatus.Succeeded : JobStatus.RetryScheduled, now.AddMinutes(-minutesAgo), code);
                seed.IntegrationJobs.Add(job);
                seed.JobAttempts.Add(new JobAttempt { Id = Guid.CreateVersion7(), TenantId = tenant.Id, JobId = job.Id, AttemptNumber = 1, StartedAt = now.AddMinutes(-minutesAgo), CompletedAt = now.AddMinutes(-minutesAgo).AddSeconds(1), Succeeded = succeeded, ErrorCode = code, ErrorSummary = code is null ? null : $"{code} özeti" });
            }
            await seed.SaveChangesAsync();
        }

        await using var db = fixture.CreateContext();
        var service = new ApiHealthSignalsService(db, fixture.TimeProvider);
        var cell = Assert.Single((await service.SignalsAsync(tenant.Id, CancellationToken.None)).Connections).ErrorRate;

        var all = (await service.AttemptsAsync(tenant.Id, pazarama.Id, null, 1, 50, CancellationToken.None)).Value!;
        Assert.Equal(cell, all.Totals);
        Assert.Equal(new ApiHealthErrorRateSignal(8, 6, 3, 1, 1, 1), all.Totals);
        Assert.Equal(6, all.TotalCount);
        Assert.Equal(ApiHealthSignalPolicy.Classes, all.Classes);
        Assert.Equal("PAZARAMA_TIMEOUT", all.Items[0].ErrorCode);
        Assert.Equal(ApiHealthSignalPolicy.ClassMarketplace, all.Items[0].Class);

        var marketplace = (await service.AttemptsAsync(tenant.Id, pazarama.Id, "MARKETPLACE", 1, 50, CancellationToken.None)).Value!;
        Assert.Equal((cell.RemoteSide, cell.RemoteSide), (marketplace.TotalCount, marketplace.Items.Count));
        Assert.All(marketplace.Items, item => Assert.Equal(ApiHealthSignalPolicy.ClassMarketplace, item.Class));
        Assert.Equal(cell.OurSide, (await service.AttemptsAsync(tenant.Id, pazarama.Id, "ours", 1, 50, CancellationToken.None)).Value!.TotalCount);
        Assert.Equal(cell.RateLimited, (await service.AttemptsAsync(tenant.Id, pazarama.Id, "rateLimited", 1, 50, CancellationToken.None)).Value!.TotalCount);
        Assert.Equal(cell.WorkflowWait, (await service.AttemptsAsync(tenant.Id, pazarama.Id, "workflowOther", 1, 50, CancellationToken.None)).Value!.TotalCount);
        var page = (await service.AttemptsAsync(tenant.Id, pazarama.Id, null, 3, 2, CancellationToken.None)).Value!;
        Assert.Equal(["PAZARAMA_RATE_LIMITED", "PAZARAMA_TOKEN_INVALID"], page.Items.Select(x => x.ErrorCode));
        await Assert.ThrowsAsync<ArgumentException>(() => service.AttemptsAsync(tenant.Id, pazarama.Id, "remote", 1, 50, CancellationToken.None));

        var foreignAnswer = await service.AttemptsAsync(tenant.Id, foreign.Id, null, 1, 50, CancellationToken.None);
        Assert.Equal(("CONNECTION_NOT_FOUND", 404), (foreignAnswer.Error!.Code, foreignAnswer.Error.Status));
    }

    private static OperationalIssue Issue(Tenant tenant, PlatformConnection connection, ProductVariant variant, string code, string summary, DateTimeOffset at, IssueStatus status = IssueStatus.Open) =>
        Issue(tenant, connection, variant.Id, code, summary, at, status);

    private static OperationalIssue Issue(Tenant tenant, PlatformConnection connection, Guid variantId, string code, string summary, DateTimeOffset at, IssueStatus status = IssueStatus.Open) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenant.Id,
        DedupeKey = $"stock-projection-offer-invalid:{connection.Id:N}:{variantId:N}",
        Code = code,
        Summary = summary,
        Status = status,
        FirstSeenAt = at,
        LastSeenAt = at,
        OccurrenceCount = 1
    };

    private static IntegrationJob Job(Tenant tenant, PlatformConnection connection, string jobType, JobStatus status, DateTimeOffset at, string? code)
    {
        var id = Guid.CreateVersion7();
        return new IntegrationJob
        {
            Id = id,
            TenantId = tenant.Id,
            ConnectionId = connection.Id,
            JobType = jobType,
            PayloadJson = "{}",
            PayloadVersion = 1,
            PayloadHash = "h",
            JobDedupKey = $"monitor-detail:{id:N}",
            EffectIdempotencyKey = $"monitor-detail:{id:N}",
            Status = status,
            AvailableAt = at,
            CorrelationId = "monitor-detail",
            CreatedAt = at.AddSeconds(-5),
            StartedAt = at,
            CompletedAt = at.AddSeconds(1),
            LastErrorCode = code,
            LastErrorSummary = code is null ? null : $"{code} özeti",
            AttemptCount = 1,
            Version = 1
        };
    }

    private static Tenant NewTenant(string code)
    {
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"{code}-{Guid.NewGuid():N}", DisplayName = code, Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, Version = 1 };
        CreatedTenants.Add(tenant.Id);
        return tenant;
    }

    private static PlatformConnection NewConnection(Tenant tenant, string platformCode) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenant.Id,
        PublicId = Guid.CreateVersion7(),
        PlatformCode = platformCode,
        Environment = "PRODUCTION",
        DisplayName = $"{platformCode} detay testi",
        ExternalStoreId = $"store-{Guid.NewGuid():N}",
        Status = "ACTIVE",
        ApiVersion = "V1",
        SettingsJson = "{}",
        Version = 1
    };

    private static (Product Product, ProductVariant Variant) NewVariant(Tenant tenant, string sku, string title)
    {
        var now = DateTimeOffset.UtcNow;
        var product = new Product { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Title = title, Description = "Detay testi", Status = ProductStatus.Active, CreatedAt = now, UpdatedAt = now };
        var barcode = $"869{Guid.NewGuid().ToString("N")[..10]}";
        var variant = new ProductVariant { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ProductId = product.Id, Sku = sku, SkuNormalized = sku.ToUpperInvariant(), Barcode = barcode, BarcodeNormalized = barcode.ToUpperInvariant(), OptionSignature = "Renk: Mavi | Beden: M", DefaultListPrice = 150m, DefaultSalePrice = 120m, CreatedAt = now, UpdatedAt = now };
        return (product, variant);
    }

    // The card a variant made by NewVariant belongs to (NewVariant returns it, but only the first one is kept by the caller).
    private static Product Card(Tenant tenant, ProductVariant variant, string title) =>
        new() { Id = variant.ProductId, TenantId = tenant.Id, Title = title, Description = "Detay testi", Status = ProductStatus.Active, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
}
