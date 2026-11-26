using System.Text.Json;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace MarketplaceHub.Infrastructure.Persistence;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>(options)
{
    // The cursor-progress and outbox times follow the same clock as the rest
    // of the application (and tests); TimeProvider.System when none is given.
    private readonly TimeProvider _timeProvider = TimeProvider.System;

    public AppDbContext(DbContextOptions<AppDbContext> options, TimeProvider timeProvider) : this(options) => _timeProvider = timeProvider;

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<TenantSetting> TenantSettings => Set<TenantSetting>();
    public DbSet<TenantMembership> TenantMemberships => Set<TenantMembership>();
    public DbSet<TenantInvitation> TenantInvitations => Set<TenantInvitation>();
    public DbSet<UserSecurity> UserSecurities => Set<UserSecurity>();
    public DbSet<RecoveryCode> RecoveryCodes => Set<RecoveryCode>();
    public DbSet<UserSession> UserSessions => Set<UserSession>();
    public DbSet<BootstrapState> BootstrapStates => Set<BootstrapState>();
    public DbSet<IntegrationJob> IntegrationJobs => Set<IntegrationJob>();
    public DbSet<IntegrationOutboxEvent> IntegrationOutboxEvents => Set<IntegrationOutboxEvent>();
    public DbSet<JobAttempt> JobAttempts => Set<JobAttempt>();
    public DbSet<JobVariantResult> JobVariantResults => Set<JobVariantResult>();
    public DbSet<JobReadSummary> JobReadSummaries => Set<JobReadSummary>();
    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();
    public DbSet<ExternalEffectRecord> ExternalEffectRecords => Set<ExternalEffectRecord>();
    public DbSet<PriceStockSendLog> PriceStockSendLogs => Set<PriceStockSendLog>();
    public DbSet<ProductChangeEntry> ProductChangeEntries => Set<ProductChangeEntry>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<OperationalIssue> OperationalIssues => Set<OperationalIssue>();
    public DbSet<PanelNotification> PanelNotifications => Set<PanelNotification>();
    public DbSet<FeatureFlag> FeatureFlags => Set<FeatureFlag>();
    public DbSet<FileAsset> FileAssets => Set<FileAsset>();
    public DbSet<ApiIdempotencyRecord> ApiIdempotencyRecords => Set<ApiIdempotencyRecord>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<ProductModel> ProductModels => Set<ProductModel>();
    public DbSet<ProductModelAttribute> ProductModelAttributes => Set<ProductModelAttribute>();
    public DbSet<ProductModelSizeChart> ProductModelSizeCharts => Set<ProductModelSizeChart>();
    public DbSet<MarketplaceModelLink> MarketplaceModelLinks => Set<MarketplaceModelLink>();
    public DbSet<MarketplaceKnownListingValue> MarketplaceKnownListingValues => Set<MarketplaceKnownListingValue>();
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
    public DbSet<ProductSizeChart> ProductSizeCharts => Set<ProductSizeChart>();
    public DbSet<AiUsageLog> AiUsageLogs => Set<AiUsageLog>();
    public DbSet<PlatformConnection> PlatformConnections => Set<PlatformConnection>();
    public DbSet<ReferenceSnapshot> ReferenceSnapshots => Set<ReferenceSnapshot>();
    public DbSet<ReferenceItem> ReferenceItems => Set<ReferenceItem>();
    public DbSet<CategoryMapping> CategoryMappings => Set<CategoryMapping>();
    public DbSet<BrandMapping> BrandMappings => Set<BrandMapping>();
    public DbSet<AttributeMapping> AttributeMappings => Set<AttributeMapping>();
    public DbSet<AttributeValueMapping> AttributeValueMappings => Set<AttributeValueMapping>();
    public DbSet<AttributeMappingTemplate> AttributeMappingTemplates => Set<AttributeMappingTemplate>();
    public DbSet<AttributeValueMappingTemplate> AttributeValueMappingTemplates => Set<AttributeValueMappingTemplate>();
    public DbSet<MappingTemplateDismissal> MappingTemplateDismissals => Set<MappingTemplateDismissal>();
    public DbSet<ConnectionAttributeDefault> ConnectionAttributeDefaults => Set<ConnectionAttributeDefault>();
    public DbSet<MarketplaceProductLink> MarketplaceProductLinks => Set<MarketplaceProductLink>();
    public DbSet<MarketplaceVariantLink> MarketplaceVariantLinks => Set<MarketplaceVariantLink>();
    public DbSet<CommissionTariff> CommissionTariffs => Set<CommissionTariff>();
    public DbSet<CommissionTariffRow> CommissionTariffRows => Set<CommissionTariffRow>();
    public DbSet<CommissionTariffSourceFile> CommissionTariffSourceFiles => Set<CommissionTariffSourceFile>();
    public DbSet<CommissionTariffSelection> CommissionTariffSelections => Set<CommissionTariffSelection>();
    public DbSet<CommissionTariffPick> CommissionTariffPicks => Set<CommissionTariffPick>();
    public DbSet<PromotionOfferFile> PromotionOfferFiles => Set<PromotionOfferFile>();
    public DbSet<AdReportFile> AdReportFiles => Set<AdReportFile>();
    public DbSet<MarketplaceFinanceTransaction> MarketplaceFinanceTransactions => Set<MarketplaceFinanceTransaction>();
    public DbSet<MarketplacePayoutOrder> MarketplacePayoutOrders => Set<MarketplacePayoutOrder>();
    public DbSet<MarketplaceCargoInvoiceItem> MarketplaceCargoInvoiceItems => Set<MarketplaceCargoInvoiceItem>();
    public DbSet<MarketplaceFinanceSyncState> MarketplaceFinanceSyncStates => Set<MarketplaceFinanceSyncState>();
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
    public DbSet<ChannelInventoryObservation> ChannelInventoryObservations => Set<ChannelInventoryObservation>();
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
    public DbSet<MarketplaceInvoiceReferenceHistory> MarketplaceInvoiceReferenceHistories => Set<MarketplaceInvoiceReferenceHistory>();
    public DbSet<PackageLineAllocation> PackageLineAllocations => Set<PackageLineAllocation>();
    public DbSet<OrderStatusHistory> OrderStatusHistory => Set<OrderStatusHistory>();
    public DbSet<ShipmentDocument> ShipmentDocuments => Set<ShipmentDocument>();
    public DbSet<ShipmentDocumentAttempt> ShipmentDocumentAttempts => Set<ShipmentDocumentAttempt>();
    public DbSet<CargoProviderMapping> CargoProviderMappings => Set<CargoProviderMapping>();
    public DbSet<ReturnClaim> ReturnClaims => Set<ReturnClaim>();
    public DbSet<ManualResend> ManualResends => Set<ManualResend>();
    public DbSet<ReturnLine> ReturnLines => Set<ReturnLine>();
    public DbSet<ReturnDecision> ReturnDecisions => Set<ReturnDecision>();
    public DbSet<ReturnEvidence> ReturnEvidence => Set<ReturnEvidence>();
    public DbSet<ReturnStockDisposition> ReturnStockDispositions => Set<ReturnStockDisposition>();
    public DbSet<MarketplaceQuestion> MarketplaceQuestions => Set<MarketplaceQuestion>();
    public DbSet<MarketplaceQuestionTemplate> MarketplaceQuestionTemplates => Set<MarketplaceQuestionTemplate>();
    public DbSet<MarketplaceQuestionSyncState> MarketplaceQuestionSyncStates => Set<MarketplaceQuestionSyncState>();
    public DbSet<LegalEntityProfile> LegalEntityProfiles => Set<LegalEntityProfile>();
    public DbSet<InvoicePolicy> InvoicePolicies => Set<InvoicePolicy>();
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<InvoiceLine> InvoiceLines => Set<InvoiceLine>();
    public DbSet<InvoicePartySnapshot> InvoicePartySnapshots => Set<InvoicePartySnapshot>();
    public DbSet<InvoiceDocument> InvoiceDocuments => Set<InvoiceDocument>();
    public DbSet<ManualOrderInvoiceDocument> ManualOrderInvoiceDocuments => Set<ManualOrderInvoiceDocument>();
    public DbSet<ManualOrderInvoiceDeliveryState> ManualOrderInvoiceDeliveryStates => Set<ManualOrderInvoiceDeliveryState>();
    public DbSet<InvoiceSubmissionAttempt> InvoiceSubmissionAttempts => Set<InvoiceSubmissionAttempt>();
    public DbSet<MarketplaceDelivery> MarketplaceDeliveries => Set<MarketplaceDelivery>();
    public DbSet<MarketplaceDeliveryState> MarketplaceDeliveryStates => Set<MarketplaceDeliveryState>();
    public DbSet<DashboardSnapshot> DashboardSnapshots => Set<DashboardSnapshot>();
    public DbSet<DashboardRevenueDaily> DashboardRevenueDaily => Set<DashboardRevenueDaily>();
    public DbSet<DashboardLowStockProjection> DashboardLowStockProjections => Set<DashboardLowStockProjection>();
    public DbSet<DashboardSyncStatusProjection> DashboardSyncStatusProjections => Set<DashboardSyncStatusProjection>();
    public DbSet<ProductImportSession> ProductImportSessions => Set<ProductImportSession>();
    public DbSet<ProductImportStagingRecord> ProductImportStagingRecords => Set<ProductImportStagingRecord>();
    public DbSet<CatalogStructureRun> CatalogStructureRuns => Set<CatalogStructureRun>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        // Trigram indexes (the brand name search on integration.reference_items).
        builder.HasPostgresExtension("pg_trgm");
        ConfigureIdentity(builder);
        ConfigureIntegration(builder);
        ConfigureOperations(builder);
        ConfigureDashboard(builder);
        builder.ConfigureCatalogModels();
        builder.ConfigureMarketplaceModels();
        builder.ConfigureInvoicingModels();
        builder.ConfigureQuestionModels();
        builder.ConfigureCommissionTariffModels();
        builder.ConfigureMarketplaceFinanceModels();
        builder.ConfigureManualResendModels();
        OrderMicroExportRules.MapFunctions(builder);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        ResetReaddressedOfferMarks(); TrackSyncCursorProgress(); ApplyIntegrationJobMetadata(); OrderBuyerTypes.Apply(ChangeTracker); OrderCustomerSearchNames.Apply(ChangeTracker); AppendDataChangeOutboxEvents(); GuardAppendOnlyAudit();
        var returnClaims = ReturnCardColumns.GuardAsync(this, () => ReturnCardColumns.BeforeSaveAsync(this, CancellationToken.None), []).GetAwaiter().GetResult();
        var models = ProductModelLinks.Capture(ChangeTracker);
        // KATALOG-10: the product list's cached rule check of the tenants whose cards or variants this save writes.
        var catalogTenants = CatalogListGenerations.Bump(ChangeTracker);
        // KATALOG-14 phase D5a: the save, its model links and the model's values in one transaction (SaveChangesAsync).
        var reported = ModelMirrorReport.Count;
        using var owned = models is not null && Database.CurrentTransaction is null ? Database.BeginTransaction() : null;
        if (owned is not null) OwnsModelLinkTransaction = true;
        try
        {
            int saved;
            // KATALOG-14 phase D5b review 3: the models the save gives a field to are locked before any card row is written.
            if (models is not null) ProductModelLinks.LockPendingModelsAsync(this, models, CancellationToken.None).GetAwaiter().GetResult();
            // KATALOG-14 D5 step B1: a new card is inserted with its model (ProductModelLinks.GiveNewCardsModels).
            if (models is not null) ProductModelLinks.GiveNewCardsModels(this, models, _timeProvider.GetUtcNow());
            _saving = true;
            try { saved = base.SaveChanges(acceptAllChangesOnSuccess && owned is null); }
            finally { _saving = false; }
            CatalogListGenerations.Bump(catalogTenants);
            ReturnCardColumns.GuardAsync(this, async () => { await ReturnCardColumns.AfterSaveAsync(this, returnClaims, CancellationToken.None); return 0; }, 0).GetAwaiter().GetResult();
            if (models is not null) LinkProductModelsAsync(models).GetAwaiter().GetResult();
            if (owned is not null)
            {
                OwnsModelLinkTransaction = false;
                owned.Commit();
                if (acceptAllChangesOnSuccess) ChangeTracker.AcceptAllChanges();
            }
            if (models is not null) ProductModelLinks.SettleFreshModelsAsync(this, models, CancellationToken.None).GetAwaiter().GetResult();
            RefreshDeferredModelValuesAsync().GetAwaiter().GetResult();
            return saved;
        }
        catch
        {
            if (models is not null) ProductModelLinks.ForgetFreshModels(this, models);
            ForgetModelMirrorReport(reported);
            throw;
        }
        finally
        {
            OwnsModelLinkTransaction = false;
        }
    }

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        await ResetReaddressedOfferMarksAsync(cancellationToken); TrackSyncCursorProgress(); ApplyIntegrationJobMetadata(); OrderBuyerTypes.Apply(ChangeTracker); OrderCustomerSearchNames.Apply(ChangeTracker); AppendDataChangeOutboxEvents(); GuardAppendOnlyAudit();
        // SIPARIS-STOK-9: the Returns page's card columns of every return claim this save touches: the claims it changes in
        // the save itself (after the outbox events, so the columns raise none), the others right after it, outside the
        // tracker (ReturnCardColumns).
        var returnClaims = await ReturnCardColumns.GuardAsync(this, () => ReturnCardColumns.BeforeSaveAsync(this, cancellationToken), []);
        var models = ProductModelLinks.Capture(ChangeTracker);
        // KATALOG-10: the product list's cached rule check of the tenants whose cards or variants this save writes.
        var catalogTenants = CatalogListGenerations.Bump(ChangeTracker);
        // KATALOG-14 phase D5a: a save that adds or changes a card links it to its model and writes the model's values in
        // the same transaction (the caller's, else one opened here), so no reader ever sees a card without its model or a
        // model behind its cards.
        var reported = ModelMirrorReport.Count;
        await using var owned = models is not null && Database.CurrentTransaction is null ? await Database.BeginTransactionAsync(cancellationToken) : null;
        if (owned is not null) OwnsModelLinkTransaction = true;
        try
        {
            int saved;
            // KATALOG-14 phase D5b review 3: the models the save gives a field to are locked before any card row is written.
            if (models is not null) await ProductModelLinks.LockPendingModelsAsync(this, models, cancellationToken);
            // KATALOG-14 D5 step B1: a new card is inserted with its model (ProductModelLinks.GiveNewCardsModels).
            if (models is not null) ProductModelLinks.GiveNewCardsModels(this, models, _timeProvider.GetUtcNow());
            _saving = true;
            // In its own transaction the changes are accepted only after the commit: a link or model write that fails rolls
            // the save back and leaves the tracked entities as they were, so the caller's next save writes them again.
            try { saved = await base.SaveChangesAsync(acceptAllChangesOnSuccess && owned is null, cancellationToken); }
            finally { _saving = false; }
            CatalogListGenerations.Bump(catalogTenants);
            await ReturnCardColumns.GuardAsync(this, async () => { await ReturnCardColumns.AfterSaveAsync(this, returnClaims, cancellationToken); return 0; }, 0);
            if (models is not null) await LinkProductModelsAsync(models);
            if (owned is not null)
            {
                OwnsModelLinkTransaction = false;
                await owned.CommitAsync(cancellationToken);
                if (acceptAllChangesOnSuccess) ChangeTracker.AcceptAllChanges();
            }
            if (models is not null) await ProductModelLinks.SettleFreshModelsAsync(this, models, cancellationToken);
            await RefreshDeferredModelValuesAsync();
            return saved;
        }
        catch
        {
            if (models is not null) ProductModelLinks.ForgetFreshModels(this, models);
            ForgetModelMirrorReport(reported);
            throw;
        }
        finally
        {
            OwnsModelLinkTransaction = false;
        }
    }

    // True while SaveChanges runs its own transaction around a save and its model links (KATALOG-14 phase D5a): the
    // reconcile then takes its advisory locks inside it (ProductModelLinks.ReconcileAsync).
    internal bool OwnsModelLinkTransaction { get; private set; }

    // KATALOG-14 phase D5b: set while a marketplace product import saves (CatalogImportService.SyncProducts). A colour
    // whose VAT, category, warranty or delivery days come in apart from its model's then does not move the model: the
    // model keeps its value, the colour's copy takes it back and PRODUCT_MODEL_VALUES_DIFFER names the difference
    // (ModelMirror). Every other save moves the model and all its colours.
    internal bool MarketplaceCatalogImport { get; set; }

    // KATALOG-14 phase D5b: what the saves since the last read wrote to models and their colours (ModelMirror), for the
    // result messages of the writers ("Model İçindeki 3 Renge Uygulandı"). Read and cleared by the writer.
    internal List<ModelMirror.Applied> ModelMirrorReport { get; } = [];

    // KATALOG-14 phase D5b (owner, 17 Nov 2026): the order a marketplace import brought its new cards in (page and row), per
    // card: the first one gives a new model its values (ProductModelLinks.FirstBroughtIn). Set by the import, cleared after
    // each model group.
    internal Dictionary<Guid, int> ModelOrderHint { get; } = [];

    // How long a save waits for the model rows another save holds (ProductModelLinks.ModelLockTimeout; a test shortens it).
    internal string ModelLockTimeout { get; set; } = ProductModelLinks.ModelLockTimeout;

    // KATALOG-14 phase D5b review: what a save that failed or was rolled back reported (its models were not written): it is
    // dropped, so "Model İçindeki N Renge Uygulandı" counts only what stands.
    internal void ForgetModelMirrorReport(int from)
    {
        if (from < ModelMirrorReport.Count) ModelMirrorReport.RemoveRange(from, ModelMirrorReport.Count - from);
    }

    // KATALOG-14 phase B: the models whose shared values a save inside a caller's transaction changed. The values are not
    // written there: the model row update made a serializable editor save fail with a conflict whenever anything else
    // (the backfill, the scan, a worker's save) wrote the same model meanwhile. They are refreshed right after the
    // transaction commits (CommitHookTransactionFactory), in the refresh's own short transactions; a transaction rolled
    // back leaves them for the next commit, where the refresh simply finds the cards as they are.
    private readonly Dictionary<Guid, HashSet<Guid>> _deferredModelValues = [];
    private bool _refreshingDeferredModelValues;
    // SaveChanges commits its own transaction while EF still holds the context: the deferred models wait for its end.
    private bool _saving;

    internal void DeferModelValues(Guid tenantId, IEnumerable<Guid> modelIds)
    {
        if (!_deferredModelValues.TryGetValue(tenantId, out var set)) _deferredModelValues[tenantId] = set = [];
        set.UnionWith(modelIds);
    }

    // KATALOG-14 phase D5b: the model fields a serializable caller's save gave its cards (ModelMirror), written after its
    // commit with the deferred model values.
    private readonly Dictionary<Guid, Dictionary<Guid, ModelMirror.FieldChange>> _deferredFieldChanges = [];

    internal void DeferFieldChanges(Guid tenantId, IReadOnlyDictionary<Guid, ModelMirror.FieldChange> changes)
    {
        if (!_deferredFieldChanges.TryGetValue(tenantId, out var map)) _deferredFieldChanges[tenantId] = map = [];
        foreach (var (productId, change) in changes) map[productId] = change;
        if (!_deferredModelValues.ContainsKey(tenantId)) _deferredModelValues[tenantId] = [];
    }

    // The models deferred by saves inside a transaction, refreshed once no transaction is open. A failure is recorded as a
    // PRODUCT_MODEL_LINK_FAILED issue and the daily scan repairs the model: the commit itself already stands.
    public async Task RefreshDeferredModelValuesAsync()
    {
        if (_refreshingDeferredModelValues || _saving || _deferredModelValues.Count == 0 || Database.CurrentTransaction is not null) return;
        var deferred = _deferredModelValues.ToList();
        _deferredModelValues.Clear();
        var deferredFields = _deferredFieldChanges.ToDictionary(x => x.Key, x => x.Value);
        _deferredFieldChanges.Clear();
        _refreshingDeferredModelValues = true;
        try
        {
            foreach (var (tenantId, modelIds) in deferred)
            {
                var now = _timeProvider.GetUtcNow();
                if (deferredFields.TryGetValue(tenantId, out var changes))
                {
                    var reported = ModelMirrorReport.Count;
                    try
                    {
                        await using var transaction = await Database.BeginTransactionAsync();
                        // KATALOG-14 phase D5b review: the models' rows first, in id order, as every other model write takes them.
                        await ProductModelLinks.LockModelsOfCardsAsync(this, tenantId, [.. changes.Keys], [], CancellationToken.None);
                        await ModelMirror.ApplyAsync(this, tenantId, changes, now, CancellationToken.None);
                        await transaction.CommitAsync();
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        ForgetModelMirrorReport(reported);
                        await ProductModelLinks.RecordLinkFailureAsync(this, tenantId, exception, now);
                    }
                }
                if (modelIds.Count == 0) continue;
                try { await ProductModelValues.RefreshAsync(this, tenantId, modelIds, now, CancellationToken.None); }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    await ProductModelLinks.RecordLinkFailureAsync(this, tenantId, exception, now);
                }
            }
        }
        finally
        {
            _refreshingDeferredModelValues = false;
        }
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
        optionsBuilder.ReplaceService<IRelationalTransactionFactory, CommitHookTransactionFactory>();

    // Runs RefreshDeferredModelValuesAsync after every commit of this context's transactions (each caller's own
    // BeginTransaction(Async) … Commit(Async)), so no transactional caller has to remember it.
    internal sealed class CommitHookTransactionFactory(RelationalTransactionFactoryDependencies dependencies) : RelationalTransactionFactory(dependencies)
    {
        public override RelationalTransaction Create(IRelationalConnection connection, System.Data.Common.DbTransaction transaction, Guid transactionId, IDiagnosticsLogger<DbLoggerCategory.Database.Transaction> logger, bool transactionOwned) =>
            new CommitHookTransaction(connection, transaction, transactionId, logger, transactionOwned, Dependencies.SqlGenerationHelper);
    }

    private sealed class CommitHookTransaction(IRelationalConnection connection, System.Data.Common.DbTransaction transaction, Guid transactionId, IDiagnosticsLogger<DbLoggerCategory.Database.Transaction> logger, bool transactionOwned, ISqlGenerationHelper sqlGenerationHelper)
        : RelationalTransaction(connection, transaction, transactionId, logger, transactionOwned, sqlGenerationHelper)
    {
        public override void Commit()
        {
            base.Commit();
            if (Connection.Context is AppDbContext db) db.RefreshDeferredModelValuesAsync().GetAwaiter().GetResult();
        }

        public override async Task CommitAsync(CancellationToken cancellationToken = default)
        {
            await base.CommitAsync(cancellationToken);
            if (Connection.Context is AppDbContext db) await db.RefreshDeferredModelValuesAsync();
        }
    }

    // Model Kaydı Faz 1: the cards and variants this save added, deleted, re-coded or re-branded are linked to their model
    // (ProductModelLinks), right after the save and in its transaction when there is one. The link only follows the
    // model-code rule, so outside a transaction a failure here leaves a card unlinked (today's rule) and the save stands;
    // inside one the error is the transaction's (PostgreSQL has already aborted it). Outside one any failure (the model
    // refresh included) is recorded as the PRODUCT_MODEL_LINK_FAILED issue: a save that stands never becomes an error.
    private async Task LinkProductModelsAsync(ProductModelLinks.Pending models)
    {
        var now = _timeProvider.GetUtcNow();
        try { await ProductModelLinks.AfterSaveAsync(this, models, now, CancellationToken.None); }
        catch (Exception exception) when (Database.CurrentTransaction is null && exception is not OperationCanceledException)
        {
            foreach (var tenantId in models.Tenants.ToList()) await ProductModelLinks.RecordLinkFailureAsync(this, tenantId, exception, now);
        }
    }

    // K-3: offers of a variant link that now points elsewhere are unsent again (ChannelOfferSendMarks).
    private void ResetReaddressedOfferMarks()
    {
        var targets = ChannelOfferSendMarks.ReaddressedTargets(ChangeTracker);
        if (targets.Count == 0) return;
        ChannelOfferSendMarks.ResetLoaded(this, ChannelOfferSendMarks.Candidates(this, targets).ToList(), targets);
    }

    private async Task ResetReaddressedOfferMarksAsync(CancellationToken cancellationToken)
    {
        var targets = ChannelOfferSendMarks.ReaddressedTargets(ChangeTracker);
        if (targets.Count == 0) return;
        ChannelOfferSendMarks.ResetLoaded(this, await ChannelOfferSendMarks.Candidates(this, targets).ToListAsync(cancellationToken), targets);
    }

    private void TrackSyncCursorProgress()
    {
        var now = _timeProvider.GetUtcNow();
        foreach (var entry in ChangeTracker.Entries<SyncCursor>().Where(x => x.State is EntityState.Added or EntityState.Modified))
        {
            if (entry.State == EntityState.Added)
            {
                if (entry.Entity.LastCursorAdvancedAt is null
                    && (!string.IsNullOrWhiteSpace(entry.Entity.OpaqueCursor) || entry.Entity.LastModifiedWatermark is not null))
                {
                    entry.Entity.LastCursorAdvancedAt = now;
                }

                continue;
            }

            SyncCursorProgressTracker.TrackModification(
                entry.Entity,
                entry.Property(x => x.OpaqueCursor).OriginalValue,
                entry.Property(x => x.LastModifiedWatermark).OriginalValue,
                entry.Property(x => x.LastSuccessAt).OriginalValue,
                now);
        }
    }

    private void ApplyIntegrationJobMetadata()
    {
        foreach (var entry in ChangeTracker.Entries<IntegrationJob>().Where(x => x.State == EntityState.Added))
        {
            if (entry.Entity.ResourceType != "jobs") continue;
            var metadata = IntegrationJobMetadataPolicy.FromJobType(entry.Entity.JobType);
            entry.Entity.ResourceType = metadata.ResourceType;
            entry.Entity.OperationType = metadata.OperationType;
            // A job created for a user or by a webhook keeps saying so.
            if (entry.Entity.TriggerType is not ("user" or "webhook")) entry.Entity.TriggerType = metadata.TriggerType;
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
        // FATURA-FINANS-3: a deleted row is announced too (a return line, an allocation...): the reports read from it as from
        // a changed one. One event per aggregate and save, as for the others; deletes outside the tracker
        // (ExecuteDelete, raw SQL) still announce themselves.
        foreach (var entry in ChangeTracker.Entries().Where(x => x.State is EntityState.Added or EntityState.Modified or EntityState.Deleted).ToList())
        {
            // A row whose only change is a "last checked" time is still saved, but the screens have nothing new to show.
            if (SyncNoOpWrites.IsCheckOnly(entry)) continue;
            // A deleted return claim is announced as a change of its order (as CancelledReturnClaimCleanup does): the report
            // cache can no longer find the claim to place it in a period, but it finds the order (ReportCache).
            var change = entry.State == EntityState.Deleted && entry.Entity is ReturnClaim deletedClaim
                ? (deletedClaim.TenantId, "returns", "Order", deletedClaim.OrderId, (long?)null)
                : OutboxChange(entry.Entity);
            if (change is null || !existing.Add((change.Value.ResourceType, change.Value.AggregateType, change.Value.AggregateId))) continue;
            var now = _timeProvider.GetUtcNow();
            IntegrationOutboxEvents.Add(new IntegrationOutboxEvent
            {
                Id = Guid.CreateVersion7(),
                TenantId = change.Value.TenantId,
                ResourceType = change.Value.ResourceType,
                OperationType = entry.State switch { EntityState.Added => "created", EntityState.Deleted => "deleted", _ => "updated" },
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
        ManualOrderInvoiceDocument value => (value.TenantId, "invoices", "ManualOrderInvoiceDocument", value.Id, value.Version),
        ManualOrderInvoiceDeliveryState value => (value.TenantId, "invoices", "ManualOrderInvoiceDocument", value.DocumentId, value.Version),
        MarketplaceInvoiceReferenceHistory value => (value.TenantId, "invoices", "MarketplaceInvoiceReferenceHistory", value.Id, null),
        MarketplaceDelivery value => (value.TenantId, "invoices", "Invoice", value.InvoiceId, null),
        MarketplaceDeliveryState value => (value.TenantId, "invoices", "Invoice", value.InvoiceId, value.Version),
        PlatformConnection value => (value.TenantId, "connections", "PlatformConnection", value.Id, value.Version),
        MarketplaceQuestion value => (value.TenantId, "questions", "MarketplaceQuestion", value.Id, value.Version),
        _ => null
    };

    private void GuardAppendOnlyAudit()
    {
        if (ChangeTracker.Entries<AuditLog>().Any(x => x.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Audit log is append-only.");
        if (ChangeTracker.Entries<OrderStatusHistory>().Any(x => x.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Order status history is append-only.");
        if (ChangeTracker.Entries<ProductChangeEntry>().Any(x => x.State == EntityState.Modified))
            throw new InvalidOperationException("Product change entries are append-only.");
        // ReturnDecision is the mutable lifecycle snapshot for a queued external
        // action. Its evidence and audit records remain immutable; the decision
        // itself must transition through its processing states.
        if (ChangeTracker.Entries<InvoiceLine>().Any(x => x.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Invoice lines are immutable snapshots.");
        if (ChangeTracker.Entries<InvoicePartySnapshot>().Any(x => x.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Invoice party snapshots are immutable.");
        if (ChangeTracker.Entries<InvoiceDocument>().Any(x => x.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Invoice documents are immutable.");
        // An attempt is written as STARTED before the provider call and completed exactly once with its outcome; after that it is immutable.
        if (ChangeTracker.Entries<InvoiceSubmissionAttempt>().Any(x => x.State == EntityState.Deleted
            || (x.State == EntityState.Modified && !string.Equals(x.OriginalValues.GetValue<string>(nameof(InvoiceSubmissionAttempt.Outcome)), "STARTED", StringComparison.Ordinal))))
            throw new InvalidOperationException("Invoice submission attempts are append-only.");
        if (ChangeTracker.Entries<MarketplaceDelivery>().Any(x => x.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Marketplace delivery attempts are append-only.");
        if (ChangeTracker.Entries<MarketplaceInvoiceReferenceHistory>().Any(x => x.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Marketplace invoice reference history is append-only.");
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
        builder.Entity<TenantSetting>(entity =>
        {
            entity.ToTable("tenant_settings", "iam"); entity.HasKey(x => new { x.TenantId, x.Key });
            entity.Property(x => x.Key).HasMaxLength(128);
            entity.Property(x => x.ValueJson).HasColumnType("jsonb");
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
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
        builder.Entity<TenantInvitation>(entity =>
        {
            entity.ToTable("tenant_invitations", "iam"); entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.UserId }).IsUnique();
            entity.HasIndex(x => x.UserId);
            entity.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            entity.Property(x => x.Role).HasConversion(MembershipRoleConverter).HasMaxLength(24);
            entity.Property(x => x.Email).HasMaxLength(256); entity.Property(x => x.DisplayName).HasMaxLength(160);
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
            // ck_jobs_status (VERITABANI-DAGITIM-15): a mistyped status (raw SQL, a hand fix) would leave a job no screen or
            // sweep sees. The list is JobStatusConverter's output; StatusCheckConstraintModelTests keeps them equal.
            entity.ToTable("jobs", "integration", table =>
            {
                table.HasCheckConstraint("ck_job_attempt_bounds", "\"AttemptCount\" >= 0 AND \"MaxAttempts\" > 0 AND \"AttemptCount\" <= \"MaxAttempts\"");
                table.HasCheckConstraint("ck_jobs_status", "\"Status\" IN ('PENDING', 'LEASED', 'RETRY_SCHEDULED', 'BLOCKED', 'MANUAL_REVIEW', 'SUCCEEDED', 'DEAD', 'CANCELLED')");
            });
            entity.HasKey(x => x.Id);
            entity.Property(x => x.JobType).HasMaxLength(96); entity.Property(x => x.ResourceType).HasMaxLength(48).HasDefaultValue("jobs"); entity.Property(x => x.OperationType).HasMaxLength(64).HasDefaultValue("execute"); entity.Property(x => x.TriggerType).HasMaxLength(32).HasDefaultValue("system"); entity.Property(x => x.JobDedupKey).HasMaxLength(256);
            entity.Property(x => x.EffectIdempotencyKey).HasMaxLength(256); entity.Property(x => x.PayloadHash).HasMaxLength(128);
            entity.Property(x => x.Status).HasConversion(JobStatusConverter).HasMaxLength(24); entity.Property(x => x.CreatedAt).HasDefaultValueSql("now()").ValueGeneratedOnAdd(); entity.Property(x => x.MaxAttempts).HasDefaultValue(JobRetryPolicy.DefaultMaxAttempts); entity.Property(x => x.Version).IsConcurrencyToken();
            entity.Property(x => x.ProgressLabel).HasMaxLength(256); entity.Property(x => x.LockGroup).HasMaxLength(48);
            entity.HasIndex(x => new { x.TenantId, x.JobType, x.JobDedupKey }).IsUnique();
            entity.HasIndex(x => new { x.Status, x.Priority, x.AvailableAt, x.CreatedAt });
            // The job list pages a tenant's jobs newest first (CreatedAt DESC, Id DESC). Created CONCURRENTLY by
            // migration AddJobListAndAttemptIndexes.
            entity.HasIndex(x => new { x.TenantId, x.CreatedAt, x.Id }).IsDescending(false, true, true);
            entity.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<JobAttempt>(entity =>
        {
            entity.ToTable("job_attempts", "integration"); entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.JobId, x.AttemptNumber }).IsUnique();
            // A tenant's recent attempts (connection error history) read by time and joined to their job. It also
            // serves the tenant foreign key, so the former single-column TenantId index is gone.
            entity.HasIndex(x => new { x.TenantId, x.StartedAt }).IncludeProperties(x => x.JobId);
            entity.HasOne<IntegrationJob>().WithMany().HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<JobVariantResult>(entity =>
        {
            entity.ToTable("job_variant_results", "integration"); entity.HasKey(x => x.Id);
            entity.Property(x => x.Barcode).HasMaxLength(256); entity.Property(x => x.StockCode).HasMaxLength(256);
            entity.Property(x => x.Status).HasMaxLength(24); entity.Property(x => x.Reason).HasMaxLength(2000);
            entity.HasIndex(x => new { x.JobId, x.Barcode }).IsUnique();
            entity.HasIndex(x => x.TenantId);
            entity.HasOne<IntegrationJob>().WithMany().HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<JobReadSummary>(entity =>
        {
            entity.ToTable("job_read_summaries", "integration"); entity.HasKey(x => x.Id);
            entity.Property(x => x.Kind).HasMaxLength(32);
            entity.HasIndex(x => x.JobId).IsUnique();
            entity.HasIndex(x => x.TenantId);
            entity.HasOne<IntegrationJob>().WithMany().HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<InboxMessage>(entity =>
        {
            entity.ToTable("inbox_messages", "integration"); entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.Source, x.ExternalMessageId }).IsUnique();
            // The hourly retention deletes processed rows older than a month.
            entity.HasIndex(x => x.ProcessedAt).HasFilter("\"ProcessedAt\" IS NOT NULL");
            entity.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<ExternalEffectRecord>(entity =>
        {
            entity.ToTable("external_effect_records", "integration"); entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.EffectType, x.IdempotencyKey }).IsUnique();
            entity.Property(x => x.Action).HasMaxLength(64); entity.Property(x => x.ExpectedValue).HasMaxLength(256); entity.Property(x => x.Outcome).HasMaxLength(32);
            entity.HasIndex(x => x.CreatedAt).HasFilter("\"CompletedAt\" IS NULL");
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
        builder.Entity<PriceStockSendLog>(entity =>
        {
            entity.ToTable("price_stock_send_log", "integration"); entity.HasKey(x => x.Id);
            entity.Property(x => x.PlatformCode).HasMaxLength(32); entity.Property(x => x.Barcode).HasMaxLength(256); entity.Property(x => x.StockCode).HasMaxLength(256);
            entity.Property(x => x.PreviousQuantity).HasPrecision(19, 4); entity.Property(x => x.NewQuantity).HasPrecision(19, 4);
            entity.Property(x => x.PreviousSalePrice).HasPrecision(19, 4); entity.Property(x => x.NewSalePrice).HasPrecision(19, 4);
            entity.Property(x => x.PreviousListPrice).HasPrecision(19, 4); entity.Property(x => x.NewListPrice).HasPrecision(19, 4);
            entity.Property(x => x.Currency).HasMaxLength(3); entity.Property(x => x.Trigger).HasMaxLength(32); entity.Property(x => x.Result).HasMaxLength(24);
            entity.Property(x => x.ExternalRequestId).HasMaxLength(256); entity.Property(x => x.ErrorCode).HasMaxLength(100); entity.Property(x => x.ErrorMessage).HasMaxLength(500);
            // The log page reads a tenant's rows newest first (also the retention's per-tenant walk); a product's history
            // and a store's rows the same way; the poll finds its job's rows.
            entity.HasIndex(x => new { x.TenantId, x.CreatedAt, x.Id }).IsDescending(false, true, true);
            entity.HasIndex(x => new { x.TenantId, x.VariantId, x.CreatedAt, x.Id }).IsDescending(false, false, true, true);
            entity.HasIndex(x => new { x.TenantId, x.ConnectionId, x.CreatedAt, x.Id }).IsDescending(false, false, true, true);
            entity.HasIndex(x => x.JobId);
            entity.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<ProductChangeEntry>(entity =>
        {
            entity.ToTable("product_change_entries", "catalog"); entity.HasKey(x => x.Id);
            entity.Property(x => x.Kind).HasMaxLength(24); entity.Property(x => x.Source).HasMaxLength(32);
            entity.Property(x => x.PreviousStatus).HasMaxLength(16); entity.Property(x => x.NewStatus).HasMaxLength(16);
            entity.Property(x => x.PreviousSalePrice).HasPrecision(19, 4); entity.Property(x => x.NewSalePrice).HasPrecision(19, 4);
            entity.Property(x => x.PreviousListPrice).HasPrecision(19, 4); entity.Property(x => x.NewListPrice).HasPrecision(19, 4);
            entity.Property(x => x.Note).HasMaxLength(200); entity.Property(x => x.CorrelationId).HasMaxLength(128);
            entity.Property(x => x.Field).HasMaxLength(24); entity.Property(x => x.PreviousValue).HasMaxLength(400); entity.Property(x => x.NewValue).HasMaxLength(400);
            // Ürün Hareketleri reads a tenant's rows newest first; a product's purge goes by product. No foreign keys: a
            // history row never blocks deleting what it names.
            entity.HasIndex(x => new { x.TenantId, x.OccurredAt, x.Id }).IsDescending(false, true, true);
            entity.HasIndex(x => new { x.TenantId, x.ProductId });
        });
    }

    private static void ConfigureOperations(ModelBuilder builder)
    {
        builder.Entity<AuditLog>(entity =>
        {
            entity.ToTable("audit_logs", "ops"); entity.HasKey(x => x.Id); entity.Property(x => x.Id).UseIdentityAlwaysColumn();
            entity.Property(x => x.Action).HasMaxLength(96); entity.Property(x => x.Reason).HasMaxLength(512);
        });
        builder.Entity<PanelNotification>(entity =>
        {
            entity.ToTable("panel_notifications", "ops"); entity.HasKey(x => x.Id);
            entity.Property(x => x.ClientKey).HasMaxLength(120); entity.Property(x => x.Severity).HasMaxLength(16); entity.Property(x => x.Category).HasMaxLength(24);
            entity.Property(x => x.Title).HasMaxLength(200); entity.Property(x => x.Message).HasMaxLength(2000); entity.Property(x => x.Href).HasMaxLength(500);
            entity.Property(x => x.ActionLabel).HasMaxLength(80); entity.Property(x => x.PlatformCode).HasMaxLength(32); entity.Property(x => x.StoreName).HasMaxLength(160);
            // The bell's id of the notification: a repeat send updates the same row. The center reads a tenant's rows
            // newest first; the retention removes rows by age.
            entity.HasIndex(x => new { x.TenantId, x.ClientKey }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.CreatedAt, x.Id }).IsDescending(false, true, true);
            entity.HasIndex(x => x.CreatedAt);
            entity.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<OperationalIssue>(entity =>
        {
            entity.ToTable("operational_issues", "ops"); entity.HasKey(x => x.Id);
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(24); entity.HasIndex(x => new { x.TenantId, x.DedupeKey }).IsUnique();
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
            // K11: one active upload per image content, so a parallel upload of the same picture fails on save and reuses
            // the first one (CatalogEndpoints) instead of storing a second asset. Live had no duplicates when it was added.
            entity.HasIndex(x => new { x.TenantId, x.Sha256 }, "UX_file_assets_active_product_media_sha256").IsUnique().HasFilter("\"Classification\" = 'PRODUCT_MEDIA' AND \"ArchivedAt\" IS NULL");
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
            entity.Property(x => x.RecentJobCount).HasDefaultValue(0);
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
