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
        var blockedFor = JobFailureAlertPolicy.BlockedFor;
        // A stop that raises its own notice (a brand list refused for shrinking) is not alerted a second time.
        var ownNotice = JobFailureAlertPolicy.OwnNoticeCodes.ToArray();
        // Review of 27 Nov 2026 (O2): the filters and the one-per-key reduction run in the database, newest first, before the
        // limit; a bulk run of superseded or pre-send-blocked jobs can no longer fill the pass and hide a new failure.
        // One row per tenant, connection, job type, error code and Istanbul day of the alert (the key), the latest job naming it.
        var stopped = await db.Database.SqlQuery<StoppedRow>($"""
            SELECT * FROM (
                SELECT DISTINCT ON (job."TenantId", job."ConnectionId", job."JobType", coalesce(job."LastErrorCode", ''), raised.day)
                       job."Id", job."TenantId", job."ConnectionId", job."JobType", job."Status", job."LastErrorCode", job."LastErrorSummary", job."CompletedAt"
                FROM integration.jobs job
                CROSS JOIN LATERAL (SELECT ((CASE WHEN job."Status" = 'BLOCKED' THEN job."CompletedAt" + {blockedFor} ELSE job."CompletedAt" END)
                    AT TIME ZONE 'Europe/Istanbul')::date AS day) raised
                WHERE ((job."Status" IN ('DEAD', 'MANUAL_REVIEW') AND job."CompletedAt" > {since})
                       OR (job."Status" = 'BLOCKED' AND job."CompletedAt" <= {blockedBefore} AND job."CompletedAt" > {blockedSince}
                           AND (job."LastErrorCode" IS NULL OR NOT (job."LastErrorCode" = ANY({preSend})))))
                  AND (job."LastErrorCode" IS NULL OR (job."LastErrorCode" NOT LIKE '%SUPERSEDED%' AND NOT (job."LastErrorCode" = ANY({ownNotice}))))
                ORDER BY job."TenantId", job."ConnectionId", job."JobType", coalesce(job."LastErrorCode", ''), raised.day, job."CompletedAt" DESC, job."Id" DESC) latest
            ORDER BY "CompletedAt" DESC
            LIMIT {MaximumJobsPerPass}
            """).ToListAsync(cancellationToken);
        var candidates = stopped
            .Select(job =>
            {
                var kind = job.Status == "DEAD" ? JobFailureAlertPolicy.Kind.Dead : job.Status == "MANUAL_REVIEW" ? JobFailureAlertPolicy.Kind.ManualReview : JobFailureAlertPolicy.Kind.Blocked;
                var raisedAt = kind == JobFailureAlertPolicy.Kind.Blocked ? job.CompletedAt + blockedFor : job.CompletedAt;
                return (Job: job, Kind: kind, Key: JobFailureAlertPolicy.ClientKey(job.ConnectionId, job.JobType, job.LastErrorCode, raisedAt));
            })
            .GroupBy(item => (item.Job.TenantId, item.Key))
            .Select(group => group.First())
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
        // D1 of the review: at most DailyCap alerts per tenant and Istanbul day; the rest of the day's are counted in one summary.
        var toWrite = new List<(StoppedRow Job, JobFailureAlertPolicy.Kind Kind, string Key)>();
        foreach (var group in fresh.GroupBy(item => (item.Job.TenantId, Day: JobFailureAlertPolicy.DayOf(item.Key))))
        {
            var dayPrefix = $"{CatalogService.EscapeLike(JobFailureAlertPolicy.KeyPrefix + group.Key.Day)}:%";
            var summaryKey = JobFailureAlertPolicy.SummaryKey(group.Key.Day);
            var already = await db.PanelNotifications.AsNoTracking()
                .CountAsync(x => x.TenantId == group.Key.TenantId && x.UserId == null && EF.Functions.Like(x.ClientKey, dayPrefix, "\\") && x.ClientKey != summaryKey, cancellationToken);
            var room = Math.Max(0, JobFailureAlertPolicy.DailyCap - already);
            toWrite.AddRange(group.Take(room));
            var overflow = group.Skip(room).Select(item => item.Key).ToList();
            if (overflow.Count > 0) added += await SummariseAsync(db, group.Key.TenantId, summaryKey, overflow, utcNow, cancellationToken);
        }
        foreach (var (job, kind, key) in toWrite)
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

    // The day's summary alert ("… ve N iş daha"): the keys it already counts are kept in its payload, so a pass that sees the
    // same stopped jobs again does not count them twice. Returns 1 when the summary row was first written.
    private static async Task<int> SummariseAsync(AppDbContext db, Guid tenantId, string summaryKey, IReadOnlyList<string> overflowKeys, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var existing = await db.PanelNotifications.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ClientKey == summaryKey)
            .Select(x => x.Payload)
            .FirstOrDefaultAsync(cancellationToken);
        var counted = new HashSet<string>(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(existing))
        {
            try
            {
                using var document = JsonDocument.Parse(existing);
                if (document.RootElement.TryGetProperty("keys", out var keys) && keys.ValueKind == JsonValueKind.Array)
                    foreach (var item in keys.EnumerateArray()) if (item.GetString() is { } value) counted.Add(value);
            }
            catch (JsonException) { }
        }
        var before = counted.Count;
        foreach (var key in overflowKeys)
            if (counted.Count < JobFailureAlertPolicy.SummaryKeyLimit) counted.Add(key);
        if (counted.Count == before && existing is not null) return 0;
        var count = counted.Count;
        var message = JobFailureAlertPolicy.SummaryMessage(count);
        var payload = JsonSerializer.Serialize(new { operation = "İş Takibi", keys = counted.Order(StringComparer.Ordinal).ToArray() }, PayloadJson);
        var href = JobFailureAlertPolicy.SummaryHref;
        return await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO ops.panel_notifications AS current ("Id", "TenantId", "UserId", "ClientKey", "Severity", "Category", "Title", "Message",
                "Href", "ActionLabel", "PlatformCode", "StoreName", "Count", "Payload", "CreatedAt", "UpdatedAt", "ReadAt", "DismissedAt")
            VALUES ({Guid.CreateVersion7()}, {tenantId}, NULL, {summaryKey}, {PanelNotificationSeverities.Warning}, {JobFailureAlertPolicy.Category}, {JobFailureAlertPolicy.SummaryTitle}, {message},
                {href}, {JobFailureAlertPolicy.SummaryActionLabel}, NULL, NULL, {count}, {payload}, {now}, {now}, NULL, NULL)
            ON CONFLICT ("TenantId", "ClientKey") DO UPDATE SET "Message" = EXCLUDED."Message", "Count" = EXCLUDED."Count", "Payload" = EXCLUDED."Payload", "UpdatedAt" = EXCLUDED."UpdatedAt"
            WHERE current."UserId" IS NULL
            """, cancellationToken) > 0 && existing is null ? 1 : 0;
    }

    private sealed record StoppedRow(Guid Id, Guid TenantId, Guid? ConnectionId, string JobType, string Status, string? LastErrorCode, string? LastErrorSummary, DateTimeOffset CompletedAt);

    private static string? Clip(string? value, int maximum)
    {
        var text = value?.Trim();
        return string.IsNullOrEmpty(text) ? null : text.Length > maximum ? text[..maximum] : text;
    }

    private sealed record AlertSubject(string Kind, string Id, string Label, string Href);

    private sealed record AlertCard(string Id, string Message, string Kind, DateTimeOffset CreatedAt, bool Read, string Title, string Operation, string Href, string ActionLabel,
        string? PlatformCode, string? ConnectionId, string? StoreName, AlertSubject Subject, string GroupKey, bool Recorded, string Source, string? Code);
}
