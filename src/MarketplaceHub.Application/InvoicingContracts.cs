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

public sealed record InvoiceSubmission(Guid InvoiceId, string LocalReferenceId, string InvoiceType, string Currency, string PayloadJson, string RequestHash);
public sealed record InvoiceSubmissionResult(string ExternalReference, string? InvoiceNumber, string? EttnUuid, string RawStatus, string? RemoteRequestId);
public sealed record ExternalInvoiceReference(string ExternalReference, string? EttnUuid, string? InvoiceType = null);
public sealed record InvoiceRemoteStatus(string ExternalReference, string RawStatus, string CanonicalStatus, string? InvoiceNumber, string? EttnUuid, bool IsTerminal, string? GibStatus = null, int? GibStatusCode = null);
public sealed record RemoteInvoiceDocument(string DocumentKind, string MimeType, string FileName, byte[] Content, string? ExternalDocumentId, string? PermanentUrl = null);
public sealed record InvoiceCancellation(string ExternalReference, string? EttnUuid, string Reason);
public sealed record InvoiceCancellationResult(string ExternalReference, string RawStatus, string CanonicalStatus, bool IsTerminal);
public sealed record InvoiceDeliveryCommand(string ExternalPackageId, string DeliveryType, string PayloadJson, string RequestHash);
public sealed record InvoiceDeliveryResult(string ExternalReference, string RawStatus);
public sealed record ExternalInvoiceDeliveryReference(string ExternalReference);
public sealed record InvoiceDeliveryStatus(string ExternalReference, string RawStatus, bool IsTerminal);

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

public sealed record InvoicePolicyView(Guid Id, Guid ProviderConnectionId, string TriggerState, string PackageScope, string DueRule, string RoundingRule, string AdjustmentRule, bool AutoSubmit, long Version);
public sealed record UpsertInvoicePolicyCommand(string TriggerState, string PackageScope, string DueRule, string RoundingRule, string AdjustmentRule, bool AutoSubmit);
public sealed record CreateInvoiceCommand(Guid OrderId, Guid? PackageId, Guid ProviderConnectionId, Guid? OriginalInvoiceId);
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
    bool CanCreateInvoice);
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
    Task<IReadOnlyList<InvoiceWorkspaceItemView>> WorkspaceAsync(Guid tenantId, int limit, CancellationToken cancellationToken);
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
