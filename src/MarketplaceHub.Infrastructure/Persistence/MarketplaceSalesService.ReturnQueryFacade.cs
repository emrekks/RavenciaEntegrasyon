using MarketplaceHub.Application;

namespace MarketplaceHub.Infrastructure.Persistence;

// SIPARIS-STOK-10: the return query side lives in ReturnQueryService; the facade keeps the interface and forwards.
public sealed partial class MarketplaceSalesService
{
    private ReturnQueryService? returnQueries;
    internal ReturnQueryService ReturnQueries => returnQueries ??= new(Sales);

    public Task<PageResult<ReturnListView>> ReturnsAsync(Guid tenantId, int limit, string? after, ReturnListQuery options, bool latest, CancellationToken cancellationToken, int page = 1) =>
        ReturnQueries.ReturnsAsync(tenantId, limit, after, options, latest, cancellationToken, page);
    public Task<ReturnSummaryView> ReturnSummaryAsync(Guid tenantId, ReturnListQuery options, CancellationToken cancellationToken) =>
        ReturnQueries.ReturnSummaryAsync(tenantId, options, cancellationToken);
    public Task<ServiceResult<ReturnDetailView>> ReturnAsync(Guid tenantId, Guid id, CancellationToken cancellationToken) =>
        ReturnQueries.ReturnAsync(tenantId, id, cancellationToken);
    public Task<ServiceResult<IReadOnlyList<ReturnShipmentCompany>>> ReturnShipmentCompaniesAsync(Guid tenantId, Guid id, string correlationId, CancellationToken cancellationToken) =>
        ReturnQueries.ReturnShipmentCompaniesAsync(tenantId, id, correlationId, cancellationToken);
    public Task<ServiceResult<IReadOnlyList<ReturnIssueReason>>> ReturnIssueReasonsAsync(Guid tenantId, Guid id, string correlationId, CancellationToken cancellationToken) =>
        ReturnQueries.ReturnIssueReasonsAsync(tenantId, id, correlationId, cancellationToken);
}
