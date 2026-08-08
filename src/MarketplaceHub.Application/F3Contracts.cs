namespace MarketplaceHub.Application;

public static class ActiveIntegrationScope
{
    public static bool Contains(string? platformCode) => platformCode is "TRENDYOL" or "TRENDYOL_EFATURAM";
}

public static class F3JobTypes
{
    public const string ConnectionTest = "TRENDYOL_CONNECTION_TEST";
    public const string ReferenceSync = "TRENDYOL_REFERENCE_SYNC";
    public const string OrderSync = "TRENDYOL_ORDER_SYNC";
    public const string ShipmentAction = "TRENDYOL_SHIPMENT_ACTION";
    public const string ReturnSync = "TRENDYOL_RETURN_SYNC";
    public const string ReturnAction = "TRENDYOL_RETURN_ACTION";
    public const string WebhookIngest = "TRENDYOL_WEBHOOK_INGEST";
    public const string ProductCreate = "TRENDYOL_PRODUCT_CREATE";
    public const string ProductApprovalReconcile = "TRENDYOL_PRODUCT_APPROVAL_RECONCILE";
    public const string ProductUpdate = "TRENDYOL_PRODUCT_UPDATE";
    public const string ProductArchive = "TRENDYOL_PRODUCT_ARCHIVE";
    public const string PriceInventorySync = "TRENDYOL_PRICE_INVENTORY_SYNC";
    public const string CommonLabel = "TRENDYOL_COMMON_LABEL";
}

public static class F3Capabilities
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

public sealed record AdapterContext(Guid TenantId, Guid ConnectionId, string CorrelationId, string IdempotencyKey, DateTimeOffset DeadlineUtc);
public sealed record AdapterError(AdapterErrorClass Class, string Code, string SafeMessage, int? HttpStatus, TimeSpan? RetryAfter, string? RemoteRequestId);
public sealed record RateLimitMetadata(int? Remaining, DateTimeOffset? ResetAt, TimeSpan? RetryAfter);
public sealed record AdapterResult<T>(bool IsSuccess, T? Value, AdapterError? Error, RateLimitMetadata? RateLimit)
{
    public static AdapterResult<T> Success(T value, RateLimitMetadata? rateLimit = null) => new(true, value, null, rateLimit);
    public static AdapterResult<T> Failure(AdapterError error, RateLimitMetadata? rateLimit = null) => new(false, default, error, rateLimit);
}

