namespace MarketplaceHub.Domain;

public enum ImportSourceType { Marketplace, Csv, Xlsx }
public enum ImportSessionStatus { Created, Fetching, Matching, ReviewRequired, ReadyToApply, Applying, Completed, PartiallyCompleted, Failed, Cancelled }
public enum ImportDecisionKind { Create, Link, Skip }

public static class ImportStateMachine
{
    public static bool CanTransition(ImportSessionStatus from, ImportSessionStatus to) => (from, to) switch
    {
        (ImportSessionStatus.Created, ImportSessionStatus.Fetching or ImportSessionStatus.Cancelled) => true,
        (ImportSessionStatus.Fetching, ImportSessionStatus.Matching or ImportSessionStatus.Failed or ImportSessionStatus.Cancelled) => true,
        (ImportSessionStatus.Matching, ImportSessionStatus.ReviewRequired or ImportSessionStatus.ReadyToApply or ImportSessionStatus.Failed) => true,
        (ImportSessionStatus.ReviewRequired, ImportSessionStatus.ReadyToApply or ImportSessionStatus.Cancelled) => true,
        (ImportSessionStatus.ReadyToApply, ImportSessionStatus.Applying or ImportSessionStatus.Cancelled) => true,
        (ImportSessionStatus.Applying, ImportSessionStatus.Completed or ImportSessionStatus.PartiallyCompleted or ImportSessionStatus.Failed) => true,
        _ => false
    };

    public static void Transition(ImportSession session, ImportSessionStatus to)
    {
        if (!CanTransition(session.Status, to)) throw new InvalidOperationException($"Import transition {session.Status} -> {to} is not allowed.");
        session.Status = to;
    }
}

public sealed class ImportSession
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public ImportSourceType SourceType { get; set; }
    public Guid? ConnectionId { get; set; }
    public Guid? SourceAssetId { get; set; }
    public Guid? ColumnProfileId { get; set; }
    public ImportSessionStatus Status { get; set; } = ImportSessionStatus.Created;
    public string? VariantGroupKey { get; set; }
    public int TotalRows { get; set; }
    public int ValidRows { get; set; }
    public int ErrorRows { get; set; }
    public int ReviewRows { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Version { get; set; } = 1;
}

public sealed class ImportColumnProfile
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public required string Name { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public long Version { get; set; } = 1;
}

public sealed class ImportColumnMapping
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ProfileId { get; set; }
    public required string SourceColumn { get; set; }
    public required string TargetField { get; set; }
    public int SortOrder { get; set; }
}

public sealed class ImportStagingRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid SessionId { get; set; }
    public int RowNumber { get; set; }
    public string? ExternalRecordId { get; set; }
    public required string RawJson { get; set; }
    public required string SafeValuesJson { get; set; }
    public required string ValidationErrorsJson { get; set; }
    public required string RowHash { get; set; }
    public string? SkuNormalized { get; set; }
    public string? BarcodeNormalized { get; set; }
    public required string ReviewStatus { get; set; }
}

public sealed class ImportMatchCandidate
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid SessionId { get; set; }
    public Guid StagingRecordId { get; set; }
    public Guid? ProductId { get; set; }
    public Guid? VariantId { get; set; }
    public required string MatchRule { get; set; }
    public required string Status { get; set; }
    public required string SafeSummary { get; set; }
    public long Version { get; set; } = 1;
}

public sealed class ImportDecision
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CandidateId { get; set; }
    public ImportDecisionKind Decision { get; set; }
    public Guid? LinkProductId { get; set; }
    public Guid? LinkVariantId { get; set; }
    public Guid ActorUserId { get; set; }
    public DateTimeOffset DecidedAt { get; set; }
    public long Version { get; set; } = 1;
}

public sealed class FieldProvenance
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid SessionId { get; set; }
    public Guid ProductId { get; set; }
    public Guid? VariantId { get; set; }
    public required string FieldName { get; set; }
    public Guid StagingRecordId { get; set; }
    public required string ValueHash { get; set; }
    public DateTimeOffset AppliedAt { get; set; }
}
