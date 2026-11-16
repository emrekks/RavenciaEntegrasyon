using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Persistence;

// SIPARIS-STOK-10: the sync job enqueues of the sales service: the order, product, catalog structure, reference data,
// stage test order, return and question reads, with their open-job and execution-group checks. Moved out of
// MarketplaceSalesService unchanged; the facade (IMarketplaceSalesService) forwards to it, and ShipmentService reaches
// NewJob and the order read through the facade.
internal sealed partial class SalesJobService(SalesContext sales)
{
    private readonly AppDbContext db = sales.Db;
    private readonly TimeProvider timeProvider = sales.TimeProvider;

    private static ServiceResult<T> NotFound<T>() => SalesResults.NotFound<T>();
}
