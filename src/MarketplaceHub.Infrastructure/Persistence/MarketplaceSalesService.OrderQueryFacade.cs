using MarketplaceHub.Application;
using MarketplaceHub.Domain;

namespace MarketplaceHub.Infrastructure.Persistence;

// SIPARIS-STOK-10: the order query side lives in OrderQueryService; the facade keeps the interface and the members the
// other partials and the tests reach, forwarding them unchanged.
public sealed partial class MarketplaceSalesService
{
    private OrderQueryService? orderQueries;
    internal OrderQueryService OrderQueries => orderQueries ??= new(Sales);

    public Task<PageResult<OrderListView>> OrdersAsync(Guid tenantId, int limit, int page, string? after, OrderListQuery queryOptions, CancellationToken cancellationToken) =>
        OrderQueries.OrdersAsync(tenantId, limit, page, after, queryOptions, cancellationToken);
    public Task<OrderSummaryView> OrderSummaryAsync(Guid tenantId, string? platform, CancellationToken cancellationToken) =>
        OrderQueries.OrderSummaryAsync(tenantId, platform, cancellationToken);
    public Task<OrderSummaryView> OrderSummaryAsync(Guid tenantId, OrderListQuery options, CancellationToken cancellationToken, IReadOnlyCollection<string>? tabs = null, bool allowCached = false) =>
        OrderQueries.OrderSummaryAsync(tenantId, options, cancellationToken, tabs, allowCached);
    public Task<OrderFacetsView> OrderFacetsAsync(Guid tenantId, OrderListQuery options, CancellationToken cancellationToken) =>
        OrderQueries.OrderFacetsAsync(tenantId, options, cancellationToken);
    public Task<ServiceResult<OrderDetailView>> OrderAsync(Guid tenantId, Guid id, CancellationToken cancellationToken) =>
        OrderQueries.OrderAsync(tenantId, id, cancellationToken);
    public Task<ServiceResult<string>> ProductImageAsync(Guid tenantId, string? barcode, string correlationId, CancellationToken cancellationToken, Guid? connectionId = null, string? productName = null) =>
        OrderQueries.ProductImageAsync(tenantId, barcode, correlationId, cancellationToken, connectionId, productName);

    internal IQueryable<Order> FilteredOrders(Guid tenantId, OrderListQuery options) => OrderQueries.FilteredOrders(tenantId, options);
    internal void ApplyOrderFilters(ref IQueryable<Order> query, OrderListQuery options, Guid tenantId) => OrderQueries.ApplyOrderFilters(ref query, options, tenantId);
    internal IQueryable<Order> CurrentManualShopifyOrderStatuses(Guid tenantId) => OrderQueries.CurrentManualShopifyOrderStatuses(tenantId);
    internal IQueryable<Order> HepsiburadaUnpackagedOnHoldOrders(Guid tenantId) => OrderQueries.HepsiburadaUnpackagedOnHoldOrders(tenantId);
    internal IQueryable<Order> HepsiburadaUnpackagedDeliveredOrders(Guid tenantId) => OrderQueries.HepsiburadaUnpackagedDeliveredOrders(tenantId);
    internal IQueryable<Order> ExcludeStaleUnpackagedHepsiburadaOrders(IQueryable<Order> query, Guid tenantId) => OrderQueries.ExcludeStaleUnpackagedHepsiburadaOrders(query, tenantId);

    internal static readonly ShipmentPackageStatus[] ResentNotShippedStatuses = OrderQueryService.ResentNotShippedStatuses;
    internal static ShipmentPackageStatus[]? PackageStatusesForOrderTab(string status) => OrderQueryService.PackageStatusesForOrderTab(status);
    internal static string[]? DerivedStatusesForOrderTab(string status) => OrderQueryService.DerivedStatusesForOrderTab(status);
    internal static string[] ManualShopifyStatusesForOrderTab(string status) => OrderQueryService.ManualShopifyStatusesForOrderTab(status);
    internal static bool SearchesCustomerSnapshot(string term) => OrderQueryService.SearchesCustomerSnapshot(term);
    internal static (string AsTyped, string Folded) SearchPatterns(string term) => OrderQueryService.SearchPatterns(term);
    internal static string CustomerSnapshotSearchPattern(string term) => OrderQueryService.CustomerSnapshotSearchPattern(term);
    internal static readonly string[] OrderSummaryTabs = OrderQueryService.OrderSummaryTabs;
    internal static readonly TimeSpan OrderSummaryCacheLifetime = OrderQueryService.OrderSummaryCacheLifetime;
    internal static IReadOnlyDictionary<string, int> MicroExportCountryCounts(IEnumerable<(string InvoiceAddressJson, string? StoreFrontCode)> orders) => OrderQueryService.MicroExportCountryCounts(orders);
    internal static bool AmountMayBeIncomplete(string? platformCode, int unreadableLines) => OrderQueryService.AmountMayBeIncomplete(platformCode, unreadableLines);
    internal static int UnreadableLineCount(IEnumerable<ShipmentPackage> packages) => OrderQueryService.UnreadableLineCount(packages);
    internal static bool HasLiveImageLookup(string? platformCode) => OrderQueryService.HasLiveImageLookup(platformCode);
    private const string LikeEscape = OrderQueryService.LikeEscape;
}
