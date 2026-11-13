using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

// The notification center's store (ops.panel_notifications). A caller sees the tenant's shared notices and its own
// notifications raised within the retention; reads and writes never reach another tenant's or another user's rows.
public sealed class PanelNotificationService(AppDbContext db, TimeProvider timeProvider) : IPanelNotificationService
{
    private const string LikeEscape = "\\";

    public async Task<ServiceResult<int>> SaveAsync(Guid tenantId, Guid userId, IReadOnlyList<PanelNotificationInput> items, CancellationToken cancellationToken)
    {
        if (items.Count > PanelNotificationPolicy.MaximumBatch)
            return ServiceResult<int>.Fail("NOTIFICATION_BATCH_TOO_LARGE", "Bir seferde en fazla 50 bildirim kaydedilebilir; kalanını ayrı gönderin.", 400);
        var now = timeProvider.GetUtcNow().ToUniversalTime();
        var oldest = now - PanelNotificationPolicy.Retention;
        var saved = 0;
        foreach (var item in items)
        {
            var key = item.ClientKey?.Trim() ?? string.Empty;
            var message = Clip(item.Message, PanelNotificationPolicy.MaximumMessageLength);
            if (key.Length == 0 || key.Length > PanelNotificationPolicy.MaximumClientKeyLength || message is null) continue;
            var raised = item.CreatedAt?.ToUniversalTime() ?? now;
            var createdAt = raised > now ? now : raised;
            if (createdAt < oldest) continue;
            var severity = Code(item.Severity, PanelNotificationSeverities.All) ?? PanelNotificationSeverities.Info;
            var category = Code(item.Category, PanelNotificationCategories.All) ?? PanelNotificationCategories.Other;
            var title = Clip(item.Title, PanelNotificationPolicy.MaximumTitleLength) ?? Clip(message, 80)!;
            var payload = item.Payload is { Length: > 0 and <= PanelNotificationPolicy.MaximumPayloadLength } ? item.Payload : null;
            Guid? owner = item.Shared ? null : userId;
            var count = Math.Clamp(item.Count ?? 1, 1, 9_999);
            DateTimeOffset? readAt = item.Read ? now : null;
            // One row per (tenant, client key): a repeat send or a grown burst updates it in place, keeping when it was
            // first raised and whether it was dismissed; a grown burst is unread again unless the bell shows it read.
            // A key that belongs to another user's row is left alone.
            saved += await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO ops.panel_notifications AS existing ("Id", "TenantId", "UserId", "ClientKey", "Severity", "Category", "Title", "Message",
                    "Href", "ActionLabel", "PlatformCode", "StoreName", "Count", "Payload", "CreatedAt", "UpdatedAt", "ReadAt", "DismissedAt")
                VALUES ({Guid.CreateVersion7()}, {tenantId}, {owner}, {key}, {severity}, {category}, {title}, {message},
                    {PanelNotificationPolicy.InternalHref(item.Href)}, {Clip(item.ActionLabel, 80)}, {Clip(item.PlatformCode?.ToUpperInvariant(), 32)}, {Clip(item.StoreName, 160)}, {count}, {payload},
                    {createdAt}, {now}, {readAt}, NULL)
                ON CONFLICT ("TenantId", "ClientKey") DO UPDATE SET
                    "Severity" = EXCLUDED."Severity", "Category" = EXCLUDED."Category", "Title" = EXCLUDED."Title", "Message" = EXCLUDED."Message",
                    "Href" = EXCLUDED."Href", "ActionLabel" = EXCLUDED."ActionLabel", "PlatformCode" = EXCLUDED."PlatformCode", "StoreName" = EXCLUDED."StoreName",
                    "Count" = GREATEST(existing."Count", EXCLUDED."Count"), "Payload" = coalesce(EXCLUDED."Payload", existing."Payload"), "UpdatedAt" = EXCLUDED."UpdatedAt",
                    "ReadAt" = CASE WHEN EXCLUDED."ReadAt" IS NOT NULL THEN coalesce(existing."ReadAt", EXCLUDED."ReadAt")
                                    WHEN EXCLUDED."Count" > existing."Count" THEN NULL ELSE existing."ReadAt" END
                WHERE existing."UserId" IS NOT DISTINCT FROM EXCLUDED."UserId"
                """, cancellationToken);
        }
        return ServiceResult<int>.Ok(saved);
    }

    public async Task<ServiceResult<PageResult<PanelNotificationView>>> PageAsync(Guid tenantId, Guid userId, PanelNotificationQuery request, CancellationToken cancellationToken)
    {
        var query = Visible(tenantId, userId);
        var categories = Codes(request.Categories, PanelNotificationCategories.All);
        var severities = Codes(request.Severities, PanelNotificationSeverities.All);
        if (categories is { Length: 0 } || severities is { Length: 0 }) return ServiceResult<PageResult<PanelNotificationView>>.Ok(new([], null, false));
        if (categories is not null) query = query.Where(x => categories.Contains(x.Category));
        if (severities is not null) query = query.Where(x => severities.Contains(x.Severity));
        if (request.From is { } from) { var fromUtc = from.ToUniversalTime(); query = query.Where(x => x.CreatedAt >= fromUtc); }
        if (request.To is { } to) { var toUtc = to.ToUniversalTime(); query = query.Where(x => x.CreatedAt <= toUtc); }
        var state = request.State?.Trim().ToUpperInvariant();
        if (state == "UNREAD") query = query.Where(x => x.ReadAt == null);
        else if (state == "READ") query = query.Where(x => x.ReadAt != null);
        var term = request.Search?.Trim();
        if (!string.IsNullOrEmpty(term))
        {
            if (term.Length > PanelNotificationPolicy.MaximumSearchLength) term = term[..PanelNotificationPolicy.MaximumSearchLength];
            var pattern = $"%{CatalogService.EscapeLike(term)}%";
            query = query.Where(x => EF.Functions.ILike(x.Title, pattern, LikeEscape) || EF.Functions.ILike(x.Message, pattern, LikeEscape)
                || (x.StoreName != null && EF.Functions.ILike(x.StoreName, pattern, LikeEscape)));
        }
        if (!string.IsNullOrWhiteSpace(request.After))
        {
            if (!JobListCursor.TryDecode(request.After, out var afterCreatedAt, out var afterId))
                return ServiceResult<PageResult<PanelNotificationView>>.Fail("NOTIFICATION_CURSOR_INVALID", "Listenin devamı okunamadı. Listeyi yenileyip tekrar deneyin.", 400);
            query = query.Where(x => x.CreatedAt <= afterCreatedAt && (x.CreatedAt < afterCreatedAt || x.Id.CompareTo(afterId) < 0));
        }
        var limit = request.EffectiveLimit;
        var rows = await query.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).Take(limit + 1).ToListAsync(cancellationToken);
        var hasMore = rows.Count > limit;
        if (hasMore) rows = rows.Take(limit).ToList();
        var next = hasMore ? JobListCursor.Encode(rows[^1].CreatedAt, rows[^1].Id) : null;
        return ServiceResult<PageResult<PanelNotificationView>>.Ok(new(rows.Select(View).ToList(), next, hasMore));
    }

    public async Task<PanelNotificationSummary> SummaryAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken)
    {
        var counts = await Visible(tenantId, userId)
            .GroupBy(_ => 1)
            .Select(group => new { group.Key, Total = group.Count(), Unread = group.Count(x => x.ReadAt == null), Errors = group.Count(x => x.Severity == PanelNotificationSeverities.Error) })
            // One group at most; the order only keeps EF's FirstWithoutOrderBy warning out of the API log (live, 13 Nov 2026).
            .OrderBy(group => group.Key)
            .FirstOrDefaultAsync(cancellationToken);
        return counts is null ? new(0, 0, 0) : new(counts.Total, counts.Unread, counts.Errors);
    }

    public Task<int> MarkReadAsync(Guid tenantId, Guid userId, PanelNotificationSelection selection, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().ToUniversalTime();
        return Selected(tenantId, userId, selection)?.Where(x => x.ReadAt == null).ExecuteUpdateAsync(set => set.SetProperty(x => x.ReadAt, now), cancellationToken) ?? Task.FromResult(0);
    }

    public Task<int> DismissAsync(Guid tenantId, Guid userId, PanelNotificationSelection selection, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().ToUniversalTime();
        return Selected(tenantId, userId, selection)?.Where(x => x.DismissedAt == null).ExecuteUpdateAsync(set => set.SetProperty(x => x.DismissedAt, now), cancellationToken) ?? Task.FromResult(0);
    }

    private IQueryable<PanelNotification> Visible(Guid tenantId, Guid userId)
    {
        var oldest = timeProvider.GetUtcNow().ToUniversalTime() - PanelNotificationPolicy.Retention;
        return db.PanelNotifications.Where(x => x.TenantId == tenantId && (x.UserId == null || x.UserId == userId) && x.CreatedAt >= oldest);
    }

    // Null when the selection names nothing.
    private IQueryable<PanelNotification>? Selected(Guid tenantId, Guid userId, PanelNotificationSelection selection)
    {
        var query = Visible(tenantId, userId);
        if (selection.All) return query;
        var ids = (selection.Ids ?? []).Distinct().Take(500).ToArray();
        var keys = (selection.ClientKeys ?? []).Select(key => key.Trim()).Where(key => key.Length is > 0 and <= PanelNotificationPolicy.MaximumClientKeyLength).Distinct(StringComparer.Ordinal).Take(500).ToArray();
        if (ids.Length == 0 && keys.Length == 0) return null;
        return query.Where(x => ids.Contains(x.Id) || keys.Contains(x.ClientKey));
    }

    private static PanelNotificationView View(PanelNotification row) => new(row.Id, row.ClientKey, row.Severity, row.Category, row.Title, row.Message, row.Href, row.ActionLabel,
        row.PlatformCode, row.StoreName, row.Count, row.Payload, row.UserId is null, row.CreatedAt, row.UpdatedAt, row.ReadAt, row.DismissedAt);

    private static string? Clip(string? value, int maximum)
    {
        var text = value?.Trim();
        return string.IsNullOrEmpty(text) ? null : text.Length > maximum ? text[..maximum] : text;
    }

    private static string? Code(string? value, IReadOnlyList<string> known)
    {
        var code = value?.Trim().ToUpperInvariant();
        return code is not null && known.Contains(code, StringComparer.Ordinal) ? code : null;
    }

    // Null: no filter. Empty: only unknown values, so nothing can match.
    private static string[]? Codes(IReadOnlyList<string>? values, IReadOnlyList<string> known)
    {
        var codes = (values ?? []).Select(code => code.Trim().ToUpperInvariant()).Where(code => code.Length > 0).Distinct(StringComparer.Ordinal).Take(20).ToArray();
        return codes.Length == 0 ? null : codes.Where(code => known.Contains(code, StringComparer.Ordinal)).ToArray();
    }
}
