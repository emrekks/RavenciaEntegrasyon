using System.Linq.Expressions;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;
using static MarketplaceHub.Infrastructure.Persistence.InvoiceLabelPolicy;
using static MarketplaceHub.Infrastructure.Persistence.OrderSnapshotReader;

namespace MarketplaceHub.Infrastructure.Persistence;

// SIPARIS-STOK-9: the Returns page's cards under every filter, as database queries. Every claim the filters keep has one
// first card, whose tab follows the claim's own status (ReturnStatusWire only turns an approved or completed claim into
// PARTIALLY_APPROVED, the same tab); a claim that splits (an accepted line beside a waiting one) has an accepted card
// more. The invoice label is the claim's (both cards of a claim share it) and is decided by the database
// (ReturnInvoiceLabelAsync); the search is decided by the database for a whole card on its columns
// (ReturnSearchInDatabase) and from the view for the rest. Only the claims that split, and the claims the search leaves to
// their view, are read row by row.
public sealed partial class MarketplaceSalesService
{
    // Claims: every claim the filters keep (the tab's database filter included). FirstCard: the claims whose first card is
    // listed. SplitCards: the cards of every claim of Claims that splits. Extras: their accepted cards that are listed.
    internal sealed record ReturnCardSet(
        IQueryable<ReturnClaim> Claims,
        Expression<Func<ReturnClaim, bool>> FirstCard,
        IReadOnlyDictionary<Guid, List<ReturnCard>> SplitCards,
        IReadOnlyDictionary<Guid, ReturnClaimRow> SplitRows,
        IReadOnlyList<ReturnCard> Extras)
    {
        public IQueryable<ReturnClaim> First => Claims.Where(FirstCard);

        // The claims with at least one listed card.
        public Expression<Func<ReturnClaim, bool>> AnyCard
        {
            get
            {
                var extraIds = Extras.Select(card => card.ClaimId).ToArray();
                return extraIds.Length == 0 ? FirstCard : OrElse(FirstCard, claim => extraIds.Contains(claim.Id));
            }
        }
    }

