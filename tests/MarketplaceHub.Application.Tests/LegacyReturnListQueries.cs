using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Application.Tests;

// The Returns page's card scan, page and summary as they were before 15 Nov 2026 (audit SIPARIS-STOK-9): every claim of
// the filter and every line of those claims read and split, sorted and paged in memory. Kept only so
// ReturnListEquivalencePostgreSqlTests can prove that the rewrite (lines read only for claims that may split, the
// unfiltered page counted, ordered and paged by the database) gives the same pages, cursors and counts on the same data.
internal static class LegacyReturnListQueries
{
    private static string Wire<T>(T value) where T : Enum => string.Concat(value.ToString().Select((ch, index) => char.IsUpper(ch) && index > 0 ? "_" + ch : ch.ToString())).ToUpperInvariant();

    internal static async Task<ReturnQueryService.ReturnCardScan> ScanReturnCardsAsync(MarketplaceSalesService service, AppDbContext db, Guid tenantId, ReturnListQuery options, bool applyTab, bool applyRefinements, CancellationToken cancellationToken)
    {
        var query = service.ReturnQueries.ReturnBaseQuery(tenantId);
        service.ReturnQueries.ApplyReturnFilters(ref query, applyTab ? options : options with { Status = null });
        if (applyRefinements) service.ReturnQueries.ApplyReturnRefinements(ref query, tenantId, options);
        var rows = await query.OrderBy(x => x.Id)
            .Select(x => new ReturnQueryService.ReturnClaimRow(x.Id, x.Status, x.OrderId, x.ConnectionId, x.ExternalClaimId, x.CargoProviderName, x.ReasonCode, x.ReasonText, x.LastRemoteModifiedAt, x.RejectedPackageState, x.DecidedAt, x.RequestedAt))
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
        var referencedLineIds = lines.Select(x => x.OrderLineId).Distinct().ToArray();
        var existingLineIds = (await db.OrderLines.AsNoTracking().Where(x => x.TenantId == tenantId && referencedLineIds.Contains(x.Id))
            .Select(x => x.Id).ToListAsync(cancellationToken)).ToHashSet();
        var linesByClaim = lines.Select(x => new { x.ClaimId, x.OrderLineId, x.RawStatus, HasOrderLine = existingLineIds.Contains(x.OrderLineId) }).ToLookup(x => x.ClaimId);

        var cards = new List<ReturnQueryService.ReturnCard>();
        foreach (var row in rows)
        {
            var connection = connections.GetValueOrDefault(row.ConnectionId);
            var platformCode = connection?.PlatformCode ?? "TRENDYOL";
            if (applyRefinements && !ReturnQueryService.MatchesReturnRefinements(platformCode, row.CargoProviderName, row.ReasonCode, row.ReasonText, options)) continue;
            var claimLines = linesByClaim[row.Id].ToList();
            var statuses = claimLines.Where(x => !string.IsNullOrWhiteSpace(x.RawStatus)).GroupBy(x => x.OrderLineId)
                .ToDictionary(group => group.Key, group => Wire(MarketplaceReturnStatus.Canonicalize(group.First().RawStatus)));
            var status = ReturnQueryService.ReturnStatusWire(row.Status, claimLines.Select(x => x.RawStatus).ToList());
            foreach (var (kind, cardLines) in ReturnQueryService.SplitReturnClaim(claimLines.Where(x => x.HasOrderLine).Select(x => x.OrderLineId).ToList(), statuses, row.Status))
                cards.Add(new(row.Id, kind, kind == ReturnQueryService.ApprovedCard ? "APPROVED" : status, platformCode, connection?.DisplayName ?? "Trendyol", row.CargoProviderName, row.ReasonCode, row.ReasonText, cardLines));
        }

        var tab = applyTab ? options.Status?.Trim().ToUpperInvariant() : null;
        if (!string.IsNullOrEmpty(tab) && tab != "ALL")
            cards = cards.Where(card => ReturnQueryService.ReturnTabGroup(card.Status) == tab).ToList();

        IReadOnlyDictionary<Guid, ReturnListView>? views = null;
        if (!string.IsNullOrWhiteSpace(options.Search) && cards.Count > 0)
        {
            views = await service.ReturnQueries.ReturnViewsByIdAsync(tenantId, cards.Select(card => card.ClaimId).Distinct().ToArray(), cancellationToken);
            cards = cards.Where(card => views.TryGetValue(card.ClaimId, out var view) && ReturnQueryService.MatchesReturnSearch(view, card.LineIds, options.Search!)).ToList();
        }

        var invoice = applyRefinements ? options.InvoiceStatus?.Trim().ToUpperInvariant() : null;
        if (!string.IsNullOrEmpty(invoice) && invoice != "ALL" && cards.Count > 0)
        {
            var labels = await ReturnInvoiceLabelsAsync(db, tenantId, cards.Select(card => claims[card.ClaimId]).Distinct().ToList(), cancellationToken);
            cards = cards.Where(card => labels.GetValueOrDefault(card.ClaimId) == invoice).ToList();
        }
        return new(cards, claims, views);
    }

