using MarketplaceHub.Domain;

namespace MarketplaceHub.Application;

public static class InvoicingJobTypes
{
    public const string ConnectionTest = "EFATURAM_CONNECTION_TEST";
    public const string InvoiceSubmit = "INVOICE_SUBMIT";
    public const string InvoiceReconcile = "INVOICE_RECONCILE";
    public const string InvoiceDocumentFetch = "INVOICE_DOCUMENT_FETCH";
    public const string MarketplaceDelivery = "INVOICE_MARKETPLACE_DELIVERY";
    public const string ManualOrderInvoiceDelivery = "MANUAL_ORDER_INVOICE_DELIVERY";
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
        orderNumber is TargetOrderNumber or "4034357330";

    public static bool IsAuthorizedStageDocument(string? orderNumber, Guid invoiceId) =>
        orderNumber == "4034357330" && invoiceId == Guid.Parse("01a125c4-a478-74a8-8865-943228b09515")
        || orderNumber == TargetOrderNumber && invoiceId == Guid.Parse("01a121b0-28bf-7bc7-9e3e-0c1ca397c0ae");

    public static bool IsEligibleSourceFailure(string? latestErrorCode, bool hasPriorNoWriteAttempt) =>
        latestErrorCode == InvoiceMarketplaceRetryPolicy.RepeatedRemoteFailure
        || string.Equals(latestErrorCode, PriorNoWriteFailureCode, StringComparison.Ordinal)
        || string.Equals(latestErrorCode, DeliveryAlreadyFailedErrorCode, StringComparison.Ordinal)
            && hasPriorNoWriteAttempt;

    public static bool IsSafePriorFailure(string? status, string? errorCode, string? externalReference) =>
        string.Equals(status, "FAILED", StringComparison.Ordinal)
        && (errorCode == PriorNoWriteFailureCode || errorCode == InvoiceMarketplaceRetryPolicy.RepeatedRemoteFailure)
        && string.IsNullOrWhiteSpace(externalReference);
}

public sealed record InvoiceSubmission(Guid InvoiceId, string LocalReferenceId, string InvoiceType, string Currency, string PayloadJson, string RequestHash);
public sealed record InvoiceSubmissionResult(string ExternalReference, string? InvoiceNumber, string? EttnUuid, string RawStatus, string? RemoteRequestId);
public sealed record ExternalInvoiceReference(string ExternalReference, string? EttnUuid, string? InvoiceType = null);
public sealed record InvoiceRemoteStatus(string ExternalReference, string RawStatus, string CanonicalStatus, string? InvoiceNumber, string? EttnUuid, bool IsTerminal, string? GibStatus = null, int? GibStatusCode = null);
public sealed record RemoteInvoiceDocument(string DocumentKind, string MimeType, string FileName, byte[] Content, string? ExternalDocumentId, string? PermanentUrl = null);
public sealed record InvoiceCancellation(string ExternalReference, string? EttnUuid, string Reason);
public sealed record InvoiceCancellationResult(string ExternalReference, string RawStatus, string CanonicalStatus, bool IsTerminal);
// StoreFrontCode: the stored storefront of the package's order (Trendyol sends it as the storeFrontCode header; null = TR).
public sealed record InvoiceDeliveryCommand(string ExternalPackageId, string DeliveryType, string PayloadJson, string RequestHash, string? StoreFrontCode = null);
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
    string? InvoiceLink = null,
    // Where the package can be found (order date, storefront, last status time), so a marketplace that
    // searches by date window and storefront reads the right one instead of trying them all.
    PackageLookup? Lookup = null);
public sealed record InvoiceDeliveryStatus(
    string ExternalReference,
    string RawStatus,
    bool IsTerminal,
    string? VerifiedInvoiceNumber = null,
    string? VerifiedInvoiceLink = null,
    bool ReferencesAreAuthoritative = false);

