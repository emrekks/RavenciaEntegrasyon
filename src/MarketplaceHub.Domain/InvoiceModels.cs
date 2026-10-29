namespace MarketplaceHub.Domain;

public enum InvoiceStatus
{
    Draft,
    Validating,
    ValidationFailed,
    Ready,
    Submitting,
    UnknownResult,
    Submitted,
    Accepted,
    Rejected,
    MarketplacePending,
    MarketplaceFailed,
    Completed,
    CancellationPending,
    Cancelled,
    CancellationRejected,
    AdjustmentRequired,
    ManualReview,
    CancelledLocal
}

public static class InvoiceStateMachine
{
    private static readonly IReadOnlyDictionary<InvoiceStatus, InvoiceStatus[]> Allowed =
        new Dictionary<InvoiceStatus, InvoiceStatus[]>
        {
            [InvoiceStatus.Draft] = [InvoiceStatus.Validating, InvoiceStatus.CancelledLocal],
            [InvoiceStatus.Validating] = [InvoiceStatus.Ready, InvoiceStatus.ValidationFailed],
            [InvoiceStatus.ValidationFailed] = [InvoiceStatus.Validating, InvoiceStatus.ManualReview],
            [InvoiceStatus.Ready] = [InvoiceStatus.Submitting],
            [InvoiceStatus.Submitting] = [InvoiceStatus.Submitted, InvoiceStatus.Rejected, InvoiceStatus.UnknownResult],
            [InvoiceStatus.UnknownResult] = [InvoiceStatus.Submitted, InvoiceStatus.Rejected, InvoiceStatus.ManualReview],
            [InvoiceStatus.Submitted] = [InvoiceStatus.Accepted, InvoiceStatus.Rejected, InvoiceStatus.MarketplacePending, InvoiceStatus.Completed, InvoiceStatus.MarketplaceFailed],
            [InvoiceStatus.Accepted] = [InvoiceStatus.MarketplacePending, InvoiceStatus.CancellationPending, InvoiceStatus.Completed, InvoiceStatus.MarketplaceFailed, InvoiceStatus.AdjustmentRequired],
            [InvoiceStatus.MarketplacePending] = [InvoiceStatus.Completed, InvoiceStatus.MarketplaceFailed, InvoiceStatus.ManualReview, InvoiceStatus.AdjustmentRequired],
            [InvoiceStatus.MarketplaceFailed] = [InvoiceStatus.MarketplacePending, InvoiceStatus.ManualReview],
            [InvoiceStatus.Completed] = [InvoiceStatus.CancellationPending, InvoiceStatus.AdjustmentRequired, InvoiceStatus.MarketplaceFailed, InvoiceStatus.ManualReview],
            // The order was cancelled or a return was accepted after the sale invoice: the e-Archive is cancelled
            // (or a return invoice is issued outside the panel) after review.
            [InvoiceStatus.AdjustmentRequired] = [InvoiceStatus.CancellationPending, InvoiceStatus.ManualReview],
            [InvoiceStatus.CancellationPending] = [InvoiceStatus.Cancelled, InvoiceStatus.CancellationRejected],
            // A refused cancellation can be requested again from the panel.
            [InvoiceStatus.CancellationRejected] = [InvoiceStatus.CancellationPending],
            [InvoiceStatus.ManualReview] = [InvoiceStatus.Submitting]
        };

    public static bool CanTransition(InvoiceStatus current, InvoiceStatus next) =>
        current == next || Allowed.TryGetValue(current, out var allowed) && allowed.Contains(next);
}

public sealed class LegalEntityProfile
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public required string Title { get; set; }
    public required string ProtectedTaxId { get; set; }
    public required string MaskedTaxId { get; set; }
    public required string AddressSnapshotJson { get; set; }
    public required string ContactSnapshotJson { get; set; }
    public required string Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Version { get; set; } = 1;
}

public sealed class InvoicePolicy
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ProviderConnectionId { get; set; }
    public required string TriggerState { get; set; }
    public required string PackageScope { get; set; }
    public required string DueRule { get; set; }
    public required string RoundingRule { get; set; }
    public required string AdjustmentRule { get; set; }
    public bool AutoSubmit { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Version { get; set; } = 1;
}

