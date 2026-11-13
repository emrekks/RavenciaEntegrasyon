using System.Globalization;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

// The notification center's background notices (AttentionNoticeView), read from the state itself on every call: a read
// failing run after run (its sync cursor), and product cards whose approval check ran out of time. Nothing is written;
// a notice disappears as soon as its cause does.
public sealed class AttentionNoticeService(AppDbContext db, TimeProvider timeProvider) : IAttentionNoticeService
{
    private const int MaxItemsPerNotice = 50;

    public async Task<IReadOnlyList<AttentionNoticeView>> ListAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var connections = await db.PlatformConnections.AsNoTracking()
            .Where(x => x.TenantId == tenantId && (x.Status == "ACTIVE" || x.Status == "VERIFIED"))
            .Select(x => new { x.Id, x.PlatformCode, x.DisplayName })
            .ToListAsync(cancellationToken);
        if (connections.Count == 0) return await WithOffsiteBackupAsync(tenantId, [], cancellationToken);
        var byId = connections.ToDictionary(x => x.Id);
        var connectionIds = connections.Select(x => x.Id).ToArray();
        var notices = new List<AttentionNoticeView>();

        var threshold = AttentionNoticePolicy.ConsecutiveFailureThreshold;
        var failing = await db.SyncCursors.AsNoTracking()
            .Where(x => x.TenantId == tenantId && connectionIds.Contains(x.ConnectionId) && x.ConsecutiveFailureCount >= threshold)
            .Select(x => new { x.ConnectionId, x.ResourceType, x.ConsecutiveFailureCount, x.LastErrorAt, x.LastError, x.LastAttemptAt, x.LastSuccessAt })
            .ToListAsync(cancellationToken);
        foreach (var cursor in failing
                     .Where(x => AttentionNoticePolicy.IsReadResource(x.ResourceType)
                         && AttentionNoticePolicy.IsFailing(x.ConsecutiveFailureCount, x.LastErrorAt, now)
                         && !AttentionNoticePolicy.CoveredByOwnNotice(x.ResourceType, x.LastError))
                     .OrderBy(x => x.ConnectionId).ThenBy(x => x.ResourceType, StringComparer.Ordinal))
        {
            var connection = byId[cursor.ConnectionId];
            notices.Add(new AttentionNoticeView(
                $"sync-cursor:{cursor.ConnectionId:N}:{cursor.ResourceType}",
                // One streak is one notice: it is updated in place while it lasts; a new streak after a success is new.
                FailingRevision(cursor.LastSuccessAt),
                AttentionNoticePolicy.SyncCursorFailingCode,
                AttentionNoticePolicy.FailingTitle,
                AttentionNoticePolicy.FailingMessage(cursor.ResourceType, cursor.ConsecutiveFailureCount, cursor.LastError),
                cursor.ConnectionId,
                connection.PlatformCode,
                connection.DisplayName,
                cursor.ResourceType,
                cursor.ConsecutiveFailureCount,
                cursor.LastAttemptAt ?? cursor.LastErrorAt,
                cursor.LastSuccessAt,
                cursor.LastError,
                []));
        }

