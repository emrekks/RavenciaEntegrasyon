using MarketplaceHub.Domain;

namespace MarketplaceHub.Application;

public static class ActiveIntegrationScope
{
    public static bool Contains(string? platformCode) => platformCode is "TRENDYOL" or "TRENDYOL_EFATURAM" or "SHOPIFY" or "HEPSIBURADA";

    public static bool IsMarketplace(string? platformCode) => platformCode is "TRENDYOL" or "SHOPIFY" or "HEPSIBURADA";
}

public static class MarketplaceJobTypes
{
    public const string ActivationBootstrapPrefix = "activation-bootstrap:";
    public const string ConnectionTest = "TRENDYOL_CONNECTION_TEST";
    public const string ReferenceSync = "TRENDYOL_REFERENCE_SYNC";
    public const string HepsiburadaReferenceSync = "HEPSIBURADA_REFERENCE_SYNC";
    public const string OrderSync = "TRENDYOL_ORDER_SYNC";
    public const string OrderRecoverySync = "TRENDYOL_ORDER_RECOVERY_SYNC";
    public const string OrderStatusSync = "TRENDYOL_ORDER_STATUS_SYNC";
    public const string TrendyolOrderCargoInfoReconciliation = "TRENDYOL_ORDER_CARGO_INFO_RECONCILIATION";
    public const string OrderReconciliation = "TRENDYOL_ORDER_RECONCILIATION";
    public const string OrderInvoiceReconciliation = "TRENDYOL_ORDER_INVOICE_RECONCILIATION";
    public const string ProductSync = "TRENDYOL_PRODUCT_SYNC";
    public const string ShipmentAction = "TRENDYOL_SHIPMENT_ACTION";
    public const string ReturnSync = "TRENDYOL_RETURN_SYNC";
    public const string ReturnStatusSync = "TRENDYOL_RETURN_STATUS_SYNC";
    public const string ReturnReconciliation = "TRENDYOL_RETURN_RECONCILIATION";
    public const string ReturnAction = "TRENDYOL_RETURN_ACTION";
    public const string WebhookIngest = "TRENDYOL_WEBHOOK_INGEST";
    public const string ProductCreate = "TRENDYOL_PRODUCT_CREATE";
    public const string ProductApprovalReconcile = "TRENDYOL_PRODUCT_APPROVAL_RECONCILE";
    public const string ProductUpdate = "TRENDYOL_PRODUCT_UPDATE";
    public const string ProductArchive = "TRENDYOL_PRODUCT_ARCHIVE";
    public const string PriceInventorySync = "TRENDYOL_PRICE_INVENTORY_SYNC";
    public const string StockProjectionDispatch = "STOCK_PROJECTION_DISPATCH";
    public const string StockReconciliation = "TRENDYOL_STOCK_RECONCILIATION";
    public const string CommonLabel = "TRENDYOL_COMMON_LABEL";
    public const string CapabilityProbe = "TRENDYOL_CAPABILITY_PROBE";
    public const string StageTestOrder = "TRENDYOL_STAGE_TEST_ORDER";
    public const string ShopifyConnectionTest = "SHOPIFY_CONNECTION_TEST";
    public const string ShopifyProductSync = "SHOPIFY_PRODUCT_SYNC";
    public const string ShopifyOrderSync = "SHOPIFY_ORDER_SYNC";
    public const string ShopifyOrderRecoverySync = "SHOPIFY_ORDER_RECOVERY_SYNC";
    public const string ShopifyOrderStatusSync = "SHOPIFY_ORDER_STATUS_SYNC";
    public const string ShopifyOrderReconciliation = "SHOPIFY_ORDER_RECONCILIATION";
    public const string ShopifyOrderInvoiceReconciliation = "SHOPIFY_ORDER_INVOICE_RECONCILIATION";
    public const string ShopifyWebhookIngest = "SHOPIFY_WEBHOOK_INGEST";
    public const string HepsiburadaConnectionTest = "HEPSIBURADA_CONNECTION_TEST";
    public const string HepsiburadaProductSync = "HEPSIBURADA_PRODUCT_SYNC";
    public const string HepsiburadaOrderSync = "HEPSIBURADA_ORDER_SYNC";
    public const string HepsiburadaOrderRecoverySync = "HEPSIBURADA_ORDER_RECOVERY_SYNC";
    public const string HepsiburadaOrderStatusSync = "HEPSIBURADA_ORDER_STATUS_SYNC";
    public const string HepsiburadaWebhookIngest = "HEPSIBURADA_WEBHOOK_INGEST";
    public const string HepsiburadaReturnSync = "HEPSIBURADA_RETURN_SYNC";
    public const string HepsiburadaOrderInvoiceReconciliation = "HEPSIBURADA_ORDER_INVOICE_RECONCILIATION";
    public const string QuestionSync = "MARKETPLACE_QUESTION_SYNC";

    public static bool IsMarketplaceProcessorJob(string? jobType) => jobType is
        ConnectionTest or ShopifyConnectionTest or HepsiburadaConnectionTest
        or ReferenceSync or HepsiburadaReferenceSync
        or ProductSync or ShopifyProductSync or HepsiburadaProductSync
        or ProductCreate or ProductApprovalReconcile or ProductUpdate or ProductArchive
        or PriceInventorySync or StockProjectionDispatch
        or OrderSync or ShopifyOrderSync or HepsiburadaOrderSync
        or OrderRecoverySync or ShopifyOrderRecoverySync or HepsiburadaOrderRecoverySync
        or OrderStatusSync or ShopifyOrderStatusSync or HepsiburadaOrderStatusSync or TrendyolOrderCargoInfoReconciliation
        or OrderReconciliation or ShopifyOrderReconciliation
        or OrderInvoiceReconciliation or ShopifyOrderInvoiceReconciliation or HepsiburadaOrderInvoiceReconciliation
        or ShipmentAction or CommonLabel or CapabilityProbe or StageTestOrder
        or ReturnSync or HepsiburadaReturnSync or ReturnStatusSync or ReturnReconciliation or ReturnAction
        or StockReconciliation
        or WebhookIngest or ShopifyWebhookIngest or HepsiburadaWebhookIngest
        or QuestionSync;

    public static string ForPlatform(string? platformCode, string jobType) => platformCode?.Trim().ToUpperInvariant() switch
    {
        "SHOPIFY" => jobType switch
        {
            ConnectionTest => ShopifyConnectionTest,
            ProductSync => ShopifyProductSync,
            OrderSync => ShopifyOrderSync,
            OrderRecoverySync => ShopifyOrderRecoverySync,
            OrderStatusSync => ShopifyOrderStatusSync,
            OrderReconciliation => ShopifyOrderReconciliation,
            OrderInvoiceReconciliation => ShopifyOrderInvoiceReconciliation,
            WebhookIngest => ShopifyWebhookIngest,
            _ => jobType
        },
        "HEPSIBURADA" => jobType switch
        {
            ConnectionTest => HepsiburadaConnectionTest,
            ReferenceSync => HepsiburadaReferenceSync,
            ProductSync => HepsiburadaProductSync,
            OrderSync => HepsiburadaOrderSync,
            OrderRecoverySync => HepsiburadaOrderRecoverySync,
            OrderStatusSync => HepsiburadaOrderStatusSync,
            WebhookIngest => HepsiburadaWebhookIngest,
            ReturnSync => HepsiburadaReturnSync,
            OrderInvoiceReconciliation => HepsiburadaOrderInvoiceReconciliation,
            _ => jobType
        },
        _ => jobType
    };
}

public enum FullOrderSyncConflictResolution
{
    ReuseExisting,
    PromotePending,
    Reject
}

public static class FullOrderSyncConflictPolicy
{
    public static FullOrderSyncConflictResolution Resolve(
        string? requestedJobType,
        string? conflictingJobType,
        JobStatus conflictingStatus,
        bool requestedFullScan,
        bool conflictingJobTargetsSingleOrder,
        bool conflictingJobHasStarted)
    {
        if (!requestedFullScan || !IsRecoverySyncType(requestedJobType))
            return FullOrderSyncConflictResolution.ReuseExisting;

        if (conflictingStatus == JobStatus.Pending
            && !conflictingJobTargetsSingleOrder
            && !conflictingJobHasStarted
            && IsPromotableOrderSyncType(requestedJobType, conflictingJobType))
            return FullOrderSyncConflictResolution.PromotePending;

        return FullOrderSyncConflictResolution.Reject;
    }

