using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;
using static MarketplaceHub.Infrastructure.Persistence.SalesResults;

namespace MarketplaceHub.Infrastructure.Persistence;

internal sealed partial class ReturnDecisionService
{
    // Owner, 18 Nov 2026 ("Değişim Onaylandı Olarak İşaretle"): a Trendyol exchange request that Trendyol answered Rejected
    // after the owner made the exchange in its seller panel, where its claim does not say so. Panel only: nothing is sent
    // to Trendyol. ResolvedWith CHANGE with its source MANUAL moves it to the approved tab as "Değişim Onaylandı" and out
    // of the invoice's returns; removing the mark puts back what was there. The caller checks the role (owner or admin).
    public async Task<ServiceResult<ReturnDetailView>> MarkExchangeApprovedAsync(Guid tenantId, Guid userId, Guid claimId, long expectedVersion, bool marked, string correlationId, CancellationToken cancellationToken)
    {
        var claim = await db.ReturnClaims.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == claimId, cancellationToken);
        if (claim is null) return NotFound<ReturnDetailView>();
        if (claim.Version != expectedVersion) return Precondition<ReturnDetailView>(claim.Version);
        var platformCode = await db.PlatformConnections.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == claim.ConnectionId).Select(x => x.PlatformCode).SingleOrDefaultAsync(cancellationToken);
        if (marked)
        {
            if (!ReturnCustomerRequestPolicy.ManualExchangeMarkAllowed(platformCode, claim.CustomerRequest, claim.Status, claim.ResolvedWith))
                return ServiceResult<ReturnDetailView>.Fail(ReturnCustomerRequestPolicy.ManualExchangeMarkNotAllowedCode,
                    "Yalnız Trendyol'un reddettiği, sonucu henüz belli olmayan bir değişim talebi Değişim Onaylandı olarak işaretlenebilir.", 409);
            claim.ResolvedWith = ReturnCustomerRequestPolicy.Change;
            claim.ResolvedWithSource = ReturnCustomerRequestPolicy.ManualSource;
        }
        else
        {
            if (claim.ResolvedWithSource != ReturnCustomerRequestPolicy.ManualSource)
                return ServiceResult<ReturnDetailView>.Fail(ReturnCustomerRequestPolicy.ManualExchangeMarkNotAllowedCode, "Bu iadede elle konmuş bir Değişim Onaylandı işareti yok.", 409);
            claim.ResolvedWith = null;
            claim.ResolvedWithSource = null;
        }
        var now = timeProvider.GetUtcNow();
        claim.UpdatedAt = now;
        claim.Version++;
        db.AuditLogs.Add(new AuditLog
        {
            TenantId = tenantId,
            ActorUserId = userId,
            Action = marked ? "RETURN_EXCHANGE_MARKED_APPROVED" : "RETURN_EXCHANGE_MARK_REMOVED",
            TargetType = "ReturnClaim",
            TargetId = claimId.ToString("D"),
            Reason = marked
                ? "Değişim Onaylandı olarak elle işaretlendi; Trendyol'a bir şey gönderilmedi, iade fatura işlemlerinden çıkarıldı."
                : "Elle konan Değişim Onaylandı işareti kaldırıldı; Trendyol'a bir şey gönderilmedi.",
            CorrelationId = correlationId,
            CreatedAt = now
        });
        await db.SaveChangesAsync(cancellationToken);
        return await ReturnAsync(tenantId, claimId, cancellationToken);
    }
}