public static class InvoiceDeliveryProofPolicy
{
    public static bool HasExactProof(InvoiceDeliveryStatus status, string? invoiceNumber, string? invoiceLink)
    {
        var numberMatches = !string.IsNullOrWhiteSpace(invoiceNumber)
            && string.Equals(status.VerifiedInvoiceNumber, invoiceNumber, StringComparison.Ordinal);
        var linkMatches = !string.IsNullOrWhiteSpace(invoiceLink)
            && string.Equals(status.VerifiedInvoiceLink, invoiceLink, StringComparison.Ordinal);
        return status.ReferencesAreAuthoritative
            ? numberMatches && linkMatches
            : numberMatches || linkMatches;
    }
}
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
    public const string HepsiburadaAcceptedButReadbackMissingErrorCode = "HEPSIBURADA_INVOICE_DELIVERY_READBACK_MISSING";
    public const string MarketplaceInvoiceNotPresentErrorCode = "REMOTE_INVOICE_NOT_PRESENT";
    public const string MarketplaceInvoiceIdentityMismatchErrorCode = "MARKETPLACE_INVOICE_IDENTITY_MISMATCH";

    public static bool ShouldStopAfterRemoteFailures(string? platformCode, int failureCount) =>
        string.Equals(platformCode, "HEPSIBURADA", StringComparison.OrdinalIgnoreCase)
        && failureCount >= HepsiburadaRemoteFailureLimit;

    // Hepsiburada accepted the link for a package read empty just before it, but did not yet show an invoice; a later
    // read that shows one confirms this delivery (Hepsiburada never reports which invoice number is attached).
    public static bool ConfirmsAcceptedHepsiburadaAttemptReadback(
        string? deliveryStatus,
        string? deliveryErrorCode,
        MarketplaceInvoiceStatus observedMarketplaceStatus,
        DateTimeOffset? observedAt,
        DateTimeOffset acceptedAttemptAt) =>
        string.Equals(deliveryStatus, "UNKNOWN", StringComparison.Ordinal)
        && string.Equals(deliveryErrorCode, HepsiburadaAcceptedButReadbackMissingErrorCode, StringComparison.Ordinal)
        && observedMarketplaceStatus == MarketplaceInvoiceStatus.Invoiced
        && observedAt is { } readAt
        && readAt > acceptedAttemptAt;

    public static bool ShouldPreservePreviouslyConfirmedDelivery(
        InvoiceStatus invoiceStatus,
        string? deliveryStatus,
        string? remoteInvoiceNumber) =>
        invoiceStatus == InvoiceStatus.Completed
        && string.Equals(deliveryStatus, "CONFIRMED", StringComparison.Ordinal)
        && string.IsNullOrWhiteSpace(remoteInvoiceNumber);

    public static bool ShouldMarkMarketplaceInvoiceNotPresent(
        InvoiceStatus invoiceStatus,
        string? lastErrorCode,
        MarketplaceInvoiceStatus marketplaceStatus) =>
        marketplaceStatus == MarketplaceInvoiceStatus.NotInvoiced
        && (invoiceStatus == InvoiceStatus.Completed
            || (invoiceStatus is InvoiceStatus.MarketplacePending or InvoiceStatus.MarketplaceFailed)
                && string.Equals(lastErrorCode, MarketplaceInvoiceIdentityMismatchErrorCode, StringComparison.Ordinal)
            || (invoiceStatus == InvoiceStatus.ManualReview
                && string.Equals(lastErrorCode, MarketplaceInvoiceIdentityMismatchErrorCode, StringComparison.Ordinal)));

    public static bool ShouldQueueAutomaticRedelivery(
        InvoiceStatus invoiceStatus,
        string? lastErrorCode,
        MarketplaceInvoiceStatus marketplaceStatus) =>
        marketplaceStatus == MarketplaceInvoiceStatus.NotInvoiced
        && (invoiceStatus is InvoiceStatus.Accepted or InvoiceStatus.MarketplacePending or InvoiceStatus.Completed
            || invoiceStatus == InvoiceStatus.MarketplaceFailed
                && string.Equals(lastErrorCode, MarketplaceInvoiceNotPresentErrorCode, StringComparison.Ordinal));

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