public sealed class Invoice
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid OrderId { get; set; }
    public Guid? PackageId { get; set; }
    // New sale invoices claim one durable fiscal slot for the whole order.
    // Legacy duplicates stay unguarded and are blocked by application review.
    public bool OrderFiscalGuarded { get; set; }
    public Guid ProviderConnectionId { get; set; }
    public Guid LegalEntityProfileId { get; set; }
    public Guid InvoicePolicyId { get; set; }
    public required string InvoiceType { get; set; }
    public required string SequencePurpose { get; set; }
    public long? SequenceNumber { get; set; }
    public InvoiceStatus Status { get; set; } = InvoiceStatus.Draft;
    public required string Currency { get; set; }
    public decimal TaxExclusiveTotal { get; set; }
    public decimal DiscountTotal { get; set; }
    public decimal TaxTotal { get; set; }
    public decimal PayableTotal { get; set; }
    public required string Note { get; set; }
    public bool IncludeInternetSalesInfo { get; set; } = true;
    public required string IdempotencyKey { get; set; }
    public string? ExternalReference { get; set; }
    public string? InvoiceNumber { get; set; }
    public string? EttnUuid { get; set; }
    public DateTimeOffset? DueAt { get; set; }
    public DateTimeOffset? IssuedAt { get; set; }
    public Guid? OriginalInvoiceId { get; set; }
    public string? LastErrorCode { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Version { get; set; } = 1;
}

public sealed class InvoiceLine
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid InvoiceId { get; set; }
    public Guid? OrderLineId { get; set; }
    public int LineSequence { get; set; }
    public required string DescriptionSnapshot { get; set; }
    public string? SkuSnapshot { get; set; }
    public required string UnitSnapshot { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal DiscountAmount { get; set; }
    public decimal VatRate { get; set; }
    public decimal VatAmount { get; set; }
    public decimal LineTotal { get; set; }
}

public sealed class InvoicePartySnapshot
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid InvoiceId { get; set; }
    public required string Role { get; set; }
    public required string ProtectedContent { get; set; }
    public required string ContentHash { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class InvoiceDocument
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid InvoiceId { get; set; }
    public required string DocumentType { get; set; }
    public Guid FileAssetId { get; set; }
    public required string Sha256 { get; set; }
    public string? ExternalDocumentId { get; set; }
    public string? PermanentUrl { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// A document supplied by the operator for an order. It deliberately has no
/// dependency on a fiscal provider connection or a fiscal Invoice row.
/// Uploading it is local-only; delivery is a separate, explicit action.
/// </summary>
public sealed class ManualOrderInvoiceDocument
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid OrderId { get; set; }
    public Guid? PackageId { get; set; }
    public Guid ConnectionId { get; set; }
    public string? InvoiceNumber { get; set; }
    public DateTimeOffset? IssuedAt { get; set; }
    public required string IdempotencyKey { get; set; }
    public required string DocumentType { get; set; }
    public required string DocumentUrl { get; set; }
    public Guid? FileAssetId { get; set; }
    public string? Sha256 { get; set; }
    public required string Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Version { get; set; } = 1;
}

public sealed class ManualOrderInvoiceDeliveryState
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid DocumentId { get; set; }
    public Guid ConnectionId { get; set; }
    public Guid PackageId { get; set; }
    public required string IdempotencyKey { get; set; }
    public required string RequestHash { get; set; }
    public required string Status { get; set; }
    public int AttemptCount { get; set; }
    public string? ExternalReference { get; set; }
    public string? ErrorCode { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public long Version { get; set; } = 1;
}

public sealed class InvoiceSubmissionAttempt
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid InvoiceId { get; set; }
    public int AttemptNumber { get; set; }
    public required string RequestHash { get; set; }
    public required string Outcome { get; set; }
    public string? ErrorClass { get; set; }
    public string? ErrorCode { get; set; }
    public string? RemoteRequestId { get; set; }
    public string? ExternalReference { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

public sealed class MarketplaceDelivery
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid InvoiceId { get; set; }
    public Guid ConnectionId { get; set; }
    public Guid? PackageId { get; set; }
    public int AttemptNumber { get; set; }
    public required string IdempotencyKey { get; set; }
    public string? ExternalIdempotencyKey { get; set; }
    public required string RequestHash { get; set; }
    public required string DeliveryType { get; set; }
    public required string Status { get; set; }
    public string? ExternalReference { get; set; }
    public string? ErrorCode { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

public sealed class MarketplaceDeliveryState
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid InvoiceId { get; set; }
    public Guid ConnectionId { get; set; }
    public Guid? PackageId { get; set; }
    public int AttemptNumber { get; set; }
    public required string ExternalIdempotencyKey { get; set; }
    public required string RequestHash { get; set; }
    public required string DeliveryType { get; set; }
    public required string Status { get; set; }
    public string? ExternalReference { get; set; }
    public string? ErrorCode { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public long Version { get; set; } = 1;
}
