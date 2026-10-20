using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

// Links a cancelled invoice left on marketplace packages (delivery states in REVOCATION_REQUIRED). Nothing here
// runs on its own: the operator asks for each package from the invoice detail. Trendyol links are removed through
// the documented delete endpoint; for Hepsiburada the operator removes the link in the seller panel and the panel
// reads the package to confirm it; other marketplaces only get instructions (MarketplaceInvoiceLinkRevocationPolicy).
public sealed partial class InvoicingBillingService
{
    public async Task<ServiceResult<MarketplaceLinkRevocationView>> RevokeMarketplaceLinkAsync(Guid tenantId, Guid invoiceId, Guid deliveryStateId, string idempotencyKey, string correlationId, CancellationToken cancellationToken)
    {
        var invoice = await db.Invoices.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == invoiceId, cancellationToken);
        if (invoice is null) return NotFound<MarketplaceLinkRevocationView>();
        var state = await db.MarketplaceDeliveryStates.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.InvoiceId == invoiceId && x.Id == deliveryStateId, cancellationToken);
        if (state is null) return NotFound<MarketplaceLinkRevocationView>();
        var connection = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == state.ConnectionId, cancellationToken);
        if (connection is null) return NotFound<MarketplaceLinkRevocationView>();
        var package = state.PackageId is { } packageId
            ? await db.ShipmentPackages.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == packageId, cancellationToken)
            : null;

        // A replay after the link was removed answers with the finished state instead of writing again.
        if (state.Status == MarketplaceInvoiceLinkRevocationPolicy.Removed)
            return ServiceResult<MarketplaceLinkRevocationView>.Ok(await LinkRevocationViewAsync(state, connection, package?.ExternalPackageId, cancellationToken));
        var now = timeProvider.GetUtcNow();
        if (!MarketplaceInvoiceLinkRevocationPolicy.IsOpen(state.Status))
            return ServiceResult<MarketplaceLinkRevocationView>.Fail("INVOICE_LINK_REVOCATION_NOT_REQUIRED", "Bu pazaryeri teslimi için kaldırılacak fatura linki yok.", 409);
        if (!MarketplaceInvoiceLinkRevocationPolicy.CanStart(state.Status, state.UpdatedAt, now))
            return ServiceResult<MarketplaceLinkRevocationView>.Fail("INVOICE_LINK_REVOCATION_RUNNING", "Bu link için kaldırma isteği sürüyor. Birkaç dakika bekleyip sayfayı yenileyin.", 409);

        var mode = MarketplaceInvoiceLinkRevocationPolicy.ModeFor(connection.PlatformCode);
        var instructions = MarketplaceInvoiceLinkRevocationPolicy.Instructions(connection.PlatformCode, package?.ExternalPackageId ?? state.ExternalReference);
        if (mode == MarketplaceInvoiceLinkRevocationPolicy.Manual)
            return ServiceResult<MarketplaceLinkRevocationView>.Fail(MarketplaceInvoiceLinkRevocationPolicy.UnsupportedCode, instructions, 422);
        if (marketplace is null)
            return ServiceResult<MarketplaceLinkRevocationView>.Fail("INVOICE_LINK_REVOCATION_UNAVAILABLE", "Pazaryeri bağlantısı bu işlem için hazır değil.", 503);
        if (package is null)
            return ServiceResult<MarketplaceLinkRevocationView>.Fail("INVOICE_PACKAGE_NOT_FOUND", "Fatura linkinin bulunduğu paket panelde bulunamadı.", 409);
        if (mode == MarketplaceInvoiceLinkRevocationPolicy.Automatic && !await WriteGates(tenantId, connection.Id, InvoicingCapabilities.InvoiceDeliver, cancellationToken))
            return ServiceResult<MarketplaceLinkRevocationView>.Fail("INVOICE_LINK_REVOCATION_WRITE_CLOSED", $"{await DeliveryBlockedReasonAsync(tenantId, connection.Id, cancellationToken)} Fatura linki kaldırılmadı.", 409);
        if (!IntegrationRuntimePolicy.AllowsManualRead(connection))
            return ServiceResult<MarketplaceLinkRevocationView>.Fail("INVOICE_LINK_STATUS_UNAVAILABLE", $"{connection.DisplayName} bağlantısı etkin değil; paket okunamadı.", 409);

        var order = await db.Orders.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == package.OrderId)
            .Select(x => new { x.OrderNumber, x.OrderedAt, x.LastRemoteModifiedAt, x.StoreFrontCode }).SingleOrDefaultAsync(cancellationToken);
        var permanentLinks = await db.InvoiceDocuments.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.InvoiceId == invoiceId && x.PermanentUrl != null)
            .Select(x => x.PermanentUrl!).ToListAsync(cancellationToken);
        var expectedLinks = permanentLinks.Concat(permanentLinks.Select(InvoiceDocumentLink.PdfAlias)).Distinct(StringComparer.Ordinal).ToList();
        var reference = new ExternalInvoiceDeliveryReference(package.ExternalPackageId, order?.OrderNumber, null, invoice.InvoiceNumber, expectedLinks.FirstOrDefault(),
            order is null ? null : new PackageLookup(package.StatusOccurredAt, new OrderLookupHint(order.OrderedAt, order.LastRemoteModifiedAt, order.StoreFrontCode), InvoiceOnly: true));
        var context = new AdapterContext(tenantId, connection.Id, correlationId, $"invoice-link-revocation:{state.Id:N}", now.AddMinutes(2), IsExplicitManualInvoiceDelivery: true);

        if (mode == MarketplaceInvoiceLinkRevocationPolicy.VerifyOnly)
        {
            var observed = await marketplace.QueryDeliveryAsync(context, reference, cancellationToken);
            if (!observed.IsSuccess)
                return ServiceResult<MarketplaceLinkRevocationView>.Fail("INVOICE_LINK_STATUS_UNAVAILABLE", $"Pazaryeri paketi okunamadı ({observed.Error!.Code}); durum değiştirilmedi.", 502);
            if (MarketplaceInvoiceStatePolicy.FromRemote(observed.Value!.RawStatus) != MarketplaceInvoiceStatus.NotInvoiced)
                return ServiceResult<MarketplaceLinkRevocationView>.Fail("INVOICE_LINK_STILL_PRESENT", $"{connection.DisplayName} paketi hâlâ faturalı görünüyor. {instructions}", 409);
            await CompleteLinkRevocationAsync(invoice, state, package, observed.Value.RawStatus, "verified-absent", idempotencyKey, correlationId, cancellationToken);
            return ServiceResult<MarketplaceLinkRevocationView>.Ok(await LinkRevocationViewAsync(state, connection, package.ExternalPackageId, cancellationToken));
        }

        // The attempt is recorded before the request leaves, so a lost answer leaves a trace and a second click waits.
        InvoiceAdjustmentTracking.AppendDeliveryHistory(db, state, MarketplaceInvoiceLinkRevocationPolicy.Started, state.ExternalReference, null, now);
        db.AuditLogs.Add(new AuditLog { TenantId = tenantId, Action = "INVOICE_MARKETPLACE_LINK_REVOCATION_STARTED", TargetType = "Invoice", TargetId = invoiceId.ToString("D"), Reason = $"package:{package.ExternalPackageId};key:{Hash(idempotencyKey)[..16]}", CorrelationId = correlationId, CreatedAt = now });
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { return ServiceResult<MarketplaceLinkRevocationView>.Fail("INVOICE_LINK_REVOCATION_RUNNING", "Bu link için kaldırma isteği eşzamanlı başlatıldı. Sayfayı yenileyin.", 409); }

        var result = await marketplace.RevokeLinkAsync(context, new InvoiceLinkRevocationCommand(reference, expectedLinks, invoice.InvoiceNumber), cancellationToken);
        if (result.IsSuccess)
        {
            await CompleteLinkRevocationAsync(invoice, state, package, result.Value!.ObservedRawStatus, result.Value.AlreadyAbsent ? "already-absent" : "deleted", idempotencyKey, correlationId, cancellationToken);
            return ServiceResult<MarketplaceLinkRevocationView>.Ok(await LinkRevocationViewAsync(state, connection, package.ExternalPackageId, cancellationToken));
        }

        var error = result.Error!;
        var unknown = MarketplaceInvoiceLinkRevocationPolicy.LeavesOutcomeUnknown(error);
        var failedAt = timeProvider.GetUtcNow();
        InvoiceAdjustmentTracking.AppendDeliveryHistory(db, state, unknown ? MarketplaceInvoiceLinkRevocationPolicy.Unknown : MarketplaceInvoiceLinkRevocationPolicy.Required, state.ExternalReference, error.Code, failedAt);
        db.AuditLogs.Add(new AuditLog { TenantId = tenantId, Action = "INVOICE_MARKETPLACE_LINK_REVOCATION_FAILED", TargetType = "Invoice", TargetId = invoiceId.ToString("D"), Reason = $"package:{package.ExternalPackageId};{error.Code}", CorrelationId = correlationId, CreatedAt = failedAt });
        await db.SaveChangesAsync(cancellationToken);
        var message = unknown ? error.SafeMessage : $"{error.SafeMessage} {instructions}";
        return ServiceResult<MarketplaceLinkRevocationView>.Fail(error.Code, message, error.Class switch
        {
            AdapterErrorClass.Validation or AdapterErrorClass.NotSupported => 422,
            AdapterErrorClass.BusinessConflict or AdapterErrorClass.NotFound => 409,
            AdapterErrorClass.RateLimit => 429,
            AdapterErrorClass.Authentication => 409,
            _ => 502
        });
    }

    // The link is gone: the delivery state ends as LINK_REMOVED, the package no longer shows the cancelled invoice,
    // and the operational issue closes once no other package of the invoice still holds a link.
    private async Task CompleteLinkRevocationAsync(Invoice invoice, MarketplaceDeliveryState state, ShipmentPackage package, string observedRawStatus, string outcome, string idempotencyKey, string correlationId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        InvoiceAdjustmentTracking.AppendDeliveryHistory(db, state, MarketplaceInvoiceLinkRevocationPolicy.Removed, state.ExternalReference, null, now);
        state.CompletedAt = now;
        MarketplaceInvoiceReferenceHistoryRecorder.RecordPreviousReferences(db, package, null, null, now, "LINK_REVOCATION");
        package.MarketplaceInvoiceStatus = MarketplaceInvoiceStatus.NotInvoiced;
        package.MarketplaceInvoiceRawStatus = observedRawStatus;
        package.MarketplaceInvoiceNumber = null;
        package.MarketplaceInvoiceUrl = null;
        package.MarketplaceInvoiceObservedAt = now;
        package.UpdatedAt = now;
        package.Version++;
        db.AuditLogs.Add(new AuditLog { TenantId = invoice.TenantId, Action = "INVOICE_MARKETPLACE_LINK_REVOKED", TargetType = "Invoice", TargetId = invoice.Id.ToString("D"), Reason = $"package:{package.ExternalPackageId};{outcome};key:{Hash(idempotencyKey)[..16]}", CorrelationId = correlationId, CreatedAt = now });
        await InvoiceAdjustmentTracking.ResolveLinkRevocationIfDoneAsync(db, invoice.TenantId, invoice.Id, now, cancellationToken);
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException exception) when (exception.Entries.All(entry => entry.Entity is ShipmentPackage))
        {
            // An order sync changed the package meanwhile; the removal itself is still recorded and the next
            // package read brings the marketplace's view of the invoice.
            foreach (var entry in exception.Entries) entry.State = EntityState.Detached;
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task<IReadOnlyList<MarketplaceLinkRevocationView>> LinkRevocationViewsAsync(Invoice invoice, CancellationToken cancellationToken)
    {
        var states = await db.MarketplaceDeliveryStates.AsNoTracking()
            .Where(x => x.TenantId == invoice.TenantId && x.InvoiceId == invoice.Id
                && (x.Status == MarketplaceInvoiceLinkRevocationPolicy.Required || x.Status == MarketplaceInvoiceLinkRevocationPolicy.Started
                    || x.Status == MarketplaceInvoiceLinkRevocationPolicy.Unknown || x.Status == MarketplaceInvoiceLinkRevocationPolicy.Removed))
            .OrderBy(x => x.CreatedAt).ToListAsync(cancellationToken);
        if (states.Count == 0) return [];
        var connectionIds = states.Select(x => x.ConnectionId).Distinct().ToArray();
        var connections = await db.PlatformConnections.AsNoTracking().Where(x => x.TenantId == invoice.TenantId && connectionIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, cancellationToken);
        var packageIds = states.Where(x => x.PackageId.HasValue).Select(x => x.PackageId!.Value).Distinct().ToArray();
        var packageNumbers = await db.ShipmentPackages.AsNoTracking().Where(x => x.TenantId == invoice.TenantId && packageIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.ExternalPackageId, cancellationToken);
        var views = new List<MarketplaceLinkRevocationView>(states.Count);
        foreach (var state in states)
        {
            if (!connections.TryGetValue(state.ConnectionId, out var connection)) continue;
            var packageNumber = state.PackageId is { } packageId ? packageNumbers.GetValueOrDefault(packageId) : null;
            views.Add(await LinkRevocationViewAsync(state, connection, packageNumber, cancellationToken));
        }
        return views;
    }

    private async Task<MarketplaceLinkRevocationView> LinkRevocationViewAsync(MarketplaceDeliveryState state, PlatformConnection connection, string? packageNumber, CancellationToken cancellationToken)
    {
        var mode = MarketplaceInvoiceLinkRevocationPolicy.ModeFor(connection.PlatformCode);
        var now = timeProvider.GetUtcNow();
        string? blocked = null;
        var canAct = mode != MarketplaceInvoiceLinkRevocationPolicy.Manual
            && MarketplaceInvoiceLinkRevocationPolicy.CanStart(state.Status, state.UpdatedAt, now)
            && marketplace is not null;
        if (canAct && mode == MarketplaceInvoiceLinkRevocationPolicy.Automatic && !await WriteGates(state.TenantId, connection.Id, InvoicingCapabilities.InvoiceDeliver, cancellationToken))
        {
            canAct = false;
            blocked = await DeliveryBlockedReasonAsync(state.TenantId, connection.Id, cancellationToken);
        }
        else if (canAct && !IntegrationRuntimePolicy.AllowsManualRead(connection))
        {
            canAct = false;
            blocked = $"{connection.DisplayName} bağlantısı etkin değil.";
        }
        return new(state.Id, state.PackageId, connection.PlatformCode, connection.DisplayName, packageNumber ?? state.ExternalReference, state.Status, state.ErrorCode, mode,
            MarketplaceInvoiceLinkRevocationPolicy.Instructions(connection.PlatformCode, packageNumber ?? state.ExternalReference), canAct, blocked, state.UpdatedAt);
    }
}
