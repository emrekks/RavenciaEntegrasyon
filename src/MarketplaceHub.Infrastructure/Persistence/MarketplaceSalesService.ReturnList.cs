using System.Globalization;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

// The Returns page's list, tab counts and filter choices (audit 2026-10-19 W4). The page used to load
// every return and split, filter, count and page them in the browser; the server now does the same
// work with the same rules and sends one page of cards plus a summary.
public sealed partial class MarketplaceSalesService
{
    internal const int WholeCard = 0;
    internal const int WaitingCard = 1;
    internal const int ApprovedCard = 2;
    internal static readonly string[] ReturnTabs = ["ALL", "REQUESTED", "SHIPPING", "ACTION_REQUIRED", "APPROVED", "REJECTED", "REVIEW", "DISPUTED", "SUSPENDED"];
    private static readonly CultureInfo TurkishCulture = CultureInfo.GetCultureInfo("tr-TR");

    private sealed record ReturnClaimRow(Guid Id, ReturnClaimStatus Status, Guid OrderId, Guid ConnectionId, string ExternalClaimId, string? CargoProviderName, string? ReasonCode, string? ReasonText, DateTimeOffset LastRemoteModifiedAt);

    // One row of the Returns page. LineIds are the card's order lines (a claim's view lines).
    internal sealed record ReturnCard(Guid ClaimId, int Kind, string Status, string PlatformCode, string PlatformName, string? CargoProviderName, string? ReasonCode, string? ReasonText, IReadOnlyList<Guid> LineIds)
    {
        public string? RowKey => Kind switch { WaitingCard => $"{ClaimId:D}:waiting", ApprovedCard => $"{ClaimId:D}:approved", _ => null };
    }

    private sealed record ReturnCardScan(List<ReturnCard> Cards, IReadOnlyDictionary<Guid, ReturnClaimRow> Claims, IReadOnlyDictionary<Guid, ReturnListView>? Views);

    // The page's status groups. AWAITING_SHIPMENT belongs to "Kargoya Verilen", the tab that lists it.
    internal static string ReturnTabGroup(string status) => status.Trim().ToUpperInvariant() switch
    {
        "APPROVED" or "COMPLETED" => "APPROVED",
        "REJECTED" or "CANCELLED" => "REJECTED",
        "REQUESTED" or "CREATED" => "REQUESTED",
        "ACTION_REQUIRED" or "WAITING_FOR_SELLER_ACTION" => "ACTION_REQUIRED",
        "AWAITING_SHIPMENT" or "WAITING_FOR_SHIPMENT" or "IN_TRANSIT" or "RETURN_IN_TRANSIT" or "SHIPPED" => "SHIPPING",
        "DISPUTED" => "DISPUTED",
        "SUSPENDED" or "ON_HOLD" => "SUSPENDED",
        _ => "REVIEW"
    };

    // A claim whose lines are partly accepted and partly still waiting for the seller is two cards:
    // the waiting lines (under "Aksiyon Bekleyen"), then the accepted ones (under "Onaylanan").
    internal static IReadOnlyList<(int Kind, IReadOnlyList<Guid> LineIds)> SplitReturnClaim(IReadOnlyList<Guid> lineIds, IReadOnlyDictionary<Guid, string>? lineStatuses)
    {
        if (lineStatuses is null || lineIds.Count < 2) return [(WholeCard, lineIds)];
        string? Status(Guid id) => lineStatuses.TryGetValue(id, out var value) ? value.ToUpperInvariant() : null;
        var accepted = lineIds.Where(id => Status(id) is "APPROVED" or "COMPLETED").ToList();
        var waiting = lineIds.Where(id => string.IsNullOrEmpty(Status(id)) || Status(id) == "ACTION_REQUIRED").ToList();
        return accepted.Count == 0 || waiting.Count == 0 ? [(WholeCard, lineIds)] : [(WaitingCard, waiting), (ApprovedCard, accepted)];
    }

