using MarketplaceHub.Domain;

namespace MarketplaceHub.Application;

public static class InvoicingJobTypes
{
    public const string ConnectionTest = "EFATURAM_CONNECTION_TEST";
    public const string InvoiceSubmit = "INVOICE_SUBMIT";
    public const string InvoiceReconcile = "INVOICE_RECONCILE";
    public const string InvoiceDocumentFetch = "INVOICE_DOCUMENT_FETCH";
    public const string MarketplaceDelivery = "INVOICE_MARKETPLACE_DELIVERY";
    public const string InvoiceCancellation = "INVOICE_CANCELLATION";
    public const string InvoiceDueScan = "INVOICE_DUE_SCAN";
    public const string StageCapabilityProbe = "EFATURAM_STAGE_CAPABILITY_PROBE";
}

public static class InvoicingCapabilities
{
    public const string ConnectionTest = "CONNECTION_TEST";
    public const string InvoiceSubmit = "INVOICE_SUBMIT";
    public const string InvoiceStatusRead = "INVOICE_STATUS_READ";
    public const string InvoiceDocumentRead = "INVOICE_DOCUMENT_READ";
    public const string InvoiceCancel = "INVOICE_CANCEL";
    public const string InvoiceDeliver = "INVOICE_DELIVER";
}

public static class OneTimeInvoiceDeliveryPolicy
{
    public const string TargetOrderNumber = "4486229624";
    public const string PriorNoWriteFailureCode = "HEPSIBURADA_CAPABILITY_NOT_ENABLED";
    public const string DeliveryAlreadyFailedErrorCode = "DELIVERY_ALREADY_FAILED";

    public static bool IsAuthorizedTarget(string? orderNumber) =>
        string.Equals(orderNumber, TargetOrderNumber, StringComparison.Ordinal);

    public static bool IsEligibleSourceFailure(string? latestErrorCode, bool hasPriorNoWriteAttempt) =>
        string.Equals(latestErrorCode, PriorNoWriteFailureCode, StringComparison.Ordinal)
        || string.Equals(latestErrorCode, DeliveryAlreadyFailedErrorCode, StringComparison.Ordinal)
            && hasPriorNoWriteAttempt;

    public static bool IsSafePriorFailure(string? status, string? errorCode, string? externalReference) =>
        string.Equals(status, "FAILED", StringComparison.Ordinal)
        && string.Equals(errorCode, PriorNoWriteFailureCode, StringComparison.Ordinal)
        && string.IsNullOrWhiteSpace(externalReference);
}

public sealed record InvoiceSubmission(Guid InvoiceId, string LocalReferenceId, string InvoiceType, string Currency, string PayloadJson, string RequestHash);
public sealed record InvoiceSubmissionResult(string ExternalReference, string? InvoiceNumber, string? EttnUuid, string RawStatus, string? RemoteRequestId);
public sealed record ExternalInvoiceReference(string ExternalReference, string? EttnUuid, string? InvoiceType = null);
public sealed record InvoiceRemoteStatus(string ExternalReference, string RawStatus, string CanonicalStatus, string? InvoiceNumber, string? EttnUuid, bool IsTerminal, string? GibStatus = null, int? GibStatusCode = null);
public sealed record RemoteInvoiceDocument(string DocumentKind, string MimeType, string FileName, byte[] Content, string? ExternalDocumentId, string? PermanentUrl = null);
public sealed record InvoiceCancellation(string ExternalReference, string? EttnUuid, string Reason);
public sealed record InvoiceCancellationResult(string ExternalReference, string RawStatus, string CanonicalStatus, bool IsTerminal);
public sealed record InvoiceDeliveryCommand(string ExternalPackageId, string DeliveryType, string PayloadJson, string RequestHash);
public sealed record InvoiceDeliveryResult(
    string ExternalReference,
    string RawStatus,
    bool SameAttemptVerified = false,
    string? VerifiedInvoiceNumber = null,
    string? VerifiedInvoiceLink = null);
