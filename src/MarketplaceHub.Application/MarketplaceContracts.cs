using MarketplaceHub.Domain;

namespace MarketplaceHub.Application;

public static class ActiveIntegrationScope
{
    public static bool Contains(string? platformCode) => platformCode is "TRENDYOL" or "TRENDYOL_EFATURAM" or "SHOPIFY" or "HEPSIBURADA" or "N11" or "PAZARAMA" or "PTTAVM";

    public static bool IsMarketplace(string? platformCode) => platformCode is "TRENDYOL" or "SHOPIFY" or "HEPSIBURADA" or "N11" or "PAZARAMA" or "PTTAVM";
}

public static class MarketplaceJobTypes
{
    public const string ActivationBootstrapPrefix = "activation-bootstrap:";
    public const string ConnectionTest = "TRENDYOL_CONNECTION_TEST";
    public const string ReferenceSync = "TRENDYOL_REFERENCE_SYNC";
    public const string HepsiburadaReferenceSync = "HEPSIBURADA_REFERENCE_SYNC";
    public const string OrderSync = "TRENDYOL_ORDER_SYNC";
    public const string OrderRecoverySync = "TRENDYOL_ORDER_RECOVERY_SYNC";
    public const string OrderStatusSync = "ORDER_STATUS_SYNC";
    public const string TrendyolOrderCargoInfoReconciliation = "TRENDYOL_ORDER_CARGO_INFO_RECONCILIATION";
    // Trendyol current account statement (settlements, deduction invoices, cargo invoice lines); read only.
    public const string TrendyolFinanceSync = "TRENDYOL_FINANCE_SYNC";
    // Pazarama's money movements of each recent order (order/paymentAgreement), for its commission in the reports.
    public const string PazaramaFinanceSync = "PAZARAMA_FINANCE_SYNC";
    public const string OrderReconciliation = "ORDER_RECONCILIATION";
    public const string OrderInvoiceReconciliation = "ORDER_INVOICE_RECONCILIATION";
    public const string ProductSync = "PRODUCT_SYNC";
    public const string ShipmentAction = "SHIPMENT_ACTION";
    public const string ReturnSync = "RETURN_SYNC";
    public const string ReturnStatusSync = "RETURN_STATUS_SYNC";
    public const string ReturnReconciliation = "RETURN_RECONCILIATION";
    public const string ReturnAction = "RETURN_ACTION";
    public const string ExternalEffectReconcile = "EXTERNAL_EFFECT_RECONCILE";
    public const string WebhookIngest = "TRENDYOL_WEBHOOK_INGEST";
    public const string ProductCreate = "PRODUCT_CREATE";
    public const string ProductApprovalReconcile = "PRODUCT_APPROVAL_RECONCILE";
    public const string ProductUpdate = "PRODUCT_UPDATE";
    public const string ProductArchive = "PRODUCT_ARCHIVE";
    public const string PriceInventorySync = "PRICE_INVENTORY_SYNC";
    public const string StockProjectionDispatch = "STOCK_PROJECTION_DISPATCH";
    public const string StockReconciliation = "STOCK_RECONCILIATION";
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
    public const string N11ConnectionTest = "N11_CONNECTION_TEST";
    public const string N11ReferenceSync = "N11_REFERENCE_SYNC";
    public const string N11OrderSync = "N11_ORDER_SYNC";
    public const string N11OrderRecoverySync = "N11_ORDER_RECOVERY_SYNC";
    public const string PazaramaConnectionTest = "PAZARAMA_CONNECTION_TEST";
    public const string PazaramaReferenceSync = "PAZARAMA_REFERENCE_SYNC";
    public const string PazaramaOrderSync = "PAZARAMA_ORDER_SYNC";
    public const string PazaramaOrderRecoverySync = "PAZARAMA_ORDER_RECOVERY_SYNC";
    public const string PazaramaOrderStatusSync = "PAZARAMA_ORDER_STATUS_SYNC";
    public const string PttAvmConnectionTest = "PTTAVM_CONNECTION_TEST";
    public const string PttAvmReferenceSync = "PTTAVM_REFERENCE_SYNC";
    public const string PttAvmOrderSync = "PTTAVM_ORDER_SYNC";
    public const string PttAvmOrderRecoverySync = "PTTAVM_ORDER_RECOVERY_SYNC";
    public const string PttAvmOrderStatusSync = "PTTAVM_ORDER_STATUS_SYNC";
    public const string QuestionSync = "MARKETPLACE_QUESTION_SYNC";
    public const string CatalogStructureImport = "CATALOG_STRUCTURE_IMPORT";
    // The nightly read of a connection's whole marketplace catalog that only checks which linked codes are still
    // there (link health); it never writes products, content, prices or stock.
    public const string ListingVerify = "LISTING_VERIFY";
    // Single-variant lookups at the marketplace by barcode (or the platform's own key) for codes a price/stock send
    // refused as unknown or a verification scan did not see.
    public const string ListingLookup = "LISTING_LOOKUP";
    // Removes every operational record of one connection (Entegrasyonlar › Verileri Sıfırla › Tam Sıfırlama) while it
    // holds all of the connection's sync locks; the connection's settings stay.
    public const string ConnectionFullReset = "CONNECTION_FULL_RESET";

    // Every job type executed by the marketplace processor. Each of them runs under the
    // per-connection execution lock of its lock group.
    public static readonly IReadOnlyList<string> ProcessorJobTypes =
    [
        ConnectionTest, ShopifyConnectionTest, HepsiburadaConnectionTest, N11ConnectionTest, PazaramaConnectionTest, PttAvmConnectionTest,
        ReferenceSync, HepsiburadaReferenceSync, N11ReferenceSync, PazaramaReferenceSync, PttAvmReferenceSync,
        ProductSync, ShopifyProductSync, HepsiburadaProductSync,
        ProductCreate, ProductApprovalReconcile, ProductUpdate, ProductArchive,
        PriceInventorySync, StockProjectionDispatch, ExternalEffectReconcile,
        OrderSync, ShopifyOrderSync, HepsiburadaOrderSync, N11OrderSync, PazaramaOrderSync, PttAvmOrderSync,
        OrderRecoverySync, ShopifyOrderRecoverySync, HepsiburadaOrderRecoverySync, N11OrderRecoverySync, PazaramaOrderRecoverySync, PttAvmOrderRecoverySync,
        OrderStatusSync, ShopifyOrderStatusSync, HepsiburadaOrderStatusSync, PazaramaOrderStatusSync, PttAvmOrderStatusSync, TrendyolOrderCargoInfoReconciliation,
        OrderReconciliation, ShopifyOrderReconciliation,
        OrderInvoiceReconciliation, ShopifyOrderInvoiceReconciliation, HepsiburadaOrderInvoiceReconciliation,
        ShipmentAction, CommonLabel, CapabilityProbe, StageTestOrder,
        ReturnSync, HepsiburadaReturnSync, ReturnStatusSync, ReturnReconciliation, ReturnAction,
        StockReconciliation,
        WebhookIngest, ShopifyWebhookIngest, HepsiburadaWebhookIngest,
        QuestionSync, CatalogStructureImport, TrendyolFinanceSync, PazaramaFinanceSync,
        ListingVerify, ListingLookup, ConnectionFullReset
    ];

