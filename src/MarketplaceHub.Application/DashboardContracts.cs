namespace MarketplaceHub.Application;

// The operational cards of the overview page and the side menu. Every card total and its hover breakdown come
// from the same query; breakdowns are keyed by connection id (see Channels for names and platforms).
public sealed record DashboardMetricsView(
    int PendingOrders,
    int LateOrders,
    int PendingReturns,
    IReadOnlyDictionary<string, int> PendingByConnection,
    IReadOnlyDictionary<string, int> LateByConnection,
    IReadOnlyDictionary<string, int> PendingReturnsByConnection,
    int DeadJobCount = 0,
    int ManualReviewJobCount = 0,
    int RecentJobCount = 0);

public sealed record DashboardChannelView(Guid ConnectionId, string PlatformCode, string DisplayName, string Status);
public sealed record DashboardIntegrationCellView(string State, DateTimeOffset? LastSuccessAt, DateTimeOffset? LastProblemAt);
public sealed record DashboardIntegrationRowView(Guid ConnectionId, string PlatformCode, string DisplayName, string ExternalStoreId, string Status, IReadOnlyDictionary<string, DashboardIntegrationCellView> Cells, string? Issue);
public sealed record DashboardBootstrapView(
    DashboardMetricsView Metrics,
    IReadOnlyList<DashboardChannelView> Channels,
    DateTimeOffset GeneratedAt,
    long Version);

// One day of one connection in one currency. Revenue is what the customer paid (net, gross only when no net is
// known); cancelled and returned orders are left out of Amount/OrderCount, returned ones are shown separately.
public sealed record DashboardPerformanceRowView(
    DateOnly Day,
    Guid ConnectionId,
    string Currency,
    decimal Amount,
    int OrderCount,
    decimal ProductQuantity,
    int PackageCount,
    decimal ReturnedAmount,
    int ReturnedOrderCount);

// The performance panel: the chosen period and the previous one of equal elapsed time. When the period ends today
// it runs until now and the previous period stops at the same time of day (PartialDay).
public sealed record DashboardPerformanceView(
    DateOnly From,
    DateOnly To,
    DateOnly PreviousFrom,
    DateOnly PreviousTo,
    DateTimeOffset CurrentStart,
    DateTimeOffset CurrentEnd,
    DateTimeOffset PreviousStart,
    DateTimeOffset PreviousEnd,
    bool PartialDay,
    IReadOnlyList<DashboardChannelView> Channels,
    IReadOnlyList<DashboardPerformanceRowView> Current,
    IReadOnlyList<DashboardPerformanceRowView> Previous);

public interface IDashboardReadService
{
    Task<DashboardBootstrapView> BootstrapAsync(Guid tenantId, CancellationToken cancellationToken);
    Task<ServiceResult<DashboardPerformanceView>> PerformanceAsync(Guid tenantId, DateOnly? from, DateOnly? to, CancellationToken cancellationToken);
    Task RebuildTenantAsync(Guid tenantId, CancellationToken cancellationToken);
}
