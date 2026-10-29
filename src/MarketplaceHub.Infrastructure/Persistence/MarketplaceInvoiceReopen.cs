using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

/// <summary>
/// The seller sets an n11, Pazarama or PTT AVM package back to "Fatura Bekliyor" after its invoice was cancelled
/// (MarketplaceInvoiceReopenPolicy). These marketplaces neither read back which invoice a package holds nor remove a
/// link, so the cancelled invoice's link would otherwise keep the package invoiced and refuse every new invoice.
/// </summary>
public static class MarketplaceInvoiceReopen
{
    /// <summary>
    /// Sets aside what a cancelled invoice of this panel left on each package: its open link removal ends as
    /// LINK_REMOVED (the cancelled invoice's public link stops answering), the cancelled sale invoice leaves the order's
    /// fiscal slot, and the package no longer shows an invoice. A package with no such evidence is left as it is. The
    /// caller saves. Returns the packages reopened.
    /// </summary>
    public static async Task<int> ReopenAsync(AppDbContext db, Guid tenantId, IReadOnlyCollection<(ShipmentPackage Package, string PlatformCode)> packages,
        Guid? actorUserId, string correlationId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var reopened = 0;
        foreach (var (package, platformCode) in packages)
        {
            if (!MarketplaceInvoiceReopenPolicy.AppliesTo(platformCode)) continue;
            var states = await (from state in db.MarketplaceDeliveryStates
                                join invoice in db.Invoices on new { state.TenantId, Id = state.InvoiceId } equals new { invoice.TenantId, invoice.Id }
                                where state.TenantId == tenantId && state.PackageId == package.Id && invoice.Status == InvoiceStatus.Cancelled
                                    && (state.Status == MarketplaceInvoiceLinkRevocationPolicy.Required || state.Status == MarketplaceInvoiceLinkRevocationPolicy.Started
                                        || state.Status == MarketplaceInvoiceLinkRevocationPolicy.Unknown)
                                select state).ToListAsync(cancellationToken);
            if (states.Count == 0) continue;

            foreach (var state in states)
            {
                InvoiceAdjustmentTracking.AppendDeliveryHistory(db, state, MarketplaceInvoiceLinkRevocationPolicy.Removed, state.ExternalReference, MarketplaceInvoiceReopenPolicy.ReopenedCode, now);
                state.CompletedAt = now;
            }
            foreach (var invoiceId in states.Select(state => state.InvoiceId).Distinct())
            {
                var invoice = await db.Invoices.SingleAsync(item => item.TenantId == tenantId && item.Id == invoiceId, cancellationToken);
                // Cancelled at the provider already (a cancellation job, or the operator's replacement): it leaves the
                // order's one sale slot, as "Portalda İptal Ettim, Yeni Fatura Kes" does, so a new invoice can be drafted.
                if (invoice.SequencePurpose == "SALE" && invoice.OriginalInvoiceId == null)
                {
                    invoice.SequencePurpose = "REPLACED";
                    invoice.IdempotencyKey = $"replaced:{invoice.Id:N}";
                    invoice.OrderFiscalGuarded = false;
                    invoice.UpdatedAt = now;
                    invoice.Version++;
                }
                await InvoiceAdjustmentTracking.ResolveLinkRevocationIfDoneAsync(db, tenantId, invoiceId, now, cancellationToken);
            }

            MarketplaceInvoiceReferenceHistoryRecorder.RecordPreviousReferences(db, package, null, null, now, MarketplaceInvoiceReopenPolicy.ReopenedCode);
            package.MarketplaceInvoiceStatus = MarketplaceInvoiceStatus.NotInvoiced;
            package.MarketplaceInvoiceRawStatus = MarketplaceInvoiceReopenPolicy.ReopenedCode;
            package.MarketplaceInvoiceNumber = null;
            package.MarketplaceInvoiceUrl = null;
            package.MarketplaceInvoiceObservedAt = now;
            package.UpdatedAt = now;
            package.Version++;
            db.AuditLogs.Add(new AuditLog
            {
                TenantId = tenantId,
                ActorUserId = actorUserId,
                Action = "INVOICE_MARKETPLACE_PACKAGE_REOPENED",
                TargetType = "ShipmentPackage",
                TargetId = package.Id.ToString("D"),
                Reason = $"{platformCode}:cancelled-invoice:{string.Join(',', states.Select(state => state.InvoiceId.ToString("N")).Distinct())}",
                CorrelationId = correlationId,
                CreatedAt = now
            });
            reopened++;
        }
        return reopened;
    }

    /// <summary>The packages among the given ones the seller reopened after a cancelled invoice.</summary>
    public static async Task<IReadOnlySet<Guid>> ReopenedPackageIdsAsync(AppDbContext db, Guid tenantId, IReadOnlyCollection<Guid> packageIds, CancellationToken cancellationToken) =>
        packageIds.Count == 0
            ? new HashSet<Guid>()
            : (await db.MarketplaceDeliveryStates.AsNoTracking()
                .Where(state => state.TenantId == tenantId && state.PackageId != null && packageIds.Contains(state.PackageId.Value)
                    && state.Status == MarketplaceInvoiceLinkRevocationPolicy.Removed && state.ErrorCode == MarketplaceInvoiceReopenPolicy.ReopenedCode)
                .Select(state => state.PackageId!.Value).Distinct().ToListAsync(cancellationToken)).ToHashSet();

    /// <summary>Whether a package's invoice evidence is set aside: the seller reopened it and still says "Fatura Bekliyor".</summary>
    public static bool Overrides(ShipmentPackage package, IReadOnlySet<Guid> reopenedPackageIds) =>
        MarketplaceInvoiceReopenPolicy.OverridesInvoiceEvidence(package.ManualInvoiceStatus, reopenedPackageIds.Contains(package.Id));
}
