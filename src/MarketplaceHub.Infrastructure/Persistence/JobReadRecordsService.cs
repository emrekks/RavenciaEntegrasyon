using System.Globalization;
using MarketplaceHub.Application;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

// İşlem Takibi › İş detayı › Çekilen Kayıtlar: a product read's records as it stored them (one job_read_summaries row,
// read once, no tracking), filtered and paged in the stored order (problems first, then new, updated, unchanged; within
// each the order the read met them), so a record shows on one page only. Read-only.
public sealed class JobReadRecordsService(AppDbContext db) : IJobReadRecordsService
{
    private const string ProductKind = "PRODUCT";
    private const string JobNotFoundCode = "JOB_NOT_FOUND";
    private const string JobNotFoundMessage = "İşlem kaydı bulunamadı; silinmiş ya da kapatılmış olabilir.";

    private sealed record Stored(string Kind, IReadOnlyList<JobReadRecord> Records, JobReadCounts Counts, int Omitted, MarketplaceHub.Domain.IntegrationJob Job);

    public async Task<ServiceResult<JobReadRecordsPageView>> PageAsync(Guid tenantId, Guid jobId, JobReadRecordsQuery query, CancellationToken cancellationToken)
    {
        var stored = await StoredAsync(tenantId, jobId, cancellationToken);
        if (!stored.Succeeded) return ServiceResult<JobReadRecordsPageView>.Fail(stored.Error!.Code, stored.Error.Message, stored.Error.Status);
        var value = stored.Value!;
        var matching = Filtered(value.Records, query).ToList();
        var page = query.EffectivePage;
        var pageSize = query.EffectivePageSize;
        var items = matching.Skip((int)Math.Min(int.MaxValue, (long)(page - 1) * pageSize)).Take(pageSize).Select(View).ToList();
        var counts = value.Counts;
        return ServiceResult<JobReadRecordsPageView>.Ok(new(value.Kind, items, page, pageSize, matching.Count, value.Records.Count, counts.Received, value.Omitted,
            new JobReadRecordCounts(counts.Inserted, counts.Updated, counts.Unchanged, counts.Skipped, counts.Failed)));
    }

    public async Task<ServiceResult<JobExportFile>> ExportAsync(Guid tenantId, Guid jobId, JobReadRecordsQuery query, CancellationToken cancellationToken)
    {
        var stored = await StoredAsync(tenantId, jobId, cancellationToken);
        if (!stored.Succeeded) return ServiceResult<JobExportFile>.Fail(stored.Error!.Code, stored.Error.Message, stored.Error.Status);
        var value = stored.Value!;
        var matching = Filtered(value.Records, query).ToList();
        if (matching.Count > JobReadRecordsQuery.MaximumExportRows)
            return ServiceResult<JobExportFile>.Fail("JOB_EXPORT_TOO_LARGE",
                $"Liste {matching.Count:N0} satır; Excel en çok {JobReadRecordsQuery.MaximumExportRows:N0} satır alır. Arama ya da sorun süzgeciyle daraltıp tekrar deneyin.", 422);
        var file = await JobExportWorkbook.WriteAsync("Çekilen Kayıtlar", Header, Widths, Rows(matching), cancellationToken);
        return ServiceResult<JobExportFile>.Ok(new(JobExportWorkbook.FileName(value.Job, "cekilen-kayitlar"), file));
    }

    private static readonly IReadOnlyList<string> Header = ["Model Kodu", "Barkodlar", "Varyant Sayısı", "Sonuç", "Neden"];
    private static readonly IReadOnlyList<double> Widths = [24, 30, 14, 16, 60];