    internal static string ReturnReasonKey(string? reasonCode, string? reasonText) =>
        reasonCode?.Trim() is { Length: > 0 } code ? code : reasonText?.Trim() ?? string.Empty;

    internal static bool MatchesReturnRefinements(string platformCode, string? cargoProviderName, string? reasonCode, string? reasonText, ReturnListQuery options)
    {
        var platforms = options.Platforms?.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()).ToArray() ?? [];
        if (platforms.Length > 0 && !platforms.Contains(platformCode, StringComparer.Ordinal)) return false;
        var cargo = options.Cargo?.Trim();
        if (!string.IsNullOrEmpty(cargo) && cargo != "ALL" && cargoProviderName?.Trim() != cargo) return false;
        var reason = options.ReasonKey?.Trim();
        return string.IsNullOrEmpty(reason) || reason == "ALL" || ReturnReasonKey(reasonCode, reasonText) == reason;
    }

    // The page's search: any of the card's buyer, order, claim, barcode, reason or line fields
    // contains the term, ignoring case the Turkish way and with the i letters folded like the
    // database pre-filter (TurkishSearchText): "ışık" and "işik" both find "Işıklı".
    internal static bool MatchesReturnSearch(ReturnListView view, IReadOnlyCollection<Guid> lineIds, string term)
    {
        static string Fold(string value) => TurkishSearchText.Fold(value.ToLower(TurkishCulture));
        var needle = Fold(term.Trim());
        if (needle.Length == 0) return true;
        var lines = (view.Lines ?? []).Where(line => lineIds.Contains(line.Id));
        return new[] { view.CustomerName, view.OrderNumber, view.ExternalClaimId, view.PrimaryBarcode, view.ReasonText, view.ReasonCode }
            .Concat(lines.SelectMany(line => new[] { line.Sku, line.Barcode, line.Title }))
            .Any(candidate => candidate is not null && Fold(candidate).Contains(needle, StringComparison.Ordinal));
    }

    // The platform, cargo and reason choices of the page as a database filter (MatchesReturnRefinements).
    private void ApplyReturnRefinements(ref IQueryable<ReturnClaim> query, Guid tenantId, ReturnListQuery options)
    {
        var platforms = options.Platforms?.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()).Distinct().ToArray() ?? [];
        if (platforms.Length > 0)
            query = query.Where(claim => db.PlatformConnections.Any(connection => connection.TenantId == tenantId && connection.Id == claim.ConnectionId && platforms.Contains(connection.PlatformCode)));
        var cargo = options.Cargo?.Trim();
        if (!string.IsNullOrEmpty(cargo) && cargo != "ALL")
            query = query.Where(claim => claim.CargoProviderName != null && claim.CargoProviderName.Trim() == cargo);
        var reason = options.ReasonKey?.Trim();
        if (!string.IsNullOrEmpty(reason) && reason != "ALL")
            query = query.Where(claim => (claim.ReasonCode != null && claim.ReasonCode.Trim() != "" ? claim.ReasonCode.Trim() : (claim.ReasonText ?? "").Trim()) == reason);
    }

    private async Task<ReturnCardScan> ScanReturnCardsAsync(Guid tenantId, ReturnListQuery options, bool applyTab, bool applyRefinements, CancellationToken cancellationToken)
    {
        var query = ReturnBaseQuery(tenantId);
        ApplyReturnFilters(ref query, applyTab ? options : options with { Status = null });
        // Platform, cargo and reason are decided by the database (audit 2026-10-20 G8); the card
        // check below repeats them on the loaded rows.
        if (applyRefinements) ApplyReturnRefinements(ref query, tenantId, options);
        var rows = await query.OrderBy(x => x.Id)
            .Select(x => new ReturnClaimRow(x.Id, x.Status, x.OrderId, x.ConnectionId, x.ExternalClaimId, x.CargoProviderName, x.ReasonCode, x.ReasonText, x.LastRemoteModifiedAt))
            .ToListAsync(cancellationToken);
        var claims = rows.ToDictionary(x => x.Id);
        if (rows.Count == 0) return new([], claims, null);

        var connectionIds = rows.Select(x => x.ConnectionId).Distinct().ToArray();
        var connections = await db.PlatformConnections.AsNoTracking().Where(x => x.TenantId == tenantId && connectionIds.Contains(x.Id))
            .Select(x => new { x.Id, x.PlatformCode, x.DisplayName }).ToDictionaryAsync(x => x.Id, cancellationToken);
        var claimIds = rows.Select(x => x.Id).ToArray();
        var lines = await db.ReturnLines.AsNoTracking().Where(x => x.TenantId == tenantId && claimIds.Contains(x.ClaimId))
            .Select(x => new { x.ClaimId, x.OrderLineId, x.RawStatus })
            .ToListAsync(cancellationToken);
        // One lookup of the order lines that still exist instead of one sub-query per return line.
        var referencedLineIds = lines.Select(x => x.OrderLineId).Distinct().ToArray();
        var existingLineIds = (await db.OrderLines.AsNoTracking().Where(x => x.TenantId == tenantId && referencedLineIds.Contains(x.Id))
            .Select(x => x.Id).ToListAsync(cancellationToken)).ToHashSet();
        var linesByClaim = lines.Select(x => new { x.ClaimId, x.OrderLineId, x.RawStatus, HasOrderLine = existingLineIds.Contains(x.OrderLineId) }).ToLookup(x => x.ClaimId);

        var cards = new List<ReturnCard>();
        foreach (var row in rows)
        {
            var connection = connections.GetValueOrDefault(row.ConnectionId);
            var platformCode = connection?.PlatformCode ?? "TRENDYOL";
            if (applyRefinements && !MatchesReturnRefinements(platformCode, row.CargoProviderName, row.ReasonCode, row.ReasonText, options)) continue;
            var claimLines = linesByClaim[row.Id].ToList();
            var statuses = claimLines.Where(x => !string.IsNullOrWhiteSpace(x.RawStatus)).GroupBy(x => x.OrderLineId)
                .ToDictionary(group => group.Key, group => Wire(MarketplaceReturnStatus.Canonicalize(group.First().RawStatus)));
            var status = Wire(row.Status);
            foreach (var (kind, cardLines) in SplitReturnClaim(claimLines.Where(x => x.HasOrderLine).Select(x => x.OrderLineId).ToList(), statuses))
                cards.Add(new(row.Id, kind, kind == ApprovedCard ? "APPROVED" : status, platformCode, connection?.DisplayName ?? "Trendyol", row.CargoProviderName, row.ReasonCode, row.ReasonText, cardLines));
        }

        // Every card belongs only to the tab of its own group: the database filter of a tab is
        // broader (Onaylanan also reads claims still waiting with an accepted line), and a whole
        // card of such a claim waits for an action (audit 2026-10-20 G9).
        var tab = applyTab ? options.Status?.Trim().ToUpperInvariant() : null;
        if (!string.IsNullOrEmpty(tab) && tab != "ALL")
            cards = cards.Where(card => ReturnTabGroup(card.Status) == tab).ToList();

        IReadOnlyDictionary<Guid, ReturnListView>? views = null;
        if (!string.IsNullOrWhiteSpace(options.Search) && cards.Count > 0)
        {
            // The database search above is broad (it finds the term anywhere in the customer
            // snapshot); the page's own search then decides, card by card, on the shown fields.
            views = await ReturnViewsByIdAsync(tenantId, cards.Select(card => card.ClaimId).Distinct().ToArray(), cancellationToken);
            cards = cards.Where(card => views.TryGetValue(card.ClaimId, out var view) && MatchesReturnSearch(view, card.LineIds, options.Search!)).ToList();
        }

        var invoice = applyRefinements ? options.InvoiceStatus?.Trim().ToUpperInvariant() : null;
        if (!string.IsNullOrEmpty(invoice) && invoice != "ALL" && cards.Count > 0)
        {
            var labels = await ReturnInvoiceLabelsAsync(tenantId, cards.Select(card => claims[card.ClaimId]).Distinct().ToList(), cancellationToken);
            cards = cards.Where(card => labels.GetValueOrDefault(card.ClaimId) == invoice).ToList();
        }
        return new(cards, claims, views);
    }

    private async Task<IReadOnlyDictionary<Guid, ReturnListView>> ReturnViewsByIdAsync(Guid tenantId, Guid[] claimIds, CancellationToken cancellationToken)
    {
        var claims = await db.ReturnClaims.AsNoTracking().Where(x => x.TenantId == tenantId && claimIds.Contains(x.Id)).OrderBy(x => x.Id).ToListAsync(cancellationToken);
        return (await ReturnViewsAsync(tenantId, claims, cancellationToken)).ToDictionary(view => view.Id);
    }

    // The invoice label each claim's view shows (ReturnInvoiceLabel), read without building the
    // views: the order's latest package and invoice, and its customer snapshot only when the label
    // falls back on it.
    private async Task<Dictionary<Guid, string>> ReturnInvoiceLabelsAsync(Guid tenantId, IReadOnlyCollection<ReturnClaimRow> claims, CancellationToken cancellationToken)
    {
        var orderIds = claims.Select(x => x.OrderId).Distinct().ToArray();
        var existingOrders = (await db.Orders.AsNoTracking().Where(x => x.TenantId == tenantId && orderIds.Contains(x.Id)).Select(x => x.Id).ToListAsync(cancellationToken)).ToHashSet();
        var packages = (await db.ShipmentPackages.AsNoTracking().Where(x => x.TenantId == tenantId && orderIds.Contains(x.OrderId))
                .Select(x => new { x.OrderId, x.StatusOccurredAt, x.MarketplaceInvoiceStatus, x.RawStatus }).ToListAsync(cancellationToken))
            .GroupBy(x => x.OrderId).ToDictionary(group => group.Key, group => group.OrderByDescending(x => x.StatusOccurredAt).First());
        var invoices = (await db.Invoices.AsNoTracking().Where(x => x.TenantId == tenantId && orderIds.Contains(x.OrderId) && x.OriginalInvoiceId == null
                    && db.PlatformConnections.Any(connection => connection.TenantId == tenantId && connection.Id == x.ProviderConnectionId && (connection.Status == "ACTIVE" || connection.Status == "VERIFIED")))
                .Select(x => new { x.OrderId, x.CreatedAt, x.Status, x.InvoiceNumber }).ToListAsync(cancellationToken))
            .GroupBy(x => x.OrderId).ToDictionary(group => group.Key, group => group.OrderByDescending(x => x.CreatedAt).First());
        var snapshotOrderIds = orderIds.Where(id => existingOrders.Contains(id) && !invoices.ContainsKey(id)
            && ReturnInvoiceLabelReadsSnapshot(null, packages.GetValueOrDefault(id)?.MarketplaceInvoiceStatus ?? MarketplaceInvoiceStatus.Unknown, packages.GetValueOrDefault(id)?.RawStatus)).ToArray();
        var snapshots = snapshotOrderIds.Length == 0
            ? new Dictionary<Guid, string>()
            : await db.Orders.AsNoTracking().Where(x => x.TenantId == tenantId && snapshotOrderIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.CustomerSnapshotJson, cancellationToken);
        var labels = new Dictionary<Guid, string>();
        foreach (var claim in claims)
        {
            if (!existingOrders.Contains(claim.OrderId)) { labels[claim.Id] = "FATURA_BEKLIYOR"; continue; }
            var package = packages.GetValueOrDefault(claim.OrderId);
            var invoice = invoices.GetValueOrDefault(claim.OrderId);
            labels[claim.Id] = InvoiceLabel(invoice?.Status, invoice?.InvoiceNumber, package?.MarketplaceInvoiceStatus ?? MarketplaceInvoiceStatus.Unknown,
                snapshots.GetValueOrDefault(claim.OrderId) ?? "{}", package is null ? [] : [package.RawStatus]);
        }
        return labels;
    }

    // InvoiceLabel reads the customer snapshot only without a local invoice, without a marketplace
    // invoice observation and without an INVOICED package status.
    internal static bool ReturnInvoiceLabelReadsSnapshot(InvoiceStatus? invoiceStatus, MarketplaceInvoiceStatus marketplaceStatus, string? packageRawStatus) =>
        invoiceStatus is null && marketplaceStatus == MarketplaceInvoiceStatus.Unknown && !IsInvoicedRemoteStatus(packageRawStatus);

    private static string NormalizeReturnSort(string? sort) => sort?.Trim().ToUpperInvariant() switch
    {
        "APPROVED_DESC" => "APPROVED_DESC",
        "APPROVED_ASC" => "APPROVED_ASC",
        "ORDERED_DESC" => "ORDERED_DESC",
        "ORDERED_ASC" => "ORDERED_ASC",
        _ => "ID"
    };

    // Claim order (as the database lists claims), or the approved tab's dates: empty dates last,
    // then the claim id, then the claim's cards in order.
    internal static int CompareReturnCards((DateTimeOffset? Date, Guid ClaimId, int Kind) left, (DateTimeOffset? Date, Guid ClaimId, int Kind) right, string sort)
    {
        if (sort != "ID")
        {
            if (left.Date is null && right.Date is not null) return 1;
            if (right.Date is null && left.Date is not null) return -1;
            if (left.Date is { } leftDate && right.Date is { } rightDate && leftDate != rightDate)
                return sort.EndsWith("_DESC", StringComparison.Ordinal) ? rightDate.CompareTo(leftDate) : leftDate.CompareTo(rightDate);
        }
        var byClaim = string.CompareOrdinal(left.ClaimId.ToString("D"), right.ClaimId.ToString("D"));
        return byClaim != 0 ? byClaim : left.Kind.CompareTo(right.Kind);
    }

    private async Task<Dictionary<Guid, DateTimeOffset?>> ReturnSortDatesAsync(Guid tenantId, IReadOnlyCollection<ReturnClaimRow> claims, string sort, CancellationToken cancellationToken)
    {
        if (sort.StartsWith("ORDERED", StringComparison.Ordinal))
        {
            var orderIds = claims.Select(x => x.OrderId).Distinct().ToArray();
            var orderedAt = await db.Orders.AsNoTracking().Where(x => x.TenantId == tenantId && orderIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.OrderedAt, cancellationToken);
            return claims.ToDictionary(x => x.Id, x => orderedAt.TryGetValue(x.OrderId, out var value) ? (DateTimeOffset?)value : null);
        }
        // As the views: an approved or completed claim's last successful approval, else its last change.
        var approvedIds = claims.Where(x => x.Status is ReturnClaimStatus.Approved or ReturnClaimStatus.Completed).Select(x => x.Id).ToArray();
        var decisions = approvedIds.Length == 0
            ? new Dictionary<Guid, DateTimeOffset?>()
            : await db.ReturnDecisions.AsNoTracking()
                .Where(x => x.TenantId == tenantId && approvedIds.Contains(x.ClaimId) && x.Action == "APPROVE" && x.Status == "SUCCEEDED")
                .GroupBy(x => x.ClaimId).Select(group => new { ClaimId = group.Key, ApprovedAt = group.Max(decision => decision.CompletedAt) })
                .ToDictionaryAsync(x => x.ClaimId, x => x.ApprovedAt, cancellationToken);
        return claims.ToDictionary(x => x.Id, x => x.Status is ReturnClaimStatus.Approved or ReturnClaimStatus.Completed
            ? decisions.GetValueOrDefault(x.Id) ?? x.LastRemoteModifiedAt
            : (DateTimeOffset?)null);
    }

    private async Task<PageResult<ReturnListView>> ReturnCardPageAsync(Guid tenantId, int limit, int page, string? after, ReturnListQuery options, CancellationToken cancellationToken)
    {
        var sort = NormalizeReturnSort(options.Sort);
        var scan = await ScanReturnCardsAsync(tenantId, options, applyTab: true, applyRefinements: true, cancellationToken);
        var dates = sort == "ID" ? null : await ReturnSortDatesAsync(tenantId, scan.Cards.Select(card => scan.Claims[card.ClaimId]).Distinct().ToList(), sort, cancellationToken);
        var ordered = scan.Cards.Select(card => (Card: card, Key: (Date: dates?.GetValueOrDefault(card.ClaimId), card.ClaimId, card.Kind))).ToList();
        ordered.Sort((left, right) => CompareReturnCards(left.Key, right.Key, sort));

        int start;
        if (!string.IsNullOrWhiteSpace(after))
        {
            if (!cursors.TryDecodeReturn(after, out var cursorSort, out var cursorDate, out var cursorClaimId, out var cursorKind) || cursorSort != sort)
                throw new ArgumentException("Cursor geçersiz veya seçili sıralamayla uyuşmuyor.", nameof(after));
            start = ordered.FindIndex(item => CompareReturnCards(item.Key, (cursorDate, cursorClaimId, cursorKind), sort) > 0);
            if (start < 0) start = ordered.Count;
        }
        else start = page > 1 ? (int)Math.Min(ordered.Count, (long)(page - 1) * limit) : 0;

        var pageCards = ordered.Skip(start).Take(limit).ToList();
        var views = scan.Views ?? await ReturnViewsByIdAsync(tenantId, pageCards.Select(item => item.Card.ClaimId).Distinct().ToArray(), cancellationToken);
        var items = pageCards.Where(item => views.ContainsKey(item.Card.ClaimId)).Select(item => ReturnCardView(views[item.Card.ClaimId], item.Card)).ToList();
        var hasMore = start + pageCards.Count < ordered.Count;
        var last = pageCards.LastOrDefault();
        var next = hasMore && pageCards.Count > 0 ? cursors.EncodeReturn(sort, last.Key.Date, last.Key.ClaimId, last.Key.Kind) : null;
        return new(items, next, hasMore, ordered.Count);
    }

    // A split card shows only its own lines; the accepted card reads as an approved return.
    internal static ReturnListView ReturnCardView(ReturnListView view, ReturnCard card)
    {
        if (card.Kind == WholeCard) return view;
        var lines = (view.Lines ?? []).Where(line => card.LineIds.Contains(line.Id)).ToList();
        return view with
        {
            Lines = lines,
            ProductCount = lines.Count,
            Status = card.Kind == ApprovedCard ? "APPROVED" : view.Status,
            RawStatus = card.Kind == ApprovedCard ? "Accepted" : view.RawStatus,
            RefundAmount = ReturnRefundAmounts.ClaimTotal(lines.Select(line => line.RefundAmount)),
            RowKey = card.RowKey
        };
    }

    // The page polls its summary; while no claim of the tenant changed (same count and latest
    // change) the same summary is answered for a short while instead of scanning every claim again.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTimeOffset At, (int Count, DateTimeOffset? Changed) Stamp, ReturnSummaryView View)> ReturnSummaryCache = new();
    internal static readonly TimeSpan ReturnSummaryCacheLifetime = TimeSpan.FromSeconds(30);

    public async Task<ReturnSummaryView> ReturnSummaryAsync(Guid tenantId, ReturnListQuery options, CancellationToken cancellationToken)
    {
        var counts = ReturnTabs.ToDictionary(tab => tab, _ => 0, StringComparer.Ordinal);
        if (!await HasOperationalReturnConnectionAsync(tenantId, cancellationToken)) return new(counts, [], [], [], [], 0);

        var now = timeProvider.GetUtcNow();
        var stamp = await db.ReturnClaims.AsNoTracking().Where(x => x.TenantId == tenantId)
            .GroupBy(_ => 1).Select(group => new { Count = group.Count(), Changed = group.Max(x => (DateTimeOffset?)x.UpdatedAt) })
            .SingleOrDefaultAsync(cancellationToken);
        var currentStamp = (stamp?.Count ?? 0, stamp?.Changed);
        var cacheKey = $"{tenantId:N}|{System.Text.Json.JsonSerializer.Serialize(options)}|{now.UtcDateTime:yyyyMMdd}";
        if (ReturnSummaryCache.TryGetValue(cacheKey, out var cached) && cached.Stamp == currentStamp && now - cached.At < ReturnSummaryCacheLifetime)
            return cached.View;
        var view = await BuildReturnSummaryAsync(tenantId, options, counts, cancellationToken);
        if (ReturnSummaryCache.Count > 1000) ReturnSummaryCache.Clear();
        ReturnSummaryCache[cacheKey] = (now, currentStamp, view);
        return view;
    }

    private async Task<ReturnSummaryView> BuildReturnSummaryAsync(Guid tenantId, ReturnListQuery options, Dictionary<string, int> counts, CancellationToken cancellationToken)
    {

        var counted = await ScanReturnCardsAsync(tenantId, options, applyTab: false, applyRefinements: true, cancellationToken);
        foreach (var card in counted.Cards)
        {
            counts["ALL"]++;
            counts[ReturnTabGroup(card.Status)]++;
        }

        var tab = options.Status?.Trim().ToUpperInvariant();
        var refined = options.Platforms?.Any(value => !string.IsNullOrWhiteSpace(value)) == true
            || options.Cargo is { Length: > 0 } and not "ALL" || options.ReasonKey is { Length: > 0 } and not "ALL" || options.InvoiceStatus is { Length: > 0 } and not "ALL";
        var listed = (string.IsNullOrEmpty(tab) || tab == "ALL") && !refined
            ? counted
            : await ScanReturnCardsAsync(tenantId, options, applyTab: true, applyRefinements: false, cancellationToken);

        var platforms = new List<string>();
        var platformNames = new Dictionary<string, string>(StringComparer.Ordinal);
        var cargo = new List<string>();
        var reasons = new List<string>();
        var reasonSources = new Dictionary<string, ReturnReasonOption>(StringComparer.Ordinal);
        foreach (var card in listed.Cards)
        {
            if (!platformNames.ContainsKey(card.PlatformCode)) platforms.Add(card.PlatformCode);
            platformNames[card.PlatformCode] = card.PlatformName;
            if (card.CargoProviderName?.Trim() is { Length: > 0 } provider && !cargo.Contains(provider, StringComparer.Ordinal)) cargo.Add(provider);
            var reason = ReturnReasonKey(card.ReasonCode, card.ReasonText);
            if (reason.Length == 0) continue;
            if (!reasonSources.ContainsKey(reason)) reasons.Add(reason);
            reasonSources[reason] = new(reason, card.ReasonCode, card.ReasonText);
        }
        var labels = listed.Cards.Count == 0
            ? new Dictionary<Guid, string>()
            : await ReturnInvoiceLabelsAsync(tenantId, listed.Cards.Select(card => listed.Claims[card.ClaimId]).Distinct().ToList(), cancellationToken);
        var invoiceStatuses = listed.Cards.Select(card => labels.GetValueOrDefault(card.ClaimId)).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        return new(counts, platforms.Select(code => new ReturnPlatformOption(code, platformNames[code])).ToList(), cargo,
            reasons.Select(reason => reasonSources[reason]).ToList(), invoiceStatuses, listed.Cards.Count);
    }
}