public sealed record AdapterPageRequest(string? Cursor, int Limit);
public sealed record AdapterPageResult<T>(IReadOnlyList<T> Items, string? NextCursor, bool HasMore);
public sealed record ConnectionIdentity(string PlatformCode, string Environment, string ExternalStoreId, string ApiVersion, string ScopeFingerprint);
public sealed record CapabilityEvidence(string Code, string SupportLevel, string ApiVersion, string Environment, string StoreScope, string SourceUrl, string SourceVersion, string? RequiredScope, string? ConstraintsJson, string EvidenceNote, string? FixtureChecksum, DateTimeOffset VerifiedAt);
public sealed record RemoteReferenceItem(string ResourceType, string ExternalId, string? ParentExternalId, string Name, string Path, int Depth, bool IsLeaf, bool IsActive, string RawJson, bool? IsRequired = null, bool? AllowsCustomValue = null, bool? AllowsMultipleValues = null);
public sealed record ReferenceResource(string ResourceType, string? ParentExternalId);
public sealed record RemoteOperationRef(string ExternalOperationId, string Kind, DateTimeOffset SubmittedAt);
public sealed record RemoteOperationLine(string ExternalKey, bool Succeeded, string? ExternalId, string? ErrorCode, bool Retryable);
public sealed record RemoteOperationStatus(string ExternalOperationId, string Status, IReadOnlyList<RemoteOperationLine> Lines);
public sealed record ProductPublication(Guid ProductId, string PayloadHash, string PayloadJson);
public sealed record ProductPublicationJobPayload(Guid JobId, Guid ProductId, Guid ProfileId, string Phase, string PayloadHash, string PayloadJson, string? ExternalOperationId, DateTimeOffset? SubmittedAt);
public sealed record ProductApprovalReconciliationJobPayload(Guid JobId, Guid ProductId, Guid ProfileId, string PayloadHash, DateTimeOffset StartedAt, DateTimeOffset DeadlineAt);
public sealed record ProductUpdatePublication(Guid ProductId, string Mode, string PayloadHash, string UnapprovedPayloadJson, string ApprovedContentPayloadJson, string ApprovedVariantPayloadJson, string ApprovedDeliveryPayloadJson);
public sealed record ProductUpdateJobPayload(Guid JobId, Guid ProductId, Guid ProfileId, string Phase, string Mode, string PayloadHash, string UnapprovedPayloadJson, string ApprovedContentPayloadJson, string ApprovedVariantPayloadJson, string ApprovedDeliveryPayloadJson, string? ExternalOperationId, DateTimeOffset? SubmittedAt);
public sealed record ProductArchiveJobPayload(Guid JobId, Guid ProductId, Guid ProfileId, bool Archived, string Phase, string PayloadHash, string PayloadJson, string? ExternalOperationId, DateTimeOffset StartedAt, DateTimeOffset DeadlineAt);
public sealed record ExternalProductIdentity(string ExternalProductId, string? ExternalVariantId);
public sealed record RemoteProduct(string ExternalProductId, string? ExternalVariantId, string? Barcode, string? Sku, string RawJson);
public sealed record RemotePublicationStatus(string Barcode, string Status, string? ExternalProductId, string? ExternalVariantId, string? RejectionCode, string RawJson);
public sealed record ProductReadFilter(DateTimeOffset? ModifiedAfter);
public sealed record StockPushLine(Guid VariantId, string Barcode, decimal Quantity, long ProjectionVersion);
public sealed record PricePushLine(Guid VariantId, string Barcode, decimal ListPrice, decimal SalePrice, string Currency, long PriceVersion);
public sealed record PriceInventoryPushLine(Guid VariantId, Guid OfferId, string Barcode, decimal Quantity, decimal ListPrice, decimal SalePrice, string Currency, long ProjectionVersion, long PriceVersion, string PriceHash);
public sealed record PriceInventoryJobPayload(Guid JobId, Guid ConnectionId, string Phase, string PayloadHash, string PayloadJson, IReadOnlyList<PriceInventoryPushLine> Lines, string? ExternalOperationId, DateTimeOffset? SubmittedAt);
public sealed record BatchLineResult(Guid LocalId, bool Succeeded, string? ErrorCode, bool Retryable);
public sealed record BatchResult<T>(IReadOnlyList<T> Lines, string? ExternalOperationId, bool IsPartial);
public sealed record OrderPollWindow(DateTimeOffset? ModifiedAfter, DateTimeOffset? ModifiedBefore);
public sealed record RemoteOrderLine(string ExternalLineId, string Sku, string? Barcode, string Title, decimal Quantity, decimal UnitPrice, decimal VatRate, string RawStatus);
public sealed record RemotePackageAllocation(string ExternalLineId, decimal AllocatedQuantity, decimal CancelledQuantity, decimal ShippedQuantity, decimal DeliveredQuantity, decimal ReturnedQuantity);
public sealed record RemotePackage(string ExternalPackageId, string? OriginExternalPackageId, string RawStatus, DateTimeOffset OccurredAt, string? CargoProviderExternalId, string? CargoTrackingNumber, IReadOnlyList<RemotePackageAllocation> Allocations, decimal GrossAmount = 0, decimal DiscountAmount = 0, decimal NetAmount = 0);
public sealed record RemoteOrder(string ExternalOrderId, string OrderNumber, DateTimeOffset OrderedAt, DateTimeOffset LastModifiedAt, string Currency, decimal GrossAmount, decimal DiscountAmount, decimal NetAmount, string CustomerSnapshotJson, string ShipmentAddressSnapshotJson, string InvoiceAddressSnapshotJson, IReadOnlyList<RemoteOrderLine> Lines, IReadOnlyList<RemotePackage> Packages, string RawJson);
public sealed record PackageActionCommand(string ExternalPackageId, string Action, string PayloadJson);
public sealed record ShipmentActionJobPayload(Guid JobId, Guid PackageId, string Action, string PayloadJson);
public sealed record PackageActionResult(string ExternalPackageId, string Status, string? ExternalOperationId);
public sealed record CommonLabelRequest(string CargoTrackingNumber, int BoxQuantity, decimal VolumetricHeight);
public sealed record CommonLabelDocument(string CargoTrackingNumber, string Format, byte[] Content);
public sealed record CommonLabelJobPayload(Guid JobId, Guid PackageId, string Phase, int BoxQuantity, decimal VolumetricHeight, DateTimeOffset StartedAt, DateTimeOffset DeadlineAt);
public sealed record ReturnPollWindow(DateTimeOffset? ModifiedAfter, DateTimeOffset? ModifiedBefore);
public sealed record RemoteReturnLine(string ExternalLineId, string ExternalOrderLineId, decimal Quantity);
public sealed record RemoteReturnClaim(string ExternalClaimId, string ExternalOrderId, string RawStatus, string? ReasonCode, string? ReasonText, DateTimeOffset? ActionDueAt, DateTimeOffset LastModifiedAt, IReadOnlyList<RemoteReturnLine> Lines, string RawJson);
public sealed record ReturnEvidenceFile(string FileName, string MimeType, byte[] Content);
public sealed record ReturnActionCommand(string ExternalClaimId, IReadOnlyList<string> ExternalLineItemIds, string Action, string? ReasonCode, string? Explanation, IReadOnlyList<ReturnEvidenceFile> EvidenceFiles);
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
    Task<AdapterResult<RemoteOperationRef>> CreateAsync(AdapterContext context, ProductPublication publication, CancellationToken cancellationToken);
    Task<AdapterResult<RemoteOperationRef>> UpdateUnapprovedAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken);
    Task<AdapterResult<RemoteOperationRef>> UpdateApprovedContentAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken);
    Task<AdapterResult<RemoteOperationRef>> UpdateApprovedVariantsAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken);
    Task<AdapterResult<RemoteOperationRef>> UpdateApprovedDeliveryAsync(AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken);
    Task<AdapterResult<RemoteOperationStatus>> GetOperationAsync(AdapterContext context, string externalOperationId, CancellationToken cancellationToken);
    Task<AdapterResult<RemotePublicationStatus>> GetPublicationStatusAsync(AdapterContext context, string barcode, CancellationToken cancellationToken);
    Task<AdapterResult<RemoteOperationRef>> ArchiveAsync(AdapterContext context, string payloadJson, CancellationToken cancellationToken);
}

