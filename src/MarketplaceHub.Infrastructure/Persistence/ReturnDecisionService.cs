using MarketplaceHub.Application;
using MarketplaceHub.Domain;

namespace MarketplaceHub.Infrastructure.Persistence;

// SIPARIS-STOK-10: the return decisions of the sales service: the instant approve / reject, the received mark and the
// stock disposition. Moved out of MarketplaceSalesService unchanged; the facade (IMarketplaceSalesService) forwards to it.
// It reads the return views from ReturnQueryService.
internal sealed partial class ReturnDecisionService(SalesContext sales, ReturnQueryService returnsView)
{
    private readonly AppDbContext db = sales.Db;
    private readonly IReturnPort returns = sales.Returns;
    private readonly IPrivateFileStorage files = sales.Files;
    private readonly TimeProvider timeProvider = sales.TimeProvider;

    private Task<ServiceResult<T>> VerifyLaterAsync<T>(ExternalEffectRecord effect, CancellationToken cancellationToken) => sales.VerifyLaterAsync<T>(effect, cancellationToken);
    private Task<bool> IsStageConnection(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) => sales.IsStageConnection(tenantId, connectionId, cancellationToken);
    private Task<bool> IsProductionConnection(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) => sales.IsProductionConnection(tenantId, connectionId, cancellationToken);
    private Task<bool> WritesEnabled(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) => sales.WritesEnabled(tenantId, connectionId, cancellationToken);
    private Task<bool> ExternalWritePolicyEnabledAsync(Guid tenantId, Guid connectionId, string resourceType, CancellationToken cancellationToken) => sales.ExternalWritePolicyEnabledAsync(tenantId, connectionId, resourceType, cancellationToken);

    // The return view and the facts the decision checks, read from ReturnQueryService.
    private Task<ServiceResult<ReturnDetailView>> ReturnAsync(Guid tenantId, Guid id, CancellationToken cancellationToken) => returnsView.ReturnAsync(tenantId, id, cancellationToken);
    private Task<IReadOnlyList<ReturnIssueReason>?> CachedIssueReasonsAsync(Guid tenantId, Guid connectionId, string correlationId, CancellationToken cancellationToken) => returnsView.CachedIssueReasonsAsync(tenantId, connectionId, correlationId, cancellationToken);
    private Task<bool> Supported(Guid tenantId, Guid connectionId, string code, CancellationToken cancellationToken) => sales.Supported(tenantId, connectionId, code, cancellationToken);
    private static bool RejectEvidenceRequired(string? reasonCode, IReadOnlyList<ReturnIssueReason>? reasons, string? platformCode) => ReturnQueryService.RejectEvidenceRequired(reasonCode, reasons, platformCode);
    private static bool IsN11(string? platformCode) => ReturnQueryService.IsN11(platformCode);
    private static bool ReturnReceiptAllowed(string? platformCode) => ReturnQueryService.ReturnReceiptAllowed(platformCode);
}