    private static bool IsRecoverySyncType(string? jobType) => jobType is
        MarketplaceJobTypes.OrderRecoverySync
        or MarketplaceJobTypes.ShopifyOrderRecoverySync
        or MarketplaceJobTypes.HepsiburadaOrderRecoverySync;

    private static bool IsPromotableOrderSyncType(string? requestedJobType, string? conflictingJobType) => requestedJobType switch
    {
        MarketplaceJobTypes.OrderRecoverySync => conflictingJobType is MarketplaceJobTypes.OrderSync or MarketplaceJobTypes.OrderRecoverySync or MarketplaceJobTypes.OrderStatusSync,
        MarketplaceJobTypes.ShopifyOrderRecoverySync => conflictingJobType is MarketplaceJobTypes.ShopifyOrderSync or MarketplaceJobTypes.ShopifyOrderRecoverySync or MarketplaceJobTypes.ShopifyOrderStatusSync,
        MarketplaceJobTypes.HepsiburadaOrderRecoverySync => conflictingJobType is MarketplaceJobTypes.HepsiburadaOrderSync or MarketplaceJobTypes.HepsiburadaOrderRecoverySync or MarketplaceJobTypes.HepsiburadaOrderStatusSync,
        _ => false
    };
}

public enum TargetedOrderSyncConflictResolution
{
    ReuseExisting,
    PromotePending,
    QueueBehindActiveWork,
    Reject
}

public static class TargetedOrderSyncConflictPolicy
{
    public static int? PreferredConflictIndex(
        IReadOnlyList<(string JobType, string? ExternalOrderId, JobStatus Status, bool HasStarted, string? PackageNumber)> conflicts,
        string? requestedJobType,
        string? requestedExternalOrderId,
        string? requestedPackageNumber = null)
    {
        if (conflicts.Count == 0) return null;

        var resolutions = conflicts.Select(conflict => Resolve(
            requestedJobType,
            requestedExternalOrderId,
            conflict.JobType,
            conflict.ExternalOrderId,
            conflict.Status,
            conflict.HasStarted,
            requestedPackageNumber,
            conflict.PackageNumber)).ToArray();

        for (var index = 0; index < resolutions.Length; index++)
            if (resolutions[index] == TargetedOrderSyncConflictResolution.ReuseExisting)
                return index;

        for (var index = 0; index < resolutions.Length; index++)
            if (resolutions[index] == TargetedOrderSyncConflictResolution.PromotePending)
                return index;

        for (var index = 0; index < resolutions.Length; index++)
            if (resolutions[index] == TargetedOrderSyncConflictResolution.QueueBehindActiveWork)
                return index;

        return 0;
    }

    public static TargetedOrderSyncConflictResolution Resolve(
        string? requestedJobType,
        string? requestedExternalOrderId,
        string? conflictingJobType,
        string? conflictingExternalOrderId,
        JobStatus conflictingStatus,
        bool conflictingJobHasStarted,
        string? requestedPackageNumber = null,
        string? conflictingPackageNumber = null)
    {
        if (string.IsNullOrWhiteSpace(requestedExternalOrderId))
            return TargetedOrderSyncConflictResolution.Reject;

        if (!string.IsNullOrWhiteSpace(conflictingExternalOrderId)
            && string.Equals(requestedExternalOrderId.Trim(), conflictingExternalOrderId.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            // An order-only read cannot satisfy an explicit package refresh.
            // Do not discard the requested package when reusing an active job.
            if (!string.IsNullOrWhiteSpace(requestedPackageNumber)
                && !string.Equals(requestedPackageNumber.Trim(), conflictingPackageNumber?.Trim(), StringComparison.Ordinal))
                return conflictingStatus == JobStatus.Pending && !conflictingJobHasStarted
                    && IsSameIncrementalOrderSyncType(requestedJobType, conflictingJobType)
                        ? TargetedOrderSyncConflictResolution.PromotePending
                        : TargetedOrderSyncConflictResolution.Reject;
            return TargetedOrderSyncConflictResolution.ReuseExisting;
        }

        if (conflictingStatus == JobStatus.Pending
            && !conflictingJobHasStarted
            && IsSameIncrementalOrderSyncType(requestedJobType, conflictingJobType)
            && string.IsNullOrWhiteSpace(conflictingExternalOrderId))
            return TargetedOrderSyncConflictResolution.PromotePending;

        if (string.IsNullOrWhiteSpace(conflictingExternalOrderId)
            && conflictingStatus is JobStatus.Leased or JobStatus.RetryScheduled
            && IsOrderSyncLaneJob(requestedJobType, conflictingJobType))
            return TargetedOrderSyncConflictResolution.QueueBehindActiveWork;

        return TargetedOrderSyncConflictResolution.Reject;
    }

    private static bool IsOrderSyncLaneJob(string? requestedJobType, string? conflictingJobType) => requestedJobType switch
    {
        MarketplaceJobTypes.OrderSync => conflictingJobType is MarketplaceJobTypes.OrderSync or MarketplaceJobTypes.OrderRecoverySync or MarketplaceJobTypes.OrderStatusSync or MarketplaceJobTypes.OrderReconciliation,
        MarketplaceJobTypes.ShopifyOrderSync => conflictingJobType is MarketplaceJobTypes.ShopifyOrderSync or MarketplaceJobTypes.ShopifyOrderRecoverySync or MarketplaceJobTypes.ShopifyOrderStatusSync or MarketplaceJobTypes.ShopifyOrderReconciliation,
        MarketplaceJobTypes.HepsiburadaOrderSync => conflictingJobType is MarketplaceJobTypes.HepsiburadaOrderSync or MarketplaceJobTypes.HepsiburadaOrderRecoverySync or MarketplaceJobTypes.HepsiburadaOrderStatusSync,
        _ => false
    };

    private static bool IsSameIncrementalOrderSyncType(string? requestedJobType, string? conflictingJobType) => requestedJobType switch
    {
        MarketplaceJobTypes.OrderSync => conflictingJobType == MarketplaceJobTypes.OrderSync,
        MarketplaceJobTypes.ShopifyOrderSync => conflictingJobType == MarketplaceJobTypes.ShopifyOrderSync,
        MarketplaceJobTypes.HepsiburadaOrderSync => conflictingJobType == MarketplaceJobTypes.HepsiburadaOrderSync,
        _ => false
    };
}

public static class MarketplaceSyncPolicyRules
{
    public static bool RequiresExternalWrites(string? resourceType) => resourceType?.Trim().ToUpperInvariant() is
        "STOCK_RECONCILE_SHORT" or "STOCK_RECONCILE_MEDIUM" or "STOCK_RECONCILE_DAILY"
        or MarketplaceExternalWritePolicies.Price
        or MarketplaceExternalWritePolicies.Stock
        or MarketplaceExternalWritePolicies.Shipment
        or MarketplaceExternalWritePolicies.Return;
}

public static class MarketplaceExternalWritePolicies
{
    public const string Price = "PRICE_WRITE";
    public const string Stock = "STOCK_WRITE";
    public const string Shipment = "SHIPMENT_WRITE";
    public const string Return = "RETURN_WRITE";

    public static bool IsPolicy(string? resourceType) => resourceType?.Trim().ToUpperInvariant() is
        Price or Stock or Shipment or Return;