    public static bool IsMarketplaceProcessorJob(string? jobType) => jobType is not null && ProcessorJobTypes.Contains(jobType);

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
        // N11 has its own connection test, reference and order read job types. Order and
        // stock reconciliation and the price/stock writes reuse the shared job types and
        // reach the n11 adapter through the connection's port routing
        // (docs/N11-ENTEGRASYON-PLANI.md).
        "N11" => jobType switch
        {
            ConnectionTest => N11ConnectionTest,
            ReferenceSync => N11ReferenceSync,
            OrderSync => N11OrderSync,
            OrderRecoverySync => N11OrderRecoverySync,
            _ => jobType
        },
        // Pazarama gets its own read job types phase by phase (docs/PAZARAMA-ENTEGRASYON-PLANI.md);
        // phase 1 is the connection test.
        "PAZARAMA" => jobType switch
        {
            ConnectionTest => PazaramaConnectionTest,
            ReferenceSync => PazaramaReferenceSync,
            OrderSync => PazaramaOrderSync,
            OrderRecoverySync => PazaramaOrderRecoverySync,
            OrderStatusSync => PazaramaOrderStatusSync,
            _ => jobType
        },
        // PTT AVM takes the same path (docs/PTTAVM-ENTEGRASYON-PLANI.md); phase 1 is the connection test, phase 2 the order reads, phase 3 the category tree.
        "PTTAVM" => jobType switch
        {
            ConnectionTest => PttAvmConnectionTest,
            ReferenceSync => PttAvmReferenceSync,
            OrderSync => PttAvmOrderSync,
            OrderRecoverySync => PttAvmOrderRecoverySync,
            OrderStatusSync => PttAvmOrderStatusSync,
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
        or MarketplaceJobTypes.HepsiburadaOrderRecoverySync or MarketplaceJobTypes.N11OrderRecoverySync or MarketplaceJobTypes.PazaramaOrderRecoverySync or MarketplaceJobTypes.PttAvmOrderRecoverySync;

    private static bool IsPromotableOrderSyncType(string? requestedJobType, string? conflictingJobType) => requestedJobType switch
    {
        MarketplaceJobTypes.OrderRecoverySync => conflictingJobType is MarketplaceJobTypes.OrderSync or MarketplaceJobTypes.OrderRecoverySync or MarketplaceJobTypes.OrderStatusSync,
        MarketplaceJobTypes.ShopifyOrderRecoverySync => conflictingJobType is MarketplaceJobTypes.ShopifyOrderSync or MarketplaceJobTypes.ShopifyOrderRecoverySync or MarketplaceJobTypes.ShopifyOrderStatusSync,
        MarketplaceJobTypes.HepsiburadaOrderRecoverySync => conflictingJobType is MarketplaceJobTypes.HepsiburadaOrderSync or MarketplaceJobTypes.HepsiburadaOrderRecoverySync or MarketplaceJobTypes.HepsiburadaOrderStatusSync,
        MarketplaceJobTypes.N11OrderRecoverySync => conflictingJobType is MarketplaceJobTypes.N11OrderSync or MarketplaceJobTypes.N11OrderRecoverySync,
        MarketplaceJobTypes.PazaramaOrderRecoverySync => conflictingJobType is MarketplaceJobTypes.PazaramaOrderSync or MarketplaceJobTypes.PazaramaOrderRecoverySync or MarketplaceJobTypes.PazaramaOrderStatusSync,
        MarketplaceJobTypes.PttAvmOrderRecoverySync => conflictingJobType is MarketplaceJobTypes.PttAvmOrderSync or MarketplaceJobTypes.PttAvmOrderRecoverySync or MarketplaceJobTypes.PttAvmOrderStatusSync,
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
            && (conflictingStatus is JobStatus.Leased or JobStatus.RetryScheduled
                || conflictingStatus == JobStatus.Pending && !conflictingJobHasStarted)
            && IsOrderSyncLaneJob(requestedJobType, conflictingJobType))
            return TargetedOrderSyncConflictResolution.QueueBehindActiveWork;

        return TargetedOrderSyncConflictResolution.Reject;
    }

    private static bool IsOrderSyncLaneJob(string? requestedJobType, string? conflictingJobType) => requestedJobType switch
    {
        MarketplaceJobTypes.OrderSync => conflictingJobType is MarketplaceJobTypes.OrderSync or MarketplaceJobTypes.OrderRecoverySync or MarketplaceJobTypes.OrderStatusSync or MarketplaceJobTypes.OrderReconciliation,
        MarketplaceJobTypes.ShopifyOrderSync => conflictingJobType is MarketplaceJobTypes.ShopifyOrderSync or MarketplaceJobTypes.ShopifyOrderRecoverySync or MarketplaceJobTypes.ShopifyOrderStatusSync or MarketplaceJobTypes.ShopifyOrderReconciliation,
        MarketplaceJobTypes.HepsiburadaOrderSync => conflictingJobType is MarketplaceJobTypes.HepsiburadaOrderSync or MarketplaceJobTypes.HepsiburadaOrderRecoverySync or MarketplaceJobTypes.HepsiburadaOrderStatusSync,
        MarketplaceJobTypes.N11OrderSync => conflictingJobType is MarketplaceJobTypes.N11OrderSync or MarketplaceJobTypes.N11OrderRecoverySync,
        MarketplaceJobTypes.PazaramaOrderSync => conflictingJobType is MarketplaceJobTypes.PazaramaOrderSync or MarketplaceJobTypes.PazaramaOrderRecoverySync or MarketplaceJobTypes.PazaramaOrderStatusSync,
        MarketplaceJobTypes.PttAvmOrderSync => conflictingJobType is MarketplaceJobTypes.PttAvmOrderSync or MarketplaceJobTypes.PttAvmOrderRecoverySync or MarketplaceJobTypes.PttAvmOrderStatusSync,
        _ => false
    };

    private static bool IsSameIncrementalOrderSyncType(string? requestedJobType, string? conflictingJobType) => requestedJobType switch
    {
        MarketplaceJobTypes.OrderSync => conflictingJobType == MarketplaceJobTypes.OrderSync,
        MarketplaceJobTypes.ShopifyOrderSync => conflictingJobType == MarketplaceJobTypes.ShopifyOrderSync,
        MarketplaceJobTypes.HepsiburadaOrderSync => conflictingJobType == MarketplaceJobTypes.HepsiburadaOrderSync,
        MarketplaceJobTypes.N11OrderSync => conflictingJobType == MarketplaceJobTypes.N11OrderSync,
        MarketplaceJobTypes.PazaramaOrderSync => conflictingJobType == MarketplaceJobTypes.PazaramaOrderSync,
        MarketplaceJobTypes.PttAvmOrderSync => conflictingJobType == MarketplaceJobTypes.PttAvmOrderSync,
        _ => false
    };
}

public static class MarketplaceSyncPolicyRules
{
    public static string CursorResourceType(string resourceType) => resourceType.Trim().ToUpperInvariant() switch
    {
        "ORDERS" => "ORDERS_HOT",
        "ORDER_RECOVERY" => "ORDERS_RECOVERY",
        "ORDER_RECONCILE_SHORT" or "ORDER_RECONCILE_MEDIUM" or "ORDER_RECONCILE_DAILY" => "ORDER_RECONCILIATION",
        // The return and stock reconciliations write one cursor for their three tiers, like the order ones.
        "RETURN_RECONCILE_SHORT" or "RETURN_RECONCILE_MEDIUM" or "RETURN_RECONCILE_DAILY" => "RETURN_RECONCILIATION",
        "STOCK_RECONCILE_SHORT" or "STOCK_RECONCILE_MEDIUM" or "STOCK_RECONCILE_DAILY" => "STOCK_RECONCILIATION",
        _ => resourceType.Trim().ToUpperInvariant()
    };

    public static bool RequiresExternalWrites(string? resourceType) => resourceType?.Trim().ToUpperInvariant() is
        "STOCK_RECONCILE_SHORT" or "STOCK_RECONCILE_MEDIUM" or "STOCK_RECONCILE_DAILY"
        or MarketplaceExternalWritePolicies.Price
        or MarketplaceExternalWritePolicies.Stock
        or MarketplaceExternalWritePolicies.Shipment
        or MarketplaceExternalWritePolicies.Return
        or MarketplaceExternalWritePolicies.Manual;
}

public static class MarketplaceExternalWritePolicies
{
    public const string Price = "PRICE_WRITE";
    public const string Stock = "STOCK_WRITE";
    public const string Shipment = "SHIPMENT_WRITE";
    public const string Return = "RETURN_WRITE";
    // PTT AVM only: the sends a user starts from the panel (price-stock sends, product sends). PTT AVM has no
    // shipment or return write API, so this is its "Manuel Dış Yazmalar" switch.
    public const string Manual = "MANUAL_WRITE";

    // The automatic write flows, which the master switch turning off also switches off.
    public static bool IsPolicy(string? resourceType) => resourceType?.Trim().ToUpperInvariant() is
        Price or Stock or Shipment or Return;

    // Every write switch, the PTT AVM manual one included: a permission, not a scheduled read.
    public static bool IsPermissionPolicy(string? resourceType) =>
        IsPolicy(resourceType) || resourceType?.Trim().ToUpperInvariant() == Manual;
}

public enum PttAvmManualWriteDecision
{
    Allowed,
    MasterSwitchOff,
    PolicyOff
}

// PTT AVM manual writes need the master write switch and the connection's MANUAL_WRITE policy. A missing policy row
// is not read as permission (the row is created with the connection and by the data migration).
public static class PttAvmManualWritePolicy
{
    public const string DisabledCode = "EXTERNAL_WRITE_POLICY_DISABLED";
    public const string DisabledMessage = "PTT AVM manuel dış yazmaları kapalı; Entegrasyonlar → Pazaryerine Yazma bölümünden açın.";

    public static bool Applies(string? platformCode) => string.Equals(platformCode?.Trim(), "PTTAVM", StringComparison.OrdinalIgnoreCase);

    public static PttAvmManualWriteDecision Decide(bool masterWritesEnabled, bool? manualPolicyEnabled) =>
        !masterWritesEnabled ? PttAvmManualWriteDecision.MasterSwitchOff
        : manualPolicyEnabled == true ? PttAvmManualWriteDecision.Allowed
        : PttAvmManualWriteDecision.PolicyOff;
}