public sealed record ExternalInvoiceDeliveryReference(
    string ExternalReference,
    string? OrderNumber = null,
    string? ExternalOrderId = null,
    string? InvoiceNumber = null,
    string? InvoiceLink = null);
public sealed record InvoiceDeliveryStatus(
    string ExternalReference,
    string RawStatus,
    bool IsTerminal,
    string? VerifiedInvoiceNumber = null,
    string? VerifiedInvoiceLink = null);
public sealed record RemoteMissingInvoicePackage(string OrderNumber, string PackageNumber, string? OrderStatus);

public enum InvoiceDeliveryFailureDisposition
{
    Retry,
    Unknown,
    Failed
}

public static class InvoiceDeliveryFailurePolicy
{
    public static InvoiceDeliveryFailureDisposition Classify(AdapterErrorClass errorClass) => errorClass switch
    {
        // A rate-limit response explicitly rejects the request before a
        // delivery result is returned, so retrying is safe for this adapter.
        AdapterErrorClass.RateLimit => InvoiceDeliveryFailureDisposition.Retry,
        // Trendyol has no delivery-status query for this flow and the
        // adapter does not advertise a provider idempotency header. A network
        // timeout, 5xx, or conflict may have happened after remote execution.
        AdapterErrorClass.TransientNetwork or AdapterErrorClass.Remote5xx or AdapterErrorClass.BusinessConflict => InvoiceDeliveryFailureDisposition.Unknown,
        _ => InvoiceDeliveryFailureDisposition.Failed
    };
}

public enum InvoiceDeliveryRecoveryAction
{
    WaitForMarketplaceReadback,
    RetryDelivery,
    ConfirmDelivery,
    StopRejected,
    ManualReview
}

public static class InvoiceDeliveryRecoveryPolicy
{
    public const int HepsiburadaRemoteFailureLimit = 3;

    public static bool ShouldStopAfterRemoteFailures(string? platformCode, int failureCount) =>
        string.Equals(platformCode, "HEPSIBURADA", StringComparison.OrdinalIgnoreCase)
        && failureCount >= HepsiburadaRemoteFailureLimit;

    public static InvoiceDeliveryRecoveryAction Decide(
        MarketplaceInvoiceStatus marketplaceStatus,
        string? marketplaceInvoiceNumber,
        string? expectedInvoiceNumber,
        DateTimeOffset? observedAt,
        DateTimeOffset uncertainAttemptAt)
    {
        if (observedAt is null || observedAt <= uncertainAttemptAt)
            return InvoiceDeliveryRecoveryAction.WaitForMarketplaceReadback;

        return marketplaceStatus switch
        {
            MarketplaceInvoiceStatus.NotInvoiced => InvoiceDeliveryRecoveryAction.RetryDelivery,
            MarketplaceInvoiceStatus.Invoiced
                when !string.IsNullOrWhiteSpace(expectedInvoiceNumber)
                     && string.Equals(marketplaceInvoiceNumber?.Trim(), expectedInvoiceNumber.Trim(), StringComparison.Ordinal)
                => InvoiceDeliveryRecoveryAction.ConfirmDelivery,
            MarketplaceInvoiceStatus.Invoiced => InvoiceDeliveryRecoveryAction.ManualReview,
            MarketplaceInvoiceStatus.Rejected => InvoiceDeliveryRecoveryAction.StopRejected,
            _ => InvoiceDeliveryRecoveryAction.WaitForMarketplaceReadback
        };
    }
}