    public static string? ForJobType(string? jobType) => jobType switch
    {
        MarketplaceJobTypes.PriceInventorySync => Price,
        MarketplaceJobTypes.StockProjectionDispatch or MarketplaceJobTypes.StockReconciliation => Stock,
        MarketplaceJobTypes.ShipmentAction => Shipment,
        MarketplaceJobTypes.ReturnAction => Return,
        _ => null
    };
}

public static class MarketplaceCapabilities
{
    public const string ConnectionTest = "CONNECTION_TEST";
    public const string ReferenceRead = "REFERENCE_READ";
    public const string ProductRead = "PRODUCT_READ";
    public const string ProductWrite = "PRODUCT_WRITE";
    public const string InventoryWrite = "INVENTORY_WRITE";
    public const string PriceWrite = "PRICE_WRITE";
    public const string OrderRead = "ORDER_READ";
    public const string OrderWebhook = "ORDER_WEBHOOK";
    public const string ShipmentWrite = "SHIPMENT_WRITE";
    public const string LabelRead = "LABEL_READ";
    public const string LabelWrite = "LABEL_WRITE";
    public const string ReturnRead = "RETURN_READ";
    public const string ReturnWrite = "RETURN_WRITE";
    public const string QuestionRead = "QUESTION_READ";
    public const string QuestionWrite = "QUESTION_WRITE";
}

public enum AdapterErrorClass
{
    TransientNetwork,
    RateLimit,
    Remote5xx,
    Authentication,
    Validation,
    BusinessConflict,
    NotFound,
    NotSupported,
    ContractViolation,
    InternalBug
}

public sealed record AdapterContext(Guid TenantId, Guid ConnectionId, string CorrelationId, string IdempotencyKey, DateTimeOffset DeadlineUtc, bool IsStageCapabilityProbe = false, IntegrationOperation Operation = IntegrationOperation.Manual);
public sealed record AdapterError(AdapterErrorClass Class, string Code, string SafeMessage, int? HttpStatus, TimeSpan? RetryAfter, string? RemoteRequestId);
public sealed record RateLimitMetadata(int? Remaining, DateTimeOffset? ResetAt, TimeSpan? RetryAfter, int? Limit = null);
public sealed record AdapterResult<T>(bool IsSuccess, T? Value, AdapterError? Error, RateLimitMetadata? RateLimit)
{
    public static AdapterResult<T> Success(T value, RateLimitMetadata? rateLimit = null) => new(true, value, null, rateLimit);
    public static AdapterResult<T> Failure(AdapterError error, RateLimitMetadata? rateLimit = null) => new(false, default, error, rateLimit);
}

public sealed record AdapterPageRequest(string? Cursor, int Limit);
public sealed record AdapterPageIssue(string Code, string Identity, string Message);
public sealed record AdapterPageResult<T>(IReadOnlyList<T> Items, string? NextCursor, bool HasMore, int? TotalCount = null, IReadOnlyList<AdapterPageIssue>? Issues = null);
public sealed record ConnectionIdentity(string PlatformCode, string Environment, string ExternalStoreId, string ApiVersion, string ScopeFingerprint);
public sealed record CapabilityEvidence(string Code, string SupportLevel, string ApiVersion, string Environment, string StoreScope, string SourceUrl, string SourceVersion, string? RequiredScope, string? ConstraintsJson, string EvidenceNote, string? FixtureChecksum, DateTimeOffset VerifiedAt);
public sealed record RemoteReferenceItem(string ResourceType, string ExternalId, string? ParentExternalId, string Name, string Path, int Depth, bool IsLeaf, bool IsActive, string RawJson, bool? IsRequired = null, bool? AllowsCustomValue = null, bool? AllowsMultipleValues = null);
public sealed record ReferenceResource(string ResourceType, string? ParentExternalId);
public sealed record RemoteOperationRef(string ExternalOperationId, string Kind, DateTimeOffset SubmittedAt);
public sealed record RemoteOperationLine(string ExternalKey, bool Succeeded, string? ExternalId, string? ErrorCode, bool Retryable, string? Status = null);
public sealed record RemoteOperationStatus(string ExternalOperationId, string Status, IReadOnlyList<RemoteOperationLine> Lines);
public sealed record ProductPublication(Guid ProductId, string PayloadHash, string PayloadJson);
public sealed record ProductPublicationJobPayload(Guid JobId, Guid ProductId, Guid ProfileId, string Phase, string PayloadHash, string PayloadJson, string? ExternalOperationId, DateTimeOffset? SubmittedAt);
public sealed record ProductApprovalReconciliationJobPayload(Guid JobId, Guid ProductId, Guid ProfileId, string PayloadHash, DateTimeOffset StartedAt, DateTimeOffset DeadlineAt, string? ExternalOperationId = null);
public sealed record ProductUpdatePublication(Guid ProductId, string Mode, string PayloadHash, string UnapprovedPayloadJson, string ApprovedContentPayloadJson, string ApprovedVariantPayloadJson, string ApprovedDeliveryPayloadJson);
public sealed record ProductUpdateJobPayload(Guid JobId, Guid ProductId, Guid ProfileId, string Phase, string Mode, string PayloadHash, string UnapprovedPayloadJson, string ApprovedContentPayloadJson, string ApprovedVariantPayloadJson, string ApprovedDeliveryPayloadJson, string? ExternalOperationId, DateTimeOffset? SubmittedAt);
public sealed record ProductArchiveJobPayload(Guid JobId, Guid ProductId, Guid ProfileId, bool Archived, string Phase, string PayloadHash, string PayloadJson, string? ExternalOperationId, DateTimeOffset StartedAt, DateTimeOffset DeadlineAt);
public sealed record ExternalProductIdentity(string ExternalProductId, string? ExternalVariantId);
public sealed record RemoteProduct(string ExternalProductId, string? ExternalVariantId, string? Barcode, string? Sku, string RawJson);
public sealed record RemoteCatalogProduct(
    string ExternalProductId,
    string? ProductMainId,
    string Title,
    string Description,
    string? BrandExternalId,
    string? BrandName,
    string? CategoryExternalId,
    string? CategoryName,
    IReadOnlyList<string> ImageUrls,
    IReadOnlyList<RemoteCatalogVariant> Variants,
    string RawJson,
    bool IsDraft = false,
    bool IsPendingApproval = false);
public sealed record RemoteProductMatch(string MerchantSku, string Status, string? HepsiburadaSku, string? ProductName, string? BrandName, IReadOnlyList<string> ImageUrls, string? Barcode, string RawJson);
public sealed record RemoteInventoryLevel(string ExternalLocationId, string? LocationName, decimal Quantity, string RawJson);
public sealed record RemoteCatalogVariant(
    string ExternalVariantId,
    string Sku,
    string? Barcode,
    string? ModelCode,
    IReadOnlyDictionary<string, string> Options,
    bool Archived,
    decimal? SalePrice,
    decimal? ListPrice,
    decimal? VatRate,
    decimal? StockQuantity,
    string? Currency,
    string RawJson,
    IReadOnlyList<string>? ImageUrls = null,
    IReadOnlyList<RemoteInventoryLevel>? InventoryLevels = null);
public sealed record RemotePublicationStatus(string Barcode, string Status, string? ExternalProductId, string? ExternalVariantId, string? RejectionCode, string RawJson);
public sealed record ProductReadFilter(DateTimeOffset? ModifiedAfter, string? Barcode = null, string? ProductMainId = null, string? ContentId = null, string? ProductUrl = null, bool IncludePendingApproval = false);
public sealed record StockPushLine(Guid VariantId, string Barcode, decimal Quantity, long ProjectionVersion);
public sealed record PricePushLine(Guid VariantId, string Barcode, decimal ListPrice, decimal SalePrice, string Currency, long PriceVersion);
public sealed record PriceInventoryPushLine(Guid VariantId, Guid OfferId, string Barcode, decimal Quantity, decimal ListPrice, decimal SalePrice, string Currency, long ProjectionVersion, long PriceVersion, string PriceHash, string? MerchantSku = null, string? HepsiburadaSku = null);
public sealed record PriceInventoryJobPayload(Guid JobId, Guid ConnectionId, string Phase, string PayloadHash, string PayloadJson, IReadOnlyList<PriceInventoryPushLine> Lines, string? ExternalOperationId, DateTimeOffset? SubmittedAt, Guid? VariantId = null, Guid? ProductId = null);
public sealed record BatchLineResult(Guid LocalId, bool Succeeded, string? ErrorCode, bool Retryable);
public sealed record BatchResult<T>(IReadOnlyList<T> Lines, string? ExternalOperationId, bool IsPartial);
public sealed record OrderPollWindow(DateTimeOffset? ModifiedAfter, DateTimeOffset? ModifiedBefore, string? PackageItemStatuses = null, string? StoreFrontCode = null);
public sealed record RemoteOrderLine(string ExternalLineId, string Sku, string? Barcode, string Title, decimal Quantity, decimal UnitPrice, decimal VatRate, string RawStatus, string SourceSnapshotJson = "{}", decimal CancelledQuantity = 0);
public sealed record RemotePackageAllocation(string ExternalLineId, decimal AllocatedQuantity, decimal CancelledQuantity, decimal ShippedQuantity, decimal DeliveredQuantity, decimal ReturnedQuantity);
public sealed record RemotePackageInvoiceObservation(string? RawStatus, string? InvoiceNumber, string? InvoiceUrl, DateTimeOffset? SourceUpdatedAt);
public sealed record RemotePackage(string ExternalPackageId, string? OriginExternalPackageId, string RawStatus, DateTimeOffset OccurredAt, string? CargoProviderExternalId, string? CargoTrackingNumber, IReadOnlyList<RemotePackageAllocation> Allocations, decimal GrossAmount = 0, decimal DiscountAmount = 0, decimal NetAmount = 0, RemotePackageInvoiceObservation? Invoice = null, string? CreatedBy = null, bool IsStatusObservation = false);
public sealed record RemoteOrderRefund(string ExternalRefundId, DateTimeOffset OccurredAt, decimal Amount, string Currency, string RawJson);
public sealed record RemoteOrder(string ExternalOrderId, string OrderNumber, DateTimeOffset OrderedAt, DateTimeOffset LastModifiedAt, string Currency, decimal GrossAmount, decimal DiscountAmount, decimal NetAmount, string CustomerSnapshotJson, string ShipmentAddressSnapshotJson, string InvoiceAddressSnapshotJson, IReadOnlyList<RemoteOrderLine> Lines, IReadOnlyList<RemotePackage> Packages, string RawJson, DateTimeOffset? ShipmentDueAt = null, string PaymentStatus = "UNKNOWN", string CancellationStatus = "NOT_CANCELLED", string RefundStatus = "NOT_REFUNDED", decimal RefundedAmount = 0, IReadOnlyList<RemoteOrderRefund>? Refunds = null, string? LifecycleStatus = null);
public sealed record PackagePollWindow(DateTimeOffset? ModifiedAfter, DateTimeOffset? ModifiedBefore);
public sealed record RemoteOrderPackage(string ExternalOrderId, RemotePackage Package, RemoteOrder? OrderSnapshot = null);
public sealed record PackageTrackingStatusSnapshot(string PackageNumber, string? Status, string? CargoCompany, string? TrackingInfoCode, string? OrderNumber = null);

public sealed record ShopifyOrderCsvImportResult(
    int FileRows,
    int FileOrders,
    int MatchedOrders,
    int UpdatedOrders,
    int UpdatedLines,
    int UnmatchedOrders,
    int AmbiguousOrders,
    int UnmatchedLines,
    IReadOnlyList<string> UnmatchedOrderNumbers,
    IReadOnlyList<string> Issues);

public interface IShopifyOrderCsvImportService
{
    Task<ServiceResult<ShopifyOrderCsvImportResult>> ImportAsync(
        Guid tenantId,
        Guid actorUserId,
        Guid? connectionId,
        Stream csv,
        string correlationId,
        CancellationToken cancellationToken);
}
public sealed record PackageActionCommand(string ExternalPackageId, string Action, string PayloadJson);
public sealed record ShipmentActionJobPayload(Guid JobId, Guid PackageId, string Action, string PayloadJson);
public sealed record PackageActionResult(string ExternalPackageId, string Status, string? ExternalOperationId);
public sealed record RemoteCargoCompany(string ShortName, string Name);
public sealed record RemotePackageableLine(string LineItemId, int Quantity);
public sealed record PackageCreateLineRequest(Guid OrderLineId, int Quantity);
public sealed record RemotePackageCreateLineRequest(string LineItemId, int Quantity);
public sealed record OrderPackageCreateRequest(string Barcode, string CargoCompany, string Carrier, string CreationReason, int Deci, int ParcelQuantity, string ShippingAddressLabel, string ShippingModel, IReadOnlyList<PackageCreateLineRequest> LineItems);
public sealed record CreateOrderPackageCommand(string Barcode, string CargoCompany, string Carrier, string CreationReason, int Deci, int ParcelQuantity, string ShippingAddressLabel, string ShippingModel, IReadOnlyList<RemotePackageCreateLineRequest> LineItems);
public sealed record CreateOrderPackageResult(string PackageNumber);
public sealed record CommonLabelRequest(string CargoTrackingNumber, int BoxQuantity, decimal VolumetricHeight);
public sealed record CommonLabelDocument(string CargoTrackingNumber, string Format, byte[] Content);
public sealed record CommonLabelJobPayload(Guid JobId, Guid PackageId, string Phase, int BoxQuantity, decimal VolumetricHeight, DateTimeOffset StartedAt, DateTimeOffset DeadlineAt, string Format = "ZPL");
public sealed record CapabilityProbeJobPayload(Guid JobId, Guid PackageId, Guid ActorUserId, string CapabilityCode, int BoxQuantity, decimal VolumetricHeight, DateTimeOffset StartedAt, DateTimeOffset DeadlineAt);
public sealed record StageTestOrderJobPayload(Guid JobId, Guid ActorUserId, string Barcode, DateTimeOffset StartedAt);
public sealed record StageTestOrderResult(string OrderNumber);
public sealed record ReturnPollWindow(
    DateTimeOffset? ModifiedAfter,
    DateTimeOffset? ModifiedBefore,
    string? StoreFrontCode = null,
    string? Status = null,
    DateTimeOffset? StatusModifiedAfter = null,
    DateTimeOffset? StatusModifiedBefore = null);
public sealed record RemoteReturnLine(string ExternalLineId, string ExternalOrderLineId, decimal Quantity, IReadOnlyList<string>? AlternateExternalOrderLineIds = null);
public sealed record RemoteReturnClaim(string ExternalClaimId, string ExternalOrderId, string RawStatus, string? ReasonCode, string? ReasonText, DateTimeOffset? ActionDueAt, DateTimeOffset LastModifiedAt, IReadOnlyList<RemoteReturnLine> Lines, string RawJson, string? CargoProviderName = null, string? CargoTrackingNumber = null, string? CargoTrackingLink = null);
public sealed record ReturnEvidenceFile(string FileName, string MimeType, byte[] Content);
public sealed record ReturnActionCommand(string ExternalClaimId, IReadOnlyList<string> ExternalLineItemIds, string Action, string? ReasonCode, string? Explanation, IReadOnlyList<ReturnEvidenceFile> EvidenceFiles, string? FinalizedWith = null);
public sealed record ReturnActionResult(string ExternalClaimId, string Status, string? ExternalOperationId);
public sealed record ReturnIssueReason(string Id, string Name, bool EvidenceRequired);
public sealed record VerifiedWebhookEnvelope(string ExternalMessageId, string PayloadHash, string ResourceType, string RawJson);

public interface IConnectionPort
{
    Task<AdapterResult<ConnectionIdentity>> TestAsync(AdapterContext context, CancellationToken cancellationToken);
    Task<AdapterResult<IReadOnlyList<CapabilityEvidence>>> DiscoverCapabilitiesAsync(AdapterContext context, CancellationToken cancellationToken);
}

public interface IReferenceDataPort
{
    Task<AdapterResult<AdapterPageResult<RemoteReferenceItem>>> ReadAsync(AdapterContext context, ReferenceResource resource, AdapterPageRequest page, CancellationToken cancellationToken);
}

public interface IProductPort
{
    Task<AdapterResult<AdapterPageResult<RemoteProduct>>> ListAsync(AdapterContext context, AdapterPageRequest page, ProductReadFilter filter, CancellationToken cancellationToken);
    Task<AdapterResult<AdapterPageResult<RemoteCatalogProduct>>> ListCatalogAsync(AdapterContext context, AdapterPageRequest page, ProductReadFilter filter, CancellationToken cancellationToken);
    Task<AdapterResult<RemoteOperationRef>> CreateAsync(AdapterContext context, ProductPublication publication, CancellationToken cancellationToken);
    Task<AdapterResult<RemoteOperationRef>> UpdateUnapprovedAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken);
    Task<AdapterResult<RemoteOperationRef>> UpdateApprovedContentAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken);
    Task<AdapterResult<RemoteOperationRef>> UpdateApprovedVariantsAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken);
    Task<AdapterResult<RemoteOperationRef>> UpdateApprovedDeliveryAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken);
    Task<AdapterResult<RemoteOperationStatus>> GetOperationAsync(AdapterContext context, string externalOperationId, CancellationToken cancellationToken);
    Task<AdapterResult<RemotePublicationStatus>> GetPublicationStatusAsync(AdapterContext context, string barcode, CancellationToken cancellationToken);
    Task<AdapterResult<RemoteOperationRef>> ArchiveAsync(AdapterContext context, string payloadJson, CancellationToken cancellationToken);
}

