namespace MarketplaceHub.Infrastructure.Jobs;

// KUYRUK-8 (16 Nov 2026): every place that creates a job row, with what it decides today. The factory and the enqueuer
// (IntegrationJobFactory, JobEnqueuer) build the rows; whether priority and lock group could be filled from
// JobPriorityPolicy and the job type registry automatically depends on each site matching them. This table is where
// that is read (JobEnqueueSitesTests pins it and fails when a new site appears in the code without a row here):
// - PriorityToday is what the site sets for SampleJobType (0 when it sets nothing); AutoFillIdentical says whether
//   JobPriorityPolicy.For(SampleJobType, UserInitiated) gives the same value.
// - LockGroupToday is what the site writes (null: nothing; the lease then uses the type's group for marketplace processor
//   types, but no group at all for invoicing, import and panel types, so filling one there would serialize them).
// - Dedup is the site's rule; for CallerChecked sites CallerRule says what the caller checks itself. Unifying those
//   rules is the owner's decision; they are listed here so it can be made from one place.
// Today's verdict: auto-fill would change priority (and for invoicing types the lock group) at 22 of the 46 sites, so
// the factory keeps taking both from the request; ON CONFLICT and a job event on insert would change how every
// EF-saved site behaves on a key race and what the outbox carries, so they were not added either.
public sealed record JobEnqueueSite(
    string Location,
    string SampleJobType,
    int PriorityToday,
    bool UserInitiated,
    bool AutoFillIdentical,
    string? LockGroupToday,
    string Dedup,
    string? CallerRule = null);

public static class JobEnqueueSites
{
    public const string CallerChecked = "CallerChecked";
    public const string ReuseAnyWithKey = "ReuseAnyWithKey";
    public const string ReuseHeldElseRetire = "ReuseHeldElseRetire";
    public const string OnConflict = "OnConflictDoNothing";

