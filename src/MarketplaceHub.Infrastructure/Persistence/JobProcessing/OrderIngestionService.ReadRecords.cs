using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

// ISLEYICI-9 stage 3b: the order upsert with its read record (Çekilen Kayıtlar), moved from the processor's read records.
internal sealed partial class OrderIngestionService
{
    internal const string StaleReason = "Panelde daha yeni bir bilgi vardı; eski kayıt uygulanmadı.";
    private const string RejectedOrderReason = "Pazaryeri kaydı eksik veya tutarsızdı; uygulanmadı (Operasyon Sorunları).";

    internal async Task<bool> UpsertOrder(Guid tenantId, Guid connectionId, RemoteOrder remote, CancellationToken cancellationToken, OrderIngestionBatch? batch = null, bool saveChanges = true, bool persistFinancialObservations = false)
    {
        if (!run.ReadRecordingEnabled) return await UpsertOrderCore(tenantId, connectionId, remote, cancellationToken, batch, saveChanges, persistFinancialObservations);
        var before = await OrderStateAsync(tenantId, connectionId, remote.ExternalOrderId, fromDatabase: true, cancellationToken);
        var skippedBefore = run.SkippedCount;
        var accepted = await UpsertOrderCore(tenantId, connectionId, remote, cancellationToken, batch, saveChanges, persistFinancialObservations);
        var after = await OrderStateAsync(tenantId, connectionId, remote.ExternalOrderId, fromDatabase: false, cancellationToken);
        var outcome = !accepted ? JobReadOutcomes.Failed
            : before is null ? (after is null ? JobReadOutcomes.Skipped : JobReadOutcomes.New)
            // An invoice check reads the invoice state, which it compares whatever the order's age: an older order snapshot
            // that left the invoice as it was is unchanged there, not skipped.
            : run.ReadRecordingKind != "INVOICE" && run.SkippedCount > skippedBefore && after?.Compare == before.Compare ? JobReadOutcomes.Skipped
            : after is not null && after.Compare != before.Compare ? JobReadOutcomes.Updated
            : JobReadOutcomes.Unchanged;
        var reason = outcome == JobReadOutcomes.Failed ? RejectedOrderReason : outcome == JobReadOutcomes.Skipped ? StaleReason : null;
        var state = after ?? before;
        // An invoice check names the packages' invoice state; any other read the order's status.
        var invoiceCheck = run.ReadRecordingKind == "INVOICE";
        run.ReadRecorder.Add(invoiceCheck ? "INVOICE" : "ORDER", new JobReadRecord(remote.OrderNumber, state?.PackagesText, invoiceCheck ? state?.InvoiceText : state?.Status, outcome, reason));
        return accepted;
    }

    private sealed record OrderReadState(string Compare, string Status, string? PackagesText, string? InvoiceText);

    // The order as the read leaves it: the tracked entities (a batch may not have saved yet), or before the upsert the
    // stored row overlaid with what this run already changed in memory.
    private async Task<OrderReadState?> OrderStateAsync(Guid tenantId, Guid connectionId, string externalOrderId, bool fromDatabase, CancellationToken cancellationToken)
    {
        var tracked = db.Orders.Local.FirstOrDefault(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ExternalOrderId == externalOrderId);
        Guid orderId;
        string status;
        if (tracked is not null) { orderId = tracked.Id; status = tracked.DerivedStatus; }
        else if (fromDatabase)
        {
            var stored = await db.Orders.AsNoTracking().Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ExternalOrderId == externalOrderId)
                .Select(x => new { x.Id, x.DerivedStatus }).FirstOrDefaultAsync(cancellationToken);
            if (stored is null) return null;
            orderId = stored.Id;
            status = stored.DerivedStatus;
        }
        else return null;
        var packages = new Dictionary<Guid, (string Number, string Key, string Invoice)>();
        if (fromDatabase)
            foreach (var row in await db.ShipmentPackages.AsNoTracking().Where(x => x.TenantId == tenantId && x.OrderId == orderId)
                         .Select(x => new { x.Id, x.ExternalPackageId, x.Status, x.RawStatus, x.CargoTrackingNumber, x.MarketplaceInvoiceStatus, x.MarketplaceInvoiceNumber }).ToListAsync(cancellationToken))
                packages[row.Id] = (row.ExternalPackageId, $"{row.ExternalPackageId}|{row.Status}|{row.RawStatus}|{row.CargoTrackingNumber}|{row.MarketplaceInvoiceStatus}|{row.MarketplaceInvoiceNumber}", row.MarketplaceInvoiceStatus.ToString());
        foreach (var package in db.ShipmentPackages.Local.Where(x => x.TenantId == tenantId && x.OrderId == orderId))
            packages[package.Id] = (package.ExternalPackageId, $"{package.ExternalPackageId}|{package.Status}|{package.RawStatus}|{package.CargoTrackingNumber}|{package.MarketplaceInvoiceStatus}|{package.MarketplaceInvoiceNumber}", package.MarketplaceInvoiceStatus.ToString());
        // The comparison holds the tracking number; the stored text shows only package numbers, at most three.
        var key = string.Join(";", packages.Values.Select(x => x.Key).Order(StringComparer.Ordinal));
        var numbers = packages.Values.Select(x => x.Number).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var text = numbers.Count == 0 ? null : string.Join(", ", numbers.Take(3)) + (numbers.Count > 3 ? $" +{numbers.Count - 3}" : "");
        var invoices = packages.Values.Select(x => x.Invoice).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        return new OrderReadState($"{status}#{key}", status, text, invoices.Count == 0 ? null : string.Join(", ", invoices));
    }
}
