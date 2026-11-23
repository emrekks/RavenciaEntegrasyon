using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;
using static MarketplaceHub.Infrastructure.Persistence.SalesResults;

namespace MarketplaceHub.Infrastructure.Persistence;

internal sealed partial class ReturnDecisionService
{
    internal const string ReplacementShipMarkNotAllowedCode = "EXCHANGE_RESEND_SHIP_MARK_NOT_ALLOWED";

    // Owner, 23 Nov 2026 ("yeniden gönderimi kargoya verdim ve kapanması lazım ama kapatamadım"): Trendyol's claim names
    // the exchange package and its tracking number but no tracking link or status, so the Yeniden Gönderimler row waited
    // for the carrier for ever. "Kargoya Verildi Olarak İşaretle" records the owner's word (ReplacementShippedAt, source
    // MANUAL): the row leaves the not-shipped count and Gönderilecekler and stays under Yeniden Gönderimler as
    // "Kargoya Verildi (Elle)". Panel only, nothing is sent to Trendyol; owners and admins only. "İşareti Kaldır" undoes it.
    public async Task<ServiceResult<ReturnDetailView>> MarkReplacementShippedAsync(Guid tenantId, Guid userId, Guid claimId, long expectedVersion, bool marked, string correlationId, CancellationToken cancellationToken)
    {
        var claim = await db.ReturnClaims.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == claimId, cancellationToken);
        if (claim is null) return NotFound<ReturnDetailView>();
        if (claim.Version != expectedVersion) return Precondition<ReturnDetailView>(claim.Version);
        if (!ExchangeResendRules.IsExchangeResend(claim))
            return ServiceResult<ReturnDetailView>.Fail(ReplacementShipMarkNotAllowedCode, "Bu iade bir değişim yeniden gönderimi değil; kargoya verildi işareti konamaz.", 409);
        var now = timeProvider.GetUtcNow();
        if (marked)
        {
            if (claim.ReplacementCargoTrackingLink is not null || claim.ReplacementShippedAt is not null)
                return ServiceResult<ReturnDetailView>.Fail(ReplacementShipMarkNotAllowedCode, "Bu değişim paketi zaten kargoya verildi olarak görünüyor.", 409);
            claim.ReplacementShippedAt = now;
            claim.ReplacementShippedSource = ReturnCustomerRequestPolicy.ManualSource;
        }
        else
        {
            if (claim.ReplacementShippedSource != ReturnCustomerRequestPolicy.ManualSource)
                return ServiceResult<ReturnDetailView>.Fail(ReplacementShipMarkNotAllowedCode, "Bu değişim paketinde elle konmuş bir Kargoya Verildi işareti yok.", 409);
            claim.ReplacementShippedAt = null;
            claim.ReplacementShippedSource = null;
        }
        claim.UpdatedAt = now;
        claim.Version++;
        db.AuditLogs.Add(new AuditLog
        {
            TenantId = tenantId,
            ActorUserId = userId,
            Action = marked ? "RETURN_REPLACEMENT_MARKED_SHIPPED" : "RETURN_REPLACEMENT_SHIP_MARK_REMOVED",
            TargetType = "ReturnClaim",
            TargetId = claimId.ToString("D"),
            Reason = marked
                ? "Değişim paketi Kargoya Verildi olarak elle işaretlendi; Trendyol'a bir şey gönderilmedi."
                : "Değişim paketine elle konan Kargoya Verildi işareti kaldırıldı; Trendyol'a bir şey gönderilmedi.",
            CorrelationId = correlationId,
            CreatedAt = now
        });
        await db.SaveChangesAsync(cancellationToken);
        return await ReturnAsync(tenantId, claimId, cancellationToken);
    }
}

// The exchange resend rule (owner, 18 and 23 Nov 2026), one place for the Yeniden Gönderimler tab, its rows, the mark and
// the re-read: a Trendyol exchange request (CustomerRequest CHANGE, not cancelled) whose claim names the exchange package
// or that the owner marked made. Its package is handed to the carrier once its tracking link is known or the owner marked
// it so; a tracking number alone says nothing about it (Trendyol names it before the parcel leaves).
internal static class ExchangeResendRules
{
    internal const string MarketplaceSource = "MARKETPLACE";

    internal static bool IsExchangeResend(ReturnClaim claim) =>
        claim.Status != ReturnClaimStatus.Cancelled
        && claim.CustomerRequest == ReturnCustomerRequestPolicy.Change
        && (claim.ReplacementPackageId != null || claim.ResolvedWith == ReturnCustomerRequestPolicy.Change);

    internal static string? ShippedSource(string? trackingLink, DateTimeOffset? shippedAt) =>
        trackingLink is not null ? MarketplaceSource : shippedAt is not null ? ReturnCustomerRequestPolicy.ManualSource : null;
}
