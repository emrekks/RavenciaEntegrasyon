using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

// A new order an integrity check refused (missing lines or packages, invalid line quantities, lines without a package,
// package quantities that do not add up) is not lost behind the read window (audit 14 Nov 2026: PttAVM order
// PTT-1C2OSLF2R-120926, refused at its first import, was never read again because only Trendyol's refusals joined the
// re-read list and PttAVM's reads cover two days). On every marketplace it joins the re-read list (RejectedOrderRereadPolicy:
// read by its number, with backoff), and an ORDER_NOT_IMPORTED notice stays open until the order is imported: the
// 30-day stale pass leaves it, and the import resolves it with the refusals that kept it out.
public sealed partial class MarketplaceJobProcessor
{
    internal const string OrderNotImportedPrefix = "order-not-imported:";

    internal static string OrderNotImportedKey(Guid connectionId, string externalOrderId) => $"{OrderNotImportedPrefix}{connectionId}:{externalOrderId}";

    // The order-level refusal keys UpsertOrderCore records (each ends with the event's time in ms).
    private static readonly string[] OrderRefusalKinds = ["order-contract", "order-lines", "order-coverage"];

    private async Task NoteRefusedOrderAsync(Guid tenantId, Guid connectionId, RemoteOrder remote, bool isTrendyol, bool isNew, CancellationToken cancellationToken)
    {
        // Trendyol re-reads every refused order (T2-O8); every other marketplace the orders the panel does not have yet.
        if (isTrendyol || isNew) rereadOrderIds.Add(remote.ExternalOrderId);
        if (!isNew) return;
        await RecordIssue(tenantId, OrderNotImportedKey(connectionId, remote.ExternalOrderId), OperationalIssueRetentionService.OrderNotImportedCode,
            $"{remote.OrderNumber} numaralı sipariş pazaryeri kaydı eksik veya tutarsız olduğu için içeri alınamadı; panel siparişi numarasıyla aralıklarla yeniden okuyor. Sipariş içeri alınınca bu uyarı kendiliğinden kapanır; kapanmazsa siparişi pazaryeri panelinden kontrol edin.",
            cancellationToken);
    }

    // A new order that lands closes what kept it out before: its not-imported notice and its refusals.
    private async Task ResolveOrderRefusalsAsync(Guid tenantId, Guid connectionId, RemoteOrder remote, CancellationToken cancellationToken)
    {
        var marker = OrderNotImportedKey(connectionId, remote.ExternalOrderId);
        var prefixes = OrderRefusalKinds.Select(kind => $"{kind}:{connectionId}:{remote.ExternalOrderId}:")
            .Concat(remote.Packages.Where(package => !string.IsNullOrWhiteSpace(package.ExternalPackageId))
                .Select(package => OperationalIssueRetentionService.PackageQuantityIssuePrefix(connectionId, package.ExternalPackageId)))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var open = await db.OperationalIssues
            .Where(x => x.TenantId == tenantId && x.Status != IssueStatus.Resolved && (x.DedupeKey == marker || prefixes.Any(prefix => x.DedupeKey.StartsWith(prefix))))
            .ToListAsync(cancellationToken);
        // Notices this run recorded and has not saved yet (the re-read's own not-imported notice) close too.
        open.AddRange(db.OperationalIssues.Local.Where(x => x.TenantId == tenantId && x.Status != IssueStatus.Resolved
            && (x.DedupeKey == marker || prefixes.Any(prefix => x.DedupeKey.StartsWith(prefix, StringComparison.Ordinal)))));
        var now = timeProvider.GetUtcNow();
        foreach (var issue in open)
        {
            issue.Status = IssueStatus.Resolved;
            issue.ResolvedAt = now;
        }
    }