public interface IHepsiburadaProductMatchPort
{
    Task<AdapterResult<AdapterPageResult<RemoteProductMatch>>> ListPendingProductMatchesAsync(AdapterContext context, AdapterPageRequest page, CancellationToken cancellationToken);
    Task<AdapterResult<bool>> ReviewProductMatchesAsync(AdapterContext context, IReadOnlyList<string> merchantSkus, bool approve, CancellationToken cancellationToken);
}

public interface IProductVisualLookupPort
{
    Task<AdapterResult<RemoteProduct?>> FindByBarcodeAsync(AdapterContext context, string barcode, CancellationToken cancellationToken);
}

public interface IInventoryPricePort
{
    Task<AdapterResult<RemoteOperationRef>> PushPriceAndInventoryAsync(AdapterContext context, string payloadJson, CancellationToken cancellationToken);
}

public interface IOrderPort
{
    Task<AdapterResult<AdapterPageResult<RemoteOrder>>> PollAsync(AdapterContext context, OrderPollWindow window, AdapterPageRequest page, CancellationToken cancellationToken);
    Task<AdapterResult<RemoteOrder>> GetAsync(AdapterContext context, string externalOrderId, CancellationToken cancellationToken);
    Task<AdapterResult<RemoteOrderPackage>> GetShipmentPackageAsync(AdapterContext context, string externalPackageId, CancellationToken cancellationToken) =>
        Task.FromResult(AdapterResult<RemoteOrderPackage>.Failure(new(AdapterErrorClass.NotSupported, "ORDER_PACKAGE_READ_UNSUPPORTED", "Bu pazaryerinde paket ayrıntısı okuması desteklenmiyor.", null, null, null)));
    Task<AdapterResult<RemoteOrderPackage>> GetShipmentPackageAsync(AdapterContext context, string externalPackageId, DateTimeOffset? packageStatusOccurredAt, CancellationToken cancellationToken) =>
        GetShipmentPackageAsync(context, externalPackageId, cancellationToken);
    Task<AdapterResult<PackageActionResult>> ExecutePackageActionAsync(AdapterContext context, PackageActionCommand command, CancellationToken cancellationToken);
    Task<AdapterResult<IReadOnlyList<RemoteCargoCompany>>> GetChangeableCargoCompaniesAsync(AdapterContext context, string externalPackageId, CancellationToken cancellationToken);
    Task<AdapterResult<IReadOnlyList<RemotePackageableLine>>> GetPackageableLineItemsAsync(AdapterContext context, string externalLineItemId, CancellationToken cancellationToken);
    Task<AdapterResult<CreateOrderPackageResult>> CreateOrderPackageAsync(AdapterContext context, CreateOrderPackageCommand command, CancellationToken cancellationToken);
    Task<AdapterResult<bool>> CreateCommonLabelAsync(AdapterContext context, CommonLabelRequest request, CancellationToken cancellationToken);
    Task<AdapterResult<CommonLabelDocument>> GetCommonLabelAsync(AdapterContext context, string cargoTrackingNumber, CancellationToken cancellationToken, string format = "ZPL");
    Task<AdapterResult<StageTestOrderResult>> CreateStageTestOrderAsync(AdapterContext context, string barcode, CancellationToken cancellationToken);
}

