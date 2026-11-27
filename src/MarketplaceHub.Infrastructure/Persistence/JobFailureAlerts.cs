using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

// The bell's job alerts (JobFailureAlertPolicy), written by the worker's alert pass (Worker:JobFailureAlerts:*) into the
// notification center's shared rows, the same way NewOrderNotice writes "Yeni Sipariş". It looks at the jobs that ended
// DEAD or went to MANUAL_REVIEW within the lookback, and at the jobs whose BLOCKED state passed a day within the lookback,
// so every stopped job is looked at in a few passes and then never again. A key already written (same connection, job
// type, error code and day) is left as it is: one bell entry a day, whether the owner read or removed it. It writes the
// notification row alone; no job, order or marketplace state is touched.
public static class JobFailureAlerts
{
    private static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
    private const int MaximumJobsPerPass = 500;

    // Returns how many new notifications were written.
    public static async Task<int> RaiseAsync(AppDbContext db, DateTimeOffset now, TimeSpan lookback, CancellationToken cancellationToken)
    {
        var utcNow = now.ToUniversalTime();
        var since = utcNow - lookback;
        var blockedBefore = utcNow - JobFailureAlertPolicy.BlockedFor;
        var blockedSince = blockedBefore - lookback;
        var preSend = JobRetentionService.PreSendBlockCodes;
        var stopped = await (from job in db.IntegrationJobs.AsNoTracking()
                             where ((job.Status == JobStatus.Dead || job.Status == JobStatus.ManualReview) && job.CompletedAt > since)
                                 || (job.Status == JobStatus.Blocked && job.CompletedAt <= blockedBefore && job.CompletedAt > blockedSince)
                             orderby job.CompletedAt
                             select new { job.Id, job.TenantId, job.ConnectionId, job.JobType, job.Status, job.LastErrorCode, job.LastErrorSummary, job.CompletedAt })
            .Take(MaximumJobsPerPass)
            .ToListAsync(cancellationToken);
        var candidates = stopped
            .Where(job => !JobFailureAlertPolicy.IsSuperseded(job.LastErrorCode))
            .Where(job => job.Status != JobStatus.Blocked || job.LastErrorCode is null || !preSend.Contains(job.LastErrorCode))
            .Select(job =>
            {
                var kind = job.Status == JobStatus.Dead ? JobFailureAlertPolicy.Kind.Dead : job.Status == JobStatus.ManualReview ? JobFailureAlertPolicy.Kind.ManualReview : JobFailureAlertPolicy.Kind.Blocked;
                var raisedAt = kind == JobFailureAlertPolicy.Kind.Blocked ? job.CompletedAt!.Value + JobFailureAlertPolicy.BlockedFor : job.CompletedAt!.Value;
                return (Job: job, Kind: kind, Key: JobFailureAlertPolicy.ClientKey(job.ConnectionId, job.JobType, job.LastErrorCode, raisedAt));
            })
            // One per key: the latest job of the key names it.
            .GroupBy(item => (item.Job.TenantId, item.Key))
            .Select(group => group.Last())
            .ToList();
        if (candidates.Count == 0) return 0;

        var tenantIds = candidates.Select(item => item.Job.TenantId).Distinct().ToArray();
        var keys = candidates.Select(item => item.Key).Distinct().ToArray();
        var written = (await db.PanelNotifications.AsNoTracking()
                .Where(x => tenantIds.Contains(x.TenantId) && keys.Contains(x.ClientKey))
                .Select(x => new { x.TenantId, x.ClientKey })
                .ToListAsync(cancellationToken))
            .Select(x => (x.TenantId, x.ClientKey)).ToHashSet();
        var fresh = candidates.Where(item => !written.Contains((item.Job.TenantId, item.Key))).ToList();
        if (fresh.Count == 0) return 0;

        var connectionIds = fresh.Where(item => item.Job.ConnectionId is not null).Select(item => item.Job.ConnectionId!.Value).Distinct().ToArray();
        var connections = await db.PlatformConnections.AsNoTracking()
            .Where(x => connectionIds.Contains(x.Id))
            .Select(x => new { x.Id, x.TenantId, x.PlatformCode, x.DisplayName })
            .ToDictionaryAsync(x => (x.TenantId, x.Id), cancellationToken);
        var added = 0;
        foreach (var (job, kind, key) in fresh)
        {
            var connection = job.ConnectionId is { } connectionId && connections.TryGetValue((job.TenantId, connectionId), out var found) ? found : null;
            var platformCode = connection?.PlatformCode.ToUpperInvariant();
            var platformName = platformCode is null ? "Panel" : AttentionNoticePolicy.PlatformName(platformCode);
            var storeName = Clip(connection?.DisplayName, 160);
            var message = JobFailureAlertPolicy.Message(kind, storeName, platformName, job.JobType, JobFailureAlertPolicy.Reason(job.LastErrorCode, job.LastErrorSummary));
            var title = JobFailureAlertPolicy.Title(kind);
            var href = JobFailureAlertPolicy.Href(job.Id);
            var severity = JobFailureAlertPolicy.Severity(kind);
            var jobName = JobFailureAlertPolicy.JobName(job.JobType);
            // The bell's card (shared/notifications.ts AppNotification), so the bell and the center show it alike.
            string? payload = JsonSerializer.Serialize(new AlertCard(key, message, severity == PanelNotificationSeverities.Error ? "error" : "warning", utcNow, false, title, "İş Takibi", href,
                JobFailureAlertPolicy.ActionLabel, platformCode, job.ConnectionId?.ToString("D"), storeName, new AlertSubject("job", job.Id.ToString("D"), jobName, href), key, true, "Otomatik", job.LastErrorCode), PayloadJson);
            if (payload.Length > PanelNotificationPolicy.MaximumPayloadLength) payload = null;
            added += await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO ops.panel_notifications ("Id", "TenantId", "UserId", "ClientKey", "Severity", "Category", "Title", "Message",
                    "Href", "ActionLabel", "PlatformCode", "StoreName", "Count", "Payload", "CreatedAt", "UpdatedAt", "ReadAt", "DismissedAt")
                VALUES ({Guid.CreateVersion7()}, {job.TenantId}, NULL, {key}, {severity}, {JobFailureAlertPolicy.Category}, {title}, {message},
                    {href}, {JobFailureAlertPolicy.ActionLabel}, {platformCode}, {storeName}, 1, {payload}, {utcNow}, {utcNow}, NULL, NULL)
                ON CONFLICT ("TenantId", "ClientKey") DO NOTHING
                """, cancellationToken);
        }
        return added;
    }

    private static string? Clip(string? value, int maximum)
    {
        var text = value?.Trim();
        return string.IsNullOrEmpty(text) ? null : text.Length > maximum ? text[..maximum] : text;
    }

    private sealed record AlertSubject(string Kind, string Id, string Label, string Href);

    private sealed record AlertCard(string Id, string Message, string Kind, DateTimeOffset CreatedAt, bool Read, string Title, string Operation, string Href, string ActionLabel,
        string? PlatformCode, string? ConnectionId, string? StoreName, AlertSubject Subject, string GroupKey, bool Recorded, string Source, string? Code);
}