    // The cards of the filter, or null when the search term cannot be sent to the database (the in-memory scan decides).
    // Without a tab every card counts; without refinements the platform, cargo, reason and invoice choices are left out.
    internal async Task<ReturnCardSet?> ReturnCardSetAsync(Guid tenantId, ReturnListQuery options, bool applyTab, bool applyRefinements, ReturnColumnState columns, CancellationToken cancellationToken)
    {
        var search = ReturnSearchTermOf(options.Search);
        if (search is null && !string.IsNullOrWhiteSpace(options.Search)) return null;
        var query = ReturnBaseQuery(tenantId);
        ApplyReturnFilters(ref query, applyTab ? options : options with { Status = null });
        if (applyRefinements) ApplyReturnRefinements(ref query, tenantId, options);
        var invoice = applyRefinements ? options.InvoiceStatus?.Trim().ToUpperInvariant() : null;
        if (!string.IsNullOrEmpty(invoice) && invoice != "ALL") query = await ApplyReturnInvoiceFilterAsync(query, tenantId, invoice, cancellationToken);

        var tab = applyTab ? options.Status?.Trim().ToUpperInvariant() : null;
        var everyTab = string.IsNullOrEmpty(tab) || tab == "ALL";
        var tabStatuses = everyTab ? null : Enum.GetValues<ReturnClaimStatus>().Where(status => ReturnTabGroup(Wire(status)) == tab).ToArray();
        Expression<Func<ReturnClaim, bool>> firstCard = tabStatuses is null ? claim => true
            : columns.Fresh ? claim => claim.TabGroup == tab
            : claim => tabStatuses.Contains(claim.Status);

        // The claims that may split (their CardSplit, or found by the raw statuses the tenant's lines carry); only their
        // lines are read, and only those that do split keep their cards.
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
        var extras = splitCards.Values.Select(cards => cards[^1]).Where(card => everyTab || ReturnTabGroup(card.Status) == tab).ToList();

        if (search is not null)
        {
            // A whole card the columns match is listed; the others, and every card of a claim that splits (each card's own
            // lines decide), are decided from their views, as the page always did.
            // SIPARIS-STOK-9 (round 7): the kept customer name and a line's own variant barcode are matched by the database
            // too; a split claim's cards are decided from its rows. Only a claim whose name is not kept yet, or whose
            // barcode the catalog lends by other keys while a lent barcode may match, is read from its view (at most
            // ReturnSearchViewLimit claims).
            var splitIds = splitCards.Keys.ToArray();
            var columnsMatch = OrElse(ReturnSearchInDatabase(tenantId, search), ReturnSearchOnKeptFields(tenantId, search));
            var lentBarcodeMayMatch = await ReturnSearchLentBarcodeMayMatchAsync(tenantId, search, cancellationToken);
            var undecided = await query.Where(firstCard).Where(Not(columnsMatch)).Where(ReturnSearchUndecided(tenantId, lentBarcodeMayMatch))
                .Where(claim => !splitIds.Contains(claim.Id)).Select(claim => claim.Id).Take(ReturnSearchViewLimit + 1).ToListAsync(cancellationToken);
            var splitDecisions = await SplitCardSearchAsync(tenantId, splitRows.Where(row => splitCards.ContainsKey(row.Id)).ToDictionary(row => row.Id), search, lentBarcodeMayMatch, cancellationToken);
            var viewIds = undecided.Concat(splitDecisions.Where(pair => pair.Value is null).Select(pair => pair.Key)).ToArray();
            if (viewIds.Length > ReturnSearchViewLimit) throw ReturnSearchTooBroad();
            var views = viewIds.Length == 0 ? new Dictionary<Guid, ReturnListView>() : await ReturnViewsByIdAsync(tenantId, viewIds, cancellationToken);
            bool Matches(Guid claimId, IReadOnlyCollection<Guid>? lineIds) =>
                splitDecisions.GetValueOrDefault(claimId) is { } decide ? decide(lineIds ?? [])
                : views.TryGetValue(claimId, out var view) && MatchesReturnSearch(view, lineIds ?? [.. (view.Lines ?? []).Select(line => line.Id)], search.Term);
            Guid[] matched = [.. undecided.Where(id => Matches(id, null)), .. splitCards.Where(pair => Matches(pair.Key, pair.Value[0].LineIds)).Select(pair => pair.Key)];
            firstCard = AndAlso(firstCard, OrElse(AndAlso(columnsMatch, claim => !splitIds.Contains(claim.Id)), claim => matched.Contains(claim.Id)));
            extras = extras.Where(card => Matches(card.ClaimId, card.LineIds)).ToList();
        }
        return new(query, firstCard, splitCards, splitRows.ToDictionary(row => row.Id), extras);
    }

    private static Expression<Func<ReturnClaim, bool>> Not(Expression<Func<ReturnClaim, bool>> predicate) =>
        Expression.Lambda<Func<ReturnClaim, bool>>(Expression.Not(predicate.Body), predicate.Parameters);

    private static Expression<Func<ReturnClaim, bool>> AndAlso(Expression<Func<ReturnClaim, bool>> left, Expression<Func<ReturnClaim, bool>> right) =>
        Expression.Lambda<Func<ReturnClaim, bool>>(Expression.AndAlso(left.Body, Rebind(right, left.Parameters[0])), left.Parameters);

    private static Expression<Func<ReturnClaim, bool>> OrElse(Expression<Func<ReturnClaim, bool>> left, Expression<Func<ReturnClaim, bool>> right) =>
        Expression.Lambda<Func<ReturnClaim, bool>>(Expression.OrElse(left.Body, Rebind(right, left.Parameters[0])), left.Parameters);

    // The body of the lambda with its parameter replaced.
    private static Expression Rebind<T>(Expression<Func<ReturnClaim, T>> lambda, ParameterExpression parameter) =>
        new ParameterSwap(lambda.Parameters[0], parameter).Visit(lambda.Body);

    private sealed class ParameterSwap(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : base.VisitParameter(node);
    }
}
