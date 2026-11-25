using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Xunit;

namespace MarketplaceHub.Application.Tests;

// Scan 25 Nov 2026 C-6: the "Fatura Kes" answer told a lost provider answer as BAŞARISIZ and a Submitted invoice as
// accepted, and the provider status poll ran after the job was handed back to the queue.
[Collection("postgres-integration")]
public sealed class InvoiceInlineRunnerTests(PostgreSqlTenantIsolationFixture fixture) : IClassFixture<PostgreSqlTenantIsolationFixture>
{
    [Fact]
    public void A_lost_provider_answer_is_unknown_not_failed_and_a_submitted_invoice_is_not_accepted_yet()
    {
        var unknown = InvoiceInlineRunner.Describe(InvoiceStatus.UnknownResult, "EFATURAM_TIMEOUT", null);
        Assert.Equal(InvoiceInlineRunner.UnknownResultStatus, unknown.Status);
        Assert.StartsWith("Sonuç belirsiz", unknown.Message);
        Assert.Equal("EFATURAM_TIMEOUT", unknown.ErrorCode);

        var submitted = InvoiceInlineRunner.Describe(InvoiceStatus.Submitted, null, "GIB2026000000001");
        Assert.Equal("IN_PROGRESS", submitted.Status);
        Assert.DoesNotContain("kabul", submitted.Message);

        Assert.Equal("COMPLETED", InvoiceInlineRunner.Describe(InvoiceStatus.Accepted, null, null).Status);
        Assert.Equal("FAILED", InvoiceInlineRunner.Describe(InvoiceStatus.Rejected, "X", null).Status);
    }

    [PostgreSqlFact]
    public async Task The_provider_status_poll_runs_while_the_job_is_still_leased()
    {
        var events = new List<string>();
        var job = new LeasedJob(Guid.CreateVersion7(), Guid.CreateVersion7(), null, InvoicingJobTypes.InvoiceReconcile, "{}", "corr", "token");
        var leases = new RecordingLeases(job, events);
        var processor = new PendingThenDone(events);
        await using var db = fixture.CreateContext();
        var runner = new InvoiceInlineRunner(db, leases, processor, TimeProvider.System);

        await runner.RunAsync(job.TenantId, Guid.CreateVersion7(), job.Id, DateTimeOffset.UtcNow.AddSeconds(30), CancellationToken.None);

        // Both status reads ran before the job was completed once, with the final answer.
        Assert.Equal(["lease", "process", "process", "complete:Succeeded"], events);
    }

    private sealed class RecordingLeases(LeasedJob job, List<string> events) : IJobLeaseService
    {
        private bool leased;

        public Task<LeasedJob?> TryLeaseAsync(TimeSpan leaseDuration, int? maximumPriority, int? minimumPriority, CancellationToken cancellationToken, string? onlyJobType = null, Guid? onlyJobId = null, string[]? includedJobTypes = null, string[]? excludedJobTypes = null, JobQuotaHold? quotaHold = null, JobFairness? fairness = null, JobBulkhead? bulkhead = null)
        {
            if (leased || onlyJobId != job.Id) return Task.FromResult<LeasedJob?>(null);
            leased = true;
            events.Add("lease");
            return Task.FromResult<LeasedJob?>(job);
        }

        public Task<bool> CompleteAsync(Guid jobId, string leaseToken, JobExecutionResult result, CancellationToken cancellationToken)
        {
            events.Add($"complete:{result.Kind}");
            return Task.FromResult(true);
        }

        public Task<bool> HeartbeatAsync(Guid jobId, string leaseToken, TimeSpan extension, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<int> ReapExpiredAsync(CancellationToken cancellationToken) => Task.FromResult(0);
        public Task<bool> ReleaseAsync(Guid jobId, string leaseToken, CancellationToken cancellationToken) => Task.FromResult(true);
    }

    private sealed class PendingThenDone(List<string> events) : IInvoicingJobProcessor
    {
        private int calls;

        public Task<JobExecutionResult> ProcessAsync(Guid tenantId, Guid? connectionId, string jobType, string payloadJson, string correlationId, CancellationToken cancellationToken)
        {
            events.Add("process");
            return Task.FromResult(++calls == 1 ? JobExecutionResult.Retry("INVOICE_REMOTE_PENDING", "Bekleniyor") : JobExecutionResult.Success());
        }
    }
}