/// <summary>
/// An invoice that is being cancelled, was cancelled, was refused or must be adjusted never has its link sent to (or
/// confirmed on) a marketplace, and a package whose link is in removal is never sent again.
/// </summary>
public static class InvoiceDeliveryGuardPolicy
{
    public const string InvoiceNotDeliverableCode = "INVOICE_DELIVERY_INVOICE_NOT_DELIVERABLE";
    public const string LinkRevocationOpenCode = "INVOICE_DELIVERY_LINK_REVOCATION_OPEN";
    public const string CancelledJobCode = "INVOICE_DELIVERY_CANCELLED_BY_INVOICE_STATE";

    public static bool InvoiceBlocksDelivery(InvoiceStatus status) => status is InvoiceStatus.Cancelled
        or InvoiceStatus.CancellationPending or InvoiceStatus.CancellationRejected or InvoiceStatus.AdjustmentRequired
        or InvoiceStatus.CancelledLocal or InvoiceStatus.Rejected;

    public static bool StateBlocksDelivery(string? deliveryStatus) =>
        MarketplaceInvoiceLinkRevocationPolicy.IsOpen(deliveryStatus)
        || string.Equals(deliveryStatus, MarketplaceInvoiceLinkRevocationPolicy.Removed, StringComparison.Ordinal);

    /// <summary>
    /// The invoice status a confirmed delivery leads to (Completed once every target package is confirmed, otherwise
    /// MarketplacePending), when the state machine allows it: directly, or through MarketplacePending for a failed
    /// delivery that a later read-back confirms. A delivery under review of an invoice the provider holds may also be
    /// confirmed by such a read-back. Null leaves the invoice as it is.
    /// </summary>
    public static InvoiceStatus? OutcomeStatus(InvoiceStatus current, bool allTargetsConfirmed, bool issuedAtProvider)
    {
        if (InvoiceBlocksDelivery(current)) return null;
        var target = allTargetsConfirmed ? InvoiceStatus.Completed : InvoiceStatus.MarketplacePending;
        if (InvoiceStateMachine.CanTransition(current, target)) return target;
        if (InvoiceStateMachine.CanTransition(current, InvoiceStatus.MarketplacePending)
            && InvoiceStateMachine.CanTransition(InvoiceStatus.MarketplacePending, target)) return target;
        return current == InvoiceStatus.ManualReview && issuedAtProvider ? target : null;
    }
}

public static class MarketplaceInvoiceDeliveryEvidencePolicy
{
    public static bool HasConfirmedDelivery(string? currentStateStatus, bool hasConfirmedHistoricalAttempt) =>
        string.Equals(currentStateStatus, "CONFIRMED", StringComparison.Ordinal)
        || currentStateStatus is null && hasConfirmedHistoricalAttempt;