public interface IOrderPackageReadPort
{
    Task<AdapterResult<AdapterPageResult<RemoteOrderPackage>>> PollPackagesAsync(AdapterContext context, PackagePollWindow window, AdapterPageRequest page, CancellationToken cancellationToken);
    Task<AdapterResult<PackageTrackingStatusSnapshot>> GetPackageTrackingInfoAsync(AdapterContext context, string packageNumber, CancellationToken cancellationToken);
}

public interface IReturnPort
{
    Task<AdapterResult<AdapterPageResult<RemoteReturnClaim>>> PollAsync(AdapterContext context, ReturnPollWindow window, AdapterPageRequest page, CancellationToken cancellationToken);
    Task<AdapterResult<RemoteReturnClaim>> GetAsync(AdapterContext context, string externalReturnId, CancellationToken cancellationToken);
    Task<AdapterResult<IReadOnlyList<ReturnIssueReason>>> IssueReasonsAsync(AdapterContext context, CancellationToken cancellationToken);
    Task<AdapterResult<ReturnActionResult>> ExecuteAsync(AdapterContext context, ReturnActionCommand command, CancellationToken cancellationToken);
}

public sealed record QuestionPollRequest(string Kind, string? Status, DateTimeOffset? StartDate, DateTimeOffset? EndDate, int Page, int Size);
public sealed record RemoteQuestionConversation(string Author, string Text, DateTimeOffset CreatedAt, string? RejectionReason = null);
public sealed record RemoteMarketplaceQuestion(
    string Id, string Kind, string Status, string Text, string? ProductName, string? ProductImageUrl,
    string? ProductSku, string? ProductBarcode, string? ProductModelCode, string? CustomerName,
    string? OrderNumber, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt,
    DateTimeOffset LastModifiedAt, IReadOnlyList<RemoteQuestionConversation> Conversations);
public sealed record MarketplaceQuestionPage(IReadOnlyList<RemoteMarketplaceQuestion> Items, int Page, int TotalPages, long TotalElements);
public sealed record RemoteQuestionAnswerResult(bool Accepted, bool IsVerified, string? AnswerId);
public interface IQuestionPort
{
    Task<AdapterResult<MarketplaceQuestionPage>> ListQuestionsAsync(AdapterContext context, QuestionPollRequest request, CancellationToken cancellationToken);
    Task<AdapterResult<RemoteMarketplaceQuestion>> GetQuestionAsync(AdapterContext context, string questionId, string kind, CancellationToken cancellationToken);
    Task<AdapterResult<RemoteQuestionAnswerResult>> AnswerQuestionAsync(AdapterContext context, string questionId, string kind, string answer, CancellationToken cancellationToken);
}

public sealed record MarketplaceQuestionView(Guid Id, Guid ConnectionId, string PlatformCode, string StoreName, string ExternalQuestionId, string Kind, string Status, string QuestionText, string? ProductName, string? ProductImageUrl, string? ProductSku, string? ProductBarcode, string? ProductModelCode, string? CustomerName, string? ExternalOrderNumber, IReadOnlyList<RemoteQuestionConversation> Conversations, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt, DateTimeOffset LastRemoteModifiedAt, DateTimeOffset LastSyncedAt, long Version);
public sealed record MarketplaceQuestionTemplateView(Guid Id, string Title, string Text, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, long Version);
public sealed record SaveQuestionTemplateCommand(string Title, string Text);
public sealed record AnswerQuestionCommand(string Text, long Version);
public sealed record QuestionSyncView(int Added, int Updated, DateTimeOffset SyncedAt, string? Error = null);
public sealed record MarketplaceQuestionSyncStateView(Guid ConnectionId, string PlatformCode, string StoreName, bool HistoryImported, DateTimeOffset? HistoryStartedAt, string ProgressStatus, int ImportedCount, DateTimeOffset? LastRunStartedAt, DateTimeOffset? LastSuccessAt, string? LastError);
public sealed record QuestionListQuery(string Kind, string? Status, string? PlatformCode, Guid? ConnectionId, DateTimeOffset? DateFrom, DateTimeOffset? DateTo, string? Search, int Page = 1, int Limit = 50, string Sort = "NEWEST");
public sealed record MarketplaceQuestionListPage(IReadOnlyList<MarketplaceQuestionView> Items, int Page, int Limit, int TotalCount);

