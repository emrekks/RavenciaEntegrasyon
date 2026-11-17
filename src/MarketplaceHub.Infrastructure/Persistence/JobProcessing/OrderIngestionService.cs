using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Persistence;

// ISLEYICI-9 stage 3b (16 Nov 2026): the order ingestion core, moved out of the job processor as it was: storing order
// pages (UpsertOrders/UpsertOrderCore), the order line allocation and stock helpers, the refused-order bookkeeping, the
// invoice state of orders and packages, and the re-matching of unmatched order lines. It works for one run of one job
// (run: that run's counters, re-read list and read records) on the job's AppDbContext, with the same cursor store and
// issue recorder as the processor; the processor's methods of the same names call into it. The moved code reads no
// marketplace, writes none and reads no setting, so it takes neither a port, the effect guard nor the configuration.
internal sealed partial class OrderIngestionService(
    AppDbContext db,
    IssueRecorder issues,
    SyncCursorStore cursors,
    TimeProvider timeProvider,
    IInvoicingBillingService? invoiceBilling,
    JobRunContext run)
{
    public JobRunContext Run => run;
}