    public static readonly IReadOnlyList<JobEnqueueSite> All =
    [
        new("InvoicingEndpoints.QueueManualOrderInvoiceDeliveryAsync", "MANUAL_ORDER_INVOICE_DELIVERY", -3, true, false, null, CallerChecked,
            "Reuses an open job found by key or effect key; after a finished one adds \"{key}:r{state.Version}\"; a duplicate-key save returns the concurrent open jobs."),
        new("SalesContext.VerifyLaterAsync (ExternalEffectReconcilePolicy.NewJob)", "EXTERNAL_EFFECT_RECONCILE", -3, false, false, "orders|returns", CallerChecked,
            "Any job of the tenant with the key, any type and status, means no new job."),
        new("ExternalEffectSweep.SweepAsync (ExternalEffectReconcilePolicy.NewJob)", "EXTERNAL_EFFECT_RECONCILE", -3, false, false, "orders|returns", CallerChecked,
            "The next unused verification key, chosen from the statuses of the record's existing verification jobs."),
        new("ImportService.EnqueuePreviewAsync", "IMPORT_PREVIEW", 0, false, false, null, ReuseAnyWithKey),
        new("ImportService.EnqueueApplyAsync", "IMPORT_APPLY", 0, false, false, null, ReuseAnyWithKey),
        new("CatalogService.EnqueuePublicationAsync", "PRODUCT_CREATE", 2, true, true, null, CallerChecked,
            "Key lookup, then ReuseOrRetirePublicationJobAsync: an open (or succeeded, when asked) job is reused, else its key is retired; a duplicate-key save returns the concurrent job."),
        new("CatalogService.EnqueueProductUpdateCoreAsync", "PRODUCT_UPDATE", 2, true, true, null, CallerChecked,
            "As the publication, never reusing a succeeded job; SavePublicationJobAsync returns the concurrent job on a duplicate key."),
        new("CatalogService.EnqueuePazaramaSaleStatusAsync", "PRODUCT_ARCHIVE", 4, true, true, null, CallerChecked,
            "Open-job check on the key prefix; the key carries a job id, so it is unique by construction."),
        new("CatalogService.RequestCardPublicationStatusRefreshAsync", "PRODUCT_APPROVAL_RECONCILE", 4, true, true, null, CallerChecked,
            "A leased job by key or manual prefix is returned, a pending one requeued; otherwise a key with the profile version; concurrency and duplicate saves are caught."),
        new("CatalogService.EnqueuePttAvmProductUpdateAsync", "PRODUCT_UPDATE", 2, true, true, null, CallerChecked,
            "As the product update."),
        new("ConnectionReferencePlan.QueueForSavedCategoryMappingAsync", "TRENDYOL_REFERENCE_SYNC", 5, true, true, null, CallerChecked,
            "OpenReadExistsAsync (an open read with the same payload fragments); the key carries a millisecond timestamp."),
        new("ReferenceSyncService.QueueMappedCategoryReferencesAsync", "TRENDYOL_REFERENCE_SYNC", 5, false, true, null, CallerChecked,
            "Taken keys (any status) and OpenReadExistsAsync; inherits the triggering job's priority when there is one (then it may differ)."),
        new("ReferenceSnapshotBackfill.QueueMissingAsync", "TRENDYOL_REFERENCE_SYNC", 5, false, true, null, CallerChecked,
            "KeyTakenAsync then OpenReadExistsAsync; the key carries the day; a unique-violation catch absorbs races."),
        new("OperationalDataMaintenanceService.RequestFullResetAsync", "CONNECTION_FULL_RESET", 3, true, true, null, CallerChecked,
            "An open reset of the connection means no new one; Guid key."),
        new("HepsiburadaExchangeInvoiceResend.QueueAsync", "INVOICE_MARKETPLACE_DELIVERY", -2, false, false, null, CallerChecked,
            "The key in the change tracker or the database, any status, means no new job."),
        new("InvoicingJobProcessor.QueueDeliveryRetry (InvoiceDeliveryRetryReader.NewDocumentFetchJob)", "INVOICE_DOCUMENT_FETCH", -2, false, false, null, CallerChecked,
            "Only without an active job of the entry; the key carries the attempt count and unix seconds."),
        new("InvoicingBillingService.RetryDeliveryAsync (InvoiceDeliveryRetryReader.NewDocumentFetchJob)", "INVOICE_DOCUMENT_FETCH", -3, false, false, null, CallerChecked,
            "Replay check on the fetch and delivery keys, then 409 while an active job exists."),
        new("InventoryService.EnqueuePriceInventorySyncAsync", "PRICE_INVENTORY_SYNC", -3, true, false, null, ReuseHeldElseRetire),
        new("InventoryService.EnqueueHepsiburadaStagePriceInventoryAsync", "PRICE_INVENTORY_SYNC", -3, true, false, null, ReuseAnyWithKey),
        new("InventoryService.EnqueueProductPriceInventorySyncAsync", "PRICE_INVENTORY_SYNC", -3, true, false, null, ReuseHeldElseRetire),
        new("InventoryService.EnqueuePttAvmProductPriceInventoryAsync", "PRICE_INVENTORY_SYNC", -3, true, false, null, ReuseHeldElseRetire),
        new("InvoicingBillingService (order invoice check)", "ORDER_INVOICE_RECONCILIATION", -3, true, false, null, ReuseAnyWithKey),
        new("InvoicingBillingService.AddJob", "INVOICE_SUBMIT", -3, true, false, null, ReuseAnyWithKey),
        new("InvoicingJobProcessor.QueuePendingMarketplaceDeliveryRecovery", "INVOICE_MARKETPLACE_DELIVERY", -2, false, false, null, CallerChecked,
            "Recovery keys already taken (any status) and an active delivery job holding the package (InvoiceDeliveryJobHygiene.Holds)."),
        new("InvoicingJobProcessor.EnqueueAutomaticJob", "INVOICE_RECONCILE", -3, false, false, null, CallerChecked,
            "KeyTakenAsync, then for a delivery AutomaticDeliveryHeldAsync."),
        new("JobOperationsService.EnqueueOneTimeInvoiceDeliveryAsync", "INVOICE_MARKETPLACE_DELIVERY", -3, true, false, null, CallerChecked,
            "An existing key, any status, is returned; the only site that writes a job event (AddOutboxEvent)."),
        new("ListingVerificationService.AddListingLookupJobAsync", "LISTING_LOOKUP", 6, false, true, null, ReuseAnyWithKey),
        new("ProductJobService.EnsureApprovalReconciliationJob", "PRODUCT_APPROVAL_RECONCILE", 4, true, true, null, ReuseAnyWithKey),
        new("PriceStockService.QueueAutomaticPushAsync", "PRICE_INVENTORY_SYNC", 1, true, true, null, CallerChecked,
            "AutomaticSendMayRetire: keep the existing job, or retire its key."),
        new("MarketplaceJobProcessor.QueueHepsiburadaOrderReadsAsync", "HEPSIBURADA_ORDER_SYNC", 0, false, true, null, ReuseAnyWithKey),
        new("MarketplaceConnectionService.AddBootstrapJob", "TRENDYOL_ORDER_RECOVERY_SYNC", -1, false, false, null, CallerChecked,
            "The key prefix carries the connection version just bumped, so it is unique by construction."),
        new("SalesJobService.EnqueueStageTestOrderAsync", "TRENDYOL_STAGE_TEST_ORDER", 3, true, true, null, CallerChecked,
            "A job with the same effect key is returned."),
        new("SalesJobService.Enqueue (behind active work)", "TRENDYOL_ORDER_SYNC", -3, true, false, null, CallerChecked,
            "Same-type open-job check (prefix match for recurring reads, which get a millisecond suffix) and the execution-group conflict rule."),
        new("SalesJobService.Enqueue (EnqueueRead)", "TRENDYOL_REFERENCE_SYNC", 5, true, true, null, CallerChecked,
            "As above; identical except the six order syncs (-3 against 0)."),
        new("ShipmentService.EnqueueShipmentActionAsync", "SHIPMENT_ACTION", -3, true, false, null, CallerChecked,
            "A job with the same effect key is returned."),
        new("ShipmentService.EnqueueCommonLabelAsync", "TRENDYOL_COMMON_LABEL", -3, true, false, null, CallerChecked,
            "An existing ShipmentDocumentAttempt with the idempotency key is returned."),
        new("ShipmentService.EnqueueLabelCapabilityProbeAsync", "TRENDYOL_CAPABILITY_PROBE", 3, true, true, null, CallerChecked,
            "A job with the same effect key is returned."),
        new("MarketplaceWebhookService (WebhookInboxWriter raw insert)", "TRENDYOL_WEBHOOK_INGEST", 0, false, true, "returns|null", OnConflict),
        new("MarketplaceWebhookService.QueueListingVerifyAsync", "LISTING_VERIFY", 6, false, true, null, ReuseAnyWithKey),
        new("PanelFactoryResetService.RequestAsync", "PANEL_FACTORY_RESET", -3, true, false, null, CallerChecked,
            "Advisory transaction lock, then an open-job check; Guid key."),
        new("ScheduledJobProducer.EnqueueDueAsync (sync policies)", "TRENDYOL_ORDER_SYNC", 0, false, true, null, ReuseAnyWithKey),
        new("ScheduledJobProducer.EnqueueDueAsync (invoice due scan)", "INVOICE_DUE_SCAN", 4, false, true, null, ReuseAnyWithKey),
        new("ScheduledJobProducer.RetestTransientDraftsAsync", "TRENDYOL_CONNECTION_TEST", 3, false, true, null, CallerChecked,
            "Open-job check on the key prefix; the key carries unix seconds."),
        new("ScheduledJobProducer.RetestFailedOperationalConnectionsAsync", "TRENDYOL_CONNECTION_TEST", 3, false, true, null, CallerChecked,
            "An open test of that type means no new one; the key carries seconds."),
        new("ScheduledJobProducer.RunNowAsync", "TRENDYOL_ORDER_RECOVERY_SYNC", 6, true, true, null, CallerChecked,
            "A leased or due pending job on the scheduled or manual prefix is returned; Guid key."),
        new("StockProjectionOutbox.AddDispatchesAsync", "STOCK_PROJECTION_DISPATCH", 1, true, true, null, CallerChecked,
            "Existing keys of the tenant (any type, any status) and the change tracker."),
        new("CatalogImportService.QueueProductImportRetryAsync", "PRODUCT_SYNC", 5, false, true, null, CallerChecked,
            "A job of the type with the try's key in any status; the key names the connection, the model and the attempt (ISLEYICI-1).")
    ];