public interface IInventoryPricePort
{
    Task<AdapterResult<RemoteOperationRef>> PushPriceAndInventoryAsync(AdapterContext context, string payloadJson, CancellationToken cancellationToken);
}

public interface IOrderPort
{
    Task<AdapterResult<AdapterPageResult<RemoteOrder>>> PollAsync(AdapterContext context, OrderPollWindow window, AdapterPageRequest page, CancellationToken cancellationToken);
    Task<AdapterResult<RemoteOrder>> GetAsync(AdapterContext context, string externalOrderId, CancellationToken cancellationToken);
    Task<AdapterResult<PackageActionResult>> ExecutePackageActionAsync(AdapterContext context, PackageActionCommand command, CancellationToken cancellationToken);
    Task<AdapterResult<bool>> CreateCommonLabelAsync(AdapterContext context, CommonLabelRequest request, CancellationToken cancellationToken);
    Task<AdapterResult<CommonLabelDocument>> GetCommonLabelAsync(AdapterContext context, string cargoTrackingNumber, CancellationToken cancellationToken);
}

public interface IReturnPort
{
    Task<AdapterResult<AdapterPageResult<RemoteReturnClaim>>> PollAsync(AdapterContext context, ReturnPollWindow window, AdapterPageRequest page, CancellationToken cancellationToken);
    Task<AdapterResult<RemoteReturnClaim>> GetAsync(AdapterContext context, string externalReturnId, CancellationToken cancellationToken);
    Task<AdapterResult<IReadOnlyList<ReturnIssueReason>>> IssueReasonsAsync(AdapterContext context, CancellationToken cancellationToken);
    Task<AdapterResult<ReturnActionResult>> ExecuteAsync(AdapterContext context, ReturnActionCommand command, CancellationToken cancellationToken);
}