public interface IInvoiceProviderPort
{
    Task<AdapterResult<ConnectionIdentity>> TestConnectionAsync(AdapterContext context, CancellationToken cancellationToken);
    Task<AdapterResult<InvoiceSubmissionResult>> SubmitAsync(AdapterContext context, InvoiceSubmission submission, CancellationToken cancellationToken);
    Task<AdapterResult<InvoiceRemoteStatus>> QueryStatusAsync(AdapterContext context, ExternalInvoiceReference reference, CancellationToken cancellationToken);
    Task<AdapterResult<RemoteInvoiceDocument>> GetDocumentAsync(AdapterContext context, ExternalInvoiceReference reference, string documentKind, CancellationToken cancellationToken);
    Task<AdapterResult<InvoiceCancellationResult>> CancelAsync(AdapterContext context, InvoiceCancellation command, CancellationToken cancellationToken);
}

public interface IInvoiceMarketplacePort
{
    Task<AdapterResult<InvoiceDeliveryResult>> DeliverAsync(AdapterContext context, InvoiceDeliveryCommand command, CancellationToken cancellationToken);
    Task<AdapterResult<InvoiceDeliveryStatus>> QueryDeliveryAsync(AdapterContext context, ExternalInvoiceDeliveryReference reference, CancellationToken cancellationToken);
}

public interface IHepsiburadaInvoiceStatusPort
{
    Task<AdapterResult<AdapterPageResult<RemoteMissingInvoicePackage>>> ListMissingInvoicePackagesAsync(AdapterContext context, AdapterPageRequest page, CancellationToken cancellationToken);
}

public sealed record InvoicePolicyView(Guid Id, Guid ProviderConnectionId, string TriggerState, string PackageScope, string DueRule, string RoundingRule, string AdjustmentRule, bool AutoSubmit, long Version);
public sealed record UpsertInvoicePolicyCommand(string TriggerState, string PackageScope, string DueRule, string RoundingRule, string AdjustmentRule, bool AutoSubmit);
public sealed record CreateInvoiceCommand(Guid OrderId, Guid? PackageId, Guid ProviderConnectionId, Guid? OriginalInvoiceId);
public sealed record InvoiceWorkspacePreviewRequest(IReadOnlyList<InvoiceWorkspacePreviewTarget> Items);
public sealed record InvoiceWorkspacePreviewTarget(Guid OrderId, Guid PackageId, Guid ProviderConnectionId);
public sealed record InvoiceWorkspacePreviewConfirmRequest(IReadOnlyList<InvoiceWorkspacePreviewConfirmation> Items);
public sealed record InvoiceWorkspacePreviewConfirmation(Guid OrderId, Guid PackageId, Guid ProviderConnectionId, string PreviewDigest);
public sealed record InvoiceWorkspacePreviewLine(string Description, string? Sku, decimal Quantity, string Unit, decimal VatRate, decimal UnitPrice, decimal DiscountAmount, decimal VatAmount, decimal Total);
public sealed record InvoiceWorkspacePreviewItem(
    Guid OrderId,
    Guid PackageId,
    Guid ProviderConnectionId,
    string OrderNumber,
    string PlatformCode,
    string PlatformName,
    string Environment,
    string CustomerType,
    string CustomerName,
    string TaxIdentityNumber,
    string InvoiceAddressJson,
    string InvoiceType,
    string Currency,
    decimal TaxExclusiveTotal,
    decimal DiscountTotal,
    decimal TaxTotal,
    decimal PayableTotal,
    IReadOnlyList<InvoiceWorkspacePreviewLine> Lines,
    bool CanConfirm,
    string? BlockedReason,
    string PreviewDigest,
    Guid? ExistingInvoiceId,
    string? ExistingInvoiceStatus,
    string? NextAction);
