using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.Extensions.Configuration;

namespace MarketplaceHub.Infrastructure.Persistence;

// SIPARIS-STOK-10: the return query side of the sales service: the Returns list and its cards, search, filters, labels and
// summary, the return detail and its marketplace lists (shipment companies, issue reasons). Moved out of
// MarketplaceSalesService unchanged; the facade (IMarketplaceSalesService) forwards to it, and ReturnDecisionService reads
// the return view from it. It shares the database, ports and helpers through SalesContext.
internal sealed partial class ReturnQueryService(SalesContext sales)
{
    private readonly AppDbContext db = sales.Db;
    private readonly CursorCodec cursors = sales.Cursors;
    private readonly IConfiguration configuration = sales.Configuration;
    private readonly IReturnPort returns = sales.Returns;
    private readonly TimeProvider timeProvider = sales.TimeProvider;

    private IQueryable<ShipmentPackage> CurrentShipmentPackages() => sales.CurrentShipmentPackages();
    private Task<Dictionary<Guid, string>> MediaUrls(Guid tenantId, IEnumerable<Guid?> variantIds, CancellationToken cancellationToken) => sales.MediaUrls(tenantId, variantIds, cancellationToken);
    private Task<bool> IsStageConnection(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) => sales.IsStageConnection(tenantId, connectionId, cancellationToken);
    private Task<bool> IsProductionConnection(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) => sales.IsProductionConnection(tenantId, connectionId, cancellationToken);
    private Task<bool> WritesEnabled(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) => sales.WritesEnabled(tenantId, connectionId, cancellationToken);
    private Task<IReadOnlyList<string>> CapabilityValues(Guid tenantId, Guid connectionId, string code, string property, CancellationToken cancellationToken) => sales.CapabilityValues(tenantId, connectionId, code, property, cancellationToken);
    private IQueryable<ReturnDecision> OpenReturnDecisions(Guid tenantId, Guid claimId, bool sentDecisionHoldsClaim) => ReturnDecisionService.OpenReturnDecisions(db, tenantId, claimId, sentDecisionHoldsClaim);
    private static readonly IReadOnlyList<string> ReturnActions = ReturnDecisionService.ReturnActions;
    private Task<bool> ExternalWritePolicyEnabledAsync(Guid tenantId, Guid connectionId, string resourceType, CancellationToken cancellationToken) => sales.ExternalWritePolicyEnabledAsync(tenantId, connectionId, resourceType, cancellationToken);
}
