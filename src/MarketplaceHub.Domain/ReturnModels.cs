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

public enum ReturnStockDispositionKind { Pass, Quarantine, Damaged, NotReceived }

public static class ReturnClaimStateMachine
{
    private static readonly IReadOnlyDictionary<ReturnClaimStatus, ReturnClaimStatus[]> Allowed =
        new Dictionary<ReturnClaimStatus, ReturnClaimStatus[]>
        {
            [ReturnClaimStatus.Requested] = [ReturnClaimStatus.AwaitingShipment, ReturnClaimStatus.InTransit, ReturnClaimStatus.ActionRequired, ReturnClaimStatus.Approved, ReturnClaimStatus.Rejected, ReturnClaimStatus.Cancelled],
            [ReturnClaimStatus.AwaitingShipment] = [ReturnClaimStatus.InTransit, ReturnClaimStatus.Cancelled],
            [ReturnClaimStatus.InTransit] = [ReturnClaimStatus.ActionRequired, ReturnClaimStatus.Approved, ReturnClaimStatus.Disputed],
            [ReturnClaimStatus.ActionRequired] = [ReturnClaimStatus.Approved, ReturnClaimStatus.Rejected, ReturnClaimStatus.Disputed],
            [ReturnClaimStatus.Approved] = [ReturnClaimStatus.Disputed, ReturnClaimStatus.Completed],
            [ReturnClaimStatus.Rejected] = [ReturnClaimStatus.Disputed, ReturnClaimStatus.Completed],
            [ReturnClaimStatus.Disputed] = [ReturnClaimStatus.Approved, ReturnClaimStatus.Rejected, ReturnClaimStatus.Completed]
        };

    public static bool CanTransition(ReturnClaimStatus current, ReturnClaimStatus next) =>
        current == next || Allowed.TryGetValue(current, out var allowed) && allowed.Contains(next);
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
    public string? ReasonCode { get; set; }
    public string? ReasonText { get; set; }
    public DateTimeOffset? ActionDueAt { get; set; }
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
