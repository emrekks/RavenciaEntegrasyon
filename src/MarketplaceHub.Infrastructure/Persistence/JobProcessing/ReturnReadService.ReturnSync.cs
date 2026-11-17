using System.Globalization;
using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.Pazarama;
using MarketplaceHub.Infrastructure.Adapters.Trendyol;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

internal sealed partial class ReturnReadService
{
    // Trendyol's claims feed is date-bounded for the initial scan. Invalidating
    // the previous all-time cursor makes the next read start at three months.
    private const string ShopifyReturnSyncStateVersion = "shopify-returns-v1";
    // Pages counts the pages read with the current query (Cursor), for the restart before Shopify's 25,000-result cap.
    internal sealed record ShopifyReturnSyncState(string Version, string? Cursor, DateTimeOffset? ModifiedAfter, DateTimeOffset Anchor, int Pages = 0);
    internal const int ShopifyReturnInitialLookbackDays = 180;
    // ISLEYICI-9 round 10 (16 Nov 2026): pages of 100 orders in one run (the query restarts every 200 pages): 500,000
    // orders, as Shopify's order read (ShopifyOrderPageCap), far above a 180-day read of a real store; guards against a read
    // that never ends.
    internal const int ShopifyReturnPageCap = 5_000;
    private static readonly PageGuard ShopifyReturnPageGuard = new(MaxPages: ShopifyReturnPageCap);
    internal static readonly TimeSpan ShopifyReturnOverlap = TimeSpan.FromMinutes(15);

    // Where a new Shopify return read starts: 180 days back on the first read or when asked for a full read, otherwise
    // 15 minutes before the last completed read began. A failed run never widens the read: the watermark moves only when
    // a whole run completes, so the next run still covers everything the failed one missed (the failure counters on the
    // RETURNS cursor are the job's telemetry, and one throttled page used to turn every next run into a 180-day read).
    internal static DateTimeOffset ShopifyReturnReadStart(bool forceFull, DateTimeOffset? lastSuccessAt, DateTimeOffset? lastModifiedWatermark, DateTimeOffset now) =>
        forceFull || lastSuccessAt is null || (lastModifiedWatermark ?? lastSuccessAt) is not { } watermark
            ? now.AddDays(-ShopifyReturnInitialLookbackDays)
            : watermark - ShopifyReturnOverlap;

    // The orders are sorted by updated_at, oldest first, and one query pages through at most 25,000 of them; near that cap
    // (200 pages, as for orders) the read starts a new query from the latest order change it has seen. Orders read twice
    // are harmless (claims are upserted). Without a later change time the query keeps paging.
    internal static ShopifyReturnSyncState NextShopifyReturnPage(ShopifyReturnSyncState state, string nextCursor, DateTimeOffset? lastModifiedSeen) =>
        state.Pages + 1 >= ShopifyOrderHistoryPolicy.PagesBeforeRestart && lastModifiedSeen is { } seen && (state.ModifiedAfter is not { } from || seen > from)
            ? state with { ModifiedAfter = seen, Cursor = null, Pages = 0 }
            : state with { Cursor = nextCursor, Pages = state.Pages + 1 };
    internal const string ReturnSyncStateVersion = "returns-v10";
    // PageCursor is the adapter's NextCursor kept as it came (Trendyol: "s:Created=2,Accepted=5", one next page per
    // claim status bucket that still has claims). Page counts the pages read and is the request cursor only when
    // PageCursor is empty (first page, or a state written before PageCursor existed), so old states keep working.
    // PermanentFailures counts the permanent refusals (4xx) of the saved page in a row.
    internal sealed record ReturnSyncState(string Version, int StoreFrontIndex, int Page, bool Full = true, DateTimeOffset? StartAt = null, DateTimeOffset? EndAt = null, string? PageCursor = null, int PermanentFailures = 0);
    internal const int MaximumReturnPageCursorLength = 1_000;
    internal const int ReturnPagePermanentFailureLimit = 3;

    // A permanent refusal of a saved page (400/404/422, or a validation/not-found error) is counted; a quota,
    // sign-in or server error is not. The read restarts from its baseline at the limit.
    internal static (int Failures, bool Restart) ReturnPageRefusal(ReturnSyncState state, AdapterError? error)
    {
        var permanent = error is not null
            && (error.Class is AdapterErrorClass.Validation or AdapterErrorClass.NotFound || error.HttpStatus is 400 or 404 or 422)
            && error.Class is not (AdapterErrorClass.RateLimit or AdapterErrorClass.Authentication or AdapterErrorClass.Remote5xx or AdapterErrorClass.TransientNetwork);
        if (!permanent) return (state.PermanentFailures, false);
        var failures = state.PermanentFailures + 1;
        return (failures, failures >= ReturnPagePermanentFailureLimit);
    }