public interface IMarketplaceQuestionService
{
    Task<MarketplaceQuestionListPage> ListAsync(Guid tenantId, QuestionListQuery query, CancellationToken cancellationToken);
    Task<MarketplaceQuestionView?> GetAsync(Guid tenantId, Guid id, CancellationToken cancellationToken);
    Task<QuestionSyncView> SyncAsync(Guid tenantId, string? kind, CancellationToken cancellationToken);
    Task<QuestionSyncView> SyncConnectionAsync(Guid tenantId, Guid connectionId, string? kind, CancellationToken cancellationToken);
    Task<IReadOnlyList<MarketplaceQuestionSyncStateView>> SyncStatesAsync(Guid tenantId, CancellationToken cancellationToken);
    Task<ServiceResult<MarketplaceQuestionView>> AnswerAsync(Guid tenantId, Guid userId, Guid id, long expectedVersion, string answer, string idempotencyKey, string correlationId, CancellationToken cancellationToken);
    Task<IReadOnlyList<MarketplaceQuestionTemplateView>> TemplatesAsync(Guid tenantId, CancellationToken cancellationToken);
    Task<ServiceResult<MarketplaceQuestionTemplateView>> SaveTemplateAsync(Guid tenantId, Guid? id, long? expectedVersion, SaveQuestionTemplateCommand command, CancellationToken cancellationToken);
    Task<ServiceResult<bool>> DeleteTemplateAsync(Guid tenantId, Guid id, long expectedVersion, CancellationToken cancellationToken);
}

public interface IWebhookVerifier
{
    ValueTask<AdapterResult<VerifiedWebhookEnvelope>> VerifyAsync(ReadOnlyMemory<byte> rawBody, IReadOnlyDictionary<string, string> headers, Guid connectionId, Guid subscriptionId, CancellationToken cancellationToken);
}

public sealed record ConnectionView(Guid Id, Guid PublicId, string PlatformCode, string Environment, string DisplayName, string ExternalStoreId, string Status, string ApiVersion, DateTimeOffset? LastTestedAt, DateTimeOffset? LastSuccessAt, string? LastErrorCode, bool HasCredential, bool ExternalWritesEnabled, long Version, bool InvoiceCreationEnabled = true);
public sealed record CapabilityView(string Code, string SupportLevel, string ApiVersion, string Environment, string StoreScope, string? SourceUrl, DateTimeOffset? VerifiedAt, string? ConstraintsJson, string? EvidenceNote, long Version, bool VerifiedForConnection = false);
public sealed record RecordCapabilityEvidenceCommand(string SupportLevel, string SourceUrl, string SourceVersion, string Environment, string StoreScope, string EvidenceNote, string? FixtureChecksum, string? ConstraintsJson, DateTimeOffset VerifiedAt);
public sealed record CreateConnectionCommand(string DisplayName, string Environment, string ExternalStoreId, string ApiVersion, string? UserAgentIdentity, string? PlatformCode = null, string? ShopifyAccessToken = null, string? HepsiburadaServiceKey = null, string? HepsiburadaIntegratorUsername = null);
public sealed record UpdateConnectionCommand(string DisplayName, string? UserAgentIdentity, string? Environment = null, string? ExternalStoreId = null, bool? ExternalWritesEnabled = null, bool? InvoiceCreationEnabled = null);
public sealed record CredentialCommand(
    string? ApiKey,
    string? ApiSecret,
    string? Email = null,
    string? Password = null,
    string? ShopifyAccessToken = null,
    string? HepsiburadaServiceKey = null,
    string? HepsiburadaIntegratorUsername = null);
public sealed record SyncPolicyView(
    Guid Id,
    string ResourceType,
    int IntervalSeconds,
    int OverlapSeconds,
    int JitterSeconds,
    bool Enabled,
    long Version,
    DateTimeOffset? LastSuccessAt = null,
    DateTimeOffset? LastModifiedWatermark = null,
    string HealthStatus = "OFFLINE",
    DateTimeOffset? LastAttemptAt = null,
    int ConsecutiveFailureCount = 0,
    int LastRequestCount = 0,
    int LastReceivedCount = 0,
    int LastChangedCount = 0,
    int LastInsertedCount = 0,
    int LastUpdatedCount = 0,
    int LastSkippedCount = 0,
    int LastFailedCount = 0,
    int LastRetryCount = 0,
    int LastRateLimitCount = 0,
    string RecoveryGapStatus = "UNKNOWN",
    double? RecoveryGapDays = null,
    bool RequiresExternalWrites = false,
    long? LastDurationMs = null,
    int ConnectionBacklogCount = 0,
    DateTimeOffset? ConnectionOldestBacklogAt = null,
    int ConnectionManualReviewCount = 0,
    int ConnectionDeadJobCount24h = 0,
    DateTimeOffset? LastCursorAdvancedAt = null,
    DateTimeOffset? CursorStagnantSince = null,
    string CursorProgressStatus = "UNKNOWN");
public sealed record UpdateSyncPolicyCommand(int IntervalSeconds, int OverlapSeconds, int JitterSeconds, bool Enabled);
public sealed record WebhookSubscriptionView(Guid Id, string AuthenticationType, string Status, string? ExternalSubscriptionId, DateTimeOffset? VerifiedAt, DateTimeOffset? LastReceivedAt, long Version);
public sealed record CreateWebhookSubscriptionCommand(string AuthenticationType, string? Username, string? Password, string? ApiKey);
public sealed record CreatedWebhookSubscription(WebhookSubscriptionView Subscription, Guid ConnectionPublicId, string RouteToken);
public sealed record DeleteConnectionCommand(string Confirmation);
public sealed record DataVisibilityCommand(bool Hidden);
public sealed record ResetOperationalDataCommand(IReadOnlyList<string> Scopes, string Confirmation);
public sealed record OperationalDataResetView(
    int Products,
    int Orders,
    int Returns,
    int Invoices,
    int Categories = 0,
    int Brands = 0,
    int Options = 0,
    int CategoryAttributes = 0,
    bool ConnectionDeleted = false);

public interface IOperationalDataMaintenanceService
{
    Task<ServiceResult<OperationalDataResetView>> DeleteConnectionAsync(Guid tenantId, Guid actorUserId, Guid connectionId, long expectedVersion, DeleteConnectionCommand command, string correlationId, CancellationToken cancellationToken);
    Task<ServiceResult<OperationalDataResetView>> ResetConnectionDataAsync(Guid tenantId, Guid actorUserId, Guid connectionId, long expectedVersion, ResetOperationalDataCommand command, string correlationId, CancellationToken cancellationToken);
    Task<ServiceResult<bool>> SetDataVisibilityAsync(Guid tenantId, Guid actorUserId, Guid connectionId, long expectedVersion, bool hidden, string correlationId, CancellationToken cancellationToken);
    Task<ServiceResult<OperationalDataResetView>> ResetAsync(Guid tenantId, Guid actorUserId, ResetOperationalDataCommand command, string correlationId, CancellationToken cancellationToken);
}

