namespace MarketplaceHub.Domain;

public enum ReturnClaimStatus
{
    Requested,
    AwaitingShipment,
    InTransit,
    ActionRequired,
    Approved,
    Rejected,
    Disputed,
    Completed,
    Cancelled
}

public static class MarketplaceReturnStatus
{
    public static ReturnClaimStatus Canonicalize(string? rawStatus, string? cargoTrackingLink = null)
    {
        var normalized = string.Concat((rawStatus ?? string.Empty).Where(char.IsLetterOrDigit)).ToUpperInvariant();
        return normalized switch
        {
            "CREATED" when !string.IsNullOrWhiteSpace(cargoTrackingLink) => ReturnClaimStatus.InTransit,
            "CREATED" or "NEWREQUEST" => ReturnClaimStatus.Requested,
            "AWAITINGPREAPPROVAL" or "WAITINGINACTION" or "AWAITINGACTION" or "INANALYSIS" or "WAITINGFRAUDCHECK" or "WAITINGFORSELLERACTION" => ReturnClaimStatus.ActionRequired,
            "WAITINGFORSHIPMENT" => ReturnClaimStatus.AwaitingShipment,
            "WAITINGINCARGO" or "INTRANSIT" or "RETURNINTRANSIT" or "SHIPPED" => ReturnClaimStatus.InTransit,
            "ACCEPTED" or "APPROVED" => ReturnClaimStatus.Approved,
            "REJECTED" => ReturnClaimStatus.Rejected,
            "UNRESOLVED" or "INDISPUTE" or "DISPUTED" => ReturnClaimStatus.Disputed,
            "COMPLETED" or "REFUNDED" => ReturnClaimStatus.Completed,
            "CANCELLED" or "CANCELED" => ReturnClaimStatus.Cancelled,
            // Shopify ReturnStatus (only Shopify reports these words for returns): OPEN is an approved return in progress,
            // CLOSED a finished one, DECLINED a refused request. REQUESTED still waits for the seller (default below).
            "OPEN" => ReturnClaimStatus.Approved,
            "CLOSED" => ReturnClaimStatus.Completed,
            "DECLINED" => ReturnClaimStatus.Rejected,
            _ => ReturnClaimStatus.ActionRequired
        };
    }
}

public enum ReturnStockDispositionKind { Pass, Quarantine, Damaged, NotReceived }

public static class ReturnLineStatusPolicy
{
    // A line without a known status is treated as actionable so older rows keep working.
    public static bool IsActionable(string? rawStatus) =>
        string.IsNullOrWhiteSpace(rawStatus) || MarketplaceReturnStatus.Canonicalize(rawStatus) == ReturnClaimStatus.ActionRequired;

    // Per-line statuses decide the claim only while some line still waits for the seller; otherwise the
    // claim-level status the marketplace reported stands.
    public static ReturnClaimStatus Resolve(ReturnClaimStatus claimLevel, IReadOnlyCollection<string?> lineStatuses)
    {
        var known = lineStatuses.Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
        if (known.Count == 0 || known.Count != lineStatuses.Count) return claimLevel;
        return known.Any(x => MarketplaceReturnStatus.Canonicalize(x) == ReturnClaimStatus.ActionRequired) ? ReturnClaimStatus.ActionRequired : claimLevel;
    }
}

public static class ReturnClaimStateMachine
{
    private static readonly IReadOnlyDictionary<ReturnClaimStatus, ReturnClaimStatus[]> Allowed =
        new Dictionary<ReturnClaimStatus, ReturnClaimStatus[]>
        {
            // A marketplace can accept and refund in one step (Hepsiburada lists the claim straight under Refunded),
            // so an open claim may close as Completed without passing through Approved.
            [ReturnClaimStatus.Requested] = [ReturnClaimStatus.AwaitingShipment, ReturnClaimStatus.InTransit, ReturnClaimStatus.ActionRequired, ReturnClaimStatus.Approved, ReturnClaimStatus.Rejected, ReturnClaimStatus.Cancelled, ReturnClaimStatus.Completed],
            [ReturnClaimStatus.AwaitingShipment] = [ReturnClaimStatus.InTransit, ReturnClaimStatus.ActionRequired, ReturnClaimStatus.Cancelled, ReturnClaimStatus.Completed],
            [ReturnClaimStatus.InTransit] = [ReturnClaimStatus.ActionRequired, ReturnClaimStatus.Approved, ReturnClaimStatus.Disputed, ReturnClaimStatus.Completed],
            [ReturnClaimStatus.ActionRequired] = [ReturnClaimStatus.AwaitingShipment, ReturnClaimStatus.Approved, ReturnClaimStatus.Rejected, ReturnClaimStatus.Disputed, ReturnClaimStatus.Completed],
            [ReturnClaimStatus.Approved] = [ReturnClaimStatus.Disputed, ReturnClaimStatus.Completed],
            [ReturnClaimStatus.Rejected] = [ReturnClaimStatus.Disputed, ReturnClaimStatus.Completed],
            [ReturnClaimStatus.Disputed] = [ReturnClaimStatus.Approved, ReturnClaimStatus.Rejected, ReturnClaimStatus.Completed]
        };