public interface IWebhookVerifier
{
    ValueTask<AdapterResult<VerifiedWebhookEnvelope>> VerifyAsync(ReadOnlyMemory<byte> rawBody, IReadOnlyDictionary<string, string> headers, Guid connectionId, Guid subscriptionId, CancellationToken cancellationToken);
}

public sealed record ConnectionView(Guid Id, Guid PublicId, string PlatformCode, string Environment, string DisplayName, string ExternalStoreId, string Status, string ApiVersion, DateTimeOffset? LastTestedAt, DateTimeOffset? LastSuccessAt, string? LastErrorCode, bool HasCredential, long Version);
public sealed record CapabilityView(string Code, string SupportLevel, string ApiVersion, string Environment, string StoreScope, string? SourceUrl, DateTimeOffset? VerifiedAt, string? ConstraintsJson, long Version);
public sealed record RecordCapabilityEvidenceCommand(string SupportLevel, string SourceUrl, string SourceVersion, string Environment, string StoreScope, string EvidenceNote, string? FixtureChecksum, string? ConstraintsJson, DateTimeOffset VerifiedAt);
public sealed record CreateConnectionCommand(string DisplayName, string Environment, string ExternalStoreId, string ApiVersion, string? UserAgentIdentity, string? PlatformCode = null);
public sealed record UpdateConnectionCommand(string DisplayName, string? UserAgentIdentity);
public sealed record CredentialCommand(
    string? ApiKey,
    string? ApiSecret,
    string? Email = null,
    string? Password = null,
    string? AccessToken = null,
    string? ClientSecret = null,
    string? Username = null);
public sealed record SyncPolicyView(Guid Id, string ResourceType, int IntervalSeconds, int OverlapSeconds, int JitterSeconds, bool Enabled, long Version);
public sealed record UpdateSyncPolicyCommand(int IntervalSeconds, int OverlapSeconds, int JitterSeconds, bool Enabled);
public sealed record WebhookSubscriptionView(Guid Id, string AuthenticationType, string Status, string? ExternalSubscriptionId, DateTimeOffset? VerifiedAt, DateTimeOffset? LastReceivedAt, long Version);
public sealed record CreateWebhookSubscriptionCommand(string AuthenticationType, string? Username, string? Password, string? ApiKey);
public sealed record CreatedWebhookSubscription(WebhookSubscriptionView Subscription, Guid ConnectionPublicId, string RouteToken);

public interface IF3ConnectionService
{
    Task<PageResult<ConnectionView>> ListAsync(Guid tenantId, int limit, string? after, CancellationToken cancellationToken);
    Task<ServiceResult<ConnectionView>> CreateAsync(Guid tenantId, CreateConnectionCommand command, CancellationToken cancellationToken);
    Task<ServiceResult<ConnectionView>> GetAsync(Guid tenantId, Guid id, CancellationToken cancellationToken);
    Task<ServiceResult<ConnectionView>> UpdateAsync(Guid tenantId, Guid id, long expectedVersion, UpdateConnectionCommand command, CancellationToken cancellationToken);
    Task<ServiceResult<ConnectionView>> RotateCredentialAsync(Guid tenantId, Guid id, long expectedVersion, CredentialCommand command, CancellationToken cancellationToken);
    Task<ServiceResult<Guid>> EnqueueTestAsync(Guid tenantId, Guid id, string idempotencyKey, string correlationId, CancellationToken cancellationToken);
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
    IReadOnlyList<ShipmentView>? Packages = null);
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
    string? ImageUrl = null);
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
    string? CargoProviderName = null);
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
    string InvoiceStatus = "FATURA_BEKLIYOR");
public sealed record ShipmentDetailView(ShipmentView Package, IReadOnlyList<string> AllowedActions, IReadOnlyList<string> SupportedLabelFormats, IReadOnlyList<ShipmentDocumentView> Documents);
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
    bool HasInventoryMapping);
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
    int ProductCount = 0);
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
    bool StockDispositionAvailable = false);