    // The orders the open refusal notices name that the panel does not have, to be read again by number now: an order
    // whose not-imported notice was last tried a week ago or more (an order still refused keeps being tried, slowly), and
    // one only a refusal seen once names (recorded before refused new orders joined the list; within 30 days). Each one
    // taken gets its not-imported notice touched, so it is taken again only a week later. A notice whose order the panel
    // has (imported by another path) is resolved.
    private async Task<IReadOnlyList<string>> RefusedOrdersToRereadAsync(Guid tenantId, Guid connectionId, IReadOnlyCollection<string> listed, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var markerPrefix = $"{OrderNotImportedPrefix}{connectionId}:";
        var refusalPrefixes = OrderRefusalKinds.Append("package-quantity").Select(kind => $"{kind}:{connectionId}:").ToArray();
        var monthAgo = now - TimeSpan.FromDays(30);
        var markers = await db.OperationalIssues.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.Status != IssueStatus.Resolved && x.DedupeKey.StartsWith(markerPrefix))
            .OrderBy(x => x.LastSeenAt)
            .Select(x => new { x.DedupeKey, x.LastSeenAt })
            .Take(200)
            .ToListAsync(cancellationToken);
        var refusals = await db.OperationalIssues.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.Status != IssueStatus.Resolved && x.OccurrenceCount == 1 && x.FirstSeenAt >= monthAgo
                && refusalPrefixes.Any(prefix => x.DedupeKey.StartsWith(prefix)))
            .OrderBy(x => x.FirstSeenAt)
            .Select(x => x.DedupeKey)
            .Take(200)
            .ToListAsync(cancellationToken);
        var marked = markers.Select(x => RefusedOrderIdOf(x.DedupeKey, connectionId)).Where(id => id is not null).Select(id => id!).ToHashSet(StringComparer.Ordinal);
        var candidates = markers.Where(x => x.LastSeenAt <= now - RefusedOrderRetryInterval).Select(x => RefusedOrderIdOf(x.DedupeKey, connectionId))
            .Concat(refusals.Select(key => RefusedOrderIdOf(key, connectionId)).Where(id => id is not null && !marked.Contains(id)))
            .Where(id => id is not null && !listed.Contains(id))
            .Select(id => id!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var check = candidates.Concat(marked).Distinct(StringComparer.Ordinal).ToList();
        if (check.Count == 0) return [];
        var stored = (await db.Orders.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && check.Contains(x.ExternalOrderId))
                .Select(x => x.ExternalOrderId)
                .ToListAsync(cancellationToken))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var orderId in marked.Where(stored.Contains)) await ResolveIssue(tenantId, OrderNotImportedKey(connectionId, orderId), cancellationToken);
        var due = candidates.Where(id => !stored.Contains(id)).ToList();
        foreach (var orderId in due)
            await RecordIssue(tenantId, OrderNotImportedKey(connectionId, orderId), OperationalIssueRetentionService.OrderNotImportedCode,
                $"{orderId} numaralı sipariş pazaryeri kaydı eksik veya tutarsız olduğu için içeri alınamadı; panel siparişi numarasıyla aralıklarla yeniden okuyor. Sipariş içeri alınınca bu uyarı kendiliğinden kapanır; kapanmazsa siparişi pazaryeri panelinden kontrol edin.",
                cancellationToken);
        return due;
    }

    // How long an order still refused after its re-reads waits before it is read again.
    internal static readonly TimeSpan RefusedOrderRetryInterval = TimeSpan.FromDays(7);

    // The order a refusal notice names: the not-imported notice and the order-level refusals carry its id; a package
    // quantity refusal names a package, which is the order itself only on the marketplaces that keep one package per order
    // named "order:{number}" (PttAVM, Pazarama, n11 without a cargo code). Null when the key names no order.
    internal static string? RefusedOrderIdOf(string dedupeKey, Guid connectionId)
    {
        var marker = $"{OrderNotImportedPrefix}{connectionId}:";
        if (dedupeKey.StartsWith(marker, StringComparison.Ordinal))
            return dedupeKey.Length > marker.Length ? dedupeKey[marker.Length..] : null;
        foreach (var kind in OrderRefusalKinds.Append("package-quantity"))
        {
            var prefix = $"{kind}:{connectionId}:";
            if (!dedupeKey.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var rest = dedupeKey[prefix.Length..];
            var lastColon = rest.LastIndexOf(':');
            // The key ends with the event time in ms; anything else (an adapter's own contract key) names no order.
            if (lastColon <= 0 || !long.TryParse(rest[(lastColon + 1)..], out _)) return null;
            var subject = rest[..lastColon];
            if (kind != "package-quantity") return subject;
            return subject.StartsWith("order:", StringComparison.Ordinal) && subject.Length > "order:".Length ? subject["order:".Length..] : null;
        }
        return null;
    }
}
