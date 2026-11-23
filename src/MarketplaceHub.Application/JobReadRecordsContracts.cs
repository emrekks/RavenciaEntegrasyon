namespace MarketplaceHub.Application;

// İşlem Takibi › İş detayı › Çekilen Kayıtlar: the products a product read pulled, page by page and as Excel, from what
// the read stored (job_read_summaries). Read-only. A read before 19 Nov 2026 stored only its first 100 records (problems
// first): the answer says how many the job read (ReceivedCount) and how many are kept (StoredCount) instead of
// pretending the list is whole; the counts are always the job's own, every record included.

// onlyProblems keeps the skipped and failed records; search names a model code or a barcode (contains, any case);
// page is 1-based, pageSize 1-200 (default 25).
public sealed record JobReadRecordsQuery(bool OnlyProblems = false, string? Search = null, int? Page = null, int? PageSize = null)
{
    public const int DefaultPageSize = 25;
    public const int MaximumPageSize = 200;
    public const int MaximumPage = 100_000;
    public const int MaximumSearchLength = 100;
    public const int MaximumExportRows = 100_000;
    public int EffectivePage => Math.Clamp(Page ?? 1, 1, MaximumPage);
    public int EffectivePageSize => Math.Clamp(PageSize ?? DefaultPageSize, 1, MaximumPageSize);
}

// One product the read pulled: its model code (the marketplace's product id when it has none), its first barcode and
// how many variants (each with its barcode) came with it, what the read did with it (JobReadOutcomes: NEW, UPDATED,
// UNCHANGED, SKIPPED, FAILED) and why it was skipped or failed. A null count was not recorded.
public sealed record JobReadRecordView(string ModelCode, string? FirstBarcode, int? BarcodeCount, int? VariantCount, string Result, string? Reason);

// The job's own counts, every record it read included (not only the stored ones).
public sealed record JobReadRecordCounts(int New, int Updated, int Unchanged, int Skipped, int Failed);

// Items: the page of the stored records the filters keep; TotalCount: how many stored records they keep (the pages);
// StoredCount: every record the job kept; ReceivedCount: every record the job read (the counts' sum). StoredCount below
// ReceivedCount: the read kept only part of its list (OmittedCount records are counted but not listed).
public sealed record JobReadRecordsPageView(
    string Kind,
    IReadOnlyList<JobReadRecordView> Items,
    int Page,
    int PageSize,
    int TotalCount,
    int StoredCount,
    int ReceivedCount,
    int OmittedCount,
    JobReadRecordCounts Counts);

public interface IJobReadRecordsService
{
    Task<ServiceResult<JobReadRecordsPageView>> PageAsync(Guid tenantId, Guid jobId, JobReadRecordsQuery query, CancellationToken cancellationToken);

    // Every stored record the filters keep, as an .xlsx (Page and PageSize are ignored).
    Task<ServiceResult<JobExportFile>> ExportAsync(Guid tenantId, Guid jobId, JobReadRecordsQuery query, CancellationToken cancellationToken);
}
