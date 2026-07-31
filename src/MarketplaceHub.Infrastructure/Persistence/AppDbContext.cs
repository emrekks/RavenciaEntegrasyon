using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace MarketplaceHub.Infrastructure.Persistence;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>(options)
{
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<TenantMembership> TenantMemberships => Set<TenantMembership>();
    public DbSet<UserSecurity> UserSecurities => Set<UserSecurity>();
    public DbSet<RecoveryCode> RecoveryCodes => Set<RecoveryCode>();
    public DbSet<UserSession> UserSessions => Set<UserSession>();
    public DbSet<BootstrapState> BootstrapStates => Set<BootstrapState>();
    public DbSet<IntegrationJob> IntegrationJobs => Set<IntegrationJob>();
    public DbSet<JobAttempt> JobAttempts => Set<JobAttempt>();
    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();
    public DbSet<ExternalEffectRecord> ExternalEffectRecords => Set<ExternalEffectRecord>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<OperationalIssue> OperationalIssues => Set<OperationalIssue>();
    public DbSet<FeatureFlag> FeatureFlags => Set<FeatureFlag>();
    public DbSet<FileAsset> FileAssets => Set<FileAsset>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        ConfigureIdentity(builder);
        ConfigureIntegration(builder);
        ConfigureOperations(builder);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess) { GuardAppendOnlyAudit(); return base.SaveChanges(acceptAllChangesOnSuccess); }
    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default) { GuardAppendOnlyAudit(); return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken); }

    private void GuardAppendOnlyAudit()
    {
        if (ChangeTracker.Entries<AuditLog>().Any(x => x.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Audit log is append-only.");
    }

    private static void ConfigureIdentity(ModelBuilder builder)
    {
        builder.Entity<ApplicationUser>().ToTable("users", "iam");
        builder.Entity<ApplicationUser>(entity => { entity.Property(x => x.DisplayName).HasMaxLength(160); entity.Property(x => x.Status).HasMaxLength(24); });
        builder.Entity<IdentityRole<Guid>>().ToTable("roles", "iam");
        builder.Entity<IdentityUserRole<Guid>>().ToTable("user_roles", "iam");
        builder.Entity<IdentityUserClaim<Guid>>().ToTable("user_claims", "iam");
        builder.Entity<IdentityUserLogin<Guid>>().ToTable("user_logins", "iam");
        builder.Entity<IdentityRoleClaim<Guid>>().ToTable("role_claims", "iam");
        builder.Entity<IdentityUserToken<Guid>>().ToTable("user_tokens", "iam");

        builder.Entity<Tenant>(entity =>
        {
            entity.ToTable("tenants", "iam"); entity.HasKey(x => x.Id);
            entity.Property(x => x.Code).HasMaxLength(64); entity.HasIndex(x => x.Code).IsUnique();
            entity.Property(x => x.DisplayName).HasMaxLength(160); entity.Property(x => x.Timezone).HasMaxLength(64);
            entity.Property(x => x.Status).HasConversion(RecordStatusConverter).HasMaxLength(24); entity.Property(x => x.Version).IsConcurrencyToken();
        });
        builder.Entity<TenantMembership>(entity =>
        {
            entity.ToTable("tenant_memberships", "iam"); entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.UserId }).IsUnique();
            entity.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
            entity.Property(x => x.Role).HasConversion(v => "OWNER", v => MembershipRole.Owner).HasMaxLength(24); entity.Property(x => x.Status).HasConversion(RecordStatusConverter).HasMaxLength(24);
            entity.Property(x => x.Version).IsConcurrencyToken();
        });
        builder.Entity<UserSecurity>(entity =>
        {
            entity.ToTable("user_security", "iam"); entity.HasKey(x => x.UserId);
            entity.Property(x => x.TotpState).HasConversion(TotpStateConverter).HasMaxLength(24); entity.Property(x => x.ProtectedTotpSecret).HasMaxLength(2048);
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasOne<ApplicationUser>().WithOne().HasForeignKey<UserSecurity>(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<RecoveryCode>(entity =>
        {
            entity.ToTable("recovery_codes", "iam"); entity.HasKey(x => x.Id);
            entity.Property(x => x.CodeDigest).HasMaxLength(128); entity.HasIndex(x => new { x.UserId, x.CodeDigest }).IsUnique();
            entity.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<UserSession>(entity =>
        {
            entity.ToTable("user_sessions", "iam"); entity.HasKey(x => x.Id);
            entity.Property(x => x.State).HasConversion(SessionStateConverter).HasMaxLength(40); entity.Property(x => x.TokenHash).HasMaxLength(128);
            entity.HasIndex(x => x.TokenHash).IsUnique(); entity.HasIndex(x => new { x.UserId, x.State });
            entity.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<BootstrapState>(entity =>
        {
            entity.ToTable("bootstrap_state", "iam"); entity.HasKey(x => x.Key); entity.Property(x => x.Key).HasMaxLength(64);
            entity.Property(x => x.ConfigurationFingerprint).HasMaxLength(128); entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.OwnerUserId).OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void ConfigureIntegration(ModelBuilder builder)
    {
        builder.Entity<IntegrationJob>(entity =>
        {
            entity.ToTable("jobs", "integration"); entity.HasKey(x => x.Id);
            entity.Property(x => x.JobType).HasMaxLength(96); entity.Property(x => x.JobDedupKey).HasMaxLength(256);
            entity.Property(x => x.EffectIdempotencyKey).HasMaxLength(256); entity.Property(x => x.PayloadHash).HasMaxLength(128);
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(24); entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasIndex(x => new { x.TenantId, x.JobType, x.JobDedupKey }).IsUnique();
            entity.HasIndex(x => new { x.Status, x.AvailableAt, x.Priority });
            entity.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<JobAttempt>(entity =>
        {
            entity.ToTable("job_attempts", "integration"); entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.JobId, x.AttemptNumber }).IsUnique();
            entity.HasOne<IntegrationJob>().WithMany().HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<InboxMessage>(entity =>
        {
            entity.ToTable("inbox_messages", "integration"); entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.Source, x.ExternalMessageId }).IsUnique();
            entity.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<ExternalEffectRecord>(entity =>
        {
            entity.ToTable("external_effect_records", "integration"); entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.EffectType, x.IdempotencyKey }).IsUnique();
            entity.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void ConfigureOperations(ModelBuilder builder)
    {
        builder.Entity<AuditLog>(entity =>
        {
            entity.ToTable("audit_logs", "ops"); entity.HasKey(x => x.Id); entity.Property(x => x.Id).UseIdentityAlwaysColumn();
            entity.Property(x => x.Action).HasMaxLength(96); entity.Property(x => x.Reason).HasMaxLength(512);
        });
        builder.Entity<OperationalIssue>(entity =>
        {
            entity.ToTable("operational_issues", "ops"); entity.HasKey(x => x.Id);
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(24); entity.HasIndex(x => x.DedupeKey).IsUnique();
        });
        builder.Entity<FeatureFlag>(entity =>
        {
            entity.ToTable("feature_flags", "ops"); entity.HasKey(x => x.Key); entity.Property(x => x.Key).HasMaxLength(128);
            entity.Property(x => x.Version).IsConcurrencyToken();
        });
        builder.Entity<FileAsset>(entity =>
        {
            entity.ToTable("file_assets", "ops"); entity.HasKey(x => x.Id);
            entity.Property(x => x.RelativePath).HasMaxLength(512); entity.Property(x => x.MimeType).HasMaxLength(128);
            entity.HasIndex(x => new { x.TenantId, x.RelativePath }).IsUnique();
            entity.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static readonly ValueConverter<RecordStatus, string> RecordStatusConverter = new(
        value => value == RecordStatus.Active ? "ACTIVE" : "DISABLED",
        value => value == "ACTIVE" ? RecordStatus.Active : RecordStatus.Disabled);
    private static readonly ValueConverter<TotpState, string> TotpStateConverter = new(
        value => value == TotpState.Disabled ? "DISABLED" : value == TotpState.Pending ? "PENDING" : "ENABLED",
        value => value == "PENDING" ? TotpState.Pending : value == "ENABLED" ? TotpState.Enabled : TotpState.Disabled);
    private static readonly ValueConverter<SessionState, string> SessionStateConverter = new(
        value => value == SessionState.PasswordChangeRequired ? "PASSWORD_CHANGE_REQUIRED" : value == SessionState.MfaChallenge ? "MFA_CHALLENGE" : value == SessionState.Active ? "ACTIVE" : "REVOKED",
        value => value == "PASSWORD_CHANGE_REQUIRED" ? SessionState.PasswordChangeRequired : value == "MFA_CHALLENGE" ? SessionState.MfaChallenge : value == "ACTIVE" ? SessionState.Active : SessionState.Revoked);
}
