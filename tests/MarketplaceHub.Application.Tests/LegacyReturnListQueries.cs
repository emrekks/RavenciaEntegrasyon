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

    internal static async Task<MarketplaceSalesService.ReturnCardScan> ScanReturnCardsAsync(MarketplaceSalesService service, AppDbContext db, Guid tenantId, ReturnListQuery options, bool applyTab, bool applyRefinements, CancellationToken cancellationToken)
    {
        var query = service.ReturnBaseQuery(tenantId);
        service.ApplyReturnFilters(ref query, applyTab ? options : options with { Status = null });
        if (applyRefinements) service.ApplyReturnRefinements(ref query, tenantId, options);
        var rows = await query.OrderBy(x => x.Id)
            .Select(x => new MarketplaceSalesService.ReturnClaimRow(x.Id, x.Status, x.OrderId, x.ConnectionId, x.ExternalClaimId, x.CargoProviderName, x.ReasonCode, x.ReasonText, x.LastRemoteModifiedAt, x.RejectedPackageState, x.DecidedAt, x.RequestedAt))
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

        var cards = new List<MarketplaceSalesService.ReturnCard>();
        foreach (var row in rows)
        {
            var connection = connections.GetValueOrDefault(row.ConnectionId);
            var platformCode = connection?.PlatformCode ?? "TRENDYOL";
            if (applyRefinements && !MarketplaceSalesService.MatchesReturnRefinements(platformCode, row.CargoProviderName, row.ReasonCode, row.ReasonText, options)) continue;
            var claimLines = linesByClaim[row.Id].ToList();
            var statuses = claimLines.Where(x => !string.IsNullOrWhiteSpace(x.RawStatus)).GroupBy(x => x.OrderLineId)
                .ToDictionary(group => group.Key, group => Wire(MarketplaceReturnStatus.Canonicalize(group.First().RawStatus)));
            var status = MarketplaceSalesService.ReturnStatusWire(row.Status, claimLines.Select(x => x.RawStatus).ToList());
            foreach (var (kind, cardLines) in MarketplaceSalesService.SplitReturnClaim(claimLines.Where(x => x.HasOrderLine).Select(x => x.OrderLineId).ToList(), statuses))
                cards.Add(new(row.Id, kind, kind == MarketplaceSalesService.ApprovedCard ? "APPROVED" : status, platformCode, connection?.DisplayName ?? "Trendyol", row.CargoProviderName, row.ReasonCode, row.ReasonText, cardLines));
        }

        var tab = applyTab ? options.Status?.Trim().ToUpperInvariant() : null;
        if (!string.IsNullOrEmpty(tab) && tab != "ALL")
            cards = cards.Where(card => MarketplaceSalesService.ReturnTabGroup(card.Status) == tab).ToList();

        IReadOnlyDictionary<Guid, ReturnListView>? views = null;
        if (!string.IsNullOrWhiteSpace(options.Search) && cards.Count > 0)
        {
            views = await service.ReturnViewsByIdAsync(tenantId, cards.Select(card => card.ClaimId).Distinct().ToArray(), cancellationToken);
            cards = cards.Where(card => views.TryGetValue(card.ClaimId, out var view) && MarketplaceSalesService.MatchesReturnSearch(view, card.LineIds, options.Search!)).ToList();
        }

        var invoice = applyRefinements ? options.InvoiceStatus?.Trim().ToUpperInvariant() : null;
        if (!string.IsNullOrEmpty(invoice) && invoice != "ALL" && cards.Count > 0)
        {
            var labels = await service.ReturnInvoiceLabelsAsync(tenantId, cards.Select(card => claims[card.ClaimId]).Distinct().ToList(), cancellationToken);
            cards = cards.Where(card => labels.GetValueOrDefault(card.ClaimId) == invoice).ToList();
        }
        return new(cards, claims, views);
    }

    internal static async Task<PageResult<ReturnListView>> ReturnCardPageAsync(MarketplaceSalesService service, AppDbContext db, CursorCodec cursors, Guid tenantId, int limit, int page, string? after, ReturnListQuery options, CancellationToken cancellationToken)
    {
        var sort = MarketplaceSalesService.NormalizeReturnSort(options.Sort);
        var scan = await ScanReturnCardsAsync(service, db, tenantId, options, applyTab: true, applyRefinements: true, cancellationToken);
        var dates = sort == "ID" ? null : await service.ReturnSortDatesAsync(tenantId, scan.Cards.Select(card => scan.Claims[card.ClaimId]).Distinct().ToList(), sort, cancellationToken);
        var ordered = scan.Cards.Select(card => (Card: card, Key: (Date: dates?.GetValueOrDefault(card.ClaimId), card.ClaimId, card.Kind))).ToList();
        ordered.Sort((left, right) => MarketplaceSalesService.CompareReturnCards(left.Key, right.Key, sort));

        int start;
        if (!string.IsNullOrWhiteSpace(after))
        {
            if (!cursors.TryDecodeReturn(after, out var cursorSort, out var cursorDate, out var cursorClaimId, out var cursorKind) || cursorSort != sort)
                throw new ArgumentException("Cursor geçersiz veya seçili sıralamayla uyuşmuyor.", nameof(after));
            start = ordered.FindIndex(item => MarketplaceSalesService.CompareReturnCards(item.Key, (cursorDate, cursorClaimId, cursorKind), sort) > 0);
            if (start < 0) start = ordered.Count;
        }
        else start = page > 1 ? (int)Math.Min(ordered.Count, (long)(page - 1) * limit) : 0;

        var pageCards = ordered.Skip(start).Take(limit).ToList();
        var views = scan.Views ?? await service.ReturnViewsByIdAsync(tenantId, pageCards.Select(item => item.Card.ClaimId).Distinct().ToArray(), cancellationToken);
        var items = pageCards.Where(item => views.ContainsKey(item.Card.ClaimId)).Select(item => MarketplaceSalesService.ReturnCardView(views[item.Card.ClaimId], item.Card)).ToList();
        var hasMore = start + pageCards.Count < ordered.Count;
        var last = pageCards.LastOrDefault();
        var next = hasMore && pageCards.Count > 0 ? cursors.EncodeReturn(sort, last.Key.Date, last.Key.ClaimId, last.Key.Kind) : null;
        return new(items, next, hasMore, ordered.Count);
    }

    internal static async Task<ReturnSummaryView> BuildReturnSummaryAsync(MarketplaceSalesService service, AppDbContext db, Guid tenantId, ReturnListQuery options, CancellationToken cancellationToken)
    {
        var counts = MarketplaceSalesService.ReturnTabs.ToDictionary(tab => tab, _ => 0, StringComparer.Ordinal);
        var counted = await ScanReturnCardsAsync(service, db, tenantId, options, applyTab: false, applyRefinements: true, cancellationToken);
        foreach (var card in counted.Cards)
        {
            counts["ALL"]++;
            counts[MarketplaceSalesService.ReturnTabGroup(card.Status)]++;
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
            var reason = MarketplaceSalesService.ReturnReasonKey(card.ReasonCode, card.ReasonText);
            if (reason.Length == 0) continue;
            if (!reasonSources.ContainsKey(reason)) reasons.Add(reason);
            reasonSources[reason] = new(reason, card.ReasonCode, card.ReasonText);
        }
        var labels = listed.Cards.Count == 0
            ? new Dictionary<Guid, string>()
            : await service.ReturnInvoiceLabelsAsync(tenantId, listed.Cards.Select(card => listed.Claims[card.ClaimId]).Distinct().ToList(), cancellationToken);
        var invoiceStatuses = listed.Cards.Select(card => labels.GetValueOrDefault(card.ClaimId)).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        var invoice = options.InvoiceStatus?.Trim().ToUpperInvariant();
        bool InvoiceMatches(MarketplaceSalesService.ReturnCard card) => string.IsNullOrEmpty(invoice) || invoice == "ALL" || labels.GetValueOrDefault(card.ClaimId) == invoice;
        bool Matches(MarketplaceSalesService.ReturnCard card, ReturnListQuery refinements) => MarketplaceSalesService.MatchesReturnRefinements(card.PlatformCode, card.CargoProviderName, card.ReasonCode, card.ReasonText, refinements);
        Dictionary<string, int> CountBy(Func<MarketplaceSalesService.ReturnCard, bool> keep, Func<MarketplaceSalesService.ReturnCard, string?> key) => listed.Cards.Where(keep)
            .Select(key).Where(value => !string.IsNullOrEmpty(value)).GroupBy(value => value!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var platformCounts = CountBy(card => Matches(card, options with { Platforms = null }) && InvoiceMatches(card), card => card.PlatformCode);
        var cargoCounts = CountBy(card => Matches(card, options with { Cargo = null }) && InvoiceMatches(card), card => card.CargoProviderName?.Trim());
        var reasonCounts = CountBy(card => Matches(card, options with { ReasonKey = null }) && InvoiceMatches(card), card => MarketplaceSalesService.ReturnReasonKey(card.ReasonCode, card.ReasonText));
        var invoiceCounts = CountBy(card => Matches(card, options), card => labels.GetValueOrDefault(card.ClaimId));
        var metrics = await service.ReturnMetricsAsync(tenantId, counted, cancellationToken);
        return new(counts, platforms.Select(code => new ReturnPlatformOption(code, platformNames[code])).ToList(), cargo,
            reasons.Select(reason => reasonSources[reason]).ToList(), invoiceStatuses, listed.Cards.Count, platformCounts, cargoCounts, reasonCounts, invoiceCounts, metrics);
    }
}