    public static bool AllTargetsConfirmed(IEnumerable<Guid> targetPackageIds, IEnumerable<Guid> confirmedPackageIds)
    {
        var targets = targetPackageIds.ToHashSet();
        if (targets.Count == 0) return false;
        var confirmed = confirmedPackageIds.ToHashSet();
        return targets.IsSubsetOf(confirmed);
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

    // Removes the invoice link a cancelled invoice left on a package. Only a marketplace with a documented
    // delete endpoint overrides this (Trendyol); the others keep the manual step in the seller panel.
    Task<AdapterResult<InvoiceLinkRevocationResult>> RevokeLinkAsync(AdapterContext context, InvoiceLinkRevocationCommand command, CancellationToken cancellationToken) =>
        Task.FromResult(AdapterResult<InvoiceLinkRevocationResult>.Failure(new(AdapterErrorClass.NotSupported, MarketplaceInvoiceLinkRevocationPolicy.UnsupportedCode,
            "Bu pazaryeri fatura linkini silme servisi sunmuyor; link satıcı panelinden kaldırılmalıdır.", null, null, null)));
}

// The links and the number this panel sent for the invoice: a package link is removed only when it is one of them.
public sealed record InvoiceLinkRevocationCommand(ExternalInvoiceDeliveryReference Reference, IReadOnlyList<string> ExpectedInvoiceLinks, string? ExpectedInvoiceNumber);
// AlreadyAbsent: the package held no invoice before the request, so nothing was sent.
public sealed record InvoiceLinkRevocationResult(string ExternalReference, bool AlreadyAbsent, string ObservedRawStatus);

/// <summary>
/// What a delivery state left behind by a cancelled invoice can do. AUTOMATIC: the marketplace documents a delete
/// endpoint and the panel removes the link on the operator's request (Trendyol, "Fatura Linki Silme":
/// https://developers.trendyol.com/v2.0/docs/delete-customer-invoice-link). VERIFY_ONLY: there is no delete endpoint
/// but the package can be read back, so the operator removes the link in the seller panel and the panel checks it
/// (Hepsiburada: only PUT .../invoice exists, 409 when the package already has an invoice). MANUAL: neither (n11's
/// SaveLinkSellerInvoice only replaces the link, Pazarama's order/invoice-link only updates it; no delete, no read-back).
/// </summary>
public static class MarketplaceInvoiceLinkRevocationPolicy
{
    public const string Required = "REVOCATION_REQUIRED";
    public const string Started = "REVOCATION_STARTED";
    public const string Unknown = "REVOCATION_UNKNOWN";
    public const string Removed = "LINK_REMOVED";
    public const string Automatic = "AUTOMATIC";
    public const string VerifyOnly = "VERIFY_ONLY";
    public const string Manual = "MANUAL";
    public const string UnsupportedCode = "INVOICE_LINK_REVOCATION_UNSUPPORTED";
    public const string ReadbackPendingCode = "INVOICE_LINK_REVOCATION_READBACK_PENDING";
    public const string NotOursCode = "INVOICE_LINK_NOT_FROM_THIS_INVOICE";
    // A request that never came back (process stopped mid-call) may be started again after this long; the adapter
    // reads the package first, so a link that is already gone is not deleted twice.
    public static readonly TimeSpan StaleStart = TimeSpan.FromMinutes(5);

    public static bool IsOpen(string? status) => status is Required or Started or Unknown;

    public static bool CanStart(string? status, DateTimeOffset updatedAt, DateTimeOffset now) =>
        status is Required or Unknown || (status == Started && now - updatedAt >= StaleStart);

    public static string ModeFor(string? platformCode) => platformCode?.Trim().ToUpperInvariant() switch
    {
        "TRENDYOL" => Automatic,
        "HEPSIBURADA" => VerifyOnly,
        _ => Manual
    };

    // A timeout, a server error, a rate limit or an accepted request whose effect is not yet visible: the link may be
    // gone or not, so the state waits for the next attempt (which reads first) instead of going back to "required".
    public static bool LeavesOutcomeUnknown(AdapterError error) =>
        error.Class is AdapterErrorClass.TransientNetwork or AdapterErrorClass.Remote5xx or AdapterErrorClass.RateLimit
        || string.Equals(error.Code, ReadbackPendingCode, StringComparison.Ordinal);