public sealed record InvoiceWorkspaceConfirmResult(IReadOnlyList<InvoiceWorkspaceConfirmItemResult> Items);
public sealed record InvoiceWorkspaceConfirmItemResult(Guid PackageId, Guid? InvoiceId, Guid? JobId, string Status, string Action, string Message);
public sealed record InvoiceListView(Guid Id, string OrderNumber, string InvoiceType, string Status, string Currency, decimal PayableTotal, string? InvoiceNumber, DateTimeOffset? DueAt, DateTimeOffset CreatedAt, long Version);
public sealed record InvoiceWorkspaceItemView(
    Guid OrderId,
    Guid PackageId,
    string OrderNumber,
    string CustomerName,
    DateTimeOffset OrderedAt,
    string ShipmentStatus,
    DateTimeOffset? DeliveredAt,
    DateTimeOffset? InvoiceDueAt,
    bool IsDueSoon,
    string Currency,
    decimal Amount,
    int ProductCount,
    string? PrimaryImageUrl,
    string? CargoProviderName,
    string? CargoTrackingNumber,
    Guid? InvoiceId,
    string InvoiceStatus,
    string? InvoiceNumber,
    bool CanCreateInvoice,
    string? ShipmentAddressJson = null,
    string? InvoiceAddressJson = null,
    IReadOnlyList<InvoiceWorkspaceLineView>? Lines = null,
    string? InvoiceErrorCode = null,
    string? InvoiceDeliveryStatus = null,
    string? InvoiceDeliveryReference = null,
    bool InvoiceDocumentAvailable = false,
    string PlatformCode = "TRENDYOL",
    string PlatformDisplayName = "Trendyol",
    bool InvoiceCreationEnabled = true,
    string? MarketplaceInvoiceReadErrorCode = null,
    string? MarketplaceInvoiceReadErrorSummary = null,
    string Environment = "UNKNOWN");
public sealed record InvoiceWorkspaceLineView(string Sku, string? Barcode, string Description, decimal Quantity, decimal UnitPrice, decimal VatRate, string? ImageUrl);
public sealed record InvoiceWorkspaceSummaryView(int DueSoonCount);
public sealed record InvoiceWorkspacePageQuery(
    int PageNumber = 1,
    int PageSize = 20,
    string Tab = "UNINVOICED",
    string? Search = null,
    IReadOnlyList<string>? PlatformCodes = null,
    string? ShipmentStatus = null,
    string? CargoProviderName = null,
    string? InvoiceStatus = null,
    string? InvoiceAction = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    bool ProviderHasCredential = false);
public sealed record InvoiceWorkspacePageView(
    IReadOnlyList<InvoiceWorkspaceItemView> Items,
    int TotalCount,
    int PageNumber,
    int PageSize,
    int TotalPages,
    int UninvoicedCount,
    int InvoicedCount,
    int DueSoonCount,
    int HiddenCount,
    int TotalPackageCount,
    bool HasPendingMarketplaceInvoices,
    IReadOnlyList<string> ShipmentStatuses,
    IReadOnlyList<string> CargoProviders,
    IReadOnlyList<string> InvoiceStatuses);
public sealed record InvoiceLineView(Guid Id, int LineSequence, string Description, string? Sku, string Unit, decimal Quantity, decimal UnitPrice, decimal DiscountAmount, decimal VatRate, decimal VatAmount, decimal LineTotal);
public sealed record InvoiceDocumentView(Guid Id, string DocumentType, string Sha256, DateTimeOffset CreatedAt);
public sealed record InvoiceAttemptView(int AttemptNumber, string Outcome, string? ErrorCode, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt);
public sealed record MarketplaceDeliveryView(Guid Id, string DeliveryType, string Status, string? ExternalReference, string? ErrorCode, DateTimeOffset CreatedAt);
public sealed record InvoiceDetailView(Guid Id, Guid OrderId, string OrderNumber, Guid? PackageId, Guid ProviderConnectionId, string InvoiceType, string SequencePurpose, string Status, string Currency, decimal TaxExclusiveTotal, decimal DiscountTotal, decimal TaxTotal, decimal PayableTotal, string Note, string? InvoiceNumber, string? EttnUuid, DateTimeOffset? DueAt, DateTimeOffset? IssuedAt, string? LastErrorCode, IReadOnlyList<InvoiceLineView> Lines, IReadOnlyList<InvoiceDocumentView> Documents, IReadOnlyList<InvoiceAttemptView> Attempts, IReadOnlyList<MarketplaceDeliveryView> Deliveries, IReadOnlyList<string> AllowedActions, long Version, bool RequiresSensitiveConfirmation);

