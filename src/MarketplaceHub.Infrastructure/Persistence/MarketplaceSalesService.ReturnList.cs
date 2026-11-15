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

    internal sealed record ReturnClaimRow(Guid Id, ReturnClaimStatus Status, Guid OrderId, Guid ConnectionId, string ExternalClaimId, string? CargoProviderName, string? ReasonCode, string? ReasonText, DateTimeOffset? LastRemoteModifiedAt, string? RejectedPackageState = null, DateTimeOffset? DecidedAt = null, DateTimeOffset? RequestedAt = null, bool? CardSplit = null);

    // SIPARIS-STOK-9: the tenant's line raw statuses sorted by the split rule (accepted; waiting, a missing status aside),
    // and whether every claim of the tenant has its card columns (ReturnCardColumns) filled and equal to the rule right
    // now. Only then are the columns read; otherwise the page is computed from the claims and lines as before.
    internal sealed record ReturnColumnState(bool Fresh, string?[] Accepted, string?[] Waiting);

    internal async Task<ReturnColumnState> ReturnColumnStateAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var rawStatuses = await db.ReturnLines.AsNoTracking().Where(x => x.TenantId == tenantId).Select(x => x.RawStatus).Distinct().ToListAsync(cancellationToken);
        var accepted = rawStatuses.Where(IsAcceptedSplitLineStatus).ToArray();
        var waiting = rawStatuses.Where(raw => raw is not null && IsWaitingSplitLineStatus(raw)).ToArray();
        var fresh = !await ReturnCardColumns.Stale(db, tenantId, accepted, waiting).AnyAsync(cancellationToken);
        return new(fresh, accepted, waiting);
    }

    private static readonly ReturnColumnState UnfilledColumns = new(false, [], []);

    private sealed record ReturnCardLine(Guid ClaimId, Guid OrderLineId, string? RawStatus);

    // A claim's id and the date the page sorts it by (ReturnSortDatesAsync, read by the database).
    private sealed class ReturnSortKey
    {
        public Guid Id { get; init; }
        public DateTimeOffset? Date { get; init; }
    }

    // One row of the Returns page. LineIds are the card's order lines (a claim's view lines).
    internal sealed record ReturnCard(Guid ClaimId, int Kind, string Status, string PlatformCode, string PlatformName, string? CargoProviderName, string? ReasonCode, string? ReasonText, IReadOnlyList<Guid> LineIds)
    {
        public string? RowKey => Kind switch { WaitingCard => $"{ClaimId:D}:waiting", ApprovedCard => $"{ClaimId:D}:approved", _ => null };
    }

    internal sealed record ReturnCardScan(List<ReturnCard> Cards, IReadOnlyDictionary<Guid, ReturnClaimRow> Claims, IReadOnlyDictionary<Guid, ReturnListView>? Views);

    // The page's status groups. AWAITING_SHIPMENT belongs to "Kargoya Verilen", the tab that lists it.
    // A claim approved in part (PARTIALLY_APPROVED, "Kısmen Onaylandı") is listed with the approved ones.
    internal static string ReturnTabGroup(string status) => status.Trim().ToUpperInvariant() switch
    {
        "APPROVED" or "COMPLETED" or ReturnClaimPartialApprovalPolicy.PartiallyApproved => "APPROVED",
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

    // The status SplitReturnClaim reads for a line (ScanReturnCardsAsync): the canonical status of a raw status that is not
    // blank, else none.
    private static string? SplitLineStatus(string? rawStatus) =>
        string.IsNullOrWhiteSpace(rawStatus) ? null : Wire(MarketplaceReturnStatus.Canonicalize(rawStatus)).ToUpperInvariant();

    // SIPARIS-STOK-9: a claim splits only when one of its lines reads as accepted and another as still waiting, so a claim
    // without both kinds of raw status is one whole card and its lines need not be read for the list.
    internal static bool IsAcceptedSplitLineStatus(string? rawStatus) => SplitLineStatus(rawStatus) is "APPROVED" or "COMPLETED";

    internal static bool IsWaitingSplitLineStatus(string? rawStatus) => SplitLineStatus(rawStatus) is null or "ACTION_REQUIRED";

    internal static bool MaySplitReturnClaim(IEnumerable<string?> rawStatuses)
    {
        var statuses = rawStatuses.ToList();
        return statuses.Any(IsAcceptedSplitLineStatus) && statuses.Any(IsWaitingSplitLineStatus);
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
    internal void ApplyReturnRefinements(ref IQueryable<ReturnClaim> query, Guid tenantId, ReturnListQuery options)
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

    private async Task<ReturnCardScan> ScanReturnCardsAsync(Guid tenantId, ReturnListQuery options, bool applyTab, bool applyRefinements, ReturnColumnState columns, CancellationToken cancellationToken)
    {
        var query = ReturnBaseQuery(tenantId);
        ApplyReturnFilters(ref query, applyTab ? options : options with { Status = null });
        // Platform, cargo and reason are decided by the database (audit 2026-10-20 G8); the card
        // check below repeats them on the loaded rows.
        if (applyRefinements) ApplyReturnRefinements(ref query, tenantId, options);
        var tab = applyTab ? options.Status?.Trim().ToUpperInvariant() : null;
        // With the card columns filled, the database reads only the claims with a card in the tab: their first card's tab,
        // or an accepted card (Onaylanan) of a claim that may split. The card check below still decides.
        if (columns.Fresh && !string.IsNullOrEmpty(tab) && tab != "ALL")
            query = tab == "APPROVED" ? query.Where(x => x.TabGroup == tab || x.CardSplit == true) : query.Where(x => x.TabGroup == tab);
        var rows = await query.OrderBy(x => x.Id)
            .Select(x => new ReturnClaimRow(x.Id, x.Status, x.OrderId, x.ConnectionId, x.ExternalClaimId, x.CargoProviderName, x.ReasonCode, x.ReasonText, x.LastRemoteModifiedAt, x.RejectedPackageState, x.DecidedAt, x.RequestedAt, x.CardSplit))
            .ToListAsync(cancellationToken);
        var claims = rows.ToDictionary(x => x.Id);
        if (rows.Count == 0) return new([], claims, null);

        var cards = await ReturnCardsAsync(tenantId, rows, applyRefinements ? options : null, withLineIds: !string.IsNullOrWhiteSpace(options.Search), columns.Fresh, cancellationToken);

        // Every card belongs only to the tab of its own group: the database filter of a tab is
        // broader (Onaylanan also reads claims still waiting with an accepted line), and a whole
        // card of such a claim waits for an action (audit 2026-10-20 G9).
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

    // The cards of these claims, in their order (refinements, when given, drop the claims they do not match). A claim's lines
    // are read only when it may split (SIPARIS-STOK-9), or for every claim when the caller needs each card's lines (the
    // search); any other claim is one whole card, its status read from the distinct statuses of its lines. With the card
    // columns filled (columnsFresh) the claim's CardSplit says whether it may split, and a whole card's status is the
    // claim's own: the lines can only turn it into PARTIALLY_APPROVED, the same tab, and a card's status is read only
    // for its tab.
    private async Task<List<ReturnCard>> ReturnCardsAsync(Guid tenantId, IReadOnlyList<ReturnClaimRow> rows, ReturnListQuery? refinements, bool withLineIds, bool columnsFresh, CancellationToken cancellationToken)
    {
        var cards = new List<ReturnCard>();
        if (rows.Count == 0) return cards;
        var connectionIds = rows.Select(x => x.ConnectionId).Distinct().ToArray();
        var connections = await db.PlatformConnections.AsNoTracking().Where(x => x.TenantId == tenantId && connectionIds.Contains(x.Id))
            .Select(x => new { x.Id, x.PlatformCode, x.DisplayName }).ToDictionaryAsync(x => x.Id, cancellationToken);
        var claimIds = rows.Select(x => x.Id).ToArray();
        // With every claim's lines read anyway, their statuses need no read of their own.
        var rawStatuses = withLineIds || columnsFresh
            ? Array.Empty<(Guid ClaimId, string? RawStatus)>().ToLookup(x => x.ClaimId, x => x.RawStatus)
            : (await db.ReturnLines.AsNoTracking().Where(x => x.TenantId == tenantId && claimIds.Contains(x.ClaimId))
                    .Select(x => new { x.ClaimId, x.RawStatus }).Distinct().ToListAsync(cancellationToken))
                .ToLookup(x => x.ClaimId, x => x.RawStatus);
        var lineClaimIds = withLineIds ? claimIds
            : columnsFresh ? rows.Where(x => x.CardSplit == true).Select(x => x.Id).ToArray()
            : claimIds.Where(id => MaySplitReturnClaim(rawStatuses[id])).ToArray();
        List<ReturnCardLine> lines = lineClaimIds.Length == 0
            ? []
            : await db.ReturnLines.AsNoTracking().Where(x => x.TenantId == tenantId && lineClaimIds.Contains(x.ClaimId))
                .Select(x => new ReturnCardLine(x.ClaimId, x.OrderLineId, x.RawStatus))
                .ToListAsync(cancellationToken);
        // One lookup of the order lines that still exist instead of one sub-query per return line.
        var referencedLineIds = lines.Select(x => x.OrderLineId).Distinct().ToArray();
        HashSet<Guid> existingLineIds = referencedLineIds.Length == 0
            ? []
            : (await db.OrderLines.AsNoTracking().Where(x => x.TenantId == tenantId && referencedLineIds.Contains(x.Id))
                .Select(x => x.Id).ToListAsync(cancellationToken)).ToHashSet();
        var linesByClaim = lines.Select(x => new { x.ClaimId, x.OrderLineId, x.RawStatus, HasOrderLine = existingLineIds.Contains(x.OrderLineId) }).ToLookup(x => x.ClaimId);
        var lineClaims = lineClaimIds.ToHashSet();

        foreach (var row in rows)
        {
            var connection = connections.GetValueOrDefault(row.ConnectionId);
            var platformCode = connection?.PlatformCode ?? "TRENDYOL";
            if (refinements is not null && !MatchesReturnRefinements(platformCode, row.CargoProviderName, row.ReasonCode, row.ReasonText, refinements)) continue;
            if (!lineClaims.Contains(row.Id))
            {
                // ReturnStatusWire only asks whether some line is approved and some rejected: the distinct statuses answer it.
                cards.Add(new(row.Id, WholeCard, ReturnStatusWire(row.Status, rawStatuses[row.Id].ToList()), platformCode, connection?.DisplayName ?? "Trendyol", row.CargoProviderName, row.ReasonCode, row.ReasonText, []));
                continue;
            }
            var claimLines = linesByClaim[row.Id].ToList();
            var statuses = claimLines.Where(x => !string.IsNullOrWhiteSpace(x.RawStatus)).GroupBy(x => x.OrderLineId)
                .ToDictionary(group => group.Key, group => Wire(MarketplaceReturnStatus.Canonicalize(group.First().RawStatus)));
            var status = ReturnStatusWire(row.Status, claimLines.Select(x => x.RawStatus).ToList());
            foreach (var (kind, cardLines) in SplitReturnClaim(claimLines.Where(x => x.HasOrderLine).Select(x => x.OrderLineId).ToList(), statuses))
                cards.Add(new(row.Id, kind, kind == ApprovedCard ? "APPROVED" : status, platformCode, connection?.DisplayName ?? "Trendyol", row.CargoProviderName, row.ReasonCode, row.ReasonText, cardLines));
        }
        return cards;
    }

    internal async Task<IReadOnlyDictionary<Guid, ReturnListView>> ReturnViewsByIdAsync(Guid tenantId, Guid[] claimIds, CancellationToken cancellationToken)
    {
        var claims = await db.ReturnClaims.AsNoTracking().Where(x => x.TenantId == tenantId && claimIds.Contains(x.Id)).OrderBy(x => x.Id).ToListAsync(cancellationToken);
        return (await ReturnViewsAsync(tenantId, claims, cancellationToken)).ToDictionary(view => view.Id);
    }

    // The invoice label each claim's view shows (ReturnInvoiceLabel), read without building the
    // views: the order's latest package and invoice, and its customer snapshot only when the label
    // falls back on it.
    internal async Task<Dictionary<Guid, string>> ReturnInvoiceLabelsAsync(Guid tenantId, IReadOnlyCollection<ReturnClaimRow> claims, CancellationToken cancellationToken)
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

    internal static string NormalizeReturnSort(string? sort) => sort?.Trim().ToUpperInvariant() switch
    {
        "APPROVED_DESC" => "APPROVED_DESC",
        "APPROVED_ASC" => "APPROVED_ASC",
        "ORDERED_DESC" => "ORDERED_DESC",
        "ORDERED_ASC" => "ORDERED_ASC",
        "REQUESTED_ASC" => "REQUESTED_ASC",
        "ID" => "ID",
        // The page's default: the newest return request first (it used to be the claim id, the order in which the
        // panel first stored the claims, so the dates on the page looked random).
        _ => "REQUESTED_DESC"
    };

    // Claim order (as the database lists claims), or a date (the return request, the order, the approval): empty
    // dates last in either direction, then the claim id, then the claim's cards in order.
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

    internal async Task<Dictionary<Guid, DateTimeOffset?>> ReturnSortDatesAsync(Guid tenantId, IReadOnlyCollection<ReturnClaimRow> claims, string sort, CancellationToken cancellationToken)
    {
        // The marketplace's own request date of the claim (ReturnClaim.RequestedAt); a claim it has not dated stays empty.
        if (sort.StartsWith("REQUESTED", StringComparison.Ordinal))
            return claims.ToDictionary(x => x.Id, x => x.RequestedAt);
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

    internal async Task<PageResult<ReturnListView>> ReturnCardPageAsync(Guid tenantId, int limit, int page, string? after, ReturnListQuery options, CancellationToken cancellationToken)
    {
        var sort = NormalizeReturnSort(options.Sort);
        // The search reads every listed claim's lines anyway (each card's own lines decide); the columns save it nothing.
        var columns = string.IsNullOrWhiteSpace(options.Search) ? await ReturnColumnStateAsync(tenantId, cancellationToken) : UnfilledColumns;
        if (!ReturnCardsFilteredInMemory(options)) return await ReturnCardPageInDatabaseAsync(tenantId, limit, page, after, options, sort, columns, cancellationToken);
        var scan = await ScanReturnCardsAsync(tenantId, options, applyTab: true, applyRefinements: true, columns, cancellationToken);
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

    // The page's own search, its platform, cargo and reason choices and the invoice label are decided card by card on
    // loaded rows; without them the cards are counted, ordered and paged by the database.
    internal static bool ReturnCardsFilteredInMemory(ReturnListQuery options)
    {
        var invoice = options.InvoiceStatus?.Trim().ToUpperInvariant();
        var cargo = options.Cargo?.Trim();
        var reason = options.ReasonKey?.Trim();
        return !string.IsNullOrWhiteSpace(options.Search)
            || options.Platforms?.Any(value => !string.IsNullOrWhiteSpace(value)) == true
            || !string.IsNullOrEmpty(cargo) && cargo != "ALL"
            || !string.IsNullOrEmpty(reason) && reason != "ALL"
            || !string.IsNullOrEmpty(invoice) && invoice != "ALL";
    }

    // SIPARIS-STOK-9: the same page as ReturnCardPageAsync without reading every claim and line of the tenant. Every claim
    // in the tab's database filter is one card whose tab follows the claim's own status (ReturnStatusWire only turns an
    // approved or completed claim into PARTIALLY_APPROVED, the same tab), so the database counts and orders those cards
    // and reads one page of them. Only claims that may split (an accepted line beside a waiting one) have their lines
    // read: such a claim's first card is the waiting one and its accepted card is placed among the others by count.
    private async Task<PageResult<ReturnListView>> ReturnCardPageInDatabaseAsync(Guid tenantId, int limit, int page, string? after, ReturnListQuery options, string sort, ReturnColumnState columns, CancellationToken cancellationToken)
    {
        var query = ReturnBaseQuery(tenantId);
        ApplyReturnFilters(ref query, options);
        var tab = options.Status?.Trim().ToUpperInvariant();
        var everyTab = string.IsNullOrEmpty(tab) || tab == "ALL";
        var tabStatuses = everyTab ? null : Enum.GetValues<ReturnClaimStatus>().Where(status => ReturnTabGroup(Wire(status)) == tab).ToArray();
        var main = tabStatuses is null ? query : columns.Fresh ? query.Where(x => x.TabGroup == tab) : query.Where(x => tabStatuses.Contains(x.Status));
        var mainKeys = ReturnSortKeys(main, tenantId, sort, columns.Fresh);

        // The claims that may split (their CardSplit, or found by the raw statuses the tenant's lines carry); only their
        // lines are read.
        var (accepted, waiting) = (columns.Accepted, columns.Waiting);
        var splitQuery = columns.Fresh
            ? query.Where(x => x.CardSplit == true)
            : query.Where(x => db.ReturnLines.Any(line => line.TenantId == tenantId && line.ClaimId == x.Id && accepted.Contains(line.RawStatus))
                && db.ReturnLines.Any(line => line.TenantId == tenantId && line.ClaimId == x.Id && (line.RawStatus == null || waiting.Contains(line.RawStatus))));
        List<ReturnClaimRow> splitRows = accepted.Length == 0
            ? []
            : await splitQuery
                .OrderBy(x => x.Id)
                .Select(x => new ReturnClaimRow(x.Id, x.Status, x.OrderId, x.ConnectionId, x.ExternalClaimId, x.CargoProviderName, x.ReasonCode, x.ReasonText, x.LastRemoteModifiedAt, x.RejectedPackageState, x.DecidedAt, x.RequestedAt, x.CardSplit))
                .ToListAsync(cancellationToken);
        var splitCards = (await ReturnCardsAsync(tenantId, splitRows, null, withLineIds: false, columns.Fresh, cancellationToken))
            .GroupBy(card => card.ClaimId).Where(group => group.Count() > 1).ToDictionary(group => group.Key, group => group.ToList());
        int MainKind(Guid claimId) => splitCards.ContainsKey(claimId) ? WaitingCard : WholeCard;

        // The accepted cards in this tab, each with its place among all the tab's cards.
        var extraIds = splitCards.Values.Select(cards => cards[^1]).Where(card => everyTab || ReturnTabGroup(card.Status) == tab).Select(card => card.ClaimId).ToArray();
        var extras = new List<(DateTimeOffset? Date, Guid ClaimId, int Kind)>();
        var places = new List<int>();
        if (extraIds.Length > 0)
        {
            // One read: each accepted card's date, whether its claim's first card is listed too, and how many of the
            // listed first cards of other claims come before it.
            var placed = (await ReturnSortKeysBeforeEach(mainKeys, ReturnSortKeys(query.Where(x => extraIds.Contains(x.Id)), tenantId, sort, columns.Fresh), sort).ToListAsync(cancellationToken))
                .Select(row => (Key: (row.Date, ClaimId: row.Id, Kind: ApprovedCard), row.Before, row.Listed)).ToList();
            placed.Sort((left, right) => CompareReturnCards(left.Key, right.Key, sort));
            extras = placed.Select(row => row.Key).ToList();
            places = placed.Select((row, index) => row.Before + (row.Listed ? 1 : 0) + index).ToList();
        }
        var total = await main.CountAsync(cancellationToken) + extras.Count;

        int start;
        if (!string.IsNullOrWhiteSpace(after))
        {
            if (!cursors.TryDecodeReturn(after, out var cursorSort, out var cursorDate, out var cursorClaimId, out var cursorKind) || cursorSort != sort)
                throw new ArgumentException("Cursor geçersiz veya seçili sıralamayla uyuşmuyor.", nameof(after));
            var cursor = (cursorDate, cursorClaimId, cursorKind);
            start = await ReturnSortKeysBefore(mainKeys.Where(x => x.Id != cursorClaimId), cursorDate, cursorClaimId, sort).CountAsync(cancellationToken);
            start += (await mainKeys.Where(x => x.Id == cursorClaimId).ToListAsync(cancellationToken))
                .Count(key => CompareReturnCards((key.Date, key.Id, MainKind(key.Id)), cursor, sort) <= 0);
            start += extras.Count(extra => CompareReturnCards(extra, cursor, sort) <= 0);
        }
        else start = page > 1 ? (int)Math.Min(total, (long)(page - 1) * limit) : 0;

        var pageKeys = new List<(DateTimeOffset? Date, Guid ClaimId, int Kind)>();
        if (start < total && limit > 0)
        {
            var firstCards = await OrderReturnSortKeys(mainKeys, sort).Skip(start - places.Count(place => place < start)).Take(limit).ToListAsync(cancellationToken);
            pageKeys = firstCards.Select(key => (key.Date, ClaimId: key.Id, Kind: MainKind(key.Id)))
                .Concat(extras.Where((_, index) => places[index] >= start)).ToList();
            pageKeys.Sort((left, right) => CompareReturnCards(left, right, sort));
            pageKeys = pageKeys.Take(limit).ToList();
        }
        var views = await ReturnViewsByIdAsync(tenantId, pageKeys.Select(key => key.ClaimId).Distinct().ToArray(), cancellationToken);
        var items = pageKeys.Where(key => views.ContainsKey(key.ClaimId))
            .Select(key => key.Kind == WholeCard ? views[key.ClaimId] : ReturnCardView(views[key.ClaimId], splitCards[key.ClaimId].Single(card => card.Kind == key.Kind)))
            .ToList();
        var hasMore = start + pageKeys.Count < total;
        var next = hasMore && pageKeys.Count > 0 ? cursors.EncodeReturn(sort, pageKeys[^1].Date, pageKeys[^1].ClaimId, pageKeys[^1].Kind) : null;
        return new(items, next, hasMore, total);
    }

    // ReturnSortDatesAsync as one database read per claim: the request date, the order date, or an approved or completed
    // claim's last successful approval (else its last change; the claim's ApprovedAt once the card columns are filled). The ID
    // order sorts by the claim alone.
    private IQueryable<ReturnSortKey> ReturnSortKeys(IQueryable<ReturnClaim> claims, Guid tenantId, string sort, bool columnsFresh)
    {
        if (sort == "ID") return claims.Select(x => new ReturnSortKey { Id = x.Id, Date = null });
        if (columnsFresh && sort.StartsWith("APPROVED", StringComparison.Ordinal)) return claims.Select(x => new ReturnSortKey { Id = x.Id, Date = x.ApprovedAt });
        if (sort.StartsWith("REQUESTED", StringComparison.Ordinal)) return claims.Select(x => new ReturnSortKey { Id = x.Id, Date = x.RequestedAt });
        if (sort.StartsWith("ORDERED", StringComparison.Ordinal))
            return claims.Select(x => new ReturnSortKey
            {
                Id = x.Id,
                Date = db.Orders.Where(order => order.TenantId == tenantId && order.Id == x.OrderId).Select(order => (DateTimeOffset?)order.OrderedAt).FirstOrDefault()
            });
        return claims.Select(x => new ReturnSortKey
        {
            Id = x.Id,
            Date = x.Status == ReturnClaimStatus.Approved || x.Status == ReturnClaimStatus.Completed
                ? db.ReturnDecisions.Where(decision => decision.TenantId == tenantId && decision.ClaimId == x.Id && decision.Action == "APPROVE" && decision.Status == "SUCCEEDED")
                    .Max(decision => decision.CompletedAt) ?? x.LastRemoteModifiedAt
                : null
        });
    }

    // CompareReturnCards as database order: empty dates last in either direction, then the claim id (a uuid orders as its
    // text does).
    private static IQueryable<ReturnSortKey> OrderReturnSortKeys(IQueryable<ReturnSortKey> keys, string sort) =>
        sort == "ID" ? keys.OrderBy(x => x.Id)
        : sort.EndsWith("_DESC", StringComparison.Ordinal) ? keys.OrderBy(x => x.Date == null).ThenByDescending(x => x.Date).ThenBy(x => x.Id)
        : keys.OrderBy(x => x.Date == null).ThenBy(x => x.Date).ThenBy(x => x.Id);

    // The claims ordered before (date, claim id) by OrderReturnSortKeys.
    private static IQueryable<ReturnSortKey> ReturnSortKeysBefore(IQueryable<ReturnSortKey> keys, DateTimeOffset? date, Guid claimId, string sort)
    {
        if (sort == "ID") return keys.Where(x => x.Id.CompareTo(claimId) < 0);
        if (date is not { } value) return keys.Where(x => x.Date != null || x.Id.CompareTo(claimId) < 0);
        var at = value.ToUniversalTime();
        return sort.EndsWith("_DESC", StringComparison.Ordinal)
            ? keys.Where(x => x.Date != null && (x.Date > at || x.Date == at && x.Id.CompareTo(claimId) < 0))
            : keys.Where(x => x.Date != null && (x.Date < at || x.Date == at && x.Id.CompareTo(claimId) < 0));
    }

    private sealed class ReturnSortKeyPlace
    {
        public Guid Id { get; init; }
        public DateTimeOffset? Date { get; init; }
        public int Before { get; init; }
        public bool Listed { get; init; }
    }

    // For each of the targets: how many keys of other claims ReturnSortKeysBefore counts before it, and whether its own
    // claim is among the keys.
    private static IQueryable<ReturnSortKeyPlace> ReturnSortKeysBeforeEach(IQueryable<ReturnSortKey> keys, IQueryable<ReturnSortKey> targets, string sort)
    {
        if (sort == "ID")
            return targets.Select(target => new ReturnSortKeyPlace
            {
                Id = target.Id,
                Date = target.Date,
                Before = keys.Count(x => x.Id != target.Id && x.Id.CompareTo(target.Id) < 0),
                Listed = keys.Any(x => x.Id == target.Id)
            });
        if (sort.EndsWith("_DESC", StringComparison.Ordinal))
            return targets.Select(target => new ReturnSortKeyPlace
            {
                Id = target.Id,
                Date = target.Date,
                Before = keys.Count(x => x.Id != target.Id && (target.Date == null
                    ? x.Date != null || x.Id.CompareTo(target.Id) < 0
                    : x.Date != null && (x.Date > target.Date || x.Date == target.Date && x.Id.CompareTo(target.Id) < 0))),
                Listed = keys.Any(x => x.Id == target.Id)
            });
        return targets.Select(target => new ReturnSortKeyPlace
        {
            Id = target.Id,
            Date = target.Date,
            Before = keys.Count(x => x.Id != target.Id && (target.Date == null
                ? x.Date != null || x.Id.CompareTo(target.Id) < 0
                : x.Date != null && (x.Date < target.Date || x.Date == target.Date && x.Id.CompareTo(target.Id) < 0))),
            Listed = keys.Any(x => x.Id == target.Id)
        });
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
            ReturnAmounts = ReturnMarketplaceAmounts.Total(lines.Select(line => line.ReturnAmounts).ToList()),
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

    internal async Task<ReturnSummaryView> BuildReturnSummaryAsync(Guid tenantId, ReturnListQuery options, Dictionary<string, int> counts, CancellationToken cancellationToken)
    {
        var columns = await ReturnColumnStateAsync(tenantId, cancellationToken);
        var tab = options.Status?.Trim().ToUpperInvariant();
        var refined = options.Platforms?.Any(value => !string.IsNullOrWhiteSpace(value)) == true
            || options.Cargo is { Length: > 0 } and not "ALL" || options.ReasonKey is { Length: > 0 } and not "ALL" || options.InvoiceStatus is { Length: > 0 } and not "ALL";
        var everyTab = string.IsNullOrEmpty(tab) || tab == "ALL";

        // SIPARIS-STOK-9: with the card columns filled and nothing decided card by card, the tab counts and the metric cards
        // are counted by the database; only the selected tab's claims are read, for its filter choices.
        ReturnCardScan? counted = null;
        ReturnMetricCounts metrics;
        if (columns.Fresh && !everyTab && !ReturnCardsFilteredInMemory(options))
            metrics = await CountReturnCardsInDatabaseAsync(tenantId, options, counts, columns, cancellationToken);
        else
        {
            counted = await ScanReturnCardsAsync(tenantId, options, applyTab: false, applyRefinements: true, columns, cancellationToken);
            foreach (var card in counted.Cards)
            {
                counts["ALL"]++;
                counts[ReturnTabGroup(card.Status)]++;
            }
            metrics = await ReturnMetricsAsync(tenantId, counted, cancellationToken);
        }

        var listed = everyTab && !refined && counted is not null
            ? counted
            : await ScanReturnCardsAsync(tenantId, options, applyTab: true, applyRefinements: false, columns, cancellationToken);

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
        // Each choice's count: the cards of the tab under every other filter, the filter itself left out.
        var invoice = options.InvoiceStatus?.Trim().ToUpperInvariant();
        bool InvoiceMatches(ReturnCard card) => string.IsNullOrEmpty(invoice) || invoice == "ALL" || labels.GetValueOrDefault(card.ClaimId) == invoice;
        bool Matches(ReturnCard card, ReturnListQuery refinements) => MatchesReturnRefinements(card.PlatformCode, card.CargoProviderName, card.ReasonCode, card.ReasonText, refinements);
        Dictionary<string, int> CountBy(Func<ReturnCard, bool> keep, Func<ReturnCard, string?> key) => listed.Cards.Where(keep)
            .Select(key).Where(value => !string.IsNullOrEmpty(value)).GroupBy(value => value!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var platformCounts = CountBy(card => Matches(card, options with { Platforms = null }) && InvoiceMatches(card), card => card.PlatformCode);
        var cargoCounts = CountBy(card => Matches(card, options with { Cargo = null }) && InvoiceMatches(card), card => card.CargoProviderName?.Trim());
        var reasonCounts = CountBy(card => Matches(card, options with { ReasonKey = null }) && InvoiceMatches(card), card => ReturnReasonKey(card.ReasonCode, card.ReasonText));
        var invoiceCounts = CountBy(card => Matches(card, options), card => labels.GetValueOrDefault(card.ClaimId));
        return new(counts, platforms.Select(code => new ReturnPlatformOption(code, platformNames[code])).ToList(), cargo,
            reasons.Select(reason => reasonSources[reason]).ToList(), invoiceStatuses, listed.Cards.Count, platformCounts, cargoCounts, reasonCounts, invoiceCounts, metrics);
    }

    // The tab counts and ReturnMetricsAsync of every card the filter keeps (the tab aside), counted by the database from the
    // card columns. Without a card-by-card filter every claim is at least one card: its first card in its TabGroup, and a
    // claim that splits (only CardSplit claims can; their lines decide) has an accepted card more, under Onaylanan. A
    // rejected card is its claim's first card (TabGroup REJECTED), and every claim has a card for the decisions of today.
    private async Task<ReturnMetricCounts> CountReturnCardsInDatabaseAsync(Guid tenantId, ReturnListQuery options, Dictionary<string, int> counts, ReturnColumnState columns, CancellationToken cancellationToken)
    {
        var query = ReturnBaseQuery(tenantId);
        ApplyReturnFilters(ref query, options with { Status = null });
        foreach (var group in await query.GroupBy(x => x.TabGroup).Select(group => new { group.Key, Count = group.Count() }).ToListAsync(cancellationToken))
        {
            counts["ALL"] += group.Count;
            counts[group.Key!] += group.Count;
        }
        var splitRows = await query.Where(x => x.CardSplit == true).OrderBy(x => x.Id)
            .Select(x => new ReturnClaimRow(x.Id, x.Status, x.OrderId, x.ConnectionId, x.ExternalClaimId, x.CargoProviderName, x.ReasonCode, x.ReasonText, x.LastRemoteModifiedAt, x.RejectedPackageState, x.DecidedAt, x.RequestedAt, x.CardSplit))
            .ToListAsync(cancellationToken);
        var accepted = (await ReturnCardsAsync(tenantId, splitRows, null, withLineIds: false, columns.Fresh, cancellationToken)).Count(card => card.Kind == ApprovedCard);
        counts["ALL"] += accepted;
        counts["APPROVED"] += accepted;

        var dayStart = IstanbulDayStart(timeProvider.GetUtcNow());
        var metrics = await query.GroupBy(_ => 1).Select(group => new
        {
            AwaitingCargo = group.Count(x => x.Status == ReturnClaimStatus.Rejected && x.RejectedPackageState == ReturnRejectedPackageStates.AwaitingCargo),
            UnknownCargo = group.Count(x => x.Status == ReturnClaimStatus.Rejected && x.RejectedPackageState == null),
            ApprovedToday = group.Count(x => (x.Status == ReturnClaimStatus.Approved || x.Status == ReturnClaimStatus.Completed)
                && (x.DecidedAt >= dayStart || db.ReturnDecisions.Any(decision => decision.TenantId == tenantId && decision.ClaimId == x.Id && decision.Status == "SUCCEEDED" && decision.Action == "APPROVE" && decision.CompletedAt >= dayStart))),
            RejectedToday = group.Count(x => x.Status == ReturnClaimStatus.Rejected
                && (x.DecidedAt >= dayStart || db.ReturnDecisions.Any(decision => decision.TenantId == tenantId && decision.ClaimId == x.Id && decision.Status == "SUCCEEDED" && decision.Action == "REJECT" && decision.CompletedAt >= dayStart)))
        }).SingleOrDefaultAsync(cancellationToken);
        return metrics is null ? new(0, 0, 0, 0) : new(metrics.AwaitingCargo, metrics.UnknownCargo, metrics.ApprovedToday, metrics.RejectedToday);
    }

    // 00:00 today in Istanbul (UTC+3 all year), as an instant.
    internal static DateTimeOffset IstanbulDayStart(DateTimeOffset now)
    {
        var offset = TimeSpan.FromHours(3);
        return new DateTimeOffset(now.ToOffset(offset).Date, offset).ToUniversalTime();
    }

    // A rejected card counts as waiting for cargo only when the marketplace said its package back to the customer exists
    // and has no tracking link yet; a rejected card without a known package state is counted apart, never as waiting.
    internal static (bool AwaitingCargo, bool Unknown) RejectedCargoState(ReturnClaimStatus status, string? rejectedPackageState) =>
        status != ReturnClaimStatus.Rejected ? (false, false)
        : (rejectedPackageState == ReturnRejectedPackageStates.AwaitingCargo, rejectedPackageState is null);

    // The metric cards over the counted cards (every filter but the tab). Today's decisions: the marketplace's own date
    // (DecidedAt) or a panel decision confirmed today, the claim still standing in that outcome.
    internal async Task<ReturnMetricCounts> ReturnMetricsAsync(Guid tenantId, ReturnCardScan counted, CancellationToken cancellationToken)
    {
        var awaitingCargo = 0; var unknownCargo = 0;
        foreach (var card in counted.Cards)
        {
            if (ReturnTabGroup(card.Status) != "REJECTED" || !counted.Claims.TryGetValue(card.ClaimId, out var row)) continue;
            var (awaiting, unknown) = RejectedCargoState(row.Status, row.RejectedPackageState);
            if (awaiting) awaitingCargo++;
            if (unknown) unknownCargo++;
        }
        var dayStart = IstanbulDayStart(timeProvider.GetUtcNow());
        var cardClaims = counted.Cards.Select(card => card.ClaimId).ToHashSet();
        bool IsApproved(ReturnClaimRow row) => row.Status is ReturnClaimStatus.Approved or ReturnClaimStatus.Completed;
        var approved = new HashSet<Guid>(); var rejected = new HashSet<Guid>();
        foreach (var row in counted.Claims.Values)
        {
            if (!cardClaims.Contains(row.Id) || row.DecidedAt is not { } decidedAt || decidedAt < dayStart) continue;
            if (IsApproved(row)) approved.Add(row.Id);
            else if (row.Status == ReturnClaimStatus.Rejected) rejected.Add(row.Id);
        }
        if (cardClaims.Count > 0)
        {
            var decisions = await db.ReturnDecisions.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.Status == "SUCCEEDED" && x.CompletedAt >= dayStart && (x.Action == "APPROVE" || x.Action == "REJECT"))
                .Select(x => new { x.ClaimId, x.Action })
                .ToListAsync(cancellationToken);
            foreach (var decision in decisions)
            {
                if (!cardClaims.Contains(decision.ClaimId) || !counted.Claims.TryGetValue(decision.ClaimId, out var row)) continue;
                if (decision.Action == "APPROVE" && IsApproved(row)) approved.Add(row.Id);
                else if (decision.Action == "REJECT" && row.Status == ReturnClaimStatus.Rejected) rejected.Add(row.Id);
            }
        }
        return new(awaitingCargo, unknownCargo, approved.Count, rejected.Count);
    }
}