    public static bool CanTransition(ReturnClaimStatus current, ReturnClaimStatus next) =>
        current == next || Allowed.TryGetValue(current, out var allowed) && allowed.Contains(next);

    // The marketplace decides a claim's outcome: a newer Approved, Rejected or
    // Completed applies even where the local workflow would not reach it (a
    // claim approved here and rejected there). Completed or cancelled claims
    // are not reopened.
    public static bool IsAuthoritativeRemoteOutcome(ReturnClaimStatus current, ReturnClaimStatus next) =>
        current != next
        && next is ReturnClaimStatus.Approved or ReturnClaimStatus.Rejected or ReturnClaimStatus.Completed
        && current is not (ReturnClaimStatus.Completed or ReturnClaimStatus.Cancelled);
}

public static class ReturnClaimDecisionPolicy
{
    public static ReturnClaimStatus StatusAfterLineDecision(ReturnClaimStatus current, string action, int selectedLineCount, int totalLineCount)
    {
        if (totalLineCount <= 0 || selectedLineCount < totalLineCount) return current;
        return action.Trim().ToUpperInvariant() switch
        {
            "APPROVE" => ReturnClaimStatus.Approved,
            "REJECT" => ReturnClaimStatus.Rejected,
            "PREAPPROVAL_CONFIRM" => ReturnClaimStatus.AwaitingShipment,
            _ => current
        };
    }
}

public static class OpenReturnLifecyclePolicy
{
    public static bool ShouldPoll(ReturnClaimStatus status) =>
        status is not ReturnClaimStatus.Completed and not ReturnClaimStatus.Cancelled;
}

public static class ReturnClaimStoragePolicy
{
    public static bool ShouldPersist(ReturnClaimStatus status) => status != ReturnClaimStatus.Cancelled;
}

public sealed class ReturnClaim
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ConnectionId { get; set; }
    public Guid OrderId { get; set; }
    public required string ExternalClaimId { get; set; }
    public ReturnClaimStatus Status { get; set; }
    public required string RawStatus { get; set; }
    public string? CargoProviderName { get; set; }
    public string? CargoTrackingNumber { get; set; }
    public string? CargoTrackingLink { get; set; }
    public string? ReasonCode { get; set; }
    public string? ReasonText { get; set; }
    public DateTimeOffset? ActionDueAt { get; set; }
    // True when the marketplace sent no deadline and ActionDueAt was derived
    // from its usual rule (Trendyol: 48 hours after WaitingInAction, a week
    // after the claim); shown as an estimate, never as the marketplace's date.
    public bool ActionDueAtEstimated { get; set; }
    public DateTimeOffset LastRemoteModifiedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Version { get; set; } = 1;
}

public sealed class ReturnLine
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ClaimId { get; set; }
    public Guid OrderLineId { get; set; }
    public required string ExternalLineId { get; set; }
    public decimal Quantity { get; set; }
    // The marketplace's own per-line status (Trendyol claimItemStatus). One claim can mix lines that
    // are already accepted with lines that still wait for the seller.
    public string? RawStatus { get; set; }
}

public sealed class ReturnDecision
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ClaimId { get; set; }
    public required string Action { get; set; }
    public string? ReasonCode { get; set; }
    public string? Explanation { get; set; }
    public required string IdempotencyKey { get; set; }
    public required string Status { get; set; }
    public string? ExternalOperationId { get; set; }
    public string? ErrorCode { get; set; }
    public Guid ActorUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

public sealed class ReturnEvidence
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ClaimId { get; set; }
    public Guid DecisionId { get; set; }
    public Guid FileAssetId { get; set; }
    public required string EvidenceKind { get; set; }
    public required string Checksum { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class ReturnStockDisposition
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ClaimId { get; set; }
    public Guid ReturnLineId { get; set; }
    public Guid InventoryItemId { get; set; }
    public ReturnStockDispositionKind Disposition { get; set; }
    public decimal Quantity { get; set; }
    public required string IdempotencyKey { get; set; }
    public required string Reason { get; set; }
    public Guid ActorUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