public static class MarketplaceCapabilities
{
    public const string ConnectionTest = "CONNECTION_TEST";
    public const string ShopifyAppScopes = "SHOPIFY_APP_SCOPES";
    public const string ReferenceRead = "REFERENCE_READ";
    public const string ProductRead = "PRODUCT_READ";
    public const string ProductWrite = "PRODUCT_WRITE";
    public const string PublicationRead = "PUBLICATION_READ";
    public const string PublicationWrite = "PUBLICATION_WRITE";
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

public sealed record AdapterContext(
    Guid TenantId,
    Guid ConnectionId,
    string CorrelationId,
    string IdempotencyKey,
    DateTimeOffset DeadlineUtc,
    bool IsStageCapabilityProbe = false,
    IntegrationOperation Operation = IntegrationOperation.Manual,
    bool IsOneTimeInvoiceDeliveryAuthorized = false,
    string? OneTimeInvoiceDeliveryOrderNumber = null,
    bool IsAutomaticInvoiceMarketplaceDelivery = false,
    bool IsExplicitManualInvoiceDelivery = false);
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
// UnlistedLinesUnconfirmed: the answer lists refused lines alone (Hepsiburada) but failed as a whole, so a line it
// leaves out was not shown to be applied.
public sealed record RemoteOperationStatus(string ExternalOperationId, string Status, IReadOnlyList<RemoteOperationLine> Lines, bool UnlistedLinesUnconfirmed = false);
public sealed record ProductPublication(Guid ProductId, string PayloadHash, string PayloadJson);
public sealed record ProductPublicationJobPayload(Guid JobId, Guid ProductId, Guid ProfileId, string Phase, string PayloadHash, string PayloadJson, string? ExternalOperationId, DateTimeOffset? SubmittedAt);
public sealed record ProductApprovalReconciliationJobPayload(Guid JobId, Guid ProductId, Guid ProfileId, string PayloadHash, DateTimeOffset StartedAt, DateTimeOffset DeadlineAt, string? ExternalOperationId = null);
public sealed record ProductUpdatePublication(Guid ProductId, string Mode, string PayloadHash, string UnapprovedPayloadJson, string ApprovedContentPayloadJson, string ApprovedVariantPayloadJson, string ApprovedDeliveryPayloadJson);
public sealed record ProductUpdateJobPayload(Guid JobId, Guid ProductId, Guid ProfileId, string Phase, string Mode, string PayloadHash, string UnapprovedPayloadJson, string ApprovedContentPayloadJson, string ApprovedVariantPayloadJson, string ApprovedDeliveryPayloadJson, string? ExternalOperationId, DateTimeOffset? SubmittedAt);
// Pazarama sale open/close by barcode; it travels in a PRODUCT_ARCHIVE job and is told apart by SaleStatusBarcodes.
public sealed record PazaramaSaleStatusJobPayload(Guid JobId, IReadOnlyList<string> SaleStatusBarcodes, bool Close, DateTimeOffset StartedAt);
public sealed record ProductArchiveJobPayload(Guid JobId, Guid ProductId, Guid ProfileId, bool Archived, string Phase, string PayloadHash, string PayloadJson, string? ExternalOperationId, DateTimeOffset StartedAt, DateTimeOffset DeadlineAt);
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
    IReadOnlyList<RemoteInventoryLevel>? InventoryLevels = null,
    // The marketplace's desi (volumetric weight) when it carries one; ImportedDesi drops 0 and nonsense.
    decimal? Desi = null,
    // The marketplace keeps and ships this variant's stock (n11depom, isFulfilledByHB): the stock is not the seller's to
    // send. Null when the marketplace answer does not say.
    bool? FulfilledByMarketplace = null);

// A desi read from a marketplace or an import file counts only when it is a real measurement: Trendyol
// answers 0 for a product whose desi was never entered.
public static class ImportedDesi
{
    public const decimal Maximum = 10000m;

    public static decimal? From(decimal? value) => value is > 0 and <= Maximum ? decimal.Round(value.Value, 2, MidpointRounding.AwayFromZero) : null;

    // A desi sent as text: "2,5", "2.5", "2,5 desi" or "3 kg". A single comma is the decimal separator
    // (Turkish), so "2,5" is 2.5 and not 25; with both marks the last one separates the decimals.
    public static decimal? FromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var value = text.Trim();
        foreach (var unit in new[] { "desi", "kg" })
        {
            if (!value.EndsWith(unit, StringComparison.OrdinalIgnoreCase)) continue;
            value = value[..^unit.Length].TrimEnd();
            break;
        }
        var commas = value.Count(character => character == ',');
        if (commas > 1) return null;
        if (commas == 1)
            value = value.Contains('.', StringComparison.Ordinal) && value.LastIndexOf('.') > value.LastIndexOf(',')
                ? value.Replace(",", "", StringComparison.Ordinal)
                : value.Replace(".", "", StringComparison.Ordinal).Replace(',', '.');
        return decimal.TryParse(value, System.Globalization.NumberStyles.AllowDecimalPoint, System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? From(parsed) : null;
    }

    // A desi field of a marketplace answer: a JSON number, or text read by FromText.
    public static decimal? FromJson(System.Text.Json.JsonElement value) => value.ValueKind switch
    {
        System.Text.Json.JsonValueKind.Number => value.TryGetDecimal(out var number) ? From(number) : null,
        System.Text.Json.JsonValueKind.String => FromText(value.GetString()),
        _ => null
    };
}
public sealed record RemotePublicationStatus(string Barcode, string Status, string? ExternalProductId, string? ExternalVariantId, string? RejectionCode, string RawJson);
public sealed record ProductReadFilter(DateTimeOffset? ModifiedAfter, string? Barcode = null, string? ProductMainId = null, string? ContentId = null, string? ProductUrl = null, bool IncludePendingApproval = false, bool CatalogStructure = false);
public sealed record PriceInventoryPushLine(Guid VariantId, Guid OfferId, string Barcode, decimal Quantity, decimal ListPrice, decimal SalePrice, string Currency, long ProjectionVersion, long PriceVersion, string PriceHash, string? MerchantSku = null, string? HepsiburadaSku = null, string? ExternalVariantId = null,
    // Fields left out of the request: the value equals the one the marketplace confirmed last (Trendyol), or the
    // marketplace keeps the stock itself (n11depom, Hepsiburada fulfilment). Both false is a full line.
    bool QuantityOmitted = false, bool PriceOmitted = false,
    // The offer's safety stock the quantity was computed with; null on lines queued before it was carried.
    decimal? SafetyStock = null);
// Automatic: the job was built by the system on its own (a stock or price change, the reconcile scan); its values are
// rebuilt the same way before the send (PriceInventoryComposer automatic mode). Trigger: why it was queued
// (PriceStockSendTriggers), recorded in the send log; null on jobs queued before it was carried.
public sealed record PriceInventoryJobPayload(Guid JobId, Guid ConnectionId, string Phase, string PayloadHash, string PayloadJson, IReadOnlyList<PriceInventoryPushLine> Lines, string? ExternalOperationId, DateTimeOffset? SubmittedAt, Guid? VariantId = null, Guid? ProductId = null, bool Manual = false, bool Automatic = false, string? Trigger = null);
public sealed record BatchResult<T>(IReadOnlyList<T> Lines, string? ExternalOperationId, bool IsPartial);
public sealed record OrderPollWindow(DateTimeOffset? ModifiedAfter, DateTimeOffset? ModifiedBefore, string? StoreFrontCode = null);
public sealed record RemoteOrderLine(string ExternalLineId, string Sku, string? Barcode, string Title, decimal Quantity, decimal UnitPrice, decimal? VatRate, string RawStatus, string SourceSnapshotJson = "{}", decimal CancelledQuantity = 0);
public sealed record RemotePackageAllocation(string ExternalLineId, decimal AllocatedQuantity, decimal CancelledQuantity, decimal ShippedQuantity, decimal DeliveredQuantity, decimal ReturnedQuantity);
public sealed record RemotePackageInvoiceObservation(
    string? RawStatus,
    string? InvoiceNumber,
    string? InvoiceUrl,
    DateTimeOffset? SourceUpdatedAt,
    bool ReferencesAreAuthoritative = false);

public static class MarketplaceInvoiceReconciliationCadencePolicy
{
    public static readonly TimeSpan UnresolvedInterval = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan RecentInterval = TimeSpan.FromHours(1);
    public static readonly TimeSpan OlderInterval = TimeSpan.FromHours(24);
    public static readonly TimeSpan RecentOrderWindow = TimeSpan.FromDays(90);

    public static TimeSpan IntervalFor(
        MarketplaceInvoiceStatus platformStatus,
        bool hasUnresolvedLocalInvoice,
        DateTimeOffset orderCreatedAt,
        DateTimeOffset now)
    {
        if (platformStatus != MarketplaceInvoiceStatus.Invoiced || hasUnresolvedLocalInvoice)
            return UnresolvedInterval;
        return now - orderCreatedAt < RecentOrderWindow ? RecentInterval : OlderInterval;
    }