public sealed record ReturnDecisionCommand(string Action, string? ReasonCode, string? Explanation, IReadOnlyList<Guid>? EvidenceAssetIds);
public sealed record ReturnDispositionCommand(Guid ReturnLineId, string Disposition, decimal Quantity, string Reason);

public interface IF3SalesService
{
    Task<PageResult<OrderListView>> OrdersAsync(Guid tenantId, int limit, string? after, string? status, CancellationToken cancellationToken);
    Task<ServiceResult<OrderDetailView>> OrderAsync(Guid tenantId, Guid id, CancellationToken cancellationToken);
    Task<PageResult<ShipmentView>> ShipmentsAsync(Guid tenantId, int limit, string? after, string? status, CancellationToken cancellationToken);
    Task<ServiceResult<ShipmentDetailView>> ShipmentAsync(Guid tenantId, Guid id, CancellationToken cancellationToken);
    Task<ServiceResult<Guid>> EnqueueOrderSyncAsync(Guid tenantId, Guid connectionId, string? externalOrderId, string correlationId, CancellationToken cancellationToken);
    Task<ServiceResult<Guid>> EnqueueReferenceSyncAsync(Guid tenantId, Guid connectionId, string resourceType, string? parentExternalId, string correlationId, CancellationToken cancellationToken);
    Task<ServiceResult<Guid>> EnqueueShipmentActionAsync(Guid tenantId, Guid packageId, long expectedVersion, ShipmentActionCommand command, string idempotencyKey, string correlationId, CancellationToken cancellationToken);
    Task<ServiceResult<Guid>> EnqueueCommonLabelAsync(Guid tenantId, Guid packageId, long expectedVersion, int boxQuantity, decimal volumetricHeight, string idempotencyKey, string correlationId, CancellationToken cancellationToken);
    Task<PageResult<ReturnListView>> ReturnsAsync(Guid tenantId, int limit, string? after, string? status, CancellationToken cancellationToken);
    Task<ServiceResult<ReturnDetailView>> ReturnAsync(Guid tenantId, Guid id, CancellationToken cancellationToken);
    Task<ServiceResult<IReadOnlyList<ReturnIssueReason>>> ReturnIssueReasonsAsync(Guid tenantId, Guid id, string correlationId, CancellationToken cancellationToken);
    Task<ServiceResult<Guid>> EnqueueReturnSyncAsync(Guid tenantId, Guid connectionId, string correlationId, CancellationToken cancellationToken);
    Task<ServiceResult<Guid>> EnqueueReturnActionAsync(Guid tenantId, Guid userId, Guid claimId, long expectedVersion, ReturnDecisionCommand command, string idempotencyKey, string correlationId, CancellationToken cancellationToken);
    Task<ServiceResult<ReturnDetailView>> ApplyDispositionAsync(Guid tenantId, Guid userId, Guid claimId, ReturnDispositionCommand command, string idempotencyKey, string correlationId, CancellationToken cancellationToken);
}

public interface IF3WebhookService
{
    Task<ServiceResult<bool>> ReceiveAsync(Guid connectionPublicId, string routeToken, ReadOnlyMemory<byte> rawBody, IReadOnlyDictionary<string, string> headers, string correlationId, CancellationToken cancellationToken);
}

public interface IF3JobProcessor
{
    Task<JobExecutionResult> ProcessAsync(Guid tenantId, Guid? connectionId, string jobType, string payloadJson, string correlationId, CancellationToken cancellationToken);
}

public sealed record ReconciliationDifferenceView(string EntityType, string EntityKey, string FieldName, string? LocalValueHash, string? RemoteValueHash, string Resolution);
public sealed record ReconciliationRunView(Guid Id, Guid ConnectionId, string Scope, string Status, int ComparedCount, int DifferenceCount, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt, IReadOnlyList<ReconciliationDifferenceView> Differences);

public interface IF3ReconciliationService
{
    Task<ServiceResult<ReconciliationRunView>> RunLocalDryAsync(Guid tenantId, Guid connectionId, string scope, CancellationToken cancellationToken);
    Task<ServiceResult<ReconciliationRunView>> GetAsync(Guid tenantId, Guid id, CancellationToken cancellationToken);
}
