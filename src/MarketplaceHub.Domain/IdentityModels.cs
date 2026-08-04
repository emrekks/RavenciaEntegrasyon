namespace MarketplaceHub.Domain;

public enum RecordStatus { Active, Disabled }
public enum MembershipRole { Owner, Administrator, Operations, Accounting, ReadOnly }
public enum SessionState { PasswordChangeRequired, MfaChallenge, Active, Revoked }
public enum TotpState { Disabled, Pending, Enabled }

public sealed class Tenant
{
    public Guid Id { get; set; }
    public required string Code { get; set; }
    public required string DisplayName { get; set; }
    public RecordStatus Status { get; set; } = RecordStatus.Active;
    public string Timezone { get; set; } = "Europe/Istanbul";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Version { get; set; }
}

public sealed class TenantMembership
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public MembershipRole Role { get; set; }
    public RecordStatus Status { get; set; } = RecordStatus.Active;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Version { get; set; }
}

public sealed class UserSecurity
{
    public Guid UserId { get; set; }
    public TotpState TotpState { get; set; }
    public string? ProtectedTotpSecret { get; set; }
    public DateTimeOffset? EnrollmentExpiresAt { get; set; }
    public long? LastAcceptedTimeStep { get; set; }
    public Guid? RecoveryBatchId { get; set; }
    public long Version { get; set; }
}

public sealed class RecoveryCode
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid BatchId { get; set; }
    public required string CodeDigest { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
    public DateTimeOffset? InvalidatedAt { get; set; }
}

public sealed class UserSession
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid? TenantId { get; set; }
    public SessionState State { get; set; }
    public required string TokenHash { get; set; }
    public long SessionVersion { get; set; }
    public DateTimeOffset IssuedAt { get; set; }
    public DateTimeOffset? ReauthenticatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset AbsoluteExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public DateTimeOffset? LastSeenAt { get; set; }
}

public sealed class BootstrapState
{
    public required string Key { get; set; }
    public DateTimeOffset CompletedAt { get; set; }
    public Guid TenantId { get; set; }
    public Guid OwnerUserId { get; set; }
    public required string ConfigurationFingerprint { get; set; }
    public long Version { get; set; }
}
