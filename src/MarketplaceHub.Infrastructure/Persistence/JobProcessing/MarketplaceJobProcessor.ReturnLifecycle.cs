using System.Globalization;
using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.Trendyol;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace MarketplaceHub.Infrastructure.Persistence;

public sealed partial class MarketplaceJobProcessor
{
    // Hepsiburada has no single-claim read. Claims that are still open locally are followed up by
    // listing the statuses a claim moves on to, over the claim creation dates of those open claims,
    // so a decision taken on Hepsiburada's own panel reaches the panel even when the status-change
    // filter of the regular scan misses it. Only claims open locally are updated here.
    // PREAPPROVAL_CONFIRM_SUBMITTED is the local mark after "send for review": Hepsiburada has no single-claim
    // read, so only this follow-up moves such a claim on.
    internal static readonly string[] HepsiburadaOpenClaimStatuses = ["NewRequest", "AwaitingAction", "InDispute", "AwaitingPreApproval", "PREAPPROVAL_CONFIRM_SUBMITTED"];
    private static readonly string[] HepsiburadaClaimFollowUpStatuses = ["Accepted", "Refunded", "Rejected", "Cancelled", "InDispute", "AwaitingPreApproval", "AwaitingAction"];

    // The claims the open-return scan follows; the scheduler asks the same question before it queues a scan. Hepsiburada
    // follows its own open claim words. Elsewhere only claims still moving on the marketplace are read again (Requested,
    // AwaitingShipment, InTransit, ActionRequired, Disputed): an approved or rejected claim is settled, and a later change
    // of it still comes with the incremental return read, which lists claims by their last change.
    internal static IQueryable<ReturnClaim> ReturnLifecycleCandidates(IQueryable<ReturnClaim> claims, Guid tenantId, Guid connectionId, string? platformCode)
    {
        claims = claims.Where(claim => claim.TenantId == tenantId && claim.ConnectionId == connectionId);
        return string.Equals(platformCode, "HEPSIBURADA", StringComparison.OrdinalIgnoreCase)
            ? claims.Where(claim => claim.Status != ReturnClaimStatus.Completed && claim.Status != ReturnClaimStatus.Cancelled
                && HepsiburadaOpenClaimStatuses.Contains(claim.RawStatus))
            : string.Equals(platformCode, "TRENDYOL", StringComparison.OrdinalIgnoreCase)
                // A rejected Trendyol return stays open until its package back to the customer leaves: the package and
                // its tracking link may come without a new lastModifiedDate, so only a read by id sees them.
                ? claims.Where(claim => claim.Status == ReturnClaimStatus.Requested || claim.Status == ReturnClaimStatus.AwaitingShipment
                    || claim.Status == ReturnClaimStatus.InTransit || claim.Status == ReturnClaimStatus.ActionRequired || claim.Status == ReturnClaimStatus.Disputed
                    || claim.Status == ReturnClaimStatus.Rejected && (claim.RejectedPackageState == null || claim.RejectedPackageState == ReturnRejectedPackageStates.None || claim.RejectedPackageState == ReturnRejectedPackageStates.AwaitingCargo))
                : claims.Where(claim => claim.Status == ReturnClaimStatus.Requested || claim.Status == ReturnClaimStatus.AwaitingShipment
                    || claim.Status == ReturnClaimStatus.InTransit || claim.Status == ReturnClaimStatus.ActionRequired || claim.Status == ReturnClaimStatus.Disputed);
    }

    // How many Trendyol claims one run dates from their item audits, and how far back: a claim changed earlier than
    // that cannot have been decided today (a decision changes the claim's lastModifiedDate).
    internal const int ReturnDecisionDatesPerRun = 25;
    internal static readonly TimeSpan ReturnDecisionDateWindow = TimeSpan.FromHours(36);

    // The decision time of a Trendyol claim: the latest audit entry, over the claim's items, that moved an item to the
    // claim's current outcome (Accepted for approved, Rejected for rejected). None found: unknown (null).
    internal static DateTimeOffset? TrendyolDecisionTime(IEnumerable<RemoteReturnLineAudit> entries, ReturnClaimStatus outcome) =>
        entries.Where(entry => MarketplaceReturnStatus.Canonicalize(entry.NewStatus) == outcome)
            .Select(entry => (DateTimeOffset?)entry.At.ToUniversalTime())
            .Max();

