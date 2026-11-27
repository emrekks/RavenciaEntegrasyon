using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

// The bell's "Yeni Sipariş" notifications (NewOrderNoticePolicy), written once at the end of a job run from the orders the
// run created (JobRunContext.NoteNewOrder). Only rows that were really stored count, so a save that failed raises nothing.
// It writes the notification center's row alone (ops.panel_notifications, shared): never an order, invoice or stock row.
// The caller keeps a failure here away from the run (MarketplaceJobProcessor.RecordNewOrderNoticesAsync); inside an open
// transaction the write runs under a savepoint so a failed insert does not abort the transaction around it.
internal static class NewOrderNotice
{
    private const int MaximumItems = 50;
    private const string Savepoint = "new_order_notice";
    private static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    // Returns how many notifications were written (one per store at most).
    internal static async Task<int> RecordAsync(AppDbContext db, Guid tenantId, JobRunContext run, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!run.NewOrderNotices || run.NewOrderIdsByConnection.Count == 0) return 0;
        now = now.ToUniversalTime();
        var startedAt = run.NewOrderNoticeRunStartedAt.ToUniversalTime();
        var written = 0;
        foreach (var (connectionId, noted) in run.NewOrderIdsByConnection)
        {
            if (noted.Count == 0) continue;
            var ids = noted.ToArray();
            var orders = (await db.Orders.AsNoTracking()
                    .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && ids.Contains(x.Id))
                    .Select(x => new { x.Id, x.OrderNumber, x.OrderedAt, x.DerivedStatus })
                    .ToListAsync(cancellationToken))
                .Where(x => NewOrderNoticePolicy.IsEligible(x.OrderedAt, x.DerivedStatus, now))
                .OrderByDescending(x => x.OrderedAt).ThenBy(x => x.OrderNumber, StringComparer.Ordinal)
                .ToList();
            if (orders.Count == 0) continue;
            // A store that had no order before this run began: its first read or a read after a reset, not new business.
            var hadOrders = await db.Orders.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.CreatedAt < startedAt, cancellationToken);
            if (!hadOrders) continue;
            var connection = await db.PlatformConnections.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.Id == connectionId)
                .Select(x => new { x.PlatformCode, x.DisplayName })
                .SingleOrDefaultAsync(cancellationToken);
            if (connection is null) continue;
            var orderIds = orders.Select(x => x.Id).ToArray();
            // Packages of the new orders; Trendyol's "0" placeholder of an order awaiting payment is not a package yet, but
            // the order still counts as one.
            var packageCounts = await db.ShipmentPackages.AsNoTracking()
                .Where(x => x.TenantId == tenantId && orderIds.Contains(x.OrderId) && x.ExternalPackageId != ShipmentPackageSupersession.PlaceholderExternalId)
                .GroupBy(x => x.OrderId)
                .Select(group => new { group.Key, Count = group.Count() })
                .ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);
            var packages = orders.Sum(order => Math.Max(1, packageCounts.GetValueOrDefault(order.Id)));
            var single = orders.Count == 1 ? orders[0] : null;
            var platformCode = connection.PlatformCode.ToUpperInvariant();
            var message = NewOrderNoticePolicy.Message(AttentionNoticePolicy.PlatformName(platformCode), packages, single?.OrderNumber);
            var href = NewOrderNoticePolicy.Href(single?.OrderNumber);
            var actionLabel = single is null ? NewOrderNoticePolicy.ActionLabel : NewOrderNoticePolicy.SingleActionLabel;
            var key = NewOrderNoticePolicy.ClientKey(connectionId, run.NewOrderNoticeRunId);
            var storeName = Clip(connection.DisplayName, 160);
            // The bell's card (shared/notifications.ts AppNotification), so the bell and the center show it alike.
            var items = orders.Take(MaximumItems).Select(order => new NoticeSubject("order", order.Id.ToString("D"), order.OrderNumber, NewOrderNoticePolicy.Href(order.OrderNumber))).ToList();
            string? payload = JsonSerializer.Serialize(new NoticeCard(key, message, "info", now, false, NewOrderNoticePolicy.Title, "Sipariş okuma", href, actionLabel,
                platformCode, connectionId.ToString("D"), storeName, single is null ? null : items[0], single is null ? items : null, key, true, "Otomatik"), PayloadJson);
            if (payload.Length > PanelNotificationPolicy.MaximumPayloadLength) payload = null;
            await UpsertAsync(db, tenantId, key, message, href, actionLabel, platformCode, storeName, payload, now, cancellationToken);
            written++;
        }
        return written;
    }

    private static async Task UpsertAsync(AppDbContext db, Guid tenantId, string key, string message, string href, string actionLabel, string platformCode, string? storeName, string? payload, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var transaction = db.Database.CurrentTransaction;
        if (transaction is not null) await transaction.CreateSavepointAsync(Savepoint, cancellationToken);
        try
        {
            // One row per (tenant, key): the same run writing again keeps when it was raised and replaces what it says.
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO ops.panel_notifications AS existing ("Id", "TenantId", "UserId", "ClientKey", "Severity", "Category", "Title", "Message",
                    "Href", "ActionLabel", "PlatformCode", "StoreName", "Count", "Payload", "CreatedAt", "UpdatedAt", "ReadAt", "DismissedAt")
                VALUES ({Guid.CreateVersion7()}, {tenantId}, NULL, {key}, {PanelNotificationSeverities.Info}, {"ORDER"}, {NewOrderNoticePolicy.Title}, {message},
                    {href}, {actionLabel}, {platformCode}, {storeName}, 1, {payload}, {now}, {now}, NULL, NULL)
                ON CONFLICT ("TenantId", "ClientKey") DO UPDATE SET
                    "Message" = EXCLUDED."Message", "Href" = EXCLUDED."Href", "ActionLabel" = EXCLUDED."ActionLabel",
                    "StoreName" = EXCLUDED."StoreName", "Payload" = EXCLUDED."Payload", "UpdatedAt" = EXCLUDED."UpdatedAt"
                WHERE existing."UserId" IS NULL
                """, cancellationToken);
            if (transaction is not null) await transaction.ReleaseSavepointAsync(Savepoint, cancellationToken);
        }
        catch when (transaction is not null)
        {
            await transaction.RollbackToSavepointAsync(Savepoint, CancellationToken.None);
            throw;
        }
    }

    private static string? Clip(string? value, int maximum)
    {
        var text = value?.Trim();
        return string.IsNullOrEmpty(text) ? null : text.Length > maximum ? text[..maximum] : text;
    }

    private sealed record NoticeSubject(string Kind, string Id, string Label, string Href);

    private sealed record NoticeCard(string Id, string Message, string Kind, DateTimeOffset CreatedAt, bool Read, string Title, string Operation, string Href, string ActionLabel,
        string PlatformCode, string ConnectionId, string? StoreName, NoticeSubject? Subject, IReadOnlyList<NoticeSubject>? Items, string GroupKey, bool Recorded, string Source);
}