public interface IInvoicingBillingService
{
    Task<ServiceResult<InvoicePolicyView>> GetPolicyAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken);
    Task<ServiceResult<InvoicePolicyView>> UpsertPolicyAsync(Guid tenantId, Guid connectionId, long? expectedVersion, UpsertInvoicePolicyCommand command, CancellationToken cancellationToken);
    Task<PageResult<InvoiceListView>> ListAsync(Guid tenantId, int limit, string? after, string? status, CancellationToken cancellationToken);
    Task<IReadOnlyList<InvoiceWorkspaceItemView>> WorkspaceAsync(Guid tenantId, CancellationToken cancellationToken);
    Task<InvoiceWorkspacePageView> WorkspacePageAsync(Guid tenantId, InvoiceWorkspacePageQuery query, CancellationToken cancellationToken);
    Task<InvoiceWorkspaceSummaryView> WorkspaceSummaryAsync(Guid tenantId, CancellationToken cancellationToken);
    Task<ServiceResult<IReadOnlyList<InvoiceWorkspacePreviewItem>>> PreviewWorkspaceInvoicesAsync(Guid tenantId, InvoiceWorkspacePreviewRequest request, CancellationToken cancellationToken);
    Task<ServiceResult<InvoiceWorkspaceConfirmResult>> ConfirmWorkspaceInvoicesAsync(Guid tenantId, InvoiceWorkspacePreviewConfirmRequest request, string idempotencyKey, string correlationId, CancellationToken cancellationToken);
    Task<ServiceResult<InvoiceDetailView>> CreateDraftAsync(Guid tenantId, CreateInvoiceCommand command, string idempotencyKey, CancellationToken cancellationToken);
    Task<ServiceResult<InvoiceDetailView>> GetAsync(Guid tenantId, Guid id, CancellationToken cancellationToken);
    Task<ServiceResult<InvoiceDetailView>> ValidateAsync(Guid tenantId, Guid id, long expectedVersion, CancellationToken cancellationToken);
    Task<ServiceResult<Guid>> EnqueueSubmitAsync(Guid tenantId, Guid id, long expectedVersion, string idempotencyKey, string correlationId, CancellationToken cancellationToken);
    Task<ServiceResult<Guid>> EnqueueStageCapabilityProbeAsync(Guid tenantId, Guid id, long expectedVersion, string idempotencyKey, string correlationId, CancellationToken cancellationToken);
    Task<ServiceResult<Guid>> EnqueueReconcileAsync(Guid tenantId, Guid id, string idempotencyKey, string correlationId, CancellationToken cancellationToken);
    Task<ServiceResult<Guid>> EnqueueDeliveryAsync(Guid tenantId, Guid id, string idempotencyKey, string correlationId, CancellationToken cancellationToken);
    Task<ServiceResult<Guid>> EnqueueCancellationAsync(Guid tenantId, Guid id, long expectedVersion, string idempotencyKey, string correlationId, CancellationToken cancellationToken);
    Task<ServiceResult<(Stream Content, string MimeType, string FileName)>> OpenDocumentAsync(Guid tenantId, Guid invoiceId, Guid documentId, CancellationToken cancellationToken);
}

public interface IInvoicingJobProcessor
{
    Task<JobExecutionResult> ProcessAsync(Guid tenantId, Guid? connectionId, string jobType, string payloadJson, string correlationId, CancellationToken cancellationToken);
}

public sealed record InvoiceReconciliationView(Guid InvoiceId, string Status, IReadOnlyList<ReconciliationDifferenceView> Differences);
public interface IInvoicingReconciliationService
{
    Task<ServiceResult<InvoiceReconciliationView>> RunLocalDryAsync(Guid tenantId, Guid invoiceId, CancellationToken cancellationToken);
}
