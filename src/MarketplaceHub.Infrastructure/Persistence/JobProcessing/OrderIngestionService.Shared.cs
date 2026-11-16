using System.Text.Json;
using MarketplaceHub.Domain;

namespace MarketplaceHub.Infrastructure.Persistence;

// ISLEYICI-9 stage 3b: what the moved order ingestion used from the processor: its issue, cursor and text helpers (the
// same classes and functions the processor calls), the run's counting, and the invoice discovery note (moved as it was).
internal sealed partial class OrderIngestionService
{
    private Task RecordIssue(Guid tenantId, string key, string code, string summary, CancellationToken cancellationToken) =>
        issues.RecordAsync(tenantId, key, code, summary, timeProvider.GetUtcNow(), cancellationToken);

    private Task ResolveIssue(Guid tenantId, string key, CancellationToken cancellationToken) =>
        issues.ResolveAsync(tenantId, key, timeProvider.GetUtcNow(), cancellationToken);

    private Task<SyncCursor> Cursor(Guid tenantId, Guid connectionId, string resource, CancellationToken cancellationToken) =>
        cursors.GetAsync(tenantId, connectionId, resource, cancellationToken);

    private void TrackReceived(bool changed = true) => run.TrackReceived(changed);

    private static string Short(string? value, int maximum) => MarketplaceJobProcessor.Short(value, maximum);

    private static string Wire<T>(T value) where T : Enum => MarketplaceJobProcessor.Wire(value);

    private sealed record InvoiceDiscoveryCounts(int Stream, int Reconciliation, DateTimeOffset Since, string[] RecentReconciliationPackages);

    private async Task NoteInvoiceDiscoveryAsync(ShipmentPackage package, string source, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // One job may note several packages before it saves; reuse the cursor it already tracks.
        var cursor = db.SyncCursors.Local.FirstOrDefault(x => x.TenantId == package.TenantId && x.ConnectionId == package.ConnectionId && x.ResourceType == "TRENDYOL_INVOICE_DISCOVERY")
            ?? await Cursor(package.TenantId, package.ConnectionId, "TRENDYOL_INVOICE_DISCOVERY", cancellationToken);
        InvoiceDiscoveryCounts? counts = null;
        if (!string.IsNullOrWhiteSpace(cursor.OpaqueCursor))
        {
            try { counts = JsonSerializer.Deserialize<InvoiceDiscoveryCounts>(cursor.OpaqueCursor); }
            catch (JsonException) { }
        }
        counts ??= new(0, 0, now, []);
        counts = source == "reconciliation"
            ? counts with { Reconciliation = counts.Reconciliation + 1, RecentReconciliationPackages = [.. counts.RecentReconciliationPackages.Append(package.ExternalPackageId).TakeLast(20)] }
            : counts with { Stream = counts.Stream + 1 };
        cursor.OpaqueCursor = JsonSerializer.Serialize(counts);
        cursor.LastSuccessAt = now;
        cursor.Version++;
    }
}
