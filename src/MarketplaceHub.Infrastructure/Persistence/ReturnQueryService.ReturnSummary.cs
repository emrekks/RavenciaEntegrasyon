using System.Linq.Expressions;
using System.Text.RegularExpressions;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;
using static MarketplaceHub.Infrastructure.Persistence.InvoiceLabelPolicy;
using static MarketplaceHub.Infrastructure.Persistence.OrderSnapshotReader;
using static MarketplaceHub.Infrastructure.Persistence.SalesResults;

namespace MarketplaceHub.Infrastructure.Persistence;

// SIPARIS-STOK-9: the Returns page's summary (tab counts, metric cards, filter choices and their counts) counted by the
// database over the card sets (ReturnCardSetAsync), every tab and every filter alike. The choices keep the order in which
// the page's cards first show them (the cards are in claim id order) and a platform's name and a reason's code and text
// are those of its last card, as the card-by-card summary had them: each group sends back its count and its first and
// last claim id. The few accepted cards of claims that split are added from memory.
internal sealed partial class ReturnQueryService
{
    internal async Task<ReturnSummaryView> BuildReturnSummaryAsync(Guid tenantId, ReturnListQuery options, Dictionary<string, int> counts, CancellationToken cancellationToken)
    {
        var columns = await ReturnColumnStateAsync(tenantId, cancellationToken);
        var tab = options.Status?.Trim().ToUpperInvariant();
        var refined = options.Platforms?.Any(value => !string.IsNullOrWhiteSpace(value)) == true
            || options.Cargo is { Length: > 0 } and not "ALL" || options.ReasonKey is { Length: > 0 } and not "ALL" || options.InvoiceStatus is { Length: > 0 } and not "ALL";
        var everyTab = string.IsNullOrEmpty(tab) || tab == "ALL";
        var counted = await ReturnCardSetAsync(tenantId, options, applyTab: false, applyRefinements: true, columns, cancellationToken);
        if (counted is null) return await BuildReturnSummaryFromScanAsync(tenantId, options, counts, cancellationToken);
        var metrics = await CountReturnCardsAsync(tenantId, counted, counts, cancellationToken);
        var listed = everyTab && !refined ? counted : (await ReturnCardSetAsync(tenantId, options, applyTab: true, applyRefinements: false, columns, cancellationToken))!;

        var first = listed.First;
        var extras = listed.Extras;
        var extraLabels = await ReturnInvoiceLabelsAsync(tenantId, [.. extras.Select(card => listed.SplitRows[card.ClaimId]).Distinct()], cancellationToken);
        var invoice = options.InvoiceStatus?.Trim().ToUpperInvariant();
        var byInvoice = !string.IsNullOrEmpty(invoice) && invoice != "ALL";
        bool InvoiceMatches(ReturnCard card) => !byInvoice || extraLabels.GetValueOrDefault(card.ClaimId) == invoice;
        bool Matches(ReturnCard card, ReturnListQuery refinements) => MatchesReturnRefinements(card.PlatformCode, card.CargoProviderName, card.ReasonCode, card.ReasonText, refinements);
        async Task<IQueryable<ReturnClaim>> RefinedAsync(ReturnListQuery refinements, bool withInvoice)
        {
            var query = first;
            ApplyReturnRefinements(ref query, tenantId, refinements);
            return withInvoice && byInvoice ? await ApplyReturnInvoiceFilterAsync(query, tenantId, invoice!, cancellationToken) : query;
        }

        var connections = await db.PlatformConnections.AsNoTracking().Where(x => x.TenantId == tenantId)
            .Select(x => new { x.Id, x.PlatformCode, x.DisplayName }).ToDictionaryAsync(x => x.Id, cancellationToken);
        async Task<Dictionary<string, ReturnTally>> PlatformsAsync(IQueryable<ReturnClaim> claims, IEnumerable<ReturnCard> cards)
        {
            var tallies = new Dictionary<string, ReturnTally>(StringComparer.Ordinal);
            foreach (var group in await GroupReturnClaimsAsync(claims, claim => claim.ConnectionId, cancellationToken))
            {
                var connection = connections.GetValueOrDefault(group.Key);
                ReturnTally.Add(tallies, connection?.PlatformCode ?? "TRENDYOL", group.Count, group.First, group.Last, connection?.DisplayName ?? "Trendyol");
            }
            foreach (var card in cards) ReturnTally.Add(tallies, card.PlatformCode, 1, card.ClaimId.ToString("D"), card.ClaimId.ToString("D"), card.PlatformName);
            return tallies;
        }
        async Task<Dictionary<string, ReturnTally>> CargoAsync(IQueryable<ReturnClaim> claims, IEnumerable<ReturnCard> cards)
        {
            var tallies = new Dictionary<string, ReturnTally>(StringComparer.Ordinal);
            foreach (var group in await GroupReturnClaimsAsync(claims, TrimmedCargo, cancellationToken))
                if (!string.IsNullOrEmpty(group.Key)) ReturnTally.Add(tallies, group.Key, group.Count, group.First, group.Last, null);
            foreach (var card in cards)
                if (card.CargoProviderName?.Trim() is { Length: > 0 } provider) ReturnTally.Add(tallies, provider, 1, card.ClaimId.ToString("D"), card.ClaimId.ToString("D"), null);
            return tallies;
        }
        async Task<Dictionary<string, ReturnTally>> ReasonsAsync(IQueryable<ReturnClaim> claims, IEnumerable<ReturnCard> cards)
        {
            var tallies = new Dictionary<string, ReturnTally>(StringComparer.Ordinal);
            foreach (var group in await GroupReturnClaimsAsync(claims, ReasonKey, cancellationToken))
                if (!string.IsNullOrEmpty(group.Key)) ReturnTally.Add(tallies, group.Key, group.Count, group.First, group.Last, group.Last);
            foreach (var card in cards)
                if (ReturnReasonKey(card.ReasonCode, card.ReasonText) is { Length: > 0 } reason) ReturnTally.Add(tallies, reason, 1, card.ClaimId.ToString("D"), card.ClaimId.ToString("D"), card.ClaimId.ToString("D"));
            return tallies;
        }
        async Task<Dictionary<string, ReturnTally>> LabelsAsync(IQueryable<ReturnClaim> claims, IEnumerable<ReturnCard> cards)
        {
            var tallies = new Dictionary<string, ReturnTally>(StringComparer.Ordinal);
            foreach (var group in await GroupReturnClaimsAsync(claims, await ReturnInvoiceLabelAsync(claims, tenantId, cancellationToken), cancellationToken))
                if (!string.IsNullOrEmpty(group.Key)) ReturnTally.Add(tallies, group.Key, group.Count, group.First, group.Last, null);
            foreach (var card in cards)
                if (extraLabels.GetValueOrDefault(card.ClaimId) is { Length: > 0 } label) ReturnTally.Add(tallies, label, 1, card.ClaimId.ToString("D"), card.ClaimId.ToString("D"), null);
            return tallies;
        }

        var platforms = await PlatformsAsync(first, extras);
        var cargo = await CargoAsync(first, extras);
        var reasons = await ReasonsAsync(first, extras);
        var reasonClaimIds = reasons.Values.Select(tally => Guid.Parse(tally.LastPayload!)).ToArray();
        var reasonClaims = reasonClaimIds.Length == 0
            ? []
            : await db.ReturnClaims.AsNoTracking().Where(x => x.TenantId == tenantId && reasonClaimIds.Contains(x.Id))
                .Select(x => new { x.Id, x.ReasonCode, x.ReasonText }).ToDictionaryAsync(x => x.Id, cancellationToken);
        var invoiceStatuses = await LabelsAsync(first, extras);

        var platformCounts = await PlatformsAsync(await RefinedAsync(options with { Platforms = null }, true), extras.Where(card => Matches(card, options with { Platforms = null }) && InvoiceMatches(card)));
        var cargoCounts = await CargoAsync(await RefinedAsync(options with { Cargo = null }, true), extras.Where(card => Matches(card, options with { Cargo = null }) && InvoiceMatches(card)));
        var reasonCounts = await ReasonsAsync(await RefinedAsync(options with { ReasonKey = null }, true), extras.Where(card => Matches(card, options with { ReasonKey = null }) && InvoiceMatches(card)));
        var invoiceCounts = await LabelsAsync(await RefinedAsync(options, false), extras.Where(card => Matches(card, options)));
        var total = await first.CountAsync(cancellationToken) + extras.Count;

        return new(counts,
            [.. ReturnTally.Ordered(platforms).Select(pair => new ReturnPlatformOption(pair.Key, pair.Value.LastPayload!))],
            [.. ReturnTally.Ordered(cargo).Select(pair => pair.Key)],
            [.. ReturnTally.Ordered(reasons).Select(pair =>
            {
                var source = reasonClaims[Guid.Parse(pair.Value.LastPayload!)];
                return new ReturnReasonOption(pair.Key, source.ReasonCode, source.ReasonText);
            })],
            [.. ReturnTally.Ordered(invoiceStatuses).Select(pair => pair.Key)],
            total, ReturnTally.Counts(platformCounts), ReturnTally.Counts(cargoCounts), ReturnTally.Counts(reasonCounts), ReturnTally.Counts(invoiceCounts), metrics);
    }