    private static readonly Dictionary<string, string> Results = new(StringComparer.Ordinal)
    {
        [JobReadOutcomes.New] = "Yeni",
        [JobReadOutcomes.Updated] = "Güncellendi",
        [JobReadOutcomes.Unchanged] = "Değişmedi",
        [JobReadOutcomes.Skipped] = "Atlandı",
        [JobReadOutcomes.Failed] = "Hatalı"
    };

#pragma warning disable CS1998 // The rows are in memory already; the writer takes them as a stream.
    private static async IAsyncEnumerable<IReadOnlyList<object?>> Rows(IEnumerable<JobReadRecord> records)
    {
        foreach (var record in records)
        {
            var view = View(record);
            // Every variant's barcode where the read kept them; a record stored before that knows only the first one.
            var barcodes = record.Barcodes is { Count: > 0 } all ? string.Join(", ", all)
                : view.FirstBarcode is null ? null : view.BarcodeCount is > 1 ? $"{view.FirstBarcode} (+{view.BarcodeCount - 1})" : view.FirstBarcode;
            yield return [view.ModelCode, barcodes, view.VariantCount is { } variants ? (object)(long)variants : null, Results.GetValueOrDefault(view.Result, view.Result), view.Reason];
        }
    }
#pragma warning restore CS1998

    // The job (in the tenant) and its product read's stored records; a job of another tenant is not found.
    private async Task<ServiceResult<Stored>> StoredAsync(Guid tenantId, Guid jobId, CancellationToken cancellationToken)
    {
        var job = await db.IntegrationJobs.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == jobId, cancellationToken);
        if (job is null) return ServiceResult<Stored>.Fail(JobNotFoundCode, JobNotFoundMessage, 404);
        var row = await db.JobReadSummaries.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.JobId == jobId, cancellationToken);
        var kind = row?.Kind ?? MarketplaceJobProcessor.ReadRecordKind(job.JobType);
        if (kind != ProductKind)
            return ServiceResult<Stored>.Fail("JOB_READ_RECORDS_NOT_PRODUCT_READ", "Çekilen kayıt listesi yalnız ürün çekme işlerinde vardır.", 400);
        // A product read that has not stored anything yet (queued, or run before reads recorded it): an empty list.
        if (row is null) return ServiceResult<Stored>.Ok(new(ProductKind, [], JobReadCounts.Zero, 0, job));
        var counts = new JobReadCounts(row.Received, row.Inserted, row.Updated, row.Unchanged, row.Skipped, row.Failed);
        return ServiceResult<Stored>.Ok(new(row.Kind, JobReadRecorder.Deserialize(row.RecordsJson), counts, row.Omitted, job));
    }

    private static IEnumerable<JobReadRecord> Filtered(IEnumerable<JobReadRecord> records, JobReadRecordsQuery query)
    {
        var term = query.Search?.Trim();
        if (term is { Length: > JobReadRecordsQuery.MaximumSearchLength }) term = term[..JobReadRecordsQuery.MaximumSearchLength];
        foreach (var record in records)
        {
            if (query.OnlyProblems && record.Outcome is not (JobReadOutcomes.Failed or JobReadOutcomes.Skipped)) continue;
            if (!string.IsNullOrEmpty(term)
                && !record.Reference.Contains(term, StringComparison.OrdinalIgnoreCase)
                && !(Barcodes(record.Secondary).First?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
                && !(record.Barcodes?.Any(barcode => barcode.Contains(term, StringComparison.OrdinalIgnoreCase)) ?? false))
                continue;
            yield return record;
        }
    }

    internal static JobReadRecordView View(JobReadRecord record)
    {
        var (first, count) = Barcodes(record.Secondary);
        return new(record.Reference, first, count, VariantCount(record.Status), record.Outcome, string.IsNullOrWhiteSpace(record.Reason) ? null : record.Reason);
    }

    // The read stores the first barcode and how many more variants came with it: "8690001 +3" (CatalogImportService).
    internal static (string? First, int Count) Barcodes(string? secondary)
    {
        if (string.IsNullOrWhiteSpace(secondary)) return (null, 0);
        var text = secondary.Trim();
        var plus = text.LastIndexOf(" +", StringComparison.Ordinal);
        if (plus > 0 && int.TryParse(text.AsSpan(plus + 2), NumberStyles.None, CultureInfo.InvariantCulture, out var more))
            return (text[..plus], more + 1);
        return (text, 1);
    }

    // "4 varyant" → 4; anything else is not a count.
    internal static int? VariantCount(string? status)
    {
        if (string.IsNullOrWhiteSpace(status)) return null;
        var text = status.Trim();
        var space = text.IndexOf(' ', StringComparison.Ordinal);
        return int.TryParse(space < 0 ? text : text[..space], NumberStyles.None, CultureInfo.InvariantCulture, out var count) ? count : null;
    }
}