    public static bool IsDue(
        MarketplaceInvoiceStatus platformStatus,
        bool hasUnresolvedLocalInvoice,
        DateTimeOffset orderCreatedAt,
        DateTimeOffset? lastSuccessfulCheckAt,
        DateTimeOffset now) =>
        lastSuccessfulCheckAt is null
        || now - lastSuccessfulCheckAt.Value >= IntervalFor(platformStatus, hasUnresolvedLocalInvoice, orderCreatedAt, now);
}
// Trendyol invoice reconciliation reads only what can still change: packages whose local
// invoice is not confirmed yet (every run) and the shipped or delivered packages of the last
// 90 days the platform still shows as uninvoiced (one sweep every six hours). New, picking
// and payment-pending packages receive their invoice state from the order stream.
public static class TrendyolInvoiceReconciliationPolicy
{
    public const string SweepCursorResource = "ORDER_INVOICE_SWEEP";
    public static readonly TimeSpan SweepInterval = TimeSpan.FromHours(6);
    public static readonly TimeSpan SweepOrderWindow = TimeSpan.FromDays(90);
    public static readonly ShipmentPackageStatus[] SweepStatuses =
    [
        ShipmentPackageStatus.ReadyToShip, ShipmentPackageStatus.Shipped, ShipmentPackageStatus.Delivered,
        ShipmentPackageStatus.Undelivered, ShipmentPackageStatus.ReturnInTransit, ShipmentPackageStatus.Returned,
        ShipmentPackageStatus.PartiallyCancelled
    ];

    // A sweep in progress (a saved position) always continues; otherwise one starts six hours after the last finished.
    public static bool SweepDue(string? sweepPosition, DateTimeOffset? lastSweepCompletedAt, DateTimeOffset now) =>
        !string.IsNullOrWhiteSpace(sweepPosition)
        || lastSweepCompletedAt is null
        || now - lastSweepCompletedAt.Value >= SweepInterval;
}
// StatusTimeUnknown: the marketplace gives no time for the status, so OccurredAt only names and orders the event; the
// package keeps the time the status was first seen instead (PTT AVM).
public sealed record RemotePackage(string ExternalPackageId, string? OriginExternalPackageId, string RawStatus, DateTimeOffset OccurredAt, string? CargoProviderExternalId, string? CargoTrackingNumber, IReadOnlyList<RemotePackageAllocation> Allocations, decimal GrossAmount = 0, decimal DiscountAmount = 0, decimal NetAmount = 0, RemotePackageInvoiceObservation? Invoice = null, string? CreatedBy = null, bool IsStatusObservation = false, bool StatusTimeUnknown = false);
public sealed record RemoteOrderRefund(string ExternalRefundId, DateTimeOffset OccurredAt, decimal Amount, string Currency, string RawJson);
public sealed record RemoteOrder(string ExternalOrderId, string OrderNumber, DateTimeOffset OrderedAt, DateTimeOffset LastModifiedAt, string Currency, decimal GrossAmount, decimal DiscountAmount, decimal NetAmount, string CustomerSnapshotJson, string ShipmentAddressSnapshotJson, string InvoiceAddressSnapshotJson, IReadOnlyList<RemoteOrderLine> Lines, IReadOnlyList<RemotePackage> Packages, string RawJson, DateTimeOffset? ShipmentDueAt = null, string PaymentStatus = "UNKNOWN", string CancellationStatus = "NOT_CANCELLED", string RefundStatus = "NOT_REFUNDED", decimal RefundedAmount = 0, IReadOnlyList<RemoteOrderRefund>? Refunds = null, string? LifecycleStatus = null, string? StoreFrontCode = null);
// The order as the panel last saw it: when it was placed, last changed and which storefront it belongs to.
public sealed record OrderLookupHint(DateTimeOffset? OrderedAt, DateTimeOffset? LastModifiedAt, string? StoreFrontCode);
// InvoiceOnly: the caller needs the package record itself, not its cargo fields, so the first match is final.
public sealed record PackageLookup(DateTimeOffset? StatusOccurredAt, OrderLookupHint? Order = null, bool InvoiceOnly = false);
// IncludeStatusLists: Hepsiburada's shipped, delivered and undelivered lists change rarely, so the minute-by-minute
// read can leave them out and take them every few minutes (HepsiburadaPackagePolicy).
public sealed record PackagePollWindow(DateTimeOffset? ModifiedAfter, DateTimeOffset? ModifiedBefore, bool IncludeStatusLists = true);
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
// StoreFrontCode: the stored storefront of the package's order (Trendyol sends it as the storeFrontCode header; null = TR).
public sealed record PackageActionCommand(string ExternalPackageId, string Action, string PayloadJson, string? StoreFrontCode = null);
public sealed record ShipmentActionJobPayload(Guid JobId, Guid PackageId, string Action, string PayloadJson);
public sealed record PackageActionResult(string ExternalPackageId, string Status, string? ExternalOperationId);
public sealed record RemoteCargoCompany(string ShortName, string Name);
public sealed record RemotePackageableLine(string LineItemId, int Quantity);
public sealed record PackageCreateLineRequest(Guid OrderLineId, int Quantity);
public sealed record RemotePackageCreateLineRequest(string LineItemId, int Quantity);
// Hepsiburada's package request: the lines, desi and parcel count; barcode, cargo company, carrier, reason and warehouse
// are optional and left out of the request when empty.
public sealed record OrderPackageCreateRequest(string? Barcode, string? CargoCompany, string? Carrier, string? CreationReason, int Deci, int ParcelQuantity, string? ShippingAddressLabel, string? ShippingModel, IReadOnlyList<PackageCreateLineRequest> LineItems);
public sealed record CreateOrderPackageCommand(string? Barcode, string? CargoCompany, string? Carrier, string? CreationReason, int Deci, int ParcelQuantity, string? ShippingAddressLabel, string? ShippingModel, IReadOnlyList<RemotePackageCreateLineRequest> LineItems);
public sealed record CreateOrderPackageResult(string PackageNumber);
public sealed record CommonLabelRequest(string CargoTrackingNumber, int BoxQuantity, decimal VolumetricHeight);
public sealed record CommonLabelDocument(string CargoTrackingNumber, string Format, byte[] Content);
public sealed record CommonLabelJobPayload(Guid JobId, Guid PackageId, string Phase, int BoxQuantity, decimal VolumetricHeight, DateTimeOffset StartedAt, DateTimeOffset DeadlineAt, string Format = "ZPL");
public sealed record CapabilityProbeJobPayload(Guid JobId, Guid PackageId, Guid ActorUserId, string CapabilityCode, int BoxQuantity, decimal VolumetricHeight, DateTimeOffset StartedAt, DateTimeOffset DeadlineAt);
// HepsiburadaSku is set only on a STAGE Hepsiburada connection without a LIVE listing: the SIT order is
// then created directly from an imported hbSku link instead of a listing barcode.
public sealed record StageTestOrderJobPayload(Guid JobId, Guid ActorUserId, string Barcode, DateTimeOffset StartedAt, string? HepsiburadaSku = null);
public sealed record StageTestOrderResult(string OrderNumber);
public sealed record ReturnPollWindow(
    DateTimeOffset? ModifiedAfter,
    DateTimeOffset? ModifiedBefore,
    string? StoreFrontCode = null,
    string? Status = null,
    DateTimeOffset? StatusModifiedAfter = null,
    DateTimeOffset? StatusModifiedBefore = null);
// AcceptDetail, Note, Resolved and AcceptedBySeller: how Trendyol settled the line (getClaims claimItems); null when the
// marketplace does not say (unknown stays unknown).
public sealed record RemoteReturnLine(string ExternalLineId, string ExternalOrderLineId, decimal Quantity, IReadOnlyList<string>? AlternateExternalOrderLineIds = null, string? RawStatus = null, string? AcceptDetail = null, string? Note = null, bool? Resolved = null, bool? AcceptedBySeller = null);
// One status change of a return line as the marketplace keeps it (Trendyol getClaimItemAudits).
public sealed record RemoteReturnLineAudit(string? PreviousStatus, string NewStatus, string? ExecutorApp, string? ExecutorUser, DateTimeOffset At);
// ActionDueAtEstimated: the marketplace sent no deadline and ActionDueAt follows its usual rule.
public sealed record RemoteReturnClaim(string ExternalClaimId, string ExternalOrderId, string RawStatus, string? ReasonCode, string? ReasonText, DateTimeOffset? ActionDueAt, DateTimeOffset LastModifiedAt, IReadOnlyList<RemoteReturnLine> Lines, string RawJson, string? CargoProviderName = null, string? CargoTrackingNumber = null, string? CargoTrackingLink = null, DateTimeOffset? OrderCreatedAt = null, bool ActionDueAtEstimated = false);
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

// Order finance (commission, allowance, transfer date): read-only, asked for one order at a time.
public sealed record RemoteFinanceTransaction(string TransactionCode, string Status, decimal Amount, decimal Commission, decimal Allowance, decimal CouponDiscount, decimal IntegrationAmount, decimal CargoDebt, decimal Stoppage, int Installments, DateTimeOffset? TransactionDate, DateTimeOffset? TransferredDate);
public sealed record RemoteCommissionInvoice(string OrderNumber, string? InvoiceNumber, DateTimeOffset? InvoiceDate, decimal VatAmount, string? OperationStatus);
public sealed record RemoteOrderFinance(IReadOnlyList<RemoteFinanceTransaction> Transactions, IReadOnlyList<RemoteCommissionInvoice> CommissionInvoices);