    // ReturnQueryService.ReturnInvoiceLabelsAsync before 16 Nov 2026 (SIPARIS-STOK-9): the order's latest package and
    // invoice read and the label computed in memory, the customer snapshot only when the label falls back on it. Ties of
    // date had no order of their own (the rows came back unordered); this copy reads them by id, newest first, as the
    // database label and the views do now.
    internal static async Task<Dictionary<Guid, string>> ReturnInvoiceLabelsAsync(AppDbContext db, Guid tenantId, IReadOnlyCollection<ReturnQueryService.ReturnClaimRow> claims, CancellationToken cancellationToken)
    {
        var orderIds = claims.Select(x => x.OrderId).Distinct().ToArray();
        var existingOrders = (await db.Orders.AsNoTracking().Where(x => x.TenantId == tenantId && orderIds.Contains(x.Id)).Select(x => x.Id).ToListAsync(cancellationToken)).ToHashSet();
        var packages = (await db.ShipmentPackages.AsNoTracking().Where(x => x.TenantId == tenantId && orderIds.Contains(x.OrderId)).OrderByDescending(x => x.Id)
                .Select(x => new { x.OrderId, x.StatusOccurredAt, x.MarketplaceInvoiceStatus, x.RawStatus }).ToListAsync(cancellationToken))
            .GroupBy(x => x.OrderId).ToDictionary(group => group.Key, group => group.OrderByDescending(x => x.StatusOccurredAt).First());
        var invoices = (await db.Invoices.AsNoTracking().Where(x => x.TenantId == tenantId && orderIds.Contains(x.OrderId) && x.OriginalInvoiceId == null
                    && db.PlatformConnections.Any(connection => connection.TenantId == tenantId && connection.Id == x.ProviderConnectionId && (connection.Status == "ACTIVE" || connection.Status == "VERIFIED")))
                .OrderByDescending(x => x.Id)
                .Select(x => new { x.OrderId, x.CreatedAt, x.Status, x.InvoiceNumber }).ToListAsync(cancellationToken))
            .GroupBy(x => x.OrderId).ToDictionary(group => group.Key, group => group.OrderByDescending(x => x.CreatedAt).First());
        var snapshotOrderIds = orderIds.Where(id => existingOrders.Contains(id) && !invoices.ContainsKey(id)
            && ReturnQueryService.ReturnInvoiceLabelReadsSnapshot(null, packages.GetValueOrDefault(id)?.MarketplaceInvoiceStatus ?? MarketplaceInvoiceStatus.Unknown, packages.GetValueOrDefault(id)?.RawStatus)).ToArray();
        var snapshots = snapshotOrderIds.Length == 0
            ? new Dictionary<Guid, string>()
            : await db.Orders.AsNoTracking().Where(x => x.TenantId == tenantId && snapshotOrderIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.CustomerSnapshotJson, cancellationToken);
        var labels = new Dictionary<Guid, string>();
        foreach (var claim in claims)
        {
            if (!existingOrders.Contains(claim.OrderId)) { labels[claim.Id] = "FATURA_BEKLIYOR"; continue; }
            var package = packages.GetValueOrDefault(claim.OrderId);
            var invoice = invoices.GetValueOrDefault(claim.OrderId);
            labels[claim.Id] = InvoiceLabelPolicy.InvoiceLabel(invoice?.Status, invoice?.InvoiceNumber, package?.MarketplaceInvoiceStatus ?? MarketplaceInvoiceStatus.Unknown,
                snapshots.GetValueOrDefault(claim.OrderId) ?? "{}", package is null ? [] : [package.RawStatus]);
        }
        return labels;
    }

    internal static async Task<PageResult<ReturnListView>> ReturnCardPageAsync(MarketplaceSalesService service, AppDbContext db, CursorCodec cursors, Guid tenantId, int limit, int page, string? after, ReturnListQuery options, CancellationToken cancellationToken)
    {
        var sort = ReturnQueryService.NormalizeReturnSort(options.Sort);
        var scan = await ScanReturnCardsAsync(service, db, tenantId, options, applyTab: true, applyRefinements: true, cancellationToken);
        var dates = sort == "ID" ? null : await service.ReturnQueries.ReturnSortDatesAsync(tenantId, scan.Cards.Select(card => scan.Claims[card.ClaimId]).Distinct().ToList(), sort, cancellationToken);
        var ordered = scan.Cards.Select(card => (Card: card, Key: (Date: dates?.GetValueOrDefault(card.ClaimId), card.ClaimId, card.Kind))).ToList();
        ordered.Sort((left, right) => ReturnQueryService.CompareReturnCards(left.Key, right.Key, sort));

        int start;
        if (!string.IsNullOrWhiteSpace(after))
        {
            if (!cursors.TryDecodeReturn(after, out var cursorSort, out var cursorDate, out var cursorClaimId, out var cursorKind) || cursorSort != sort)
                throw new ArgumentException("Cursor geçersiz veya seçili sıralamayla uyuşmuyor.", nameof(after));
            start = ordered.FindIndex(item => ReturnQueryService.CompareReturnCards(item.Key, (cursorDate, cursorClaimId, cursorKind), sort) > 0);
            if (start < 0) start = ordered.Count;
        }
        else start = page > 1 ? (int)Math.Min(ordered.Count, (long)(page - 1) * limit) : 0;

        var pageCards = ordered.Skip(start).Take(limit).ToList();
        var views = scan.Views ?? await service.ReturnQueries.ReturnViewsByIdAsync(tenantId, pageCards.Select(item => item.Card.ClaimId).Distinct().ToArray(), cancellationToken);
        var items = pageCards.Where(item => views.ContainsKey(item.Card.ClaimId)).Select(item => ReturnQueryService.ReturnCardView(views[item.Card.ClaimId], item.Card)).ToList();
        var hasMore = start + pageCards.Count < ordered.Count;
        var last = pageCards.LastOrDefault();
        var next = hasMore && pageCards.Count > 0 ? cursors.EncodeReturn(sort, last.Key.Date, last.Key.ClaimId, last.Key.Kind) : null;
        return new(items, next, hasMore, ordered.Count);
    }

