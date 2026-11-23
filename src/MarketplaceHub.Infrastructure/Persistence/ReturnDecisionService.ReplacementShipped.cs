using System.Linq.Expressions;
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
        var platform = ExchangeResendRules.PlatformName(await db.PlatformConnections.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.Id == claim.ConnectionId).Select(x => x.PlatformCode).SingleOrDefaultAsync(cancellationToken));
        if (marked)
        {
            if (claim.ReplacementCargoTrackingLink is not null || claim.ReplacementShippedAt is not null
                || await db.OrderLines.AsNoTracking().AnyAsync(ExchangeResendRules.ShippedExchangeLine(tenantId, claim.OrderId), cancellationToken))
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
                ? $"Değişim paketi Kargoya Verildi olarak elle işaretlendi; {platform.To} bir şey gönderilmedi."
                : $"Değişim paketine elle konan Kargoya Verildi işareti kaldırıldı; {platform.To} bir şey gönderilmedi.",
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
// Hepsiburada (scan O3, 23 Nov 2026): its claim names no exchange shipment, but an exchange it accepts (finalizedWith
// Change) re-creates the new product as an order line whose creationReason is ClaimChangeAccepted
// (hepsiburada-api-son.md:9097, "Değişim için : ClaimChangeAccepted"; the line's raw item is kept in SourceSnapshotJson).
// Once such a line of the claim's order is shipped, Hepsiburada has reported the exchange handed to the carrier
// (source MARKETPLACE). Only that positive fact closes it; without it the owner's mark is the way, as on Trendyol.
internal static class ExchangeResendRules
{
    internal const string MarketplaceSource = "MARKETPLACE";
    internal const string HepsiburadaExchangeLineReason = "ClaimChangeAccepted";

    internal static bool IsExchangeResend(ReturnClaim claim) =>
        claim.Status != ReturnClaimStatus.Cancelled
        && claim.CustomerRequest == ReturnCustomerRequestPolicy.Change
        && (claim.ReplacementPackageId != null || claim.ResolvedWith == ReturnCustomerRequestPolicy.Change);

    // An exchange line of the order (Hepsiburada's ClaimChangeAccepted item) that the carrier has taken.
    internal static Expression<Func<OrderLine, bool>> ShippedExchangeLine(Guid tenantId, Guid orderId) => line =>
        line.TenantId == tenantId && line.OrderId == orderId && line.ShippedQuantity > 0
        && line.SourceSnapshotJson != null && line.SourceSnapshotJson.Contains(HepsiburadaExchangeLineReason);

    // The exchange's package is not known to be handed to the carrier: no tracking link, no owner mark and no shipped
    // Hepsiburada exchange line. One rule for the tab, its count, the card's stores and the mark.
    internal static Expression<Func<ReturnClaim, bool>> NotShipped(AppDbContext db) => claim =>
        claim.ReplacementCargoTrackingLink == null && claim.ReplacementShippedAt == null
        && !db.OrderLines.Any(line => line.TenantId == claim.TenantId && line.OrderId == claim.OrderId && line.ShippedQuantity > 0
            && line.SourceSnapshotJson != null && line.SourceSnapshotJson.Contains(HepsiburadaExchangeLineReason));

    // The marketplace's name as the texts use it: "Trendyol'a", "Hepsiburada'ya" (only these two have exchanges).
    internal static (string Name, string To) PlatformName(string? platformCode) => platformCode?.Trim().ToUpperInvariant() switch
    {
        "TRENDYOL" => ("Trendyol", "Trendyol'a"),
        "HEPSIBURADA" => ("Hepsiburada", "Hepsiburada'ya"),
        _ => ("Pazaryeri", "Pazaryerine")
    };

    internal static string? ShippedSource(string? trackingLink, DateTimeOffset? shippedAt, bool exchangeLineShipped = false) =>
        trackingLink is not null || exchangeLineShipped ? MarketplaceSource : shippedAt is not null ? ReturnCustomerRequestPolicy.ManualSource : null;
}