public interface IOrderFinancePort
{
    Task<AdapterResult<RemoteOrderFinance>> GetOrderFinanceAsync(AdapterContext context, string orderNumber, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken);
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

// A marketplace that answers whether it holds one code (PTT AVM's BarkodKontrol), for the link verification. True or
// false only from a well-formed answer; null when the answer could not tell. A refused request is a failure, never false.
public interface IListingCodeCheckPort
{
    Task<AdapterResult<bool?>> HoldsCodeAsync(AdapterContext context, string code, CancellationToken cancellationToken);
}

// A marketplace that keeps a model family as one product under the parent barcode (PTT AVM) answers its variants'
// publication status through that product: one status per barcode asked, under the barcode as asked.
public interface IProductFamilyStatusPort
{
    Task<AdapterResult<IReadOnlyList<RemotePublicationStatus>>> GetFamilyPublicationStatusAsync(AdapterContext context, string parentBarcode, IReadOnlyList<FamilyVariantQuery> variants, CancellationToken cancellationToken);
}

// A marketplace whose own product lists carry the approval state (Pazarama's approved and unapproved lists) answers
// many products' publication status from one walk of those lists instead of one lookup per barcode. The walk stops
// after maxRequests list requests; Complete says it reached the end of both lists. A barcode it did not list is not
// in the snapshot (unknown, never "missing"): the caller asks for it on its own.
public interface IProductApprovalSnapshotPort
{
    Task<AdapterResult<ProductApprovalSnapshot>> ReadApprovalSnapshotAsync(AdapterContext context, int maxRequests, CancellationToken cancellationToken);
}

public sealed record ProductApprovalSnapshot(IReadOnlyDictionary<string, RemotePublicationStatus> ByBarcode, bool Complete, int Requests);

// A family variant asked about: its barcode and the colour and size it was sent with (null when not known), and whether
// the send's tracking result accepted it (a succeeded product job of the publication carried it; the card did not refuse it).
public sealed record FamilyVariantQuery(string Barcode, string? Color, string? Size, bool SendAccepted = false);

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
    // What the panel already knows about an order lets an adapter ask the right place first
    // (its storefront, a date window around it) instead of searching every source.
    Task<AdapterResult<RemoteOrder>> GetAsync(AdapterContext context, string externalOrderId, OrderLookupHint? hint, CancellationToken cancellationToken) =>
        GetAsync(context, externalOrderId, cancellationToken);
    Task<AdapterResult<RemoteOrderPackage>> GetShipmentPackageAsync(AdapterContext context, string externalPackageId, PackageLookup lookup, CancellationToken cancellationToken) =>
        GetShipmentPackageAsync(context, externalPackageId, lookup.StatusOccurredAt, cancellationToken);
    // Packages of known orders that share a storefront and date window, read in one request
    // where the marketplace allows it. A package the marketplace did not return is left out.
    Task<AdapterResult<IReadOnlyList<RemoteOrder>>> GetShipmentPackagesAsync(AdapterContext context, IReadOnlyList<string> externalPackageIds, OrderLookupHint? hint, CancellationToken cancellationToken) =>
        Task.FromResult(AdapterResult<IReadOnlyList<RemoteOrder>>.Failure(new(AdapterErrorClass.NotSupported, "ORDER_PACKAGE_BATCH_READ_UNSUPPORTED", "Bu pazaryerinde toplu paket okuması desteklenmiyor.", null, null, null)));
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

// Reads a return line's status history from the marketplace (Trendyol only); nothing is written.
public interface IReturnAuditPort
{
    Task<AdapterResult<IReadOnlyList<RemoteReturnLineAudit>>> ReturnLineAuditsAsync(AdapterContext context, string externalLineId, CancellationToken cancellationToken);
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
    DateTimeOffset LastModifiedAt, IReadOnlyList<RemoteQuestionConversation> Conversations, string? ProductUrl = null, string? CustomerKey = null);
public sealed record MarketplaceQuestionPage(IReadOnlyList<RemoteMarketplaceQuestion> Items, int Page, int TotalPages, long TotalElements);
public sealed record RemoteQuestionAnswerResult(bool Accepted, bool IsVerified, string? AnswerId);

// Reporting a customer question to the marketplace instead of answering it (advertising, insults, no question).
// Each marketplace says whether it takes a report over its API, which reasons the panel offers, in which states
// and what the question becomes afterwards.
public sealed record QuestionReportReason(string Code, string Label, bool NoteRequired = false);
public sealed record QuestionReportCapability(
    string PlatformCode, bool Supported, string? UnsupportedReason, IReadOnlyList<QuestionReportReason> Reasons,
    IReadOnlyList<string> AllowedStatuses, string? ResultStatus, int NoteMinLength, int NoteMaxLength, string? DocumentationUrl)
{
    public static QuestionReportCapability Unsupported(string platformCode, string reason, string? documentationUrl) =>
        new(platformCode, false, reason, [], [], null, 0, 0, documentationUrl);
}
public sealed record RemoteQuestionReport(string ReasonCode, string ReasonText, string? Note);
public sealed record RemoteQuestionReportResult(bool Accepted);
public sealed record ReportQuestionCommand(long Version, string? ReasonCode, string? Note);

public interface IQuestionPort
{
    Task<AdapterResult<MarketplaceQuestionPage>> ListQuestionsAsync(AdapterContext context, QuestionPollRequest request, CancellationToken cancellationToken);
    Task<AdapterResult<RemoteMarketplaceQuestion>> GetQuestionAsync(AdapterContext context, string questionId, string kind, CancellationToken cancellationToken);
    Task<AdapterResult<RemoteQuestionAnswerResult>> AnswerQuestionAsync(AdapterContext context, string questionId, string kind, string answer, CancellationToken cancellationToken);
    QuestionReportCapability ReportCapability(string platformCode);
    Task<AdapterResult<RemoteQuestionReportResult>> ReportQuestionAsync(AdapterContext context, string questionId, string kind, RemoteQuestionReport report, CancellationToken cancellationToken);
}

public sealed record MarketplaceQuestionView(Guid Id, Guid ConnectionId, string PlatformCode, string StoreName, string ExternalQuestionId, string Kind, string Status, string QuestionText, string? ProductName, string? ProductImageUrl, string? ProductSku, string? ProductBarcode, string? ProductModelCode, string? CustomerName, string? ExternalOrderNumber, IReadOnlyList<RemoteQuestionConversation> Conversations, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt, DateTimeOffset LastRemoteModifiedAt, DateTimeOffset LastSyncedAt, long Version, string? ProductUrl = null, string? AnswerSubmissionStatus = null, string? PendingAnswerText = null);
public sealed record MarketplaceQuestionTemplateView(Guid Id, string Title, string Text, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, long Version);
public sealed record SaveQuestionTemplateCommand(string Title, string Text);
public sealed record AnswerQuestionCommand(string Text, long Version);
public sealed record QuestionSyncView(int Added, int Updated, DateTimeOffset SyncedAt, string? Error = null, AdapterError? Failure = null);
public sealed record MarketplaceQuestionSyncStateView(Guid ConnectionId, string PlatformCode, string StoreName, bool HistoryImported, DateTimeOffset? HistoryStartedAt, string ProgressStatus, int ImportedCount, DateTimeOffset? LastRunStartedAt, DateTimeOffset? LastSuccessAt, string? LastError, int ProductQuestionCount = 0, int OrderQuestionCount = 0);
public sealed record QuestionListQuery(string Kind, string? Status, string? PlatformCode, Guid? ConnectionId, DateTimeOffset? DateFrom, DateTimeOffset? DateTo, string? Search, int Page = 1, int Limit = 50, string Sort = "NEWEST");
public sealed record MarketplaceQuestionListPage(IReadOnlyList<MarketplaceQuestionView> Items, int Page, int Limit, int TotalCount);

public interface IMarketplaceQuestionService
{
    Task<MarketplaceQuestionListPage> ListAsync(Guid tenantId, QuestionListQuery query, CancellationToken cancellationToken);
    Task<MarketplaceQuestionView?> GetAsync(Guid tenantId, Guid id, CancellationToken cancellationToken);
    Task<IReadOnlyList<MarketplaceQuestionView>?> CustomerHistoryAsync(Guid tenantId, Guid id, CancellationToken cancellationToken);
    Task<QuestionSyncView> SyncAsync(Guid tenantId, string? kind, CancellationToken cancellationToken);
    Task<QuestionSyncView> SyncConnectionAsync(Guid tenantId, Guid connectionId, string? kind, CancellationToken cancellationToken);
    Task<IReadOnlyList<MarketplaceQuestionSyncStateView>> SyncStatesAsync(Guid tenantId, CancellationToken cancellationToken);
    Task<ServiceResult<MarketplaceQuestionView>> AnswerAsync(Guid tenantId, Guid userId, Guid id, long expectedVersion, string answer, string idempotencyKey, string correlationId, CancellationToken cancellationToken);
    QuestionReportCapability ReportCapability(string platformCode);
    Task<ServiceResult<MarketplaceQuestionView>> ReportAsync(Guid tenantId, Guid userId, Guid id, ReportQuestionCommand command, string idempotencyKey, string correlationId, CancellationToken cancellationToken);
    Task<IReadOnlyList<MarketplaceQuestionTemplateView>> TemplatesAsync(Guid tenantId, CancellationToken cancellationToken);
    Task<ServiceResult<MarketplaceQuestionTemplateView>> SaveTemplateAsync(Guid tenantId, Guid? id, long? expectedVersion, SaveQuestionTemplateCommand command, CancellationToken cancellationToken);
    Task<ServiceResult<bool>> DeleteTemplateAsync(Guid tenantId, Guid id, long expectedVersion, CancellationToken cancellationToken);
}

public interface IWebhookVerifier
{
    ValueTask<AdapterResult<VerifiedWebhookEnvelope>> VerifyAsync(ReadOnlyMemory<byte> rawBody, IReadOnlyDictionary<string, string> headers, Guid connectionId, Guid subscriptionId, CancellationToken cancellationToken);
}

// LastErrorCode is the last refused test or sync read (cleared by the next successful test); LastErrorAt is when the
// last job attempt failed with that code (null when no such attempt is found). DisabledAt is when it was last disabled.
public sealed record ConnectionView(Guid Id, Guid PublicId, string PlatformCode, string Environment, string DisplayName, string ExternalStoreId, string Status, string ApiVersion, DateTimeOffset? LastTestedAt, DateTimeOffset? LastSuccessAt, string? LastErrorCode, bool HasCredential, bool ExternalWritesEnabled, long Version, bool InvoiceCreationEnabled = true, string? N11ShipmentTemplate = null, int? N11PreparingDay = null, DateTimeOffset? DisabledAt = null, DateTimeOffset? LastErrorAt = null);
public sealed record CapabilityView(string Code, string SupportLevel, string ApiVersion, string Environment, string StoreScope, string? SourceUrl, DateTimeOffset? VerifiedAt, string? ConstraintsJson, string? EvidenceNote, long Version, bool VerifiedForConnection = false);
public sealed record RecordCapabilityEvidenceCommand(string SupportLevel, string SourceUrl, string SourceVersion, string Environment, string StoreScope, string EvidenceNote, string? FixtureChecksum, string? ConstraintsJson, DateTimeOffset VerifiedAt);
public sealed record CreateConnectionCommand(string DisplayName, string Environment, string ExternalStoreId, string ApiVersion, string? UserAgentIdentity, string? PlatformCode = null, string? ShopifyAccessToken = null, string? HepsiburadaServiceKey = null, string? HepsiburadaIntegratorUsername = null, string? N11AppKey = null, string? N11AppSecret = null, string? PazaramaClientId = null, string? PazaramaClientSecret = null, string? PttAvmUsername = null, string? PttAvmPassword = null);
// N11ShipmentTemplate and N11PreparingDay are the n11 product create settings; null keeps the
// saved value and an empty template clears it.
public sealed record UpdateConnectionCommand(string DisplayName, string? UserAgentIdentity, string? Environment = null, string? ExternalStoreId = null, bool? ExternalWritesEnabled = null, bool? InvoiceCreationEnabled = null, string? N11ShipmentTemplate = null, int? N11PreparingDay = null);
public sealed record CredentialCommand(
    string? ApiKey,
    string? ApiSecret,
    string? Email = null,
    string? Password = null,
    string? ShopifyAccessToken = null,
    string? HepsiburadaServiceKey = null,
    string? HepsiburadaIntegratorUsername = null,
    string? PttAvmUsername = null,
    string? PttAvmPassword = null);
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
// ResetToDefault puts the application default cadence back (the given cadence is ignored) and lets later
// default changes reach the policy again.
public sealed record UpdateSyncPolicyCommand(int IntervalSeconds, int OverlapSeconds, int JitterSeconds, bool Enabled, bool ResetToDefault = false);
public sealed record WebhookSubscriptionView(Guid Id, string AuthenticationType, string Status, string? ExternalSubscriptionId, DateTimeOffset? VerifiedAt, DateTimeOffset? LastReceivedAt, long Version);
public sealed record CreateWebhookSubscriptionCommand(string AuthenticationType, string? Username, string? Password, string? ApiKey);

// A webhook as the marketplace lists it. Status: ACTIVE or PASSIVE (Trendyol passivates a
// webhook whose deliveries keep failing).
// Topic is the event a Shopify subscription delivers (Trendyol lists one webhook for all events).
public sealed record RemoteWebhook(string Id, string? Url, string Status, string? Username, string? Topic = null);

// Registers the panel's order-notification endpoint at a marketplace. Called only when a user
// turns notifications on or off, and by the worker to reactivate a webhook the marketplace passivated.
public interface IWebhookRegistrationPort
{
    Task<AdapterResult<IReadOnlyList<RemoteWebhook>>> ListWebhooksAsync(AdapterContext context, CancellationToken cancellationToken);
    Task<AdapterResult<string>> CreateWebhookAsync(AdapterContext context, string url, string username, string password, CancellationToken cancellationToken);
    Task<AdapterResult<bool>> DeleteWebhookAsync(AdapterContext context, string externalId, CancellationToken cancellationToken);
    Task<AdapterResult<bool>> ActivateWebhookAsync(AdapterContext context, string externalId, CancellationToken cancellationToken);

