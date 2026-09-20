namespace MarketplaceHub.Domain;

public sealed class ProductImportSession
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ConnectionId { get; set; }
    public Guid JobId { get; set; }
    public required string Phase { get; set; }
    public string? NextCursor { get; set; }
    public int ReceivedProducts { get; set; }
    public int? TotalProducts { get; set; }
    public int PageNumber { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public long Version { get; set; } = 1;
}

public sealed class ProductImportStagingRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ConnectionId { get; set; }
    public Guid JobId { get; set; }
    public required string ModelKey { get; set; }
    public required string ExternalProductId { get; set; }
    public required string SnapshotHash { get; set; }
    public required string SnapshotJson { get; set; }
    public int ReceivedOrder { get; set; }
    public required string State { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? FinalizedAt { get; set; }
    public string? ErrorSummary { get; set; }
}
