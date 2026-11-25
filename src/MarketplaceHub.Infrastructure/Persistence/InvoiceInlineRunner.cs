using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

public sealed class InvoiceInlineRunner(AppDbContext db, IJobLeaseService leases, IInvoicingJobProcessor processor, TimeProvider timeProvider) : IInvoiceInlineRunner
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(6);
    private const int MaxSteps = 6;
    private static readonly string[] FollowUpJobTypes = [InvoicingJobTypes.InvoiceReconcile, InvoicingJobTypes.InvoiceDocumentFetch, InvoicingJobTypes.MarketplaceDelivery];

    public async Task<InlineInvoiceOutcome> RunAsync(Guid tenantId, Guid invoiceId, Guid firstJobId, DateTimeOffset deadline, CancellationToken cancellationToken)
    {
        // The caller's budget (shared by every invoice of the request) caps this invoice's own budget.
        var ownDeadline = timeProvider.GetUtcNow().Add(Budget);
        if (deadline > ownDeadline) deadline = ownDeadline;
        var jobId = firstJobId;
        for (var step = 0; step < MaxSteps; step++)
        {
            if (step > 0 && timeProvider.GetUtcNow() >= deadline) break;
            var leased = await leases.TryLeaseAsync(JobRetryPolicy.DefaultLeaseDuration, null, null, cancellationToken, null, jobId);
            if (leased is null)
            {
                // The worker (or an earlier call) already holds or finished this job.
                if (step == 0) return await DescribeAsync(tenantId, invoiceId, null, cancellationToken);
                break;
            }
            JobExecutionResult result;
            try { result = await processor.ProcessAsync(leased.TenantId, leased.ConnectionId, leased.JobType, leased.PayloadJson, leased.CorrelationId, cancellationToken); }
            catch (OperationCanceledException)
            {
                // Server shutdown or the budget's hard stop: the job goes back to the queue for the worker,
                // as on a worker shutdown, instead of staying leased until the lease expires.
                try { await leases.ReleaseAsync(leased.Id, leased.LeaseToken, CancellationToken.None); }
                catch (Exception) { /* the lease expires and the worker takes the job then */ }
                throw;
            }
            catch (Exception exception) when (exception is System.Text.Json.JsonException or ArgumentException or InvalidOperationException)
            { result = JobExecutionResult.Blocked("INVALID_JOB_STATE", $"İşlem verisi veya mevcut durum doğrulaması başarısız oldu. ({exception.GetType().Name}: {exception.Message.ReplaceLineEndings(" ")})"); }
            catch (Exception) { result = JobExecutionResult.Retry("INLINE_EXECUTION_FAILED", "Geçici bir hata oluştu; işlem otomatik olarak yeniden denenecek."); }
            // The provider usually accepts an e-archive invoice within seconds; poll the read-only status check here instead
            // of leaving it for the worker's slow retry. The poll runs while this call still holds the job's lease (scan
            // 25 Nov 2026 C-6: it used to run after the job was handed back, beside a worker that could lease it too).
            if (result.Kind == JobCompletionKind.Retry && result.ErrorCode == "INVOICE_REMOTE_PENDING" && leased.JobType == InvoicingJobTypes.InvoiceReconcile)
            {
                try { result = await PollPendingAsync(leased, result, deadline, cancellationToken); }
                catch (OperationCanceledException)
                {
                    await leases.CompleteAsync(leased.Id, leased.LeaseToken, result, CancellationToken.None);
                    throw;
                }
            }
            // The step ran (an invoice may have been cut): its outcome is recorded whatever happens to the request.
            await leases.CompleteAsync(leased.Id, leased.LeaseToken, result, CancellationToken.None);
            if (!result.Succeeded) return await DescribeAsync(tenantId, invoiceId, result, cancellationToken);
            if (timeProvider.GetUtcNow() >= deadline) break;

            var next = await NextJobAsync(tenantId, invoiceId, deadline, cancellationToken);
            if (next is null) break;
            jobId = next.Value;
        }
        return await DescribeAsync(tenantId, invoiceId, null, cancellationToken);
    }

    private async Task<JobExecutionResult> PollPendingAsync(LeasedJob job, JobExecutionResult pending, DateTimeOffset deadline, CancellationToken cancellationToken)
    {
        var result = pending;
        while (result.Kind == JobCompletionKind.Retry && result.ErrorCode == "INVOICE_REMOTE_PENDING" && timeProvider.GetUtcNow().AddSeconds(6) < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(4), cancellationToken);
            try { result = await processor.ProcessAsync(job.TenantId, job.ConnectionId, job.JobType, job.PayloadJson, job.CorrelationId, cancellationToken); }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { return pending; }
        }
        return result;
    }

    private async Task<Guid?> NextJobAsync(Guid tenantId, Guid invoiceId, DateTimeOffset deadline, CancellationToken cancellationToken)
    {
        var needle = invoiceId.ToString();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            db.ChangeTracker.Clear();
            var now = timeProvider.GetUtcNow();
            var pending = await db.IntegrationJobs.AsNoTracking()
                .Where(job => job.TenantId == tenantId && FollowUpJobTypes.Contains(job.JobType) && job.PayloadJson.Contains(needle)
                    && (job.Status == JobStatus.Pending || job.Status == JobStatus.RetryScheduled))
                .OrderBy(job => job.AvailableAt)
                .Select(job => new { job.Id, job.AvailableAt })
                .FirstOrDefaultAsync(cancellationToken);
            if (pending is null) return null;
            if (pending.AvailableAt <= now) return pending.Id;
            var wait = pending.AvailableAt - now;
            if (wait > MaxWait || now + wait >= deadline) return null;
            await Task.Delay(wait, cancellationToken);
        }
        return null;
    }

    private async Task<InlineInvoiceOutcome> DescribeAsync(Guid tenantId, Guid invoiceId, JobExecutionResult? failure, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        var invoice = await db.Invoices.AsNoTracking().Where(item => item.TenantId == tenantId && item.Id == invoiceId)
            .Select(item => new { item.Status, item.LastErrorCode, item.InvoiceNumber }).SingleOrDefaultAsync(cancellationToken);
        if (failure is not null)
        {
            var code = failure.ErrorCode ?? invoice?.LastErrorCode;
            var retrying = failure.Kind == JobCompletionKind.Retry;
            var message = failure.ErrorSummary ?? code ?? "İşlem tamamlanamadı.";
            return new(retrying ? "IN_PROGRESS" : "FAILED", retrying ? $"{message} Otomatik olarak yeniden denenecek." : message, code);
        }
        if (invoice is null) return new("IN_PROGRESS", "Fatura işlemi sürüyor.");
        return Describe(invoice.Status, invoice.LastErrorCode, invoice.InvoiceNumber);
    }

    // Scan 25 Nov 2026 C-6: a lost provider answer (UnknownResult) is not a failure, the invoice may exist at the provider,
    // so it reads SONUÇ BELİRSİZ and is followed like one still going; a Submitted invoice was sent, not yet accepted.
    internal const string UnknownResultStatus = "UNKNOWN_RESULT";

    internal static InlineInvoiceOutcome Describe(InvoiceStatus status, string? lastErrorCode, string? invoiceNumber)
    {
        var number = string.IsNullOrWhiteSpace(invoiceNumber) ? "" : $" ({invoiceNumber})";
        return status switch
        {
            InvoiceStatus.Completed => new("COMPLETED", $"Fatura kesildi ve pazaryerine iletildi{number}."),
            InvoiceStatus.Accepted => new("COMPLETED", $"Fatura E-Faturam tarafından kabul edildi{number}; pazaryeri iletimi sürüyor."),
            InvoiceStatus.Submitted => new("IN_PROGRESS", $"Fatura E-Faturam'a gönderildi{number}; sonucu bekleniyor."),
            InvoiceStatus.MarketplacePending => new("COMPLETED", $"Fatura kesildi{number}; pazaryerine iletim sürüyor."),
            InvoiceStatus.UnknownResult => new(UnknownResultStatus, "Sonuç belirsiz: E-Faturam'ın cevabı alınamadı; fatura sağlayıcıda oluşmuş olabilir, kontrol edilmeden yeniden kesilmez.", lastErrorCode),
            InvoiceStatus.Rejected or InvoiceStatus.ValidationFailed or InvoiceStatus.MarketplaceFailed or InvoiceStatus.ManualReview =>
                new("FAILED", lastErrorCode is { Length: > 0 } code ? code : "Fatura işlemi tamamlanamadı; ayrıntı için fatura kaydına bakın.", lastErrorCode),
            _ => new("IN_PROGRESS", "Fatura işlemi sürüyor; sonuç birazdan güncellenecek.")
        };
    }
}
