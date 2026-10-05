namespace MarketplaceHub.Domain;

public sealed class MarketplaceQuestion
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ConnectionId { get; set; }
    public required string ExternalQuestionId { get; set; }
    public required string Kind { get; set; }
    public required string Status { get; set; }
    public required string QuestionText { get; set; }
    public string? ProductName { get; set; }
    public string? ProductImageUrl { get; set; }
    public string? ProductSku { get; set; }
    public string? ProductBarcode { get; set; }
    public string? ProductModelCode { get; set; }
    public string? CustomerName { get; set; }
    public string? ExternalOrderNumber { get; set; }
    public string? HistoryJson { get; set; }
    public string? PendingAnswerText { get; set; }
    public string? AnswerSubmissionStatus { get; set; }
    public string? AnswerSubmissionKey { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset LastRemoteModifiedAt { get; set; }
    public DateTimeOffset LastSyncedAt { get; set; }
    public long Version { get; set; } = 1;
}

public sealed class MarketplaceQuestionTemplate
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public required string Title { get; set; }
    public required string Text { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Version { get; set; } = 1;
}

public sealed class MarketplaceQuestionSyncState
{
    public Guid TenantId { get; set; }
    public Guid ConnectionId { get; set; }
    public bool HistoryImported { get; set; }
    public DateTimeOffset? HistoryStartedAt { get; set; }
    public string ProgressStatus { get; set; } = "IDLE";
    public int ImportedCount { get; set; }
    public int HistoryStatusIndex { get; set; }
    public int HistoryKindIndex { get; set; }
    public int HistoryWindowIndex { get; set; }
    public int HistoryPageIndex { get; set; }
    public DateTimeOffset? LastRunStartedAt { get; set; }
    public DateTimeOffset? LastSuccessAt { get; set; }
    public string? LastError { get; set; }
    public long Version { get; set; } = 1;
}
