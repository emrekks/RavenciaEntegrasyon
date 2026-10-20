using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Audit 2026-10-19 W4 and İ13: the Returns page's cards, filters, counts and paging come from the
// server, and returns carry an amount calculated from the order.
[Collection("postgres-integration")]
public sealed class ReturnListPostgreSqlTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    [PostgreSqlFact]
    public async Task Summary_counts_cards_per_tab_with_every_filter_but_the_tab_and_lists_the_tabs_choices()
    {
        var seed = await SeedAsync();
        try
        {
            await using var db = fixture.CreateContext();
            var service = NewService(db);

            var all = await service.ReturnSummaryAsync(seed.TenantId, new ReturnListQuery(), CancellationToken.None);
            Assert.Equal(4, all.TabCounts["ALL"]);
            Assert.Equal(1, all.TabCounts["ACTION_REQUIRED"]);
            Assert.Equal(2, all.TabCounts["APPROVED"]);
            Assert.Equal(1, all.TabCounts["SHIPPING"]);
            Assert.Equal(0, all.TabCounts["REVIEW"]);
            Assert.Equal(["TRENDYOL", "N11"], all.Platforms.Select(x => x.PlatformCode));
            Assert.Equal(["Aras Kargo", "Yurtiçi"], all.CargoProviders);
            Assert.Equal(["SIZE", "Beğenmedim"], all.Reasons.Select(x => x.Value));
            Assert.Equal(["FATURA_BILINMIYOR", "FATURA_KONTROLDE"], all.InvoiceStatuses);
            Assert.Equal(4, all.UnfilteredCount);

            var n11 = await service.ReturnSummaryAsync(seed.TenantId, new ReturnListQuery(Platforms: ["N11"]), CancellationToken.None);
            Assert.Equal((1, 1), (n11.TabCounts["ALL"], n11.TabCounts["APPROVED"]));
            // The choices still come from the unfiltered tab.
            Assert.Equal(4, n11.UnfilteredCount);
            Assert.Equal(2, n11.Platforms.Count);

            var invoiced = await service.ReturnSummaryAsync(seed.TenantId, new ReturnListQuery(InvoiceStatus: "FATURA_KONTROLDE"), CancellationToken.None);
            Assert.Equal((1, 1), (invoiced.TabCounts["ALL"], invoiced.TabCounts["SHIPPING"]));

            var approvedTab = await service.ReturnSummaryAsync(seed.TenantId, new ReturnListQuery(Status: "APPROVED"), CancellationToken.None);
            Assert.Equal(4, approvedTab.TabCounts["ALL"]);
            Assert.Equal(2, approvedTab.UnfilteredCount);
            Assert.Equal(["Aras Kargo"], approvedTab.CargoProviders);
        }
        finally
        {
            await CleanupAsync(seed.TenantId);
        }
    }

    [PostgreSqlFact]
    public async Task List_pages_cards_by_cursor_or_page_and_carries_the_refund_amounts()
    {
        var seed = await SeedAsync();
        try
        {
            await using var db = fixture.CreateContext();
            var service = NewService(db);

            var first = await service.ReturnsAsync(seed.TenantId, 2, null, new ReturnListQuery(), false, CancellationToken.None);
            Assert.Equal(4, first.TotalCount);
            Assert.True(first.HasMore);
            Assert.Equal([$"{seed.SplitClaimId:D}:waiting", $"{seed.SplitClaimId:D}:approved"], first.Items.Select(x => x.RowKey));
            var waiting = first.Items[0];
            var approved = first.Items[1];
            Assert.Equal("ACTION_REQUIRED", waiting.Status);
            Assert.Equal([seed.SecondLineId], waiting.Lines!.Select(x => x.Id));
            // 200 gross, 40 discount: a 50 unit is worth 40, a 100 unit 80.
            Assert.Equal(40m, waiting.RefundAmount);
            Assert.Equal("APPROVED", approved.Status);
            Assert.Equal(80m, approved.RefundAmount);

            var second = await service.ReturnsAsync(seed.TenantId, 2, first.NextCursor, new ReturnListQuery(), false, CancellationToken.None);
            Assert.False(second.HasMore);
            Assert.Null(second.NextCursor);
            Assert.Equal([seed.TransitClaimId, seed.N11ClaimId], second.Items.Select(x => x.Id));
            Assert.Equal(100m, second.Items[0].RefundAmount);
            var jumped = await service.ReturnsAsync(seed.TenantId, 2, null, new ReturnListQuery(), false, CancellationToken.None, page: 2);
            Assert.Equal(second.Items.Select(x => x.Id), jumped.Items.Select(x => x.Id));

            var detail = await service.ReturnAsync(seed.TenantId, seed.SplitClaimId, CancellationToken.None);
            Assert.Equal(120m, detail.Value!.RefundAmount);
            Assert.Equal([40m, 80m], detail.Value.Lines!.Select(x => x.RefundAmount!.Value).Order());
        }
        finally
        {
            await CleanupAsync(seed.TenantId);
        }
    }

    [PostgreSqlFact]
    public async Task List_applies_the_tab_search_filters_and_the_approved_sort()
    {
        var seed = await SeedAsync();
        try
        {
            await using var db = fixture.CreateContext();
            var service = NewService(db);

            var approved = await service.ReturnsAsync(seed.TenantId, 20, null, new ReturnListQuery(Status: "APPROVED", Sort: "ORDERED_ASC"), false, CancellationToken.None);
            Assert.Equal([$"{seed.SplitClaimId:D}:approved", null], approved.Items.Select(x => x.RowKey));
            Assert.Equal([seed.SplitClaimId, seed.N11ClaimId], approved.Items.Select(x => x.Id));
            var newestFirst = await service.ReturnsAsync(seed.TenantId, 20, null, new ReturnListQuery(Status: "APPROVED", Sort: "ORDERED_DESC"), false, CancellationToken.None);
            Assert.Equal([seed.N11ClaimId, seed.SplitClaimId], newestFirst.Items.Select(x => x.Id));

            var actionRequired = await service.ReturnsAsync(seed.TenantId, 20, null, new ReturnListQuery(Status: "ACTION_REQUIRED"), false, CancellationToken.None);
            Assert.Equal([$"{seed.SplitClaimId:D}:waiting"], actionRequired.Items.Select(x => x.RowKey));

            var byName = await service.ReturnsAsync(seed.TenantId, 20, null, new ReturnListQuery(Search: "Ayşe"), false, CancellationToken.None);
            Assert.Equal(2, byName.TotalCount);
            Assert.All(byName.Items, item => Assert.Equal(seed.SplitClaimId, item.Id));
            var byEmail = await service.ReturnsAsync(seed.TenantId, 20, null, new ReturnListQuery(Search: "ayse@example"), false, CancellationToken.None);
            Assert.Empty(byEmail.Items);
            var bySku = await service.ReturnsAsync(seed.TenantId, 20, null, new ReturnListQuery(Search: "RET-SKU-3"), false, CancellationToken.None);
            Assert.Equal([seed.TransitClaimId], bySku.Items.Select(x => x.Id));
            // The reason is searched ignoring case the Turkish way, whatever the database locale (O3).
            var byReason = await service.ReturnsAsync(seed.TenantId, 20, null, new ReturnListQuery(Search: "BEĞENMEDİM"), false, CancellationToken.None);
            Assert.Equal([seed.TransitClaimId], byReason.Items.Select(x => x.Id));
            var byReasonText = await service.ReturnsAsync(seed.TenantId, 20, null, new ReturnListQuery(Search: "KÜÇÜK"), false, CancellationToken.None);
            Assert.All(byReasonText.Items, item => Assert.Equal(seed.SplitClaimId, item.Id));
            Assert.Equal(2, byReasonText.TotalCount);

            var filtered = await service.ReturnsAsync(seed.TenantId, 20, null, new ReturnListQuery(Platforms: ["TRENDYOL"], Cargo: "Aras Kargo", ReasonKey: "SIZE"), false, CancellationToken.None);
            Assert.Equal(2, filtered.TotalCount);
            var invoiced = await service.ReturnsAsync(seed.TenantId, 20, null, new ReturnListQuery(InvoiceStatus: "FATURA_KONTROLDE"), false, CancellationToken.None);
            Assert.Equal([seed.TransitClaimId], invoiced.Items.Select(x => x.Id));
            var none = await service.ReturnsAsync(seed.TenantId, 20, null, new ReturnListQuery(Platforms: ["__NONE__"]), false, CancellationToken.None);
            Assert.Equal(0, none.TotalCount);
        }
        finally
        {
            await CleanupAsync(seed.TenantId);
        }
    }

    private static MarketplaceSalesService NewService(AppDbContext db) =>
        new(db, new CursorCodec(new EphemeralDataProtectionProvider(), TimeProvider.System), new ConfigurationBuilder().Build(), null!, null!, null!, null!, TimeProvider.System);

    private sealed record Seed(Guid TenantId, Guid SplitClaimId, Guid TransitClaimId, Guid N11ClaimId, Guid FirstLineId, Guid SecondLineId);

    private async Task<Seed> SeedAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Code = $"return-list-{Guid.NewGuid():N}", DisplayName = "return-list", Status = RecordStatus.Active, Timezone = "Europe/Istanbul", CreatedAt = now, UpdatedAt = now, Version = 1 };
        PlatformConnection Connection(string platform, string name) => new() { Id = Guid.CreateVersion7(), TenantId = tenant.Id, PublicId = Guid.CreateVersion7(), PlatformCode = platform, Environment = "PRODUCTION", DisplayName = name, ExternalStoreId = $"store-{Guid.NewGuid():N}", Status = "ACTIVE", ApiVersion = "V1" };
        var trendyol = Connection("TRENDYOL", "Trendyol Mağaza");
        var n11 = Connection("N11", "N11 Mağaza");
        Order NewOrder(PlatformConnection connection, string number, decimal gross, decimal discount, string customer, DateTimeOffset orderedAt) => new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            ConnectionId = connection.Id,
            ExternalOrderId = number,
            OrderNumber = number,
            Currency = "TRY",
            GrossAmount = gross,
            DiscountAmount = discount,
            NetAmount = gross - discount,
            OrderedAt = orderedAt,
            LastRemoteModifiedAt = orderedAt,
            CustomerSnapshotJson = customer,
            ShipmentAddressSnapshotJson = "{}",
            InvoiceAddressSnapshotJson = "{}",
            DerivedStatus = "DELIVERED",
            CreatedAt = now,
            UpdatedAt = now,
            Version = 1
        };
        OrderLine NewLine(Order order, string sku, decimal quantity, decimal price) => new() { Id = Guid.CreateVersion7(), TenantId = tenant.Id, OrderId = order.Id, ExternalLineId = sku, Sku = sku, TitleSnapshot = $"Ürün {sku}", OrderedQuantity = quantity, DeliveredQuantity = quantity, ShippedQuantity = quantity, UnitPrice = price, VatRate = 20m, RawStatus = "Delivered" };
        // Claims made in the same millisecond get random-order ids; one millisecond apart they sort in the order made.
        var claimTick = 0;
        ReturnClaim NewClaim(PlatformConnection connection, Order order, ReturnClaimStatus status, string raw, string? cargo, string? reasonCode, string? reasonText) => new()
        {
            Id = Guid.CreateVersion7(now.AddMilliseconds(++claimTick)),
            TenantId = tenant.Id,
            ConnectionId = connection.Id,
            OrderId = order.Id,
            ExternalClaimId = $"claim-{Guid.NewGuid():N}",
            Status = status,
            RawStatus = raw,
            CargoProviderName = cargo,
            ReasonCode = reasonCode,
            ReasonText = reasonText,
            LastRemoteModifiedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
            Version = 1
        };
        ReturnLine NewReturnLine(ReturnClaim claim, OrderLine line, decimal quantity, string? raw) => new() { Id = Guid.CreateVersion7(), TenantId = tenant.Id, ClaimId = claim.Id, OrderLineId = line.Id, ExternalLineId = $"r-{line.Sku}", Quantity = quantity, RawStatus = raw };

        var splitOrder = NewOrder(trendyol, "RET-1001", 200m, 40m, """{"customerFirstName":"Ayşe","customerLastName":"Yılmaz","customerEmail":"ayse@example.com"}""", now.AddDays(-10));
        var firstLine = NewLine(splitOrder, "RET-SKU-1", 1m, 100m);
        var secondLine = NewLine(splitOrder, "RET-SKU-2", 2m, 50m);
        var transitOrder = NewOrder(trendyol, "RET-1002", 100m, 0m, """{"customerFirstName":"Mehmet"}""", now.AddDays(-8));
        var transitLine = NewLine(transitOrder, "RET-SKU-3", 1m, 100m);
        var n11Order = NewOrder(n11, "RET-1003", 50m, 0m, """{"customerFirstName":"Can"}""", now.AddDays(-5));
        var n11Line = NewLine(n11Order, "RET-SKU-4", 1m, 50m);
        var splitClaim = NewClaim(trendyol, splitOrder, ReturnClaimStatus.ActionRequired, "WaitingInAction", " Aras Kargo", "SIZE", "Beden küçük");
        var transitClaim = NewClaim(trendyol, transitOrder, ReturnClaimStatus.InTransit, "InTransit", "Yurtiçi", null, "Beğenmedim");
        var n11Claim = NewClaim(n11, n11Order, ReturnClaimStatus.Approved, "Approved", null, "SIZE", null);

        await using var db = fixture.CreateContext();
        db.Tenants.Add(tenant);
        db.PlatformConnections.AddRange(trendyol, n11);
        db.Orders.AddRange(splitOrder, transitOrder, n11Order);
        db.OrderLines.AddRange(firstLine, secondLine, transitLine, n11Line);
        db.ShipmentPackages.Add(new ShipmentPackage
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            ConnectionId = trendyol.Id,
            OrderId = transitOrder.Id,
            ExternalPackageId = "ret-pkg-2",
            GrossAmount = 100m,
            NetAmount = 100m,
            Status = ShipmentPackageStatus.Delivered,
            RawStatus = "Delivered",
            StatusOccurredAt = now.AddDays(-7),
            MarketplaceInvoiceStatus = MarketplaceInvoiceStatus.Invoiced,
            CreatedAt = now,
            UpdatedAt = now,
            Version = 1
        });
        db.ReturnClaims.AddRange(splitClaim, transitClaim, n11Claim);
        db.ReturnLines.AddRange(
            NewReturnLine(splitClaim, firstLine, 1m, "Accepted"),
            NewReturnLine(splitClaim, secondLine, 1m, "WaitingInAction"),
            NewReturnLine(transitClaim, transitLine, 1m, null),
            NewReturnLine(n11Claim, n11Line, 1m, null));
        await db.SaveChangesAsync();
        return new(tenant.Id, splitClaim.Id, transitClaim.Id, n11Claim.Id, firstLine.Id, secondLine.Id);
    }

    // Every row these tests create is removed; nothing here queues a job, but any found is removed too.
    private async Task CleanupAsync(Guid tenantId)
    {
        await using var db = fixture.CreateContext();
        await db.JobAttempts.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.IntegrationJobs.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.ReturnDecisions.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.ReturnLines.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.ReturnClaims.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.ShipmentPackages.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.OrderLines.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.Orders.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.PlatformConnections.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.IntegrationOutboxEvents.Where(row => row.TenantId == tenantId).ExecuteDeleteAsync();
        await db.Tenants.Where(row => row.Id == tenantId).ExecuteDeleteAsync();
    }
}
