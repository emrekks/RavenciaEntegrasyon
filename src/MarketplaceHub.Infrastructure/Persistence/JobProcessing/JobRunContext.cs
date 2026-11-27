namespace MarketplaceHub.Infrastructure.Persistence;

// ISLEYICI-9 stage 2 (16 Nov 2026): the state of one run of a job (its counters, the product import's summary, the orders
// to read again, the read-record mode). It used to be thirty-odd fields of the processor, reset one by one in
// ResetTelemetry, and a field that reset missed (the re-read list, the read-record reasons) could carry over into the
// next job run by the same processor. ProcessAsync now starts every run with a new context.
internal sealed class JobRunContext
{
    public int RequestCount;
    public int ReceivedCount;
    public int ChangedCount;
    public int InsertedCount;
    public int UpdatedCount;
    public int SkippedCount;
    public int FailedCount;
    // An open-order scan could not read an order or package (OpenOrderReadTally): its label says so, without a count.
    public bool OpenOrderReadFailed;
    public int RetryCount;
    public int RateLimitCount;
    public int ImportProcessedCount;
    public int ImportSkippedCount;
    public int ImportFailedCount;
    // A product import run reports to its cursor what the job reports (received, processed, skipped, failed), counted
    // over the whole import, not each product the pages brought as changed (audit 2026-11-06 O3).
    public bool ProductImportTelemetry;
    public int ImportReceivedCount;
    // Sizes (barcodes) Trendyol lists on an owner-edited card that a plain pull kept whole and did not add: the run's
    // count for the progress line, the last product's for its read record.
    public int UnaddedSizeCount;
    public int ProductUnaddedSizeCount;
    // The result line of a product pull, by mode (owner, 12 Nov 2026): what the focused price/stock path changed, what
    // "Yeni Ürünleri Aktar" added, what "Ürün Eşleme" linked.
    public MarketplaceJobProcessor.ImportSummaryMode ImportSummaryMode;
    public readonly HashSet<Guid> PriceChangedVariantIds = [];
    // "Fiyat ve Stokları Güncelle": the panel prices a product's variants took, applied to the other connections' offers in
    // one query per product (CatalogImportService.FlushPanelRepricesAsync), and the connections' platforms, read once.
    public readonly Dictionary<Guid, PanelPriceOffers.PanelPriceChange> PendingPanelReprices = [];
    public Dictionary<Guid, string>? TenantPlatforms;
    public readonly HashSet<Guid> StockChangedVariantIds = [];
    public int UnchangedVariantCount;
    public int NewProductCount;
    public int NewSizeCount;
    public int AlreadyInPanelCount;
    public int ProductImportedNewVariantCount;
    public int MappedCount;
    public int AlreadyMappedCount;
    public int NoCounterpartCount;
    public int MappingConflictCount;
    // When the marketplace stock of the model group being finalized was read (K-2); null outside a catalog import.
    public DateTimeOffset? CatalogStockReadAt;
    // Trendyol orders an integrity check refused during this run (T2-O8), and orders of any marketplace stored with a
    // product line that could not be read; the order read hands them to its re-read list.
    public readonly HashSet<string> RereadOrderIds = new(StringComparer.Ordinal);
    public bool OrderRereadRan;
    // Whether this run records what it read (Çekilen Kayıtlar) and under which kind, and why the return being stored
    // was skipped or failed.
    public bool ReadRecordingEnabled;
    public string ReadRecordingKind = "ORDER";
    public string? ReadReturnSkipReason;
    public string? ReadReturnFailReason;
    // ISLEYICI-9 stage 3a: the records this run read (Çekilen Kayıtlar), flushed to the job by the processor.
    public readonly MarketplaceHub.Application.JobReadRecorder ReadRecorder = new();
    // The bell's new-order notifications (NewOrderNoticePolicy): whether this run raises them (off outside a job run, for
    // the activation's first import and for full reads), its own id, and the orders it created by store. NewOrderNotice
    // writes them once at the end of the run.
    public bool NewOrderNotices;
    public Guid NewOrderNoticeRunId = Guid.CreateVersion7();
    public readonly Dictionary<Guid, HashSet<Guid>> NewOrderIdsByConnection = [];
    // Whether each store's order read had completed once before this run noted its first order (stored cursor state).
    public readonly Dictionary<Guid, bool> NewOrderNoticeCaughtUp = [];

    public void NoteNewOrder(Guid connectionId, Guid orderId)
    {
        if (!NewOrderNotices) return;
        if (!NewOrderIdsByConnection.TryGetValue(connectionId, out var ids)) NewOrderIdsByConnection[connectionId] = ids = [];
        ids.Add(orderId);
    }

    // ISLEYICI-9 stage 3a: the run's request and record counts, kept here so a service working for the run (order,
    // return or catalog ingestion) counts into the same run as the processor.
    public void TrackRequest() => RequestCount++;

    public void TrackReceived(bool changed = true)
    {
        ReceivedCount++;
        if (changed) ChangedCount++;
        else SkippedCount++;
    }

    public void TrackSkipped() => SkippedCount++;

    public void TrackResultFailure(MarketplaceHub.Application.AdapterError? error)
    {
        FailedCount++;
        if (error?.Class == MarketplaceHub.Application.AdapterErrorClass.RateLimit) RateLimitCount++;
        if (error?.Class is MarketplaceHub.Application.AdapterErrorClass.TransientNetwork or MarketplaceHub.Application.AdapterErrorClass.RateLimit or MarketplaceHub.Application.AdapterErrorClass.Remote5xx) RetryCount++;
    }
}