    // The tab counts and the metric cards of the cards of the set (every tab). Each claim's first card stands in the tab of
    // its status; an accepted card under Onaylanan. A rejected card is its claim's first card, and today's decisions count
    // the claims with a card.
    internal async Task<ReturnMetricCounts> CountReturnCardsAsync(Guid tenantId, ReturnCardSet set, Dictionary<string, int> counts, CancellationToken cancellationToken)
    {
        foreach (var group in await set.First.GroupBy(x => x.Status).Select(group => new { group.Key, Count = group.Count() }).ToListAsync(cancellationToken))
        {
            counts["ALL"] += group.Count;
            counts[ReturnTabGroup(Wire(group.Key))] += group.Count;
        }
        foreach (var card in set.Extras)
        {
            counts["ALL"]++;
            counts[ReturnTabGroup(card.Status)]++;
        }

        var dayStart = IstanbulDayStart(timeProvider.GetUtcNow());
        var cargo = await set.First.GroupBy(_ => 1).Select(group => new
        {
            AwaitingCargo = group.Count(x => x.Status == ReturnClaimStatus.Rejected && x.RejectedPackageState == ReturnRejectedPackageStates.AwaitingCargo),
            UnknownCargo = group.Count(x => x.Status == ReturnClaimStatus.Rejected && x.RejectedPackageState == null)
        }).SingleOrDefaultAsync(cancellationToken);
        var today = await set.Claims.Where(set.AnyCard).GroupBy(_ => 1).Select(group => new
        {
            ApprovedToday = group.Count(x => (x.Status == ReturnClaimStatus.Approved || x.Status == ReturnClaimStatus.Completed)
                && (x.DecidedAt >= dayStart || db.ReturnDecisions.Any(decision => decision.TenantId == tenantId && decision.ClaimId == x.Id && decision.Status == "SUCCEEDED" && decision.Action == "APPROVE" && decision.CompletedAt >= dayStart))),
            RejectedToday = group.Count(x => x.Status == ReturnClaimStatus.Rejected
                && (x.DecidedAt >= dayStart || db.ReturnDecisions.Any(decision => decision.TenantId == tenantId && decision.ClaimId == x.Id && decision.Status == "SUCCEEDED" && decision.Action == "REJECT" && decision.CompletedAt >= dayStart)))
        }).SingleOrDefaultAsync(cancellationToken);
        return new(cargo?.AwaitingCargo ?? 0, cargo?.UnknownCargo ?? 0, today?.ApprovedToday ?? 0, today?.RejectedToday ?? 0);
    }

