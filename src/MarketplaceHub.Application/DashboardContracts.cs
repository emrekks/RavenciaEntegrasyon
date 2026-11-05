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
    int RecentJobCount = 0,
    // The products of the pending orders (ordered less cancelled), per store; null while unknown.
    IReadOnlyDictionary<string, decimal>? PendingUnitsByConnection = null,
    // The pending orders by the cargo company of their current package (empty name: none known yet), with their products.
    IReadOnlyList<DashboardPendingCargoView>? PendingByCargo = null);

public sealed record DashboardPendingCargoView(string Cargo, int Orders, decimal Units);

public sealed record DashboardChannelView(Guid ConnectionId, string PlatformCode, string DisplayName, string Status);
public sealed record DashboardIntegrationCellView(string State, DateTimeOffset? LastSuccessAt, DateTimeOffset? LastProblemAt);
public sealed record DashboardIntegrationRowView(Guid ConnectionId, string PlatformCode, string DisplayName, string ExternalStoreId, string Status, IReadOnlyDictionary<string, DashboardIntegrationCellView> Cells, string? Issue);
public sealed record DashboardBootstrapView(
    DashboardMetricsView Metrics,
    IReadOnlyList<DashboardChannelView> Channels,
    DateTimeOffset GeneratedAt,
    long Version);

// One day of one connection in one currency. Revenue is what the customer paid (net, gross only when no net is
// known); cancelled orders are left out. Amount leaves the returned money out (ReturnedAmount holds it); OrderCount counts
// every order with an amount, returned ones included, so (Amount + ReturnedAmount) ÷ OrderCount is one basis.
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
    IReadOnlyList<DashboardPerformanceRowView> Previous,
    // A one-day period also comes hour by hour (the previous day up to the same time), so the panel can draw the day.
    IReadOnlyList<DashboardPerformanceHourView>? CurrentHours = null,
    IReadOnlyList<DashboardPerformanceHourView>? PreviousHours = null);

// One hour (tenant's local time, "yyyy-MM-ddTHH:00") of one connection in one currency; same rules as the day rows.
public sealed record DashboardPerformanceHourView(
    string Hour,
    Guid ConnectionId,
    string Currency,
    decimal Amount,
    int OrderCount,
    decimal ProductQuantity,
    int PackageCount,
    decimal ReturnedAmount,
    int ReturnedOrderCount);

public interface IDashboardReadService
{
    Task<DashboardBootstrapView> BootstrapAsync(Guid tenantId, CancellationToken cancellationToken);
    Task<ServiceResult<DashboardPerformanceView>> PerformanceAsync(Guid tenantId, DateOnly? from, DateOnly? to, CancellationToken cancellationToken);
    Task<ServiceResult<DashboardCancellationsView>> CancellationsAsync(Guid tenantId, DateOnly? day, CancellationToken cancellationToken);
    Task RebuildTenantAsync(Guid tenantId, CancellationToken cancellationToken);
}

// Orders cancelled on one tenant calendar day, per store. A line counts on the day the marketplace says it was
// cancelled; a line whose time the marketplace does not give counts apart (UnknownTime…) on the day the panel saw the
// cancellation. CancelledOrders are orders cancelled whole, PartialOrders orders that stay open with fewer products;
// the units are the cancelled quantities of the lines that count (CancelledUnits = WholeUnits + PartialUnits: those of
// the orders cancelled whole, and those cancelled of the orders that stay open).
public sealed record DashboardCancellationRowView(
    Guid ConnectionId,
    int CancelledOrders,
    int PartialOrders,
    decimal CancelledUnits,
    int UnknownTimeOrders,
    decimal UnknownTimeUnits,
    decimal WholeUnits = 0,
    decimal PartialUnits = 0);

public sealed record DashboardCancellationsView(DateOnly Day, DateTimeOffset Start, DateTimeOffset End, IReadOnlyList<DashboardCancellationRowView> Stores);
