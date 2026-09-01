namespace MarketplaceHub.Domain;

public static class DashboardMetricPolicy
{
    // Dashboard metrics must use the same connection lifecycle as the rest of
    // the active panel. Hidden and deleted connection history is not live data.
    public static readonly string[] OperationalConnectionStatuses = ["ACTIVE", "VERIFIED", "CONNECTED"];

    // A shipment that has already left the warehouse is no longer a late
    // fulfillment order. Its delivery tracking belongs to the shipment view.
    public static readonly string[] LateOrderStatuses =
        ["NEW", "PROCESSING", "ON_HOLD", "READY_TO_SHIP", "PARTIALLY_CANCELLED", "MANUAL_REVIEW"];

    // Approved, rejected, disputed and terminal claims are separate queues.
    // This metric is the actionable return flow shown as pending in the panel.
    public static readonly ReturnClaimStatus[] PendingReturnStatuses =
        [ReturnClaimStatus.Requested, ReturnClaimStatus.AwaitingShipment, ReturnClaimStatus.InTransit, ReturnClaimStatus.ActionRequired];

    public static bool IsLateOrderStatus(string? status) =>
        status is "NEW" or "PROCESSING" or "ON_HOLD" or "READY_TO_SHIP" or "PARTIALLY_CANCELLED" or "MANUAL_REVIEW";

    public static bool IsPendingReturn(ReturnClaimStatus status) =>
        status is ReturnClaimStatus.Requested or ReturnClaimStatus.AwaitingShipment or ReturnClaimStatus.InTransit or ReturnClaimStatus.ActionRequired;
}

/// <summary>
/// Small, tenant-scoped read models used by the operations dashboard. They are
/// deliberately separate from the transactional tables so a dashboard request
/// never has to scan orders, products, jobs and invoices together.
/// </summary>
public sealed class DashboardSnapshot
{
    public Guid TenantId { get; set; }
    public int PendingOrders { get; set; }
    public int LateOrders { get; set; }
    public int TodayOrders { get; set; }
    public decimal TodayProductQuantity { get; set; }
    public int MonthOrders { get; set; }
    public decimal MonthProductQuantity { get; set; }
    public int PendingReturns { get; set; }
    public int DueSoonInvoices { get; set; }
    public int UninvoicedInvoices { get; set; }
    public int LowStockProducts { get; set; }
    public int ActiveConnections { get; set; }
    public string PendingByPlatformJson { get; set; } = "{}";
    public DateTimeOffset UpdatedAt { get; set; }
    public long Version { get; set; } = 1;
}

public sealed class DashboardRevenueDaily
{
    public Guid TenantId { get; set; }
    public DateTime Day { get; set; }
    public required string PlatformName { get; set; }
    public required string Currency { get; set; }
    public decimal Amount { get; set; }
    public int OrderCount { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class DashboardLowStockProjection
{
    public Guid TenantId { get; set; }
    public Guid ProductId { get; set; }
    public required string Title { get; set; }
    public decimal TotalStock { get; set; }
    public string? PrimaryImageUrl { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class DashboardSyncStatusProjection
{
    public Guid TenantId { get; set; }
    public required string ResourceType { get; set; }
    public required string DisplayName { get; set; }
    public required string Kind { get; set; }
    public string Status { get; set; } = "UNKNOWN";
    public DateTimeOffset? LastAttemptAt { get; set; }
    public DateTimeOffset? LastSuccessAt { get; set; }
    public string? LastErrorCode { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Version { get; set; } = 1;
}

public sealed class IntegrationOutboxEvent
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public required string ResourceType { get; set; }
    public required string OperationType { get; set; }
    public required string AggregateType { get; set; }
    public Guid? AggregateId { get; set; }
    public long? AggregateVersion { get; set; }
    public required string PayloadJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public int DispatchAttempts { get; set; }
    public string? LastDispatchError { get; set; }
}

public readonly record struct IntegrationJobMetadata(string ResourceType, string OperationType, string TriggerType);

/// <summary>
/// The mapping is intentionally explicit. A new job type must choose a
/// resource before it can influence a realtime invalidation or a dashboard
/// projection, instead of relying on substring matching.
/// </summary>
public static class IntegrationJobMetadataPolicy
{
    public static IntegrationJobMetadata FromJobType(string? jobType) => jobType switch
    {
        "TRENDYOL_ORDER_SYNC" or "TRENDYOL_ORDER_RECOVERY_SYNC" or "TRENDYOL_ORDER_STATUS_SYNC" or "TRENDYOL_ORDER_RECONCILIATION" or "TRENDYOL_WEBHOOK_INGEST" => new("orders", "sync", "scheduled"),
        "TRENDYOL_SHIPMENT_ACTION" or "TRENDYOL_COMMON_LABEL" => new("orders", "shipment_action", "manual"),
        "TRENDYOL_RETURN_SYNC" or "TRENDYOL_RETURN_STATUS_SYNC" or "TRENDYOL_RETURN_RECONCILIATION" or "TRENDYOL_RETURN_ACTION" => new("returns", "sync", "scheduled"),
        "TRENDYOL_PRODUCT_SYNC" or "TRENDYOL_PRODUCT_CREATE" or "TRENDYOL_PRODUCT_APPROVAL_RECONCILE" or "TRENDYOL_PRODUCT_UPDATE" or "TRENDYOL_PRODUCT_ARCHIVE" => new("products", "sync", "scheduled"),
        "TRENDYOL_PRICE_INVENTORY_SYNC" or "STOCK_PROJECTION_DISPATCH" or "TRENDYOL_STOCK_RECONCILIATION" => new("inventory", "sync", "scheduled"),
        "INVOICE_SUBMIT" or "INVOICE_RECONCILE" or "INVOICE_DOCUMENT_FETCH" or "INVOICE_MARKETPLACE_DELIVERY" or "INVOICE_CANCELLATION" or "INVOICE_DUE_SCAN" => new("invoices", "sync", "scheduled"),
        "TRENDYOL_CONNECTION_TEST" or "EFATURAM_CONNECTION_TEST" or "TRENDYOL_CAPABILITY_PROBE" or "EFATURAM_STAGE_CAPABILITY_PROBE" or "TRENDYOL_REFERENCE_SYNC" => new("connections", "sync", "scheduled"),
        "IMPORT_PREVIEW" or "IMPORT_APPLY" => new("products", "import", "manual"),
        _ => new("jobs", "execute", "system")
    };

    public static IntegrationJobMetadata Apply(IntegrationJob job)
    {
        var metadata = FromJobType(job.JobType);
        if (!string.IsNullOrWhiteSpace(job.ResourceType) && job.ResourceType != "jobs")
            return new(job.ResourceType, job.OperationType, job.TriggerType);
        return metadata;
    }
}