        var expired = await db.ChannelListingProfiles.AsNoTracking()
            .Where(x => x.TenantId == tenantId && connectionIds.Contains(x.ConnectionId)
                && x.ActualStatus == "MANUAL_REVIEW" && x.LastRejectionCode == AttentionNoticePolicy.ApprovalDeadlineExpiredCode)
            .Select(x => new { x.Id, x.ConnectionId, x.ProductId })
            .ToListAsync(cancellationToken);
        if (expired.Count > 0)
        {
            var profileIds = expired.Select(x => x.Id).ToArray();
            var productIds = expired.Select(x => x.ProductId).Distinct().ToArray();
            var titles = await db.Products.AsNoTracking()
                .Where(x => x.TenantId == tenantId && productIds.Contains(x.Id))
                .Select(x => new { x.Id, x.Title })
                .ToDictionaryAsync(x => x.Id, x => x.Title, cancellationToken);
            var barcodes = (await db.ChannelListingVariants.AsNoTracking()
                    .Where(x => x.TenantId == tenantId && profileIds.Contains(x.ProfileId))
                    .Select(x => new { x.ProfileId, x.ExternalBarcode, x.ExternalSku })
                    .ToListAsync(cancellationToken))
                .GroupBy(x => x.ProfileId)
                .ToDictionary(group => group.Key, group => (IReadOnlyList<string>)group
                    .Select(x => (x.ExternalBarcode ?? x.ExternalSku)?.Trim())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => x!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Order(StringComparer.Ordinal)
                    .ToList());
            var expiredConnectionIds = expired.Select(x => x.ConnectionId).Distinct().ToArray();
            var checks = (await db.IntegrationJobs.AsNoTracking()
                    .Where(x => x.TenantId == tenantId && x.ConnectionId != null && expiredConnectionIds.Contains(x.ConnectionId.Value)
                        && x.JobType == MarketplaceJobTypes.ProductApprovalReconcile && x.Status == JobStatus.ManualReview
                        && x.LastErrorCode == AttentionNoticePolicy.ApprovalDeadlineExpiredCode)
                    .Select(x => new { ConnectionId = x.ConnectionId!.Value, x.CompletedAt })
                    .ToListAsync(cancellationToken))
                .GroupBy(x => x.ConnectionId)
                .ToDictionary(group => group.Key, group => (Count: group.Count(), Last: group.Max(x => x.CompletedAt)));
            foreach (var group in expired.GroupBy(x => x.ConnectionId).OrderBy(x => x.Key))
            {
                var connection = byId[group.Key];
                var marketplace = AttentionNoticePolicy.PlatformName(connection.PlatformCode);
                var items = group
                    .Select(x => new AttentionNoticeItemView(x.ProductId, titles.GetValueOrDefault(x.ProductId) ?? "Ürün", barcodes.GetValueOrDefault(x.Id) ?? []))
                    .OrderBy(x => x.Label, StringComparer.Create(CultureInfo.GetCultureInfo("tr-TR"), true))
                    .Take(MaxItemsPerNotice)
                    .ToList();
                var check = checks.GetValueOrDefault(group.Key);
                notices.Add(new AttentionNoticeView(
                    $"approval-expired:{group.Key:N}",
                    // A card that joins or leaves the list raises the notice again.
                    string.Join(',', group.Select(x => x.Id.ToString("N")).Order(StringComparer.Ordinal)),
                    AttentionNoticePolicy.ApprovalDeadlineExpiredCode,
                    AttentionNoticePolicy.ExpiredTitle(marketplace),
                    AttentionNoticePolicy.ExpiredMessage(marketplace, group.Count()),
                    group.Key,
                    connection.PlatformCode,
                    connection.DisplayName,
                    "PRODUCT_APPROVAL",
                    check.Count,
                    check.Last,
                    null,
                    AttentionNoticePolicy.ApprovalDeadlineExpiredCode,
                    items));
            }
        }
        return await WithOffsiteBackupAsync(tenantId, notices, cancellationToken);
    }

    // The Google Drive copy has stopped: an invalid Google authorization, or failed runs that already alerted the owner.
    private async Task<IReadOnlyList<AttentionNoticeView>> WithOffsiteBackupAsync(Guid tenantId, List<AttentionNoticeView> notices, CancellationToken cancellationToken)
    {
        var rows = await db.TenantSettings.AsNoTracking()
            .Where(x => x.TenantId == tenantId && (x.Key == Backups.OffsiteBackupSettingsService.SettingKey || x.Key == Backups.OffsiteBackupSettingsService.StateKey))
            .ToListAsync(cancellationToken);
        var settings = Backups.OffsiteBackupSettingsService.ReadSettings(rows.FirstOrDefault(x => x.Key == Backups.OffsiteBackupSettingsService.SettingKey));
        var state = Backups.OffsiteBackupSettingsService.ReadState(rows.FirstOrDefault(x => x.Key == Backups.OffsiteBackupSettingsService.StateKey));
        if (settings?.Enabled != true || state is null || state.ConnectionId is null) return notices;
        var since = state.AuthInvalidAt ?? (state.AlertSentAt is not null ? state.FailingSince : null);
        if (since is not { } failingSince) return notices;
        var message = state.AuthInvalidAt is not null
            ? $"Google Drive yedeği durdu: {state.AuthError ?? "Google yetkisi geçersiz."} Sistem Ayarları › Google Drive Yedeği'nden Google ile yeniden bağlanın."
            : $"Google Drive yedeği {state.ConsecutiveFailures} denemedir gönderilemiyor: {state.LastError}";
        notices.Insert(0, new AttentionNoticeView(
            AttentionNoticePolicy.OffsiteBackupKey,
            AttentionNoticePolicy.OffsiteBackupRevision(failingSince),
            AttentionNoticePolicy.OffsiteBackupFailingCode,
            AttentionNoticePolicy.OffsiteBackupTitle,
            message,
            Guid.Empty,
            string.Empty,
            "Google Drive Yedeği",
            "OFFSITE_BACKUP",
            Math.Max(state.ConsecutiveFailures, 1),
            state.LastAttemptAt,
            state.LastSuccessAt,
            state.AuthError ?? state.LastError,
            []));
        return notices;
    }


    internal static string FailingRevision(DateTimeOffset? lastSuccessAt) =>
        lastSuccessAt is { } success ? success.UtcTicks.ToString(CultureInfo.InvariantCulture) : "never";
}