    // How many job-creating calls (JobEnqueuer.EnqueueAsync / JobEnqueuer.Add / IntegrationJobFactory.New, or a raw insert)
    // each file holds today. A helper used by several sites counts once in its own file.
    public static readonly IReadOnlyDictionary<string, int> CreatingCallsPerFile = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["src/MarketplaceHub.Api/Invoicing/InvoicingEndpoints.ManualDocumentDelivery.cs"] = 1,
        ["src/MarketplaceHub.Application/SynchronizationPolicies.cs"] = 1,
        ["src/MarketplaceHub.Infrastructure/Imports/ImportService.cs"] = 2,
        ["src/MarketplaceHub.Infrastructure/Persistence/CatalogService.PttAvmProductUpdate.cs"] = 1,
        ["src/MarketplaceHub.Infrastructure/Persistence/CatalogService.cs"] = 4,
        ["src/MarketplaceHub.Infrastructure/Persistence/ConnectionReferencePlan.cs"] = 1,
        ["src/MarketplaceHub.Infrastructure/Persistence/HepsiburadaExchangeInvoiceResend.cs"] = 1,
        ["src/MarketplaceHub.Infrastructure/Persistence/InventoryService.PttAvmProductPriceInventory.cs"] = 1,
        ["src/MarketplaceHub.Infrastructure/Persistence/InventoryService.cs"] = 3,
        ["src/MarketplaceHub.Infrastructure/Persistence/InvoiceDeliveryRetryReader.cs"] = 1,
        ["src/MarketplaceHub.Infrastructure/Persistence/InvoicingBillingService.cs"] = 2,
        ["src/MarketplaceHub.Infrastructure/Persistence/InvoicingJobProcessor.cs"] = 2,
        ["src/MarketplaceHub.Infrastructure/Persistence/JobOperationsService.cs"] = 1,
        ["src/MarketplaceHub.Infrastructure/Persistence/JobProcessing/ListingVerificationService.cs"] = 1,
        ["src/MarketplaceHub.Infrastructure/Persistence/JobProcessing/ProductJobService.ProductCreate.cs"] = 1,
        ["src/MarketplaceHub.Infrastructure/Persistence/JobProcessing/CatalogImportService.ProductImportRetry.cs"] = 1,
        ["src/MarketplaceHub.Infrastructure/Persistence/JobProcessing/PriceStockService.cs"] = 1,
        ["src/MarketplaceHub.Infrastructure/Persistence/JobProcessing/MarketplaceJobProcessor.Webhooks.cs"] = 1,
        ["src/MarketplaceHub.Infrastructure/Persistence/MarketplaceConnectionService.cs"] = 1,
        ["src/MarketplaceHub.Infrastructure/Persistence/SalesJobService.Jobs.cs"] = 1,
        ["src/MarketplaceHub.Infrastructure/Persistence/MarketplaceWebhookService.cs"] = 2,
        ["src/MarketplaceHub.Infrastructure/Persistence/PanelFactoryResetService.cs"] = 1,
        ["src/MarketplaceHub.Infrastructure/Persistence/ScheduledJobProducer.cs"] = 3,
        ["src/MarketplaceHub.Infrastructure/Persistence/StockProjectionOutbox.cs"] = 1,
        ["src/MarketplaceHub.Infrastructure/Persistence/WebhookInboxWriter.cs"] = 1
    };
}