    // Recreates the parts of a registration the marketplace removed (Shopify drops a subscription
    // after repeated failed deliveries). Returns the new external id, or null when nothing changed.
    Task<AdapterResult<string?>> RestoreMissingWebhooksAsync(AdapterContext context, string externalId, CancellationToken cancellationToken) =>
        Task.FromResult(AdapterResult<string?>.Success(null));
}

// State: UNSUPPORTED, OFF, ACTIVE, PASSIVE (the marketplace stopped delivering), MISSING (the
// marketplace no longer has it), or UNKNOWN (the marketplace could not be asked).
// PreviousPreparedAt: a closed Hepsiburada definition that can be made the active one again (when it was
// prepared), shown while the current one has received nothing. AdoptableFrom: an active Hepsiburada definition of an
// inactive Hepsiburada connection (a test store) that can be moved here with its address and password, shown likewise.
public sealed record InstantNotificationView(
    string State,
    string? Endpoint,
    DateTimeOffset? RegisteredAt,
    DateTimeOffset? LastReceivedAt,
    int ReceivedLast24Hours,
    string? Message,
    WebhookSetupInstructions? Setup = null,
    DateTimeOffset? LastRejectedAt = null,
    string? LastRejectedReason = null,
    DateTimeOffset? PreviousPreparedAt = null,
    WebhookAdoptableDefinition? AdoptableFrom = null);

public sealed record WebhookAdoptableDefinition(string ConnectionName, string Environment, DateTimeOffset PreparedAt);

// What the seller sends a marketplace that registers webhooks by hand (Hepsiburada): the base
// address with its secret route, the Basic Auth user and password, and the paths it calls.
// Shown once, when prepared; the panel keeps only hashes and protected copies.
public sealed record WebhookSetupInstructions(string Environment, string BaseUrl, string Username, string Password, IReadOnlyList<string> Paths);

public interface IInstantNotificationService
{
    Task<ServiceResult<InstantNotificationView>> StatusAsync(Guid tenantId, Guid connectionId, string correlationId, CancellationToken cancellationToken);
    // clientSecret: the Shopify app's client secret, with which Shopify signs its notifications.
    Task<ServiceResult<InstantNotificationView>> EnableAsync(Guid tenantId, Guid connectionId, string correlationId, CancellationToken cancellationToken, string? clientSecret = null);
    Task<ServiceResult<InstantNotificationView>> DisableAsync(Guid tenantId, Guid connectionId, string correlationId, CancellationToken cancellationToken);
    // Hepsiburada: makes the last closed definition (the address and password Hepsiburada was given) the active one
    // again, closing a newer one that has received nothing.
    Task<ServiceResult<InstantNotificationView>> RestorePreviousAsync(Guid tenantId, Guid connectionId, string correlationId, CancellationToken cancellationToken);
    // Hepsiburada: moves the active definition of an inactive Hepsiburada connection here, keeping the address, user and
    // password Hepsiburada was given; the current definition is closed and can be restored.
    Task<ServiceResult<InstantNotificationView>> AdoptFromInactiveAsync(Guid tenantId, Guid connectionId, string correlationId, CancellationToken cancellationToken);
    // Reactivates registered webhooks the marketplace passivated (for example after the server was off).
    Task<int> ReactivatePassiveAsync(CancellationToken cancellationToken);
}
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
    bool ConnectionDeleted = false,
    // A connection reset keeps the issued invoices (fiscal records; deleting them would let the orders be invoiced
    // again) and the orders and returns they belong to, and says how many it kept.
    int KeptIssuedInvoices = 0,
    int CommissionTariffs = 0,
    int PromotionOffers = 0);

// What a reset would remove, scope by scope, shown before it runs; KeptIssuedInvoices is what an order, return or
// invoice reset of the connection keeps.
public sealed record DataResetCountView(string Scope, int Count);
public sealed record DataResetCountsView(IReadOnlyList<DataResetCountView> Items, int KeptIssuedInvoices);

