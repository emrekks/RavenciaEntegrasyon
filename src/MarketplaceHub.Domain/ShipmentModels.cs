namespace MarketplaceHub.Domain;

public sealed class ShipmentDocument
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ConnectionId { get; set; }
    public Guid PackageId { get; set; }
    public Guid FileAssetId { get; set; }
    public required string DocumentKind { get; set; }
    public required string Format { get; set; }
    public required string Source { get; set; }
    public required string Checksum { get; set; }
    public int DocumentVersion { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
}

public sealed class ShipmentDocumentAttempt
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid PackageId { get; set; }
    public Guid? DocumentId { get; set; }
    public required string IdempotencyKey { get; set; }
    public required string Status { get; set; }
    public string? ExternalOperationId { get; set; }
    public string? ErrorCode { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

public sealed class CargoProviderMapping
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ConnectionId { get; set; }
    public required string ExternalProviderId { get; set; }
    public required string ExternalProviderName { get; set; }
    public required string LocalProviderCode { get; set; }
    public required string Status { get; set; }
    public DateTimeOffset? VerifiedAt { get; set; }
    public long Version { get; set; } = 1;
}
