namespace MarketplaceHub.Application;

public sealed record DashboardMetricsView(
    int PendingOrders,
    int LateOrders,
    int TodayOrders,
    decimal TodayProductQuantity,
    int MonthOrders,
    decimal MonthProductQuantity,
    int PendingReturns,
    int DueSoonInvoices,
    int UninvoicedInvoices,
    int LowStockProducts,
    int ActiveConnections,
    IReadOnlyDictionary<string, int> PendingByPlatform);

public sealed record DashboardLowStockView(Guid Id, string Title, decimal TotalStock, string? PrimaryImageUrl);
public sealed record DashboardPlatformView(string Name, string Status);
public sealed record DashboardSyncStatusView(string ResourceType, string Label, string Kind, string Status, DateTimeOffset? LastAttemptAt, DateTimeOffset? LastSuccessAt, string? LastErrorCode);
public sealed record DashboardBootstrapView(
    DashboardMetricsView Metrics,
    IReadOnlyList<DashboardLowStockView> LowStock,
    IReadOnlyList<DashboardSyncStatusView> Sync,
    IReadOnlyList<DashboardPlatformView> Platforms,
    DateTimeOffset GeneratedAt,
    long Version);

public sealed record DashboardRevenuePointView(DateTime Day, decimal Amount, int OrderCount, string Currency);

public interface IDashboardReadService
{
    Task<DashboardBootstrapView> BootstrapAsync(Guid tenantId, CancellationToken cancellationToken);
    Task<IReadOnlyList<DashboardRevenuePointView>> RevenueSeriesAsync(Guid tenantId, DateTimeOffset from, DateTimeOffset to, string? platform, CancellationToken cancellationToken);
    Task RebuildTenantAsync(Guid tenantId, CancellationToken cancellationToken);
}