// A connection's full reset: what it removes (issued invoices included), the linked products it keeps (used by another
// connection or created on the panel; only their link to this connection goes) and the reset job when one is queued or
// running.
public sealed record FullResetConnectionCommand(string? Confirmation);
public sealed record ConnectionFullResetJobView(Guid JobId, string Status, string? ProgressLabel, int? ProgressPercent, DateTimeOffset CreatedAt);
public sealed record ConnectionFullResetView(IReadOnlyList<DataResetCountView> Items, int KeptProducts, ConnectionFullResetJobView? Job);

public interface IOperationalDataMaintenanceService
{
    Task<ServiceResult<ConnectionFullResetView>> FullResetAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken);
    Task<ServiceResult<ConnectionFullResetView>> RequestFullResetAsync(Guid tenantId, Guid actorUserId, Guid connectionId, long expectedVersion, FullResetConnectionCommand command, string correlationId, CancellationToken cancellationToken);
    // The reset job's work (ConnectionFullResetJobHandler); jobId is the running job, which stays.
    Task<JobOutcome> RunFullResetAsync(Guid tenantId, Guid connectionId, Guid? jobId, CancellationToken cancellationToken);
    Task<ServiceResult<DataResetCountsView>> ResetCountsAsync(Guid tenantId, Guid? connectionId, CancellationToken cancellationToken);
    Task<ServiceResult<OperationalDataResetView>> DeleteConnectionAsync(Guid tenantId, Guid actorUserId, Guid connectionId, long expectedVersion, DeleteConnectionCommand command, string correlationId, CancellationToken cancellationToken);
    Task<ServiceResult<OperationalDataResetView>> ResetConnectionDataAsync(Guid tenantId, Guid actorUserId, Guid connectionId, long expectedVersion, ResetOperationalDataCommand command, string correlationId, CancellationToken cancellationToken);
    Task<ServiceResult<bool>> SetDataVisibilityAsync(Guid tenantId, Guid actorUserId, Guid connectionId, long expectedVersion, bool hidden, string correlationId, CancellationToken cancellationToken);
    Task<ServiceResult<OperationalDataResetView>> ResetAsync(Guid tenantId, Guid actorUserId, ResetOperationalDataCommand command, string correlationId, CancellationToken cancellationToken);
}

// G: the automatic price/stock pipeline of each marketplace connection, as the monitor panel shows it.
public sealed record PriceStockPushFailureView(Guid JobId, string? ErrorCode, string? ErrorSummary, string Status, DateTimeOffset? At);
public sealed record PriceStockOfferIssueView(string Code, string Summary, DateTimeOffset LastSeenAt);
public sealed record PriceStockConnectionMonitorView(
    Guid ConnectionId,
    string PlatformCode,
    string DisplayName,
    string Status,
    bool MasterSwitchOn,
    bool PriceWriteEnabled,
    bool StockWriteEnabled,
    bool AutomaticWritesOpen,
    DateTimeOffset? LastSuccessfulPushAt,
    int QueuedJobs,
    int WaitingForOrderRead,
    int FailedPushes7d,
    int ManualReviewPushes,
    int InvalidOffers,
    int NotFoundLinks,
    int RejectedLinks,
    PriceStockPushFailureView? LatestFailure,
    IReadOnlyList<PriceStockOfferIssueView> InvalidOfferSamples,
    // Where new orders come from (WebhookPollingPolicy.OrderSource*), the last notification received and the cadence
    // the scheduled order read actually runs at (null when the connection's order read is off).
    string OrderSource,
    DateTimeOffset? LastNotificationAt,
    int? OrderReadIntervalSeconds);
public sealed record PriceStockMonitorView(DateTimeOffset GeneratedAt, IReadOnlyList<PriceStockConnectionMonitorView> Connections);
public sealed record PriceStockEmergencyStopView(int ConnectionsStopped, int PoliciesDisabled, int JobsCancelled);

public interface IPriceStockMonitorService
{
    Task<PriceStockMonitorView> MonitorAsync(Guid tenantId, CancellationToken cancellationToken);
    // "Tüm Otomatik Yazmaları Durdur": every connection's PRICE_WRITE and STOCK_WRITE off and every queued price/stock
    // job cancelled, in one save. Switching them on again takes a new baseline (decision B).
    Task<PriceStockEmergencyStopView> StopAllAutomaticWritesAsync(Guid tenantId, Guid actorUserId, string correlationId, CancellationToken cancellationToken);
}

public interface IMarketplaceConnectionService
{
    Task<PageResult<ConnectionView>> ListAsync(Guid tenantId, int limit, string? after, CancellationToken cancellationToken);
    Task<ServiceResult<ConnectionView>> CreateAsync(Guid tenantId, CreateConnectionCommand command, CancellationToken cancellationToken);
    Task<ServiceResult<ConnectionView>> GetAsync(Guid tenantId, Guid id, CancellationToken cancellationToken);
    // elevated: the caller is an owner or administrator. Only they may change whether the panel writes to the marketplace or
    // creates invoices; other edits stay open to operations (G5). Nobody moves a connection to another environment or
    // store: that is a new connection (CONNECTION_SCOPE_IMMUTABLE).
    Task<ServiceResult<ConnectionView>> UpdateAsync(Guid tenantId, Guid id, long expectedVersion, UpdateConnectionCommand command, CancellationToken cancellationToken, bool elevated = true);
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
    Task<ServiceResult<WebhookSubscriptionView>> RevokeWebhookAsync(Guid tenantId, Guid id, Guid subscriptionId, CancellationToken cancellationToken);
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
    // No e-mail here: the shipping label reads it from the order detail when it prints.
    // Masked (***1234): the full identity or tax number is only in the order detail.
    string? CustomerTaxOrIdentityNumber = null,
    // The delivery address fields a shipping label needs; the raw address
    // snapshots (phones, identity numbers) are only in the order detail.
    OrderAddressView? ShipmentAddress = null,
    decimal GrossAmount = 0,
    decimal DiscountAmount = 0,
    IReadOnlyList<OrderLineView>? Lines = null,
    IReadOnlyList<ShipmentView>? Packages = null,
    Guid? InvoiceId = null,
    string? InvoiceDocumentUrl = null,
    IReadOnlyList<OrderInfoItem>? PlatformInfo = null,
    // When the order first reached the carrier (its first "shipped" status); null when not seen.
    DateTimeOffset? ShippedAt = null,
    // When a shipping label of the order was first printed from the panel (any computer).
    DateTimeOffset? LabelPrintedAt = null);
public sealed record OrderAddressView(
    string? FullName,
    string? FullAddress,
    string? Address1,
    string? Address2,
    string? Neighborhood,
    string? District,
    string? City,
    string? PostalCode,
    string? CountryCode,
    bool IsDeliveryPoint = false,
    string? DeliveryPointType = null);
// A label/value fact a marketplace adds to the order detail (n11 commission, tracking link, ...).
public sealed record OrderInfoItem(string Label, string Value, string? Url = null);
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
    IReadOnlyList<string>? Platforms = null,
    bool? LabelPrinted = null);
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
    int Unverified = 0,
    int PaymentPending = 0,
    int Undelivered = 0);
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
    decimal AllocatedQuantity = 0,
    // Return lists only: the returned quantity's amounts as the marketplace wrote them (ReturnMarketplaceAmounts).
    ReturnAmountsView? ReturnAmounts = null,
    // The catalog's photo of the line's variant when ImageUrl is the marketplace's own address; shown if that one fails to load.
    string? FallbackImageUrl = null);
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
    string? InvoiceDocumentUrl = null,
    // The delivery address in the list's shape, for the shipping label printed from the detail.
    OrderAddressView? ShipmentAddress = null);
public sealed record ShipmentDetailView(ShipmentView Package, IReadOnlyList<string> AllowedActions, IReadOnlyList<string> SupportedLabelFormats, bool IsStageConnection, IReadOnlyList<ShipmentDocumentView> Documents, string PlatformCode = "TRENDYOL");
public sealed record ShipmentDocumentView(Guid Id, string DocumentKind, string Format, string Source, int DocumentVersion, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt);
public sealed record ShipmentActionCommand(string Action, string PayloadJson);
public sealed record ShopifyOrderStatusResult(Guid Id, string Status, long Version);
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
    string? ModelCode = null,
    string? Status = null,
    // The returned quantity's amounts as the marketplace wrote them; null when it wrote none.
    ReturnAmountsView? ReturnAmounts = null,
    // Trendyol's settlement of the line (RemoteReturnLine); null when the marketplace has not said.
    string? AcceptDetail = null,
    string? MarketplaceNote = null,
    bool? Resolved = null,
    bool? AcceptedBySeller = null,
    // A sent decision whose outcome is still open covers this line; the claim's other lines stay free.
    bool DecisionPending = false,
    // Stock can be decided on this line: the line, or the claim when the line has no status of its own, is approved.
    bool StockDispositionAvailable = false);