    internal static string ReturnPageCursor(ReturnSyncState state) =>
        string.IsNullOrWhiteSpace(state.PageCursor) ? state.Page.ToString(System.Globalization.CultureInfo.InvariantCulture) : state.PageCursor;

    // A numeric NextCursor is a plain page number; any other non-empty value is opaque and is sent back unchanged.
    // Without a usable NextCursor the next page number is the only way on.
    internal static ReturnSyncState NextReturnPage(ReturnSyncState state, string? nextCursor)
    {
        if (int.TryParse(nextCursor, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var page))
            return state with { Page = page, PageCursor = null };
        if (!string.IsNullOrWhiteSpace(nextCursor) && nextCursor.Length <= MaximumReturnPageCursorLength)
            return state with { Page = state.Page + 1, PageCursor = nextCursor };
        return state with { Page = state.Page + 1, PageCursor = null };
    }
    private const string HepsiburadaReturnSyncStateVersion = "hepsiburada-returns-v2";
    private static readonly string[] HepsiburadaClaimStatuses = ["NewRequest", "AwaitingAction", "InDispute", "Accepted", "Rejected", "Refunded", "Cancelled", "AwaitingPreApproval"];
    private sealed record HepsiburadaReturnSyncState(string Version, int StatusIndex, int Offset, bool Full, DateTimeOffset? StatusChangedAfter, DateTimeOffset AnchorEnd);