public interface IMarketplaceConnectionService
{
    Task<PageResult<ConnectionView>> ListAsync(Guid tenantId, int limit, string? after, CancellationToken cancellationToken);
    Task<ServiceResult<ConnectionView>> CreateAsync(Guid tenantId, CreateConnectionCommand command, CancellationToken cancellationToken);
    Task<ServiceResult<ConnectionView>> GetAsync(Guid tenantId, Guid id, CancellationToken cancellationToken);
    Task<ServiceResult<ConnectionView>> UpdateAsync(Guid tenantId, Guid id, long expectedVersion, UpdateConnectionCommand command, CancellationToken cancellationToken);
    Task<ServiceResult<ConnectionView>> RotateCredentialAsync(Guid tenantId, Guid id, long expectedVersion, CredentialCommand command, CancellationToken cancellationToken);
    Task<ServiceResult<Guid>> EnqueueTestAsync(Guid tenantId, Guid id, string idempotencyKey, string correlationId, CancellationToken cancellationToken);
    Task<ServiceResult<Guid>> EnqueueInitialDataSyncAsync(Guid tenantId, Guid id, string idempotencyKey, long expectedVersion, string correlationId, CancellationToken cancellationToken);
    Task<ServiceResult<ConnectionView>> SetActiveAsync(Guid tenantId, Guid id, long expectedVersion, bool active, CancellationToken cancellationToken);
    Task<ServiceResult<IReadOnlyList<CapabilityView>>> CapabilitiesAsync(Guid tenantId, Guid id, CancellationToken cancellationToken);
    Task<ServiceResult<CapabilityView>> RecordCapabilityEvidenceAsync(Guid tenantId, Guid actorUserId, Guid id, string code, long expectedVersion, RecordCapabilityEvidenceCommand command, string correlationId, CancellationToken cancellationToken);
    Task<ServiceResult<IReadOnlyList<SyncPolicyView>>> SyncPoliciesAsync(Guid tenantId, Guid id, CancellationToken cancellationToken);
    Task<ServiceResult<SyncPolicyView>> UpsertSyncPolicyAsync(Guid tenantId, Guid id, string resourceType, long? expectedVersion, UpdateSyncPolicyCommand command, CancellationToken cancellationToken);
    Task<ServiceResult<IReadOnlyList<WebhookSubscriptionView>>> WebhooksAsync(Guid tenantId, Guid id, CancellationToken cancellationToken);
    Task<ServiceResult<CreatedWebhookSubscription>> CreateWebhookAsync(Guid tenantId, Guid id, CreateWebhookSubscriptionCommand command, CancellationToken cancellationToken);
}

public sealed record OrderListView(
    Guid Id,
    string OrderNumber,
    string DerivedStatus,
    string Currency,
    decimal NetAmount,
    DateTimeOffset OrderedAt,
    int LineCount,
    int PackageCount,
    long Version,
    Guid? ConnectionId = null,
    string PlatformCode = "TRENDYOL",
    string PlatformDisplayName = "Trendyol",
    string CustomerName = "—",
    string OrderType = "BIREYSEL",
    bool IsMicroExport = false,
    DateTimeOffset? ShipmentDueAt = null,
    bool IsDeadlineCritical = false,
    string InvoiceStatus = "FATURA_BEKLIYOR",
    string? CargoProviderName = null,
    string? CargoTrackingNumber = null,
    string? PrimaryImageUrl = null,
    decimal ProductQuantity = 0,
    string? CustomerEmail = null,
    string? CustomerTaxOrIdentityNumber = null,
    string ShipmentAddressJson = "{}",
    string InvoiceAddressJson = "{}",
    decimal GrossAmount = 0,
    decimal DiscountAmount = 0,
    IReadOnlyList<OrderLineView>? Lines = null,
    IReadOnlyList<ShipmentView>? Packages = null,
    Guid? InvoiceId = null,
    string? InvoiceDocumentUrl = null);
public sealed record OrderListQuery(
    string? Status = null,
    string? Search = null,
    string? Platform = null,
    string? Listing = null,
    string? Cargo = null,
    string? Invoice = null,
    string? InvoiceType = null,
    string? InvoiceRegion = null,
    DateTimeOffset? DateFrom = null,
    DateTimeOffset? DateTo = null,
    string? Sort = null,
    IReadOnlyList<string>? Platforms = null);
public sealed record OrderSummaryView(
    int All,
    int New,
    int Processing,
    int Shipped,
    int Delivered,
    int Resent,
    int OnHold,
    int Cancelled = 0,
    int Returned = 0,
    int ReturnInTransit = 0,
    int PartiallyCancelled = 0,
    int ManualReview = 0,
    int Pending = 0,
    int Unverified = 0);
public sealed record OrderLineView(
    Guid Id,
    string Sku,
    string? Barcode,
    string Title,
    decimal OrderedQuantity,
    decimal CancelledQuantity,
    decimal ShippedQuantity,
    decimal DeliveredQuantity,
    decimal ReturnedQuantity,
    decimal UnitPrice,
    decimal VatRate,
    string RawStatus,
    Guid? VariantId = null,
    string? ModelCode = null,
    string? OptionSignature = null,
    string? ImageUrl = null,
    decimal AllocatedQuantity = 0);
public sealed record ShipmentView(
    Guid Id,
    Guid OrderId,
    string OrderNumber,
    string ExternalPackageId,
    string Status,
    string RawStatus,
    string? CargoTrackingNumber,
    DateTimeOffset StatusOccurredAt,
    long Version,
    string? CargoProviderName = null,
    bool IsResend = false);
public sealed record OrderDetailView(
    Guid Id,
    string OrderNumber,
    string DerivedStatus,
    string Currency,
    decimal GrossAmount,
    decimal DiscountAmount,
    decimal NetAmount,
    DateTimeOffset OrderedAt,
    IReadOnlyList<OrderLineView> Lines,
    IReadOnlyList<ShipmentView> Packages,
    long Version,
    Guid? ConnectionId = null,
    string PlatformCode = "TRENDYOL",
    string PlatformDisplayName = "Trendyol",
    string CustomerName = "—",
    string? CustomerEmail = null,
    string? CustomerTaxOrIdentityNumber = null,
    string OrderType = "BIREYSEL",
    bool IsMicroExport = false,
    string ShipmentAddressJson = "{}",
    string InvoiceAddressJson = "{}",
    DateTimeOffset? ShipmentDueAt = null,
    string InvoiceStatus = "FATURA_BEKLIYOR",
    string? CustomerPhone = null,
    bool? IsEInvoiceAvailable = null,
    string? InvoiceDocumentUrl = null);
public sealed record ShipmentDetailView(ShipmentView Package, IReadOnlyList<string> AllowedActions, IReadOnlyList<string> SupportedLabelFormats, bool IsStageConnection, IReadOnlyList<ShipmentDocumentView> Documents, string PlatformCode = "TRENDYOL");
public sealed record ShipmentDocumentView(Guid Id, string DocumentKind, string Format, string Source, int DocumentVersion, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt);
public sealed record ShipmentActionCommand(string Action, string PayloadJson);
public sealed record ReturnLineView(
    Guid Id,
    string ExternalLineId,
    Guid OrderLineId,
    string Sku,
    string? Barcode,
    string Title,
    decimal Quantity,
    decimal DisposedQuantity,
    decimal RemainingQuantity,
    decimal UnitPrice,
    string? ImageUrl,
    bool HasInventoryMapping,
    string? OptionSignature = null,
    string? ModelCode = null);
public sealed record ReturnListView(
    Guid Id,
    string ExternalClaimId,
    string OrderNumber,
    string Status,
    string RawStatus,
    string? ReasonText,
    DateTimeOffset? ActionDueAt,
    long Version,
    string CustomerName = "—",
    DateTimeOffset? OrderedAt = null,
    decimal OrderAmount = 0,
    string Currency = "TRY",
    string? CargoProviderName = null,
    string? CargoTrackingNumber = null,
    string? PrimaryImageUrl = null,
    int ProductCount = 0,
    string? PrimaryBarcode = null,
    IReadOnlyList<OrderLineView>? Lines = null,
    string? PackageNumber = null,
    string InvoiceStatus = "FATURA_BEKLIYOR",
    decimal GrossAmount = 0,
    decimal DiscountAmount = 0,
    bool IsMicroExport = false,
    Guid? ConnectionId = null,
    string PlatformCode = "TRENDYOL",
    string PlatformDisplayName = "Trendyol",
    string? OrderCargoProviderName = null,
    string? OrderCargoTrackingNumber = null,
    string? ReasonCode = null,
    DateTimeOffset? ApprovedAt = null,
    string? CargoTrackingLink = null);