// A return's amounts as the marketplace wrote them: sale (before discount), discount, invoiced, and the refund itself
// where the marketplace states one (Hepsiburada). A null figure is unknown, never calculated from the order.
public sealed record ReturnAmountsView(decimal? SaleAmount, decimal? DiscountAmount, decimal? InvoicedAmount, decimal? RefundAmount);
public sealed record ReturnAuditEntryView(string? PreviousStatus, string NewStatus, string? ExecutorApp, string? ExecutorUser, DateTimeOffset At);
// A return line's status history read live from the marketplace; Error when that read failed for this line.
public sealed record ReturnLineAuditView(Guid ReturnLineId, string Title, IReadOnlyList<ReturnAuditEntryView> Entries, string? Error);

public interface IReturnAuditReader
{
    Task<ServiceResult<IReadOnlyList<ReturnLineAuditView>>> ReturnAuditsAsync(Guid tenantId, Guid returnId, string correlationId, CancellationToken cancellationToken);
}
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
    string? CargoTrackingLink = null,
    IReadOnlyDictionary<Guid, string>? LineStatuses = null,
    bool ActionDueAtEstimated = false,
    // The return's amounts as the marketplace wrote them for its lines, in Currency; a figure a line lacks stays null.
    ReturnAmountsView? ReturnAmounts = null,
    // Set when a claim is shown as more than one card (its accepted lines apart from the waiting ones).
    string? RowKey = null);
public sealed record ReturnListQuery(
    string? Status = null,
    string? Customer = null,
    string? OrderNumber = null,
    string? ClaimCode = null,
    string? Barcode = null,
    string? Reason = null,
    DateTimeOffset? DateFrom = null,
    DateTimeOffset? DateTo = null,
    string? Search = null,
    // The Returns page's own filters, applied to the cards the way the page applied them before:
    // platform codes, the trimmed cargo provider name, the reason key (code, else text) and the
    // invoice label. Sort orders the approved tab (APPROVED_DESC/ASC, ORDERED_DESC/ASC).
    IReadOnlyList<string>? Platforms = null,
    string? Cargo = null,
    string? ReasonKey = null,
    string? InvoiceStatus = null,
    string? Sort = null);
// Tab counts (every filter but the tab applied) and the filter choices of the current tab (search
// and dates applied, the platform/cargo/reason/invoice filters not), as the Returns page showed them.
public sealed record ReturnSummaryView(
    IReadOnlyDictionary<string, int> TabCounts,
    IReadOnlyList<ReturnPlatformOption> Platforms,
    IReadOnlyList<string> CargoProviders,
    IReadOnlyList<ReturnReasonOption> Reasons,
    IReadOnlyList<string> InvoiceStatuses,
    // Cards of the current tab before the platform/cargo/reason/invoice filters.
    int UnfilteredCount);
public sealed record ReturnPlatformOption(string PlatformCode, string DisplayName);
// Value is the filter key (reason code, else text); the page labels it from code and text.
public sealed record ReturnReasonOption(string Value, string? ReasonCode, string? ReasonText);
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
    string? CargoTrackingLink = null,
    bool ActionDueAtEstimated = false,
    // The return's amounts as the marketplace wrote them for its lines, in Currency; a figure a line lacks stays null.
    ReturnAmountsView? ReturnAmounts = null);
public sealed record ReturnDecisionCommand(string Action, string? ReasonCode, string? Explanation, IReadOnlyList<Guid>? EvidenceAssetIds, IReadOnlyList<Guid>? ReturnLineIds = null, string? FinalizedWith = null, bool AddToStock = false, IReadOnlyList<Guid>? StockLineIds = null, IReadOnlyList<ReturnStockLineCommand>? StockLines = null);
public sealed record ReturnStockLineCommand(Guid ReturnLineId, decimal Quantity);
public sealed record ReturnDispositionCommand(Guid ReturnLineId, string Disposition, decimal Quantity, string Reason);

public interface IMarketplaceSalesService
{
    Task<PageResult<OrderListView>> OrdersAsync(Guid tenantId, int limit, int page, string? after, OrderListQuery query, CancellationToken cancellationToken);
    Task<bool> MarkLabelPrintedAsync(Guid tenantId, Guid orderId, CancellationToken cancellationToken);
    // tabs: only these counters are computed (the others answer 0); null computes every tab. allowCached lets a
    // polling caller share a recent identical answer instead of running the count again.
    Task<OrderSummaryView> OrderSummaryAsync(Guid tenantId, OrderListQuery query, CancellationToken cancellationToken, IReadOnlyCollection<string>? tabs = null, bool allowCached = false);
    Task<ServiceResult<string>> ProductImageAsync(Guid tenantId, string? barcode, string correlationId, CancellationToken cancellationToken, Guid? connectionId = null, string? productName = null);
    Task<ServiceResult<OrderDetailView>> OrderAsync(Guid tenantId, Guid id, CancellationToken cancellationToken);
    Task<PageResult<ShipmentView>> ShipmentsAsync(Guid tenantId, int limit, string? after, string? status, CancellationToken cancellationToken);
    Task<ServiceResult<ShipmentDetailView>> ShipmentAsync(Guid tenantId, Guid id, CancellationToken cancellationToken);
    Task<ServiceResult<ShopifyOrderStatusResult>> SetShopifyOrderStatusAsync(Guid tenantId, Guid userId, Guid orderId, long expectedVersion, string? status, string idempotencyKey, string correlationId, CancellationToken cancellationToken);
    Task<ServiceResult<IReadOnlyList<RemoteCargoCompany>>> ChangeableCargoCompaniesAsync(Guid tenantId, Guid packageId, string correlationId, CancellationToken cancellationToken);
    Task<ServiceResult<CreateOrderPackageResult>> CreateOrderPackageInstantAsync(Guid tenantId, Guid orderId, long expectedVersion, OrderPackageCreateRequest command, string idempotencyKey, string correlationId, CancellationToken cancellationToken);
    Task<ServiceResult<Guid>> EnqueueOrderSyncAsync(Guid tenantId, Guid connectionId, string? externalOrderId, bool full, string correlationId, CancellationToken cancellationToken, string? packageNumber = null);
    Task<ServiceResult<Guid>> EnqueueReferenceSyncAsync(Guid tenantId, Guid connectionId, string resourceType, string? parentExternalId, string correlationId, CancellationToken cancellationToken);
    Task<ServiceResult<CatalogStructureRunView>> EnqueueCatalogStructureAsync(Guid tenantId, Guid connectionId, string mode, string correlationId, CancellationToken cancellationToken);
    Task<ServiceResult<CatalogStructureRunView>> CatalogStructureRunAsync(Guid tenantId, Guid runId, CancellationToken cancellationToken);
    Task<IReadOnlyList<CatalogStructureRunView>> CatalogStructureRunsAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken);
    Task<ServiceResult<Guid>> EnqueueProductSyncAsync(Guid tenantId, Guid connectionId, bool full, bool newOnly, bool existingOnly, bool mappingOnly, bool optionsOnly, bool includeArchived, bool includeDrafts, bool includePendingApproval, bool updateExistingProducts, bool updateExistingPricesAndStock, string? productLookup, string correlationId, CancellationToken cancellationToken);
    Task<ServiceResult<Guid>> EnqueueShipmentActionAsync(Guid tenantId, Guid packageId, long expectedVersion, ShipmentActionCommand command, string idempotencyKey, string correlationId, CancellationToken cancellationToken);
    Task<ServiceResult<ShipmentView>> ProcessShipmentInstantAsync(Guid tenantId, Guid packageId, long expectedVersion, string idempotencyKey, string correlationId, CancellationToken cancellationToken);
    Task<ServiceResult<ShipmentView>> ChangeCargoProviderInstantAsync(Guid tenantId, Guid packageId, long expectedVersion, ShipmentActionCommand command, string idempotencyKey, string correlationId, CancellationToken cancellationToken);
    Task<ServiceResult<Guid>> EnqueueCommonLabelAsync(Guid tenantId, Guid packageId, long expectedVersion, int boxQuantity, decimal volumetricHeight, string idempotencyKey, string correlationId, CancellationToken cancellationToken, string format = "ZPL");
    Task<ServiceResult<Guid>> EnqueueLabelCapabilityProbeAsync(Guid tenantId, Guid actorUserId, Guid packageId, long expectedVersion, string capabilityCode, int boxQuantity, decimal volumetricHeight, string idempotencyKey, string correlationId, CancellationToken cancellationToken);
    Task<ServiceResult<Guid>> EnqueueStageTestOrderAsync(Guid tenantId, Guid actorUserId, Guid connectionId, string idempotencyKey, string correlationId, CancellationToken cancellationToken);
    Task<PageResult<ReturnListView>> ReturnsAsync(Guid tenantId, int limit, string? after, ReturnListQuery query, bool latest, CancellationToken cancellationToken, int page = 1);
    Task<ReturnSummaryView> ReturnSummaryAsync(Guid tenantId, ReturnListQuery query, CancellationToken cancellationToken);
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
    // A delivery the route gate turned away: kept on the connection when the connection is known (an old or wrong route token).
    Task NoteRouteRefusedAsync(Guid connectionPublicId, CancellationToken cancellationToken);
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