    internal async Task<bool> SyncReturns(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, CancellationToken cancellationToken)
    {
        var platformCode = await ConnectionPlatformCode(tenantId, connectionId, cancellationToken);
        if (string.Equals(platformCode, "SHOPIFY", StringComparison.OrdinalIgnoreCase))
            return await SyncShopifyReturns(tenantId, connectionId, payloadJson, correlationId, cancellationToken);
        if (string.Equals(platformCode, "HEPSIBURADA", StringComparison.OrdinalIgnoreCase))
            return await SyncHepsiburadaReturns(tenantId, connectionId, payloadJson, correlationId, cancellationToken);
        if (string.Equals(platformCode, "N11", StringComparison.OrdinalIgnoreCase))
            return await SyncN11Returns(tenantId, connectionId, payloadJson, correlationId, cancellationToken);
        if (string.Equals(platformCode, "PAZARAMA", StringComparison.OrdinalIgnoreCase))
            return await SyncPazaramaReturns(tenantId, connectionId, payloadJson, correlationId, cancellationToken);
        if (string.Equals(platformCode, "PTTAVM", StringComparison.OrdinalIgnoreCase))
            return await SyncPttAvmReturns(tenantId, connectionId, payloadJson, correlationId, cancellationToken);

        var cursor = await Cursor(tenantId, connectionId, "RETURNS", cancellationToken);
        var configuredOverlapSeconds = await db.ConnectionSyncPolicies.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == "RETURNS")
            .Select(x => (int?)x.OverlapSeconds)
            .SingleOrDefaultAsync(cancellationToken) ?? 900;
        var overlap = TimeSpan.FromSeconds(Math.Clamp(configuredOverlapSeconds, 60, 86_400));
        // A failed run resumes its saved state: the watermark moves only when a whole run completes, and the
        // saved window and page say where the failed run stopped, so nothing is skipped and nothing is read
        // twice. (The run used to restart from the three-month baseline after any failure; the failure count
        // it looked at is the telemetry of the same "RETURNS" row, so one quota error meant a full re-read.)
        var state = ReadReturnSyncState(cursor, timeProvider.GetUtcNow(), overlap, ReadBoolean(payloadJson, "forceFull"));
        var pagesThisSlice = 0;
        do
        {
            var storefront = TrendyolReadStorefronts.ReturnCodes[state.StoreFrontIndex];
            // getClaims startDate/endDate are claim-creation filters, while the
            // local cursor watermark is lastModifiedDate. Reusing that watermark
            // as startDate can silently skip claims, so each read starts from page 0
            // and remains idempotent at the local claim key.
            var window = state.Full || state.StartAt is null || state.EndAt is null
                ? new ReturnPollWindow(null, null, storefront)
                : new ReturnPollWindow(state.StartAt, state.EndAt, storefront);
            TrackRequest();
            var result = await returns.PollAsync(Context(tenantId, connectionId, correlationId, $"return-sync:{storefront}:{state.Page}"), window, new(ReturnPageCursor(state), 200), cancellationToken);
            if (!result.IsSuccess && state.StoreFrontIndex > 0 && state.Page == 0 && state.PageCursor is null && result.Error?.HttpStatus is 400 or 404)
            {
                if (state.StoreFrontIndex + 1 < TrendyolReadStorefronts.ReturnCodes.Length)
                {
                    state = state with { StoreFrontIndex = state.StoreFrontIndex + 1, Page = 0, PageCursor = null };
                    cursor.OpaqueCursor = SerializeReturnSyncState(state);
                    cursor.Version++;
                    await db.SaveChangesAsync(cancellationToken);
                    continue;
                }
                cursor.OpaqueCursor = null;
                cursor.LastModifiedWatermark = timeProvider.GetUtcNow();
                cursor.Version++;
                await db.SaveChangesAsync(cancellationToken);
                break;
            }
            if (!result.IsSuccess)
            {
                TrackResultFailure(result.Error);
                // A saved page the marketplace keeps refusing for good (its cursor no longer valid) would hold the
                // read there forever: after a few such refusals in a row the read starts again from its baseline.
                if (state.Page > 0 || state.PageCursor is not null)
                {
                    var (failures, restart) = ReturnPageRefusal(state, result.Error);
                    if (failures != state.PermanentFailures)
                    {
                        cursor.OpaqueCursor = restart ? null : SerializeReturnSyncState(state with { PermanentFailures = failures });
                        cursor.Version++;
                        await db.SaveChangesAsync(cancellationToken);
                    }
                }
                throw JobProcessingException.FromAdapter(result.Error!);
            }
            foreach (var _ in result.Value!.Items) TrackReceived();
            foreach (var claim in result.Value!.Items) await UpsertReturn(tenantId, connectionId, claim, cancellationToken);
            pagesThisSlice++;
            state = state with { PermanentFailures = 0 };
            var nextStorefront = false;
            if (result.Value.HasMore)
            {
                var next = NextReturnPage(state, result.Value.NextCursor);
                // ISLEYICI-11: the read goes on in slices from its saved page, so a list that names the page just read as
                // the next one, or never ends, was read slice after slice for good. It stops with its own code at the
                // page saved before; the watermark does not move.
                if (ReturnPageRepeated(state, next))
                    throw PagedReadLoop.GuardFailure("TRENDYOL_RETURN_PAGE_REPEATED", $"Trendyol {storefront} iade listesi okunan sayfayı yeniden sonraki sayfa olarak verdi; okuma durduruldu ve bu dönem okunmuş sayılmadı.");
                if (next.Page >= TrendyolReturnPageCap)
                    throw PagedReadLoop.GuardFailure("TRENDYOL_RETURN_PAGE_CAP", $"Trendyol {storefront} iade listesi {TrendyolReturnPageCap} sayfayı aştı; okuma kaldığı sayfada durduruldu ve bu dönem okunmuş sayılmadı.");
                state = next;
            }
            else if (state.StoreFrontIndex + 1 < TrendyolReadStorefronts.ReturnCodes.Length)
            {
                state = state with { StoreFrontIndex = state.StoreFrontIndex + 1, Page = 0, PageCursor = null };
                nextStorefront = true;
            }
            else
            {
                cursor.OpaqueCursor = null;
                // The window's end (when the run started) is the next run's start, so claims changed while
                // this run read are not skipped.
                cursor.LastModifiedWatermark = state.EndAt ?? timeProvider.GetUtcNow();
                cursor.Version++;
                await db.SaveChangesAsync(cancellationToken);
                break;
            }
            cursor.OpaqueCursor = SerializeReturnSyncState(state);
            cursor.Version++;
            await db.SaveChangesAsync(cancellationToken);
            // The read hands its lock and slot back between storefronts and every few pages; the saved state
            // continues it right after, so a long baseline no longer holds the returns lane in one piece.
            if (ReturnSyncSliceEnds(nextStorefront, pagesThisSlice))
                throw new JobProcessingException(JobExecutionResult.Continue("İade okuması kaldığı sayfadan birkaç saniye içinde devam edecek."));
        } while (!cancellationToken.IsCancellationRequested);
        await DateTrendyolReturnDecisionsAsync(tenantId, connectionId, correlationId, cancellationToken);
        return true;
    }

    internal const int ReturnSyncPagesPerSlice = 5;

    // ISLEYICI-11 (16 Nov 2026): Trendyol claim pages of 200 per storefront read (400,000 claims; a full read has no date
    // window), a guard against a list that never ends. The return reconciliation uses the same cap.
    internal const int TrendyolReturnPageCap = 2_000;

    // The next page is the one just read: the same page number without a cursor, or the same opaque cursor again.
    internal static bool ReturnPageRepeated(ReturnSyncState current, ReturnSyncState next) =>
        next.PageCursor is null ? current.PageCursor is null && next.Page == current.Page
            : string.Equals(next.PageCursor, current.PageCursor, StringComparison.Ordinal);

    internal static bool ReturnSyncSliceEnds(bool movedToNextStorefront, int pagesThisSlice) =>
        movedToNextStorefront || pagesThisSlice >= ReturnSyncPagesPerSlice;

    private async Task<bool> SyncShopifyReturns(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, CancellationToken cancellationToken)
    {
        var cursor = await Cursor(tenantId, connectionId, "RETURNS", cancellationToken);
        var now = timeProvider.GetUtcNow();
        var forceFull = ReadBoolean(payloadJson, "forceFull");
        // A run stopped between pages resumes its saved query, so the page cursor is always sent with the query it came from.
        var state = (forceFull ? null : ReadShopifyReturnSyncState(cursor.OpaqueCursor))
            ?? new(ShopifyReturnSyncStateVersion, null, ShopifyReturnReadStart(forceFull, cursor.LastSuccessAt, cursor.LastModifiedWatermark, now), now);
        var walk = new PageWalk(ShopifyReturnPageGuard);
        while (!cancellationToken.IsCancellationRequested)
        {
            TrackRequest();
            var page = await returns.PollAsync(Context(tenantId, connectionId, correlationId, $"shopify-return-sync:{state.Cursor ?? "first"}"), new(state.ModifiedAfter, state.Anchor), new(state.Cursor, 100), cancellationToken);
            if (!page.IsSuccess) { TrackResultFailure(page.Error); throw JobProcessingException.FromAdapter(page.Error!); }
            foreach (var _ in page.Value!.Items) TrackReceived();
            foreach (var claim in page.Value.Items) await UpsertReturn(tenantId, connectionId, claim, cancellationToken);
            if (page.Value.HasMore)
            {
                if (string.IsNullOrWhiteSpace(page.Value.NextCursor) || string.Equals(page.Value.NextCursor, state.Cursor, StringComparison.Ordinal))
                    throw JobProcessingException.FromAdapter(new AdapterError(AdapterErrorClass.ContractViolation, "SHOPIFY_RETURN_CURSOR_INVALID", "Shopify iade akışı geçersiz veya yinelenen sayfa imleci döndürdü.", 502, null, null));
                // ISLEYICI-9 round 10: a read that never ends stops here with its own code (a repeated cursor stops above). The
                // position saved after the previous page stays, the watermark does not move, and the next run resumes there.
                if (walk.After(state.Cursor, page.Value.NextCursor) == PagedReadStop.PageCap)
                    throw PagedReadLoop.GuardFailure("SHOPIFY_RETURN_PAGE_CAP", $"Shopify iade okuması bir okumada {ShopifyReturnPageCap} sayfayı aştı; okuma kaldığı yerde durduruldu ve bu dönem okunmuş sayılmadı.");
                state = NextShopifyReturnPage(state, page.Value.NextCursor, page.Value.LastModifiedSeen);
                ResumableCursor.Save(cursor, state);
                await db.SaveChangesAsync(cancellationToken);
                continue;
            }
            cursor.OpaqueCursor = null;
            cursor.LastModifiedWatermark = state.Anchor;
            cursor.LastSuccessAt = timeProvider.GetUtcNow();
            cursor.LastError = null;
            cursor.LastErrorAt = null;
            cursor.ConsecutiveFailureCount = 0;
            cursor.Version++;
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        // KUYRUK-9 (16 Nov 2026): a run stopped between pages names its stop instead of the generic F3_JOB_REJECTED; it ends
        // as it did (blocked), the page saved before stays and the watermark does not move.
        throw new JobProcessingException(JobExecutionResult.Blocked("SHOPIFY_RETURN_READ_CANCELLED", "Shopify iade okuması tamamlanmadan durduruldu; okunmayan sayfalar okunmuş sayılmadı ve sonraki okuma kaldığı sayfadan devam edecek."));
    }

    // n11 lists claims by request date only, so a 45-day request window is re-read (every 30 minutes, see ReturnRereadPolicy) to
    // pick up later status changes (approval, denial, refund) of recent claims.
    private const int PazaramaReturnLookbackDays = 45;
    private const int PazaramaReturnInitialLookbackDays = 90;
    private const int PazaramaReturnWindowDays = 28;

    // Pazarama lists refunds by request date in windows of at most a month. Every refund names the order
    // (orderNumber) and the product (productCode = barcode); the order line is found among the stored lines.
    private async Task<bool> SyncPazaramaReturns(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, CancellationToken cancellationToken)
    {
        var cursor = await Cursor(tenantId, connectionId, "RETURNS", cancellationToken);
        var now = timeProvider.GetUtcNow();
        var full = ReadBoolean(payloadJson, "forceFull") || cursor.LastSuccessAt is null;
        var wideCursor = await Cursor(tenantId, connectionId, ReturnRereadPolicy.WideCursorResource, cancellationToken);
        var lookbackDays = ReturnRereadPolicy.LookbackDays(full, PazaramaReturnInitialLookbackDays, PazaramaReturnLookbackDays, wideCursor.LastSuccessAt, now);
        var from = now.AddDays(-lookbackDays);
        for (var windowStart = from; windowStart < now && !cancellationToken.IsCancellationRequested; windowStart = windowStart.AddDays(PazaramaReturnWindowDays))
        {
            var windowEnd = windowStart.AddDays(PazaramaReturnWindowDays) < now ? windowStart.AddDays(PazaramaReturnWindowDays) : now;
            var window = new ReturnPollWindow(windowStart, windowEnd);
            var walk = await PagedReadLoop.RunAsync("1", ReturnPageGuard, async (cursor, _) =>
            {
                var page = int.Parse(cursor!, NumberStyles.None, CultureInfo.InvariantCulture);
                TrackRequest();
                var result = await returns.PollAsync(Context(tenantId, connectionId, correlationId, $"pazarama-return-sync:{windowStart:yyyyMMdd}:{page}"), window, new(page.ToString(CultureInfo.InvariantCulture), 100), cancellationToken);
                if (!result.IsSuccess) { TrackResultFailure(result.Error); throw JobProcessingException.FromAdapter(result.Error!); }
                await RecordReturnPageIssuesAsync(tenantId, connectionId, "pazarama-return", result.Value!.Issues, cancellationToken);
                foreach (var claim in result.Value!.Items)
                {
                    TrackReceived();
                    var resolved = await ResolvePazaramaReturnOrderLinesAsync(tenantId, connectionId, correlationId, claim, cancellationToken);
                    await UpsertReturn(tenantId, connectionId, resolved, cancellationToken);
                }
                return new PageStep(result.Value.HasMore ? NextReturnPageNumber(page, result.Value.NextCursor) : null, ReturnPageKey(result.Value.Items));
            }, cancellationToken);
            ThrowAtReturnPageGuard(walk, "Pazarama");
        }
        cancellationToken.ThrowIfCancellationRequested();
        NoteWideReturnRead(wideCursor, lookbackDays, PazaramaReturnLookbackDays, now);
        cursor.OpaqueCursor = null;
        cursor.LastModifiedWatermark = now;
        cursor.LastSuccessAt = timeProvider.GetUtcNow();
        cursor.Version++;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    // J3 (15 Nov 2026): n11's and Pazarama's numbered claim pages had no stop but HasMore: a list that kept saying "more"
    // (or ignored the page number) was walked until the lease ran out. Now at most ReturnPageCap pages per window (100
    // claims a page: far above real traffic), and a page that repeats the claims of the one before ends the walk; either
    // stops the job with its code before the watermark moves, so the window is read again.
    internal const int ReturnPageCap = 500;
    private static readonly PageGuard ReturnPageGuard = new(MaxPages: ReturnPageCap, PageRepeated: PazaramaOrderPageRepeated);

    // The next page number: the one the marketplace names when it moves forward, else the next one.
    internal static string NextReturnPageNumber(int page, string? nextCursor) =>
        (int.TryParse(nextCursor, NumberStyles.None, CultureInfo.InvariantCulture, out var next) && next > page ? next : page + 1).ToString(CultureInfo.InvariantCulture);

    // The claims a page holds, for the repeated-page guard (null: an empty page, never a repeat).
    internal static string? ReturnPageKey(IReadOnlyList<RemoteReturnClaim> items) =>
        items.Count == 0 ? null : string.Join(',', items.Select(item => item.ExternalClaimId).Order(StringComparer.Ordinal));

    private static void ThrowAtReturnPageGuard(PagedReadEnd walk, string marketplace)
    {
        if (walk.Stop == PagedReadStop.PageCap)
            throw PagedReadLoop.GuardFailure($"{marketplace.ToUpperInvariant()}_RETURN_PAGE_CAP", $"{marketplace} iade listesi bir pencerede {ReturnPageCap} sayfayı aştı; okuma durduruldu ve bu dönem okunmuş sayılmadı. Sonraki okuma aynı dönemi yeniden okuyacak.");
        if (walk.Stop == PagedReadStop.RepeatedPage)
            throw PagedReadLoop.GuardFailure($"{marketplace.ToUpperInvariant()}_RETURN_PAGE_REPEATED", $"{marketplace} iade listesi sonraki sayfada aynı iadeleri döndürdü (sayfa numarası yok sayıldı); okuma durduruldu ve bu dönem okunmuş sayılmadı.");
    }

    private static void NoteWideReturnRead(SyncCursor wideCursor, int lookbackDays, int wideDays, DateTimeOffset now)
    {
        if (lookbackDays < wideDays) return;
        wideCursor.LastSuccessAt = now;
        wideCursor.Version++;
    }

    // A page's rows the adapter could not read (no claim or order number) are recorded, as on Hepsiburada.
    private async Task RecordReturnPageIssuesAsync(Guid tenantId, Guid connectionId, string keyPrefix, IReadOnlyList<AdapterPageIssue>? issues, CancellationToken cancellationToken)
    {
        foreach (var issue in issues ?? [])
        {
            TrackSkipped();
            await RecordIssue(tenantId, $"{keyPrefix}:{connectionId:N}:{issue.Identity}", issue.Code, issue.Message, cancellationToken);
        }
    }

    private async Task<RemoteReturnClaim> ResolvePazaramaReturnOrderLinesAsync(Guid tenantId, Guid connectionId, string correlationId, RemoteReturnClaim claim, CancellationToken cancellationToken)
    {
        var barcode = claim.Lines.Select(line => line.ProductCode?.Trim()).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        var stockCode = claim.Lines.Select(line => line.StockCode?.Trim()).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        decimal? refundAmount = null;
        try
        {
            using var raw = JsonDocument.Parse(claim.RawJson);
            barcode ??= raw.RootElement.TryGetProperty("productCode", out var code) && code.ValueKind == JsonValueKind.String ? code.GetString()?.Trim() : null;
            stockCode ??= raw.RootElement.TryGetProperty("productStockCode", out var stock) && stock.ValueKind == JsonValueKind.String ? stock.GetString()?.Trim() : null;
            refundAmount = PazaramaReturnMapper.RefundAmount(raw.RootElement);
        }
        catch (JsonException) { }
        var issueKey = $"pazarama-return-line:{connectionId:N}:{claim.ExternalClaimId}";
        if (string.IsNullOrWhiteSpace(barcode) && string.IsNullOrWhiteSpace(stockCode))
        {
            await RecordIssue(tenantId, issueKey, "PAZARAMA_RETURN_LINE_UNMATCHED", $"Pazarama iade talebi {claim.ExternalClaimId} için ürün barkodu gelmedi; sipariş satırı eşlenemedi.", cancellationToken);
            return claim;
        }

        var orderId = await db.Orders.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && (x.ExternalOrderId == claim.ExternalOrderId || x.OrderNumber == claim.ExternalOrderId))
            .Select(x => (Guid?)x.Id)
            .SingleOrDefaultAsync(cancellationToken);
        if (orderId is null)
        {
            TrackRequest();
            var order = await orders.GetAsync(Context(tenantId, connectionId, correlationId, $"pazarama-return-order:{claim.ExternalOrderId}"), claim.ExternalOrderId, cancellationToken);
            if (!order.IsSuccess) return claim;
            TrackReceived();
            await UpsertOrder(tenantId, connectionId, order.Value!, cancellationToken);
            orderId = await db.Orders.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && (x.ExternalOrderId == claim.ExternalOrderId || x.OrderNumber == claim.ExternalOrderId))
                .Select(x => (Guid?)x.Id)
                .SingleOrDefaultAsync(cancellationToken);
            if (orderId is null) return claim;
        }

        var lines = await db.OrderLines.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.OrderId == orderId.Value)
            .Select(x => new { x.ExternalLineId, x.Barcode, x.Sku, Quantity = x.OrderedQuantity, x.UnitPrice })
            .ToListAsync(cancellationToken);
        var matched = lines.Where(line => !string.IsNullOrWhiteSpace(barcode) && string.Equals(line.Barcode?.Trim(), barcode.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        if (matched.Count == 0 && !string.IsNullOrWhiteSpace(stockCode))
            matched = lines.Where(line => string.Equals(line.Sku?.Trim(), stockCode.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        var matches = matched.Select(line => line.ExternalLineId).ToList();
        if (matches.Count == 0)
        {
            await RecordIssue(tenantId, issueKey, "PAZARAMA_RETURN_LINE_UNMATCHED", $"Pazarama iade talebi {claim.ExternalClaimId} (barkod {barcode ?? stockCode}) siparişteki hiçbir satırla eşleşmedi; iade satırı bağlanamadı.", cancellationToken);
            return claim;
        }
        await ResolveIssue(tenantId, issueKey, cancellationToken);
        // The quantity Pazarama did not send is worked out from the order line only when every matching line agrees on
        // its quantity and unit price; otherwise it stays unknown (0) and UpsertReturn records "adet bilinmiyor".
        var sameShape = matched.Select(line => (line.Quantity, line.UnitPrice)).Distinct().Count() == 1;
        return claim with
        {
            Lines = claim.Lines.Select(line => line with
            {
                ExternalOrderLineId = matches[0],
                AlternateExternalOrderLineIds = matches.Skip(1).ToList(),
                Quantity = sameShape ? PazaramaReturnMapper.ResolveQuantity(line.Quantity, refundAmount, matched[0].UnitPrice, matched[0].Quantity) ?? 0m : line.Quantity
            }).ToList()
        };
    }

    private const int N11ReturnLookbackDays = 45;
    private const int N11ReturnInitialLookbackDays = 90;

    private async Task<bool> SyncN11Returns(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, CancellationToken cancellationToken)
    {
        var cursor = await Cursor(tenantId, connectionId, "RETURNS", cancellationToken);
        var now = timeProvider.GetUtcNow();
        var full = ReadBoolean(payloadJson, "forceFull") || cursor.LastSuccessAt is null;
        var wideCursor = await Cursor(tenantId, connectionId, ReturnRereadPolicy.WideCursorResource, cancellationToken);
        var lookbackDays = ReturnRereadPolicy.LookbackDays(full, N11ReturnInitialLookbackDays, N11ReturnLookbackDays, wideCursor.LastSuccessAt, now);
        var window = new ReturnPollWindow(now.AddDays(-lookbackDays), now);
        var walk = await PagedReadLoop.RunAsync("0", ReturnPageGuard, async (cursor, _) =>
        {
            var page = int.Parse(cursor!, NumberStyles.None, CultureInfo.InvariantCulture);
            TrackRequest();
            var result = await returns.PollAsync(Context(tenantId, connectionId, correlationId, $"n11-return-sync:{page}"), window, new(page.ToString(CultureInfo.InvariantCulture), 100), cancellationToken);
            if (!result.IsSuccess) { TrackResultFailure(result.Error); throw JobProcessingException.FromAdapter(result.Error!); }
            await RecordReturnPageIssuesAsync(tenantId, connectionId, "n11-return", result.Value!.Issues, cancellationToken);
            foreach (var claim in result.Value!.Items)
            {
                TrackReceived();
                var resolved = await ResolveN11ReturnOrderLinesAsync(tenantId, connectionId, correlationId, claim, cancellationToken);
                await UpsertReturn(tenantId, connectionId, resolved, cancellationToken);
            }
            return new PageStep(result.Value.HasMore ? NextReturnPageNumber(page, result.Value.NextCursor) : null, ReturnPageKey(result.Value.Items));
        }, cancellationToken);
        ThrowAtReturnPageGuard(walk, "N11");
        cancellationToken.ThrowIfCancellationRequested();
        NoteWideReturnRead(wideCursor, lookbackDays, N11ReturnLookbackDays, now);
        cursor.OpaqueCursor = null;
        cursor.LastModifiedWatermark = now;
        cursor.LastSuccessAt = timeProvider.GetUtcNow();
        cursor.Version++;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    // An n11 claim names the product (productId) but not the order line. Ensure the order is
    // stored, then point the claim at the order lines whose n11 snapshot carries that product.
    private async Task<RemoteReturnClaim> ResolveN11ReturnOrderLinesAsync(Guid tenantId, Guid connectionId, string correlationId, RemoteReturnClaim claim, CancellationToken cancellationToken)
    {
        var productId = claim.Lines.Select(line => line.ProductCode?.Trim()).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        if (productId is null)
            try
            {
                using var raw = JsonDocument.Parse(claim.RawJson);
                if (raw.RootElement.TryGetProperty("productId", out var value) && value.ValueKind == JsonValueKind.String) productId = value.GetString()?.Trim();
            }
            catch (JsonException) { }
        if (string.IsNullOrWhiteSpace(productId))
        {
            await RecordIssue(tenantId, $"n11-return-line:{connectionId:N}:{claim.ExternalClaimId}", "N11_RETURN_LINE_UNMATCHED", $"N11 iade talebi {claim.ExternalClaimId} için ürün numarası gelmedi; sipariş satırı eşlenemedi.", cancellationToken);
            return claim;
        }

        var orderId = await db.Orders.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && (x.ExternalOrderId == claim.ExternalOrderId || x.OrderNumber == claim.ExternalOrderId))
            .Select(x => (Guid?)x.Id)
            .SingleOrDefaultAsync(cancellationToken);
        if (orderId is null)
        {
            TrackRequest();
            var order = await orders.GetAsync(Context(tenantId, connectionId, correlationId, $"n11-return-order:{claim.ExternalOrderId}"), claim.ExternalOrderId, cancellationToken);
            if (!order.IsSuccess) return claim;
            TrackReceived();
            await UpsertOrder(tenantId, connectionId, order.Value!, cancellationToken);
            orderId = await db.Orders.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && (x.ExternalOrderId == claim.ExternalOrderId || x.OrderNumber == claim.ExternalOrderId))
                .Select(x => (Guid?)x.Id)
                .SingleOrDefaultAsync(cancellationToken);
            if (orderId is null) return claim;
        }

        var lines = await db.OrderLines.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.OrderId == orderId.Value)
            .Select(x => new { x.ExternalLineId, x.SourceSnapshotJson, Quantity = x.OrderedQuantity })
            .ToListAsync(cancellationToken);
        var matched = lines.Where(line => string.Equals(N11LineProductId(line.SourceSnapshotJson), productId, StringComparison.Ordinal)).ToList();
        var matches = matched.Select(line => line.ExternalLineId).ToList();
        if (matches.Count == 0)
        {
            await RecordIssue(tenantId, $"n11-return-line:{connectionId:N}:{claim.ExternalClaimId}", "N11_RETURN_LINE_UNMATCHED", $"N11 iade talebi {claim.ExternalClaimId} (ürün {productId}) siparişteki hiçbir satırla eşleşmedi; iade satırı bağlanmadı.", cancellationToken);
            return claim;
        }
        // n11 sent no quantity: only an order line of one unit tells it (nothing else can come back); otherwise it stays
        // unknown (0) and UpsertReturn records "adet bilinmiyor" instead of writing 1.
        var singleUnit = matched.All(line => line.Quantity == 1);
        return claim with
        {
            Lines = claim.Lines.Select(line => line with { ExternalOrderLineId = matches[0], AlternateExternalOrderLineIds = matches.Skip(1).ToList(), Quantity = line.Quantity > 0 ? line.Quantity : singleUnit ? 1 : 0 }).ToList()
        };
    }

    internal static string? N11LineProductId(string? snapshotJson)
    {
        if (string.IsNullOrWhiteSpace(snapshotJson)) return null;
        try
        {
            using var document = JsonDocument.Parse(snapshotJson);
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("productId", out var value)
                ? value.ValueKind switch
                {
                    JsonValueKind.String => value.GetString()?.Trim(),
                    JsonValueKind.Number => value.GetRawText(),
                    _ => null
                }
                : null;
        }
        catch (JsonException) { return null; }
    }

    private static ShopifyReturnSyncState? ReadShopifyReturnSyncState(string? value)
    {
        return ResumableCursor.Read<ShopifyReturnSyncState>(value, state => state is { Version: ShopifyReturnSyncStateVersion });
    }
}