    // Dates the connection's recently decided Trendyol claims from getClaimItemAudits (a read). A failed read leaves
    // the claim undated for the next run; it never fails the sync.
    private async Task DateTrendyolReturnDecisionsAsync(Guid tenantId, Guid connectionId, string correlationId, CancellationToken cancellationToken)
    {
        if (returns is not IReturnAuditPort audits) return;
        var since = timeProvider.GetUtcNow().Subtract(ReturnDecisionDateWindow);
        var claims = await db.ReturnClaims
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.DecidedAt == null && x.LastRemoteModifiedAt >= since
                && (x.Status == ReturnClaimStatus.Approved || x.Status == ReturnClaimStatus.Rejected))
            .OrderByDescending(x => x.LastRemoteModifiedAt)
            .Take(ReturnDecisionDatesPerRun)
            .ToListAsync(cancellationToken);
        foreach (var claim in claims)
        {
            var lineIds = await db.ReturnLines.AsNoTracking().Where(x => x.TenantId == tenantId && x.ClaimId == claim.Id)
                .OrderBy(x => x.Id).Select(x => x.ExternalLineId).Take(20).ToListAsync(cancellationToken);
            var entries = new List<RemoteReturnLineAudit>();
            foreach (var lineId in lineIds)
            {
                TrackRequest();
                var result = await audits.ReturnLineAuditsAsync(Context(tenantId, connectionId, correlationId, $"return-decision-audit:{lineId}"), lineId, cancellationToken);
                if (!result.IsSuccess) { TrackResultFailure(result.Error); continue; }
                entries.AddRange(result.Value!);
            }
            if (TrendyolDecisionTime(entries, claim.Status) is not { } decidedAt) continue;
            claim.DecidedAt = decidedAt;
            claim.UpdatedAt = timeProvider.GetUtcNow();
            claim.Version++;
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task<bool> SyncHepsiburadaOpenReturns(Guid tenantId, Guid connectionId, string correlationId, CancellationToken cancellationToken)
    {
        var cursor = await Cursor(tenantId, connectionId, "RETURN_LIFECYCLE", cancellationToken);
        var open = await ReturnLifecycleCandidates(db.ReturnClaims.AsNoTracking(), tenantId, connectionId, "HEPSIBURADA")
            .Select(claim => new { claim.ExternalClaimId, claim.RawStatus, claim.CreatedAt })
            .ToListAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        cursor.LastAttemptAt = now;
        if (open.Count == 0)
        {
            cursor.LastSuccessAt = now;
            cursor.Version++;
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }

        var pending = open.ToDictionary(claim => claim.ExternalClaimId, claim => claim.RawStatus, StringComparer.Ordinal);
        // The local record date can be days after the claim was opened on Hepsiburada; the whole history window is listed.
        var windowStart = now - HepsiburadaReturnHistoryPolicy.InitialLookback;
        var seen = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var status in HepsiburadaClaimFollowUpStatuses)
        {
            if (pending.Count == 0) break;
            var offset = 0;
            while (!cancellationToken.IsCancellationRequested && pending.Count > 0)
            {
                TrackRequest();
                var result = await returns.PollAsync(
                    Context(tenantId, connectionId, correlationId, $"hepsiburada-return-lifecycle:{status}:{offset}"),
                    new ReturnPollWindow(windowStart, now, Status: status),
                    new(offset.ToString(CultureInfo.InvariantCulture), 100),
                    cancellationToken);
                if (!result.IsSuccess)
                {
                    TrackResultFailure(result.Error);
                    throw JobProcessingException.FromAdapter(result.Error!);
                }
                var page = result.Value!;
                if (!seen.TryGetValue(status, out var seenInStatus)) seen[status] = seenInStatus = [];
                seenInStatus.AddRange(page.Items.Select(item => $"{item.ExternalClaimId}:{item.RawStatus}"));
                foreach (var claim in page.Items)
                {
                    TrackReceived();
                    if (!pending.TryGetValue(claim.ExternalClaimId, out var localStatus)) { TrackSkipped(); continue; }
                    // The same claim can be listed under its old status a moment longer; keep the local one.
                    if (string.Equals(status, localStatus, StringComparison.Ordinal)) { pending.Remove(claim.ExternalClaimId); continue; }
                    await UpsertReturn(tenantId, connectionId, claim, cancellationToken);
                    await db.SaveChangesAsync(cancellationToken);
                    pending.Remove(claim.ExternalClaimId);
                }
                if (!page.HasMore) break;
                offset = int.TryParse(page.NextCursor, NumberStyles.None, CultureInfo.InvariantCulture, out var next) ? Math.Max(offset + 1, next) : offset + Math.Max(1, page.Items.Count);
            }
        }
        // What Hepsiburada listed per status in this pass, kept for diagnosis; the follow-up itself keeps no cursor.
        cursor.OpaqueCursor = JsonSerializer.Serialize(new { at = now, open = open.Select(claim => claim.ExternalClaimId), seen, unresolved = pending.Keys });
        cursor.LastSuccessAt = timeProvider.GetUtcNow();
        cursor.LastError = null;
        cursor.LastErrorAt = null;
        cursor.ConsecutiveFailureCount = 0;
        cursor.Version++;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private async Task<bool> SyncHepsiburadaReturns(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, CancellationToken cancellationToken)
    {
        var cursor = await Cursor(tenantId, connectionId, "RETURNS", cancellationToken);
        var forceFull = ReadBoolean(payloadJson, "forceFull");
        var state = forceFull ? null : ReadHepsiburadaReturnSyncState(cursor.OpaqueCursor);
        if (state is null)
        {
            var now = timeProvider.GetUtcNow();
            var configuredOverlapSeconds = await db.ConnectionSyncPolicies.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == "RETURNS")
                .Select(x => (int?)x.OverlapSeconds)
                .SingleOrDefaultAsync(cancellationToken) ?? 900;
            var overlap = TimeSpan.FromSeconds(Math.Clamp(configuredOverlapSeconds, 60, 86_400));
            var retryAfterFailure = cursor.ConsecutiveFailureCount > 0 || !string.IsNullOrWhiteSpace(cursor.LastError);
            var full = forceFull || retryAfterFailure || HepsiburadaReturnHistoryPolicy.FullPassDue(cursor.LastSuccessAt, now);
            var lastSuccessfulChange = cursor.LastModifiedWatermark ?? cursor.LastSuccessAt ?? now.AddHours(-24);
            state = new(
                HepsiburadaReturnSyncStateVersion,
                0,
                0,
                full,
                full ? null : HepsiburadaReturnHistoryPolicy.IncrementalStatusChangeStart(lastSuccessfulChange, overlap, now),
                now);
            cursor.OpaqueCursor = JsonSerializer.Serialize(state);
            cursor.Version++;
            await db.SaveChangesAsync(cancellationToken);
        }
        while (!cancellationToken.IsCancellationRequested)
        {
            var status = HepsiburadaClaimStatuses[state.StatusIndex];
            TrackRequest();
            var result = await returns.PollAsync(
                Context(tenantId, connectionId, correlationId, $"hepsiburada-return-sync:{status}:{state.Offset}"),
                state.Full
                    ? new ReturnPollWindow(null, null, Status: status, StatusModifiedAfter: HepsiburadaReturnHistoryPolicy.InitialStatusChangeStart(state.AnchorEnd), StatusModifiedBefore: state.AnchorEnd)
                    : new ReturnPollWindow(null, null, Status: status, StatusModifiedAfter: state.StatusChangedAfter, StatusModifiedBefore: state.AnchorEnd),
                new(state.Offset.ToString(CultureInfo.InvariantCulture), 100),
                cancellationToken);
            if (!result.IsSuccess)
            {
                TrackResultFailure(result.Error);
                throw JobProcessingException.FromAdapter(result.Error!);
            }

            var pageResult = result.Value!;
            foreach (var issue in pageResult.Issues ?? [])
            {
                TrackSkipped();
                await RecordIssue(tenantId, $"hepsiburada-return:{connectionId}:{status}:{issue.Identity}", issue.Code, issue.Message, cancellationToken);
            }
            foreach (var claim in pageResult.Items)
            {
                TrackReceived();
                var orderCreatedAt = claim.OrderCreatedAt;
                var orderStored = false;
                if (orderCreatedAt is null || !HepsiburadaReturnHistoryPolicy.IsOrderWithinReturnHistory(orderCreatedAt, state.AnchorEnd))
                {
                    var storedOrderAt = await db.Orders.AsNoTracking()
                        .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId
                            && (x.ExternalOrderId == claim.ExternalOrderId || x.OrderNumber == claim.ExternalOrderId))
                        .Select(x => (DateTimeOffset?)x.OrderedAt)
                        .FirstOrDefaultAsync(cancellationToken);
                    orderStored = storedOrderAt is not null;
                    orderCreatedAt ??= storedOrderAt;
                }
                if (!HepsiburadaReturnHistoryPolicy.ImportsClaim(orderCreatedAt, state.AnchorEnd, orderStored))
                {
                    // An order older than the history the panel reads (or of unknown date) that the panel does not hold:
                    // the claim is named, never dropped silently, so a late return is seen.
                    TrackSkipped();
                    await RecordIssue(tenantId, $"hepsiburada-return-outside-history:{connectionId}:{Short(claim.ExternalClaimId, 120)}", "HEPSIBURADA_RETURN_ORDER_OUTSIDE_HISTORY",
                        Short($"Hepsiburada iade talebi {claim.ExternalClaimId} ({claim.ExternalOrderId} numaralı sipariş) panele alınmadı: sipariş panelde yok ve tarihi {(orderCreatedAt is null ? "bilinmiyor" : "son 30 günün dışında")}. Talebi Hepsiburada satıcı panelinden kontrol edin.", 1000)!, cancellationToken);
                    continue;
                }
                await ResolveIssue(tenantId, $"hepsiburada-return:{connectionId}:{status}:{claim.ExternalClaimId}", cancellationToken);
                if (orderStored) await ResolveIssue(tenantId, $"hepsiburada-return-outside-history:{connectionId}:{Short(claim.ExternalClaimId, 120)}", cancellationToken);
                await UpsertReturn(tenantId, connectionId, claim, cancellationToken);
            }

            if (pageResult.HasMore)
            {
                var nextOffset = int.TryParse(pageResult.NextCursor, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedOffset)
                    ? Math.Max(state.Offset + 1, parsedOffset)
                    : state.Offset + Math.Max(1, pageResult.Items.Count + (pageResult.Issues?.Count ?? 0));
                state = state with { Offset = nextOffset };
            }
            else if (state.StatusIndex + 1 < HepsiburadaClaimStatuses.Length)
            {
                state = state with { StatusIndex = state.StatusIndex + 1, Offset = 0 };
            }
            else
            {
                cursor.OpaqueCursor = null;
                cursor.LastModifiedWatermark = state.AnchorEnd;
                cursor.LastSuccessAt = timeProvider.GetUtcNow();
                cursor.LastError = null;
                cursor.LastErrorAt = null;
                cursor.ConsecutiveFailureCount = 0;
                cursor.Version++;
                await db.SaveChangesAsync(cancellationToken);
                break;
            }

            cursor.OpaqueCursor = JsonSerializer.Serialize(state);
            cursor.Version++;
            await db.SaveChangesAsync(cancellationToken);
        }
        return true;
    }

    private static HepsiburadaReturnSyncState? ReadHepsiburadaReturnSyncState(string? opaqueCursor)
    {
        if (!string.IsNullOrWhiteSpace(opaqueCursor))
        {
            try
            {
                var state = JsonSerializer.Deserialize<HepsiburadaReturnSyncState>(opaqueCursor);
                if (state is { Version: HepsiburadaReturnSyncStateVersion, StatusIndex: >= 0, Offset: >= 0 }
                    && state.StatusIndex < HepsiburadaClaimStatuses.Length
                    && state.AnchorEnd != default
                    && (state.Full || state.StatusChangedAfter is not null))
                    return state;
            }
            catch (JsonException) { }
        }
        return null;
    }

    private async Task<bool> SyncOpenReturns(Guid tenantId, Guid connectionId, string correlationId, CancellationToken cancellationToken)
    {
        var platformCode = await db.PlatformConnections.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.Id == connectionId)
            .Select(x => x.PlatformCode)
            .SingleOrDefaultAsync(cancellationToken);
        if (string.Equals(platformCode, "HEPSIBURADA", StringComparison.Ordinal))
            return await SyncHepsiburadaOpenReturns(tenantId, connectionId, correlationId, cancellationToken);
        if (string.Equals(platformCode, "TRENDYOL", StringComparison.OrdinalIgnoreCase))
            await DateTrendyolReturnRequestsAsync(tenantId, connectionId, correlationId, cancellationToken);
        var batchSize = configuration.GetValue("MarketplaceSync:ReturnLifecycle:BatchSize", 25);
        var (incompleteCargoBudget, completeCargoBudget) = ReturnLifecycleBatchPolicy.SplitBatch(batchSize);
        var incompleteCargoCursor = await Cursor(tenantId, connectionId, "RETURN_LIFECYCLE_CARGO_MISSING", cancellationToken);
        var completeCargoCursor = await Cursor(tenantId, connectionId, "RETURN_LIFECYCLE_CARGO_COMPLETE", cancellationToken);
        var incompleteCargoClaims = await ReadReturnLifecycleBatchAsync(tenantId, connectionId, platformCode, incompleteCargoCursor, missingCargo: true, incompleteCargoBudget, cancellationToken);
        var completeCargoClaims = await ReadReturnLifecycleBatchAsync(tenantId, connectionId, platformCode, completeCargoCursor, missingCargo: false, completeCargoBudget, cancellationToken);
        var openClaims = incompleteCargoClaims.Select(claim => (claim.Id, claim.ExternalClaimId, Cursor: incompleteCargoCursor))
            .Concat(completeCargoClaims.Select(claim => (claim.Id, claim.ExternalClaimId, Cursor: completeCargoCursor)))
            .ToArray();
        if (openClaims.Length == 0) return true;

        // Trendyol answers several claims in one request (claimIds, comma separated). A claim the batch did not bring
        // is asked for on its own below, as before; a batch the marketplace refuses as a request falls back to that too.
        var prefetched = new Dictionary<string, RemoteReturnClaim>(StringComparer.OrdinalIgnoreCase);
        if (string.Equals(platformCode, "TRENDYOL", StringComparison.OrdinalIgnoreCase) && returns is IReturnBatchReadPort batchReader)
        {
            foreach (var ids in ReturnLifecycleBatchPolicy.ClaimIdRequests(openClaims.Select(claim => claim.ExternalClaimId)))
            {
                TrackRequest();
                var many = await batchReader.GetManyAsync(Context(tenantId, connectionId, correlationId, $"return-lifecycle-batch:{ids[0]}:{ids.Length}"), ids, cancellationToken);
                if (!many.IsSuccess)
                {
                    TrackResultFailure(many.Error);
                    if (many.Error?.Class is AdapterErrorClass.Validation or AdapterErrorClass.ContractViolation) break;
                    throw JobProcessingException.FromAdapter(many.Error!);
                }
                foreach (var claim in many.Value!) prefetched.TryAdd(claim.ExternalClaimId, claim);
            }
        }

        foreach (var (claimId, externalClaimId, cursor) in openClaims)
        {
            cursor.OpaqueCursor = claimId.ToString("D");
            cursor.LastAttemptAt = timeProvider.GetUtcNow();
            cursor.Version++;
            await db.SaveChangesAsync(cancellationToken);
            AdapterResult<RemoteReturnClaim> result;
            if (prefetched.TryGetValue(externalClaimId, out var batched)) result = AdapterResult<RemoteReturnClaim>.Success(batched);
            else
            {
                TrackRequest();
                result = await returns.GetAsync(
                    Context(tenantId, connectionId, correlationId, $"return-lifecycle:{externalClaimId}"),
                    externalClaimId,
                    cancellationToken);
            }
            if (!result.IsSuccess)
            {
                if (result.Error?.Class == AdapterErrorClass.NotFound)
                {
                    await RecordIssue(tenantId, $"return-lifecycle:{connectionId}:{externalClaimId}", "REMOTE_RETURN_NOT_FOUND", "Açık iade kaydı Trendyol'da ClaimId ile bulunamadı; yerel durum korunarak incelemeye alındı.", cancellationToken);
                    await db.SaveChangesAsync(cancellationToken);
                    continue;
                }
                TrackResultFailure(result.Error);
                throw JobProcessingException.FromAdapter(result.Error!);
            }
            TrackReceived();

            await UpsertReturn(tenantId, connectionId, result.Value!, cancellationToken);
            await ResolveIssue(tenantId, $"return-lifecycle:{connectionId}:{externalClaimId}", cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }
        incompleteCargoCursor.LastSuccessAt = timeProvider.GetUtcNow();
        completeCargoCursor.LastSuccessAt = incompleteCargoCursor.LastSuccessAt;
        await db.SaveChangesAsync(cancellationToken);
        if (string.Equals(platformCode, "TRENDYOL", StringComparison.OrdinalIgnoreCase))
            await DateTrendyolReturnDecisionsAsync(tenantId, connectionId, correlationId, cancellationToken);
        return true;
    }

    // Claims stored before ReturnClaim.RequestedAt existed (11 Nov 2026) are dated from Trendyol: one getClaims request by
    // claimIds per run for the next claims without a request date (cursor RETURN_REQUESTED_AT, by claim id, wrapping),
    // so a claim Trendyol does not return or does not date never holds the others back. Nothing is asked once every
    // claim is dated. A refusal stops this step only; the lifecycle read goes on.
    internal const int ReturnRequestDatesPerRun = ReturnLifecycleBatchPolicy.ClaimIdsPerRequest;

    private async Task DateTrendyolReturnRequestsAsync(Guid tenantId, Guid connectionId, string correlationId, CancellationToken cancellationToken)
    {
        if (returns is not IReturnBatchReadPort batchReader) return;
        var undated = db.ReturnClaims.AsNoTracking().Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.RequestedAt == null);
        if (!await undated.AnyAsync(cancellationToken)) return;
        var cursor = await Cursor(tenantId, connectionId, "RETURN_REQUESTED_AT", cancellationToken);
        var after = Guid.TryParse(cursor.OpaqueCursor, out var cursorId) ? undated.Where(x => x.Id.CompareTo(cursorId) > 0) : undated;
        var batch = await after.OrderBy(x => x.Id).Select(x => new { x.Id, x.ExternalClaimId }).Take(ReturnRequestDatesPerRun).ToListAsync(cancellationToken);
        if (batch.Count == 0)
            batch = await undated.OrderBy(x => x.Id).Select(x => new { x.Id, x.ExternalClaimId }).Take(ReturnRequestDatesPerRun).ToListAsync(cancellationToken);
        if (batch.Count == 0) return;
        cursor.OpaqueCursor = batch[^1].Id.ToString("D");
        cursor.LastAttemptAt = timeProvider.GetUtcNow();
        cursor.Version++;
        await db.SaveChangesAsync(cancellationToken);
        var ids = batch.Select(x => x.ExternalClaimId).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Length == 0) return;
        TrackRequest();
        var many = await batchReader.GetManyAsync(Context(tenantId, connectionId, correlationId, $"return-requested-at:{ids[0]}:{ids.Length}"), ids, cancellationToken);
        if (!many.IsSuccess) { TrackResultFailure(many.Error); return; }
        foreach (var claim in many.Value!)
        {
            TrackReceived();
            await UpsertReturn(tenantId, connectionId, claim, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }
        cursor.LastSuccessAt = timeProvider.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<(Guid Id, string ExternalClaimId)>> ReadReturnLifecycleBatchAsync(
        Guid tenantId,
        Guid connectionId,
        string? platformCode,
        SyncCursor cursor,
        bool missingCargo,
        int batchSize,
        CancellationToken cancellationToken)
    {
        var claims = ReturnLifecycleCandidates(db.ReturnClaims.AsNoTracking(), tenantId, connectionId, platformCode);
        claims = missingCargo
            ? claims.Where(claim => claim.CargoProviderName == null || claim.CargoProviderName == "" || claim.CargoTrackingNumber == null || claim.CargoTrackingNumber == "")
            : claims.Where(claim => claim.CargoProviderName != null && claim.CargoProviderName != "" && claim.CargoTrackingNumber != null && claim.CargoTrackingNumber != "");

        var hasCursor = Guid.TryParse(cursor.OpaqueCursor, out var cursorId);
        var afterCursor = claims;
        if (hasCursor) afterCursor = afterCursor.Where(claim => claim.Id.CompareTo(cursorId) > 0);
        var page = await afterCursor.OrderBy(claim => claim.Id)
            .Select(claim => new ValueTuple<Guid, string>(claim.Id, claim.ExternalClaimId))
            .Take(batchSize)
            .ToListAsync(cancellationToken);

        if (!hasCursor || page.Count >= batchSize) return page.Select(item => (item.Item1, item.Item2)).ToArray();
        var wrapped = await claims.Where(claim => claim.Id.CompareTo(cursorId) <= 0)
            .OrderBy(claim => claim.Id)
            .Select(claim => new ValueTuple<Guid, string>(claim.Id, claim.ExternalClaimId))
            .Take(batchSize - page.Count)
            .ToListAsync(cancellationToken);
        return page.Concat(wrapped).Select(item => (item.Item1, item.Item2)).ToArray();
    }

    // The page a return reconciliation reads next: the adapter's own cursor when it gives one (Trendyol's per-bucket
    // "s:Accepted=2", which asks only the buckets that still have claims), else the next page number. Before 6 Nov 2026
    // a cursor that was not a number became "page + 1" for every bucket, so finished buckets were asked for pages past
    // their end, seven needless requests a page and a contract error when such a page came back without its list.
    internal static string NextReconcilePage(string current, string? next) =>
        !string.IsNullOrWhiteSpace(next) ? next
        : int.TryParse(current, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number) ? (number + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)
        : "1";

    private async Task<bool> ReconcileReturns(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, CancellationToken cancellationToken)
    {
        var lookbackDays = ReadBoundedInt(payloadJson, "lookbackDays", 3, 1, 90);
        var end = timeProvider.GetUtcNow();
        foreach (var storefront in TrendyolReadStorefronts.ReturnCodes)
        {
            var page = "0";
            var step = 0;
            do
            {
                TrackRequest();
                var result = await returns.PollAsync(Context(tenantId, connectionId, correlationId, $"return-reconcile:{lookbackDays}:{storefront}:{step}"), new(end.AddDays(-lookbackDays), end, storefront), new(page, 200), cancellationToken);
                if (!result.IsSuccess) { TrackResultFailure(result.Error); throw JobProcessingException.FromAdapter(result.Error!); }
                foreach (var _ in result.Value!.Items) TrackReceived();
                foreach (var claim in result.Value!.Items) await UpsertReturn(tenantId, connectionId, claim, cancellationToken);
                if (!result.Value.HasMore) break;
                var next = NextReconcilePage(page, result.Value.NextCursor);
                // ISLEYICI-11: a claim list that names the page just read as the next one, or never ends, stops the
                // reconciliation with its own code instead of being read until the lease runs out.
                if (string.Equals(next, page, StringComparison.Ordinal))
                    throw PagedReadLoop.GuardFailure("TRENDYOL_RETURN_RECONCILE_PAGE_REPEATED", $"Trendyol {storefront} iade listesi okunan sayfayı yeniden sonraki sayfa olarak verdi; iade uzlaştırması durduruldu.");
                if (step + 1 >= TrendyolReturnPageCap)
                    throw PagedReadLoop.GuardFailure("TRENDYOL_RETURN_RECONCILE_PAGE_CAP", $"Trendyol {storefront} iade listesi {TrendyolReturnPageCap} sayfayı aştı; iade uzlaştırması durduruldu.");
                page = next;
                step++;
            } while (!cancellationToken.IsCancellationRequested);
        }
        return true;
    }
}
