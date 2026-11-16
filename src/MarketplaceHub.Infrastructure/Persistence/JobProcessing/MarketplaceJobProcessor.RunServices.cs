namespace MarketplaceHub.Infrastructure.Persistence;

// ISLEYICI-9 stage 4 (16 Nov 2026): the services of the current run (JobRunServices), made on first use and replaced when
// a new run starts; the processor's code and its run job handlers take them from here.
public sealed partial class MarketplaceJobProcessor
{
    private JobRunServices? runServices;

    private JobRunServices Services => ServicesFor(run);

    // The services of the given run: the processor's own when it is the current run (a handler is always called with it).
    private JobRunServices ServicesFor(JobRunContext forRun) =>
        runServices is { } current && ReferenceEquals(current.Run, forRun)
            ? current
            : runServices = new JobRunServices(db, issues, cursors, connections, references, products, orders, orderPackages, hepsiburadaInvoiceStatus, returns, configuration, timeProvider, invoiceBilling, logger, VariantLinks, files, effectGuard, WriteSwitches, forRun);
}
