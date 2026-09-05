using System.Text.Json;
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
    public DbSet<IntegrationOutboxEvent> IntegrationOutboxEvents => Set<IntegrationOutboxEvent>();
    public DbSet<JobAttempt> JobAttempts => Set<JobAttempt>();
    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();
    public DbSet<ExternalEffectRecord> ExternalEffectRecords => Set<ExternalEffectRecord>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<OperationalIssue> OperationalIssues => Set<OperationalIssue>();
    public DbSet<FeatureFlag> FeatureFlags => Set<FeatureFlag>();
    public DbSet<FileAsset> FileAssets => Set<FileAsset>();
    public DbSet<ApiIdempotencyRecord> ApiIdempotencyRecords => Set<ApiIdempotencyRecord>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Brand> Brands => Set<Brand>();
    public DbSet<AttributeDefinition> AttributeDefinitions => Set<AttributeDefinition>();
    public DbSet<AttributeValue> AttributeValues => Set<AttributeValue>();
    public DbSet<CategoryAttributeRequirement> CategoryAttributeRequirements => Set<CategoryAttributeRequirement>();
    public DbSet<ProductAttributeAssignment> ProductAttributeAssignments => Set<ProductAttributeAssignment>();
    public DbSet<ProductVariant> ProductVariants => Set<ProductVariant>();
    public DbSet<ProductOption> ProductOptions => Set<ProductOption>();
    public DbSet<ProductOptionValue> ProductOptionValues => Set<ProductOptionValue>();
    public DbSet<VariantOptionValue> VariantOptionValues => Set<VariantOptionValue>();
    public DbSet<ProductMedia> ProductMedia => Set<ProductMedia>();
    public DbSet<PlatformConnection> PlatformConnections => Set<PlatformConnection>();
    public DbSet<ReferenceSnapshot> ReferenceSnapshots => Set<ReferenceSnapshot>();
    public DbSet<ReferenceItem> ReferenceItems => Set<ReferenceItem>();
    public DbSet<CategoryMapping> CategoryMappings => Set<CategoryMapping>();
    public DbSet<BrandMapping> BrandMappings => Set<BrandMapping>();
    public DbSet<AttributeMapping> AttributeMappings => Set<AttributeMapping>();
    public DbSet<AttributeValueMapping> AttributeValueMappings => Set<AttributeValueMapping>();
    public DbSet<MarketplaceProductLink> MarketplaceProductLinks => Set<MarketplaceProductLink>();
    public DbSet<MarketplaceVariantLink> MarketplaceVariantLinks => Set<MarketplaceVariantLink>();
    public DbSet<MarketplaceListingState> MarketplaceListingStates => Set<MarketplaceListingState>();
    public DbSet<ExternalIdentifierAlias> ExternalIdentifierAliases => Set<ExternalIdentifierAlias>();
    public DbSet<ChannelListingProfile> ChannelListingProfiles => Set<ChannelListingProfile>();
    public DbSet<ChannelListingVariant> ChannelListingVariants => Set<ChannelListingVariant>();
    public DbSet<ChannelListingAttribute> ChannelListingAttributes => Set<ChannelListingAttribute>();
    public DbSet<ChannelMediaOrder> ChannelMediaOrders => Set<ChannelMediaOrder>();
    public DbSet<ImportSession> ImportSessions => Set<ImportSession>();
    public DbSet<ImportColumnProfile> ImportColumnProfiles => Set<ImportColumnProfile>();
    public DbSet<ImportColumnMapping> ImportColumnMappings => Set<ImportColumnMapping>();
    public DbSet<ImportStagingRecord> ImportStagingRecords => Set<ImportStagingRecord>();
    public DbSet<ImportMatchCandidate> ImportMatchCandidates => Set<ImportMatchCandidate>();
    public DbSet<ImportDecision> ImportDecisions => Set<ImportDecision>();
    public DbSet<FieldProvenance> FieldProvenance => Set<FieldProvenance>();
    public DbSet<ConnectionInventoryPolicy> ConnectionInventoryPolicies => Set<ConnectionInventoryPolicy>();
    public DbSet<InventoryLocation> InventoryLocations => Set<InventoryLocation>();
    public DbSet<ConnectionLocationMapping> ConnectionLocationMappings => Set<ConnectionLocationMapping>();
    public DbSet<InventoryItem> InventoryItems => Set<InventoryItem>();
    public DbSet<StockLedgerEntry> StockLedgerEntries => Set<StockLedgerEntry>();
    public DbSet<StockReservation> StockReservations => Set<StockReservation>();
    public DbSet<ChannelOffer> ChannelOffers => Set<ChannelOffer>();
    public DbSet<ChannelPriceHistory> ChannelPriceHistory => Set<ChannelPriceHistory>();
    public DbSet<PlatformCredential> PlatformCredentials => Set<PlatformCredential>();
    public DbSet<PlatformCapability> PlatformCapabilities => Set<PlatformCapability>();
    public DbSet<WebhookSubscription> WebhookSubscriptions => Set<WebhookSubscription>();
    public DbSet<SyncCursor> SyncCursors => Set<SyncCursor>();
    public DbSet<ConnectionSyncPolicy> ConnectionSyncPolicies => Set<ConnectionSyncPolicy>();
    public DbSet<ReconciliationRun> ReconciliationRuns => Set<ReconciliationRun>();
    public DbSet<ReconciliationDifference> ReconciliationDifferences => Set<ReconciliationDifference>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderLine> OrderLines => Set<OrderLine>();
    public DbSet<OrderFinancialAllocation> OrderFinancialAllocations => Set<OrderFinancialAllocation>();
    public DbSet<ShipmentPackage> ShipmentPackages => Set<ShipmentPackage>();
    public DbSet<PackageLineAllocation> PackageLineAllocations => Set<PackageLineAllocation>();
    public DbSet<OrderStatusHistory> OrderStatusHistory => Set<OrderStatusHistory>();
    public DbSet<ShipmentDocument> ShipmentDocuments => Set<ShipmentDocument>();
    public DbSet<ShipmentDocumentAttempt> ShipmentDocumentAttempts => Set<ShipmentDocumentAttempt>();
    public DbSet<CargoProviderMapping> CargoProviderMappings => Set<CargoProviderMapping>();
    public DbSet<ReturnClaim> ReturnClaims => Set<ReturnClaim>();
    public DbSet<ReturnLine> ReturnLines => Set<ReturnLine>();
    public DbSet<ReturnDecision> ReturnDecisions => Set<ReturnDecision>();
    public DbSet<ReturnEvidence> ReturnEvidence => Set<ReturnEvidence>();
    public DbSet<ReturnStockDisposition> ReturnStockDispositions => Set<ReturnStockDisposition>();
    public DbSet<LegalEntityProfile> LegalEntityProfiles => Set<LegalEntityProfile>();
    public DbSet<InvoicePolicy> InvoicePolicies => Set<InvoicePolicy>();
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<InvoiceLine> InvoiceLines => Set<InvoiceLine>();
    public DbSet<InvoicePartySnapshot> InvoicePartySnapshots => Set<InvoicePartySnapshot>();
    public DbSet<InvoiceDocument> InvoiceDocuments => Set<InvoiceDocument>();
    public DbSet<InvoiceSubmissionAttempt> InvoiceSubmissionAttempts => Set<InvoiceSubmissionAttempt>();
    public DbSet<MarketplaceDelivery> MarketplaceDeliveries => Set<MarketplaceDelivery>();
    public DbSet<DashboardSnapshot> DashboardSnapshots => Set<DashboardSnapshot>();
    public DbSet<DashboardRevenueDaily> DashboardRevenueDaily => Set<DashboardRevenueDaily>();
    public DbSet<DashboardLowStockProjection> DashboardLowStockProjections => Set<DashboardLowStockProjection>();
    public DbSet<DashboardSyncStatusProjection> DashboardSyncStatusProjections => Set<DashboardSyncStatusProjection>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        ConfigureIdentity(builder);
        ConfigureIntegration(builder);
        ConfigureOperations(builder);
        ConfigureDashboard(builder);
        builder.ConfigureCatalogModels();
        builder.ConfigureMarketplaceModels();
        builder.ConfigureInvoicingModels();
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess) { ApplyIntegrationJobMetadata(); AppendDataChangeOutboxEvents(); GuardAppendOnlyAudit(); return base.SaveChanges(acceptAllChangesOnSuccess); }
    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default) { ApplyIntegrationJobMetadata(); AppendDataChangeOutboxEvents(); GuardAppendOnlyAudit(); return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken); }

    private void ApplyIntegrationJobMetadata()
    {
        foreach (var entry in ChangeTracker.Entries<IntegrationJob>().Where(x => x.State == EntityState.Added))
        {
            if (entry.Entity.ResourceType != "jobs") continue;
            var metadata = IntegrationJobMetadataPolicy.FromJobType(entry.Entity.JobType);
            entry.Entity.ResourceType = metadata.ResourceType;
            entry.Entity.OperationType = metadata.OperationType;
            entry.Entity.TriggerType = metadata.TriggerType;
        }
    }

    private void AppendDataChangeOutboxEvents()
    {
        var existing = ChangeTracker.Entries<IntegrationOutboxEvent>()
            .Where(x => x.State == EntityState.Added)
            .Select(x => (x.Entity.ResourceType, x.Entity.AggregateType, x.Entity.AggregateId))
            .ToHashSet();
        // Adding an outbox event updates ChangeTracker. Materialize the source
        // entries first so the enumeration is not invalidated by that append.
        foreach (var entry in ChangeTracker.Entries().Where(x => x.State is EntityState.Added or EntityState.Modified).ToList())
        {
            var change = OutboxChange(entry.Entity);
            if (change is null || !existing.Add((change.Value.ResourceType, change.Value.AggregateType, change.Value.AggregateId))) continue;
            var now = DateTimeOffset.UtcNow;
            IntegrationOutboxEvents.Add(new IntegrationOutboxEvent
            {
                Id = Guid.CreateVersion7(),
                TenantId = change.Value.TenantId,
                ResourceType = change.Value.ResourceType,
                OperationType = entry.State == EntityState.Added ? "created" : "updated",
                AggregateType = change.Value.AggregateType,
                AggregateId = change.Value.AggregateId,
                AggregateVersion = change.Value.Version,
                PayloadJson = JsonSerializer.Serialize(new { aggregateType = change.Value.AggregateType, aggregateId = change.Value.AggregateId, version = change.Value.Version }),
                CreatedAt = now,
                NextAttemptAt = now
            });
        }
    }

    private static (Guid TenantId, string ResourceType, string AggregateType, Guid AggregateId, long? Version)? OutboxChange(object entity) => entity switch
    {
        Order value => (value.TenantId, "orders", "Order", value.Id, value.Version),
        OrderLine value => (value.TenantId, "orders", "Order", value.OrderId, value.Version),
        ShipmentPackage value => (value.TenantId, "orders", "ShipmentPackage", value.Id, value.Version),
        PackageLineAllocation value => (value.TenantId, "orders", "ShipmentPackage", value.PackageId, null),
        Product value => (value.TenantId, "products", "Product", value.Id, value.Version),
        ProductVariant value => (value.TenantId, "products", "Product", value.ProductId, value.Version),
        ProductMedia value => (value.TenantId, "products", "Product", value.ProductId, null),
        InventoryItem value => (value.TenantId, "inventory", "InventoryItem", value.Id, value.Version),
        StockLedgerEntry value => (value.TenantId, "inventory", "InventoryItem", value.InventoryItemId, null),
        StockReservation value => (value.TenantId, "inventory", "InventoryItem", value.InventoryItemId, value.Version),
        ReturnClaim value => (value.TenantId, "returns", "ReturnClaim", value.Id, value.Version),
        ReturnLine value => (value.TenantId, "returns", "ReturnClaim", value.ClaimId, null),
        Invoice value => (value.TenantId, "invoices", "Invoice", value.Id, value.Version),
        InvoiceLine value => (value.TenantId, "invoices", "Invoice", value.InvoiceId, null),
        InvoiceDocument value => (value.TenantId, "invoices", "Invoice", value.InvoiceId, null),
        MarketplaceDelivery value => (value.TenantId, "invoices", "Invoice", value.InvoiceId, null),
        PlatformConnection value => (value.TenantId, "connections", "PlatformConnection", value.Id, value.Version),
        _ => null
    };

    private void GuardAppendOnlyAudit()
    {
        if (ChangeTracker.Entries<AuditLog>().Any(x => x.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Audit log is append-only.");
        if (ChangeTracker.Entries<OrderStatusHistory>().Any(x => x.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Order status history is append-only.");
        if (ChangeTracker.Entries<ReturnDecision>().Any(x => x.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Return decisions are append-only.");
        if (ChangeTracker.Entries<InvoiceLine>().Any(x => x.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Invoice lines are immutable snapshots.");
        if (ChangeTracker.Entries<InvoicePartySnapshot>().Any(x => x.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Invoice party snapshots are immutable.");
        if (ChangeTracker.Entries<InvoiceDocument>().Any(x => x.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Invoice documents are immutable.");
        if (ChangeTracker.Entries<InvoiceSubmissionAttempt>().Any(x => x.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Invoice submission attempts are append-only.");
        if (ChangeTracker.Entries<MarketplaceDelivery>().Any(x => x.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Marketplace delivery attempts are append-only.");
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
            entity.Property(x => x.Role).HasConversion(MembershipRoleConverter).HasMaxLength(24); entity.Property(x => x.Status).HasConversion(RecordStatusConverter).HasMaxLength(24);
            entity.Property(x => x.Version).IsConcurrencyToken();
        });
        builder.Entity<UserSecurity>(entity =>
        {
            entity.ToTable("user_security", "iam"); entity.HasKey(x => x.UserId);
            entity.Property(x => x.TotpState).HasConversion(TotpStateConverter).HasMaxLength(24); entity.Property(x => x.ProtectedTotpSecret).HasMaxLength(2048); entity.Property(x => x.PendingProtectedTotpSecret).HasMaxLength(2048);
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
            entity.ToTable("jobs", "integration", table => table.HasCheckConstraint("ck_job_attempt_bounds", "\"AttemptCount\" >= 0 AND \"MaxAttempts\" > 0 AND \"AttemptCount\" <= \"MaxAttempts\"")); entity.HasKey(x => x.Id);
            entity.Property(x => x.JobType).HasMaxLength(96); entity.Property(x => x.ResourceType).HasMaxLength(48).HasDefaultValue("jobs"); entity.Property(x => x.OperationType).HasMaxLength(64).HasDefaultValue("execute"); entity.Property(x => x.TriggerType).HasMaxLength(32).HasDefaultValue("system"); entity.Property(x => x.JobDedupKey).HasMaxLength(256);
            entity.Property(x => x.EffectIdempotencyKey).HasMaxLength(256); entity.Property(x => x.PayloadHash).HasMaxLength(128);
            entity.Property(x => x.Status).HasConversion(JobStatusConverter).HasMaxLength(24); entity.Property(x => x.CreatedAt).HasDefaultValueSql("now()").ValueGeneratedOnAdd(); entity.Property(x => x.MaxAttempts).HasDefaultValue(JobRetryPolicy.DefaultMaxAttempts); entity.Property(x => x.Version).IsConcurrencyToken();
            entity.Property(x => x.ProgressLabel).HasMaxLength(256);
            entity.HasIndex(x => new { x.TenantId, x.JobType, x.JobDedupKey }).IsUnique();
            entity.HasIndex(x => new { x.Status, x.Priority, x.AvailableAt, x.CreatedAt });
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
        builder.Entity<IntegrationOutboxEvent>(entity =>
        {
            entity.ToTable("outbox_events", "integration"); entity.HasKey(x => x.Id);
            entity.Property(x => x.ResourceType).HasMaxLength(48);
            entity.Property(x => x.OperationType).HasMaxLength(64);
            entity.Property(x => x.AggregateType).HasMaxLength(96);
            entity.Property(x => x.PayloadJson).HasColumnType("jsonb");
            entity.Property(x => x.LastDispatchError).HasMaxLength(512);
            entity.Property(x => x.CreatedAt).HasDefaultValueSql("now()").ValueGeneratedOnAdd();
            entity.HasIndex(x => new { x.PublishedAt, x.NextAttemptAt, x.CreatedAt });
            entity.HasIndex(x => new { x.TenantId, x.CreatedAt });
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
            entity.Property(x => x.Classification).HasMaxLength(48); entity.Property(x => x.RelativePath).HasMaxLength(512); entity.Property(x => x.OriginalNameSafe).HasMaxLength(256); entity.Property(x => x.MimeType).HasMaxLength(128); entity.Property(x => x.Status).HasMaxLength(24);
            entity.HasIndex(x => new { x.TenantId, x.RelativePath }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.Sha256, x.Classification });
            entity.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<ApiIdempotencyRecord>(entity =>
        {
            entity.ToTable("api_idempotency_records", "ops"); entity.HasKey(x => x.Id);
            entity.Property(x => x.RouteTemplate).HasMaxLength(256); entity.Property(x => x.IdempotencyKey).HasMaxLength(256);
            entity.Property(x => x.RequestHash).HasMaxLength(128); entity.Property(x => x.State).HasMaxLength(24); entity.Property(x => x.ResponseBody).HasColumnType("text");
            entity.HasIndex(x => new { x.TenantId, x.RouteTemplate, x.IdempotencyKey }).IsUnique(); entity.HasIndex(x => x.ExpiresAt);
            entity.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void ConfigureDashboard(ModelBuilder builder)
    {
        builder.Entity<DashboardSnapshot>(entity =>
        {
            entity.ToTable("snapshot", "dashboard"); entity.HasKey(x => x.TenantId);
            entity.Property(x => x.PendingByPlatformJson).HasColumnType("jsonb");
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<DashboardRevenueDaily>(entity =>
        {
            entity.ToTable("revenue_daily", "dashboard"); entity.HasKey(x => new { x.TenantId, x.Day, x.PlatformName, x.Currency });
            entity.Property(x => x.Day).HasColumnType("date"); entity.Property(x => x.PlatformName).HasMaxLength(160); entity.Property(x => x.Currency).HasMaxLength(3);
            entity.HasIndex(x => new { x.TenantId, x.Day });
            entity.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<DashboardLowStockProjection>(entity =>
        {
            entity.ToTable("low_stock", "dashboard"); entity.HasKey(x => new { x.TenantId, x.ProductId });
            entity.Property(x => x.Title).HasMaxLength(512); entity.Property(x => x.PrimaryImageUrl).HasMaxLength(1024);
            entity.HasIndex(x => new { x.TenantId, x.TotalStock });
            entity.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<DashboardSyncStatusProjection>(entity =>
        {
            entity.ToTable("sync_status", "dashboard"); entity.HasKey(x => new { x.TenantId, x.ResourceType });
            entity.Property(x => x.ResourceType).HasMaxLength(48); entity.Property(x => x.DisplayName).HasMaxLength(96); entity.Property(x => x.Kind).HasMaxLength(32); entity.Property(x => x.Status).HasMaxLength(32); entity.Property(x => x.LastErrorCode).HasMaxLength(128); entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static readonly ValueConverter<JobStatus, string> JobStatusConverter = new(
        value => value == JobStatus.RetryScheduled ? "RETRY_SCHEDULED" : value == JobStatus.ManualReview ? "MANUAL_REVIEW" : value.ToString().ToUpperInvariant(),
        value => value == "RETRY_SCHEDULED" ? JobStatus.RetryScheduled : value == "MANUAL_REVIEW" ? JobStatus.ManualReview : Enum.Parse<JobStatus>(value, true));
    private static readonly ValueConverter<MembershipRole, string> MembershipRoleConverter = new(
        value => value == MembershipRole.Owner ? "OWNER" : value == MembershipRole.Administrator ? "ADMINISTRATOR" : value == MembershipRole.Operations ? "OPERATIONS" : value == MembershipRole.Accounting ? "ACCOUNTING" : "READ_ONLY",
        value => value == "ADMINISTRATOR" ? MembershipRole.Administrator : value == "OPERATIONS" ? MembershipRole.Operations : value == "ACCOUNTING" ? MembershipRole.Accounting : value == "READ_ONLY" ? MembershipRole.ReadOnly : MembershipRole.Owner);
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