    public static string Instructions(string? platformCode, string? packageNumber)
    {
        var package = string.IsNullOrWhiteSpace(packageNumber) ? "ilgili" : packageNumber.Trim();
        return platformCode?.Trim().ToUpperInvariant() switch
        {
            "TRENDYOL" => $"Trendyol fatura linkini silme servisi sunuyor. “Fatura Linkini Kaldır” {package} paketindeki linki siler ve paketi yeniden okuyarak kalktığını doğrular. Servis çalışmazsa Trendyol Satıcı Paneli’nde siparişi açıp faturayı silin.",
            "HEPSIBURADA" => $"Hepsiburada gönderilen fatura linkini silme veya değiştirme servisi sunmuyor. Hepsiburada Satıcı Paneli’nde {package} paketinin faturasını kaldırın; panelde bu seçenek yoksa Hepsiburada’ya satıcı destek talebi açıp faturanın silinmesini isteyin. Sonra “Kaldırıldığını Kontrol Et” ile paneli güncelleyin.",
            "N11" => "n11 fatura linkini silme servisi sunmuyor. Siparişte tek aktif link olur; aynı siparişe yeni fatura linki gönderildiğinde eskisinin yerine geçer. Yeni fatura kesilmeyecekse linkin kaldırılmasını n11 satıcı desteğinden isteyin.",
            "PTTAVM" => "PTT AVM fatura linkini silme servisi sunmuyor. Yeni fatura kesilmeyecekse linkin kaldırılmasını PTT AVM satıcı panelinden veya satıcı desteğinden isteyin.",
            "PAZARAMA" => "Pazarama fatura linkini silme servisi sunmuyor; link yalnız yeni bir linkle güncellenebilir. Yeni fatura kesilmeyecekse linkin kaldırılmasını Pazarama satıcı desteğinden isteyin.",
            _ => "Bu pazaryeri için fatura linkini kaldırma servisi yok. Linki pazaryerinin satıcı panelinden kaldırın."
        };
    }
}

public interface IHepsiburadaInvoiceStatusPort
{
    Task<AdapterResult<AdapterPageResult<RemoteMissingInvoicePackage>>> ListMissingInvoicePackagesAsync(AdapterContext context, AdapterPageRequest page, CancellationToken cancellationToken);

    // Hepsiburada's cancelled-order list (the last month, cancellation date filter, 50 a page).
    Task<AdapterResult<AdapterPageResult<RemoteCancelledOrderLine>>> ListCancelledOrderLinesAsync(AdapterContext context, DateTimeOffset since, DateTimeOffset until, AdapterPageRequest page, CancellationToken cancellationToken) =>
        Task.FromResult(AdapterResult<AdapterPageResult<RemoteCancelledOrderLine>>.Failure(new(AdapterErrorClass.NotSupported, "CANCELLED_ORDER_LIST_UNSUPPORTED", "Bu pazaryeri iptal edilen sipariş listesi vermiyor.", null, null, null)));
}

public sealed record RemoteCancelledOrderLine(string OrderNumber, string? LineItemId, DateTimeOffset? CancelledAt);

public sealed record InvoicePolicyView(Guid Id, Guid ProviderConnectionId, string TriggerState, string PackageScope, string DueRule, string RoundingRule, string AdjustmentRule, bool AutoSubmit, long Version);
public sealed record UpsertInvoicePolicyCommand(string TriggerState, string PackageScope, string DueRule, string RoundingRule, string AdjustmentRule, bool AutoSubmit);
public sealed record CreateInvoiceCommand(Guid OrderId, Guid? PackageId, Guid ProviderConnectionId, Guid? OriginalInvoiceId, bool IncludeInternetSalesInfo = true);
public sealed record InvoiceWorkspacePreviewRequest(IReadOnlyList<InvoiceWorkspacePreviewTarget> Items, bool IncludeInternetSalesInfo = true);
public sealed record InvoiceWorkspacePreviewTarget(Guid OrderId, Guid PackageId, Guid ProviderConnectionId);
public sealed record InvoiceWorkspacePreviewConfirmRequest(IReadOnlyList<InvoiceWorkspacePreviewConfirmation> Items, bool IncludeInternetSalesInfo = true);
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
    string? NextAction,
    bool IncludeInternetSalesInfo = true,
    IReadOnlyList<string>? TargetPackageNumbers = null,
    string? DeliveryCarrierName = null,
    DateTimeOffset? DeliverySentAt = null,
    string? DeliveryPackageNumber = null);
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
    string Environment = "UNKNOWN",
    string? MarketplaceInvoiceUrl = null,
    Guid? ManualInvoiceDocumentId = null,
    string? ManualInvoiceDocumentType = null,
    string? ManualInvoiceDocumentUrl = null,
    string? ManualInvoiceNumber = null,
    DateTimeOffset? ManualInvoiceIssuedAt = null,
    string? ManualInvoiceDeliveryStatus = null,
    string? ManualInvoiceDeliveryErrorCode = null,
    string FiscalStatus = "NONE",
    string PlatformInvoiceStatus = "UNKNOWN",
    string DeliveryStatus = "NOT_SENT",
    string? CurrentPlatformInvoiceUrl = null,
    DateTimeOffset? LastSuccessfulCheckAt = null,
    IReadOnlyList<string>? AllowedActions = null,
    IReadOnlyList<MarketplaceInvoiceReferenceHistoryView>? PreviousPlatformInvoiceReferences = null,
    Guid? MarketplaceConnectionId = null,
    string? ExternalOrderId = null,
    string? ExternalPackageNumber = null);

