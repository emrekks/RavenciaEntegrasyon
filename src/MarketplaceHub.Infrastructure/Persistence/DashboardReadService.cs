using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

public sealed class DashboardReadService(AppDbContext db, TimeProvider timeProvider) : IDashboardReadService
{
    private const decimal LowStockThreshold = 5m;

    private static readonly (string ResourceType, string Label, string Kind, string[] JobTypes, bool Required)[] SyncDefinitions =
    [
        ("orders", "Siparişler", "orders", [MarketplaceJobTypes.OrderSync, MarketplaceJobTypes.OrderRecoverySync, MarketplaceJobTypes.OrderStatusSync, MarketplaceJobTypes.OrderReconciliation, MarketplaceJobTypes.WebhookIngest, MarketplaceJobTypes.ShipmentAction], true),
        ("returns", "İadeler", "returns", [MarketplaceJobTypes.ReturnSync, MarketplaceJobTypes.ReturnStatusSync, MarketplaceJobTypes.ReturnReconciliation, MarketplaceJobTypes.ReturnAction], true),
        ("inventory", "Stok", "stock", [MarketplaceJobTypes.PriceInventorySync, MarketplaceJobTypes.StockProjectionDispatch, MarketplaceJobTypes.StockReconciliation], true),
        ("products", "Ürünler", "products", [MarketplaceJobTypes.ProductSync, MarketplaceJobTypes.ProductCreate, MarketplaceJobTypes.ProductUpdate, MarketplaceJobTypes.ProductArchive, MarketplaceJobTypes.ProductApprovalReconcile], false),
        ("invoices", "Faturalar", "invoices", [InvoicingJobTypes.InvoiceSubmit, InvoicingJobTypes.InvoiceReconcile, InvoicingJobTypes.InvoiceDocumentFetch, InvoicingJobTypes.MarketplaceDelivery, InvoicingJobTypes.InvoiceCancellation, InvoicingJobTypes.InvoiceDueScan], false),
        ("connections", "Bağlantılar", "connections", [MarketplaceJobTypes.ConnectionTest, InvoicingJobTypes.ConnectionTest, MarketplaceJobTypes.CapabilityProbe, InvoicingJobTypes.StageCapabilityProbe], false)
    ];