    internal static async Task<ReturnSummaryView> BuildReturnSummaryAsync(MarketplaceSalesService service, AppDbContext db, Guid tenantId, ReturnListQuery options, CancellationToken cancellationToken)
    {
        var counts = ReturnQueryService.ReturnTabs.ToDictionary(tab => tab, _ => 0, StringComparer.Ordinal);
        var counted = await ScanReturnCardsAsync(service, db, tenantId, options, applyTab: false, applyRefinements: true, cancellationToken);
        foreach (var card in counted.Cards)
        {
            counts["ALL"]++;
            counts[ReturnQueryService.ReturnTabGroup(card.Status)]++;
        }

        var tab = options.Status?.Trim().ToUpperInvariant();
        var refined = options.Platforms?.Any(value => !string.IsNullOrWhiteSpace(value)) == true
            || options.Cargo is { Length: > 0 } and not "ALL" || options.ReasonKey is { Length: > 0 } and not "ALL" || options.InvoiceStatus is { Length: > 0 } and not "ALL";
        var listed = (string.IsNullOrEmpty(tab) || tab == "ALL") && !refined
            ? counted
            : await ScanReturnCardsAsync(service, db, tenantId, options, applyTab: true, applyRefinements: false, cancellationToken);

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
            var reason = ReturnQueryService.ReturnReasonKey(card.ReasonCode, card.ReasonText);
            if (reason.Length == 0) continue;
            if (!reasonSources.ContainsKey(reason)) reasons.Add(reason);
            reasonSources[reason] = new(reason, card.ReasonCode, card.ReasonText);
        }
        var labels = listed.Cards.Count == 0
            ? new Dictionary<Guid, string>()
            : await ReturnInvoiceLabelsAsync(db, tenantId, listed.Cards.Select(card => listed.Claims[card.ClaimId]).Distinct().ToList(), cancellationToken);
        var invoiceStatuses = listed.Cards.Select(card => labels.GetValueOrDefault(card.ClaimId)).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        var invoice = options.InvoiceStatus?.Trim().ToUpperInvariant();
        bool InvoiceMatches(ReturnQueryService.ReturnCard card) => string.IsNullOrEmpty(invoice) || invoice == "ALL" || labels.GetValueOrDefault(card.ClaimId) == invoice;
        bool Matches(ReturnQueryService.ReturnCard card, ReturnListQuery refinements) => ReturnQueryService.MatchesReturnRefinements(card.PlatformCode, card.CargoProviderName, card.ReasonCode, card.ReasonText, refinements);
        Dictionary<string, int> CountBy(Func<ReturnQueryService.ReturnCard, bool> keep, Func<ReturnQueryService.ReturnCard, string?> key) => listed.Cards.Where(keep)
            .Select(key).Where(value => !string.IsNullOrEmpty(value)).GroupBy(value => value!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var platformCounts = CountBy(card => Matches(card, options with { Platforms = null }) && InvoiceMatches(card), card => card.PlatformCode);
        var cargoCounts = CountBy(card => Matches(card, options with { Cargo = null }) && InvoiceMatches(card), card => card.CargoProviderName?.Trim());
        var reasonCounts = CountBy(card => Matches(card, options with { ReasonKey = null }) && InvoiceMatches(card), card => ReturnQueryService.ReturnReasonKey(card.ReasonCode, card.ReasonText));
        var invoiceCounts = CountBy(card => Matches(card, options), card => labels.GetValueOrDefault(card.ClaimId));
        var metrics = await service.ReturnQueries.ReturnMetricsAsync(tenantId, counted, cancellationToken);
        return new(counts, platforms.Select(code => new ReturnPlatformOption(code, platformNames[code])).ToList(), cargo,
            reasons.Select(reason => reasonSources[reason]).ToList(), invoiceStatuses, listed.Cards.Count, platformCounts, cargoCounts, reasonCounts, invoiceCounts, metrics);
    }
}