public sealed record MarketplaceInvoiceReferenceHistoryView(
    string? InvoiceNumber,
    string? InvoiceUrl,
    string ChangeReason,
    DateTimeOffset ChangedAt);

public static class InvoiceWorkspaceReadIssuePolicy
{
    // Trendyol removes older packages from its package lookup. The worker
    // intentionally preserves the last known invoice state for those records,
    // so a 404 for a historical package is not an actionable workspace error.
    public static bool ShouldDisplay(string? code) =>
        !string.Equals(code?.Trim(), "REMOTE_PACKAGE_NOT_FOUND", StringComparison.OrdinalIgnoreCase);
}

public static class InvoiceWorkspaceActionPolicy
{
    public static IReadOnlyList<string> Resolve(
        string platformCode,
        MarketplaceInvoiceStatus platformStatus,
        bool hasFiscalInvoice,
        InvoiceStatus? fiscalStatus,
        bool fiscalDocumentAvailable,
        bool hasManualDocument,
        bool canCreateInvoice,
        bool hasReadError)
    {
        var actions = new List<string> { "CHECK_PLATFORM" };
        if (platformStatus != MarketplaceInvoiceStatus.NotInvoiced || hasReadError) return actions;

        if (hasFiscalInvoice || hasManualDocument)
        {
            var fiscalAccepted = fiscalStatus is InvoiceStatus.Accepted or InvoiceStatus.Completed or InvoiceStatus.MarketplaceFailed;
            if (hasManualDocument || fiscalAccepted && fiscalDocumentAvailable)
                actions.Add("DELIVER_EXISTING");
            return actions;
        }

        if (canCreateInvoice && (platformCode is "TRENDYOL" or "HEPSIBURADA" or "SHOPIFY"))
            actions.Add("CREATE_FISCAL_INVOICE");
        return actions;
    }
}

public sealed record InvoiceWorkspaceLineView(string Sku, string? Barcode, string Description, decimal Quantity, decimal UnitPrice, decimal VatRate, string? ImageUrl);
public sealed record InvoiceWorkspaceSummaryView(int DueSoonCount, int UninvoicedCount = 0);
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
    bool ProviderHasCredential = false,
    string? Sort = null);
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
    IReadOnlyList<string> InvoiceStatuses,
    int UnverifiedCount = 0);
