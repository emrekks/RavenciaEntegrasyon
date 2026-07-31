namespace MarketplaceHub.Domain;

public enum CapabilitySupportLevel { Supported, NotSupported, Unknown, TemporarilyUnavailable }

public sealed class PlatformCredential
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ConnectionId { get; set; }
    public required string CredentialType { get; set; }
    public required string ProtectedPayload { get; set; }
    public required string MaskedHint { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public long Version { get; set; } = 1;
}

public sealed class PlatformCapability
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ConnectionId { get; set; }
    public required string Code { get; set; }
    public CapabilitySupportLevel SupportLevel { get; set; } = CapabilitySupportLevel.Unknown;
    public required string ApiVersion { get; set; }
    public required string Environment { get; set; }
    public required string StoreScope { get; set; }
    public string? SourceUrl { get; set; }
    public string? SourceVersion { get; set; }
    public string? RequiredScope { get; set; }
    public string? ConstraintsJson { get; set; }
    public string? EvidenceNote { get; set; }
    public string? FixtureChecksum { get; set; }
    public DateTimeOffset? VerifiedAt { get; set; }
    public long Version { get; set; } = 1;
}

public sealed class WebhookSubscription
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ConnectionId { get; set; }
    public required string RouteTokenHash { get; set; }
    public required string AuthenticationType { get; set; }
    public required string ProtectedVerifierSecret { get; set; }
    public string? ExternalSubscriptionId { get; set; }
    public required string Status { get; set; }
    public DateTimeOffset? VerifiedAt { get; set; }
    public DateTimeOffset? LastReceivedAt { get; set; }
    public long Version { get; set; } = 1;
}

public sealed class SyncCursor
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ConnectionId { get; set; }
    public required string ResourceType { get; set; }
    public string? OpaqueCursor { get; set; }
    public DateTimeOffset? LastModifiedWatermark { get; set; }
    public DateTimeOffset? LastSuccessAt { get; set; }
    public long Version { get; set; } = 1;
}

public sealed class ConnectionSyncPolicy
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ConnectionId { get; set; }
    public required string ResourceType { get; set; }
    public int IntervalSeconds { get; set; }
    public int OverlapSeconds { get; set; }
    public int JitterSeconds { get; set; }
    public bool Enabled { get; set; }
    public long Version { get; set; } = 1;
}

public sealed class ReconciliationRun
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ConnectionId { get; set; }
    public required string Scope { get; set; }
    public required string Status { get; set; }
    public int ComparedCount { get; set; }
    public int DifferenceCount { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

public sealed class ReconciliationDifference
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid RunId { get; set; }
    public required string EntityType { get; set; }
    public required string EntityKey { get; set; }
    public required string FieldName { get; set; }
    public string? LocalValueHash { get; set; }
    public string? RemoteValueHash { get; set; }
    public required string Resolution { get; set; }
}
