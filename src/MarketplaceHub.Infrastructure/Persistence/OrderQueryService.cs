using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.Extensions.Configuration;

namespace MarketplaceHub.Infrastructure.Persistence;

// SIPARIS-STOK-10: the order query side of the sales service: the order list, its filters and counters, the facets, the
// order detail and the product picture lookup. Moved out of MarketplaceSalesService unchanged; the facade
// (IMarketplaceSalesService) forwards to it. It shares the database, ports and helpers through SalesContext.
internal sealed partial class OrderQueryService(SalesContext sales)
{
    private readonly AppDbContext db = sales.Db;
    private readonly CursorCodec cursors = sales.Cursors;
    private readonly IConfiguration configuration = sales.Configuration;
    private readonly IProductVisualLookupPort productVisuals = sales.ProductVisuals;
    private readonly IOrderPort orders = sales.Orders;
    private readonly TimeProvider timeProvider = sales.TimeProvider;

    private IQueryable<ShipmentPackage> CurrentShipmentPackages() => sales.CurrentShipmentPackages();
    private Task<Dictionary<Guid, decimal>> ActiveAllocatedQuantitiesAsync(Guid tenantId, Guid[] orderLineIds, CancellationToken cancellationToken) =>
        ShipmentService.ActiveAllocatedQuantitiesAsync(db, tenantId, orderLineIds, cancellationToken);
    private static readonly System.Globalization.CultureInfo TurkishCulture = System.Globalization.CultureInfo.GetCultureInfo("tr-TR");
    private Task<Dictionary<Guid, string>> MediaUrls(Guid tenantId, IEnumerable<Guid?> variantIds, CancellationToken cancellationToken) => sales.MediaUrls(tenantId, variantIds, cancellationToken);
}