public sealed record InvoiceLineView(Guid Id, int LineSequence, string Description, string? Sku, string Unit, decimal Quantity, decimal UnitPrice, decimal DiscountAmount, decimal VatRate, decimal VatAmount, decimal LineTotal);
public sealed record InvoiceDocumentView(Guid Id, string DocumentType, string Sha256, DateTimeOffset CreatedAt);
public sealed record InvoiceAttemptView(int AttemptNumber, string Outcome, string? ErrorCode, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt);
public sealed record MarketplaceDeliveryView(Guid Id, string DeliveryType, string Status, string? ExternalReference, string? ErrorCode, DateTimeOffset CreatedAt);
public sealed record InvoiceDetailView(Guid Id, Guid OrderId, string OrderNumber, Guid? PackageId, Guid ProviderConnectionId, string InvoiceType, string SequencePurpose, string Status, string Currency, decimal TaxExclusiveTotal, decimal DiscountTotal, decimal TaxTotal, decimal PayableTotal, string Note, string? InvoiceNumber, string? EttnUuid, DateTimeOffset? DueAt, DateTimeOffset? IssuedAt, string? LastErrorCode, IReadOnlyList<InvoiceLineView> Lines, IReadOnlyList<InvoiceDocumentView> Documents, IReadOnlyList<InvoiceAttemptView> Attempts, IReadOnlyList<MarketplaceDeliveryView> Deliveries, IReadOnlyList<string> AllowedActions, long Version, bool RequiresSensitiveConfirmation, IReadOnlyList<MarketplaceLinkRevocationView>? LinkRevocations = null);
// A marketplace link a cancelled invoice left behind. Mode is AUTOMATIC, VERIFY_ONLY or MANUAL
// (MarketplaceInvoiceLinkRevocationPolicy); Instructions tell the operator what to do on the marketplace.
public sealed record MarketplaceLinkRevocationView(Guid DeliveryStateId, Guid? PackageId, string PlatformCode, string PlatformName, string? PackageNumber, string Status, string? ErrorCode, string Mode, string Instructions, bool CanAct, string? BlockedReason, DateTimeOffset UpdatedAt);

public interface IInvoicingBillingService
{
    Task<ServiceResult<InvoicePolicyView>> GetPolicyAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken);
    Task<ServiceResult<InvoicePolicyView>> UpsertPolicyAsync(Guid tenantId, Guid connectionId, long? expectedVersion, UpsertInvoicePolicyCommand command, CancellationToken cancellationToken);
    Task<PageResult<InvoiceListView>> ListAsync(Guid tenantId, int limit, string? after, string? status, CancellationToken cancellationToken);
    Task<IReadOnlyList<InvoiceWorkspaceItemView>> WorkspaceAsync(Guid tenantId, CancellationToken cancellationToken);
    Task<InvoiceWorkspacePageView> WorkspacePageAsync(Guid tenantId, InvoiceWorkspacePageQuery query, CancellationToken cancellationToken);
    Task<InvoiceWorkspaceSummaryView> WorkspaceSummaryAsync(Guid tenantId, CancellationToken cancellationToken);
    Task<ServiceResult<bool>> ConfirmMarketplaceDeliveredAsync(Guid tenantId, Guid orderId, string correlationId, CancellationToken cancellationToken);
    Task<ServiceResult<bool>> ReplaceCancelledInvoiceAsync(Guid tenantId, Guid orderId, string correlationId, CancellationToken cancellationToken);
    Task<ServiceResult<bool>> ConfirmNoRemoteInvoiceAsync(Guid tenantId, Guid orderId, string correlationId, CancellationToken cancellationToken);
    Task<ServiceResult<Guid>> EnqueueOrderInvoiceReconciliationAsync(Guid tenantId, Guid orderId, string idempotencyKey, string correlationId, CancellationToken cancellationToken);
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
    // Operator-triggered only: removes (Trendyol) or verifies the removal of (Hepsiburada) the link a cancelled invoice left on a package.
    Task<ServiceResult<MarketplaceLinkRevocationView>> RevokeMarketplaceLinkAsync(Guid tenantId, Guid invoiceId, Guid deliveryStateId, string idempotencyKey, string correlationId, CancellationToken cancellationToken);
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