    // A claim's cargo provider and reason key as the summary reads them, trimmed of what .NET's Trim strips (as
    // ApplyReturnRefinements compares them).
    private static readonly Expression<Func<ReturnClaim, string?>> TrimmedCargo = claim =>
        claim.CargoProviderName == null ? null : Regex.Replace(Regex.Replace(claim.CargoProviderName, LeadingWhiteSpace, ""), TrailingWhiteSpace, "");

    private static readonly Expression<Func<ReturnClaim, string>> ReasonKey = claim =>
        claim.ReasonCode != null && Regex.Replace(Regex.Replace(claim.ReasonCode, LeadingWhiteSpace, ""), TrailingWhiteSpace, "") != ""
            ? Regex.Replace(Regex.Replace(claim.ReasonCode, LeadingWhiteSpace, ""), TrailingWhiteSpace, "")
            : Regex.Replace(Regex.Replace(claim.ReasonText ?? "", LeadingWhiteSpace, ""), TrailingWhiteSpace, "");

    internal sealed class ReturnKeyGroup<TKey>
    {
        public TKey Key { get; init; } = default!;
        public int Count { get; init; }
        public string First { get; init; } = "";
        public string Last { get; init; } = "";
    }

    // Each key's claim count and its first and last claim id in the order the database lists claims (a uuid orders as its
    // text, compared byte by byte).
    private static Task<List<ReturnKeyGroup<TKey>>> GroupReturnClaimsAsync<TKey>(IQueryable<ReturnClaim> claims, Expression<Func<ReturnClaim, TKey>> key, CancellationToken cancellationToken) =>
        claims.GroupBy(key).Select(group => new ReturnKeyGroup<TKey>
        {
            Key = group.Key,
            Count = group.Count(),
            First = group.Min(claim => EF.Functions.Collate(claim.Id.ToString(), "C"))!,
            Last = group.Max(claim => EF.Functions.Collate(claim.Id.ToString(), "C"))!
        }).ToListAsync(cancellationToken);

    // A choice's cards: how many, the first and last claim id among them (lower-case uuid text, compared ordinally), and
    // what the last one carries (a platform name, a reason's claim).
    private sealed class ReturnTally
    {
        public int Count { get; private set; }
        public string First { get; private set; } = "";
        public string Last { get; private set; } = "";
        public string? LastPayload { get; private set; }

        public static void Add(Dictionary<string, ReturnTally> tallies, string key, int count, string first, string last, string? lastPayload)
        {
            if (!tallies.TryGetValue(key, out var tally)) tallies[key] = tally = new() { First = first, Last = last, LastPayload = lastPayload };
            tally.Count += count;
            if (string.CompareOrdinal(first, tally.First) < 0) tally.First = first;
            if (string.CompareOrdinal(last, tally.Last) > 0) (tally.Last, tally.LastPayload) = (last, lastPayload);
        }

        public static IEnumerable<KeyValuePair<string, ReturnTally>> Ordered(Dictionary<string, ReturnTally> tallies) =>
            tallies.OrderBy(pair => pair.Value.First, StringComparer.Ordinal);

        public static Dictionary<string, int> Counts(Dictionary<string, ReturnTally> tallies) =>
            Ordered(tallies).ToDictionary(pair => pair.Key, pair => pair.Value.Count, StringComparer.Ordinal);
    }
}
