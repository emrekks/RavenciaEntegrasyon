using MarketplaceHub.Application;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MarketplaceHub.Infrastructure.Persistence;

// ISLEYICI-9 stage 4 (16 Nov 2026): the services of one run of one job, made once each when first needed, on the job's
// AppDbContext and the processor's ports, cursor store and issue recorder. The processor and the run job handlers
// (IRunJobHandler) take them from the same instance, so a run has one order ingestion, one catalog import and so on,
// whichever of them calls first (they used to be the processor's own per-run properties, built the same way).
internal sealed class JobRunServices(
    AppDbContext db,
    IssueRecorder issues,
    SyncCursorStore cursors,
    IReferenceDataPort references,
    IProductPort products,
    IOrderPort orders,
    IOrderPackageReadPort orderPackages,
    IHepsiburadaInvoiceStatusPort hepsiburadaInvoiceStatus,
    IReturnPort returns,
    IConfiguration configuration,
    TimeProvider timeProvider,
    IInvoicingBillingService? invoiceBilling,
    ILogger? logger,
    VariantLinkBookkeeping variantLinks,
    JobRunContext run)
{
    private OrderIngestionService? orderIngestion;
    private ReturnIngestionService? returnIngestion;
    private OrderReadService? orderRead;
    private ReturnReadService? returnRead;
    private ReferenceSyncService? referenceSync;
    private CatalogAttributeService? catalogAttributes;
    private CatalogImportService? catalogImport;

    public JobRunContext Run => run;

    public OrderIngestionService OrderIngestion => orderIngestion ??= new(db, issues, cursors, timeProvider, invoiceBilling, run);

    public ReturnIngestionService ReturnIngestion => returnIngestion ??= new(db, issues, timeProvider, configuration, OrderIngestion, run);

    public OrderReadService OrderRead => orderRead ??= new(db, issues, cursors, orders, orderPackages, hepsiburadaInvoiceStatus, configuration, timeProvider, OrderIngestion, run);

    public ReturnReadService ReturnRead => returnRead ??= new(db, issues, cursors, returns, orders, configuration, timeProvider, OrderIngestion, ReturnIngestion, run);

    public ReferenceSyncService ReferenceSync => referenceSync ??= new(db, references, timeProvider, OrderIngestion, run);

    public CatalogAttributeService CatalogAttributes => catalogAttributes ??= new(db, timeProvider, ReferenceSync, run);

    public CatalogImportService CatalogImport => catalogImport ??= new(db, issues, cursors, products, timeProvider, logger, CatalogAttributes, variantLinks, OrderIngestion, run);
}