    public async Task<DashboardBootstrapView> BootstrapAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var snapshot = await db.DashboardSnapshots.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId, cancellationToken);
        if (snapshot is null)
        {
            await RebuildTenantAsync(tenantId, cancellationToken);
            snapshot = await db.DashboardSnapshots.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId, cancellationToken);
        }

        if (snapshot is null) return EmptyBootstrap();
        var lowStock = await db.DashboardLowStockProjections.AsNoTracking()
            .Where(x => x.TenantId == tenantId)
            .OrderBy(x => x.TotalStock)
            .ThenBy(x => x.Title)
            .Take(8)
            .Select(x => new DashboardLowStockView(x.ProductId, x.Title, x.TotalStock, x.PrimaryImageUrl))
            .ToListAsync(cancellationToken);
        var sync = await db.DashboardSyncStatusProjections.AsNoTracking()
            .Where(x => x.TenantId == tenantId)
            .OrderBy(x => x.ResourceType)
            .Select(x => new DashboardSyncStatusView(x.ResourceType, x.DisplayName, x.Kind, x.Status, x.LastAttemptAt, x.LastSuccessAt, x.LastErrorCode))
            .ToListAsync(cancellationToken);
        var platforms = await db.PlatformConnections.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.Status != "HIDDEN")
            .OrderBy(x => x.DisplayName)
            .Select(x => new DashboardPlatformView(x.DisplayName, x.Status))
            .ToListAsync(cancellationToken);
        var pendingByPlatform = JsonSerializer.Deserialize<Dictionary<string, int>>(snapshot.PendingByPlatformJson) ?? [];
        return new(
            new DashboardMetricsView(snapshot.PendingOrders, snapshot.LateOrders, snapshot.TodayOrders, snapshot.TodayProductQuantity, snapshot.MonthOrders, snapshot.MonthProductQuantity, snapshot.PendingReturns, snapshot.DueSoonInvoices, snapshot.UninvoicedInvoices, snapshot.LowStockProducts, snapshot.ActiveConnections, pendingByPlatform),
            lowStock,
            sync,
            platforms,
            snapshot.UpdatedAt,
            snapshot.Version);
    }

    public async Task<IReadOnlyList<DashboardRevenuePointView>> RevenueSeriesAsync(Guid tenantId, DateTimeOffset from, DateTimeOffset to, string? platform, CancellationToken cancellationToken)
    {
        var snapshotExists = await db.DashboardSnapshots.AsNoTracking().AnyAsync(x => x.TenantId == tenantId, cancellationToken);
        if (!snapshotExists) await RebuildTenantAsync(tenantId, cancellationToken);

        var timezoneId = await db.Tenants.AsNoTracking().Where(x => x.Id == tenantId).Select(x => x.Timezone).SingleOrDefaultAsync(cancellationToken);
        var timezone = ResolveTimezone(timezoneId);
        var startDay = TimeZoneInfo.ConvertTime(from, timezone).Date;
        var endDay = TimeZoneInfo.ConvertTime(to, timezone).Date;
        if (endDay < startDay) (startDay, endDay) = (endDay, startDay);
        var rows = await db.DashboardRevenueDaily.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.Day >= startDay && x.Day <= endDay && (string.IsNullOrWhiteSpace(platform) || platform == "ALL" || x.PlatformName == platform))
            .ToListAsync(cancellationToken);
        var byDay = rows.GroupBy(x => x.Day.Date).ToDictionary(
            x => x.Key,
            x => new DashboardRevenuePointView(x.Key, x.Sum(row => row.Amount), x.Sum(row => row.OrderCount), x.Select(row => row.Currency).FirstOrDefault() ?? "TRY"));
        var result = new List<DashboardRevenuePointView>((endDay - startDay).Days + 1);
        for (var day = startDay; day <= endDay; day = day.AddDays(1))
            result.Add(byDay.GetValueOrDefault(day) ?? new DashboardRevenuePointView(day, 0, 0, "TRY"));
        return result;
    }

    public async Task RebuildTenantAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var tenant = await db.Tenants.AsNoTracking().SingleOrDefaultAsync(x => x.Id == tenantId, cancellationToken);
        if (tenant is null) return;

        var now = timeProvider.GetUtcNow();
        var timezone = ResolveTimezone(tenant.Timezone);
        var localNow = TimeZoneInfo.ConvertTime(now, timezone);
        var todayStart = UtcOffset(DateTime.SpecifyKind(localNow.Date, DateTimeKind.Unspecified), timezone);
        var monthStart = UtcOffset(new DateTime(localNow.Year, localNow.Month, 1), timezone);
        var pendingOrdersQuery = db.Orders.AsNoTracking().Where(x => x.TenantId == tenantId
            && db.PlatformConnections.Any(connection => connection.TenantId == tenantId && connection.Id == x.ConnectionId && DashboardMetricPolicy.OperationalConnectionStatuses.Contains(connection.Status))
            && DashboardMetricPolicy.PendingOrderStatuses.Contains(x.DerivedStatus));
        var revenueOrders = db.Orders.AsNoTracking().Where(x => x.TenantId == tenantId
            && db.PlatformConnections.Any(connection => connection.TenantId == tenantId && connection.Id == x.ConnectionId && DashboardMetricPolicy.OperationalConnectionStatuses.Contains(connection.Status))
            && !new[] { "CANCELLED", "CANCELED", "RETURNED" }.Contains(x.DerivedStatus) && (x.NetAmount > 0 || x.GrossAmount > 0));

        var pendingOrders = await pendingOrdersQuery.CountAsync(cancellationToken);
        var lateOrders = await pendingOrdersQuery.CountAsync(x => DashboardMetricPolicy.LateOrderStatuses.Contains(x.DerivedStatus)
            && x.ShipmentDueAt != null && x.ShipmentDueAt < now, cancellationToken);
        var todayOrdersQuery = revenueOrders.Where(x => x.OrderedAt >= todayStart && x.OrderedAt < todayStart.AddDays(1));
        var monthOrdersQuery = revenueOrders.Where(x => x.OrderedAt >= monthStart && x.OrderedAt < monthStart.AddMonths(1));
        var todayOrders = await todayOrdersQuery.CountAsync(cancellationToken);
        var monthOrders = await monthOrdersQuery.CountAsync(cancellationToken);
        var todayProductQuantity = await (from line in db.OrderLines.AsNoTracking()
                                          join order in todayOrdersQuery on line.OrderId equals order.Id
                                          select (decimal?)line.OrderedQuantity).SumAsync(cancellationToken) ?? 0m;
        var monthProductQuantity = await (from line in db.OrderLines.AsNoTracking()
                                          join order in monthOrdersQuery on line.OrderId equals order.Id
                                          select (decimal?)line.OrderedQuantity).SumAsync(cancellationToken) ?? 0m;

        var pendingByConnection = await pendingOrdersQuery
            .GroupBy(x => x.ConnectionId)
            .Select(x => new { ConnectionId = x.Key, Count = x.Count() })
            .ToListAsync(cancellationToken);
        var connectionNames = await db.PlatformConnections.AsNoTracking().Where(x => x.TenantId == tenantId && x.Status != "HIDDEN").ToDictionaryAsync(x => x.Id, x => x.DisplayName, cancellationToken);
        var pendingByPlatform = pendingByConnection
            .GroupBy(x => connectionNames.GetValueOrDefault(x.ConnectionId, "Belirtilmemiş"))
            .ToDictionary(x => x.Key, x => x.Sum(row => row.Count));

        var pendingReturns = await db.ReturnClaims.AsNoTracking()
            .CountAsync(x => x.TenantId == tenantId
                && db.PlatformConnections.Any(connection => connection.TenantId == tenantId && connection.Id == x.ConnectionId && DashboardMetricPolicy.OperationalConnectionStatuses.Contains(connection.Status))
                && DashboardMetricPolicy.PendingReturnStatuses.Contains(x.Status), cancellationToken);
        var hasOperationalTrendyol = await db.PlatformConnections.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.PlatformCode == "TRENDYOL" && DashboardMetricPolicy.OperationalConnectionStatuses.Contains(x.Status), cancellationToken);
        var uninvoicedInvoices = 0;
        var dueSoonInvoices = 0;
        if (hasOperationalTrendyol)
        {
            var activePackages = db.ShipmentPackages.AsNoTracking().Where(x => x.TenantId == tenantId
                && db.PlatformConnections.Any(connection => connection.TenantId == tenantId && connection.Id == x.ConnectionId && DashboardMetricPolicy.OperationalConnectionStatuses.Contains(connection.Status))
                && x.Status != ShipmentPackageStatus.Cancelled && x.Status != ShipmentPackageStatus.Returned);
            var invoiceEligiblePackages = activePackages.Where(package => package.Status == ShipmentPackageStatus.Delivered);
            uninvoicedInvoices = await invoiceEligiblePackages.CountAsync(package => !db.Invoices.AsNoTracking().Any(invoice => invoice.TenantId == tenantId && invoice.OriginalInvoiceId == null && (invoice.PackageId == package.Id || invoice.PackageId == null && invoice.OrderId == package.OrderId)), cancellationToken);
            var invoiceDueAt = now.AddDays(-DashboardMetricPolicy.InvoiceDueDays);
            var invoiceReminderAt = now.AddDays(-DashboardMetricPolicy.InvoiceReminderStartDays);
            dueSoonInvoices = await invoiceEligiblePackages.CountAsync(package => package.StatusOccurredAt > invoiceDueAt
                && package.StatusOccurredAt <= invoiceReminderAt
                && !db.Invoices.AsNoTracking().Any(invoice => invoice.TenantId == tenantId && invoice.OriginalInvoiceId == null && (invoice.PackageId == package.Id || invoice.PackageId == null && invoice.OrderId == package.OrderId)), cancellationToken);
        }

        var stockRows = await (from variant in db.ProductVariants.AsNoTracking()
                               join item in db.InventoryItems.AsNoTracking().Where(x => x.LocationCode == "MAIN") on variant.Id equals item.VariantId
                               where variant.TenantId == tenantId
                                   && (!db.MarketplaceProductLinks.Any(link => link.TenantId == tenantId && link.ProductId == variant.ProductId)
                                       || db.MarketplaceProductLinks.Any(link => link.TenantId == tenantId && link.ProductId == variant.ProductId
                                           && db.PlatformConnections.Any(connection => connection.TenantId == tenantId && connection.Id == link.ConnectionId && connection.Status != "HIDDEN")))
                               group item by variant.ProductId into grouped
                               select new { ProductId = grouped.Key, TotalStock = grouped.Sum(x => x.OnHand) }).ToListAsync(cancellationToken);
        var stockByProduct = stockRows.ToDictionary(x => x.ProductId, x => x.TotalStock);
        var products = await db.Products.AsNoTracking().Where(x => x.TenantId == tenantId && x.Status == ProductStatus.Active
            && (!db.MarketplaceProductLinks.Any(link => link.TenantId == tenantId && link.ProductId == x.Id)
                || db.MarketplaceProductLinks.Any(link => link.TenantId == tenantId && link.ProductId == x.Id
                    && db.PlatformConnections.Any(connection => connection.TenantId == tenantId && connection.Id == link.ConnectionId && connection.Status != "HIDDEN"))))
            .Select(x => new { x.Id, x.Title }).ToListAsync(cancellationToken);
        var lowProducts = products.Where(x => stockByProduct.GetValueOrDefault(x.Id) <= LowStockThreshold).ToList();
        var lowProductIds = lowProducts.Select(x => x.Id).ToArray();
        var imageRows = await (from media in db.ProductMedia.AsNoTracking()
                               join asset in db.FileAssets.AsNoTracking() on new { media.TenantId, media.FileAssetId } equals new { asset.TenantId, FileAssetId = asset.Id }
                               where media.TenantId == tenantId && lowProductIds.Contains(media.ProductId) && media.Status == "ACTIVE" && asset.Status == "ACTIVE"
                               orderby media.SortOrder
                               select new { media.ProductId, Url = asset.RelativePath }).ToListAsync(cancellationToken);
        var imageByProduct = imageRows.GroupBy(x => x.ProductId).ToDictionary(x => x.Key, x => x.First().Url);

        var revenueRows = await revenueOrders.Select(x => new { x.OrderedAt, x.ConnectionId, x.Currency, x.NetAmount, x.GrossAmount }).ToListAsync(cancellationToken);
        var revenue = revenueRows
            .Select(x => new
            {
                Day = TimeZoneInfo.ConvertTime(x.OrderedAt, timezone).Date,
                PlatformName = connectionNames.GetValueOrDefault(x.ConnectionId, "Belirtilmemiş"),
                x.Currency,
                Amount = x.NetAmount > 0 ? x.NetAmount : x.GrossAmount
            })
            .GroupBy(x => new { x.Day, x.PlatformName, x.Currency })
            .Select(x => new DashboardRevenueDaily { TenantId = tenantId, Day = x.Key.Day, PlatformName = x.Key.PlatformName, Currency = x.Key.Currency, Amount = x.Sum(row => row.Amount), OrderCount = x.Count(), UpdatedAt = now })
            .ToList();

        var snapshot = await db.DashboardSnapshots.SingleOrDefaultAsync(x => x.TenantId == tenantId, cancellationToken);
        if (snapshot is null)
        {
            snapshot = new DashboardSnapshot { TenantId = tenantId, Version = 1 };
            db.DashboardSnapshots.Add(snapshot);
        }
        else snapshot.Version++;
        snapshot.PendingOrders = pendingOrders;
        snapshot.LateOrders = lateOrders;
        snapshot.TodayOrders = todayOrders;
        snapshot.TodayProductQuantity = todayProductQuantity;
        snapshot.MonthOrders = monthOrders;
        snapshot.MonthProductQuantity = monthProductQuantity;
        snapshot.PendingReturns = pendingReturns;
        snapshot.DueSoonInvoices = dueSoonInvoices;
        snapshot.UninvoicedInvoices = uninvoicedInvoices;
        snapshot.LowStockProducts = lowProducts.Count;
        snapshot.ActiveConnections = await db.PlatformConnections.AsNoTracking().CountAsync(x => x.TenantId == tenantId && DashboardMetricPolicy.OperationalConnectionStatuses.Contains(x.Status), cancellationToken);
        snapshot.PendingByPlatformJson = JsonSerializer.Serialize(pendingByPlatform);
        snapshot.UpdatedAt = now;

        var oldRevenue = await db.DashboardRevenueDaily.Where(x => x.TenantId == tenantId).ToListAsync(cancellationToken);
        db.DashboardRevenueDaily.RemoveRange(oldRevenue);
        db.DashboardRevenueDaily.AddRange(revenue);
        var oldLowStock = await db.DashboardLowStockProjections.Where(x => x.TenantId == tenantId).ToListAsync(cancellationToken);
        db.DashboardLowStockProjections.RemoveRange(oldLowStock);
        db.DashboardLowStockProjections.AddRange(lowProducts.Select(x => new DashboardLowStockProjection { TenantId = tenantId, ProductId = x.Id, Title = x.Title, TotalStock = stockByProduct.GetValueOrDefault(x.Id), PrimaryImageUrl = imageByProduct.GetValueOrDefault(x.Id), UpdatedAt = now }));

        var oldSync = await db.DashboardSyncStatusProjections.Where(x => x.TenantId == tenantId).ToListAsync(cancellationToken);
        db.DashboardSyncStatusProjections.RemoveRange(oldSync);
        foreach (var definition in SyncDefinitions)
        {
            var recent = await db.IntegrationJobs.AsNoTracking()
                .Where(x => x.TenantId == tenantId && definition.JobTypes.Contains(x.JobType))
                .OrderByDescending(x => x.CreatedAt)
                .Take(20)
                .Select(x => new { x.Status, x.CreatedAt, x.StartedAt, x.CompletedAt, x.LastErrorCode })
                .ToListAsync(cancellationToken);
            var latestAttempt = recent.OrderByDescending(x => x.CompletedAt ?? x.StartedAt ?? x.CreatedAt).FirstOrDefault();
            var latestSuccess = recent.Where(x => x.Status == JobStatus.Succeeded && x.CompletedAt != null).OrderByDescending(x => x.CompletedAt).FirstOrDefault();
            if (definition.Required || latestAttempt is not null)
                db.DashboardSyncStatusProjections.Add(new DashboardSyncStatusProjection { TenantId = tenantId, ResourceType = definition.ResourceType, DisplayName = definition.Label, Kind = definition.Kind, Status = latestAttempt is null ? "UNKNOWN" : JobStatusText(latestAttempt.Status), LastAttemptAt = latestAttempt?.StartedAt ?? latestAttempt?.CreatedAt, LastSuccessAt = latestSuccess?.CompletedAt, LastErrorCode = latestAttempt?.LastErrorCode, UpdatedAt = now, Version = 1 });
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private static DashboardBootstrapView EmptyBootstrap() => new(
        new DashboardMetricsView(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, new Dictionary<string, int>()),
        [], [], [], DateTimeOffset.UtcNow, 0);

    private static string JobStatusText(JobStatus status) => status == JobStatus.RetryScheduled ? "RETRY_SCHEDULED" : status == JobStatus.ManualReview ? "MANUAL_REVIEW" : status.ToString().ToUpperInvariant();

    private static DateTimeOffset UtcOffset(DateTime localDate, TimeZoneInfo timezone) =>
        new(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(localDate, DateTimeKind.Unspecified), timezone), TimeSpan.Zero);

    private static TimeZoneInfo ResolveTimezone(string? timezone)
    {
        if (string.IsNullOrWhiteSpace(timezone)) return TimeZoneInfo.Utc;
        try { return TimeZoneInfo.FindSystemTimeZoneById(timezone); }
        catch (TimeZoneNotFoundException)
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(timezone == "Europe/Istanbul" ? "Turkey Standard Time" : "Europe/Istanbul"); }
            catch (TimeZoneNotFoundException) { return TimeZoneInfo.Utc; }
        }
        catch (InvalidTimeZoneException) { return TimeZoneInfo.Utc; }
    }
}