public sealed record ReturnListQuery(
    string? Status = null,
    string? Customer = null,
    string? OrderNumber = null,
    string? ClaimCode = null,
    string? Barcode = null,
    string? Reason = null,
    DateTimeOffset? DateFrom = null,
    DateTimeOffset? DateTo = null,
    string? Search = null);
public sealed record ReturnDetailView(
    Guid Id,
    string ExternalClaimId,
    string OrderNumber,
    string Status,
    string RawStatus,
    string? ReasonCode,
    string? ReasonText,
    DateTimeOffset? ActionDueAt,
    IReadOnlyList<string> AllowedActions,
    long Version,
    string CustomerName = "—",
    DateTimeOffset? OrderedAt = null,
    decimal OrderAmount = 0,
    string Currency = "TRY",
    string? CargoProviderName = null,
    string? CargoTrackingNumber = null,
    IReadOnlyList<ReturnLineView>? Lines = null,
    bool StockDispositionAvailable = false,
    DateTimeOffset? ApprovedAt = null,
    bool ExternalWritesEnabled = false,
    bool DecisionPending = false,
    string? PlatformCode = null,
    string? CargoTrackingLink = null);
public sealed record ReturnDecisionCommand(string Action, string? ReasonCode, string? Explanation, IReadOnlyList<Guid>? EvidenceAssetIds, IReadOnlyList<Guid>? ReturnLineIds = null, string? FinalizedWith = null);
public sealed record ReturnDispositionCommand(Guid ReturnLineId, string Disposition, decimal Quantity, string Reason);

public interface IMarketplaceSalesService
{
    Task<PageResult<OrderListView>> OrdersAsync(Guid tenantId, int limit, int page, string? after, OrderListQuery query, CancellationToken cancellationToken);
    Task<OrderSummaryView> OrderSummaryAsync(Guid tenantId, string? platform, CancellationToken cancellationToken);
    Task<ServiceResult<string>> ProductImageAsync(Guid tenantId, string? barcode, string correlationId, CancellationToken cancellationToken, Guid? connectionId = null, string? productName = null);
    Task<ServiceResult<OrderDetailView>> OrderAsync(Guid tenantId, Guid id, CancellationToken cancellationToken);
    Task<PageResult<ShipmentView>> ShipmentsAsync(Guid tenantId, int limit, string? after, string? status, CancellationToken cancellationToken);
    Task<ServiceResult<ShipmentDetailView>> ShipmentAsync(Guid tenantId, Guid id, CancellationToken cancellationToken);
    Task<ServiceResult<IReadOnlyList<RemoteCargoCompany>>> ChangeableCargoCompaniesAsync(Guid tenantId, Guid packageId, string correlationId, CancellationToken cancellationToken);
    Task<ServiceResult<CreateOrderPackageResult>> CreateOrderPackageInstantAsync(Guid tenantId, Guid orderId, long expectedVersion, OrderPackageCreateRequest command, string idempotencyKey, string correlationId, CancellationToken cancellationToken);
    Task<ServiceResult<Guid>> EnqueueOrderSyncAsync(Guid tenantId, Guid connectionId, string? externalOrderId, bool full, string correlationId, CancellationToken cancellationToken, string? packageNumber = null);
    Task<ServiceResult<Guid>> EnqueueReferenceSyncAsync(Guid tenantId, Guid connectionId, string resourceType, string? parentExternalId, string correlationId, CancellationToken cancellationToken);
    Task<ServiceResult<Guid>> EnqueueProductSyncAsync(Guid tenantId, Guid connectionId, bool full, bool newOnly, bool existingOnly, bool mappingOnly, bool optionsOnly, bool includeArchived, bool includeDrafts, bool includePendingApproval, bool updateExistingProducts, string? productLookup, string correlationId, CancellationToken cancellationToken);
    Task<ServiceResult<Guid>> EnqueueShipmentActionAsync(Guid tenantId, Guid packageId, long expectedVersion, ShipmentActionCommand command, string idempotencyKey, string correlationId, CancellationToken cancellationToken);
    Task<ServiceResult<ShipmentView>> ProcessShipmentInstantAsync(Guid tenantId, Guid packageId, long expectedVersion, string idempotencyKey, string correlationId, CancellationToken cancellationToken);
    Task<ServiceResult<ShipmentView>> ChangeCargoProviderInstantAsync(Guid tenantId, Guid packageId, long expectedVersion, ShipmentActionCommand command, string idempotencyKey, string correlationId, CancellationToken cancellationToken);
    Task<ServiceResult<Guid>> EnqueueCommonLabelAsync(Guid tenantId, Guid packageId, long expectedVersion, int boxQuantity, decimal volumetricHeight, string idempotencyKey, string correlationId, CancellationToken cancellationToken, string format = "ZPL");
    Task<ServiceResult<Guid>> EnqueueLabelCapabilityProbeAsync(Guid tenantId, Guid actorUserId, Guid packageId, long expectedVersion, string capabilityCode, int boxQuantity, decimal volumetricHeight, string idempotencyKey, string correlationId, CancellationToken cancellationToken);
    Task<ServiceResult<Guid>> EnqueueStageTestOrderAsync(Guid tenantId, Guid actorUserId, Guid connectionId, string idempotencyKey, string correlationId, CancellationToken cancellationToken);
    Task<PageResult<ReturnListView>> ReturnsAsync(Guid tenantId, int limit, string? after, ReturnListQuery query, bool latest, CancellationToken cancellationToken);
    Task<ServiceResult<ReturnDetailView>> ReturnAsync(Guid tenantId, Guid id, CancellationToken cancellationToken);
    Task<ServiceResult<IReadOnlyList<ReturnIssueReason>>> ReturnIssueReasonsAsync(Guid tenantId, Guid id, string correlationId, CancellationToken cancellationToken);
    Task<ServiceResult<Guid>> EnqueueReturnSyncAsync(Guid tenantId, Guid connectionId, string correlationId, CancellationToken cancellationToken);
    Task<ServiceResult<Guid>> EnqueueQuestionSyncAsync(Guid tenantId, Guid connectionId, string? kind, string correlationId, CancellationToken cancellationToken);
    Task<ServiceResult<ReturnDetailView>> MarkReturnReceivedAsync(Guid tenantId, Guid userId, Guid claimId, long expectedVersion, string idempotencyKey, string correlationId, CancellationToken cancellationToken);
    Task<ServiceResult<ReturnDetailView>> ProcessReturnActionInstantAsync(Guid tenantId, Guid userId, Guid claimId, long expectedVersion, ReturnDecisionCommand command, string idempotencyKey, string correlationId, CancellationToken cancellationToken);
    Task<ServiceResult<ReturnDetailView>> ApplyDispositionAsync(Guid tenantId, Guid userId, Guid claimId, ReturnDispositionCommand command, string idempotencyKey, string correlationId, CancellationToken cancellationToken);
}

public interface IMarketplaceWebhookService
{
    Task<ServiceResult<bool>> ReceiveAsync(Guid connectionPublicId, string routeToken, ReadOnlyMemory<byte> rawBody, IReadOnlyDictionary<string, string> headers, string correlationId, CancellationToken cancellationToken);
}

public interface IMarketplaceJobProcessor
{
    Task<JobExecutionResult> ProcessAsync(Guid tenantId, Guid? connectionId, string jobType, string payloadJson, string correlationId, CancellationToken cancellationToken, Guid? jobId = null);
}

public sealed record ReconciliationDifferenceView(string EntityType, string EntityKey, string FieldName, string? LocalValueHash, string? RemoteValueHash, string Resolution);
public sealed record ReconciliationRunView(Guid Id, Guid ConnectionId, string Scope, string Status, int ComparedCount, int DifferenceCount, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt, IReadOnlyList<ReconciliationDifferenceView> Differences);

public interface IMarketplaceReconciliationService
{
    Task<ServiceResult<ReconciliationRunView>> RunLocalDryAsync(Guid tenantId, Guid connectionId, string scope, CancellationToken cancellationToken);
    Task<ServiceResult<ReconciliationRunView>> GetAsync(Guid tenantId, Guid id, CancellationToken cancellationToken);
}
